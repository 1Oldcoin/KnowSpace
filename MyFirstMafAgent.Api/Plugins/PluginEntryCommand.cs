namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 判断 manifest 里的 <c>entry.command</c> 是「宿主命令」（从 PATH 解析，如 <c>dotnet</c>）
/// 还是「插件包内的可执行文件」。
///
/// <para>
/// 旧版规则是「不含路径分隔符 且 不以 <c>.exe</c>/<c>.dll</c> 结尾 → 宿主命令」，
/// 这条规则在 Windows 上够用，但在 Linux/macOS 上会误判：
/// 一个原生的 <c>MyPlugin</c>（无后缀）会被当成 PATH 里的命令去执行，
/// 报 <c>No such file or directory</c>。所以补一条更直接的判据——
/// **插件目录里存在同名文件就当作相对入口**。
/// </para>
/// </summary>
internal static class PluginEntryCommand
{
    /// <summary>这些后缀一定是「文件」而不是 PATH 命令。</summary>
    private static readonly string[] FileExtensions =
    [
        ".exe", ".dll", ".js", ".mjs", ".cjs", ".py", ".sh", ".jar", ".bat", ".cmd", ".ps1"
    ];

    /// <summary>从 PATH 解析的宿主命令（<c>dotnet</c>、<c>node</c>、<c>python</c> …）。</summary>
    public static bool IsHostCommand(string? pluginDirectory, string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        // 带路径分隔符的一律按包内相对路径处理。
        if (command.Contains('\\') || command.Contains('/')) return false;

        foreach (var extension in FileExtensions)
        {
            if (command.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // 包内确实有这个名字的文件 → 它是入口文件（Linux 上的原生程序通常没有后缀）。
        if (!string.IsNullOrWhiteSpace(pluginDirectory))
        {
            try
            {
                if (File.Exists(Path.Combine(pluginDirectory, command))) return false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 路径不合法就退回按宿主命令判断，真正的错误会在启动时报出来。
            }
        }

        return true;
    }
}
