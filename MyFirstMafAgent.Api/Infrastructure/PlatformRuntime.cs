using System.Runtime.InteropServices;

namespace MyFirstMafAgent.Api.Infrastructure;

/// <summary>
/// 平台运行时约定：当前 RID、可执行文件后缀，以及「随包分发的原生工具」定位规则。
///
/// <para>
/// 为什么需要它：<c>ExternalTools</c> 里放的是**与平台绑定**的二进制
/// （便携 PostgreSQL、ffmpeg）。同一个发布脚本要能产出
/// <c>win-x64</c> / <c>linux-x64</c> / <c>osx-arm64</c> 等多份包，
/// 就必须让「找哪个文件」由运行时决定，而不是在代码和配置里写死路径。
/// </para>
///
/// <para>
/// 单文件工具（ffmpeg）的目录约定（按优先级，先命中先用）：
/// <list type="number">
///   <item><c>ExternalTools/{tool}/{rid}/{name}{suffix}</c> —— 推荐，多平台可共存。</item>
///   <item><c>ExternalTools/{tool}/{name}-{rid}{suffix}</c> —— 扁平命名。</item>
///   <item><c>ExternalTools/{tool}/{name}{suffix}</c> —— 旧版单平台布局，保留兼容。</item>
/// </list>
/// 找不到时返回 null，由调用方给出「去下载哪个文件、放哪里」的具体提示。
/// </para>
///
/// <para>
/// 便携 PostgreSQL 不是单文件，而是整棵目录树（<c>bin</c> / <c>lib</c> / <c>share</c> 相对位置不能拆，
/// 只拷 exe 会报 <c>0xC0000135</c> 找不到 DLL），所以单独用
/// <see cref="ResolvePostgresBinDirectory"/> 按“目录”解析，见
/// <see cref="PortablePostgresFolder"/>。
/// </para>
/// </summary>
public static class PlatformRuntime
{
    /// <summary>便携 PostgreSQL 的工具目录（相对内容根）。</summary>
    public const string PortablePostgresFolder = "ExternalTools/pgSQL";

    /// <summary>PostgreSQL 主程序名（不带后缀）。</summary>
    public const string PostgresCommand = "postgres";

    /// <summary>当前进程的 .NET RID（<c>win-x64</c> / <c>linux-arm64</c> / <c>osx-arm64</c> …）。</summary>
    public static string RuntimeIdentifier { get; } = ComputeRuntimeIdentifier();

    /// <summary>可执行文件后缀：Windows 是 <c>.exe</c>，其它平台为空。</summary>
    public static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    /// <summary>发布脚本需要产出的全部 RID。</summary>
    public static IReadOnlyList<string> SupportedRuntimeIdentifiers { get; } =
    [
        "win-x64", "win-arm64",
        "linux-x64", "linux-arm64",
        "osx-x64", "osx-arm64"
    ];

    /// <summary>把一个工具名拼成当前平台的完整文件名，例如 <c>pg_ctl.exe</c> / <c>pg_ctl</c>。</summary>
    public static string WithSuffix(string name) =>
        name.EndsWith(ExecutableSuffix, StringComparison.OrdinalIgnoreCase) ? name : name + ExecutableSuffix;

    /// <summary>当前平台 PostgreSQL 主程序的完整文件名，例如 <c>postgres.exe</c> / <c>postgres</c>。</summary>
    public static string PostgresExecutableName => WithSuffix(PostgresCommand);

    /// <summary>
    /// 便携 PostgreSQL 的 <c>bin</c> 目录候选（相对内容根），按优先级排序。
    /// <para>
    /// 首选按 RID 分目录，允许同一份检出里放多个平台的 pgsql 树；
    /// 其次是不分 RID 的扁平布局（把 <c>dist\pgsql</c> 直接放进 <c>ExternalTools/pgSQL</c> 就长这样）。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> PostgresBinCandidates() =>
    [
        $"{PortablePostgresFolder}/{RuntimeIdentifier}/bin",
        $"{PortablePostgresFolder}/bin"
    ];

    /// <summary>
    /// 解析便携 PostgreSQL 的 <c>bin</c> 目录（里面必须有 <c>postgres[.exe]</c>）。
    ///
    /// <para>
    /// <paramref name="configuredPath"/> 允许配成 <c>bin</c> 目录本身，也允许配成 pgsql 根目录
    /// （自动补 <c>bin</c>）——用户从发布包目录里复制路径时两种都可能。配置的路径无效
    /// （不存在、或目录里没有主程序）时回落到按 RID 自动定位并记日志，而不是直接启动失败。
    /// </para>
    /// </summary>
    /// <param name="contentRoot">应用内容根目录。</param>
    /// <param name="configuredPath">配置的路径；可为空。</param>
    /// <param name="log">可选的诊断输出。</param>
    public static string? ResolvePostgresBinDirectory(
        string contentRoot, string? configuredPath, Action<string>? log = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = ToAbsolute(contentRoot, configuredPath);
            foreach (var candidate in new[] { configured, Path.Combine(configured, "bin") })
            {
                if (File.Exists(Path.Combine(candidate, PostgresExecutableName)))
                {
                    log?.Invoke($"使用配置的便携 PostgreSQL 目录：{candidate}");
                    return candidate;
                }
            }

            log?.Invoke(
                $"PortablePostgres:BinDirectory 指向的目录里没有 {PostgresExecutableName}：{configured}，"
                + $"改按当前平台（{RuntimeIdentifier}）自动定位。");
        }

        foreach (var candidate in PostgresBinCandidates())
        {
            var path = ToAbsolute(contentRoot, candidate);
            if (!File.Exists(Path.Combine(path, PostgresExecutableName))) continue;

            log?.Invoke($"使用 {RuntimeIdentifier} 对应的便携 PostgreSQL：{path}");
            return path;
        }

        return null;
    }

    /// <summary>某个工具的相对候选路径（相对内容根目录），按优先级排序。</summary>
    /// <param name="toolFolder">工具目录，例如 <c>ExternalTools/pgSQL</c>。</param>
    /// <param name="baseName">不带后缀的程序名，例如 <c>ffmpeg</c>。</param>
    /// <param name="vendorFileName">可选：上游发行包里的原始文件名（用户可不改名直接放进去）。</param>
    public static IReadOnlyList<string> ToolCandidates(
        string toolFolder, string baseName, string? vendorFileName = null)
    {
        var fileName = WithSuffix(baseName);
        var candidates = new List<string>(5);

        // 1) 推荐位置：按 RID 分目录（多平台可共存）。
        if (!string.IsNullOrWhiteSpace(vendorFileName))
            candidates.Add($"{toolFolder}/{RuntimeIdentifier}/{vendorFileName}");
        candidates.Add($"{toolFolder}/{RuntimeIdentifier}/{fileName}");

        // 2) 不分子目录：上游原名，或 {name}-{rid} 扁平命名。
        if (!string.IsNullOrWhiteSpace(vendorFileName))
            candidates.Add($"{toolFolder}/{vendorFileName}");
        candidates.Add($"{toolFolder}/{baseName}-{RuntimeIdentifier}{ExecutableSuffix}");

        // 3) 旧版单平台布局。
        candidates.Add($"{toolFolder}/{fileName}");

        return candidates;
    }

    /// <summary>
    /// 解析一个随包分发的工具。
    ///
    /// <para>
    /// <paramref name="configuredPath"/> 非空且文件存在时**优先尊重配置**（允许用户把工具放到任意位置）；
    /// 配置为空、或配置了但文件不存在（典型场景：把 Windows 包里的 appsettings.json
    /// 原样带到 Linux），则回落到按 RID 的自动定位。
    /// 这样「配置写死了旧路径」不会在换平台后变成一个看不懂的启动失败。
    /// </para>
    /// </summary>
    /// <param name="contentRoot">应用内容根目录（<c>appsettings.json</c> 所在目录）。</param>
    /// <param name="configuredPath">配置里的路径；可为绝对路径、相对内容根的路径或空。</param>
    /// <param name="toolFolder">工具目录，例如 <c>ExternalTools/pgSQL</c>。</param>
    /// <param name="baseName">不带后缀的程序名，例如 <c>ffmpeg</c>。</param>
    /// <param name="log">可选的诊断输出（会打印「没用配置里的路径，改用哪个」）。</param>
    /// <param name="vendorFileName">可选：上游发行包的原始文件名。</param>
    public static string? ResolveTool(
        string contentRoot,
        string? configuredPath,
        string toolFolder,
        string baseName,
        Action<string>? log = null,
        string? vendorFileName = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = ToAbsolute(contentRoot, configuredPath);
            if (File.Exists(configured)) return configured;

            // 配置的路径不存在时不直接失败：先试试按 RID 找到的那份，
            // 找不到再报错（错误信息里会同时列出配置值和候选路径）。
            log?.Invoke($"配置的工具路径不存在：{configured}，尝试按当前平台（{RuntimeIdentifier}）自动定位。");
        }

        var found = TryFindTool(contentRoot, toolFolder, baseName, vendorFileName);
        if (found is not null)
            log?.Invoke($"使用 {RuntimeIdentifier} 对应的 {baseName}：{found}");

        return found;
    }

    /// <summary>按候选顺序查找工具，找不到返回 null。</summary>
    public static string? TryFindTool(
        string contentRoot, string toolFolder, string baseName, string? vendorFileName = null)
    {
        foreach (var candidate in ToolCandidates(toolFolder, baseName, vendorFileName))
        {
            var path = ToAbsolute(contentRoot, candidate);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// <summary>把相对路径按内容根目录展开；绝对路径原样返回。</summary>
    public static string ToAbsolute(string contentRoot, string path) =>
        Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(contentRoot, path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Unix 上补偿可执行权限。
    ///
    /// <para>
    /// 从 zip/tar 解出来、或用 <see cref="File.Copy(string, string)"/> 复制过的二进制会丢掉
    /// <c>+x</c> 位，<c>Process.Start</c> 直接报 <c>Permission denied</c>（错误码 13）。
    /// 这里做成幂等的最佳努力：Windows 上是空操作，失败也不抛（真正的失败会由启动报错暴露）。
    /// </para>
    /// </summary>
    public static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode execute =
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & execute) != execute) File.SetUnixFileMode(path, mode | execute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限补不上不算致命：真正的失败会在启动子进程时报出来，且信息更具体。
        }
    }

    /// <summary>把 RID 翻译成人能读的平台描述，用于启动日志。</summary>
    public static string Describe() => RuntimeIdentifier switch
    {
        "win-x64" => "Windows x64",
        "win-arm64" => "Windows ARM64",
        "win-x86" => "Windows x86",
        "linux-x64" => "Linux x64",
        "linux-arm64" => "Linux ARM64",
        "osx-x64" => "macOS x64",
        "osx-arm64" => "macOS Apple Silicon",
        _ => RuntimeIdentifier
    };

    private static string ComputeRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows()) return "win-" + architecture;
        if (OperatingSystem.IsMacOS()) return "osx-" + architecture;
        return "linux-" + architecture;
    }
}
