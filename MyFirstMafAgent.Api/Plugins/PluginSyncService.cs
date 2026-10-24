using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>一次同步的结果。字段与 <c>POST /api/plugins/{id}/sync</c> 的响应一一对应。</summary>
public sealed record PluginSyncOutcome(
    int Created,
    int Updated,
    int Ingested,
    int Skipped,
    int Retracted,
    int Invalid,
    int QuotaRejected,
    IReadOnlyList<string> Diagnostics,
    string? CursorApplied,
    string? NextCursor,
    int? NextSyncAfterSeconds,
    string Status)
{
    public bool HasFailures => Invalid > 0 || QuotaRejected > 0;
}

/// <summary>同步失败：携带可读原因与是否值得重试。</summary>
public sealed class PluginSyncException(string message, bool retryable, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// 平台同步用例：调用连接器能力 → 校验批次 → 幂等摄取 → 写游标 → 记审计。
///
/// <para>
/// 抽出来的原因是它有三个入口，而<b>只有一份逻辑</b>：
/// 手动同步（<c>POST /api/plugins/{id}/sync</c>）、定时同步（<c>plugin.sync</c> 任务）、
/// 以及后续的重试/补跑。任何一处实现差异都会让“同步进度”变得不可解释。
/// </para>
///
/// <para>
/// 游标语义：不传 <c>cursor</c> 就用持久化游标（真正的增量）；同步失败时<b>保留旧游标</b>，
/// 避免一次瞬时故障导致整库重放。
/// </para>
/// </summary>
public sealed class PluginSyncService(
    IPluginRuntime runtime,
    IPluginIngestionSink sink,
    IPluginSyncStateStore syncStates,
    PluginAuditWriter audit,
    IKnowledgeBaseDirectory knowledgeBases,
    IOptions<PluginOptions> options,
    ILogger<PluginSyncService> logger)
{
    /// <summary>
    /// 执行一次同步。<paramref name="progress"/> 用于把“调用插件 / 写入知识库”这两个阶段
    /// 报给任务中心（手动同步传 null 即可）。
    /// </summary>
    public async Task<PluginSyncOutcome> SyncAsync(
        string pluginId,
        string capabilityId,
        Guid? knowledgeBaseId,
        string? cursor,
        Func<int, string?, CancellationToken, Task>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capabilityId))
            throw new PluginSyncException("capabilityId 必填。", retryable: false);

        var targetKnowledgeBase = knowledgeBaseId ?? KnowledgeBaseStore.DefaultKnowledgeBaseId;
        if (!await knowledgeBases.ExistsAsync(targetKnowledgeBase, cancellationToken).ConfigureAwait(false))
            throw new PluginSyncException($"知识库 '{targetKnowledgeBase}' 不存在。", retryable: false);

        var maxItems = Math.Max(1, options.Value.MaxIngestionItemsPerBatch);
        var scopeKey = IPluginSyncStateStore.BuildScopeKey(pluginId, capabilityId, targetKnowledgeBase);

        var stored = await syncStates.GetByScopeAsync(scopeKey, cancellationToken).ConfigureAwait(false);
        var applied = string.IsNullOrWhiteSpace(cursor) ? stored?.Cursor : cursor;

        await ReportAsync(progress, 5, "调用插件", cancellationToken).ConfigureAwait(false);

        PlatformIngestionBatch? batch;
        long elapsed;
        try
        {
            var arguments = new JsonObject();
            if (!string.IsNullOrWhiteSpace(applied)) arguments["cursor"] = applied;

            var invocation = await runtime.InvokeAsync(
                pluginId,
                capabilityId,
                arguments,
                PluginInvocationContext.System,
                cancellationToken).ConfigureAwait(false);

            elapsed = invocation.ElapsedMilliseconds;
            batch = ParseBatch(invocation.Result);
        }
        catch (PluginRpcException exception)
        {
            await SaveFailureAsync(syncStates, stored, scopeKey, pluginId, capabilityId, targetKnowledgeBase, applied,
                exception.Message, cancellationToken).ConfigureAwait(false);

            // 权限/配置/未知能力这类问题重试也没用；进程不健康才值得退避重试。
            var retryable = exception.Code is PluginProtocol.ErrorUnhealthy or PluginProtocol.ErrorCancelled;
            throw new PluginSyncException(exception.Message, retryable, exception);
        }

        if (batch is null)
        {
            const string reason = "插件返回的数据不是有效的摄取结构。";
            await SaveFailureAsync(syncStates, stored, scopeKey, pluginId, capabilityId, targetKnowledgeBase, applied,
                reason, cancellationToken).ConfigureAwait(false);

            throw new PluginSyncException(reason, retryable: false);
        }

        if (batch.Items.Count > maxItems)
        {
            // 不截断：截断会让游标越过没写进去的条目，等于永久丢数据。
            var reason = $"插件返回 {batch.Items.Count} 条，超过单批上限 {maxItems}。请让插件分页。";
            await SaveFailureAsync(syncStates, stored, scopeKey, pluginId, capabilityId, targetKnowledgeBase, applied,
                reason, cancellationToken).ConfigureAwait(false);

            throw new PluginSyncException(reason, retryable: false);
        }

        await ReportAsync(progress, 40, $"写入知识库（{batch.Items.Count} 条）", cancellationToken)
            .ConfigureAwait(false);

        var outcome = await sink
            .IngestAsync(pluginId, batch, targetKnowledgeBase, cancellationToken)
            .ConfigureAwait(false);

        await audit.RecordAsync(
            pluginId,
            PluginAuditActions.Sync,
            capabilityId,
            PluginActors.System,
            outcome.HasFailures ? PluginAuditOutcomes.Failure : PluginAuditOutcomes.Success,
            elapsed,
            $"kb={targetKnowledgeBase} created={outcome.Created} updated={outcome.Updated} skipped={outcome.Skipped} retracted={outcome.Retracted} invalid={outcome.Invalid} quota={outcome.QuotaRejected}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // 有失败条目时游标仍然推进：失败项的内容哈希没被更新，下次同步会重新判定。
        // 卡住不动的话，一片坏数据会让整条同步永久停摆。
        var status = outcome.HasFailures ? PluginSyncStatuses.Partial : PluginSyncStatuses.Success;
        var nextSyncAfter = ResolveNextSyncAfterSeconds(batch.NextSyncAfterSeconds);

        await syncStates.SaveAsync(new PluginSyncState(
            scopeKey,
            pluginId,
            capabilityId,
            targetKnowledgeBase,
            batch.NextCursor,
            nextSyncAfter,
            DateTimeOffset.UtcNow,
            status,
            outcome.HasFailures ? string.Join("; ", outcome.Diagnostics.Take(5)) : null,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        await ReportAsync(progress, 100, "同步完成", cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Plugin {PluginId} sync {CapabilityId} → kb {KnowledgeBaseId}: created={Created} updated={Updated} skipped={Skipped} retracted={Retracted} invalid={Invalid} quota={Quota}",
            pluginId, capabilityId, targetKnowledgeBase, outcome.Created, outcome.Updated, outcome.Skipped,
            outcome.Retracted, outcome.Invalid, outcome.QuotaRejected);

        return new PluginSyncOutcome(
            outcome.Created,
            outcome.Updated,
            outcome.Ingested,
            outcome.Skipped,
            outcome.Retracted,
            outcome.Invalid,
            outcome.QuotaRejected,
            outcome.Diagnostics,
            applied,
            batch.NextCursor,
            nextSyncAfter,
            status);
    }

    /// <summary>
    /// 把插件要求的下次同步间隔夹到合理区间：
    /// 小于最小值视为“插件写错了单位”，直接提到最小值，避免把任务队列刷爆。
    /// </summary>
    private int? ResolveNextSyncAfterSeconds(int? requested)
    {
        if (!options.Value.ScheduledSyncEnabled) return null;
        if (requested is not { } value || value <= 0) return null;

        return Math.Max(value, Math.Max(30, options.Value.MinScheduledSyncIntervalSeconds));
    }

    private static async Task ReportAsync(
        Func<int, string?, CancellationToken, Task>? progress,
        int percent,
        string? message,
        CancellationToken cancellationToken)
    {
        if (progress is null) return;
        await progress(percent, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>记录同步失败，但保留旧游标——失败不应该让“已经同步到的位置”倒退。</summary>
    private static async Task SaveFailureAsync(
        IPluginSyncStateStore store,
        PluginSyncState? stored,
        string scopeKey,
        string pluginId,
        string capabilityId,
        Guid knowledgeBaseId,
        string? cursor,
        string error,
        CancellationToken cancellationToken)
    {
        await store.SaveAsync(new PluginSyncState(
            scopeKey,
            pluginId,
            capabilityId,
            knowledgeBaseId,
            stored?.Cursor ?? cursor,
            stored?.NextSyncAfterSeconds,
            stored?.LastSyncedAt,
            PluginSyncStatuses.Failed,
            error,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析插件返回的批次。兼容三种形态：直接返回 batch 对象、包在 <c>{ items: [...] }</c> 里、
    /// 或（插件作者自己又套一层时）包在 <c>{ result: { items: [...] } }</c> 里。
    /// </summary>
    public static PlatformIngestionBatch? ParseBatch(JsonNode? node)
    {
        // 不是对象就不是批次（字符串、数组、数字一律视为插件返回了无效结构）。
        if (node is not JsonObject root) return null;

        // RPC 层已经拆过一层 result；这里再兜一次，避免插件 double-wrap 时静默变成空批次。
        if (root["result"] is JsonObject wrapped && root["items"] is null) return ParseBatch(wrapped);

        try
        {
            var batch = PluginJson.ToObject<PlatformIngestionBatch>(node);
            if (batch is not null && batch.Items.Count > 0) return batch;

            var nested = node["items"];
            return nested is null ? batch : new PlatformIngestionBatch
            {
                Items = PluginJson.ToObject<List<PlatformIngestionItem>>(nested) ?? [],
                NextCursor = node["nextCursor"]?.GetValue<string>(),
                NextSyncAfterSeconds = node["nextSyncAfterSeconds"]?.GetValue<int>()
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
