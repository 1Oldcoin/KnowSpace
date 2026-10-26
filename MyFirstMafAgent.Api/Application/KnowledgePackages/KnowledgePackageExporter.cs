using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Topics;

namespace MyFirstMafAgent.Api.Application.KnowledgePackages;

/// <summary>导出计划：数据已经读好，只等往目标流里写字节。</summary>
/// <remarks>
/// 分成「准备」和「写入」两步，是因为 HTTP 响应头（文件名、条数）必须在响应体开始之前发出：
/// 先把元数据读齐、算好计数与告警，再一口气把 zip 流写进响应。
/// </remarks>
public sealed record KnowledgePackagePlan(
    string KnowledgeBaseName,
    DateTimeOffset KnowledgeBaseCreatedAt,
    IReadOnlyList<Topic> Topics,
    IReadOnlyList<KnowledgePackageDocumentInfo> Documents,
    IReadOnlyList<Note> Notes,
    IReadOnlyList<string> Warnings)
{
    public string FileName =>
        $"{KnowledgePackageExporter.SanitizeFileName(KnowledgeBaseName)}-{DateTimeOffset.Now:yyyyMMdd}.zjpack.zip";
}

/// <summary>写完之后才知道的东西（这里是逐份资料的 SHA-256 校验，不影响响应头）。</summary>
public sealed record KnowledgePackageExportResult(int Documents, int Notes, int Topics, IReadOnlyList<string> Warnings);

/// <summary>
/// 把知识库打包成可移植的 zip：<c>manifest.json</c> + <c>files/*</c>。
///
/// <para>
/// 不导出切片与向量：切片是解析产物、向量绑定具体模型版本，导出它们只会让包变大并且在别人机器上失效。
/// 导入端重新解析即可，代价是导入后要等索引建好。
/// </para>
///
/// <para>
/// <b>不落临时文件</b>：包动辄几百 MB，先写 <c>%TEMP%</c> 再回传等于把同一份数据写两遍磁盘，
/// 还会被火绒/360 的文件防护或「受控文件夹访问」拦下来（现场就是
/// <c>UnauthorizedAccessException: Access to the path '…\Temp\zjpack-….zip' is denied.</c>）。
/// 现在直接把 zip 流写进调用方给的 <see cref="Stream"/>——HTTP 场景下就是响应体，
/// 客户端边收边落盘，服务端零残留。
/// </para>
/// </summary>
public sealed class KnowledgePackageExporter(
    IKnowledgePackageStore packageStore,
    INoteRepository noteRepository,
    ITopicRepository topicRepository)
{
    public const string ManifestEntryName = "manifest.json";
    public const string FilesFolderName = "files";

    /// <summary>读齐导出所需的数据，并算好条数与告警（此时还没有写任何字节）。</summary>
    public async Task<KnowledgePackagePlan> PrepareAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        var knowledgeBase = await packageStore.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken)
            ?? throw new KnowledgeBaseNotFoundException(knowledgeBaseId);

        var topics = await topicRepository.ListAsync(knowledgeBaseId, cancellationToken);
        var topicNames = topics.Select(topic => topic.Id).ToHashSet();

        var warnings = new List<string>();
        var documents = new List<KnowledgePackageDocumentInfo>();
        foreach (var document in await packageStore.ListDocumentsAsync(knowledgeBaseId, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(document.StoragePath) || !File.Exists(document.StoragePath))
            {
                warnings.Add($"跳过《{document.FileName}》：原始文件已不在磁盘上。");
                continue;
            }

            documents.Add(document);

            if (document.TopicId is { } topicId && !topicNames.Contains(topicId))
                warnings.Add($"《{document.FileName}》的专题已不存在，导入后将归为未分类。");
        }

        var notes = await noteRepository.ListAsync(knowledgeBaseId, cancellationToken);
        return new KnowledgePackagePlan(knowledgeBase.Name, knowledgeBase.CreatedAt, topics, documents, notes, warnings);
    }

    /// <summary>把计划写成 zip 流。<paramref name="destination"/> 不要求可寻址，HTTP 响应体可以直接传进来。</summary>
    public async Task<KnowledgePackageExportResult> WriteAsync(
        KnowledgePackagePlan plan,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            AppVersion = typeof(KnowledgePackageExporter).Assembly.GetName().Version?.ToString(),
            KnowledgeBase = new KnowledgePackageSource
            {
                Name = plan.KnowledgeBaseName,
                CreatedAt = plan.KnowledgeBaseCreatedAt
            }
        };

        foreach (var topic in plan.Topics)
            manifest.Topics.Add(new KnowledgePackageTopic { Id = topic.Id, Name = topic.Name, Color = topic.Color });

        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            var order = 0;
            foreach (var document in plan.Documents)
            {
                order++;
                var entryPath = $"{FilesFolderName}/{order:0000}{document.Extension}";
                var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
                string sha256;
                await using (var fileStream = new FileStream(document.StoragePath!, FileMode.Open, FileAccess.Read,
                    FileShare.Read, bufferSize: 81920, useAsync: true))
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
            }

            foreach (var note in plan.Notes)
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

            manifest.Warnings = plan.Warnings.ToList();
            var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest,
                KnowledgePackageManifest.JsonOptions, cancellationToken);
        }

        return new KnowledgePackageExportResult(
            manifest.Documents.Count,
            manifest.Notes.Count,
            manifest.Topics.Count,
            plan.Warnings);
    }

    internal static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) return "knowledge-base";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}
