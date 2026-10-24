using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MyFirstMafAgent.Api.Options;
using Npgsql;

namespace MyFirstMafAgent.Api.Infrastructure.PortablePostgres;

/// <summary>
/// 便携式 PostgreSQL（pg0）生命周期管理：
/// 启动子进程 → 等待数据库就绪 → 确保目标数据库存在 → 启用 pgvector → 为 API 提供连接串。
/// 应用退出时优雅停止数据库。
/// </summary>
public sealed class PortablePostgresHost : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _instanceName;
    private readonly PortablePostgresOptions _options;
    private readonly Action<string>? _log;
    private readonly bool _startedByUs;
    private Process? _process;
    private bool _stopped;

    private PortablePostgresHost(
        string executablePath,
        string instanceName,
        int port,
        string database,
        PortablePostgresOptions options,
        bool startedByUs,
        Action<string>? log)
    {
        _executablePath = executablePath;
        _instanceName = instanceName;
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

    /// <summary>
    /// 卸载/更新前同步停止便携数据库。安装器删除程序目录时，若 PostgreSQL 子进程仍在运行，
    /// 会因为文件被占用而残留。此方法不依赖 API 主机，供安装器/卸载脚本直接调用。
    /// <para>
    /// 注意：当前应用内没有调用方（旧的 Velopack 钩子已移除），Electron 安装包的卸载钩子
    /// 需要重新接上它，见 <c>docs/ROADMAP.md</c>。
    /// </para>
    /// </summary>
    public static void TryStopForUninstall(string contentRoot)
    {
        try
        {
            var options = ReadOptionsFromAppSettings(contentRoot);
            if (options is null || !options.Enabled) return;

            var pg0 = ResolveExecutableForStop(options, contentRoot);
            if (pg0 is null) return;

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = pg0,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("stop");
            process.StartInfo.ArgumentList.Add("--name");
            process.StartInfo.ArgumentList.Add(options.InstanceName);
            process.Start();
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            }
        }
        catch
        {
            // 卸载钩子绝不能让卸载失败。
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

    private static string? ResolveExecutableForStop(PortablePostgresOptions options, string contentRoot)
    {
        // 与正常启动走同一条解析链（配置路径 → 按 RID 自动定位），
        // 否则「升级/卸载时停不掉旧的 pg0」会在非 Windows 平台上静默失效。
        var resolved = PlatformRuntime.ResolveTool(
            contentRoot, options.ExecutablePath, Pg0ToolFolder, Pg0BaseName,
            vendorFileName: PlatformRuntime.Pg0VendorFileName);
        if (resolved is not null) return resolved;

        // 常规运行时 pg0 已被缓存到用户数据目录（安装目录可能已被升级替换）。
        foreach (var cached in CachedExecutableCandidates())
        {
            if (File.Exists(cached)) return cached;
        }

        return null;
    }

    /// <summary>工具目录（相对内容根）。</summary>
    private const string Pg0ToolFolder = "ExternalTools/pgSQL";

    /// <summary>不带平台后缀的程序名。</summary>
    private const string Pg0BaseName = "pg0";

    /// <summary>
    /// 用户数据目录里的 pg0 缓存路径。
    /// 第一个是当前平台的规范名；第二个是旧版（只有一个 Windows 包时）写入的文件名，
    /// 升级上来的机器仍能复用它，不会重复解压 60 MB。
    /// </summary>
    private static IEnumerable<string> CachedExecutableCandidates()
    {
        var tools = Path.Combine(AppPaths.UserDataRoot, "tools");
        yield return Path.Combine(tools, $"pg0-{PlatformRuntime.RuntimeIdentifier}{PlatformRuntime.ExecutableSuffix}");

        if (PlatformRuntime.RuntimeIdentifier == "win-x64")
            yield return Path.Combine(tools, "pg0-windows-x86_64.exe");
    }

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
            log?.Invoke("检测到外部数据库连接串，跳过便携 PostgreSQL。");
            return null;
        }

        var portableRoot = ResolvePortableRoot()
            ?? throw new InvalidOperationException(
                "找不到可写的纯英文目录用于运行便携 PostgreSQL。"
                + AppPaths.BuildLocationAdvice() + "。"
                + "已尝试的目录：" + string.Join("；", PortableRootFailures)
                + "。也可以改用外部 PostgreSQL（配置 ConnectionStrings:KnowledgeBase）。");

        var executable = ResolveExecutable(options, contentRoot, portableRoot, log);

        // 上一次进程异常退出时数据库可能仍在运行：优先复用，避免重复解压/占用端口。
        var running = await TryGetRunningInstanceAsync(executable, options.InstanceName, cancellationToken);
        if (running is { } instance)
        {
            log?.Invoke($"检测到已运行的 PostgreSQL 实例 '{options.InstanceName}'，端口 {instance.Port}。");
            var reused = new PortablePostgresHost(
                executable, options.InstanceName, instance.Port, options.Database, options, startedByUs: false, log);
            await reused.EnsureDatabaseAndVectorAsync(cancellationToken);
            return reused;
        }

        var dataDirectory = ResolveDataDirectory(options, contentRoot, portableRoot);
        Directory.CreateDirectory(dataDirectory);

        var port = FindAvailablePort(options.Port);
        var process = StartProcess(executable, options, port, dataDirectory, log);

        var host = new PortablePostgresHost(
            executable, options.InstanceName, port, options.Database, options, startedByUs: true, log)
        {
            _process = process
        };

        try
        {
            await WaitUntilReadyAsync(process, port, options, cancellationToken);
            await host.EnsureDatabaseAndVectorAsync(cancellationToken);
            return host;
        }
        catch
        {
            await host.StopAsync(CancellationToken.None);
            throw;
        }
    }

    private static readonly List<string> PortableRootFailures = new();

    /// <summary>
    /// 返回一个纯 ASCII 且可写的产品根目录（pg0 在非 ASCII 路径下无法工作）。
    /// 依次尝试 %LOCALAPPDATA%\MyFirstMafAgentData、%TEMP%、%ProgramData%、用户主目录。
    /// </summary>
    private static string? ResolvePortableRoot()
    {
        var candidates = new[]
        {
            AppPaths.UserDataRoot,
            Path.Combine(Path.GetTempPath(), AppPaths.ProductFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppPaths.ProductFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + AppPaths.ProductFolderName.ToLowerInvariant())
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!IsAsciiPath(candidate))
            {
                PortableRootFailures.Add($"{candidate}（路径含非 ASCII 字符）");
                continue;
            }

            var error = TryCreateWritableDirectory(candidate);
            if (error is null) return candidate;
            PortableRootFailures.Add($"{candidate}（{error}）");
        }

        return null;
    }

    private static string ResolveExecutable(
        PortablePostgresOptions options,
        string contentRoot,
        string portableRoot,
        Action<string>? log)
    {
        var source = PlatformRuntime.ResolveTool(
                contentRoot, options.ExecutablePath, Pg0ToolFolder, Pg0BaseName, log,
                PlatformRuntime.Pg0VendorFileName)
            ?? throw new FileNotFoundException(BuildMissingExecutableMessage(contentRoot, options), options.ExecutablePath);

        // pg0 启动的 postgres.exe 会继承工作目录，而 pg0 自身也会在 exe 同目录写入运行文件。
        // 如果直接使用程序目录里的 pg0：
        //   1. 安装路径含中文时 pg0 会抛 os error 5；
        //   2. postgres 子进程会锁住安装目录，导致升级/卸载时报“拒绝访问”。
        // 因此统一把 pg0 复制到用户数据目录（纯 ASCII、可写）后再运行。
        var cacheDirectory = Path.Combine(portableRoot, "tools");
        if (IsUnder(source, cacheDirectory))
        {
            PlatformRuntime.EnsureExecutable(source);
            return source;
        }

        if (!options.CacheExecutableInLocalAppData)
        {
            PlatformRuntime.EnsureExecutable(source);
            return source;
        }

        var cacheError = TryCreateWritableDirectory(cacheDirectory);
        if (cacheError is not null)
        {
            throw new InvalidOperationException(
                $"应用安装路径包含非 ASCII 字符（{source}），pg0 无法在该路径下运行，"
                + $"且无法建立缓存目录 {cacheDirectory}（{cacheError}）。"
                + AppPaths.BuildLocationAdvice() + "。"
                + "也可以改用外部 PostgreSQL（配置 ConnectionStrings:KnowledgeBase）。");
        }

        var target = Path.Combine(
            cacheDirectory, $"pg0-{PlatformRuntime.RuntimeIdentifier}{PlatformRuntime.ExecutableSuffix}");
        var sourceInfo = new FileInfo(source);

        if (!File.Exists(target)
            || new FileInfo(target).Length != sourceInfo.Length
            || File.GetLastWriteTimeUtc(target) != sourceInfo.LastWriteTimeUtc)
        {
            var temp = target + ".tmp";
            try
            {
                // 用流复制并重试：首次运行时安全软件可能正在扫描 60MB 的 exe，
                // File.Copy 会立刻抛 UnauthorizedAccessException，稍后重试通常可成功。
                CopyWithRetry(source, temp, log);
                File.Move(temp, target, overwrite: true);
                // Unix 上复制会丢掉 +x 位，不补上的话 Process.Start 直接 Permission denied。
                PlatformRuntime.EnsureExecutable(target);
                log?.Invoke($"已将 pg0 缓存到 {target}。");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                TryDelete(temp);
                if (!File.Exists(target))
                    throw new InvalidOperationException(
                        $"无法把 pg0 复制到 {target}，可能被安全软件拦截。"
                        + AppPaths.BuildLocationAdvice() + "，"
                        + "或在安全软件中放行本程序后重试。原始路径：" + source,
                        exception);
                log?.Invoke($"更新 pg0 缓存失败，继续使用已有缓存：{exception.Message}");
            }
        }

        return target;
    }

    /// <summary>
    /// 缺少 pg0 时的报错文案。
    ///
    /// <para>
    /// 这里必须写出「当前平台 + 应该放哪个文件 + 去哪里下载」，
    /// 否则跨平台发布的包一旦漏了二进制，用户只会看到
    /// “找不到便携数据库程序”，完全不知道要补什么。
    /// </para>
    /// </summary>
    private static string BuildMissingExecutableMessage(string contentRoot, PortablePostgresOptions options)
    {
        var candidates = PlatformRuntime.ToolCandidates(Pg0ToolFolder, Pg0BaseName, PlatformRuntime.Pg0VendorFileName)
            .Select(relative => "  - " + PlatformRuntime.ToAbsolute(contentRoot, relative));

        var configured = string.IsNullOrWhiteSpace(options.ExecutablePath)
            ? "（未配置，按当前平台自动定位）"
            : $"（配置值：{options.ExecutablePath}）";

        var vendorHint = PlatformRuntime.Pg0VendorFileName is { } vendorName
            ? $"下载 {vendorName} 后直接放到 {Pg0ToolFolder}/{PlatformRuntime.RuntimeIdentifier}/ （不用改名）"
            : $"上游暂无 {PlatformRuntime.RuntimeIdentifier} 的发行包，请改用外部 PostgreSQL";

        return
            $"找不到便携数据库程序：当前平台 {PlatformRuntime.Describe()}（RID {PlatformRuntime.RuntimeIdentifier}）。"
            + "已尝试：" + Environment.NewLine + string.Join(Environment.NewLine, candidates) + Environment.NewLine
            + $"请从 {PlatformRuntime.Pg0ReleasesUrl} {vendorHint}{configured}；"
            + "也可以用 ConnectionStrings__KnowledgeBase 指向已有 PostgreSQL（容器部署推荐这种方式）。";
    }

    private static void CopyWithRetry(string source, string target, Action<string>? log)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
                if (attempt < 3)
                {
                    log?.Invoke($"复制 pg0 第 {attempt} 次失败，稍后重试：{exception.Message}");
                    Thread.Sleep(800 * attempt);
                }
            }
        }

        throw lastError!;
    }

    private static void TryDelete(string path)
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

    private static string ResolveDataDirectory(
        PortablePostgresOptions options,
        string contentRoot,
        string portableRoot)
    {
        string directory;
        if (!string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            directory = Path.IsPathRooted(options.DataDirectory)
                ? options.DataDirectory
                : Path.GetFullPath(Path.Combine(contentRoot, options.DataDirectory.Replace('/', Path.DirectorySeparatorChar)));
        }
        else
        {
            directory = Path.Combine(portableRoot, "pgsql", SanitizeName(options.InstanceName));
        }

        // pg0 读取中文路径下的运行输出会报 "stream did not contain valid UTF-8"，
        // 因此数据目录同样必须是纯 ASCII；配置了中文路径时回落到可写根目录。
        if (!IsAsciiPath(directory))
        {
            var fallback = Path.Combine(portableRoot, "pgsql", SanitizeName(options.InstanceName));
            if (IsAsciiPath(fallback)) return fallback;

            throw new InvalidOperationException(
                $"便携 PostgreSQL 的数据目录必须是纯英文路径，当前为 {directory}。"
                + "请修改 PortablePostgres:DataDirectory 或改用外部 PostgreSQL。");
        }

        return directory;
    }

    private static Process StartProcess(
        string executable,
        PortablePostgresOptions options,
        int port,
        string dataDirectory,
        Action<string>? log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(dataDirectory) ?? Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(options.InstanceName);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString());
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--username");
        startInfo.ArgumentList.Add(options.Username);
        startInfo.ArgumentList.Add("--password");
        startInfo.ArgumentList.Add(options.Password);
        startInfo.ArgumentList.Add("--database");
        startInfo.ArgumentList.Add(options.Database);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => LogLine(options, log, e.Data);
        process.ErrorDataReceived += (_, e) => LogLine(options, log, e.Data);

        log?.Invoke($"启动便携 PostgreSQL：端口 {port}，数据目录 {dataDirectory}");
        if (!process.Start())
            throw new InvalidOperationException("无法启动便携 PostgreSQL 进程。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static void LogLine(PortablePostgresOptions options, Action<string>? log, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        log?.Invoke($"pg0: {line}");
    }

    private static async Task WaitUntilReadyAsync(
        Process process,
        int port,
        PortablePostgresOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, options.StartupTimeoutSeconds));
        Exception? lastError = null;
        int? exitCode = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // pg0 拉起 PostgreSQL 后会立即退出（数据库转成后台服务），
            // 因此进程退出本身不代表失败：只有非 0 退出码才是真失败。
            if (exitCode is null && process.HasExited)
            {
                exitCode = process.ExitCode;
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"便携 PostgreSQL 启动失败（pg0 退出码 {exitCode}）。"
                        + $"pg0 的安装目录为 {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pg0")}，"
                        + "若日志出现 \"os error 5\"，通常是杀毒软件/受控文件夹访问拦截了该目录或数据目录的写入，"
                        + "也可能是应用安装在非 ASCII（中文）路径下。"
                        + $"日志目录：{AppPaths.LogsDirectory}",
                        lastError);
                }
            }

            try
            {
                var builder = new NpgsqlConnectionStringBuilder(BuildConnectionString(port, "postgres", options))
                {
                    Timeout = 3,
                    Pooling = false
                };
                await using var connection = new NpgsqlConnection(builder.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1";
                await command.ExecuteScalarAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is NpgsqlException or SocketException or TimeoutException)
            {
                lastError = exception;
                await Task.Delay(500, cancellationToken);
            }
        }

        throw new TimeoutException(
            $"等待便携 PostgreSQL 就绪超时（{options.StartupTimeoutSeconds} 秒，端口 {port}）。",
            lastError);
    }

    private async Task EnsureDatabaseAndVectorAsync(CancellationToken cancellationToken)
    {
        var adminConnectionString = BuildConnectionString(Port, "postgres", _options);
        await using (var admin = new NpgsqlConnection(adminConnectionString))
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
        catch (PostgresException exception) when (exception.SqlState == "0A000"
            && exception.MessageText.Contains("vector", StringComparison.OrdinalIgnoreCase))
        {
            // 少数情况下扩展文件尚未安装：调用 pg0 自带安装命令后重试一次。
            _log?.Invoke("pgvector 尚未安装，尝试调用 pg0 install-extension vector。");
            await InstallVectorExtensionAsync(cancellationToken);
            await EnableVectorExtensionAsync(cancellationToken);
        }
    }

    private async Task EnableVectorExtensionAsync(CancellationToken cancellationToken)
    {
        var connectionString = BuildConnectionString(Port, Database, _options);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        _log?.Invoke($"pgvector 已启用（数据库 {Database}）。");
    }

    private async Task InstallVectorExtensionAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("install-extension");
        startInfo.ArgumentList.Add("vector");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(_instanceName);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法执行 pg0 install-extension。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"pg0 install-extension vector 失败（退出码 {process.ExitCode}）：{output}{error}");
        }
    }

    private static string BuildConnectionString(int port, string database, PortablePostgresOptions options) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = database,
            Username = options.Username,
            Password = options.Password,
            Pooling = true,
            MaxPoolSize = 100,
            Timeout = 15,
            CommandTimeout = 120,
            IncludeErrorDetail = true
        }.ConnectionString;

    private static async Task<(int Port, bool Running)?> TryGetRunningInstanceAsync(
        string executable,
        string instanceName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("info");
        startInfo.ArgumentList.Add("--name");
        startInfo.ArgumentList.Add(instanceName);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add("json");

        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return null;

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("running", out var running) || !running.GetBoolean())
                return null;
            if (!root.TryGetProperty("port", out var portElement) || !portElement.TryGetInt32(out var port))
                return null;
            return (port, true);
        }
        catch (JsonException)
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

    private static string SanitizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        return builder.Length == 0 ? "default" : builder.ToString();
    }

    private static bool IsAsciiPath(string path) => path.All(static character => character < 128);

    /// <summary>
    /// 判断路径是否位于指定目录之下（用于识别已经缓存过的 pg0）。
    /// </summary>
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
            return exception.Message;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopped) return;
        _stopped = true;

        if (!_startedByUs || !_options.StopOnExit)
        {
            _log?.Invoke("便携 PostgreSQL 由其他进程启动，保留运行。");
            return;
        }

        _log?.Invoke("正在停止便携 PostgreSQL...");
        try
        {
            using var stop = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            stop.StartInfo.ArgumentList.Add("stop");
            stop.StartInfo.ArgumentList.Add("--name");
            stop.StartInfo.ArgumentList.Add(_instanceName);
            stop.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await stop.WaitForExitAsync(timeout.Token);
                _log?.Invoke("便携 PostgreSQL 已停止。");
            }
            catch (OperationCanceledException)
            {
                _log?.Invoke("停止 pg0 超时，强制结束进程。");
            }
        }
        catch (Exception exception)
        {
            _log?.Invoke($"停止 pg0 失败：{exception.Message}");
        }

        if (_process is { HasExited: false })
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception exception) { _log?.Invoke($"结束 pg0 进程失败：{exception.Message}"); }
        }

        _process?.Dispose();
        _process = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }
}
