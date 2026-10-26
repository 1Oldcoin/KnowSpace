using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 跨平台工具定位（P0 跨平台打包的一部分）。
///
/// <para>
/// 这几条测试守的是「换平台后必须还能找到二进制」：
/// 候选顺序、上游原始文件名免改名、配置了失效路径时的回落、以及
/// 扩展名不同的可执行文件（<c>.exe</c> vs 无后缀）都能命中。
/// </para>
/// </summary>
public sealed class PlatformRuntimeTests : IDisposable
{
    private readonly string _root;

    public PlatformRuntimeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maf-platform-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private string Touch(params string[] relativeParts)
    {
        var path = Path.Combine([_root, .. relativeParts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "stub");
        return path;
    }

    [Fact]
    public void RuntimeIdentifier_matches_the_current_platform()
    {
        var rid = PlatformRuntime.RuntimeIdentifier;

        Assert.Contains("-", rid);
        Assert.True(
            (OperatingSystem.IsWindows() && rid.StartsWith("win-", StringComparison.Ordinal))
            || (OperatingSystem.IsLinux() && rid.StartsWith("linux-", StringComparison.Ordinal))
            || (OperatingSystem.IsMacOS() && rid.StartsWith("osx-", StringComparison.Ordinal)),
            $"意料之外的 RID：{rid}");
    }

    [Fact]
    public void ExecutableSuffix_follows_the_platform()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? ".exe" : "", PlatformRuntime.ExecutableSuffix);
        Assert.Equal(
            OperatingSystem.IsWindows() ? "postgres.exe" : "postgres",
            PlatformRuntime.PostgresExecutableName);
    }

    [Fact]
    public void Postgres_bin_candidates_put_the_rid_folder_first()
    {
        var candidates = PlatformRuntime.PostgresBinCandidates();

        // 便携 PostgreSQL 是整棵目录树（bin/lib/share），所以按“目录”而不是“文件”解析。
        Assert.Equal($"ExternalTools/pgSQL/{PlatformRuntime.RuntimeIdentifier}/bin", candidates[0]);
        // 扁平布局（把 dist\pgsql 直接放进 ExternalTools/pgSQL）永远在最后。
        Assert.Equal("ExternalTools/pgSQL/bin", candidates[^1]);
    }

    [Fact]
    public void Postgres_bin_resolves_from_the_rid_layout()
    {
        var bin = Touch("ExternalTools", "pgSQL", PlatformRuntime.RuntimeIdentifier, "bin", PlatformRuntime.PostgresExecutableName);

        var resolved = PlatformRuntime.ResolvePostgresBinDirectory(_root, "");

        Assert.Equal(Path.GetDirectoryName(bin), resolved);
    }

    [Fact]
    public void Postgres_bin_resolves_from_the_flat_layout()
    {
        var bin = Touch("ExternalTools", "pgSQL", "bin", PlatformRuntime.PostgresExecutableName);

        var resolved = PlatformRuntime.ResolvePostgresBinDirectory(_root, "");

        Assert.Equal(Path.GetDirectoryName(bin), resolved);
    }

    [Fact]
    public void Configured_postgres_root_or_bin_directory_both_work()
    {
        // 用户从资源管理器复制路径时，可能给的是 pgsql 根目录，也可能直接给 bin。
        var bin = Touch("custom", "pgsql", "bin", PlatformRuntime.PostgresExecutableName);

        Assert.Equal(Path.GetDirectoryName(bin), PlatformRuntime.ResolvePostgresBinDirectory(_root, "custom/pgsql"));
        Assert.Equal(Path.GetDirectoryName(bin), PlatformRuntime.ResolvePostgresBinDirectory(_root, "custom/pgsql/bin"));
    }

    [Fact]
    public void Stale_postgres_bin_path_falls_back_to_the_platform_layout()
    {
        // 典型场景：把 Windows 包的 appsettings.json 带到 Linux，里面还写着 win-x64 的目录。
        var actual = Touch("ExternalTools", "pgSQL", PlatformRuntime.RuntimeIdentifier, "bin", PlatformRuntime.PostgresExecutableName);
        var messages = new List<string>();

        var resolved = PlatformRuntime.ResolvePostgresBinDirectory(
            _root, "ExternalTools/pgSQL/no-such-rid/bin", messages.Add);

        Assert.Equal(Path.GetDirectoryName(actual), resolved);
        Assert.Contains(messages, message => message.Contains("自动定位"));
    }

    [Fact]
    public void Missing_postgres_distribution_resolves_to_null()
    {
        Assert.Null(PlatformRuntime.ResolvePostgresBinDirectory(_root, ""));
        Assert.Null(PlatformRuntime.TryFindTool(_root, "ExternalTools/ffmpeg", "ffmpeg"));
    }

    [Fact]
    public void Single_file_tool_candidates_still_prefer_the_rid_folder()
    {
        // ffmpeg 这类单文件工具继续走 ToolCandidates（便携 PostgreSQL 不再走这条路径）。
        var candidates = PlatformRuntime.ToolCandidates("ExternalTools/ffmpeg", "ffmpeg");

        Assert.Equal($"ExternalTools/ffmpeg/{PlatformRuntime.RuntimeIdentifier}/ffmpeg{PlatformRuntime.ExecutableSuffix}", candidates[0]);
        Assert.Equal($"ExternalTools/ffmpeg/ffmpeg{PlatformRuntime.ExecutableSuffix}", candidates[^1]);
    }
    [Fact]
    public void EnsureExecutable_is_a_noop_off_windows_and_never_throws()
    {
        var file = Touch("some-tool");

        PlatformRuntime.EnsureExecutable(file);
        PlatformRuntime.EnsureExecutable(Path.Combine(_root, "does-not-exist"));

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(file);
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
    }
}

/// <summary>
/// 插件入口命令的判定。旧规则「不以 .exe/.dll 结尾就当宿主命令」在 Linux 上
/// 会把原生插件（无后缀）误当成 PATH 命令，这条测试锁定新行为。
/// </summary>
public sealed class PluginEntryCommandTests : IDisposable
{
    private readonly string _directory;

    public PluginEntryCommandTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "maf-plugin-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("node")]
    [InlineData("python3")]
    public void Bare_host_commands_are_resolved_from_path(string command)
    {
        Assert.True(PluginEntryCommand.IsHostCommand(_directory, command));
    }

    [Theory]
    [InlineData("./MyPlugin")]
    [InlineData("bin/MyPlugin")]
    [InlineData("MyPlugin.dll")]
    [InlineData("MyPlugin.exe")]
    [InlineData("index.js")]
    [InlineData("main.py")]
    public void Paths_and_known_file_extensions_are_not_host_commands(string command)
    {
        Assert.False(PluginEntryCommand.IsHostCommand(_directory, command));
    }

    [Fact]
    public void Extension_less_file_inside_the_package_is_the_entry_point()
    {
        // Linux/macOS 的原生插件通常没有后缀。
        File.WriteAllText(Path.Combine(_directory, "MyPlugin"), "#!/bin/sh");

        Assert.False(PluginEntryCommand.IsHostCommand(_directory, "MyPlugin"));
    }

    [Fact]
    public void Unknown_bare_word_stays_a_host_command()
    {
        Assert.True(PluginEntryCommand.IsHostCommand(_directory, "some-command-not-in-package"));
    }

    [Fact]
    public void ResolveEntryPath_uses_the_package_directory_for_native_plugins()
    {
        File.WriteAllText(Path.Combine(_directory, "MyPlugin"), "#!/bin/sh");

        var manifest = new PluginManifest
        {
            Entry = new PluginEntry { Command = "MyPlugin" }
        };

        var path = PluginManifestLoader.ResolveEntryPath(_directory, manifest);

        Assert.Equal(Path.GetFullPath(Path.Combine(_directory, "MyPlugin")), path);
    }
}
