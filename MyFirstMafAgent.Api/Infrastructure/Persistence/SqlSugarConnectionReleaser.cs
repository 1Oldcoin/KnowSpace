using SqlSugar;

namespace MyFirstMafAgent.Api.Application;

/// <summary>
/// <see cref="IConnectionReleaser"/> 的 SqlSugar 实现。
///
/// <para>
/// <c>Ado.Close()</c> 关掉的是**当前执行上下文**（<c>AsyncLocal</c> 缓存）那条连接，
/// 并把物理连接还给 Npgsql 连接池。没有开启事务时调用是安全的空操作。
/// </para>
///
/// <para>
/// 故意做成 <see cref="IDisposable"/> 之外的无状态单例：这里只借用 <c>SqlSugarScope</c>
/// 的关闭能力，不持有任何连接。
/// </para>
/// </summary>
public sealed class SqlSugarConnectionReleaser(SqlSugarScope database, ILogger<SqlSugarConnectionReleaser> logger)
    : IConnectionReleaser
{
    public void Release()
    {
        try
        {
            // 事务还开着时不能关：那会把未提交的改动连同连接一起丢掉。
            if (database.Ado.Transaction is not null) return;

            database.Ado.Close();
        }
        catch (Exception exception)
        {
            // 释放失败不该影响业务循环：下一轮还会再试一次。
            logger.LogDebug(exception, "Releasing the pooled connection failed");
        }
    }

    public bool ReleaseAfterRequest()
    {
        var hadResidualTransaction = false;

        try
        {
            // 请求已经结束，还开着的事务不可能再被提交：回滚是唯一正确的处置。
            // 不回滚直接关连接，Npgsql 也会隐式回滚，但那样我们什么都看不见；
            // 显式回滚 + 返回 true，才能在上层留下一条可追查的警告。
            if (database.Ado.Transaction is not null)
            {
                hadResidualTransaction = true;
                database.Ado.RollbackTran();
            }

            database.Ado.Close();
        }
        catch (Exception exception)
        {
            // 兜底失败也必须继续关连接：宁可把这条连接废弃，也不要留在池里当垃圾。
            logger.LogDebug(exception, "Rolling back / releasing the request connection failed");

            try
            {
                database.Ado.Close();
            }
            catch (Exception closeException)
            {
                logger.LogDebug(closeException, "Closing the request connection failed");
            }
        }

        return hadResidualTransaction;
    }
}
