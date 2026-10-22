using MyFirstMafAgent.Api.Agents.Tooling;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>联网工具的地址护栏：只允许公网 http/https，阻断本机与内网目标（SSRF 防护）。</summary>
public sealed class SafeUrlGuardTests
{
    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:8080/admin")]
    [InlineData("http://127.0.0.1:5432/")]
    [InlineData("http://127.1.2.3/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.4.2/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://api.internal/")]
    [InlineData("http://app.localhost/")]
    public void TryValidate_RejectsLocalAndPrivateAddresses(string url)
    {
        Assert.False(SafeUrlGuard.TryValidate(url, out var uri, out var error));
        Assert.Null(uri);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    public void TryValidate_RejectsUnsupportedSchemesAndGarbage(string url)
    {
        Assert.False(SafeUrlGuard.TryValidate(url, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryValidate_RejectsCredentialsInUrl()
    {
        Assert.False(SafeUrlGuard.TryValidate("http://user:pass@example.com/", out _, out _));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/path?q=1")]
    [InlineData("https://93.184.216.34/")]
    [InlineData("https://8.8.8.8/")]
    [InlineData(" https://example.com/ ")]
    public void TryValidate_AcceptsPublicHttpAndHttps(string url)
    {
        Assert.True(SafeUrlGuard.TryValidate(url, out var uri, out var error));
        Assert.NotNull(uri);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_TrimsAngleBracketsFromModelOutput()
    {
        Assert.True(SafeUrlGuard.TryValidate("<https://example.com/>", out var uri, out _));
        Assert.Equal("https", uri!.Scheme);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", true)]
    [InlineData("example.com", false)]
    [InlineData("1.1.1.1", false)]
    public void IsBlockedHost_ClassifiesHosts(string host, bool expected) =>
        Assert.Equal(expected, SafeUrlGuard.IsBlockedHost(host));
}
