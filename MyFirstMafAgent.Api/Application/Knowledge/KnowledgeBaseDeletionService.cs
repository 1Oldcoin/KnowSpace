using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Application.Knowledge;

/// <summary>删除知识库的结果。三种失败彼此不同，接口层据此选 404 / 409。</summary>
public sealed record KnowledgeBaseDeletionOutcome(
    bool Deleted,
    bool NotFound,
    string? Error,
    KnowledgeBaseDeletionReport? Report)
{
    public static KnowledgeBaseDeletionOutcome Succeeded(KnowledgeBaseDeletionReport report) =>
        new(true, false, null, report);

    public static KnowledgeBaseDeletionOutcome Missing() =>
        new(false, true, "知识库不存在或已被删除。", null);

    public static KnowledgeBaseDeletionOutcome Rejected(string error) =>
        new(false, false, error, null);
}

/// <summary>
/// 删除知识库：先删数据库，再删磁盘。
///
/// <para>
/// 顺序不能颠倒。文件系统不参与数据库事务，如果先删文件再回滚，就会留下
/// 「资料记录还在、原文件已经没了」的状态——界面照常列出一条点不开的资料，
/// 比删除直接失败难查得多。反过来（库已删、目录残留）最坏只是泄漏磁盘空间，
/// 而且这里会把残留写进日志，运维能清理。
/// </para>
/// </summary>
public sealed class KnowledgeBaseDeletionService(
    IKnowledgeBaseMaintenanceStore store,
    LocalDocumentStore storage,
    ILogger<KnowledgeBaseDeletionService> logger)
{
    public async Task<KnowledgeBaseDeletionOutcome> DeleteAsync(
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        // 默认知识库是所有「没指定知识库」的旧调用的落点：上传、检索、MCP 工具都靠它兜底。
        // 删掉它，这些调用会全部打到一个不存在的 ID 上，所以这里直接拒绝，而不是留给界面隐藏按钮。
        if (knowledgeBaseId == KnowledgeBaseDefaults.DefaultKnowledgeBaseId)
        {
            return KnowledgeBaseDeletionOutcome.Rejected(
                "默认知识库不能删除：未指定知识库的上传与检索都以它为兜底。"
                + "请改为删除自行创建的知识库。");
        }

        var report = await store.DeleteKnowledgeBaseAsync(knowledgeBaseId, cancellationToken);
        if (report is null) return KnowledgeBaseDeletionOutcome.Missing();

        logger.LogWarning(
            "知识库已删除：{KnowledgeBaseId}「{Name}」，资料 {Documents} 篇、切片 {Chunks} 条、"
            + "笔记 {Notes} 条、专题 {Topics} 个、问答会话 {Sessions} 个。"
            + "注意：已授予该知识库范围的 MCP 客户端不会自动放开——它们仍然受限，只是再也访问不到这个库。",
            report.KnowledgeBaseId, report.Name, report.DocumentCount, report.ChunkCount,
            report.NoteCount, report.TopicCount, report.ChatSessionCount);

        if (!storage.DeleteKnowledgeBaseDirectory(knowledgeBaseId))
        {
            // 数据库已经删了，文件删不掉只能记日志：不能因为磁盘占用失败就把已提交的删除回滚。
            logger.LogError(
                "知识库 {KnowledgeBaseId} 的数据库记录已删除，但资料目录清理失败，需要人工清理。",
                knowledgeBaseId);
        }

        return KnowledgeBaseDeletionOutcome.Succeeded(report);
    }
}
