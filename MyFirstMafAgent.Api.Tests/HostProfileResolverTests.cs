using Microsoft.Extensions.Configuration;
using MyFirstMafAgent.Api.Hosting;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>宿主形态判定：Auto 必须偏向 Server，否则云端裸奔。</summary>
public sealed class HostProfileResolverTests
{
    private static IConfiguration Config(params (string Key, string Value)[] items) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(items.Select(item => new KeyValuePair<string, string?>(item.Key, item.Value)))
            .Build();

    [Theory]
    [InlineData("http://127.0.0.1:5056")]
    [InlineData("http://localhost:5056")]
    [InlineData("http://[::1]:5056")]
    [InlineData("https://127.0.0.1:5057;http://localhost:1234")]
    public void Auto_treats_loopback_only_as_desktop(string urls)
    {
        var profile = HostProfileResolver.Resolve(Config(), urls);

        Assert.Equal(HostProfile.Desktop, profile.Profile);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5056")]
    [InlineData("http://*:8080")]
    [InlineData("https://0.0.0.0:443")]
    [InlineData("http://192.168.1.10:5056")]
    [InlineData("http://127.0.0.1:5056;http://0.0.0.0:5057")]
    public void Auto_treats_exposed_listener_as_server(string urls)
    {
        var profile = HostProfileResolver.Resolve(Config(), urls);

        Assert.Equal(HostProfile.Server, profile.Profile);
        Assert.Contains("非回环", profile.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_uses_container_port_variables_when_urls_absent()
    {
        var profile = HostProfileResolver.Resolve(Config(("http_ports", "8080")), explicitUrls: null);

        Assert.Equal(HostProfile.Server, profile.Profile);
    }

    [Fact]
    public void Explicit_desktop_wins_over_exposed_listener()
    {
        var profile = HostProfileResolver.Resolve(
            Config((HostProfileResolver.ConfigurationKey, "Desktop")), "http://0.0.0.0:5056");

        Assert.Equal(HostProfile.Desktop, profile.Profile);
    }

    [Fact]
    public void Explicit_server_is_honoured_behind_reverse_proxy()
    {
        var profile = HostProfileResolver.Resolve(
            Config((HostProfileResolver.ConfigurationKey, "Server")), "http://127.0.0.1:5056");

        Assert.Equal(HostProfile.Server, profile.Profile);
    }

    [Fact]
    public void Unknown_profile_value_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            HostProfileResolver.Resolve(Config((HostProfileResolver.ConfigurationKey, "Cloud")), null));

        Assert.Contains("Hosting:Profile", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5056", true)]
    [InlineData("http://localhost", true)]
    [InlineData("http://0.0.0.0:5056", false)]
    [InlineData("http://[::]:5056", false)]
    [InlineData("https://kb.example.com", false)]
    public void Loopback_url_detection_handles_wildcards(string url, bool expected) =>
        Assert.Equal(expected, HostProfileResolver.IsLoopbackUrl(url));
}
