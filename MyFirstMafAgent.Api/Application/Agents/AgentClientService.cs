using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Application.Agents;

/// <summary>客户端变更结果：失败时 <see cref="Errors"/> 面向用户可读。</summary>
public sealed record AgentClientMutationResult(
    bool Success,
    AgentClient? Client,
    IReadOnlyList<string> Errors,
    string? Key = null)
{
    public static AgentClientMutationResult Ok(AgentClient client, string? key = null) =>
        new(true, client, [], key);

    public static AgentClientMutationResult Fail(params string[] errors) =>
        new(false, null, errors);
}

/// <summary>
/// 外部智能体客户端的用例层：创建、轮换密钥、改作用域、启停、删除、以及接入时的密钥校验。
///
/// <para>
/// 三条约束在这里集中执行，避免各处重复：
/// </para>
/// <list type="bullet">
///   <item><description>能力名、工具名必须来自已登记清单（拼写错误不会变成"永不放行"的静默失效）。</description></item>
///   <item><description>密钥明文只在创建与轮换时返回一次。</description></item>
///   <item><description>列表接口永不返回哈希。</description></item>
/// </list>
/// </summary>
public sealed class AgentClientService(
    IAgentClientStore store,
    IKnowledgeBaseDirectory knowledgeBases,
    IOptions<McpOptions> mcpOptions,
    ILogger<AgentClientService> logger)
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;
    private const int MaxRequestsPerMinute = 100_000;

    public Task<IReadOnlyList<AgentClient>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAsync(cancellationToken);

    public Task<AgentClient?> GetAsync(string id, CancellationToken cancellationToken) =>
        store.GetAsync(id, cancellationToken);

    public async Task<AgentClientMutationResult> CreateAsync(
        CreateAgentClientCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        var name = NormalizeName(command.Name, errors);
        var description = NormalizeDescription(command.Description, errors);
        var capabilities = NormalizeCapabilities(command.Capabilities, errors);
        var allowedKnowledgeBases = NormalizeKnowledgeBases(command.AllowedKnowledgeBaseIds, errors);
        var allowedTools = NormalizeTools(command.AllowedTools, errors);
        var requestsPerMinute = NormalizeRateLimit(command.RequestsPerMinute, errors);

        await ValidateKnowledgeBasesExistAsync(allowedKnowledgeBases, errors, cancellationToken);

        if (errors.Count > 0) return AgentClientMutationResult.Fail([.. errors]);

        var existing = await store.ListAsync(cancellationToken);
        if (existing.Count >= mcpOptions.Value.MaxAgentClients)
        {
            return AgentClientMutationResult.Fail(
                $"客户端数量已达到上限 {mcpOptions.Value.MaxAgentClients}，请先删除不再使用的客户端。");
        }

        if (existing.Any(client => string.Equals(client.Name, name, StringComparison.OrdinalIgnoreCase)))
            return AgentClientMutationResult.Fail($"已存在同名客户端：{name}");

        var (key, hash, displayPrefix) = AgentClientKey.Create();
        var now = DateTimeOffset.UtcNow;
        var client = new AgentClient(
            "ac_" + Guid.NewGuid().ToString("N")[..20],
            name,
            description,
            displayPrefix,
            capabilities,
            allowedKnowledgeBases,
            allowedTools,
            requestsPerMinute,
            Enabled: true,
            now,
            now,
            LastSeenAt: null,
            LastSeenAddress: null);

        await store.CreateAsync(client, hash, cancellationToken);

        logger.LogInformation(
            "Agent client {ClientId} ({Name}) created with capabilities {Capabilities}",
            client.Id, client.Name, string.Join(",", capabilities));

        // 明文密钥只在此处出现一次。
        return AgentClientMutationResult.Ok(client, key);
    }

    public async Task<AgentClientMutationResult> UpdateAsync(
        string id,
        UpdateAgentClientCommand command,
        CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(id, cancellationToken);
        if (existing is null) return AgentClientMutationResult.Fail($"客户端不存在：{id}");

        var errors = new List<string>();

        var name = command.Name is null ? existing.Name : NormalizeName(command.Name, errors);
        var description = command.ClearDescription
            ? null
            : command.Description is null ? existing.Description : NormalizeDescription(command.Description, errors);
        var capabilities = command.Capabilities is null
            ? existing.Capabilities
            : NormalizeCapabilities(command.Capabilities, errors);
        var allowedKnowledgeBases = command.AllowedKnowledgeBaseIds is null
            ? existing.AllowedKnowledgeBaseIds
            : NormalizeKnowledgeBases(command.AllowedKnowledgeBaseIds, errors);
        var allowedTools = command.AllowedTools is null
            ? existing.AllowedTools
            : NormalizeTools(command.AllowedTools, errors);
        var requestsPerMinute = command.RequestsPerMinute is null
            ? existing.RequestsPerMinute
            : NormalizeRateLimit(command.RequestsPerMinute, errors);

        await ValidateKnowledgeBasesExistAsync(allowedKnowledgeBases, errors, cancellationToken);

        if (errors.Count > 0) return AgentClientMutationResult.Fail([.. errors]);

        if (!string.Equals(name, existing.Name, StringComparison.Ordinal))
        {
            var others = await store.ListAsync(cancellationToken);
            if (others.Any(other => other.Id != existing.Id
                && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return AgentClientMutationResult.Fail($"已存在同名客户端：{name}");
            }
        }

        var updated = existing with
        {
            Name = name,
            Description = description,
            Capabilities = capabilities,
            AllowedKnowledgeBaseIds = allowedKnowledgeBases,
            AllowedTools = allowedTools,
            RequestsPerMinute = requestsPerMinute,
            Enabled = command.Enabled ?? existing.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.UpdateAsync(updated, keyHash: null, cancellationToken);
        return AgentClientMutationResult.Ok(updated);
    }

    /// <summary>轮换密钥：旧密钥立即失效，新密钥只返回一次。</summary>
    public async Task<AgentClientMutationResult> RotateKeyAsync(string id, CancellationToken cancellationToken)
    {
        var existing = await store.GetAsync(id, cancellationToken);
        if (existing is null) return AgentClientMutationResult.Fail($"客户端不存在：{id}");

        var (key, hash, displayPrefix) = AgentClientKey.Create();
        var updated = existing with
        {
            KeyPrefix = displayPrefix,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await store.UpdateAsync(updated, hash, cancellationToken);

        logger.LogInformation("Agent client {ClientId} key rotated", id);
        return AgentClientMutationResult.Ok(updated, key);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken) is null) return false;

        await store.DeleteAsync(id, cancellationToken);
        logger.LogInformation("Agent client {ClientId} deleted", id);
        return true;
    }

    /// <summary>
    /// 校验接入密钥。命中即刷新 lastSeen；<b>不</b>在这里判断 Enabled——
    /// 调用方需要区分「密钥无效（401）」与「已停用（403）」。
    /// </summary>
    public async Task<AgentClient?> AuthenticateAsync(
        string? presentedKey,
        string? clientAddress,
        CancellationToken cancellationToken)
    {
        if (!AgentClientKey.LooksLikeKey(presentedKey)) return null;

        var hash = AgentClientKey.Hash(presentedKey!);
        var client = await store.FindByKeyHashAsync(hash, cancellationToken);
        if (client is null) return null;

        var now = DateTimeOffset.UtcNow;
        try
        {
            await store.TouchAsync(client.Id, now, clientAddress, cancellationToken);
        }
        catch (Exception exception)
        {
            // 记录连通性失败不应影响接入。
            logger.LogWarning(exception, "Failed to update last-seen for agent client {ClientId}", client.Id);
        }

        return client with { LastSeenAt = now, LastSeenAddress = clientAddress };
    }

    private string NormalizeName(string? value, List<string> errors)
    {
        var name = value?.Trim() ?? string.Empty;
        if (name.Length == 0) errors.Add("客户端名称必填。");
        else if (name.Length > MaxNameLength) errors.Add($"客户端名称不能超过 {MaxNameLength} 个字符。");
        return name;
    }

    private static string? NormalizeDescription(string? value, List<string> errors)
    {
        var description = value?.Trim();
        if (string.IsNullOrEmpty(description)) return null;
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add($"描述不能超过 {MaxDescriptionLength} 个字符。");
            return null;
        }

        return description;
    }

    private static IReadOnlyList<string> NormalizeCapabilities(IReadOnlyList<string>? values, List<string> errors)
    {
        var source = values is null || values.Count == 0
            ? AgentClientCapabilities.DefaultForNewClient
            : values;

        var result = new List<string>();
        foreach (var value in source)
        {
            var capability = value?.Trim() ?? string.Empty;
            if (!AgentClientCapabilities.IsKnown(capability))
            {
                errors.Add($"未知能力：{capability}。可用能力：{string.Join("、", AgentClientCapabilities.AllKnown)} 或 {AgentClientCapabilities.All}。");
                continue;
            }

            if (!result.Contains(capability, StringComparer.Ordinal)) result.Add(capability);
        }

        if (result.Count == 0 && errors.Count == 0) errors.Add("至少需要授予一项能力。");
        return result;
    }

    private static IReadOnlyList<Guid> NormalizeKnowledgeBases(IReadOnlyList<Guid>? values, List<string> errors)
    {
        if (values is null || values.Count == 0) return [];

        var result = new List<Guid>();
        foreach (var value in values)
        {
            if (value == Guid.Empty)
            {
                errors.Add("知识库 ID 不能为空。空数组表示不限制，而不是限制到某个空 ID。");
                continue;
            }

            if (!result.Contains(value)) result.Add(value);
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeTools(IReadOnlyList<string>? values, List<string> errors)
    {
        if (values is null || values.Count == 0) return [];

        var result = new List<string>();
        foreach (var value in values)
        {
            var tool = value?.Trim() ?? string.Empty;
            if (!McpToolCatalog.IsKnownTool(tool))
            {
                errors.Add($"未知工具：{tool}。请先调用 /api/agent-clients/catalog 查看可用工具。");
                continue;
            }

            if (!result.Contains(tool, StringComparer.Ordinal)) result.Add(tool);
        }

        return result;
    }

    private int NormalizeRateLimit(int? value, List<string> errors)
    {
        var limit = value ?? mcpOptions.Value.DefaultRequestsPerMinute;
        if (limit is < 0 or > MaxRequestsPerMinute)
        {
            errors.Add($"每分钟请求数必须在 0 到 {MaxRequestsPerMinute} 之间（0 表示不限制）。");
            return 0;
        }

        return limit;
    }

    /// <summary>知识库白名单必须指向真实存在的知识库：指向不存在的库只会得到「永远拒绝」。</summary>
    private async Task ValidateKnowledgeBasesExistAsync(
        IReadOnlyList<Guid> allowedKnowledgeBaseIds,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (var knowledgeBaseId in allowedKnowledgeBaseIds)
        {
            if (!await knowledgeBases.ExistsAsync(knowledgeBaseId, cancellationToken))
                errors.Add($"知识库不存在：{knowledgeBaseId:D}");
        }
    }
}
