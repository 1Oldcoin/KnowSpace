using Microsoft.Extensions.Configuration;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>服务端启动守卫：不安全的组合必须变成"拒绝启动"，不是"警告一句"。</summary>
public sealed class SecurityPostureGuardTests
{
    private static readonly HostProfileInfo Server = new(HostProfile.Server, "test");

    private static IConfiguration Config(params (string Key, string Value)[] items) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(items.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();

    private static DesktopSecurityOptions WithToken(string? token = "0123456789abcdef0123456789abcdef") =>
        new(token is not null, token, StrictContentSecurityPolicy: false);

    private static IReadOnlyList<SecurityPostureFinding> Errors(IEnumerable<SecurityPostureFinding> findings) =>
        findings.Where(finding => finding.IsError).ToList();

    [Fact]
    public void Missing_api_token_blocks_server_startup()
    {
        var findings = SecurityPostureGuard.Evaluate(Server, Config(), WithToken(null));

        Assert.Contains(Errors(findings), finding =>
            finding.Message.Contains("MAF_SERVER_TOKEN", StringComparison.Ordinal));
    }

    [Fact]
    public void Short_api_token_blocks_server_startup()
    {
        var findings = SecurityPostureGuard.Evaluate(Server, Config(), WithToken("short"));

        Assert.Contains(Errors(findings), finding =>
            finding.Message.Contains("太短", StringComparison.Ordinal));
    }

    [Fact]
    public void Minimum_length_token_passes()
    {
        var findings = SecurityPostureGuard.Evaluate(
            Server, Config(), WithToken(new string('a', SecurityPostureGuard.MinServerTokenLength)));

        Assert.Empty(Errors(findings));
    }

    [Fact]
    public void RequireClientKey_false_blocks_server_startup()
    {
        var findings = SecurityPostureGuard.Evaluate(
            Server, Config(("Mcp:RequireClientKey", "false")), WithToken());

        Assert.Contains(Errors(findings), finding =>
            finding.Message.Contains("RequireClientKey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Mcp:OAuth:Cimd:AllowInsecureHttp")]
    [InlineData("Mcp:OAuth:Cimd:AllowPrivateAddresses")]
    public void Cimd_debug_switches_block_server_startup(string key)
    {
        var findings = SecurityPostureGuard.Evaluate(Server, Config((key, "true")), WithToken());

        Assert.Contains(Errors(findings), finding => finding.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Oauth_with_http_issuer_blocks_server_startup()
    {
        var findings = SecurityPostureGuard.Evaluate(
            Server,
            Config(("Mcp:OAuth:Enabled", "true"), ("Mcp:OAuth:Issuer", "http://kb.example.com")),
            WithToken());

        Assert.Contains(Errors(findings), finding => finding.Message.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void Oauth_with_https_issuer_passes()
    {
        var findings = SecurityPostureGuard.Evaluate(
            Server,
            Config(
                ("Mcp:OAuth:Enabled", "true"),
                ("Mcp:OAuth:Issuer", "https://kb.example.com"),
                ("Mcp:OAuth:AllowDynamicRegistration", "false")),
            WithToken());

        Assert.Empty(Errors(findings));
    }

    [Fact]
    public void Desktop_profile_never_blocks()
    {
        var findings = SecurityPostureGuard.Evaluate(
            new HostProfileInfo(HostProfile.Desktop, "test"),
            Config(("Mcp:RequireClientKey", "false")),
            WithToken(null));

        Assert.Empty(findings);
    }

    [Fact]
    public void Enforce_throws_with_actionable_message()
    {
        var logged = new List<string>();
        var findings = SecurityPostureGuard.Evaluate(Server, Config(), WithToken(null));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SecurityPostureGuard.Enforce(Server, findings, logged.Add));

        Assert.Contains(HostProfileResolver.ConfigurationKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains(logged, line => line.Contains("宿主形态", StringComparison.Ordinal));
        Assert.Contains(logged, line => line.Contains("安全检查未通过", StringComparison.Ordinal));
    }
}
