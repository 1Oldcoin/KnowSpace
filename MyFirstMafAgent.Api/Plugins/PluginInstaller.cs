using System.IO.Compression;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>安装结果。失败时 <see cref="Errors"/> 说明原因，且不会留下半安装状态。</summary>
public sealed record PluginInstallResult(
    bool Success,
    string? PluginId,
    string? Version,
    string? PackageDigest,
    PluginIntegrityReport? Integrity,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public static PluginInstallResult Fail(params string[] errors) =>
        new(false, null, null, null, null, errors, []);
}

/// <summary>回滚结果。</summary>
public sealed record PluginRollbackResult(
    bool Success,
    string? PluginId,
    string? FromVersion,
    string? ToVersion,
    IReadOnlyList<string> Errors)
{
    public static PluginRollbackResult Fail(IReadOnlyList<string> errors) => new(false, null, null, null, errors);
}

/// <summary>
/// 已解包并校验过的安装包。释放时删除暂存目录——调用方必须 <c>using</c>。
/// </summary>
public sealed class PluginStagedPackage(
    string stagingRoot,
    string packageDirectory,
    string? pluginId,
    string? version,
    PluginManifest? manifest,
    IReadOnlyList<string> errors) : IDisposable
{
    public string StagingRoot { get; } = stagingRoot;

    /// <summary>包根目录（含 manifest.json），可直接交给 <see cref="PluginInstaller.InstallFromDirectoryAsync"/>。</summary>
    public string PackageDirectory { get; } = packageDirectory;

    public string? PluginId { get; } = pluginId;

    public string? Version { get; } = version;

    public PluginManifest? Manifest { get; } = manifest;

    public IReadOnlyList<string> Errors { get; } = errors;

    public bool Success => Errors.Count == 0 && PluginId is not null;

    public static PluginStagedPackage Fail(string stagingRoot, params string[] errors) =>
        new(stagingRoot, string.Empty, null, null, null, errors);

    public static PluginStagedPackage Fail(string stagingRoot, IReadOnlyList<string> errors) =>
        new(stagingRoot, string.Empty, null, null, null, errors);

    public void Dispose()
    {
        if (Directory.Exists(StagingRoot))
        {
            try { Directory.Delete(StagingRoot, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 暂存目录删不掉不影响安装结果，下次启动会清理。
            }
        }
    }
}

/// <summary>
/// 插件包的安装、激活、回滚与卸载。只操作文件系统，不启动进程——
/// 启停由 <see cref="PluginManager"/> 在文件布局变更后统一处理。
///
/// <para>
/// 布局见 <see cref="PluginPaths"/>：每个版本一个完整目录，<c>active.json</c> 指向生效版本，
/// 因此升级与回滚都是"换一个指针 + 重启进程"，不需要原地覆盖文件（Windows 上运行中的 exe 也删不掉）。
/// </para>
/// </summary>
public sealed class PluginInstaller(
    IOptions<PluginOptions> options,
    PluginPaths paths,
    PluginManifestLoader loader,
    PluginIntegrityVerifier verifier,
    ILogger<PluginInstaller> logger)
{
    /// <summary>
    /// 把插件包解到暂存目录并做**只读**校验，不改动任何生效目录。
    ///
    /// <para>
    /// 分两段的意义：升级一个正在运行的插件时必须先确认包是合法的新版本，再排空并停掉旧进程。
    /// 如果先停进程再发现包是坏的，用户就平白丢了一次可用服务。
    /// </para>
    /// </summary>
    public async Task<PluginStagedPackage> StageAsync(Stream package, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.StagingRoot);
        var staging = Path.Combine(paths.StagingRoot, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            var zipPath = Path.Combine(staging, "package.zip");
            var copied = await CopyWithLimitAsync(package, zipPath, options.Value.MaxPackageBytes, cancellationToken)
                .ConfigureAwait(false);

            if (copied < 0)
                return PluginStagedPackage.Fail(staging, $"插件包超过大小上限 {options.Value.MaxPackageBytes} 字节");

            var extractRoot = Path.Combine(staging, "package");
            Directory.CreateDirectory(extractRoot);

            try
            {
                ExtractZip(zipPath, extractRoot);
            }
            catch (InvalidDataException exception)
            {
                return PluginStagedPackage.Fail(staging, $"插件包不是有效的 ZIP：{exception.Message}");
            }
            catch (PluginPackageException exception)
            {
                return PluginStagedPackage.Fail(staging, exception.Message);
            }

            var packageRoot = LocatePackageRoot(extractRoot);
            if (packageRoot is null)
                return PluginStagedPackage.Fail(
                    staging,
                    $"插件包内找不到清单文件（{string.Join(" / ", PluginPaths.ManifestFileNames)}）");

            var load = loader.Load(packageRoot);
            if (load.Manifest is null || !load.Validation.IsValid)
                return PluginStagedPackage.Fail(staging, [.. load.Validation.Errors]);

            if (!load.EntryExists)
                return PluginStagedPackage.Fail(staging, $"入口文件不存在：{load.Manifest.Entry?.Command}");

            return new PluginStagedPackage(
                staging,
                packageRoot,
                load.Manifest.Id,
                load.Manifest.Version,
                load.Manifest,
                []);
        }
        catch
        {
            PluginPaths.TryDeleteDirectory(staging, logger);
            throw;
        }
    }

    /// <summary>从 ZIP 包安装。包内可以带一层包裹目录，也可以把 manifest.json 放在根上。</summary>
    public async Task<PluginInstallResult> InstallAsync(Stream package, CancellationToken cancellationToken)
    {
        var staged = await StageAsync(package, cancellationToken).ConfigureAwait(false);
        using (staged)
        {
            if (!staged.Success)
                return PluginInstallResult.Fail([.. staged.Errors]);

            return await InstallFromDirectoryAsync(staged.PackageDirectory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 从已解包的目录安装（开发期直接把 build 输出拷过来也走这里）。
    /// 会校验 manifest、入口、完整性，然后落到 <c>versions/{version}</c> 并切换 active 指针。
    /// </summary>
    public async Task<PluginInstallResult> InstallFromDirectoryAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var load = loader.Load(packageDirectory);
        if (load.Manifest is null || !load.Validation.IsValid)
            return PluginInstallResult.Fail([.. load.Validation.Errors]);

        var manifest = load.Manifest;
        if (!load.EntryExists)
            return PluginInstallResult.Fail($"入口文件不存在：{manifest.Entry?.Command}");

        var integrity = await verifier
            .VerifyAsync(packageDirectory, manifest, cancellationToken)
            .ConfigureAwait(false);

        if (!integrity.IsAcceptable)
            return new PluginInstallResult(false, manifest.Id, manifest.Version, integrity.PackageDigest, integrity, integrity.Errors, integrity.Warnings);

        var target = paths.VersionDirectory(manifest.Id, manifest.Version);
        var pluginRoot = paths.PluginRoot(manifest.Id);
        Directory.CreateDirectory(paths.VersionsRoot(manifest.Id));

        // 覆盖安装同版本时先挪走旧目录，避免部分文件残留。
        // 刚停掉的子进程在 Windows 上可能还占着文件一小会儿（句柄是异步释放的），
        // 所以这里重试几次；仍然挪不动就退化成原地覆盖——宁可留下多余文件，
        // 也不要让一次版本切换永远失败（比如从 v1.0.1 装回 v1.0.0）。
        if (Directory.Exists(target))
        {
            var trash = Path.Combine(paths.StagingRoot, "trash-" + Guid.NewGuid().ToString("N"));
            if (TryMoveDirectory(target, trash))
            {
                PluginPaths.TryDeleteDirectory(trash, logger);
            }
            else
            {
                logger.LogWarning(
                    "Plugin {PluginId} v{Version} directory could not be moved away; overwriting in place (stale files may remain)",
                    manifest.Id,
                    manifest.Version);
            }
        }

        try
        {
            CopyDirectoryWithRetry(packageDirectory, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PluginPaths.TryDeleteDirectory(target, logger);
            return PluginInstallResult.Fail($"复制插件文件失败：{exception.Message}");
        }

        await ActivateAsync(manifest.Id, manifest.Version, cancellationToken).ConfigureAwait(false);
        PruneVersions(manifest.Id, manifest.Version);

        logger.LogInformation(
            "Plugin {PluginId} v{Version} installed at {Directory}",
            manifest.Id,
            manifest.Version,
            pluginRoot);

        return new PluginInstallResult(
            true,
            manifest.Id,
            manifest.Version,
            integrity.PackageDigest,
            integrity,
            [],
            integrity.Warnings);
    }

    /// <summary>切换生效版本，并把原生效版本记为 previous（用于回滚）。</summary>
    public Task ActivateAsync(string pluginId, string version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pluginRoot = paths.PluginRoot(pluginId);
        var activeFile = paths.ActiveFile(pluginId);
        var current = PluginManifestLoader.ReadActiveVersion(pluginRoot);
        var previous = current is not null && !string.Equals(current, version, StringComparison.Ordinal)
            ? current
            : ReadPrevious(pluginId);

        var node = new JsonObject
        {
            ["version"] = version,
            ["previous"] = previous,
            ["activatedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };

        Directory.CreateDirectory(pluginRoot);
        File.WriteAllText(activeFile, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return Task.CompletedTask;
    }

    /// <summary>回滚到上一个生效版本。没有历史版本时返回失败而不破坏当前状态。</summary>
    public Task<PluginRollbackResult> RollbackAsync(string pluginId, CancellationToken cancellationToken)
    {
        var active = PluginManifestLoader.ReadActiveVersion(paths.PluginRoot(pluginId));
        var previous = ReadPrevious(pluginId);

        if (string.IsNullOrWhiteSpace(previous))
            return Task.FromResult(PluginRollbackResult.Fail(["没有可回滚的历史版本"]));

        if (!Directory.Exists(paths.VersionDirectory(pluginId, previous)))
            return Task.FromResult(PluginRollbackResult.Fail([$"历史版本 {previous} 的目录已不存在"]));

        // 反向写指针：回滚本身也可以再回滚。
        var node = new JsonObject
        {
            ["version"] = previous,
            ["previous"] = active,
            ["activatedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };

        Directory.CreateDirectory(paths.PluginRoot(pluginId));
        File.WriteAllText(
            paths.ActiveFile(pluginId),
            node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        logger.LogInformation("Plugin {PluginId} rolled back from {From} to {To}", pluginId, active, previous);

        return Task.FromResult(new PluginRollbackResult(true, pluginId, active, previous, []));
    }

    /// <summary>卸载：删除全部版本、active 指针与插件数据目录。</summary>
    public Task UninstallAsync(string pluginId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        PluginPaths.TryDeleteDirectory(paths.PluginRoot(pluginId), logger);
        PluginPaths.TryDeleteDirectory(paths.DataDirectory(pluginId), logger);
        return Task.CompletedTask;
    }

    /// <summary>读取 active.json 里的 previous 版本。</summary>
    public string? ReadPrevious(string pluginId)
    {
        var activeFile = paths.ActiveFile(pluginId);
        if (!File.Exists(activeFile)) return null;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(activeFile));
            return node?["previous"]?.GetValue<string>();
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>只保留最近 N 个版本（含当前生效版本）。</summary>
    private void PruneVersions(string pluginId, string activeVersion)
    {
        var keep = Math.Max(1, options.Value.KeepVersions);
        var versions = PluginManifestLoader.GetInstalledVersions(paths.PluginRoot(pluginId));

        var removable = versions
            .Where(version => !string.Equals(version, activeVersion, StringComparison.Ordinal))
            .Skip(keep - 1);

        foreach (var version in removable)
        {
            var directory = paths.VersionDirectory(pluginId, version);
            PluginPaths.TryDeleteDirectory(directory, logger);
            logger.LogInformation("Pruned old plugin version {PluginId} v{Version}", pluginId, version);
        }
    }

    /// <summary>
    /// 把目录挪到暂存区，容忍 Windows 上"进程刚退出、句柄还没释放"的短暂占用。
    /// 共 4 次尝试（0/120/240/480 毫秒），仍失败才返回 false。
    /// </summary>
    private static bool TryMoveDirectory(string source, string destination)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) return false;
                Thread.Sleep(120 * (1 << attempt));
            }
        }

        return false;
    }

    /// <summary>
    /// 复制插件文件，容忍"上一次的子进程刚被杀掉、句柄还没释放"造成的占用。
    /// 共 3 次尝试（0/300/900 毫秒）。
    /// </summary>
    private static void CopyDirectoryWithRetry(string source, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                CopyDirectory(source, destination);
                return;
            }
            catch (Exception exception) when (attempt < 2 && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300 * (1 << attempt));
            }
        }
    }

    /// <summary>找到包含清单文件的目录（manifest.json / plugin.json / skill.json）；包内允许有一层包裹目录。</summary>
    private static string? LocatePackageRoot(string extractRoot)
    {
        var current = extractRoot;

        for (var depth = 0; depth < 5; depth++)
        {
            if (PluginPaths.HasManifest(current)) return current;

            var children = Directory.EnumerateDirectories(current)
                .Where(directory =>
                {
                    var name = Path.GetFileName(directory);
                    return !name.StartsWith('.') && !name.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (children.Count != 1) return null;
            current = children[0];
        }

        return null;
    }

    /// <summary>解压并拒绝路径穿越与绝对路径。</summary>
    private static void ExtractZip(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目

            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment == ".."))
                throw new PluginPackageException($"插件包包含非法路径：{entry.FullName}");

            if (Path.IsPathRooted(relative) || relative.StartsWith('/'))
                throw new PluginPackageException($"插件包包含绝对路径：{entry.FullName}");

            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new PluginPackageException($"插件包条目逃逸出安装目录：{entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>把流写入文件并限制大小；超限返回 -1。</summary>
    private static async Task<long> CopyWithLimitAsync(
        Stream source,
        string destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, maxBytes);

        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit) return -1;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.') || name.Equals("versions", StringComparison.OrdinalIgnoreCase)) continue;
            CopyDirectory(directory, Path.Combine(destination, name));
        }

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(PluginPaths.ActiveFileName, StringComparison.Ordinal)) continue;
            File.Copy(file, Path.Combine(destination, name), overwrite: true);
        }
    }
}

/// <summary>插件包内容违反约束（路径穿越、绝对路径等）。</summary>
public sealed class PluginPackageException(string message) : Exception(message);
