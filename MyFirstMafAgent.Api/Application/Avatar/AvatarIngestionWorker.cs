using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Infrastructure;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 分身投喂任务 Worker。轮询 avatar_ingestions，异步完成记忆整理，
/// 不阻塞问答，也不影响知识库的 DocumentProcessingWorker。
/// </summary>
public sealed class AvatarIngestionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AvatarIngestionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    // 必须在独立 ExecutionContext 中运行，否则会与其它 Worker 共用同一条数据库连接。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Avatar ingestion worker started.");

        await RequeueStaleAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<IAvatarRepository>();
                var ingestion = await repository.ClaimNextIngestionAsync(stoppingToken);
                if (ingestion is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                var processor = scope.ServiceProvider.GetRequiredService<AvatarIngestionService>();
                await processor.ProcessAsync(ingestion, stoppingToken);
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
        }

        logger.LogInformation("Avatar ingestion worker stopped.");
    }

    /// <summary>进程崩溃可能留下 running 任务；启动时把超时任务重新排队，原文与已写入证据不会重复。</summary>
    private async Task RequeueStaleAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IAvatarRepository>();
            var count = await repository.RequeueStaleIngestionsAsync(
                TimeSpan.FromMinutes(10), stoppingToken);
            if (count > 0)
                logger.LogWarning("Requeued {Count} stale avatar ingestion tasks.", count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to requeue stale avatar ingestion tasks.");
        }
    }
}
