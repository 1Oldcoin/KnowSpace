using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 插件目录布局的唯一计算入口。
///
/// <pre>
/// plugins/
///   {pluginId}/
///     active.json          { "version": "1.0.0", "previous": "0.9.0" }
///     versions/
///       1.0.0/             manifest.json + 入口文件（一个完整包）
///       0.9.0/
///   .data/{pluginId}/      插件唯一可写目录
///   .staging/{guid}/       安装临时目录，安装成功/失败都会清理
/// </pre>
///
/// <para>
/// 兼容阶段 3 的扁平布局：若 <c>plugins/{id}/manifest.json</c> 存在，
/// 该目录本身即"本地版本"，没有 <c>versions/</c> 与 <c>active.json</c>。
/// </para>
/// </summary>
public sealed class PluginPaths
{
    public PluginPaths(IOptions<PluginOptions> options, IHostEnvironment environment)
    {
        var configured = options.Value.RootPath;
        Root = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    /// <summary>直接指定根目录（测试与命令行工具用）。</summary>
    public PluginPaths(string root) => Root = Path.GetFullPath(root);

    public const string ActiveFileName = "active.json";

    public const string VersionsDirectoryName = "versions";

    /// <summary>本项目的主清单文件名。</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// 认得的清单文件名，按优先级排列。
    ///
    /// <para>
    /// <c>plugin.json</c> / <c>skill.json</c> 是插件生态里既有的命名（QQ Agent 一类）。
    /// 认它们不是为了兼容某个具体格式，而是因为"把现成插件包丢进来"这件事里，
    /// 文件名是最不该成为门槛的一环——内容本来就是同一套字段。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> ManifestFileNames { get; } =
        [ManifestFileName, "plugin.json", "skill.json"];

    /// <summary>找到目录下的清单文件（按 <see cref="ManifestFileNames"/> 顺序），没有则返回 null。</summary>
    public static string? FindManifestFile(string directory)
    {
        foreach (var name in ManifestFileNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>目录里是否有清单（任意一种命名）。</summary>
    public static bool HasManifest(string directory) => FindManifestFile(directory) is not null;

    public string Root { get; }

    public string StagingRoot => Path.Combine(Root, ".staging");

    public string PluginRoot(string pluginId) => Path.Combine(Root, pluginId);

    public string VersionsRoot(string pluginId) => Path.Combine(PluginRoot(pluginId), VersionsDirectoryName);

    public string VersionDirectory(string pluginId, string version) =>
        Path.Combine(VersionsRoot(pluginId), version);

    public string ActiveFile(string pluginId) => Path.Combine(PluginRoot(pluginId), ActiveFileName);

    public string DataDirectory(string pluginId) => Path.Combine(Root, ".data", pluginId);

    /// <summary>尽力删除目录；失败只记录，不向上抛。</summary>
    public static void TryDeleteDirectory(string path, ILogger? logger = null)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning("Failed to delete directory {Path}: {Message}", path, exception.Message);
        }
    }
}
