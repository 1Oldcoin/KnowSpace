using MyFirstMafAgent.Api.Domain.Agents;

namespace MyFirstMafAgent.Api.Application.Agents;

/// <summary>一次 MCP 工具调用的准入结论。</summary>
public sealed record AgentCallDecision(
    bool Allowed,
    int JsonRpcErrorCode,
    int HttpStatusCode,
    string Message,
    Guid? EffectiveKnowledgeBaseId)
{
    public static AgentCallDecision Allow(Guid? knowledgeBaseId) =>
        new(true, 0, HttpOk, string.Empty, knowledgeBaseId);

    public static AgentCallDecision Deny(int jsonRpcErrorCode, string message) =>
        new(false, jsonRpcErrorCode, HttpOk, message, null);

    /// <summary>工具级拒绝用 HTTP 200 + JSON-RPC 错误体，让模型能看到可读原因而不是传输错误。</summary>
    private const int HttpOk = 200;
}

/// <summary>
/// MCP 调用的准入策略：纯函数，可脱库穷举。
///
/// <para>
/// 判定顺序固定为 <b>工具存在 → 能力 → 工具白名单 → 知识库范围</b>：
/// 先回答"这个工具存不存在"，再回答"你能不能调它"，最后回答"数据范围对不对"。
/// 顺序反过来会让错误信息泄露工具清单之外的细节。
/// </para>
/// </summary>
public static class AgentCallGate
{
    /// <summary>JSON-RPC 标准错误码。</summary>
    public const int MethodNotFound = -32601;

    /// <summary>自定义权限拒绝（与插件侧一致）。</summary>
    public const int PermissionDenied = -32003;

    public static AgentCallDecision Decide(
        AgentClient client,
        string toolName,
        Guid? requestedKnowledgeBaseId,
        Guid defaultKnowledgeBaseId)
    {
        if (!McpToolCatalog.TryGet(toolName, out var descriptor))
            return AgentCallDecision.Deny(MethodNotFound, $"未知工具：{toolName}。请先调用 tools/list 查看可用工具。");

        if (!client.HasCapability(descriptor.Capability))
        {
            return AgentCallDecision.Deny(
                PermissionDenied,
                $"客户端 '{client.Name}' 未获得能力 {descriptor.Capability}，无法调用 {toolName}。");
        }

        if (!client.IsToolAllowed(toolName))
        {
            return AgentCallDecision.Deny(
                PermissionDenied,
                $"客户端 '{client.Name}' 的白名单未包含工具 {toolName}。");
        }

        var requested = requestedKnowledgeBaseId is { } id && id != Guid.Empty ? id : (Guid?)null;

        if (!descriptor.UsesKnowledgeBase)
        {
            // 「知识库可选」的工具（chat_search_history 等）也必须校验显式传入的知识库 ID：
            // 否则受限客户端可以靠传一个不相关的 knowledgeBaseId 绕过 allowedKnowledgeBaseIds，
            // 把别人的问答历史读出来。可选 ≠ 免检。
            if (requested is { } explicitId && !client.CanAccessKnowledgeBase(explicitId))
            {
                return AgentCallDecision.Deny(
                    PermissionDenied,
                    $"客户端 '{client.Name}' 未被授权访问知识库 '{explicitId:D}'。允许的知识库："
                    + string.Join(", ", client.AllowedKnowledgeBaseIds.Select(item => item.ToString("D"))));
            }

            return AgentCallDecision.Allow(null);
        }

        var effective = requested ?? defaultKnowledgeBaseId;

        if (!client.CanAccessKnowledgeBase(effective))
        {
            return AgentCallDecision.Deny(
                PermissionDenied,
                $"客户端 '{client.Name}' 未被授权访问知识库 '{effective:D}'。允许的知识库："
                + string.Join(", ", client.AllowedKnowledgeBaseIds.Select(id => id.ToString("D"))));
        }

        return AgentCallDecision.Allow(effective);
    }

    /// <summary>
    /// 按资料反查所属知识库后的判定：<c>get_document_content</c> 只带 documentId，
    /// 光看参数无法判断越界，必须回查资料属于哪个知识库。
    /// </summary>
    public static AgentCallDecision DecideDocumentAccess(AgentClient client, Guid documentKnowledgeBaseId) =>
        client.CanAccessKnowledgeBase(documentKnowledgeBaseId)
            ? AgentCallDecision.Allow(documentKnowledgeBaseId)
            : AgentCallDecision.Deny(
                PermissionDenied,
                $"客户端 '{client.Name}' 未被授权访问该资料所属的知识库 '{documentKnowledgeBaseId:D}'。");

    /// <summary>按列表过滤知识库：受限客户端只能看到自己范围内的库。</summary>
    public static bool IsKnowledgeBaseVisible(AgentClient client, Guid knowledgeBaseId) =>
        client.CanAccessKnowledgeBase(knowledgeBaseId);
}
