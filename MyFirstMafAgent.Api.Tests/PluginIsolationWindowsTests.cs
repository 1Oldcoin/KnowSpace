using System.Text;
using MyFirstMafAgent.Api.Plugins.Isolation;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// Windows 上的 AppContainer 真机校验（其它平台直接跳过）。
///
/// <para>
/// 这些用例会真的建 AppContainer 配置、改目录 ACL、在容器里启动进程——
/// 隔离这种事模拟不出来，只能真跑。它们同时钉住两条实测结论：
/// 一是<b>只授叶子目录就够</b>（祖先不需要、也不能碰）；
/// 二是<b>Program Files 下的运行时默认读不到</b>，必须给出可照做的报错。
/// </para>
/// </summary>
public sealed class PluginIsolationWindowsTests : IDisposable
{
    private readonly string _root = Path.Combine(@"C:\Temp", "maf-container-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _profiles = [];

    public void Dispose()
    {
        foreach (var profile in _profiles) WindowsAppContainer.TryDeleteProfile(profile);

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void Creating_a_profile_twice_returns_the_same_sid()
    {
        if (!OperatingSystem.IsWindows()) return;

        var name = NewProfileName();
        var first = WindowsAppContainer.EnsureProfile(name, out var created);
        var second = WindowsAppContainer.EnsureProfile(name, out var createdAgain);

        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first, second);
        Assert.StartsWith("S-1-15-2-", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Grants_make_a_directory_readable_and_are_not_rewritten()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pluginDirectory = Path.Combine(_root, "versions", "1.0.0");
        var dataDirectory = Path.Combine(_root, ".data");
        Directory.CreateDirectory(pluginDirectory);
        Directory.CreateDirectory(dataDirectory);
        var payload = Path.Combine(pluginDirectory, "plugin.js");
        File.WriteAllText(payload, "console.log('hi')");

        var sid = WindowsAppContainer.EnsureProfile(NewProfileName(), out _);

        // 授权之前：普通用户目录默认只有当前用户与 Users 组，容器读不到。
        Assert.False(WindowsAppContainer.IsReadableByContainer(payload, sid));

        var plan = PluginIsolationPlanner.Build(pluginDirectory, dataDirectory, []);
        var applied = WindowsAppContainer.ApplyGrants(sid, plan.Grants);
        Assert.Equal(2, applied.Count);
        Assert.True(WindowsAppContainer.IsReadableByContainer(payload, sid));

        // 第二次执行同样的计划不应该再写 ACL（SetAccessControl 会触发递归重整继承）。
        Assert.Empty(WindowsAppContainer.ApplyGrants(sid, plan.Grants));
    }

    [Fact]
    public void A_runtime_under_program_files_is_refused_with_actionable_text()
    {
        if (!OperatingSystem.IsWindows()) return;

        var node = @"C:\Program Files\nodejs\node.exe";
        if (!File.Exists(node)) return;

        var sid = WindowsAppContainer.EnsureProfile(NewProfileName(), out _);

        // 实测：C:\Program Files\nodejs 没有 ALL APPLICATION PACKAGES，容器读不到。
        Assert.False(WindowsAppContainer.IsReadableByContainer(node, sid));
    }

    [Fact]
    public void Container_process_runs_with_stdin_stdout_and_a_readable_working_directory()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pluginDirectory = Path.Combine(_root, "versions", "1.0.0");
        var dataDirectory = Path.Combine(_root, ".data");
        Directory.CreateDirectory(pluginDirectory);
        Directory.CreateDirectory(dataDirectory);

        var marker = Path.Combine(pluginDirectory, "marker.txt");
        File.WriteAllText(marker, "hello-from-container");

        var sid = WindowsAppContainer.EnsureProfile(NewProfileName(), out _);
        var plan = PluginIsolationPlanner.Build(pluginDirectory, dataDirectory, []);
        WindowsAppContainer.ApplyGrants(sid, plan.Grants);

        var environment = PluginProcessFactory.BuildEnvironment(
            new Dictionary<string, string>(), plan.EnvironmentOverrides);

        // 1) 插件目录里的文件可读，且能把它当工作目录。
        var read = Run(sid, pluginDirectory, environment, ["/c", "type", marker], dataDirectory);
        Assert.True(read.Exited);
        Assert.Equal(0, read.ExitCode);
        Assert.Contains("hello-from-container", read.StandardOutput, StringComparison.Ordinal);

        // 2) 数据目录可写（重定向后的 TEMP 也在里面）。
        var write = Run(sid, dataDirectory, environment, ["/c", "echo", "written-by-container", ">", "out.txt"], dataDirectory);
        Assert.True(write.Exited);
        Assert.True(File.Exists(Path.Combine(dataDirectory, "out.txt")), "容器没能在数据目录写文件");

        // 3) 没授权的地方仍然读不到——这是隔离的本体。
        var denied = Run(sid, dataDirectory, environment, ["/c", "dir", "/b", @"C:\Program Files\nodejs"], dataDirectory);
        Assert.True(denied.Exited);
        Assert.NotEqual(0, denied.ExitCode);
    }

    private static (bool Exited, int ExitCode, string StandardOutput) Run(
        string sid,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string[] arguments,
        string? dataDirectory)
    {
        using var process = AppContainerPluginProcess.Start(
            new PluginProcess.StartSpec(
                "probe",
                @"C:\Windows\System32\cmd.exe",
                arguments,
                workingDirectory,
                environment,
                workingDirectory,
                dataDirectory,
                []),
            sid,
            [],
            PluginIsolationReport.NotStarted("require", "probe"));

        using var reader = new StreamReader(process.Output, Encoding.UTF8, false, 4096, leaveOpen: true);
        var stdout = reader.ReadToEnd();
        var exited = process.WaitForExit(30_000);

        return (exited, exited ? process.ExitCode : -1, stdout);
    }

    private string NewProfileName()
    {
        var name = $"MyFirstMafAgent.Test.{Guid.NewGuid():N}";
        _profiles.Add(name);
        return name;
    }
}
