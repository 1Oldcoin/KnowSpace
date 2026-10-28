using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Jobs;
using MyFirstMafAgent.Api.Options;
using Npgsql;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>
/// 插件同步任务：把一次平台同步放上任务队列。
///
/// <para>
/// 为什么不直接在接口里同步调用：一次平台同步可能拉几百条、写入几百篇资料，
/// 耗时以秒到分钟计。放进任务队列后它就有了三件接口调用给不了的东西——
/// 进度可见、可取消、崩溃后可自动重跑。
/// </para>
///
/// <para>
/// 手动同步（<c>POST /api/plugins/{id}/sync</c>）与定时同步共用 <see cref="PluginSyncService"/>，
/// 因此两者行为完全一致，区别只是谁发起、结果看哪里。
/// </para>
/// </summary>
public sealed class PluginSyncJobHandler(
    PluginSyncService sync,
    ILogger<PluginSyncJobHandler> logger) : IJobHandler
{
    public string Type => JobTypes.PluginSync;

    /// <summary>任务参数：插件、能力、目标知识库。</summary>
    public sealed record Payload(string PluginId, string CapabilityId, Guid KnowledgeBaseId);

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var payload = ReadPayload(context.Job);
        if (payload is null)
            throw JobExecutionException.NotRetryable("任务参数缺少 pluginId / capabilityId / knowledgeBaseId。");

        try
        {
            var outcome = await sync.SyncAsync(
                payload.PluginId,
                payload.CapabilityId,
                payload.KnowledgeBaseId,
                cursor: null,
                progress: context.ReportProgressAsync,
                cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(new
            {
                outcome.Created,
                outcome.Updated,
                outcome.Skipped,
                outcome.Retracted,
                outcome.Invalid,
                outcome.QuotaRejected,
                outcome.NextCursor,
                outcome.NextSyncAfterSeconds,
                outcome.Status
            });
        }
        catch (PluginSyncException exception)
        {
            logger.LogWarning(
                exception,
                "Plugin {PluginId} sync job {JobId} failed (retryable={Retryable})",
                payload.PluginId, context.Job.Id, exception.Retryable);

            // 权限/配置/结构错误重试也没用，直接判死，避免白跑三次。
            if (!exception.Retryable) throw JobExecutionException.NotRetryable(exception.Message);
            throw new JobExecutionException(exception.Message, fatal: false, exception);
        }
    }

    internal static Payload? ReadPayload(JobRecord job)
    {
        if (string.IsNullOrWhiteSpace(job.Payload)) return null;

        try
        {
            var node = JsonNode.Parse(job.Payload);
            var pluginId = node?["pluginId"]?.GetValue<string>();
            var capabilityId = node?["capabilityId"]?.GetValue<string>();
            var knowledgeBase = node?["knowledgeBaseId"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(capabilityId)) return null;
            if (!Guid.TryParse(knowledgeBase, out var knowledgeBaseId)) return null;

            return new Payload(pluginId, capabilityId, knowledgeBaseId);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// 定时同步的补投器：从"游标 + 插件要求的间隔"推导同步任务。
///
/// <para>
/// 与资料解析一致，任务的<b>唯一来源</b>是业务状态（<c>plugin_sync_states</c>），
/// 而不是"某个地方顺手入队"。因此手动同步、定时同步、进程重启后的补投都走同一条路径，
/// 不会出现"某个入口忘了入队"的情况。
/// </para>
///
/// <para>
/// 幂等键是 <c>plugin.sync:{scopeKey}</c>：同一插件 + 同一能力 + 同一知识库同时只会有一个任务。
/// 终态任务只有在"结束后又过了一个间隔"才会被重新武装——这样即使插件一直失败，
/// 也只是按它自己声明的节奏重试，而不是把队列刷爆。
/// </para>
/// </summary>
public sealed class PluginSyncJobFeeder(
    IPluginSyncStateStore syncStates,
    IPluginRuntime runtime,
    IJobQueue queue,
    IOptions<PluginOptions> options,
    ILogger<PluginSyncJobFeeder> logger) : IJobFeeder
{
    /// <summary>每轮最多补投多少条，避免一次积压几百个游标时把一轮调度拖长。</summary>
    private const int BatchSize = 50;

    /// <summary>
    /// PostgreSQL <c>undefined_table</c>（42P01）。写成字面量而不是依赖
    /// <c>PostgresErrorCodes</c>，避免 Npgsql 版本差异导致编译差异。
    /// </summary>
    private const string UndefinedTable = "42P01";

    /// <summary>
    /// <c>plugin_sync_states</c> 还没建表时的标记，只记一次日志。
    /// <para>
    /// 这是「全新数据库首次启动」的**确定性**竞态，不是偶发故障：这份表由
    /// <c>PluginModule.QueueInitialization</c> 在「主机已监听之后」的后台初始化里建，
    /// 而 <c>JobScheduler</c> 的第一轮补投几乎同时发生（<c>lastFedAt = MinValue</c>）。
    /// 表建好后下一轮就恢复正常，所以不需要重试逻辑，只需要不再刷 ERROR。
    /// </para>
    /// </summary>
    private bool _missingSchemaLogged;

    public async Task<int> FeedAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.ScheduledSyncEnabled) return 0;

        IReadOnlyList<PluginSyncState> scheduled;
        try
        {
            scheduled = await syncStates.ListScheduledAsync(BatchSize, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == UndefinedTable)
        {
            if (!_missingSchemaLogged)
            {
                _missingSchemaLogged = true;
                logger.LogInformation(
                    "plugin_sync_states 尚未建表，本轮跳过定时同步补投"
                    + "（插件初始化在主机就绪后后台进行，建表完成后会自动恢复）。");
            }

            return 0;
        }

        if (scheduled.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var created = 0;

        foreach (var state in scheduled)
        {
            if (!runtime.IsScheduledSyncEligible(state.PluginId)) continue;

            var interval = IntervalOf(state);
            var last = state.LastSyncedAt ?? state.UpdatedAt;
            if (now < last + interval) continue;

            var job = await queue.EnqueueAsync(
                new EnqueueJobCommand(
                    JobTypes.PluginSync,
                    JsonSerializer.Serialize(new
                    {
                        pluginId = state.PluginId,
                        capabilityId = state.CapabilityId,
                        knowledgeBaseId = state.KnowledgeBaseId
                    }),
                    // 同步是后台维护工作：让用户的资料解析先跑。
                    JobPriorities.Low,
                    state.KnowledgeBaseId,
                    null,
                    IdempotencyKey: $"{JobTypes.PluginSync}:{state.ScopeKey}"),
                cancellationToken).ConfigureAwait(false);

            if (!job.IsTerminal)
            {
                if (job.Attempts == 0 && job.Status == JobStatuses.Queued) created++;
                continue;
            }

            // 上一次同步已结束：等它自己也过了一个间隔再重来。
            var finishedAt = job.CompletedAt ?? job.CreatedAt;
            if (now < finishedAt + interval) continue;

            if (await queue.RetryAsync(job.Id, cancellationToken).ConfigureAwait(false) is not null)
                created++;
        }

        if (created > 0)
            logger.LogInformation("Enqueued {Count} scheduled plugin sync job(s)", created);

        return created;
    }

    /// <summary>把插件要求的间隔夹到最小值，避免写错单位的插件把队列刷爆。</summary>
    private TimeSpan IntervalOf(PluginSyncState state)
    {
        var minimum = Math.Max(30, options.Value.MinScheduledSyncIntervalSeconds);
        var seconds = Math.Max(state.NextSyncAfterSeconds ?? minimum, minimum);
        return TimeSpan.FromSeconds(seconds);
    }
}
