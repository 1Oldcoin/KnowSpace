using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Agents;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 外部智能体客户端管理：谁可以接入 MCP、能做什么、做了什么。
///
/// <para>
/// 密钥明文只在创建与轮换的响应中出现一次；列表接口只返回前缀与哈希无关的信息。
/// </para>
/// </summary>
public static class AgentClientEndpoints
{
    public static void MapAgentClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/agent-clients").WithTags("智能体接入");

        group.MapGet("", async (AgentClientService service, CancellationToken cancellationToken) =>
            Results.Ok(new { clients = await service.ListAsync(cancellationToken) }));

        // 能力与工具清单：前端据此渲染勾选项，插件/脚本据此校验拼写。
        group.MapGet("/catalog", () => Results.Ok(new
        {
            capabilities = AgentClientCapabilities.AllKnown,
            defaults = AgentClientCapabilities.DefaultForNewClient,
            tools = McpToolCatalog.All.Select(tool => new
            {
                name = tool.Name,
                capability = tool.Capability,
                isWrite = tool.IsWrite,
                usesKnowledgeBase = tool.UsesKnowledgeBase
            })
        }));

        // 「谁连着、调了多少次」的单一入口：界面一次请求就能渲染整页，
        // 不必先拿列表再逐客户端拉调用明细（那会变成 N+1，而且明细有 500 条上限）。
        group.MapGet("/overview", async (
            int? windowHours,
            AgentClientService service,
            IAgentCallLogStore callLog,
            CancellationToken cancellationToken) =>
        {
            var hours = Math.Clamp(windowHours ?? 24, 1, 24 * 30);
            var now = DateTimeOffset.UtcNow;
            var since = now.AddHours(-hours);

            var clients = await service.ListAsync(cancellationToken);
            var summaries = await callLog.SummarizeAsync(since, cancellationToken);
            var byClient = summaries.ToDictionary(summary => summary.ClientId, StringComparer.Ordinal);
            var onlineSince = now.AddMinutes(-AgentClientPresence.OnlineWindowMinutes);

            // 「在线」是近似值：MCP 服务端是无状态的，没有长连接可查。
            // 判定口径写在返回体里，界面据此显示"最近活跃"而不是"已连接"。
            bool IsOnline(AgentClient client) =>
                client.Enabled && client.LastSeenAt is { } seen && seen >= onlineSince;

            return Results.Ok(new
            {
                windowHours = hours,
                onlineWindowMinutes = AgentClientPresence.OnlineWindowMinutes,
                generatedAt = now,
                totals = new
                {
                    clients = clients.Count,
                    enabled = clients.Count(client => client.Enabled),
                    disabled = clients.Count(client => !client.Enabled),
                    online = clients.Count(IsOnline),
                    calls = summaries.Sum(summary => summary.Total),
                    success = summaries.Sum(summary => summary.Success),
                    denied = summaries.Sum(summary => summary.Denied),
                    failed = summaries.Sum(summary => summary.Failed)
                },
                clients = clients
                    .OrderByDescending(IsOnline)
                    .ThenByDescending(client => client.LastSeenAt ?? DateTimeOffset.MinValue)
                    .Select(client => new
                    {
                        client.Id,
                        client.Name,
                        client.Description,
                        client.KeyPrefix,
                        client.Capabilities,
                        client.AllowedKnowledgeBaseIds,
                        client.AllowedTools,
                        client.RequestsPerMinute,
                        client.Enabled,
                        client.CreatedAt,
                        client.UpdatedAt,
                        client.LastSeenAt,
                        client.LastSeenAddress,
                        online = IsOnline(client),
                        usage = byClient.TryGetValue(client.Id, out var summary)
                            ? new
                            {
                                summary.Total,
                                summary.Success,
                                summary.Denied,
                                summary.Failed,
                                summary.LastCallAt
                            }
                            : null
                    })
            });
        });

        group.MapGet("/calls", async (
            string? clientId,
            int? limit,
            IAgentCallLogStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                calls = await store.QueryAsync(clientId, limit ?? 50, cancellationToken)
            }));

        group.MapGet("/{id}", async (
            string id,
            AgentClientService service,
            CancellationToken cancellationToken) =>
        {
            var client = await service.GetAsync(id, cancellationToken);
            return client is null ? Results.NotFound() : Results.Ok(client);
        });

        group.MapPost("", async (
            CreateAgentClientRequest request,
            AgentClientService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.CreateAsync(
                new CreateAgentClientCommand(
                    request.Name ?? string.Empty,
                    request.Description,
                    request.Capabilities,
                    request.AllowedKnowledgeBaseIds,
                    request.AllowedTools,
                    request.RequestsPerMinute),
                cancellationToken);

            return result.Success
                ? Results.Ok(new { client = result.Client, key = result.Key })
                : Results.BadRequest(new { errors = result.Errors });
        });

        group.MapPut("/{id}", async (
            string id,
            UpdateAgentClientRequest request,
            AgentClientService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.UpdateAsync(
                id,
                new UpdateAgentClientCommand(
                    request.Name,
                    request.Description,
                    request.Capabilities,
                    request.AllowedKnowledgeBaseIds,
                    request.AllowedTools,
                    request.RequestsPerMinute,
                    request.Enabled,
                    request.ClearDescription ?? false),
                cancellationToken);

            if (result.Success) return Results.Ok(result.Client);
            return result.Errors.Count == 1 && result.Errors[0].StartsWith("客户端不存在", StringComparison.Ordinal)
                ? Results.NotFound(new { errors = result.Errors })
                : Results.BadRequest(new { errors = result.Errors });
        });

        // 轮换：旧密钥立即失效，新密钥只返回一次。
        group.MapPost("/{id}/rotate", async (
            string id,
            AgentClientService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.RotateKeyAsync(id, cancellationToken);
            if (result.Success) return Results.Ok(new { client = result.Client, key = result.Key });

            return result.Errors.Count == 1 && result.Errors[0].StartsWith("客户端不存在", StringComparison.Ordinal)
                ? Results.NotFound(new { errors = result.Errors })
                : Results.BadRequest(new { errors = result.Errors });
        });

        group.MapDelete("/{id}", async (
            string id,
            AgentClientService service,
            AgentRateLimiter limiter,
            CancellationToken cancellationToken) =>
        {
            if (!await service.DeleteAsync(id, cancellationToken)) return Results.NotFound();

            limiter.Forget(id);
            return Results.NoContent();
        });
    }

    /// <summary>新建请求。<c>capabilities</c> 省略时默认只读知识库。</summary>
    public sealed record CreateAgentClientRequest(
        string? Name,
        string? Description,
        IReadOnlyList<string>? Capabilities,
        IReadOnlyList<Guid>? AllowedKnowledgeBaseIds,
        IReadOnlyList<string>? AllowedTools,
        int? RequestsPerMinute);

    /// <summary>更新请求：字段为 null 表示不修改；额外支持清空描述与启停。</summary>
    public sealed record UpdateAgentClientRequest(
        string? Name,
        string? Description,
        IReadOnlyList<string>? Capabilities,
        IReadOnlyList<Guid>? AllowedKnowledgeBaseIds,
        IReadOnlyList<string>? AllowedTools,
        int? RequestsPerMinute,
        bool? Enabled,
        bool? ClearDescription);
}
