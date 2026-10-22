using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using MyFirstMafAgent.Api.Hosting;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

public sealed class DesktopSecurityTests
{
    private static DesktopSecurityOptions EnabledResponse(string token = "token-abc") =>
        new(true, token, StrictContentSecurityPolicy: true);

    private static DesktopSecurityOptions Disabled() =>
        new(false, null, StrictContentSecurityPolicy: false);

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/knowledge-bases")]
    [InlineData("/api/avatar/feed")]
    [InlineData("/mcp")]
    [InlineData("/API/Health")]
    public void Data_plane_paths_require_a_token(string path)
    {
        Assert.True(LocalApiTokenValidator.RequiresToken(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/index-abc.js")]
    [InlineData("/favicon.ico")]
    [InlineData("/apix/not-data")]
    public void Static_paths_do_not_require_a_token(string path)
    {
        Assert.False(LocalApiTokenValidator.RequiresToken(new PathString(path)));
    }

    [Fact]
    public void Valid_token_is_accepted()
    {
        var options = EnabledResponse();

        Assert.True(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), new StringValues("token-abc")));
    }

    [Fact]
    public void Missing_or_wrong_token_is_rejected()
    {
        var options = EnabledResponse();

        Assert.False(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), StringValues.Empty));
        Assert.False(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), new StringValues("token-abd")));
        Assert.False(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), new StringValues("TOKEN-ABC")));
    }

    [Fact]
    public void Disabled_security_allows_everything()
    {
        var options = Disabled();

        Assert.True(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/api/health"), StringValues.Empty));
        Assert.False(options.Enabled);
    }

    [Fact]
    public void Static_assets_stay_reachable_even_when_token_is_enabled()
    {
        var options = EnabledResponse();

        Assert.True(LocalApiTokenValidator.IsAuthorized(
            options, new PathString("/assets/app.js"), StringValues.Empty));
    }

    [Fact]
    public void Options_are_disabled_when_no_token_is_configured()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var options = DesktopSecurityOptions.FromConfiguration(configuration);

        Assert.False(options.Enabled);
        Assert.Null(options.Token);
    }

    [Fact]
    public void Options_read_the_token_from_the_environment_variable()
    {
        var previous = Environment.GetEnvironmentVariable(DesktopSecurityOptions.TokenEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(DesktopSecurityOptions.TokenEnvironmentVariable, "env-token");
            var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

            var options = DesktopSecurityOptions.FromConfiguration(configuration);

            Assert.True(options.Enabled);
            Assert.Equal("env-token", options.Token);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopSecurityOptions.TokenEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Strict_content_security_policy_blocks_inline_scripts_and_remote_origins()
    {
        var policy = DesktopSecurityOptions.StrictContentSecurityPolicyValue;

        Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", policy, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self'", policy, StringComparison.Ordinal);
    }
}

public sealed class DesktopHostArgumentsTests
{
    [Fact]
    public void Ready_file_and_data_root_are_parsed_from_separate_arguments()
    {
        var parsed = DesktopHostArguments.Parse(
            ["--no-browser", "--desktop-ready-file", @"C:\temp\ready.json", "--desktop-data-root", @"C:\data"]);

        Assert.Equal(@"C:\temp\ready.json", parsed.ReadyFilePath);
        Assert.Equal(@"C:\data", parsed.DataRoot);
    }

    [Fact]
    public void Equals_syntax_is_supported()
    {
        var parsed = DesktopHostArguments.Parse(["--desktop-ready-file=C:\\temp\\ready.json"]);

        Assert.Equal(@"C:\temp\ready.json", parsed.ReadyFilePath);
        Assert.Null(parsed.DataRoot);
    }

    [Fact]
    public void Missing_options_produce_nulls()
    {
        var parsed = DesktopHostArguments.Parse(["--urls", "http://127.0.0.1:0"]);

        Assert.Null(parsed.ReadyFilePath);
        Assert.Null(parsed.DataRoot);
    }

    [Fact]
    public async Task Ready_file_contains_the_url_and_process_id_but_never_the_token()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maf-ready-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "ready.json");
        var arguments = new DesktopHostArguments(path, null);

        try
        {
            await arguments.WriteReadyFileAsync("http://127.0.0.1:54321");

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("http://127.0.0.1:54321", content, StringComparison.Ordinal);
            Assert.Contains(Environment.ProcessId.ToString(), content, StringComparison.Ordinal);
            Assert.DoesNotContain("token", content, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ready_file_write_is_a_no_op_when_no_path_is_configured()
    {
        var arguments = new DesktopHostArguments(null, null);

        await arguments.WriteReadyFileAsync("http://127.0.0.1:1");
    }
}
