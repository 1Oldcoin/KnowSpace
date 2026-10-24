using System.Text.Json;
using System.Text.Json.Nodes;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 从插件目录读取并校验 manifest。
/// 校验分三层：schema/字段（SDK 校验器）、入口是否存在、版本布局是否可解析。
/// 三者都通过才允许启动进程（完整性校验在 <see cref="PluginManager"/> 里叠加）。
/// </summary>
public sealed class PluginManifestLoader(ILogger<PluginManifestLoader> logger)
{
    public const string ManifestFileName = PluginPaths.ManifestFileName;

    /// <summary>扫描根目录下的所有插件，返回每个插件的解析结果。</summary>
    public IReadOnlyList<PluginManifestLoadResult> Scan(string rootPath)
    {
        var results = new List<PluginManifestLoadResult>();

        if (!Directory.Exists(rootPath))
        {
            logger.LogInformation("Plugin directory {RootPath} does not exist; no plugins discovered", rootPath);
            return results;
        }

        foreach (var directory in Directory.EnumerateDirectories(rootPath))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.')) continue;
            if (IsReservedDirectory(directoryName)) continue;

            try
            {
                results.Add(LoadPlugin(ResolveDroppedDirectory(directory)));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to load plugin manifest from {Directory}", directory);
                results.Add(new PluginManifestLoadResult(
                    directory,
                    null,
                    PluginManifestValidationResult.Fail(exception.Message),
                    EntryExists: false,
                    EntryPath: null,
                    PluginRootDirectory: directory,
                    ActiveVersion: null,
                    InstalledVersions: []));
            }
        }

        return results;
    }

    /// <summary>
    /// 这些目录名是核心自己的（数据、暂存、版本），不能被当成插件。
    /// </summary>
    private static bool IsReservedDirectory(string name) =>
        name.Equals("versions", StringComparison.OrdinalIgnoreCase)
        || name.Equals(PluginPaths.VersionsDirectoryName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 处理"解压后多套了一层目录"的常见情况：<c>plugins/demo/demo/manifest.json</c>。
    ///
    /// <para>
    /// 只在自己这一层完全没有清单、且恰好只有一个子目录时下探（最多 3 层）：
    /// 多于一个子目录说明这更像一个装着一堆东西的文件夹，硬猜会选错。
    /// </para>
    /// </summary>
    private static string ResolveDroppedDirectory(string directory)
    {
        var current = directory;

        for (var depth = 0; depth < 3; depth++)
        {
            if (PluginPaths.HasManifest(current)) return current;
            if (Directory.Exists(Path.Combine(current, PluginPaths.VersionsDirectoryName))) return current;

            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current)
                    .Where(child =>
                    {
                        var name = Path.GetFileName(child);
                        return !name.StartsWith('.')
                            && !name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase)
                            && !IsReservedDirectory(name);
                    })
                    .ToList();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return current;
            }

            if (children.Count != 1) return current;
            current = children[0];
        }

        return current;
    }

    /// <summary>
    /// 解析一个插件根目录：优先使用 <c>versions/{active}</c>，
    /// 否则回退到扁平布局（<c>plugins/{id}/manifest.json</c>，阶段 3 的形态）。
    /// </summary>
    public PluginManifestLoadResult LoadPlugin(string pluginRootDirectory)
    {
        var versionsRoot = Path.Combine(pluginRootDirectory, PluginPaths.VersionsDirectoryName);
        var installed = GetInstalledVersions(pluginRootDirectory);

        if (!Directory.Exists(versionsRoot))
        {
            var flat = Load(pluginRootDirectory);
            return flat with
            {
                PluginRootDirectory = pluginRootDirectory,
                ActiveVersion = null,
                InstalledVersions = []
            };
        }

        var active = ReadActiveVersion(pluginRootDirectory);
        var chosen = active is not null
            ? installed.FirstOrDefault(version => string.Equals(version, active, StringComparison.Ordinal))
            : null;

        if (chosen is null && active is not null)
            logger.LogWarning(
                "Plugin at {Directory} points at version {Version} which is not installed; falling back to the newest version",
                pluginRootDirectory,
                active);

        chosen ??= installed.FirstOrDefault();

        if (chosen is null)
        {
            return new PluginManifestLoadResult(
                pluginRootDirectory,
                null,
                PluginManifestValidationResult.Fail($"{PluginPaths.VersionsDirectoryName}/ 下没有可用的插件版本"),
                EntryExists: false,
                EntryPath: null,
                PluginRootDirectory: pluginRootDirectory,
                ActiveVersion: null,
                InstalledVersions: []);
        }

        var loaded = Load(Path.Combine(versionsRoot, chosen));
        return loaded with
        {
            PluginRootDirectory = pluginRootDirectory,
            ActiveVersion = chosen,
            InstalledVersions = installed
        };
    }

    /// <summary>加载单个包目录（目录根部必须有清单：manifest.json / plugin.json / skill.json）。</summary>
    public PluginManifestLoadResult Load(string directory)
    {
        var manifestPath = PluginPaths.FindManifestFile(directory);
        if (manifestPath is null)
        {
            return new PluginManifestLoadResult(
                directory,
                null,
                PluginManifestValidationResult.Fail(
                    $"{PluginPaths.ManifestFileName} not found (also accepts {string.Join(" / ", PluginPaths.ManifestFileNames.Skip(1))})"),
                EntryExists: false,
                EntryPath: null,
                PluginRootDirectory: directory,
                ActiveVersion: null,
                InstalledVersions: []);
        }

        var json = File.ReadAllText(manifestPath);
        var (manifest, validation) = PluginManifestValidator.Parse(json);

        if (manifest is null)
            return new PluginManifestLoadResult(
                directory,
                null,
                validation,
                EntryExists: false,
                EntryPath: null,
                PluginRootDirectory: directory,
                ActiveVersion: null,
                InstalledVersions: []);

        // 入口校验：宿主命令（npx/dotnet/node…）可能根本不带文件参数，
        // 这时无法在安装期确认它存在，只能交给启动期报错，不能因此判插件不可用。
        var (entryPath, mustExist) = ResolveEntry(directory, manifest);
        var entryExists = !mustExist || (entryPath is not null && File.Exists(entryPath));

        if (!entryExists && validation.IsValid)
        {
            validation = new PluginManifestValidationResult(
                [.. validation.Errors, $"entry.command '{manifest.Entry?.Command}' was not found in the plugin directory"],
                validation.Warnings);
        }

        foreach (var warning in validation.Warnings)
            logger.LogWarning("Plugin {PluginId}: {Warning}", manifest.Id, warning);

        foreach (var error in validation.Errors)
            logger.LogWarning("Plugin {PluginId} is not usable: {Error}", manifest.Id, error);

        return new PluginManifestLoadResult(
            directory,
            manifest,
            validation,
            entryExists,
            entryPath,
            directory,
            null,
            []);
    }

    /// <summary>列出已安装版本，按 SemVer 降序；无法解析的目录名排在最后。</summary>
    public static IReadOnlyList<string> GetInstalledVersions(string pluginRootDirectory)
    {
        var versionsRoot = Path.Combine(pluginRootDirectory, PluginPaths.VersionsDirectoryName);
        if (!Directory.Exists(versionsRoot)) return [];

        return Directory.EnumerateDirectories(versionsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Where(name => !name.StartsWith('.'))
            .OrderByDescending(name => SemanticVersion.TryParse(name, out var version) ? version : new SemanticVersion(0, 0, 0))
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>读取 active.json 指向的版本；文件缺失或损坏时返回 null。</summary>
    public static string? ReadActiveVersion(string pluginRootDirectory)
    {
        var activeFile = Path.Combine(pluginRootDirectory, PluginPaths.ActiveFileName);
        if (!File.Exists(activeFile)) return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(activeFile));
            return node?["version"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析入口路径。相对路径以包目录为基准；
    /// 宿主命令（如 <c>dotnet</c>）直接从 PATH 解析，此时入口校验由 arguments 决定。
    /// </summary>
    public static string? ResolveEntryPath(string pluginDirectory, PluginManifest manifest) =>
        ResolveEntry(pluginDirectory, manifest).EntryPath;

    /// <summary>
    /// 解析入口，并说明"这个路径是否必须真实存在"。
    ///
    /// <para>
    /// 包内文件（<c>plugin.exe</c>、<c>server.js</c>、<c>plugin.dll</c>）必须在安装期就存在；
    /// 而 <c>npx -y @modelcontextprotocol/server-filesystem</c> 这种宿主命令 + 包名的组合，
    /// 路径由包管理器在运行期决定，安装期根本无从检查——把"查不到"当失败会直接挡掉所有 npx 形态的 MCP server。
    /// </para>
    /// </summary>
    public static (string? EntryPath, bool MustExist) ResolveEntry(string pluginDirectory, PluginManifest manifest)
    {
        var command = manifest.Entry?.Command;
        if (string.IsNullOrWhiteSpace(command)) return (null, true);

        if (PluginEntryCommand.IsHostCommand(pluginDirectory, command))
        {
            // dotnet/node/python 等宿主命令：真正的入口是 arguments 里的第一个文件。
            var firstArgument = manifest.Entry?.Arguments.FirstOrDefault(argument =>
                argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".py", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(firstArgument)
                ? (command, MustExist: false)
                : (Path.GetFullPath(Path.Combine(pluginDirectory, firstArgument)), MustExist: true);
        }

        return (Path.GetFullPath(Path.Combine(pluginDirectory, command)), MustExist: true);
    }
}
