using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Jobs;

namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>
/// 任务调度器：N 个并发槽位，每个槽位在自己的 ExecutionContext 里循环领取并执行任务。
///
/// <para>
/// 为什么是「固定槽位」而不是「来一个任务开一个线程」：
/// 无限并发会把 CPU、数据库连接和内存同时打满，最后连健康检查都超时。
/// 并发度必须是一个可配置的上限，由运维按机器能力决定。
/// </para>
///
/// <para>
/// 每个槽位通过 <see cref="BackgroundLoop.RunIsolatedAsync"/> 启动，
/// 因此各自持有独立的 SqlSugar 连接；这是并发不出现
/// <c>A command is already in progress</c> 的前提。
/// </para>
/// </summary>
public sealed class JobScheduler(
    IJobQueue queue,
    IServiceScopeFactory scopeFactory,
    JobHandlerRegistry handlers,
    IEnumerable<IJobFeeder> feeders,
    JobWakeSignal wake,
    JobTypeQuota quota,
    IOptions<JobOptions> options,
    ILogger<JobScheduler> logger) : BackgroundService
{
    private readonly string _workerId = "w_" + Environment.ProcessId + "_" + Guid.NewGuid().ToString("N")[..6];

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogWarning("Job scheduler is disabled (Jobs:Enabled=false); jobs will stay queued.");
            return Task.CompletedTask;
        }

        var laneCount = Math.Clamp(settings.MaxConcurrentJobs, 1, 32);
        logger.LogInformation(
            "Job scheduler starting with {Lanes} lane(s), poll {PollMs} ms, lease {Lease}s, handlers: {Handlers}",
            laneCount, settings.PollIntervalMs, settings.LeaseSeconds,
            string.Join(", ", handlers.Types.DefaultIfEmpty("(none)")));

        if (settings.TypeLimits is { Count: > 0 })
        {
            logger.LogInformation("Job type limits: {Limits}",
                string.Join(", ", settings.TypeLimits.Select(pair => $"{pair.Key}<={pair.Value}")));
        }

        return StartLanesAsync(laneCount, stoppingToken);
    }

    private async Task StartLanesAsync(int laneCount, CancellationToken stoppingToken)
    {
        await FailUnregisteredJobsAsync(stoppingToken);

        // 每个槽位一趟独立的 ExecutionContext：SqlSugar 的 AsyncLocal 连接缓存因此互不共享。
        await Task.WhenAll(Enumerable
            .Range(0, laneCount)
            .Select(index => BackgroundLoop.RunIsolatedAsync(
                token => LaneAsync($"{_workerId}_l{index}", token),
                stoppingToken)));
    }

    /// <summary>
    /// 把「队列里存在但没有处理器」的任务直接判死。
    ///
    /// <para>
    /// 这种任务只可能来自版本升级（旧类型被移除）或手工写库，它们会永久占着排队位且永远不会被执行。
    /// 宁可在启动时明确失败并附上原因，也不要让用户在界面上看到一个永远不动的「排队中」。
    /// </para>
    /// </summary>
    private async Task FailUnregisteredJobsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<JobRecord> queued;
        try
        {
            queued = await queue.ListAsync(new JobQuery(JobStatuses.Queued, Limit: 500), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to scan queued jobs for unregistered types");
            return;
        }

        foreach (var job in queued.Where(job => !handlers.IsRegistered(job.Type)))
        {
            logger.LogWarning("Job {JobId} has unregistered type {Type}; marking it failed", job.Id, job.Type);
            await queue.FailAsync(job.Id, _workerId,
                JobFailure.FatalError($"没有为任务类型 '{job.Type}' 注册处理器。"), cancellationToken);
        }
    }

    private async Task LaneAsync(string workerId, CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var lease = TimeSpan.FromSeconds(settings.LeaseSeconds);
        var pollInterval = TimeSpan.FromMilliseconds(Math.Max(200, settings.PollIntervalMs));
        var feederInterval = TimeSpan.FromSeconds(Math.Max(0, settings.FeederIntervalSeconds));
        var purgeInterval = TimeSpan.FromMinutes(Math.Max(1, settings.PurgeIntervalMinutes));
        var staleGrace = TimeSpan.FromSeconds(Math.Max(5, settings.LeaseSeconds / 2));

        var lastFedAt = DateTimeOffset.MinValue;
        var lastPurgedAt = DateTimeOffset.MinValue;
        var lastRecoveredAt = DateTimeOffset.MinValue;
        var types = handlers.Types.ToList();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;

                if (now - lastRecoveredAt >= feederInterval)
                {
                    lastRecoveredAt = now;
                    await queue.RecoverStaleAsync(staleGrace, stoppingToken);
                }

                if (now - lastFedAt >= feederInterval)
                {
                    lastFedAt = now;
                    var fed = await FeedAsync(stoppingToken);
                    if (fed > 0) wake.Notify(fed);
                }

                if (now - lastPurgedAt >= purgeInterval)
                {
                    lastPurgedAt = now;
                    var cutoff = now.AddHours(-settings.RetentionHours);
                    var purged = await queue.PurgeAsync(cutoff, stoppingToken);
                    if (purged > 0) logger.LogInformation("Purged {Count} finished job(s) older than {Cutoff:O}", purged, cutoff);
                }

                if (!await RunOneAsync(workerId, types, lease, stoppingToken))
                {
                    // 空闲：等信号（有人入队/有任务完成）或超时，后者用于兜底捞取到期的重试任务。
                    await wake.WaitAsync(pollInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Job scheduler lane {WorkerId} encountered an error", workerId);

                try
                {
                    await Task.Delay(pollInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Job scheduler lane {WorkerId} stopped", workerId);
    }

    /// <summary>
    /// 领一个任务并执行；返回 false 表示这一轮什么都没领到（调用方去睡觉等信号）。
    ///
    /// <para>
    /// 两条路径：
    /// <list type="bullet">
    /// <item>没有配 <c>Jobs:TypeLimits</c>：一次领取所有类型，队列按
    /// <c>Priority, NextRunAt, CreatedAt</c> 排序挑——即"全局优先级"（旧行为，零改动）。</item>
    /// <item>配了类型上限：<b>先占坑再领取</b>。占不到坑的类型这一轮直接跳过，
    /// 因此上限是硬上限；代价是领取粒度变成一个类型，跨类型的优先级退化为
    /// "按类型依次尝试"，类型内的优先级照旧。</item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<bool> RunOneAsync(
        string workerId,
        List<string> types,
        TimeSpan lease,
        CancellationToken stoppingToken)
    {
        var candidates = quota.EligibleTypes(types);
        if (candidates.Count == 0)
        {
            logger.LogDebug("All job type quotas are exhausted; lane waits for a slot to free up");
            return false;
        }

        if (!quota.HasLimits)
        {
            var single = await queue.ClaimAsync(workerId, candidates, lease, stoppingToken);
            if (single is null) return false;

            await ExecuteAsync(single, lease, stoppingToken);
            return true;
        }

        var ran = false;
        foreach (var type in candidates)
        {
            // 占坑失败说明这个类型已经跑满，换下一个候选类型。
            if (!quota.TryReserve(type)) continue;

            try
            {
                var single = await queue.ClaimAsync(workerId, [type], lease, stoppingToken);
                if (single is null) continue;   // 这个类型暂时没活儿，让给下一个候选类型

                ran = true;
                await ExecuteAsync(single, lease, stoppingToken);
            }
            finally
            {
                // 无论成功、失败还是抛异常，名额必须归还。
                quota.Release(type);
            }

            // 只有「真的跑完了一个任务」才唤醒别人：否则两个空闲槽位会互相唤醒成热循环
            // （实测：队列空时每秒产生上千次领取查询）。
            if (ran) wake.Notify();

            break;   // 一次只跑一个任务，跑完回外层循环重新算配额
        }

        return ran;
    }

    private async Task<int> FeedAsync(CancellationToken stoppingToken)
    {
        var total = 0;
        foreach (var feeder in feeders)
        {
            try
            {
                total += await feeder.FeedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 单个补投器失败不能拖垮调度：下一轮再试。
                logger.LogError(exception, "Job feeder {Feeder} failed", feeder.GetType().Name);
            }
        }

        return total;
    }

    private async Task ExecuteAsync(JobLease lease, TimeSpan leaseDuration, CancellationToken stoppingToken)
    {
        var job = lease.Job;

        // 每个任务一个 DI 作用域：处理器之间不共享任何 scoped 状态。
        using var scope = scopeFactory.CreateAsyncScope();
        var handler = JobHandlerRegistry.Resolve(scope.ServiceProvider, job.Type);
        if (handler is null)
        {
            await queue.FailAsync(job.Id, lease.WorkerId,
                JobFailure.FatalError($"没有为任务类型 '{job.Type}' 注册处理器。"), stoppingToken);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var context = new JobExecutionContext(job, (progress, message, token) =>
            queue.ReportProgressAsync(job.Id, lease.WorkerId, progress, message, token));

        var startedAt = DateTimeOffset.UtcNow;
        Task<string?> execution;
        try
        {
            execution = handler.ExecuteAsync(context, cancellation.Token);
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(lease, exception, stoppingToken);
            return;
        }

        // 心跳与执行赛跑：既续租（避免被误判为失联而重复执行），也读取取消请求。
        //
        // 心跳间隔既不能太密（白跑数据库），也不能太疏：取消请求是靠心跳读回来的，
        // 按租约/3 算的话 120 秒租约要等 40 秒才响应取消，用户会以为点了没反应。
        // 因此上限固定 5 秒——取消的响应时间不该随租约变长。
        var heartbeatInterval = TimeSpan.FromSeconds(
            Math.Clamp(leaseDuration.TotalSeconds / 3, 1, 5));
        while (!execution.IsCompleted && !stoppingToken.IsCancellationRequested)
        {
            var tick = Task.Delay(heartbeatInterval, stoppingToken);
            var finished = await Task.WhenAny(execution, tick);
            if (finished == execution) break;

            try
            {
                var cancelRequested = await queue.HeartbeatAsync(job.Id, lease.WorkerId, leaseDuration, stoppingToken);
                if (cancelRequested)
                {
                    logger.LogInformation("Job {JobId} cancellation requested; interrupting", job.Id);
                    await cancellation.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Job {JobId} heartbeat failed", job.Id);
            }
        }

        try
        {
            var result = await execution;
            await queue.CompleteAsync(job.Id, lease.WorkerId, result, WriteToken());

            logger.LogInformation(
                "Job {JobId} ({Type}) succeeded in {Elapsed} ms",
                job.Id, job.Type, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停机：不写失败，租约过期后由其它进程/下次启动回收。
            logger.LogInformation("Job {JobId} interrupted by shutdown; it will be recovered", job.Id);
        }
        catch (OperationCanceledException)
        {
            await queue.FailAsync(job.Id, lease.WorkerId, JobFailure.FromCancel(), WriteToken());
        }
        catch (JobExecutionException exception)
        {
            await queue.FailAsync(job.Id, lease.WorkerId,
                new JobFailure(exception.Message, Cancelled: false, Fatal: exception.Fatal), WriteToken());
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(lease, exception, WriteToken());
        }
    }

    private async Task HandleFailureAsync(JobLease lease, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Job {JobId} ({Type}) failed", lease.Job.Id, lease.Job.Type);

        var failure = exception is JobExecutionException jobException
            ? new JobFailure(jobException.Message, Cancelled: false, Fatal: jobException.Fatal)
            : new JobFailure($"{exception.GetType().Name}: {exception.Message}");

        try
        {
            await queue.FailAsync(lease.Job.Id, lease.WorkerId, failure, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停机期间无法写回，交给租约回收。
        }
        catch (Exception writeException)
        {
            logger.LogError(writeException, "Failed to record failure for job {JobId}", lease.Job.Id);
        }
    }

    /// <summary>
    /// 写回最终状态时用独立令牌：停机时那次写回不应该被自身的取消令牌打断，
    /// 否则任务会卡在 Running 直到租约过期（多绕一圈才被回收）。
    /// </summary>
    private static CancellationToken WriteToken() => CancellationToken.None;
}
