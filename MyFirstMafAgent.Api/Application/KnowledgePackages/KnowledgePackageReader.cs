using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyFirstMafAgent.Api.Application.KnowledgePackages;

/// <summary>
/// 包的读取与校验。抽出来是为了能单测——导入流程本身要写数据库，这些规则却不需要。
///
/// <para>
/// 校验顺序刻意从“这是不是我们的包”开始：先看格式标识，再看版本，最后才是内容。
/// 否则用户拿到一个格式版本更高的包，只会看到一堆字段缺失的莫名其妙报错。
/// </para>
/// </summary>
public static class KnowledgePackageReader
{
    /// <summary>读取并校验 <c>manifest.json</c>；任何不符合预期的内容都以 <see cref="KnowledgePackageException"/> 抛出。</summary>
    public static async Task<KnowledgePackageManifest> ReadManifestAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(KnowledgePackageExporter.ManifestEntryName)
            ?? throw new KnowledgePackageException("导入包缺少 manifest.json，可能不是知识库包。");

        KnowledgePackageManifest? manifest;
        try
        {
            await using var stream = entry.Open();
            manifest = await JsonSerializer.DeserializeAsync<KnowledgePackageManifest>(
                stream, KnowledgePackageManifest.JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new KnowledgePackageException($"manifest.json 无法解析：{exception.Message}");
        }

        if (manifest is null) throw new KnowledgePackageException("manifest.json 为空。");
        if (!string.Equals(manifest.Format, KnowledgePackageManifest.FormatName, StringComparison.Ordinal))
            throw new KnowledgePackageException($"不是本应用的知识库包（format={manifest.Format}）。");
        if (manifest.FormatVersion <= 0 || manifest.FormatVersion > KnowledgePackageManifest.CurrentVersion)
            throw new KnowledgePackageException(
                $"包格式版本 {manifest.FormatVersion} 高于当前支持的 {KnowledgePackageManifest.CurrentVersion}，请升级应用后再导入。");

        return manifest;
    }

    /// <summary>只读一遍条目算 SHA-256，不落盘、不进内存。</summary>
    public static async Task<string> ComputeEntryHashAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 统一 zip 条目路径：反斜杠转正斜杠、去掉 `./` 与前导斜杠。
    ///
    /// <para>
    /// `ZipArchive.GetEntry` 是精确字符串匹配。用 Windows 自带的压缩功能、或者
    /// 某些第三方打包器产出的 zip 里条目名是 `files\0001.md`，而清单里写的是 `files/0001.md`——
    /// 不归一化就会一条都匹配不上，最后“导入成功”但一份资料也没进来。这个坑真实踩过。
    /// </para>
    /// </summary>
    public static string NormalizeEntryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    /// <summary>按归一化后的路径建立查找表，供清单里的 <c>file</c> 字段定位。</summary>
    public static Dictionary<string, ZipArchiveEntry> BuildEntryIndex(ZipArchive archive)
    {
        var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var key = NormalizeEntryPath(entry.FullName);
            if (key.Length > 0) index[key] = entry;
        }

        return index;
    }

    /// <summary>
    /// 导入永远建新知识库，因此同名时要自动让路：<c>名称</c> → <c>名称（导入）</c> → <c>名称（导入 2）</c>…
    /// 不让用户为了导入先改名或删库。
    /// </summary>
    public static string ResolveKnowledgeBaseName(string requested, IReadOnlyCollection<string> existingNames)
    {
        var baseName = string.IsNullOrWhiteSpace(requested) ? "导入的知识库" : requested.Trim();
        if (baseName.Length > 90) baseName = baseName[..90];

        var existing = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseName)) return baseName;

        var withSuffix = $"{baseName}（导入）";
        if (!existing.Contains(withSuffix)) return withSuffix;

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{baseName}（导入 {index}）";
            if (!existing.Contains(candidate)) return candidate;
        }

        return $"{baseName}（导入 {Guid.NewGuid():N}）";
    }
}
