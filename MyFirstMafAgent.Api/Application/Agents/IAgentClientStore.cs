using MyFirstMafAgent.Api.Domain.Agents;

namespace MyFirstMafAgent.Api.Application.Agents;

/// <summary>
/// 外部智能体客户端的持久化端口。
///
/// <para>
/// 与插件注册表同理：这里回答「谁被允许接入、能做什么」，
/// 运行时状态（本分钟调用次数）只放内存，重启后重新计数。
/// </para>
/// </summary>
public interface IAgentClientStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentClient>> ListAsync(CancellationToken cancellationToken);

    Task<AgentClient?> GetAsync(string id, CancellationToken cancellationToken);

    /// <summary>按密钥摘要查找客户端；未命中返回 null。</summary>
    Task<AgentClient?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken);

    Task CreateAsync(AgentClient client, string keyHash, CancellationToken cancellationToken);

    /// <summary>写入更新；<paramref name="keyHash"/> 为 null 表示保留原密钥。</summary>
    Task UpdateAsync(AgentClient client, string? keyHash, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);

    /// <summary>记录「最近一次被看到」，用于界面显示连通性。</summary>
    Task TouchAsync(string id, DateTimeOffset seenAt, string? address, CancellationToken cancellationToken);
}

/// <summary>MCP 调用结果类别。</summary>
public static class AgentCallOutcomes
{
    /// <summary>正常完成。</summary>
    public const string Success = "success";

    /// <summary>被鉴权或权限闸门拒绝（未执行）。</summary>
    public const string Denied = "denied";

    /// <summary>执行报错或协议错误。</summary>
    public const string Failed = "failed";
}

/// <summary>一条 MCP 调用审计记录。</summary>
public sealed record AgentCallRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string ClientId,
    string? Tool,
    string? KnowledgeBaseId,
    string Outcome,
    int StatusCode,
    long DurationMs,
    string? Error)
{
    public static AgentCallRecord Pending(
        string clientId,
        string? tool,
        string? knowledgeBaseId,
        string outcome,
        int statusCode,
        long durationMs,
        string? error) =>
        new(0, DateTimeOffset.UtcNow, clientId, tool, knowledgeBaseId, outcome, statusCode, durationMs, error);
}

/// <summary>MCP 调用审计存储：追加写，卸载客户端不删除历史。</summary>
public interface IAgentCallLogStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task RecordAsync(AgentCallRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<AgentCallRecord>> QueryAsync(
        string? clientId,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>新建客户端的命令。密钥由服务生成，不接受外部传入。</summary>
public sealed record CreateAgentClientCommand(
    string Name,
    string? Description,
    IReadOnlyList<string>? Capabilities,
    IReadOnlyList<Guid>? AllowedKnowledgeBaseIds,
    IReadOnlyList<string>? AllowedTools,
    int? RequestsPerMinute);

/// <summary>更新客户端的命令；null 表示该字段不变。</summary>
public sealed record UpdateAgentClientCommand(
    string? Name = null,
    string? Description = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<Guid>? AllowedKnowledgeBaseIds = null,
    IReadOnlyList<string>? AllowedTools = null,
    int? RequestsPerMinute = null,
    bool? Enabled = null,
    bool ClearDescription = false);
