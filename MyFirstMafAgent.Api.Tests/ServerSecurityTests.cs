using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Hosting;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 云部署相关加固的契约测试：匿名健康检查、失败闸门、配置回退。
/// </summary>
public sealed class ServerSecurityTests
{
    private static DesktopSecurityOptions WithHealthOpen(string token = "server-token-abcdefgh") =>
        new(true, token, StrictContentSecurityPolicy: false, AllowAnonymousHealth: true);

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/health/")]
    [InlineData("/API/HEALTH")]
    public void Anonymous_health_is_allowed_when_enabled(string path)
    {
        var options = WithHealthOpen();

        Assert.False(LocalApiTokenValidator.RequiresToken(new PathString(path), options.AllowAnonymousHealth));
        Assert.True(LocalApiTokenValidator.IsAuthorized(options, new PathString(path), StringValues.Empty));
    }

    [Theory]
    [InlineData("/api/healthz")]
    [InlineData("/api/knowledge-bases")]
    [InlineData("/api/settings")]
    [InlineData("/api/desktop/shutdown")]
    public void Everything_else_still_needs_the_token_when_health_is_open(string path)
    {
        var options = WithHealthOpen();

        Assert.True(LocalApiTokenValidator.RequiresToken(new PathString(path), options.AllowAnonymousHealth));
        Assert.False(LocalApiTokenValidator.IsAuthorized(options, new PathString(path), StringValues.Empty));
    }

    [Fact]
    public void Health_still_requires_the_token_by_default()
    {
        // 桌面模式保持原样：Electron 主进程持有令牌，不需要开口子。
        var options = new DesktopSecurityOptions(true, "token-abc", StrictContentSecurityPolicy: true);

        Assert.True(LocalApiTokenValidator.RequiresToken(new PathString("/api/health")));
        Assert.False(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), StringValues.Empty));
    }

    [Fact]
    public void Server_token_environment_variable_enables_the_guard()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var previous = Environment.GetEnvironmentVariable(
            MyFirstMafAgent.Api.Options.ServerSecurityOptions.TokenEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(
                MyFirstMafAgent.Api.Options.ServerSecurityOptions.TokenEnvironmentVariable,
                "server-token-from-env");

            var options = DesktopSecurityOptions.FromConfiguration(configuration);

            Assert.True(options.Enabled);
            Assert.Equal("server-token-from-env", options.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                MyFirstMafAgent.Api.Options.ServerSecurityOptions.TokenEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Failed_attempts_block_the_source_after_the_limit()
    {
        var clock = new FakeTimeProvider();
        var throttle = new AgentAuthThrottle(clock);

        // 上限 3：前两次只计数，第三次触发封禁。
        Assert.False(throttle.RecordFailure("203.0.113.9", 3, 300));
        Assert.False(throttle.RecordFailure("203.0.113.9", 3, 300));
        Assert.True(throttle.RecordFailure("203.0.113.9", 3, 300));

        Assert.True(throttle.IsBlocked("203.0.113.9", out var retryAfter));
        Assert.InRange(retryAfter, 1, 300);

        // 其它来源不受牵连。
        Assert.False(throttle.IsBlocked("203.0.113.10", out _));
    }

    [Fact]
    public void Block_expires_and_the_counter_resets()
    {
        var clock = new FakeTimeProvider();
        var throttle = new AgentAuthThrottle(clock);

        throttle.RecordFailure("198.51.100.1", 1, 60);
        Assert.True(throttle.IsBlocked("198.51.100.1", out _));

        clock.Advance(TimeSpan.FromSeconds(61));

        Assert.False(throttle.IsBlocked("198.51.100.1", out _));
        // 封禁到期后重新计数：不该立刻又因为旧计数被封。
        Assert.False(throttle.RecordFailure("198.51.100.1", 3, 60));
    }

    [Fact]
    public void Success_clears_the_failure_counter()
    {
        var clock = new FakeTimeProvider();
        var throttle = new AgentAuthThrottle(clock);

        throttle.RecordFailure("198.51.100.2", 3, 60);
        throttle.RecordFailure("198.51.100.2", 3, 60);
        throttle.RecordSuccess("198.51.100.2");

        // 计数已清零，再失败两次不该被封。
        Assert.False(throttle.RecordFailure("198.51.100.2", 3, 60));
        Assert.False(throttle.RecordFailure("198.51.100.2", 3, 60));
        Assert.False(throttle.IsBlocked("198.51.100.2", out _));
    }

    [Fact]
    public void Zero_limit_disables_the_throttle()
    {
        var throttle = new AgentAuthThrottle(new FakeTimeProvider());

        for (var i = 0; i < 50; i++) Assert.False(throttle.RecordFailure("192.0.2.1", 0, 300));

        Assert.False(throttle.IsBlocked("192.0.2.1", out _));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
