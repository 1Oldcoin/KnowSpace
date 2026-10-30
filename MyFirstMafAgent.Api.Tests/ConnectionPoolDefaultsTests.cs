using Microsoft.Extensions.Configuration;
using MyFirstMafAgent.Api;
using Npgsql;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 连接池参数的兜底逻辑。
///
/// <para>
/// 背景（实测于 2026-10，Npgsql 9.0.4）：桌面版跑了 1.4 小时后，
/// PostgreSQL 里出现 <b>50 个 idle 客户端会话</b>，全部 <c>ClientRead</c> 空闲、
/// 最长已空闲 1 小时 37 分，而实际峰值并发只有个位数。根因是
/// <c>MaxPoolSize = 50</c> 加上 Npgsql 默认 <c>ConnectionIdleLifetime = 0</c>（永不回收）：
/// SqlSugar 为每个新的 ExecutionContext 在 <c>AsyncLocal</c> 里建一条物理连接，
/// <c>IsAutoCloseConnection</c> 只还进池不关闭，于是「只增不减」直到撞上上限。
/// 这组测试锁住修复后的行为。
/// </para>
/// </summary>
public sealed class ConnectionPoolDefaultsTests
{
    private static IConfiguration Configuration(
        int maxPoolSize = 16,
        int idleLifetime = 300,
        int pruningInterval = 60) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PortablePostgres:MaxPoolSize"] = maxPoolSize.ToString(),
                ["PortablePostgres:ConnectionIdleLifetimeSeconds"] = idleLifetime.ToString(),
                ["PortablePostgres:ConnectionPruningIntervalSeconds"] = pruningInterval.ToString()
            })
            .Build();

    [Fact]
    public void Bare_connection_string_gets_the_pool_defaults()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "Host=db;Port=5432;Database=kb;Username=postgres;Password=p",
            Configuration());

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(16, builder.MaxPoolSize);
        Assert.Equal(300, builder.ConnectionIdleLifetime);
        Assert.Equal(60, builder.ConnectionPruningInterval);
    }

    /// <summary>
    /// 用户显式配的值必须原样保留。
    /// 这条曾经写错过：用 <c>ContainsKey</c> 判断"用户写了吗"，
    /// 而它对任何合法键名恒返回 true，导致默认值永远不生效、判断结果毫无意义。
    /// </summary>
    [Fact]
    public void Explicit_max_pool_size_is_preserved()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "Host=db;Database=kb;Username=postgres;Password=p;Maximum Pool Size=7",
            Configuration());

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(7, builder.MaxPoolSize);
        // 没写的仍然补默认值。
        Assert.Equal(300, builder.ConnectionIdleLifetime);
    }

    [Fact]
    public void Explicit_idle_lifetime_is_preserved()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "Host=db;Database=kb;Username=postgres;Password=p;Connection Idle Lifetime=120",
            Configuration());

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(120, builder.ConnectionIdleLifetime);
        Assert.Equal(16, builder.MaxPoolSize);
    }

    [Fact]
    public void Key_detection_is_case_and_space_insensitive()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "host=db;database=kb;username=postgres;password=p;maximum pool size=9",
            Configuration());

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(9, builder.MaxPoolSize);
    }

    [Fact]
    public void Password_containing_equals_sign_does_not_confuse_key_detection()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "Host=db;Database=kb;Username=postgres;Password=a=b;Maximum Pool Size=25",
            Configuration());

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(25, builder.MaxPoolSize);
        Assert.Equal("a=b", builder.Password);
    }

    /// <summary>配置里改了值，兜底用的就应该是新值而不是硬编码的 16。</summary>
    [Fact]
    public void Values_come_from_configuration()
    {
        var result = ApiHost.ApplyPoolDefaults(
            "Host=db;Database=kb;Username=postgres;Password=p",
            Configuration(maxPoolSize: 4, idleLifetime: 90, pruningInterval: 15));

        var builder = new NpgsqlConnectionStringBuilder(result);
        Assert.Equal(4, builder.MaxPoolSize);
        Assert.Equal(90, builder.ConnectionIdleLifetime);
        Assert.Equal(15, builder.ConnectionPruningInterval);
    }

    /// <summary>
    /// 连接串本身写坏了不该在这里抛异常——那会把"连接串有错"
    /// 变成一条看不懂的 ArgumentException，掩盖真正的诊断信息。
    /// 原样返回，交给后面的启动预检去报人话。
    /// </summary>
    [Fact]
    public void Malformed_connection_string_is_returned_unchanged()
    {
        const string broken = "这不是一个连接串";

        var result = ApiHost.ApplyPoolDefaults(broken, Configuration());

        Assert.Equal(broken, result);
    }

    /// <summary>
    /// 锁住 Npgsql 的真实行为，避免以后有人"优化"成 ContainsKey。
    /// 只要这条还通过，就说明 ContainsKey 依旧不能用来判断"用户写了吗"。
    /// </summary>
    [Fact]
    public void ContainsKey_reports_valid_names_not_declared_keys()
    {
        var builder = new NpgsqlConnectionStringBuilder(
            "Host=db;Database=kb;Username=postgres;Password=p");

        Assert.True(builder.ContainsKey("Maximum Pool Size"));
        Assert.True(builder.ContainsKey("Connection Idle Lifetime"));
        Assert.True(builder.ContainsKey("Connection Pruning Interval"));

        // 拼错的名字才返回 false。
        Assert.False(builder.ContainsKey("Max Pool Size"));
    }
}
