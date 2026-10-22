namespace MyFirstMafAgent.Api.Application;

/// <summary>
/// 后台 Worker 的执行上下文隔离器。
///
/// <para>
/// SqlSugar 的 <c>SqlSugarScope</c> 会把「当前作用域的 <c>SqlSugarClient</c>（连带它的 Npgsql 连接）」
/// 缓存在 <c>AsyncLocal</c> 里（见 SqlSugar 的 <c>CallContextAsync&lt;T&gt;</c>）。
/// 宿主启动时，多个 <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> 的
/// <c>ExecuteAsync</c> 是在<b>同一个 ExecutionContext</b>（宿主启动上下文）中被依次同步调用的，
/// 因此第一个 Worker 建立的上下文缓存会被后续 Worker 命中，多个长期循环最终共用同一条数据库连接。
/// 一旦并发触发 SQL，就会抛出
/// <c>NpgsqlOperationInProgressException: A command is already in progress</c>。
/// </para>
///
/// <para>
/// 让每个 Worker 在独立的 ExecutionContext 中运行，SqlSugar 便会为各自创建独立的连接。
/// 同时把循环从启动线程挪到线程池，避免长时间运行的循环阻塞宿主启动。
/// </para>
///
/// <para>
/// 这是所有长期后台循环的强制入口：不要用 <c>Task.Run</c> 或直接调用循环方法替代。
/// 验证必须在真实宿主上进行（单元测试无法复现该并发问题）。
/// </para>
/// </summary>
public static class BackgroundLoop
{
    /// <summary>在独立 ExecutionContext 中启动后台循环。</summary>
    public static Task RunIsolatedAsync(Func<CancellationToken, Task> loop, CancellationToken stoppingToken)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => loop(stoppingToken), CancellationToken.None);
    }
}
