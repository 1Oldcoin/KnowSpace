using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Application.KnowledgePackages;

/// <summary>导出结果：已经定位到开头的临时文件流 + 建议文件名 + 数量摘要。</summary>
public sealed record KnowledgePackageExport(
    Stream Content,
    string FileName,
    int Documents,
    int Notes,
    int Topics,
    IReadOnlyList<string> Warnings) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// 把知识库打包成可移植的 zip：<c>manifest.json</c> + <c>files/*</c>。
///
/// <para>
/// 不导出切片与向量：切片是解析产物、向量绑定具体模型版本，导出它们只会让包变大并且在别人机器上失效。
/// 导入端重新解析即可，代价是导入后要等索引建好。
/// </para>
/// </summary>
public sealed class KnowledgePackageExporter(
    IKnowledgePackageStore packageStore,
    INoteRepository noteRepository,
    ITopicRepository topicRepository)
{
    public const string ManifestEntryName = "manifest.json";
    public const string FilesFolderName = "files";

    public async Task<KnowledgePackageExport> ExportAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var knowledgeBase = await packageStore.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken)
            ?? throw new KnowledgeBaseNotFoundException(knowledgeBaseId);

        var warnings = new List<string>();
        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            AppVersion = typeof(KnowledgePackageExporter).Assembly.GetName().Version?.ToString(),
            KnowledgeBase = new KnowledgePackageSource { Name = knowledgeBase.Name, CreatedAt = knowledgeBase.CreatedAt }
        };

        var topicNames = new Dictionary<Guid, string>();
        foreach (var topic in await topicRepository.ListAsync(knowledgeBaseId, cancellationToken))
        {
            manifest.Topics.Add(new KnowledgePackageTopic { Id = topic.Id, Name = topic.Name, Color = topic.Color });
            topicNames[topic.Id] = topic.Name;
        }

        // 临时文件 + DeleteOnClose：包可能几百 MB，不适合整体放进内存。
        var tempPath = Path.Combine(Path.GetTempPath(), $"zjpack-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var documents = await packageStore.ListDocumentsAsync(knowledgeBaseId, cancellationToken);
                var order = 0;
                foreach (var document in documents)
                {
                    var storagePath = document.StoragePath;
                    if (string.IsNullOrWhiteSpace(storagePath) || !File.Exists(storagePath))
                    {
                        warnings.Add($"跳过《{document.FileName}》：原始文件已不在磁盘上。");
                        continue;
                    }

                    order++;
                    var entryPath = $"{FilesFolderName}/{order:0000}{document.Extension}";
                    var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
                    string sha256;
                    await using (var fileStream = new FileStream(storagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        bufferSize: 81920, useAsync: true))
                    await using (var entryStream = entry.Open())
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await fileStream.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await entryStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        }

                        sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    }

                    manifest.Documents.Add(new KnowledgePackageDocument
                    {
                        Id = document.Id,
                        FileName = document.FileName,
                        Extension = document.Extension,
                        ContentType = document.ContentType,
                        SizeBytes = document.SizeBytes,
                        TopicId = document.TopicId,
                        Summary = document.Summary,
                        Sha256 = sha256,
                        File = entryPath
                    });

                    if (document.TopicId is { } topicId && !topicNames.ContainsKey(topicId))
                        warnings.Add($"《{document.FileName}》的专题已不存在，导入后将归为未分类。");
                }

                var notes = await noteRepository.ListAsync(knowledgeBaseId, cancellationToken);
                foreach (var note in notes)
                {
                    manifest.Notes.Add(new KnowledgePackageNote
                    {
                        Id = note.Id,
                        Title = note.Title,
                        Content = note.Content,
                        Category = note.Category,
                        Color = note.Color,
                        Tags = note.Tags.ToList(),
                        IsPinned = note.IsPinned,
                        SourceDocumentId = note.SourceDocumentId,
                        TopicId = note.TopicId,
                        Sources = note.Sources.Select(source => new KnowledgePackageNoteSource
                        {
                            DocumentId = source.DocumentId,
                            FileName = source.FileName,
                            ChunkIndex = source.ChunkIndex,
                            Similarity = source.Similarity,
                            Snippet = source.Snippet
                        }).ToList(),
                        CreatedAt = note.CreatedAt,
                        UpdatedAt = note.UpdatedAt
                    });
                }

                manifest.Warnings = warnings;
                var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest,
                    KnowledgePackageManifest.JsonOptions, cancellationToken);
            }

            stream.Position = 0;
            var safeName = SanitizeFileName(knowledgeBase.Name);
            return new KnowledgePackageExport(
                stream,
                $"{safeName}-{DateTimeOffset.Now:yyyyMMdd}.zjpack.zip",
                manifest.Documents.Count,
                manifest.Notes.Count,
                manifest.Topics.Count,
                warnings);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) return "knowledge-base";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
