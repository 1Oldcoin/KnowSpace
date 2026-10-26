using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using MyFirstMafAgent.Api.Options;
using Npgsql;

namespace MyFirstMafAgent.Api.Infrastructure.PortablePostgres;

/// <summary>
/// 便携式 PostgreSQL 生命周期管理（全部在 .NET API 里做完）：
/// 首次运行 <c>initdb</c> 建集群 → <c>pg_ctl start</c> 拉起 → 等待就绪 → 确保目标数据库存在
/// → 启用 pgvector → 为 API 提供连接串；应用退出时 <c>pg_ctl stop</c>。
///
/// <para>
/// 桌面壳（Electron）不参与这一步：它只负责启动 <c>MyFirstMafAgent.Api.exe</c>、等
/// <c>/api/health</c> 返回 200、然后加载页面。数据库起不来时 API 起不来，健康检查也就永远不通过，
/// 壳只需要把 API 的日志展示给用户。
/// </para>
///
/// <para>
/// 目录约定：
/// <list type="bullet">
///   <item>程序：<c>ExternalTools/pgSQL/{rid}/bin</c>（整棵 pgsql 树，含 <c>lib</c> / <c>share</c>）。</item>
///   <item>数据：<c>{便携根目录}\pgdata</c>，首次启动时 initdb。</item>
///   <item>日志：<c>{便携根目录}\pglogs\postgres.log</c>（**不能**放进数据目录，见
///         <see cref="AppPaths.PostgresLogFolderName"/>）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PortablePostgresHost : IAsyncDisposable
{
    /// <summary>随包便携 PostgreSQL 的主版本；数据目录的主版本必须与之一致才能复用。</summary>
    internal const string ExpectedMajorVersion = "17";

    private const string PgCtlCommand = "pg_ctl";
    private const string InitDbCommand = "initdb";
    private const string PgVersionFileName = "PG_VERSION";
    private const string PidFileName = "postmaster.pid";
    private const string ServerLogFileName = "postgres.log";
    private const string StartOutputFileName = "pgctl-start.out";
    private const string StopOutputFileName = "pgctl-stop.out";
    private const string InitDbOutputFileName = "pgctl-initdb.out";

    private readonly string _binDirectory;
    private readonly string _dataDirectory;
    private readonly string _logDirectory;
    private readonly PortablePostgresOptions _options;
    private readonly Action<string>? _log;
    private readonly bool _startedByUs;
    private bool _stopped;

    private PortablePostgresHost(
        string binDirectory,
        string dataDirectory,
        string logDirectory,
        int port,
        string database,
        PortablePostgresOptions options,
        bool startedByUs,
        Action<string>? log)
    {
        _binDirectory = binDirectory;
        _dataDirectory = dataDirectory;
        _logDirectory = logDirectory;
        _options = options;
        _startedByUs = startedByUs;
        _log = log;
        Port = port;
        Database = database;
        ConnectionString = BuildConnectionString(port, database, options);
    }

    public string ConnectionString { get; }

    public int Port { get; }

    public string Database { get; }

    /// <summary>PostgreSQL 数据目录（集群目录）。</summary>
    public string DataDirectory => _dataDirectory;

    /// <summary>服务器日志（<c>pg_ctl -l</c> 的输出）。</summary>
    public string ServerLogPath => Path.Combine(_logDirectory, ServerLogFileName);

    /// <summary>是否由本次进程启动（否则是复用上一次残留的实例，退出时不该停）。</summary>
    public bool StartedByUs => _startedByUs;

    /// <summary>
    /// 根据配置决定是否需要启动便携数据库。已存在外部连接串时直接返回 null。
    /// </summary>
    public static async Task<PortablePostgresHost?> StartAsync(
        IConfiguration configuration,
        string contentRoot,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        var options = configuration.GetSection(PortablePostgresOptions.SectionName)
            .Get<PortablePostgresOptions>() ?? new PortablePostgresOptions();

        if (!options.Enabled)
        {
            log?.Invoke("便携 PostgreSQL 已禁用。");
            return null;
        }

        var configuredConnectionString = configuration.GetConnectionString("KnowledgeBase");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString) && options.RespectExternalConnectionString)
        {
            // 带上脱敏后的连接串：光说“检测到外部连接串”时，用户没法把它和紧接着的
            // 连接失败对应起来（尤其当连接串来自 user secrets，appsettings 里什么都看不到）。
            log?.Invoke("检测到外部数据库连接串，跳过便携 PostgreSQL："
                + Persistence.ConnectionPreflight.Mask(configuredConnectionString));
            return null;
        }

        EnsureNotElevated();

        var rootFailures = new List<string>();
        var portableRoot = ResolvePortableRoot(contentRoot, options, rootFailures)
            ?? throw new InvalidOperationException(BuildNoWritableRootMessage(rootFailures));

        // 走到「程序目录兜底」说明用户目录整片不可写（安全软件/受控文件夹访问），
        // 数据因此跟着程序走：覆盖安装/删除程序目录会连带数据，必须让用户知情。
        if (IsUnder(portableRoot, contentRoot))
        {
            log?.Invoke($"警告：用户数据目录不可写，便携数据库改用程序目录下的 {portableRoot}。"
                + "该位置随程序目录一起被覆盖安装/删除，长期使用请把应用放进纯英文的自定义目录并配置 PortablePostgres:RootDirectory。");
        }

        var binDirectory = ResolvePostgresBinDirectory(contentRoot, options, log)
            ?? throw new FileNotFoundException(BuildMissingDistributionMessage(contentRoot, options), options.BinDirectory);

        var dataDirectory = ResolveDataDirectory(options, contentRoot, portableRoot);
        var logDirectory = Path.Combine(portableRoot, AppPaths.PostgresLogFolderName);

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(logDirectory);

        binDirectory = await EnsureRunnableBinDirectoryAsync(binDirectory, portableRoot, log, cancellationToken);

        WarnAboutLegacyCluster(portableRoot, dataDirectory, log);

        // 上一次进程异常退出时数据库可能仍在运行：优先复用，避免端口冲突和数据目录争用。
        var runningPort = TryReadRunningInstancePort(dataDirectory);
        if (runningPort is { } port && await CanConnectAsync(port, "postgres", options, cancellationToken))
        {
            log?.Invoke($"检测到已在运行的 PostgreSQL（端口 {port}，数据目录 {dataDirectory}），直接复用。");
            var reused = new PortablePostgresHost(
                binDirectory, dataDirectory, logDirectory, port, options.Database, options, startedByUs: false, log);
            await reused.EnsureDatabaseAndVectorAsync(cancellationToken);
            return reused;
        }

        await EnsureClusterInitializedAsync(binDirectory, dataDirectory, logDirectory, options, log, cancellationToken);

        var freePort = FindAvailablePort(options.Port);
        var host = new PortablePostgresHost(
            binDirectory, dataDirectory, logDirectory, freePort, options.Database, options, startedByUs: true, log);

        try
        {
            await host.StartServerAsync(cancellationToken);
            await host.EnsureDatabaseAndVectorAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.StopAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 卸载/更新前同步停止便携数据库。安装器删除程序目录时，若 <c>postgres.exe</c> 仍在运行，
    /// 会因为文件被占用而残留（Windows 上表现为“拒绝访问”，目录删不干净）。
    /// 此方法不依赖 API 主机，供 NSIS 卸载钩子通过 <c>--stop-portable-postgres</c> 直接调用。
    /// </summary>
    public static void TryStopForUninstall(string contentRoot)
    {
        try
        {
            var options = ReadOptionsFromAppSettings(contentRoot);
            if (options is null || !options.Enabled) return;

            var binDirectory = PlatformRuntime.ResolvePostgresBinDirectory(contentRoot, options.BinDirectory);
            if (binDirectory is null)
            {
                Console.WriteLine($"[installer] 找不到便携 PostgreSQL（{PlatformRuntime.PortablePostgresFolder}），跳过停止。");
                return;
            }

            var dataDirectory = TryFindExistingDataDirectory(options, contentRoot);
            if (dataDirectory is null)
            {
                Console.WriteLine("[installer] 没有找到便携 PostgreSQL 数据目录（可能从未启动过），跳过停止。");
                return;
            }

            var pgCtl = Path.Combine(binDirectory, PlatformRuntime.WithSuffix(PgCtlCommand));
            if (!File.Exists(pgCtl))
            {
                Console.WriteLine($"[installer] 缺少 {pgCtl}，跳过停止。");
                return;
            }

            // 输出文件放在数据目录旁的 pglogs：%TEMP% 可能被安全软件或重定向策略拦掉。
            var logDirectory = Path.Combine(Path.GetDirectoryName(dataDirectory)!, AppPaths.PostgresLogFolderName);
            var outputFile = Path.Combine(logDirectory, "pgctl-uninstall.out");
            try
            {
                Directory.CreateDirectory(logDirectory);

                var result = PgProcessRunner.RunAsync(
                    pgCtl,
                    ["stop", "-D", dataDirectory, "-m", "fast", "-w", "-t", "60"],
                    outputFile,
                    TimeSpan.FromSeconds(90),
                    CancellationToken.None).GetAwaiter().GetResult();

                Console.WriteLine(result.Succeeded
                    ? $"[installer] 便携 PostgreSQL 已停止（数据目录 {dataDirectory}）。"
                    : $"[installer] pg_ctl stop 返回 {result.ExitCode}: {result.Output.Trim()}");
            }
            finally
            {
                TryDeleteFile(outputFile);
            }
        }
        catch (Exception exception)
        {
            // 卸载钩子绝不能让卸载失败；但原因必须留在安装器日志里，否则现场无从排查。
            Console.WriteLine($"[installer] 停止便携 PostgreSQL 失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static PortablePostgresOptions? ReadOptionsFromAppSettings(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "appsettings.json");
        if (!File.Exists(path)) return new PortablePostgresOptions();

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (!document.RootElement.TryGetProperty(PortablePostgresOptions.SectionName, out var section))
            return new PortablePostgresOptions();

        return section.Deserialize<PortablePostgresOptions>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new PortablePostgresOptions();
    }

    /// <summary>
    /// PostgreSQL 拒绝在提权进程里运行（<c>initdb</c> / <c>postgres</c> 检测到管理员令牌会直接退出：
    /// <c>Execution of PostgreSQL by a user with administrative permissions is not permitted</c>）。
    /// 安装器/快捷方式若带“以管理员身份运行”，数据库就永远起不来，所以在这里提前拦下并说清原因。
    /// </summary>
    private static void EnsureNotElevated()
    {
        if (!OperatingSystem.IsWindows()) return;

        bool elevated;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            return;
        }

        if (!elevated) return;

        throw new InvalidOperationException(
            "当前进程以管理员权限运行，便携 PostgreSQL 无法启动（initdb/postgres 会拒绝提权令牌："
            + "Execution of PostgreSQL by a user with administrative permissions is not permitted）。"
            + "请以普通用户身份启动本程序；如果确实需要提权，请改用外部 PostgreSQL 并配置 ConnectionStrings:KnowledgeBase。");
    }

    /// <summary>
    /// 返回一个可写的便携根目录。纯 ASCII 路径优先（PostgreSQL 官方建议数据目录无中文、无空格），
    /// 顺序见 <see cref="AppPaths.PortableDataRootCandidates"/>：用户数据目录 → 临时目录 →
    /// 公共程序数据 → 用户主目录 → **程序目录下的 App_Data（便携兜底）**。
    ///
    /// <para>
    /// 配置了 <see cref="PortablePostgresOptions.RootDirectory"/> 时只认配置值，不再回落，
    /// 否则用户明明指定了目录、程序却悄悄跑到别处，数据会「凭空消失」。
    /// </para>
    /// </summary>
    private static string? ResolvePortableRoot(
        string contentRoot,
        PortablePostgresOptions options,
        List<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            var configured = PlatformRuntime.ToAbsolute(contentRoot, options.RootDirectory);

            // 数据目录必须是纯 ASCII（Windows 版 PostgreSQL 的硬限制，见 EnsureAsciiDataPath）。
            EnsureAsciiDataPath(configured, "PortablePostgres:RootDirectory");

            var configuredError = TryCreateWritableDirectory(configured);
            if (configuredError is not null)
            {
                throw new InvalidOperationException(
                    $"PortablePostgres:RootDirectory 指向的目录不可写：{configured}（{configuredError}）。"
                    + AppPaths.BuildLocationAdvice() + "。");
            }

            return configured;
        }

        foreach (var candidate in AppPaths.PortableDataRootCandidates(contentRoot))
        {
            if (!AppPaths.IsAsciiPath(candidate))
            {
                failures.Add($"{candidate}（路径含非 ASCII 字符，见下方说明）");
                continue;
            }

            var error = TryCreateWritableDirectory(candidate);
            if (error is null) return candidate;

            failures.Add($"{candidate}（{error}）");
        }

        return null;
    }

    /// <summary>
    /// Windows 版 PostgreSQL 把路径按本地代码页转换，**非 ASCII 的数据目录根本初始化不了**：
    /// 实测 <c>initdb -D "C:\Word\中文目录\pgdata"</c> 会先把路径打成乱码
    /// （<c>C:/Word/ͨ���ļ�����/...</c>），然后在 post-bootstrap 阶段报
    /// <c>FATAL: invalid byte sequence for encoding "UTF8": 0xd3 0xc3</c> 并回滚。
    ///
    /// <para>
    /// 注意这条限制只针对**数据目录**：便携 PostgreSQL 的程序本体放在中文目录下是可以跑的
    /// （进程创建走 UTF-16，不经过代码页转换）。Linux / macOS 上 UTF-8 路径正常，不做限制。
    /// </para>
    /// </summary>
    private static void EnsureAsciiDataPath(string directory, string source)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AppPaths.IsAsciiPath(directory)) return;

        throw new InvalidOperationException(
            $"{source} 必须是纯 ASCII 路径，当前为 {directory}。"
            + "Windows 版 PostgreSQL 按本地代码页转换路径，非 ASCII 数据目录会在 initdb 阶段报 "
            + "invalid byte sequence for encoding \"UTF8\"（实测）。"
            + "请改成纯英文路径（例如 D:\\Apps\\MyFirstMafAgentData），或改用外部 PostgreSQL：ConnectionStrings:KnowledgeBase。");
    }

    /// <summary>所有候选目录都不可用时的报错文案：先讲约束，再逐条列出实测失败原因。</summary>
    private static string BuildNoWritableRootMessage(IReadOnlyList<string> failures)
    {
        var details = string.Join(
            Environment.NewLine,
            failures.Select(static failure => "  - " + failure));

        return
            "找不到可写且路径为纯英文的目录来运行便携 PostgreSQL。"
            + AppPaths.BuildLocationAdvice() + "。"
            + Environment.NewLine + "已尝试的目录：" + Environment.NewLine + details
            + Environment.NewLine + "为什么要求纯英文：Windows 版 PostgreSQL 按本地代码页转换路径，"
            + "中文数据目录在 initdb 阶段就会报 invalid byte sequence for encoding \"UTF8\" 并回滚（实测）。"
            + Environment.NewLine + "排查建议："
            + Environment.NewLine + "  1) 把便携数据库的数据目录放到纯英文路径，例如 D:\\Apps\\MyFirstMafAgentData"
            + "（程序目录可以含中文，数据目录不行）；"
            + Environment.NewLine + "  2) 目录本身可写但程序仍被拒绝时，通常是安全软件（火绒/360 的文件防护、勒索防护）"
            + "或 Windows「受控文件夹访问」拦下了未签名 exe：把 MyFirstMafAgent.Api.exe（或整个应用目录）加入白名单后重试；"
            + Environment.NewLine + "  3) 也可以显式指定一个纯英文可写目录：PortablePostgres:RootDirectory；"
            + Environment.NewLine + "  4) 或者改用外部 PostgreSQL：配置 ConnectionStrings:KnowledgeBase（容器/已有实例都推荐这种方式）。";
    }

    private static string? ResolvePostgresBinDirectory(
        string contentRoot, PortablePostgresOptions options, Action<string>? log)
        => PlatformRuntime.ResolvePostgresBinDirectory(contentRoot, options.BinDirectory, log);

    /// <summary>
    /// 缺少便携 PostgreSQL 时的报错文案：写清当前平台、全部候选目录、包里应该有什么、
    /// 以及「改用外部数据库」的替代方案——而不是一句“找不到文件”。
    /// </summary>
    private static string BuildMissingDistributionMessage(string contentRoot, PortablePostgresOptions options)
    {
        var candidates = PlatformRuntime.PostgresBinCandidates()
            .Select(relative => "  - " + PlatformRuntime.ToAbsolute(contentRoot, relative) + Path.DirectorySeparatorChar + PlatformRuntime.PostgresExecutableName);

        var configured = string.IsNullOrWhiteSpace(options.BinDirectory)
            ? "（未配置，按当前平台自动定位）"
            : $"（配置值：{options.BinDirectory}）";

        return
            $"找不到便携 PostgreSQL：当前平台 {PlatformRuntime.Describe()}（RID {PlatformRuntime.RuntimeIdentifier}）。"
            + "已尝试：" + Environment.NewLine + string.Join(Environment.NewLine, candidates) + Environment.NewLine
            + "便携 PostgreSQL 必须是**整棵目录树**（含 bin / lib / share，相对位置不能拆，"
            + "只拷 bin 里的 exe 会报 0xC0000135 找不到 DLL）。"
            + Environment.NewLine + $"请把对应平台的 pgsql 目录放到 {PlatformRuntime.PortablePostgresFolder}/{PlatformRuntime.RuntimeIdentifier}/ 下"
            + $"（可用 scripts\\Download-ExternalTools.ps1 -Target pgsql 装配）{configured}；"
            + "也可以用 ConnectionStrings__KnowledgeBase 指向已有 PostgreSQL（容器部署推荐这种方式）。";
    }

    private static string ResolveDataDirectory(
        PortablePostgresOptions options,
        string contentRoot,
        string portableRoot)
    {
        var directory = string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(portableRoot, AppPaths.PostgresDataFolderName)
            : PlatformRuntime.ToAbsolute(contentRoot, options.DataDirectory);

        EnsureAsciiDataPath(directory, "PortablePostgres:DataDirectory");

        return directory;
    }

    /// <summary>
    /// 确保 <c>bin</c> 目录位于一个 PostgreSQL 能正常工作的路径上。
    ///
    /// <para>
    /// Windows 版 PostgreSQL 的 <c>initdb</c> 在 post-bootstrap 阶段会把自己所在的路径按**本地代码页**
    /// 拼进子进程命令行，再由 <c>postgres.exe</c> 按 UTF8 解析。安装目录含中文时这串字节直接非法：
    /// 实测 <c>initdb</c> 报 <c>FATAL: invalid byte sequence for encoding "UTF8": 0xd3 0xc3</c>
    /// 并回滚整个集群（0xd3 0xc3 正是“用”的 GBK 编码）。把整棵树放到纯 ASCII 目录后同样的命令一次通过。
    /// </para>
    ///
    /// <para>
    /// 所以：路径含非 ASCII 时先把整棵 pgsql 树镜像到纯英文的便携根目录再运行。
    /// 附带好处：<c>postgres.exe</c> 不再锁住安装目录，覆盖安装/升级不会被“文件被占用”挡住
    /// （旧实现把单个 pg0 复制到 LOCALAPPDATA 也是这个原因）。
    /// </para>
    /// </summary>
    private static async Task<string> EnsureRunnableBinDirectoryAsync(
        string binDirectory, string portableRoot, Action<string>? log, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || AppPaths.IsAsciiPath(binDirectory)) return binDirectory;

        var sourceRoot = Path.GetDirectoryName(binDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(sourceRoot))
            throw new InvalidOperationException($"无法从 {binDirectory} 推导便携 PostgreSQL 根目录。");

        var targetRoot = Path.Combine(portableRoot, "pgsql");
        if (!AppPaths.IsAsciiPath(targetRoot))
        {
            throw new InvalidOperationException(
                $"安装路径含非 ASCII 字符（{binDirectory}），而便携 PostgreSQL 在 Windows 上无法从这样的路径启动"
                + "（initdb 会报 invalid byte sequence for encoding \"UTF8\"）。"
                + $"同时便携根目录 {targetRoot} 也不是纯英文，无法用于镜像。"
                + "请把 PortablePostgres:RootDirectory 配置成纯英文可写目录（例如 D:\\Apps\\MyFirstMafAgentData）。");
        }

        if (!IsMirrorUpToDate(sourceRoot, targetRoot))
        {
            log?.Invoke($"安装路径含非 ASCII 字符，正在把便携 PostgreSQL 镜像到纯英文目录 {targetRoot}（首次约 65 MB）...");
            await MirrorDirectoryAsync(sourceRoot, targetRoot, cancellationToken);
            log?.Invoke("便携 PostgreSQL 镜像完成。");
        }

        return Path.Combine(targetRoot, "bin");
    }

    /// <summary>用包内 <c>VERSION.txt</c> 当指纹：版本变了才重新镜像，日常启动零拷贝。</summary>
    private static bool IsMirrorUpToDate(string sourceRoot, string targetRoot)
    {
        if (!File.Exists(Path.Combine(targetRoot, "bin", PlatformRuntime.PostgresExecutableName))) return false;

        var sourceVersion = TryReadText(Path.Combine(sourceRoot, "VERSION.txt"), 512);
        if (sourceVersion is null) return true; // 没有版本戳就认为已镜像，避免每次启动都拷 65 MB。

        return string.Equals(sourceVersion, TryReadText(Path.Combine(targetRoot, "VERSION.txt"), 512), StringComparison.Ordinal);
    }

    private static async Task MirrorDirectoryAsync(
        string sourceRoot, string targetRoot, CancellationToken cancellationToken)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceRoot, source);
            var target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            var sourceInfo = new FileInfo(source);
            if (File.Exists(target))
            {
                var targetInfo = new FileInfo(target);
                if (targetInfo.Length == sourceInfo.Length
                    && targetInfo.LastWriteTimeUtc == sourceInfo.LastWriteTimeUtc)
                {
                    continue;
                }
            }

            // 安全软件正在扫描时 File.Copy 可能瞬间抛 UnauthorizedAccessException，重试通常能过。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(source, target, overwrite: true);
                    break;
                }
                catch (Exception exception) when (attempt < 3
                    && exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(300 * attempt, cancellationToken);
                }
            }
        }
    }

    private static string? TryFindExistingDataDirectory(PortablePostgresOptions options, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            var configured = PlatformRuntime.ToAbsolute(contentRoot, options.DataDirectory);
            return File.Exists(Path.Combine(configured, PgVersionFileName)) ? configured : null;
        }

        if (!string.IsNullOrWhiteSpace(options.RootDirectory))
        {
            var path = Path.Combine(
                PlatformRuntime.ToAbsolute(contentRoot, options.RootDirectory), AppPaths.PostgresDataFolderName);
            return File.Exists(Path.Combine(path, PgVersionFileName)) ? path : null;
        }

        foreach (var root in AppPaths.PortableDataRootCandidates(contentRoot))
        {
            var path = Path.Combine(root, AppPaths.PostgresDataFolderName);
            if (File.Exists(Path.Combine(path, PgVersionFileName))) return path;
        }

        return null;
    }

    /// <summary>
    /// 旧版本（pg0，内置 PostgreSQL 18）留下的数据目录无法被 17.9 读取。
    /// 不静默忽略：用户会以为“数据丢了”，必须说清是版本不兼容、原数据仍在原处。
    /// </summary>
    private static void WarnAboutLegacyCluster(string portableRoot, string dataDirectory, Action<string>? log)
    {
        if (File.Exists(Path.Combine(dataDirectory, PgVersionFileName))) return;

        foreach (var legacy in new[]
                 {
                     Path.Combine(portableRoot, "pgsql", "myfirstmafagent"),
                     Path.Combine(portableRoot, "pgsql")
                 })
        {
            if (!File.Exists(Path.Combine(legacy, PgVersionFileName))) continue;

            var version = TryReadText(Path.Combine(legacy, PgVersionFileName), 32) ?? "?";
            log?.Invoke($"警告：检测到旧版便携数据库的数据目录 {legacy}（PostgreSQL {version}），"
                + $"它是上一代内置数据库（PostgreSQL 18）的集群，当前随包版本是 {ExpectedMajorVersion}，跨大版本无法直接复用。"
                + "该目录不会被删除或修改；如需迁移，请用旧版本自带的 pg_dump 导出后导入新库。");
            return;
        }
    }

    private static async Task EnsureClusterInitializedAsync(
        string binDirectory,
        string dataDirectory,
        string logDirectory,
        PortablePostgresOptions options,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var versionFile = Path.Combine(dataDirectory, PgVersionFileName);
        if (File.Exists(versionFile))
        {
            var version = (TryReadText(versionFile, 32) ?? string.Empty).Trim();
            if (!string.Equals(version, ExpectedMajorVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"数据目录 {dataDirectory} 是 PostgreSQL {version} 的集群，而随包便携数据库是 {ExpectedMajorVersion}。"
                    + "跨大版本不能直接复用（PostgreSQL 没有向下兼容的数据目录格式）。"
                    + "请改用旧版本迁移（pg_dump 导出 → 新库导入），或把 PortablePostgres:DataDirectory 指到一个空目录。");
            }

            return;
        }

        var initDb = Path.Combine(binDirectory, PlatformRuntime.WithSuffix(InitDbCommand));
        if (!File.Exists(initDb))
        {
            throw new FileNotFoundException(
                $"便携 PostgreSQL 缺少 {InitDbCommand}（{initDb}），无法创建数据库集群。"
                + "请确认 ExternalTools/pgSQL/<rid> 是完整的 bin / lib / share 目录树。",
                initDb);
        }

        if (Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            throw new InvalidOperationException(
                $"数据目录 {dataDirectory} 非空但没有 PG_VERSION，不像是完整的 PostgreSQL 集群。"
                + "这可能是一次失败的初始化留下的残留；确认里面没有需要的数据后，清空该目录再启动。");
        }

        log?.Invoke($"首次运行：正在初始化便携 PostgreSQL 集群（{dataDirectory}），通常需要 3~10 秒...");

        // initdb 带 --auth=scram-sha-256 时会交互式要求输入超级用户密码；宿主进程没有 stdin，
        // 直接跑会永久挂住，所以必须用 --pwfile 传密码，用完立刻删除。
        // 文件放在日志目录（已确认可写）而不是 %TEMP%：临时目录可能被安全软件或重定向策略拦掉。
        var passwordFile = Path.Combine(logDirectory, $"pg-initdb-{Guid.NewGuid():N}.pw");
        try
        {
            await File.WriteAllTextAsync(passwordFile, options.Password, new UTF8Encoding(false), cancellationToken);

            var result = await PgProcessRunner.RunAsync(
                initDb,
                [
                    "-D", dataDirectory,
                    "-U", options.Username,
                    "--auth=scram-sha-256",
                    "--encoding=UTF8",
                    // 便携场景最怕客户端 locale 不一致：--locale=C 保证排序规则跨机器一致，
                    // 否则 CREATE DATABASE ... TEMPLATE 可能报 collation 不兼容。中文内容照样存（UTF8）。
                    "--locale=C",
                    "--pwfile=" + passwordFile
                ],
                Path.Combine(logDirectory, InitDbOutputFileName),
                TimeSpan.FromSeconds(Math.Max(60, options.StartupTimeoutSeconds)),
                cancellationToken);

            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"初始化便携 PostgreSQL 集群失败（initdb 退出码 {result.ExitCode}，数据目录 {dataDirectory}）。"
                    + Environment.NewLine + TrimOutput(result.Output)
                    + Environment.NewLine + "常见原因：目标目录不可写（安全软件/受控文件夹访问）、路径含特殊字符、"
                    + "或以管理员权限运行。");
            }

            log?.Invoke("便携 PostgreSQL 集群初始化完成。");
        }
        finally
        {
            TryDeleteFile(passwordFile);
        }
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        var pgCtl = Path.Combine(_binDirectory, PlatformRuntime.WithSuffix(PgCtlCommand));
        if (!File.Exists(pgCtl))
            throw new FileNotFoundException($"便携 PostgreSQL 缺少 {PgCtlCommand}（{pgCtl}）。", pgCtl);

        _log?.Invoke($"启动便携 PostgreSQL：端口 {Port}，数据目录 {_dataDirectory}");
        _log?.Invoke($"PostgreSQL 日志：{ServerLogPath}");

        // -w 等就绪、-t 是等待上限；端口与监听地址用 -o 传给 postgres，
        // 这样不依赖 postgresql.conf 里 initdb 写下的 port=5432（用户机上常被占用）。
        var result = await PgProcessRunner.RunAsync(
            pgCtl,
            [
                "start",
                "-D", _dataDirectory,
                "-l", ServerLogPath,
                "-w",
                "-t", Math.Max(30, _options.StartupTimeoutSeconds).ToString(),
                "-o", $"-p {Port} -c listen_addresses=127.0.0.1"
            ],
            Path.Combine(_logDirectory, StartOutputFileName),
            TimeSpan.FromSeconds(Math.Max(30, _options.StartupTimeoutSeconds) + 30),
            cancellationToken);

        if (!result.Succeeded)
            throw new InvalidOperationException(BuildStartFailureMessage(result));

        await WaitUntilReadyAsync(cancellationToken);
    }

    private string BuildStartFailureMessage(PgProcessRunner.Result result)
    {
        var tail = PgProcessRunner.ReadTail(ServerLogPath);
        var ctlOutput = TrimOutput(result.Output);

        var builder = new StringBuilder();
        builder.Append(result.TimedOut
            ? $"启动便携 PostgreSQL 超时（{_options.StartupTimeoutSeconds} 秒，端口 {Port}）。"
            : $"启动便携 PostgreSQL 失败（pg_ctl 退出码 {result.ExitCode}，端口 {Port}）。");

        if (ctlOutput.Length > 0)
            builder.Append(Environment.NewLine).Append("pg_ctl 输出：").Append(Environment.NewLine).Append(ctlOutput);

        if (tail.Length > 0)
            builder.Append(Environment.NewLine).Append("服务器日志末尾：").Append(Environment.NewLine).Append(tail);

        builder.Append(Environment.NewLine)
            .Append("常见原因：")
            .Append(Environment.NewLine)
            .Append("  1) 端口被占用。PostgreSQL 用 SO_EXCLUSIVEADDRUSE 绑端口，此时日志里写的是 "
                + "\"could not bind IPv4 address \\\"127.0.0.1\\\": Permission denied\" 而**不是** address already in use，"
                + "先 netstat -ano | findstr :").Append(Port).Append(" 看看谁占着；")
            .Append(Environment.NewLine)
            .Append("  2) 以管理员权限运行（PostgreSQL 拒绝提权进程）；")
            .Append(Environment.NewLine)
            .Append("  3) 数据目录不可写，或被安全软件的勒索防护锁住；")
            .Append(Environment.NewLine)
            .Append("  4) pgsql 目录树不完整（bin / lib / share 必须齐全）。")
            .Append(Environment.NewLine)
            .Append($"日志目录：{_logDirectory}");

        return builder.ToString();
    }

    /// <summary>
    /// 就绪探测的退避：<c>pg_ctl -w</c> 已经把「服务器能接受连接」等到了，这里通常一次就通；
    /// 用 500ms 固定间隔时，失败一次就要白等半秒，所以从 80ms 起步、逐步拉长到 250ms 封顶。
    /// </summary>
    private static TimeSpan NextProbeDelay(int attempts) =>
        TimeSpan.FromMilliseconds(Math.Min(250, 40 + attempts * 40));

    /// <summary>
    /// <c>pg_ctl -w</c> 已经等到“就绪”，这里再用真实连接确认一次：
    /// 只有能跑通一条 SQL，才算数据库真的可用。
    /// </summary>
    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _options.StartupTimeoutSeconds));
        Exception? lastError = null;
        var attempts = 0;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;

            try
            {
                await using var connection = new NpgsqlConnection(BuildProbeConnectionString("postgres"));
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken);
                _log?.Invoke($"便携 PostgreSQL 已就绪：127.0.0.1:{Port}");
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
            {
                lastError = exception;

                // 进程已经没了就不用再等到超时：直接把服务器日志的最后几行抛出来。
                if (attempts > 3 && TryReadRunningInstancePort(_dataDirectory) is null)
                {
                    var logTail = PgProcessRunner.ReadTail(ServerLogPath);
                    throw new InvalidOperationException(
                        $"便携 PostgreSQL 启动后立即退出（端口 {Port}）。"
                        + (logTail.Length > 0
                            ? Environment.NewLine + "服务器日志末尾：" + Environment.NewLine + logTail
                            : string.Empty)
                        + Environment.NewLine + $"日志目录：{_logDirectory}",
                        lastError);
                }

                await Task.Delay(NextProbeDelay(attempts), cancellationToken);
            }
        }

        throw new TimeoutException(
            $"等待便携 PostgreSQL 就绪超时（{_options.StartupTimeoutSeconds} 秒，端口 {Port}）。"
            + (PgProcessRunner.ReadTail(ServerLogPath) is { Length: > 0 } tail
                ? Environment.NewLine + "服务器日志末尾：" + Environment.NewLine + tail
                : string.Empty),
            lastError);
    }

    private async Task EnsureDatabaseAndVectorAsync(CancellationToken cancellationToken)
    {
        await using (var admin = new NpgsqlConnection(BuildConnectionString(Port, "postgres", _options)))
        {
            await admin.OpenAsync(cancellationToken);
            await using var exists = admin.CreateCommand();
            exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            exists.Parameters.AddWithValue("name", Database);
            var found = await exists.ExecuteScalarAsync(cancellationToken);
            if (found is null)
            {
                try
                {
                    await using var create = admin.CreateCommand();
                    create.CommandText = $"CREATE DATABASE {QuoteIdentifier(Database)}";
                    await create.ExecuteNonQueryAsync(cancellationToken);
                    _log?.Invoke($"已创建数据库 {Database}。");
                }
                catch (PostgresException exception) when (exception.SqlState is "42P04" or "23505")
                {
                    // 另一个进程（例如同时启动的控制台版）已经创建了同名数据库，忽略。
                    _log?.Invoke($"数据库 {Database} 已由其他进程创建。");
                }
            }
        }

        try
        {
            await EnableVectorExtensionAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == "0A000")
        {
            throw new InvalidOperationException(
                $"启用 pgvector 失败：数据库报告扩展不可用（{exception.MessageText}）。"
                + "随包便携 PostgreSQL 应该自带 pgvector（lib/vector.dll 与 share/extension/vector.control）。"
                + $"请检查 {_binDirectory} 的上一级目录里 lib 与 share 是否完整。", exception);
        }
    }

    private async Task EnableVectorExtensionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(BuildConnectionString(Port, Database, _options));
        await connection.OpenAsync(cancellationToken);

        // 先做一次便宜的探询再决定要不要执行 DDL：
        // CREATE EXTENSION IF NOT EXISTS 虽然幂等，但它要拿锁、读 control 文件，实测每次约 300ms；
        // 而 pg_extension 的查询是毫秒级。扩展装好之后每次启动都该走这条快路。
        await using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
            if (await probe.ExecuteScalarAsync(cancellationToken) is not null) return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        _log?.Invoke($"pgvector 已启用（数据库 {Database}）。");
    }

    private static string BuildConnectionString(int port, string database, PortablePostgresOptions options) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = database,
            Username = options.Username,
            Password = options.Password,
            ApplicationName = "MyFirstMafAgent.Api",
            Pooling = true,
            MaxPoolSize = 50,
            Timeout = 15,
            CommandTimeout = 120,
            // 便携库只在 127.0.0.1 上明文跑，协商 SSL 纯属多一个往返。
            SslMode = SslMode.Disable,
            IncludeErrorDetail = true
        }.ConnectionString;

    private string BuildProbeConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(BuildConnectionString(Port, database, _options))
        {
            Timeout = 3,
            Pooling = false,
            MaxPoolSize = 1
        }.ConnectionString;

    private static async Task<bool> CanConnectAsync(
        int port, string database, PortablePostgresOptions options, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(BuildConnectionString(port, database, options))
            {
                Timeout = 3,
                Pooling = false,
                MaxPoolSize = 1
            };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从 <c>postmaster.pid</c> 里读出正在运行的实例端口。
    /// 文件格式（每行一项）：PID / 数据目录 / 启动时间 / 端口 / socket 目录 / 监听地址 / shared memory。
    /// 只有 PID 对应进程确实还活着、并且真的是 postgres 才算数（否则是上次强杀留下的残留文件）。
    /// </summary>
    private static int? TryReadRunningInstancePort(string dataDirectory)
    {
        var pidFile = Path.Combine(dataDirectory, PidFileName);
        if (!File.Exists(pidFile)) return null;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(pidFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (lines.Length < 4) return null;
        if (!int.TryParse(lines[0].Trim(), out var pid)) return null;
        if (!int.TryParse(lines[3].Trim(), out var port)) return null;

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return null;
            if (!process.ProcessName.Contains("postgres", StringComparison.OrdinalIgnoreCase)) return null;
            return port;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int FindAvailablePort(int preferred)
    {
        for (var offset = 0; offset < 32; offset++)
        {
            var candidate = preferred + offset;
            if (candidate > 65535) break;
            if (IsPortFree(candidate)) return candidate;
        }

        throw new InvalidOperationException($"从 {preferred} 起找不到可用的 PostgreSQL 端口。");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string TrimOutput(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length > 4000 ? trimmed[^4000..] : trimmed;
    }

    private static string? TryReadText(string path, int maxLength)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[maxLength];
            var read = reader.Read(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响主流程。
        }
    }

    /// <summary>判断路径是否位于指定目录之下。</summary>
    private static bool IsUnder(string path, string directory)
    {
        var parent = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>创建目录并写入探针文件，返回 null 表示成功，否则返回失败原因。</summary>
    private static string? TryCreateWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return null;
        }
        catch (Exception exception)
        {
            return AppPaths.DescribeWriteFailure(exception);
        }
    }

    /// <summary>
    /// <c>pg_ctl stop -m fast</c>：等待事务回滚、刷盘后干净退出。
    /// 不用 <c>immediate</c>（等价于断电，下次启动要做崩溃恢复）。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        if (!_startedByUs || !_options.StopOnExit)
        {
            _log?.Invoke("便携 PostgreSQL 由其他进程启动（或已配置 StopOnExit=false），保留运行。");
            return;
        }

        _log?.Invoke("正在停止便携 PostgreSQL...");

        var pgCtl = Path.Combine(_binDirectory, PlatformRuntime.WithSuffix(PgCtlCommand));
        try
        {
            var result = await PgProcessRunner.RunAsync(
                pgCtl,
                ["stop", "-D", _dataDirectory, "-m", "fast", "-w", "-t", "60"],
                Path.Combine(_logDirectory, StopOutputFileName),
                TimeSpan.FromSeconds(75),
                cancellationToken);

            if (result.Succeeded)
            {
                _log?.Invoke("便携 PostgreSQL 已停止。");
                return;
            }

            _log?.Invoke($"pg_ctl stop 返回 {result.ExitCode}：{TrimOutput(result.Output)}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log?.Invoke($"停止便携 PostgreSQL 失败：{exception.Message}");
        }

        KillPostmaster();
    }

    /// <summary>兜底：pg_ctl 停不掉（例如 pid 文件丢失）时直接结束 postmaster 进程树。</summary>
    private void KillPostmaster()
    {
        var pidFile = Path.Combine(_dataDirectory, PidFileName);
        if (!File.Exists(pidFile)) return;

        try
        {
            if (!int.TryParse(File.ReadAllLines(pidFile).FirstOrDefault()?.Trim(), out var pid)) return;
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return;

            process.Kill(entireProcessTree: true);
            _log?.Invoke($"已强制结束 PostgreSQL 进程（PID {pid}）。");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException
                or UnauthorizedAccessException or NotSupportedException)
        {
            _log?.Invoke($"强制结束 PostgreSQL 进程失败：{exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }
}
