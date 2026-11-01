using MyFirstMafAgent.Api.Application;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 请求级连接兜底：每个 HTTP 请求结束（正常返回、抛异常、被客户端取消）时，
/// 把这条请求借走的数据库连接还给连接池。
///
/// <para>
/// <b>它解决什么。</b>连接池被耗尽以后的症状极具误导性：API 进程活着、
/// <c>/api/desktop/info</c> 照常 200，但每个查库的请求都要排队等满
/// <c>Timeout</c> 秒才失败。<c>pg_stat_activity</c> 里能看到的只是
/// <c>MaxPoolSize</c> 条 <c>idle</c> 会话，<b>0 条 active、0 条 idle in transaction</b>——
/// 从数据库的角度看一切正常，因为「池里闲置的连接」和「被借出去后闲置的连接」
/// 在服务端长得一模一样。唯一的判据在客户端：第 N+1 个请求拿不到槽位。
/// </para>
///
/// <para>
/// <b>它主要防的是一个已经被实测复现的泄漏：半开事务。</b>
/// 连做 10 次「BeginTran 之后既没 Commit 也没 Rollback」，后端连接从 3 条涨到 13 条，
/// 一条不落。正常的 <c>Begin → Commit</c> / <c>Begin → Rollback</c> 是平衡的，
/// 但只要有任何一个分支（尤其是异常分支）漏掉收尾，槽位就永久少一个。
/// 请求结束点在这里，是最后一道能兜住它的地方。
/// </para>
///
/// <para>
/// <b>诚实说明它的有效边界。</b><c>SqlSugarScope</c> 把客户端缓存在
/// <c>AsyncLocal</c> 里，而 <c>AsyncLocal</c> 的值只会向下传给子上下文、不会向上回传。
/// 如果某个端点在它自己的子上下文里新建了连接，本中间件在当前上下文里
/// 未必能拿到同一份对象（实验里在 <c>Task.Run</c> 隔离的子上下文中确实碰不到）。
/// 所以它<b>不是万灵药</b>，而是一道低成本、无副作用的保险：
/// 能碰到的就关掉，碰不到的仍然由 <c>DatabasePoolWatchdog</c> 兜底重启。
/// </para>
/// </summary>
public static class ConnectionReleaseMiddleware
{
    public static void UseConnectionRelease(this WebApplication app)
    {
        var releaser = app.Services.GetRequiredService<IConnectionReleaser>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.ConnectionRelease");

        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            finally
            {
                // 无论成功、失败、还是被取消，都必须走到这里。
                try
                {
                    if (releaser.ReleaseAfterRequest())
                    {
                        // 残留事务 = 某条分支漏了 Commit/Rollback。这条日志就是抓它的钩子：
                        // 出现它就说明那个端点有 bug，而且我们已经替它收尾了。
                        logger.LogWarning(
                            "请求结束时仍有未提交的事务，已回滚并归还连接：{Method} {Path} → {StatusCode}",
                            context.Request.Method,
                            context.Request.Path.Value,
                            context.Response.StatusCode);
                    }
                }
                catch (Exception exception)
                {
                    // 兜底本身绝不能让一个已经成功的响应变成 500。
                    logger.LogDebug(exception, "Endpoint-scoped connection release failed");
                }
            }
        });
    }
}
