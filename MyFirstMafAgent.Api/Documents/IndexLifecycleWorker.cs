using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>
/// 索引生命周期后台任务：推进阴影索引构建、追赶活动索引、按回滚窗口清理旧索引。
/// 所有状态都在数据库中，API 重启后可以继续执行。
/// </summary>
public sealed class IndexLifecycleWorker(
    IndexLifecycleManager lifecycle,
    IOptions<IndexingOptions> options,
    ILogger<IndexLifecycleWorker> logger) : BackgroundService
{
    // 必须在独立 ExecutionContext 中运行，否则会与其它 Worker 共用同一条数据库连接。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds, 1, 60));
        var cleanupEvery = TimeSpan.FromMinutes(10);
        var lastCleanup = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await lifecycle.ProcessOnceAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - lastCleanup >= cleanupEvery)
                {
                    await lifecycle.CleanupAsync(stoppingToken);
                    lastCleanup = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Index lifecycle worker encountered an error.");
            }

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
}
