namespace MyFirstMafAgent.Api.Agents.Tooling;

/// <summary>
/// Agent 的知识库作用域。
///
/// <para>
/// 当对话绑定到某个知识库（<see cref="AgentToolContext.KnowledgeBaseId"/> 非空）时，
/// 模型只能读该知识库。否则模型可以凭一个 GUID 检索任意知识库——
/// 这在"多知识库、按项目隔离"的场景里是真实的越权读取。
/// </para>
///
/// <para>
/// 未绑定知识库（上下文为 null）时保持原有行为（不限制），
/// 因为总览页的全局助手本来就需要跨库检索。
/// </para>
/// </summary>
public static class AgentKnowledgeScope
{
    /// <summary>判定是否允许访问；允许返回 <c>null</c>，否则返回可读的拒绝原因。</summary>
    public static string? Check(Guid? scopedKnowledgeBaseId, Guid requestedKnowledgeBaseId)
    {
        if (scopedKnowledgeBaseId is null) return null;
        if (scopedKnowledgeBaseId.Value == requestedKnowledgeBaseId) return null;

        return $"本次对话只授权访问知识库 {scopedKnowledgeBaseId.Value}，"
            + $"不能访问 {requestedKnowledgeBaseId}。如需其他知识库，请在界面上切换后再提问。";
    }

    /// <summary>列表类工具的过滤：作用域内只保留该知识库。</summary>
    public static IReadOnlyList<Guid> VisibleKnowledgeBaseIds(
        Guid? scopedKnowledgeBaseId,
        IEnumerable<Guid> available) =>
        scopedKnowledgeBaseId is { } scoped
            ? available.Where(id => id == scoped).ToList()
            : available.ToList();
}
