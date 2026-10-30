using MyFirstMafAgent.Api.Application;
using MyFirstMafAgent.Api.Application.Health;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 连接池自愈看门狗：定期确认「应用还能从池里拿到连接」，一旦确认拿不到就主动结束进程，
/// 交给外层（桌面端 Electron / Docker <c>restart</c> / systemd）重新拉起，从而重建连接池。
///
/// <para>
/// <b>为什么需要它。</b>实测故障（2026-10-30，桌面版）：API 进程活着、<c>/api/desktop/info</c>
/// 稳定 200，但所有查库请求都失败；<c>pg_stat_activity</c> 里 16 条（= <c>MaxPoolSize</c>）
/// 全部 <c>idle</c>、无活动查询、无事务，操作系统层面也确认这 16 个 socket 都被 API 进程持有。
/// 也就是说：连接被某些长寿命对象扣住、再也还不回池，池永久为空。
/// 这种状态<b>不会自己恢复</b>——用户的感受就是「挂一会儿就连不上，只能重启」。
/// </para>
///
/// <para>
/// <b>为什么是重启进程而不是别的办法。</b>池里每一条连接都被某个托管对象持有，
/// 进程内没有任何 API 能强制收回「别人手里的连接」（<c>NpgsqlConnection.ClearPool</c>
/// 只清理空闲连接，对这种情况无效）。唯一可靠的回收方式就是结束进程、让池随之消失。
/// 与其让用户手动关掉再打开，不如让程序自己做完这件事。
/// </para>
///
/// <para>
/// <b>必须同时看两个探针，否则会造成「数据库真挂了 → 无限重启」的死循环。</b>
/// 判定条件是「旁路探针成功 + 业务池探针失败」：
/// 前者证明 PostgreSQL 本身是好的（只是我们自己把池耗尽了），此时重启有意义；
/// 若两者都失败（数据库真不可用），重启也无济于事，只记日志、不重启、等它自己回来。
/// </para>
/// </summary>
public sealed class DatabasePoolWatchdog(
    IDatabasePoolProbe poolProbe,
    IDatabaseHealthProbe serverProbe,
    IConnectionReleaser connectionReleaser,
    IHostApplicationLifetime lifetime,
    ILogger<DatabasePoolWatchdog> logger) : BackgroundService
{
    /// <summary>巡检间隔。太快会自己制造负载，太慢则故障暴露得晚。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>连续判定多少次「池不可用但数据库正常」才动手。默认 4 次 ≈ 1 分钟。</summary>
    private const int FailureThreshold = 4;

    /// <summary>本次进程只因自愈重启过一次就不再重启，避免在彻底坏掉的环境里无限重启。</summary>
    private int _selfHealAttempts;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        BackgroundLoop.RunIsolatedAsync(RunAsync, stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        // 启动阶段本来就是抢连接的高峰（建表、加载索引、启动 Worker），
        // 先让系统跑稳一分钟再开始判定，避免拿启动瞬间的失败当故障。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var consecutivePoolFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await poolProbe.CheckAsync(stoppingToken);
                if (consecutivePoolFailures > 0)
                {
                    logger.LogInformation(
                        "业务连接池恢复正常（此前连续 {Count} 次不可用）",
                        consecutivePoolFailures);
                    consecutivePoolFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception poolException)
            {
                consecutivePoolFailures += 1;

                // 顺手做一次「归还当前上下文连接」：多数情况下真正卡住的是某个后台循环，
                // 这一步能让它立刻释放；真正顽固的池耗尽仍会继续失败。
                connectionReleaser.Release();

                if (consecutivePoolFailures < FailureThreshold)
                {
                    logger.LogWarning(
                        poolException,
                        "业务连接池取连接失败（{Count}/{Threshold}），继续观察",
                        consecutivePoolFailures,
                        FailureThreshold);
                }
                else
                {
                    await HandleExhaustedPoolAsync(poolException, stoppingToken);
                    consecutivePoolFailures = 0;
                }
            }
            finally
            {
                // 看门狗自己也是长寿命上下文：探完必须把连接还回去，
                // 否则它就成了新的泄漏源（一轮一条，永不释放）。
                connectionReleaser.Release();
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task HandleExhaustedPoolAsync(Exception poolException, CancellationToken stoppingToken)
    {
        // 关键判别：数据库本身是否还活着。
        try
        {
            await serverProbe.CheckAsync(stoppingToken);
        }
        catch (Exception serverException)
        {
            // 两个都失败 = 数据库真的不可用。重启进程没有任何帮助，只记日志。
            logger.LogError(
                serverException,
                "数据库不可用（独立探针同样失败），跳过自愈重启，等数据库恢复");
            return;
        }

        if (_selfHealAttempts >= 1)
        {
            logger.LogCritical(
                poolException,
                "业务连接池仍被耗尽，且本次进程已自愈重启过 1 次；不再重启，避免无限循环。" +
                "请反馈：{DataRoot} 下的日志与 PostgreSQL 的 pg_stat_activity 现场",
                AppContext.BaseDirectory);
            return;
        }

        _selfHealAttempts += 1;

        logger.LogCritical(
            poolException,
            "业务连接池已被耗尽（数据库本身正常）。主动结束进程以便重建连接池；" +
            "桌面端会由看门狗自动拉起，容器/服务由 restart 策略拉起");

        lifetime.StopApplication();
    }
}
