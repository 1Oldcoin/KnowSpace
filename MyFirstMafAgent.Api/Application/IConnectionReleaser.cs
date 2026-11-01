namespace MyFirstMafAgent.Api.Application;

/// <summary>
/// 把「当前执行上下文持有的数据库连接」还给连接池。
///
/// <para>
/// 存在的理由（这是一次真实故障的修复）：
/// <c>SqlSugarScope</c> 把每份 <c>SqlSugarClient</c>（连带它的 Npgsql 物理连接）缓存在
/// <c>AsyncLocal</c> 里。<c>IsAutoCloseConnection=true</c> 能在**普通查询**之后把连接还回池，
/// 但后台 Worker 与任务调度槽位是**长寿命执行上下文**：它们在一个 <c>while</c> 循环里
/// 永远不退出，中途还有「任务执行」与「心跳续租」两条并发路径，
/// 连接一旦被其中一条借走就会一直挂在这个上下文上。
/// </para>
///
/// <para>
/// 实测后果：桌面版挂机约 25 分钟后，PostgreSQL 里堆积 16 个（= <c>MaxPoolSize</c>）
/// 永远 <c>idle</c> 的会话，连接池再没有空位，于是 <b>任何</b> 查库请求都会等满
/// <c>Timeout</c> 秒然后失败——表现就是「Electron 连不上 API」，而 API 进程本身还活着
/// （<c>/api/desktop/info</c> 依然 200）。实测 16 条连接的最后语句里 12 条都是任务队列的
/// <c>ClaimAsync</c> 查询，直指调度槽位。
/// </para>
///
/// <para>
/// 所以：<b>所有长寿命后台循环必须在每一轮结束时调用它</b>。
/// 它不是"优化"，是防止连接池被慢性占满的必要动作。
/// </para>
/// </summary>
public interface IConnectionReleaser
{
    /// <summary>关闭当前上下文的连接并归还连接池；没有连接时是空操作。</summary>
    void Release();

    /// <summary>
    /// 请求结束时调用：把当前上下文的连接**无条件**还给连接池，
    /// 如果发现事务还开着，先回滚再关。
    ///
    /// <para>
    /// 为什么不能直接复用 <see cref="Release"/>：那个方法遇到事务会主动跳过（以免把业务没提交的
    /// 改动连带连接一起丢掉，这在后台循环的重试里是对的）。但请求已经结束了，还开着的事务就是
    /// 纯纯粹粹的泄漏——既不会有人提交，也不会有人回滚，连接就这么被扣住。
    /// 实测：连做 10 次「开事务不提交」，后端连接从 3 条涨到 13 条，一条不落。
    /// </para>
    ///
    /// <para>返回 <c>true</c> 表示发现并清理了残留事务；调用方应该记一条警告。</para>
    /// </summary>
    bool ReleaseAfterRequest();
}
