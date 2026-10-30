using MyFirstMafAgent.Api.Application.Health;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 走**业务连接池**的探针：证明应用真的还能查库。
///
/// <para>
/// 这里刻意不复用仓储、也不做任何业务查询，只发一条 <c>SELECT 1</c>。
/// 池被占满时它会卡在建连上（Npgsql 的 <c>Timeout</c>，已收紧到 5 秒），
/// 然后抛异常——这正是我们想要的信号：<b>数据库活着，但应用已经拿不到连接了</b>。
/// </para>
///
/// <para>
/// 注意它必须走 <c>SqlSugarScope</c>：那个 scope 按 ExecutionContext 缓存客户端，
/// 池耗尽时在这里同样拿不到连接。换任何「旁路」连接都测不出这个故障。
/// </para>
/// </summary>
public sealed class SqlSugarPoolProbe(SqlSugarScope database) : IDatabasePoolProbe
{
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        // 命令级超时也收紧：万一抢到了连接但对端不响应，不要拖满 120 秒的命令超时。
        database.Ado.CommandTimeOut = 5;

        var value = await database.Ado.GetScalarAsync("SELECT 1");
        if (value is null)
        {
            throw new InvalidOperationException("SELECT 1 returned no value");
        }
    }
}
