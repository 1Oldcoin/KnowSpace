using Microsoft.AspNetCore.Http;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>MCP 传输守卫：Host / Origin 白名单是 DNS rebinding 与跨站调用的第一道闸。</summary>
public sealed class McpTransportGuardTests
{
    private static McpTransportGuard CreateGuard(
        string[]? allowedOrigins = null,
        string[]? allowedHosts = null,
        string issuer = "",
        string publicBaseUrl = "")
    {
        var mcp = new McpOptions
        {
            AllowedOrigins = [.. allowedOrigins ?? []],
            AllowedHosts = [.. allowedHosts ?? []]
        };
        var oauth = new McpOAuthOptions { Enabled = false, Issuer = issuer };
        var server = new ServerSecurityOptions { PublicBaseUrl = publicBaseUrl };
        return McpTransportGuard.Create(mcp, oauth, server);
    }

    private static HttpContext Context(string host, string? origin = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host);
        if (origin is not null) context.Request.Headers.Origin = origin;
        return context;
    }

    [Theory]
    [InlineData("127.0.0.1:5056")]
    [InlineData("localhost:5056")]
    [InlineData("[::1]:5056")]
    public void Loopback_hosts_are_allowed(string host) =>
        Assert.Null(CreateGuard().Validate(Context(host)));

    [Fact]
    public void Rebinding_host_is_rejected()
    {
        var reason = CreateGuard().Validate(Context("attacker.example.com:5056"));

        Assert.NotNull(reason);
        Assert.Contains("Host", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_host_is_allowed()
    {
        Assert.Null(CreateGuard(allowedHosts: ["kb.example.com"]).Validate(Context("kb.example.com")));
    }

    [Fact]
    public void Issuer_host_is_allowed()
    {
        var guard = CreateGuard(issuer: "https://kb.example.com");

        Assert.Null(guard.Validate(Context("kb.example.com")));
    }

    [Fact]
    public void Public_base_url_host_is_allowed()
    {
        var guard = CreateGuard(publicBaseUrl: "https://knowspace.example.com");

        Assert.Null(guard.Validate(Context("knowspace.example.com")));
    }

    [Fact]
    public void Requests_without_origin_pass_non_browser_clients()
    {
        // CLI / SDK 客户端不发 Origin，不能因为"没头"被拒。
        Assert.Null(CreateGuard().Validate(Context("127.0.0.1:5056")));
    }

    [Fact]
    public void Foreign_origin_is_rejected()
    {
        var reason = CreateGuard().Validate(Context("127.0.0.1:5056", "https://evil.example.com"));

        Assert.NotNull(reason);
        Assert.Contains("Origin", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost:6274")]
    [InlineData("http://127.0.0.1:5173")]
    public void Loopback_origins_are_allowed_for_local_tools(string origin) =>
        Assert.Null(CreateGuard().Validate(Context("127.0.0.1:5056", origin)));

    [Fact]
    public void Configured_origin_is_allowed()
    {
        var guard = CreateGuard(allowedOrigins: ["https://inspector.example.com"]);

        Assert.Null(guard.Validate(Context("kb.example.com", "https://inspector.example.com")));
    }

    [Fact]
    public void Issuer_origin_is_allowed()
    {
        var guard = CreateGuard(issuer: "https://kb.example.com");

        Assert.Null(guard.Validate(Context("kb.example.com", "https://kb.example.com")));
    }

    [Fact]
    public void Opaque_null_origin_is_rejected()
    {
        var reason = CreateGuard().Validate(Context("127.0.0.1:5056", "null"));

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("https://example.com:8443/path?x=1", true)]
    [InlineData("null", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("", false)]
    public void Origin_normalization_keeps_only_scheme_and_authority(string value, bool expected) =>
        Assert.Equal(expected, McpTransportGuard.TryNormalizeOrigin(value, out _));
}
