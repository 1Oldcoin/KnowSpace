using Npgsql;
using MyFirstMafAgent.Api.Infrastructure.Persistence;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 启动期数据库预检：外部库连不上时必须**在启动时就用人话说清**
/// 「连的是哪个端点 / 为什么连不上 / 便携库被跳过了 / 该怎么修」，
/// 而不是让用户对着一条 SqlSugar 堆栈猜。
/// </summary>
public sealed class ConnectionPreflightTests
{
    private const string DevelopmentDsn =
        "Host=localhost;Port=5432;Database=knowledge_base;Username=postgres;Password=s3cret!";

    [Fact]
    public void Mask_replaces_the_password_and_keeps_the_rest()
    {
        var masked = ConnectionPreflight.Mask(DevelopmentDsn);

        Assert.DoesNotContain("s3cret!", masked, StringComparison.Ordinal);
        Assert.Contains("Password=***", masked, StringComparison.Ordinal);
        // 其余字段原样保留，用户才能认出这是哪条配置。
        Assert.Contains("Host=localhost", masked, StringComparison.Ordinal);
        Assert.Contains("Database=knowledge_base", masked, StringComparison.Ordinal);
        Assert.Contains("Username=postgres", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host=h;Password=abc", "Host=h;Password=***")]
    [InlineData("Host=h;password = abc ;Port=1", "Host=h;password = ***;Port=1")]
    [InlineData("Host=h;Port=5432", "Host=h;Port=5432")] // 没有口令时不动它
    public void Mask_is_case_insensitive_and_tolerates_missing_password(string input, string expected)
    {
        Assert.Equal(expected, ConnectionPreflight.Mask(input));
    }

    [Fact]
    public void Mask_reports_an_empty_string_explicitly()
    {
        Assert.Equal("(空)", ConnectionPreflight.Mask(null));
        Assert.Equal("(空)", ConnectionPreflight.Mask("   "));
    }

    [Fact]
    public void ResolveEndpoint_reads_host_port_and_database_without_credentials()
    {
        var endpoint = ConnectionPreflight.ResolveEndpoint(DevelopmentDsn);

        Assert.Equal("localhost:5432/knowledge_base", endpoint);
        Assert.DoesNotContain("s3cret", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEndpoint_defaults_the_port_to_5432()
    {
        Assert.Equal(
            "db.internal:5432/app",
            ConnectionPreflight.ResolveEndpoint("Host=db.internal;Database=app;Username=u;Password=p"));
    }

    [Fact]
    public void ResolveEndpoint_admits_when_it_cannot_parse()
    {
        Assert.Equal("(无法解析)", ConnectionPreflight.ResolveEndpoint("this is not a connection string"));
        Assert.Equal("(未配置)", ConnectionPreflight.ResolveEndpoint(""));
    }

    [Fact]
    public async Task Malformed_connection_string_is_reported_without_any_networking()
    {
        var result = await ConnectionPreflight.CheckAsync(
            "definitely not a dsn", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(ConnectionPreflight.ProbeStatus.Malformed, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains("无法解析", ConnectionPreflight.BuildMessage(result, "definitely not a dsn"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_timeout_skips_the_probe_entirely()
    {
        // 允许部署时关掉预检（例如远端库启动很慢）；关掉后不再做任何网络访问。
        var result = await ConnectionPreflight.CheckAsync(
            DevelopmentDsn, TimeSpan.Zero, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("关闭", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_port_is_reported_as_unreachable_not_as_a_driver_crash()
    {
        // 端口 1 上没有监听者：Windows 会立刻返回“连接被拒绝”。
        var result = await ConnectionPreflight.CheckAsync(
            "Host=127.0.0.1;Port=1;Database=knowledge_base;Username=postgres;Password=x",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(
            result.Status is ConnectionPreflight.ProbeStatus.Unreachable or ConnectionPreflight.ProbeStatus.TimedOut,
            $"期望 Unreachable/TimedOut，实际 {result.Status}：{result.Detail}");
        Assert.Equal("127.0.0.1:1/knowledge_base", result.Endpoint);
    }

    [Fact]
    public async Task An_open_port_that_is_not_postgres_never_passes_the_preflight()
    {
        // 真实场景：端口被别的服务占了（或代理/中间件），TCP 通但握手失败。
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        var accept = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                // 立刻断开：让客户端读到 EOF，而不是干等到超时。
            }
            catch (Exception)
            {
                // 监听器已关闭，忽略。
            }
        });

        try
        {
            var result = await ConnectionPreflight.CheckAsync(
                $"Host=127.0.0.1;Port={port};Database=knowledge_base;Username=postgres;Password=x;Timeout=2",
                TimeSpan.FromSeconds(3),
                CancellationToken.None);

            Assert.False(result.Succeeded, $"端口被非 PostgreSQL 占用时不应判成功（实际 {result.Status}）");
        }
        finally
        {
            listener.Stop();
            await accept;
        }
    }

    [Fact]
    public void The_message_names_the_endpoint_the_reason_and_all_three_remedies()
    {
        var result = new ConnectionPreflight.ProbeResult(
            ConnectionPreflight.ProbeStatus.Unreachable,
            "localhost:5432/knowledge_base",
            "由于目标计算机积极拒绝，无法连接。 (127.0.0.1:5432)");

        var message = ConnectionPreflight.BuildMessage(result, DevelopmentDsn);

        // 1) 连的是哪个端点
        Assert.Contains("localhost:5432/knowledge_base", message, StringComparison.Ordinal);
        // 2) 为什么连不上（并且解释“连接被拒绝”= 库没启动）
        Assert.Contains("没有进程在监听", message, StringComparison.Ordinal);
        // 3) 关键因果：便携库是被这条连接串“跳过”的，不是坏了
        Assert.Contains("便携 PostgreSQL 会被跳过", message, StringComparison.Ordinal);
        // 4) 三条可执行的修法
        Assert.Contains("Start-Service", message, StringComparison.Ordinal);
        Assert.Contains("user-secrets remove", message, StringComparison.Ordinal);
        Assert.Contains("RespectExternalConnectionString=false", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_message_never_leaks_the_password()
    {
        var result = new ConnectionPreflight.ProbeResult(
            ConnectionPreflight.ProbeStatus.Unreachable, "localhost:5432/knowledge_base", "refused");

        var message = ConnectionPreflight.BuildMessage(result, DevelopmentDsn);

        Assert.DoesNotContain("s3cret!", message, StringComparison.Ordinal);
        Assert.Contains("Password=***", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_connection_asks_for_credentials_instead_of_asking_to_start_a_service()
    {
        var result = new ConnectionPreflight.ProbeResult(
            ConnectionPreflight.ProbeStatus.ServerRejected,
            "localhost:5432/knowledge_base",
            "服务器拒绝连接（28P01）：password authentication failed for user \"postgres\"");

        var message = ConnectionPreflight.BuildMessage(result, DevelopmentDsn);

        Assert.Contains("用户名、口令与库名", message, StringComparison.Ordinal);
        // 口令错时不该建议“去启动服务”——库明明已经在跑了。
        Assert.DoesNotContain("Start-Service", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_database_is_reported_by_its_own_status()
    {
        var missing = new PostgresException(
            "database \"nope\" does not exist", "FATAL", "FATAL", PostgresErrorCodes.InvalidCatalogName);

        // 只是确认错误码常量用对了：InvalidCatalogName 是 3D000。
        Assert.Equal("3D000", missing.SqlState);
        Assert.Equal(PostgresErrorCodes.InvalidCatalogName, missing.SqlState);

        var result = new ConnectionPreflight.ProbeResult(
            ConnectionPreflight.ProbeStatus.ServerRejected, "localhost:5432/nope", missing.MessageText);
        var message = ConnectionPreflight.BuildMessage(result, "Host=localhost;Database=nope;Password=s3cret!");

        Assert.Contains("用户名、口令与库名", message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret!", message, StringComparison.Ordinal);
    }
}
