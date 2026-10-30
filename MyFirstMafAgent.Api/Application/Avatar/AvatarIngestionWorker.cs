using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 分身投喂任务 Worker。轮询 avatar_ingestions，异步完成记忆整理，
/// 不阻塞问答，也不影响知识库的 DocumentProcessingWorker。
/// </summary>
public sealed class AvatarIngestionWorker(
    IServiceScopeFactory scopeFactory,
    IConnectionReleaser connectionReleaser,
    ILogger<AvatarIngestionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    /// <summary>单条任务的最长处理时间。超时按失败结束，避免一条卡死的任务永久堵住队列。</summary>
    private static readonly TimeSpan ItemTimeout = TimeSpan.FromMinutes(5);

    /// <summary>空闲时回收「僵尸 running」的间隔。启动时回收一次，运行期定期兜底。</summary>
    private static readonly TimeSpan StaleSweepInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    // 必须在独立 ExecutionContext 中运行，否则会与其它 Worker 共用同一条数据库连接。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Avatar ingestion worker started.");

        await RequeueStaleAsync(stoppingToken);

        var lastSweep = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAvatarRepository>();
                var ingestion = await repository.ClaimNextIngestionAsync(stoppingToken);
                if (ingestion is null)
                {
                    // 兜底：进程异常退出、或某次处理在状态回写前崩溃，都会留下 running 坟头。
                    // 只在真正空闲时扫描，避免和正在跑的任务抢同一行。
                    if (DateTimeOffset.UtcNow - lastSweep >= StaleSweepInterval)
                    {
                        lastSweep = DateTimeOffset.UtcNow;
                        await RequeueStaleAsync(stoppingToken);
                    }

                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var processor = scope.ServiceProvider.GetRequiredService<AvatarIngestionService>();
                await ProcessWithTimeoutAsync(processor, repository, ingestion, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Avatar ingestion worker iteration failed.");
                try
                {
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                // 长寿命上下文：每轮结束必须归还连接，否则会在连接池里永久占一条（见 IConnectionReleaser）。
                connectionReleaser.Release();
            }
        }

        logger.LogInformation("Avatar ingestion worker stopped.");
    }

    /// <summary>
    /// 处理单条任务并施加超时。超时用独立的取消令牌；收尾（写失败状态）走 CancellationToken.None，
    /// 否则状态回写本身会被已取消的令牌带走，任务行会重新变成 running 坟头。
    /// </summary>
    private async Task ProcessWithTimeoutAsync(
        AvatarIngestionService processor,
        IAvatarRepository repository,
        AvatarIngestion ingestion,
        CancellationToken stoppingToken)
    {
        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        itemCts.CancelAfter(ItemTimeout);

        try
        {
            await processor.ProcessAsync(ingestion, itemCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Avatar ingestion {IngestionId} timed out after {Timeout}; marking it failed to unblock the queue.",
                ingestion.Id,
                ItemTimeout);

            ingestion.Fail(
                $"整理超时（超过 {ItemTimeout.TotalMinutes:0} 分钟），原文已保存，可以重试。",
                DateTimeOffset.UtcNow);
            await repository.UpdateIngestionAsync(ingestion, CancellationToken.None);
        }
    }

    /// <summary>进程崩溃可能留下 running 任务；启动时把超时任务重新排队，原文与已写入证据不会重复。</summary>
    private async Task RequeueStaleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAvatarRepository>();
            var count = await repository.RequeueStaleIngestionsAsync(StaleAfter, stoppingToken);
            if (count > 0)
                logger.LogWarning("Requeued {Count} stale avatar ingestion tasks.", count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to requeue stale avatar ingestion tasks.");
        }
    }
}
