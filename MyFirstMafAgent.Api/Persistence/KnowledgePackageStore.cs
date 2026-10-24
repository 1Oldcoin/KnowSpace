using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// <see cref="IKnowledgePackageStore"/> 的持久化实现：把 <see cref="KnowledgeBaseStore"/>
/// 的宽接口收窄成知识库包真正需要的动作，并顺手把持久化类型翻译成领域记录。
/// </summary>
public sealed class KnowledgePackageStore(KnowledgeBaseStore documents, SqlSugarScope database) : IKnowledgePackageStore
{
    public async Task<KnowledgePackageBase?> GetKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken)
    {
        var knowledgeBase = await documents.GetKnowledgeBaseAsync(id, cancellationToken);
        return knowledgeBase is null ? null : ToDomain(knowledgeBase);
    }

    public async Task<IReadOnlyList<KnowledgePackageBase>> ListKnowledgeBasesAsync(CancellationToken cancellationToken) =>
        (await documents.ListKnowledgeBasesAsync(cancellationToken)).Select(ToDomain).ToList();

    public async Task<KnowledgePackageBase?> CreateKnowledgeBaseAsync(string name, CancellationToken cancellationToken)
    {
        var knowledgeBase = await documents.CreateKnowledgeBaseAsync(name, cancellationToken);
        return knowledgeBase is null ? null : ToDomain(knowledgeBase);
    }

    public async Task<IReadOnlyList<KnowledgePackageDocumentInfo>> ListDocumentsAsync(
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        var rows = await documents.ListDocumentsAsync(knowledgeBaseId, cancellationToken);
        var result = new List<KnowledgePackageDocumentInfo>(rows.Count);
        foreach (var row in rows)
        {
            // 原始文件可能已被手工删掉，导出时按 StoragePath 是否存在决定是否跳过。
            var storagePath = await documents.GetDocumentStoragePathAsync(row.Id, cancellationToken);
            result.Add(new KnowledgePackageDocumentInfo(
                row.Id, row.FileName, row.Extension, row.ContentType, row.SizeBytes,
                row.TopicId, row.Summary, storagePath));
        }

        return result;
    }

    public Task AddQueuedDocumentAsync(
        Guid documentId,
        Guid knowledgeBaseId,
        string fileName,
        string extension,
        string contentType,
        long sizeBytes,
        string storagePath,
        CancellationToken cancellationToken) =>
        documents.AddDocumentAsync(new KnowledgeDocument(
            documentId, knowledgeBaseId, fileName, extension, contentType, sizeBytes, storagePath, "Queued"),
            cancellationToken);

    public Task SetDocumentSummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken) =>
        documents.SetDocumentSummaryAsync(documentId, summary, cancellationToken);

    public Task<string?> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        documents.DeleteDocumentAsync(documentId, cancellationToken);

    /// <summary>
    /// 回滚专用：按「笔记 → 专题 → 资料行 → 知识库」的顺序删干净。
    /// 放在一个事务里，避免删到一半又留下半个空壳。
    /// </summary>
    public async Task DeleteKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        await database.Ado.BeginTranAsync();
        try
        {
            await database.Deleteable<NoteSchemaEntity>()
                .Where(note => note.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<TopicSchemaEntity>()
                .Where(topic => topic.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<KnowledgeDocumentSchemaEntity>()
                .Where(document => document.KnowledgeBaseId == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);
            await database.Deleteable<KnowledgeBaseSchemaEntity>()
                .Where(entity => entity.Id == knowledgeBaseId)
                .ExecuteCommandAsync(cancellationToken);

            await database.Ado.CommitTranAsync();
        }
        catch
        {
            await database.Ado.RollbackTranAsync();
            throw;
        }
    }

    private static KnowledgePackageBase ToDomain(KnowledgeBaseInfo info) =>
        new(info.Id, info.Name, info.CreatedAt);
}
