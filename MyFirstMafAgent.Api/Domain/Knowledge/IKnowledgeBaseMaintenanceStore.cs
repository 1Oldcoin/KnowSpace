namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>
/// 知识库删除后的实际清理结果。界面用它告诉用户「删掉了多少东西」，
/// 也用于运维核对有没有残留。
/// </summary>
public sealed record KnowledgeBaseDeletionReport(
    Guid KnowledgeBaseId,
    string Name,
    int DocumentCount,
    int ChunkCount,
    int NoteCount,
    int TopicCount,
    int ChatSessionCount);

/// <summary>
/// 知识库整体删除端口。
///
/// <para>
/// 这是一个高破坏性动作，必须一次删干净：只剩一半的知识库（比如文档没了但切片还在）
/// 会让检索返回指向不存在资料的结果，比彻底删失败更难排查。
/// </para>
///
/// <para>
/// 实现负责数据库侧（在单个事务内按「子表 → 父表」顺序清理）；
/// 磁盘上的资料目录由调用方在事务提交后再删——文件系统不在事务里，
/// 先删文件再回滚会留下「资料在库里、文件不见了」的坏状态。
/// </para>
/// </summary>
public interface IKnowledgeBaseMaintenanceStore
{
    /// <summary>删除知识库及其全部从属数据；知识库不存在时返回 <c>null</c>。</summary>
    Task<KnowledgeBaseDeletionReport?> DeleteKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken);
}
