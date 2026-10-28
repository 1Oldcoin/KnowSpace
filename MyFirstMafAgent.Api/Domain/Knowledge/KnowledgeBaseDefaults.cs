namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>
/// 跨层共享的知识库常量。
///
/// <para>
/// 放在 Domain 而不是持久化实现里，是因为「默认知识库不可删除」这条规则属于业务约束，
/// 应用层与端点层都需要判断它，而这两层不应该反向依赖 <c>Persistence</c>。
/// </para>
/// </summary>
public static class KnowledgeBaseDefaults
{
    /// <summary>
    /// 默认知识库 ID。所有未显式指定知识库的旧调用（上传、检索、MCP 工具）都落在这里，
    /// 因此它既是兜底目标，也是唯一不允许删除的知识库。
    /// </summary>
    public static readonly Guid DefaultKnowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public const string DefaultName = "默认知识库";
}
