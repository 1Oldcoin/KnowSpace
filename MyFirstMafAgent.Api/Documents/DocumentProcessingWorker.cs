using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Documents;

public sealed class DocumentProcessingWorker(
    IServiceScopeFactory scopeFactory,
    KnowledgeBaseStore knowledgeBase,
    ILogger<DocumentProcessingWorker> logger) : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(5);

    // 必须在独立 ExecutionContext 中运行，否则会与其它 Worker 共用同一条数据库连接。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var document = await knowledgeBase.TryClaimQueuedDocumentAsync(stoppingToken);
                if (document is null)
                {
                    await Task.Delay(IdleDelay, stoppingToken);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<KnowledgeIngestionService>();
                await ingestion.ProcessAsync(document, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The document processing worker encountered an error.");
                try
                {
                    await Task.Delay(ErrorDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
