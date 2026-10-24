using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Domain.Jobs;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 任务中心：查看排队/运行中的长任务，取消、重跑，以及实时进度推送。
///
/// <para>
/// 这里的每个端点都是「观察与控制」，不是「执行」：
/// 真正的执行永远由调度器在后台槽位里完成，端点不会因为某个任务慢而被拖住。
/// </para>
/// </summary>
public static class JobEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/jobs").WithTags("任务中心");

        group.MapGet("", async (
            string? status,
            string? type,
            Guid? subjectId,
            int? limit,
            IJobQueue queue,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                jobs = await queue.ListAsync(
                    new JobQuery(status, type, subjectId, limit ?? 50), cancellationToken)
            }));

        group.MapGet("/counts", async (IJobQueue queue, CancellationToken cancellationToken) =>
            Results.Ok(await queue.CountAsync(cancellationToken)));

        // 列出已注册的任务类型：前端据此填下拉框，也避免手工猜类型名猜错。
        group.MapGet("/types", (JobHandlerRegistry handlers) =>
            Results.Ok(new { types = handlers.Types.OrderBy(type => type, StringComparer.Ordinal) }));

        // 按类型配额：limit=null 表示不单独限制，running 是当前占用（本进程）。
        group.MapGet("/quotas", (JobHandlerRegistry handlers, JobTypeQuota quota, IOptions<JobOptions> options) =>
            Results.Ok(new
            {
                maxConcurrentJobs = options.Value.MaxConcurrentJobs,
                quotas = quota.Snapshot(handlers.Types)
            }));

        group.MapGet("/{id}", async (string id, IJobQueue queue, CancellationToken cancellationToken) =>
        {
            var job = await queue.GetAsync(id, cancellationToken);
            return job is null ? Results.NotFound() : Results.Ok(job);
        });

        group.MapPost("", async (
            CreateJobRequest request,
            IJobQueue queue,
            JobHandlerRegistry handlers,
            JobWakeSignal wake,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Type))
                return Results.BadRequest(new { error = "type 必填。" });

            if (!handlers.IsRegistered(request.Type))
            {
                return Results.BadRequest(new
                {
                    error = $"未注册的任务类型：{request.Type}。可用类型：{string.Join("、", handlers.Types)}"
                });
            }

            var job = await queue.EnqueueAsync(
                new EnqueueJobCommand(
                    request.Type,
                    request.Payload?.ToJsonString(),
                    request.Priority ?? JobPriorities.Normal,
                    request.KnowledgeBaseId,
                    request.SubjectId,
                    request.IdempotencyKey),
                cancellationToken);

            wake.Notify();
            return Results.Ok(job);
        });

        group.MapPost("/{id}/cancel", async (
            string id,
            IJobQueue queue,
            JobWakeSignal wake,
            CancellationToken cancellationToken) =>
        {
            var job = await queue.GetAsync(id, cancellationToken);
            if (job is null) return Results.NotFound();

            if (!JobTransitions.CanCancel(job.Status))
                return Results.Conflict(new { error = $"任务已结束（{job.Status}），无法取消。", job });

            await queue.RequestCancelAsync(id, cancellationToken);
            wake.Notify();
            return Results.Ok(await queue.GetAsync(id, cancellationToken));
        });

        group.MapPost("/{id}/retry", async (
            string id,
            IJobQueue queue,
            JobWakeSignal wake,
            CancellationToken cancellationToken) =>
        {
            var job = await queue.GetAsync(id, cancellationToken);
            if (job is null) return Results.NotFound();

            var retried = await queue.RetryAsync(id, cancellationToken);
            if (retried is null)
                return Results.Conflict(new { error = $"任务当前状态为 {job.Status}，只有已结束的任务可以重跑。", job });

            wake.Notify();
            return Results.Ok(retried);
        });

        // 实时进度：SSE。比轮询省电，也比 WebSocket 简单（单向、可自动重连）。
        group.MapGet("/stream", async (
            HttpContext context,
            JobEventStream events,
            IJobQueue queue,
            CancellationToken cancellationToken) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.Headers.Connection = "keep-alive";
            // 反向代理（IIS/Nginx）默认缓冲会让 SSE 变成"攒一堆再发"，必须显式关闭。
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var subscription = events.Subscribe();
            try
            {
                // 首帧给一份快照：新连上来的客户端立刻有数据，不必等下一次状态变化。
                await WriteEventAsync(context, "snapshot",
                    JsonSerializer.Serialize(new
                    {
                        counts = await queue.CountAsync(cancellationToken),
                        jobs = await queue.ListAsync(new JobQuery(Limit: 20), cancellationToken)
                    }, JsonOptions), cancellationToken);

                using var keepAlive = new PeriodicTimer(TimeSpan.FromSeconds(15));
                var reader = subscription.Reader;

                while (!cancellationToken.IsCancellationRequested)
                {
                    var readTask = reader.WaitToReadAsync(cancellationToken).AsTask();
                    var tickTask = keepAlive.WaitForNextTickAsync(cancellationToken).AsTask();

                    if (await Task.WhenAny(readTask, tickTask) == tickTask)
                    {
                        // 注释行既是保活，也避免中间设备因长时间无数据而断连。
                        await context.Response.WriteAsync(": ping\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        continue;
                    }

                    if (!await readTask) break;

                    while (reader.TryRead(out var jobEvent))
                    {
                        await WriteEventAsync(context, jobEvent.Kind,
                            JsonSerializer.Serialize(jobEvent, JsonOptions), cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 客户端断开是正常结束。
            }
            finally
            {
                events.Unsubscribe(subscription.Id);
            }
        });
    }

    private static async Task WriteEventAsync(
        HttpContext context,
        string eventName,
        string json,
        CancellationToken cancellationToken)
    {
        var payload = new StringBuilder()
            .Append("event: ").Append(eventName).Append('\n')
            .Append("data: ").Append(json).Append("\n\n")
            .ToString();

        await context.Response.WriteAsync(payload, Encoding.UTF8, cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>新建任务请求（通用入队，类型必须是已注册的处理器）。</summary>
    public sealed record CreateJobRequest(
        string? Type,
        JsonNode? Payload,
        int? Priority,
        Guid? KnowledgeBaseId,
        Guid? SubjectId,
        string? IdempotencyKey);
}
