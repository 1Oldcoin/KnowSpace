using System.Text;
using Microsoft.AspNetCore.Http;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Persistence;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// <see cref="IPluginContentStore"/> 的唯一实现：把平台条目包装成一次普通 Markdown 上传，
/// 因此解析、分块、内容版本、Outbox 与索引生命周期全部复用既有链路。
///
/// <para>
/// 关键点：<b>文件名由 externalId 决定而不是内容哈希决定</b>。
/// 同一平台条目更新时文件名不变，资料 ID 与存储路径语义保持一致，
/// 便于排查"这篇内容到底是哪来的"。
/// </para>
/// </summary>
public sealed class KnowledgePluginContentStore(
    KnowledgeIngestionService ingestion,
    KnowledgeBaseStore knowledgeBaseStore,
    LocalDocumentStore documentStore,
    ILogger<KnowledgePluginContentStore> logger) : IPluginContentStore
{
    public async Task<bool> KnowledgeBaseExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
        await knowledgeBaseStore.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<Guid?> CreateAsync(
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var upload = BuildUpload(item);
            var response = await ingestion
                .UploadAsync(upload.File, knowledgeBaseId, cancellationToken)
                .ConfigureAwait(false);

            return Guid.TryParse(response.Id, out var id) ? id : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to ingest plugin item {Source}/{ExternalId}", item.Source, item.ExternalId);
            return null;
        }
    }

    public async Task<bool> ReplaceAsync(
        Guid documentId,
        PlatformIngestionItem item,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        using var upload = BuildUpload(item);
        return await ingestion
            .ReplaceAsync(documentId, upload.File, knowledgeBaseId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var storagePath = await knowledgeBaseStore.DeleteDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);

        // DeleteDocumentAsync 返回 null 有两种情况：资料不存在，或事务回滚。
        // 前者对撤回是幂等的成功，后者不该把源文件删掉。
        if (storagePath is null)
            return await knowledgeBaseStore.GetDocumentKnowledgeBaseIdAsync(documentId, cancellationToken)
                .ConfigureAwait(false) is null;

        documentStore.DeleteStoredFile(storagePath);
        return true;
    }

    /// <summary>把条目包装成一次普通上传；<c>.md</c> 走既有解析链路。</summary>
    private static PluginUpload BuildUpload(PlatformIngestionItem item)
    {
        var content = ComposeContent(item);
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content), writable: false);

        var file = new FormFile(stream, 0, stream.Length, "file", BuildFileName(item))
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/markdown; charset=utf-8"
        };

        return new PluginUpload(file, stream);
    }

    /// <summary>标题进正文头部，元数据与来源附在末尾，便于人工核对。</summary>
    internal static string ComposeContent(PlatformIngestionItem item)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            builder.Append("# ").AppendLine(item.Title.Trim());
            builder.AppendLine();
        }

        builder.AppendLine(item.Content.Trim());

        if (item.Metadata is { Count: > 0 })
        {
            builder.AppendLine();
            builder.AppendLine("---");
            foreach (var (key, value) in item.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                builder.AppendLine($"{key}: {value}");
        }

        builder.AppendLine();
        builder.AppendLine($"来源: {item.Source} / {item.ExternalId}");
        if (item.UpdatedAt is { } updated)
            builder.AppendLine($"更新时间: {updated:O}");

        return builder.ToString();
    }

    internal static string BuildFileName(PlatformIngestionItem item)
    {
        var baseName = string.IsNullOrWhiteSpace(item.Title) ? item.ExternalId : item.Title;
        var safe = new StringBuilder(baseName.Length);
        foreach (var character in baseName)
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_' or ' ') safe.Append(character);
        }

        var trimmed = safe.ToString().Trim();
        if (trimmed.Length == 0) trimmed = "plugin-item";
        if (trimmed.Length > 80) trimmed = trimmed[..80];

        // 稳定前缀来自 externalId，而不是内容：同一条目更新后文件名不变。
        var hash = PlatformIngestionItem.ComputeContentHash(item.ExternalId);
        return $"{trimmed}-{hash[..8]}.md";
    }

    /// <summary>上传句柄：<see cref="FormFile"/> 本身不持有资源，底层流由这里释放。</summary>
    private sealed class PluginUpload(FormFile file, MemoryStream stream) : IDisposable
    {
        public FormFile File => file;

        public void Dispose() => stream.Dispose();
    }
}
