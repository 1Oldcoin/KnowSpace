using MyFirstMafAgent.Api.Domain.Agents;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>
/// 一次 Bearer 令牌校验通过后的调用主体。
///
/// <para>
/// 它<b>不是</b>另一个权限系统，而是把 OAuth 的结果翻译成既有的
/// <see cref="AgentClient"/> 形状，交给同一个 <see cref="Hosting.AgentClientGate"/>：
/// 能力校验、工具白名单、知识库范围、限流、审计一行都不用改。
/// 这就是「OAuth 只管第一层，其余闸门照旧」的落地方式。
/// </para>
/// </summary>
public sealed record OAuthPrincipal(
    string ClientId,
    string ClientName,
    string Subject,
    string ConsentId,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<Guid> KnowledgeBaseIds,
    DateTimeOffset ExpiresAt,
    string Jti)
{
    /// <summary>
    /// 合成一个 <see cref="AgentClient"/> 供闸门使用。
    /// Id 固定带 <c>oauth:</c> 前缀，审计里一眼能分清是密钥还是 OAuth 通道。
    /// </summary>
    public AgentClient ToAgentClient(int requestsPerMinute)
    {
        var capabilities = OAuthScopes.ToCapabilities(Scopes);
        var now = DateTimeOffset.UtcNow;

        return new AgentClient(
            "oauth:" + ClientId,
            ClientName,
            "OAuth 授权（subject=" + Subject + "）",
            KeyPrefix: "oauth",
            capabilities,
            KnowledgeBaseIds,
            AllowedTools: [],
            requestsPerMinute,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastSeenAt: now,
            LastSeenAddress: null);
    }
}
