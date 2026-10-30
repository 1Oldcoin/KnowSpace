using MyFirstMafAgent.Api.Application.Health;
using Npgsql;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 数据库健康探针：用一条**独立、不进连接池**的连接判断数据库是否可用。
///
/// <para>
/// 为什么不再借用共享的 <c>SqlSugarScope</c>：那个 scope 的当前连接来自业务连接池，
/// 一旦池被占满（后台长寿命循环慢性占用），探针就会跟着卡满 <c>Timeout</c> 秒再报 503。
/// 结果是「PostgreSQL 明明好好的，界面却显示离线」，而且掩盖了真正的原因
/// （实测：16 条连接全部 idle、无任何活动查询，<c>/api/health</c> 却稳定 15 秒后 503，
/// 而同一时刻 <c>psql</c> 用同样参数 <c>SELECT 1</c> 立刻成功）。
/// </para>
///
/// <para>
/// 用 <c>Pooling=false</c> 的独立连接后，探针只回答「数据库本身能不能连」，
/// 与业务连接池状态解耦：池满时探针仍然 200，界面上的问题就精确落回「业务请求失败」，
/// 不会再被误判成「后端挂了」而导致整个界面不可用。
/// </para>
/// </summary>
public sealed class SqlSugarDatabaseHealthProbe(string connectionString) : IDatabaseHealthProbe
{
    /// <summary>探针自己的超时：健康检查必须在几秒内给结论，不能跟着业务超时一起拖。</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly string _probeConnectionString = BuildProbeConnectionString(connectionString);

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        await using var connection = new NpgsqlConnection(_probeConnectionString);
        await connection.OpenAsync(timeout.Token);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        command.CommandTimeout = (int)ProbeTimeout.TotalSeconds;
        await command.ExecuteScalarAsync(timeout.Token);
    }

    /// <summary>
    /// 复用业务的连接信息（主机/端口/库/账号/口令），但强制关掉池化：
    /// 探针只连一次，池化只会让它继承池的故障状态。
    /// </summary>
    private static string BuildProbeConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            Timeout = (int)ProbeTimeout.TotalSeconds,
            CommandTimeout = (int)ProbeTimeout.TotalSeconds,
            ApplicationName = "MyFirstMafAgent.Health"
        };

        return builder.ConnectionString;
    }
}
