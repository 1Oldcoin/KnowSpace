using System.Text.Json;
using System.Text.Json.Nodes;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Domain.Jobs;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>
/// 资料解析任务：把 <c>document.ingest</c> 任务接到既有的摄取用例上。
///
/// <para>
/// 三条不变量：
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>先抢占再解析</b>：用 <c>Queued → Processing</c> 的条件更新抢占资料。
///     抢不到说明别人已经在做（或已完成），本次直接结束——重复投递是安全的。
///   </description></item>
///   <item><description>
///     <b>进度跟着资料走</b>：解析过程中轮询资料的 <c>Progress</c> 并回写任务，
///     这样任务中心与前端进度条看到的是同一份真实进度，而不是估出来的。
///   </description></item>
///   <item><description>
///     <b>失败要可重试</b>：解析失败时把资料放回 <c>Queued</c> 再抛
///     <see cref="JobExecutionException"/>，重试才有东西可抢；否则第二次一定"状态不对"而立即失败。
///   </description></item>
/// </list>
/// </summary>
public sealed class DocumentIngestJobHandler(
    KnowledgeBaseStore store,
    KnowledgeIngestionService ingestion,
    ILogger<DocumentIngestJobHandler> logger) : IJobHandler
{
    /// <summary>解析进度轮询间隔：够快能看到进度，又不至于把数据库打满。</summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(1200);

    private const string FailedStatus = "Failed";

    public string Type => JobTypes.DocumentIngest;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryReadDocumentId(context.Job.Payload, out var documentId))
            throw JobExecutionException.NotRetryable("任务参数缺少有效的 documentId。");

        var queued = await store.TryClaimDocumentAsync(documentId, cancellationToken);
        if (queued is null)
        {
            // 幂等出口：已被别人处理、已完成、或者已被删除，都不该算失败。
            var existing = await store.GetDocumentAsync(documentId, cancellationToken);
            if (existing is null)
                throw JobExecutionException.NotRetryable($"资料不存在：{documentId:D}");

            // 资料停在 Failed，说明是用户在任务中心点了"重跑"。
            // 只把任务重新排队而不动资料的话，重跑会立刻读到 Failed 然后"成功但什么都没做"，
            // 所以这里顺手把资料也放回队列，让"重跑任务"和"重新解析资料"是同一件事。
            if (string.Equals(existing.Status, FailedStatus, StringComparison.Ordinal))
            {
                await store.ResetDocumentToQueuedAsync(documentId, cancellationToken);
                queued = await store.TryClaimDocumentAsync(documentId, cancellationToken);
            }

            if (queued is null)
            {
                logger.LogInformation(
                    "Document {DocumentId} is {Status}; skipping ingestion for job {JobId}",
                    documentId, existing.Status, context.Job.Id);

                return $"资料状态为 {existing.Status}，无需重复解析。";
            }
        }

        await context.ReportProgressAsync(1, "开始解析", cancellationToken);

        var processing = ingestion.ProcessAsync(queued, cancellationToken);
        while (!processing.IsCompleted)
        {
            var tick = Task.Delay(ProgressPollInterval, cancellationToken);
            if (await Task.WhenAny(processing, tick) == processing) break;

            var snapshot = await store.GetDocumentAsync(documentId, cancellationToken);
            if (snapshot is null) continue;

            // 上报进度同时也会续租，因此长文档解析不会因为"太久没动静"被回收重跑。
            await context.ReportProgressAsync(
                snapshot.Progress,
                string.IsNullOrWhiteSpace(snapshot.ProcessingStage) ? null : snapshot.ProcessingStage,
                cancellationToken);
        }

        try
        {
            await processing;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw;   // 内部超时之类，照常交给调度器判定
        }
        catch (OperationCanceledException)
        {
            // 被取消：解析已经不做了，资料不能永远停在"处理中"。
            // 标成 Failed 而不是 Queued，是因为补投器看到 Queued 会自动重新排任务，
            // 那等于把用户的取消当没发生。
            await BestEffortFailAsync(documentId);
            throw;
        }

        var after = await store.GetDocumentAsync(documentId, cancellationToken);
        if (after is null)
            return "资料在解析过程中被删除。";

        if (string.Equals(after.Status, "Failed", StringComparison.Ordinal))
        {
            // 还有重试机会：把资料放回队列，下一次尝试才有东西可抢；
            // 否则重试会读到 Failed 并立刻放弃。
            //
            // 已经用尽次数：资料就留在 Failed（这才是用户该看到的状态），
            // 否则它会永远停在“排队中”而永远没人处理。
            if (context.Job.Attempts < context.Job.MaxAttempts)
                await store.ResetDocumentToQueuedAsync(documentId, cancellationToken);

            throw new JobExecutionException(after.Error ?? "资料解析失败（未提供原因）。");
        }

        return JsonSerializer.Serialize(new
        {
            documentId,
            status = after.Status,
            chunkCount = after.ChunkCount
        });
    }

    /// <summary>取消时把资料标成失败；这里失败也不能掩盖原始的取消异常。</summary>
    private async Task BestEffortFailAsync(Guid documentId)
    {
        try
        {
            await store.SetDocumentStatusAsync(documentId, "Failed", "任务已被取消。", CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to mark document {DocumentId} as failed after cancellation", documentId);
        }
    }

    private static bool TryReadDocumentId(string? payload, out Guid documentId)
    {
        documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        try
        {
            var node = JsonNode.Parse(payload);
            var value = node?["documentId"]?.GetValue<string>();
            return Guid.TryParse(value, out documentId) && documentId != Guid.Empty;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>
/// 资料解析任务的「补投器」：从 <c>kb_documents.status = Queued</c> 推导出任务。
///
/// <para>
/// 这样做而不是在"上传接口里顺手入队"，是为了让任务只可能有一个来源：
/// 上传时入队漏了、进程崩了、插件直接写库了，都靠下一轮补投自动补齐；
/// 重复补投被幂等键（<c>document.ingest:{documentId}</c>）挡住，不会重复解析。
/// </para>
/// </summary>
public sealed class DocumentIngestJobFeeder(
    KnowledgeBaseStore store,
    IJobQueue queue,
    Microsoft.Extensions.Options.IOptions<JobOptions> options,
    ILogger<DocumentIngestJobFeeder> logger) : IJobFeeder
{
    /// <summary>每轮最多补投多少条：避免积压很多时一次性把队列塞满。</summary>
    private const int BatchSize = 50;

    public async Task<int> FeedAsync(CancellationToken cancellationToken)
    {
        // 排队上限：异常循环（或用户一次性倒入几千篇）不该把库和内存一起拖垮。
        var counts = await queue.CountAsync(cancellationToken);
        if (counts.Queued >= options.Value.MaxQueuedJobs) return 0;

        var pending = await store.ListQueuedDocumentIdsAsync(BatchSize, cancellationToken);
        if (pending.Count == 0) return 0;

        var created = 0;
        foreach (var entry in pending)
        {
            var payload = JsonSerializer.Serialize(new { documentId = entry.DocumentId });

            var job = await queue.EnqueueAsync(
                new EnqueueJobCommand(
                    JobTypes.DocumentIngest,
                    payload,
                    JobPriorities.High,
                    entry.KnowledgeBaseId,
                    entry.DocumentId,
                    IdempotencyKey: $"{JobTypes.DocumentIngest}:{entry.DocumentId:D}"),
                cancellationToken);

            if (!job.IsTerminal)
            {
                // Attempts == 0 说明是本次新建的；已在排队/运行的任务不重复计数。
                if (job.Attempts == 0 && job.Status == JobStatuses.Queued) created++;
                continue;
            }

            // 幂等键命中了已结束的旧任务。这里有两种完全相反的用户意图，必须区分：
            //
            // 1) 任务结束<b>之后</b>资料又被重新排队（用户点了“重新解析”、或上传了新版本）
            //    —— 说明用户想要它再跑一次，把旧任务重新武装。
            // 2) 资料在任务结束<b>之前</b>就是排队状态（典型：用户取消了任务）
            //    —— 说明用户不想让它跑，此时若自动重新武装，取消就等于没取消。
            if (entry.UpdatedAt > (job.CompletedAt ?? DateTimeOffset.MinValue))
            {
                if (await queue.RetryAsync(job.Id, cancellationToken) is not null) created++;
            }
            else if (job.Status == JobStatuses.Cancelled)
            {
                await store.SetDocumentStatusAsync(
                    entry.DocumentId, "Failed", "任务已被取消。", cancellationToken);
            }
            else
            {
                // 兜底：任务已结束但资料仍排队（例如旧版本升级上来的数据），让它再跑一次。
                if (await queue.RetryAsync(job.Id, cancellationToken) is not null) created++;
            }
        }

        if (created > 0)
            logger.LogInformation("Enqueued {Count} document ingestion job(s)", created);

        return created;
    }
}
