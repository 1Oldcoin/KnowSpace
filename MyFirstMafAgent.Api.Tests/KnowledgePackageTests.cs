using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyFirstMafAgent.Api.Application.KnowledgePackages;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 知识库包的读取与校验。
/// 这里不碰数据库：导入流程要写库，但“这个包能不能信”是纯规则，必须先有把握。
/// </summary>
public sealed class KnowledgePackageTests
{
    private static ZipArchive BuildArchive(string? manifestJson, string? extraFileName = null, string? extraContent = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (manifestJson is not null)
            {
                var entry = archive.CreateEntry(KnowledgePackageExporter.ManifestEntryName);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(manifestJson);
            }

            if (extraFileName is not null)
            {
                var entry = archive.CreateEntry(extraFileName, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(extraContent ?? string.Empty);
            }
        }

        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static string Serialize(KnowledgePackageManifest manifest) =>
        JsonSerializer.Serialize(manifest, KnowledgePackageManifest.JsonOptions);

    [Fact]
    public async Task Reader_accepts_minimal_package()
    {
        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            KnowledgeBase = new KnowledgePackageSource { Name = "我的库", CreatedAt = DateTimeOffset.UtcNow }
        };

        using var archive = BuildArchive(Serialize(manifest));
        var read = await KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None);

        Assert.Equal("我的库", read.KnowledgeBase.Name);
        Assert.Equal(KnowledgePackageManifest.CurrentVersion, read.FormatVersion);
    }

    [Fact]
    public async Task Reader_ignores_unknown_fields_for_forward_compatibility()
    {
        // 未来版本加的字段不应该让旧版本直接拒绝整包。
        var json = """
        {
          "format": "zhijian-knowledge-package",
          "formatVersion": 1,
          "exportedAt": "2026-02-01T00:00:00+00:00",
          "knowledgeBase": { "name": "旧版能读", "createdAt": "2026-02-01T00:00:00+00:00", "futureField": 42 },
          "documents": [],
          "notes": [],
          "topics": [],
          "somethingNew": { "a": 1 }
        }
        """;

        using var archive = BuildArchive(json);
        var read = await KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None);

        Assert.Equal("旧版能读", read.KnowledgeBase.Name);
    }

    [Fact]
    public async Task Reader_rejects_missing_manifest()
    {
        using var archive = BuildArchive(manifestJson: null, "files/0000.md", "内容");

        var exception = await Assert.ThrowsAsync<KnowledgePackageException>(
            () => KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None));
        Assert.Contains("manifest.json", exception.Message);
    }

    [Fact]
    public async Task Reader_rejects_foreign_format()
    {
        var json = """{ "format": "other-app-package", "formatVersion": 1, "knowledgeBase": { "name": "x" } }""";

        using var archive = BuildArchive(json);
        var exception = await Assert.ThrowsAsync<KnowledgePackageException>(
            () => KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None));
        Assert.Contains("不是本应用", exception.Message);
    }

    [Fact]
    public async Task Reader_rejects_newer_format_version()
    {
        var json = """{ "format": "zhijian-knowledge-package", "formatVersion": 99, "knowledgeBase": { "name": "x" } }""";

        using var archive = BuildArchive(json);
        var exception = await Assert.ThrowsAsync<KnowledgePackageException>(
            () => KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None));
        Assert.Contains("99", exception.Message);
    }

    [Fact]
    public async Task Reader_rejects_broken_json()
    {
        using var archive = BuildArchive("{ not json");

        await Assert.ThrowsAsync<KnowledgePackageException>(
            () => KnowledgePackageReader.ReadManifestAsync(archive, CancellationToken.None));
    }

    [Fact]
    public async Task Entry_hash_matches_file_bytes()
    {
        const string content = "插件协议要点：一个插件只能声明一次工具。";
        using var archive = BuildArchive(null, "files/0000.md", content);
        var entry = archive.GetEntry("files/0000.md")!;

        var hash = await KnowledgePackageReader.ComputeEntryHashAsync(entry, CancellationToken.None);
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

        Assert.Equal(expected, hash);
    }

    [Fact]
    public void Entry_path_is_normalized_across_zip_producers()
    {
        // Windows 自带压缩写的条目名是反斜杠；清单里写的是正斜杠。两者必须能对上。
        Assert.Equal("files/0001.md", KnowledgePackageReader.NormalizeEntryPath("files\\0001.md"));
        Assert.Equal("files/0001.md", KnowledgePackageReader.NormalizeEntryPath("./files/0001.md"));
        Assert.Equal("files/0001.md", KnowledgePackageReader.NormalizeEntryPath("/files/0001.md"));
        Assert.Equal("files/0001.md", KnowledgePackageReader.NormalizeEntryPath("  files/0001.md  "));
        Assert.Equal(string.Empty, KnowledgePackageReader.NormalizeEntryPath(null));
        Assert.Equal(string.Empty, KnowledgePackageReader.NormalizeEntryPath("   "));
    }

    [Fact]
    public void Entry_index_finds_backslash_entries_by_forward_slash_path()
    {
        using var archive = BuildArchive("{\"format\":\"zhijian-knowledge-package\",\"formatVersion\":1}", "files\\0001.md", "content");

        var index = KnowledgePackageReader.BuildEntryIndex(archive);

        Assert.True(index.ContainsKey("files/0001.md"));
    }

    [Fact]
    public void Resolve_name_keeps_original_when_free()
    {
        Assert.Equal("知识库", KnowledgePackageReader.ResolveKnowledgeBaseName("知识库", ["另一个库"]));
    }

    [Fact]
    public void Resolve_name_appends_suffix_when_taken()
    {
        Assert.Equal("知识库（导入）", KnowledgePackageReader.ResolveKnowledgeBaseName("知识库", ["知识库"]));
        Assert.Equal("知识库（导入 2）",
            KnowledgePackageReader.ResolveKnowledgeBaseName("知识库", ["知识库", "知识库（导入）"]));
        Assert.Equal("知识库（导入 3）",
            KnowledgePackageReader.ResolveKnowledgeBaseName("知识库", ["知识库", "知识库（导入）", "知识库（导入 2）"]));
    }

    [Fact]
    public void Resolve_name_is_case_insensitive_and_handles_blank()
    {
        Assert.Equal("plugin（导入）", KnowledgePackageReader.ResolveKnowledgeBaseName("plugin", ["Plugin"]));
        Assert.Equal("导入的知识库", KnowledgePackageReader.ResolveKnowledgeBaseName("   ", []));
    }

    [Fact]
    public void Manifest_serializes_camel_case_and_skips_nulls()
    {
        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            KnowledgeBase = new KnowledgePackageSource { Name = "库" },
            Notes =
            [
                new KnowledgePackageNote { Title = "笔记", Sources = [new KnowledgePackageNoteSource { FileName = "a.md" }] }
            ]
        };

        var json = Serialize(manifest);

        Assert.Contains("\"formatVersion\"", json);
        Assert.Contains("\"knowledgeBase\"", json);
        Assert.DoesNotContain("\"appVersion\"", json);
        // 中文不转义，方便用户直接打开 manifest.json 看清包里有什么。
        Assert.Contains("笔记", json);
    }
}
