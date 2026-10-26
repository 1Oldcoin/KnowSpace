using System.Text;

namespace MyFirstMafAgent.Api.Infrastructure;

/// <summary>应用级路径约定（便携数据库、日志、内容根目录）。</summary>
public static class AppPaths
{
    /// <summary>
    /// 安装目录名（%LOCALAPPDATA%\MyFirstMafAgent）。
    /// 该目录由打包器/安装器管理，升级或卸载时会被整体删除，因此不能存放用户数据。
    /// </summary>
    public const string ProductFolderName = "MyFirstMafAgent";

    /// <summary>应用程序目录（%LOCALAPPDATA%\MyFirstMafAgent），仅安装版使用。</summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);

    /// <summary>
    /// 用户数据根目录（%LOCALAPPDATA%\MyFirstMafAgentData）。
    /// 与安装目录分开，卸载/升级不会丢失上传文件、日志、模型缓存与数据库。
    /// </summary>
    public static string UserDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName + "Data");

    /// <summary>可写数据目录（%LOCALAPPDATA%\MyFirstMafAgentData\data）。</summary>
    public static string DataDirectory => Path.Combine(UserDataRoot, "data");

    /// <summary>日志目录（%LOCALAPPDATA%\MyFirstMafAgentData\logs）。</summary>
    public static string LogsDirectory => Path.Combine(UserDataRoot, "logs");

    /// <summary>
    /// 解析应用内容根目录：优先显式环境变量，其次当前目录（dotnet run / 从发布目录启动），
    /// 最后回退到程序所在目录（从任意工作目录启动 exe）。
    /// </summary>
    public static string ResolveContentRoot()
    {
        var configured = Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        var current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "appsettings.json")))
            return current;

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 便携 PostgreSQL 的数据目录名（{便携根目录}\pgdata）。
    /// 数据目录必须独立于程序目录：安装目录升级时会被整体替换。
    /// </summary>
    public const string PostgresDataFolderName = "pgdata";

    /// <summary>
    /// 便携 PostgreSQL 的日志目录名（{便携根目录}\pglogs）。
    /// <para>
    /// 必须与数据目录**平级**而不能放进去：<c>pg_ctl -l</c> 的日志若落在 PGDATA 内，
    /// PostgreSQL 崩溃恢复对数据目录做 fsync 时会撞上这个仍被占用的文件句柄，
    /// 报 <c>could not open file "./log/ctl.out": sharing violation</c> 并启动超时。
    /// </para>
    /// </summary>
    public const string PostgresLogFolderName = "pglogs";

    /// <summary>
    /// 便携数据库的根目录候选，按优先级排列（数据目录 = <c>{根目录}\pgdata</c>，
    /// 日志目录 = <c>{根目录}\pglogs</c>）。
    ///
    /// <para>
    /// 前四个是「用户/系统级」位置，卸载升级都不会碰；最后一档是**与程序同目录**的
    /// <c>{程序目录}\App_Data</c>，也就是文件夹版的「便携模式」兜底：
    /// 当机器的安全软件（例如火绒的文件防护）只允许未签名 exe 写入自身目录、
    /// 或用户目录整体不可写时，只有它还能用。代价是覆盖安装/删除程序目录会连带数据，
    /// 因此排在最后。
    /// </para>
    ///
    /// <para>
    /// 纯 ASCII 路径是**首选**（PostgreSQL 官方建议数据目录无中文、无空格），
    /// 因此调用方逐个候选筛掉非 ASCII 路径；配置里显式指定非 ASCII 路径时只警告，不再拒绝启动。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> PortableDataRootCandidates(string contentRoot)
    {
        var candidates = new List<string>(5)
        {
            UserDataRoot,
            Path.Combine(Path.GetTempPath(), ProductFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ProductFolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + ProductFolderName.ToLowerInvariant()),
            Path.Combine(Path.GetFullPath(contentRoot), "App_Data")
        };

        return candidates
            .Where(static candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>路径是否为纯 ASCII（便携 PostgreSQL 数据目录的首选条件）。</summary>
    public static bool IsAsciiPath(string path) => path.All(static character => character < 128);

    /// <summary>
    /// 把「探针文件写入失败」翻译成能直接照着排查的中文原因。
    /// <c>UnauthorizedAccessException</c> 几乎都不是代码问题，而是安全软件的 HIPS/勒索防护
    /// 或 Windows「受控文件夹访问」拦下了未签名程序的写入，所以这里必须点名。
    /// </summary>
    public static string DescribeWriteFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException =>
            "写入被拒绝（Access denied）：通常是火绒/360 等安全软件的文件防护，"
            + "或 Windows「受控文件夹访问」拦截了未签名程序，可在安全软件里放行本程序或整个应用目录"
            + $"（原始信息：{exception.Message}）",
        DirectoryNotFoundException or IOException =>
            $"IO 错误：{exception.Message}",
        _ => exception.Message
    };

    /// <summary>
    /// 判断程序是否运行在 Windows 受保护的用户目录（桌面 / 文档 / 下载）下。
    /// 这些位置受「受控文件夹访问」等策略保护，从其中启动的未签名程序写用户目录会被拒绝，
    /// 便携 PostgreSQL 因此无法初始化。返回命中的目录名，未命中返回 null。
    /// </summary>
    public static string? GetRestrictedLocation(string? directory = null)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var target = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        var folders = new (string Name, Environment.SpecialFolder Folder)[]
        {
            ("桌面", Environment.SpecialFolder.DesktopDirectory),
            ("文档", Environment.SpecialFolder.MyDocuments),
            ("下载", Environment.SpecialFolder.UserProfile)
        };

        foreach (var (name, folder) in folders)
        {
            var root = Environment.GetFolderPath(folder);
            if (string.IsNullOrWhiteSpace(root)) continue;

            var rootPath = Path.GetFullPath(root);
            if (name == "下载")
            {
                var downloads = Path.Combine(rootPath, "Downloads");
                if (IsUnder(target, downloads)) return name;
                continue;
            }

            if (IsUnder(target, rootPath)) return name;
        }

        return null;
    }

    private static bool IsUnder(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把受限位置与缓存目录的失败原因组合成可操作的建议文本。</summary>
    public static string BuildLocationAdvice()
    {
        var restricted = GetRestrictedLocation();
        return restricted is null
            ? "请把整个应用目录移动到纯英文路径（例如 D:\\Apps\\MyFirstMafAgent）后重试"
            : $"应用当前位于 Windows 受保护目录（{restricted}）。受控文件夹访问会阻止程序写入用户目录，"
              + "请把整个应用文件夹移动到其他位置（例如 D:\\Apps\\MyFirstMafAgent 或 C:\\Apps\\MyFirstMafAgent）后重新运行";
    }
}
