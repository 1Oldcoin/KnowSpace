namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>知识库包需要的最少知识库信息（不含 UI 关心的其它字段）。</summary>
public sealed record KnowledgePackageBase(Guid Id, string Name, DateTimeOffset CreatedAt);

/// <summary>
/// 知识库包里的一份资料：元数据 + 原始文件路径。
/// <see cref="StoragePath"/> 由基础设施提供，导出时直接读该文件。
/// </summary>
public sealed record KnowledgePackageDocumentInfo(
    Guid Id,
    string FileName,
    string Extension,
    string ContentType,
    long SizeBytes,
    Guid? TopicId,
    string? Summary,
    string? StoragePath);

/// <summary>
/// 知识库包的数据面端口。
///
/// <para>
/// 为什么单独开一个端口而不是直接注入 <c>KnowledgeBaseStore</c>：包导出/导入属于应用服务，
/// 应用层不该认识持久化类型（分层检查会报错，也确实会带来循环依赖的诱惑）。
/// 这里只声明包里真正用到的那几个动作，实现放在持久化层。
/// </para>
/// </summary>
public interface IKnowledgePackageStore
{
    Task<KnowledgePackageBase?> GetKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgePackageBase>> ListKnowledgeBasesAsync(CancellationToken cancellationToken);

    /// <summary>新建知识库；同名时返回 null（调用方要先解决重名）。</summary>
    Task<KnowledgePackageBase?> CreateKnowledgeBaseAsync(string name, CancellationToken cancellationToken);

    Task<IReadOnlyList<KnowledgePackageDocumentInfo>> ListDocumentsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);

    /// <summary>登记一份已落盘的资料并置为排队解析——导入后由既有队列重新切片建索引。</summary>
    Task AddQueuedDocumentAsync(
        Guid documentId,
        Guid knowledgeBaseId,
        string fileName,
        string extension,
        string contentType,
        long sizeBytes,
        string storagePath,
        CancellationToken cancellationToken);

    Task SetDocumentSummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken);

    /// <summary>
    /// 删除一份资料及其切片/版本/摘要行，返回它的磁盘路径（文件由调用方删）。
    /// 只用于导入失败时的补偿回滚。
    /// </summary>
    Task<string?> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken);

    /// <summary>
    /// 删除一个知识库及其笔记/专题/资料行。只用于导入失败时的补偿回滚：
    /// 正常路径下库里已经没有内容了，调用它只是把空壳收掉。
    /// </summary>
    Task DeleteKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);
}
