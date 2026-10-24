using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>
/// 词法索引补分词后台任务：为历史切片补齐 <c>kb_chunks.lexical_tokens</c>。
///
/// <para>
/// 新切片在 <c>PublishChunksAsync</c> 落库时就带上分词，所以这个 Worker 正常只在
/// "老库第一次启动" 或 "分词规则变更后" 才有活干；空转时每轮只发一条
/// <c>LIMIT 1</c> 的探测查询，代价可以忽略。
/// </para>
///
/// <para>
/// 为什么必须放在后台：分词只能在应用侧做（PostgreSQL 默认解析器不认中文），
/// 一个几十万切片的库在启动流程里同步分词会让 API 启动卡住几分钟。
/// </para>
/// </summary>
public sealed class LexicalIndexWorker(
    KnowledgeBaseStore store,
    IOptions<IndexingOptions> options,
    ILogger<LexicalIndexWorker> logger) : BackgroundService
{
    /// <summary>每批处理的切片数量：批量太小会把数据库往返放大，太大则单事务过重。</summary>
    private const int BatchSize = 200;

    // 必须在独立 ExecutionContext 中运行，否则会与其它 Worker 共用同一条数据库连接。
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollIntervalSeconds * 15, 10, 120));
        var total = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await store.BackfillLexicalTokensAsync(BatchSize, stoppingToken);
                if (processed > 0)
                {
                    total += processed;
                    logger.LogInformation("Lexical token backfill: +{Processed} chunks (total {Total} in this run)", processed, total);
                    // 还有活就立刻继续，不做额外等待。
                    continue;
                }

                if (total > 0)
                {
                    logger.LogInformation("Lexical token backfill finished: {Total} chunk(s) tokenized", total);
                    total = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Lexical token backfill encountered an error.");
            }

            try
            {
                await Task.Delay(idleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
