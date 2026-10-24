using MyFirstMafAgent.Api.Plugins.Isolation;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// D-1：隔离计划的纯逻辑。这些用例钉住的是「声明哪些权限 → 给容器什么」的映射，
/// 尤其"没有 network 就不给 internetClient"这一条，它是整个功能的意义所在。
/// </summary>
public sealed class PluginIsolationPlannerTests
{
    private static readonly string PluginDirectory =
        Path.GetFullPath(Path.Combine("temp", "plugins", "com.example.notes", "versions", "1.0.0"));

    private static readonly string DataDirectory =
        Path.GetFullPath(Path.Combine("temp", "plugins", ".data", "com.example.notes"));

    [Fact]
    public void Network_permission_grants_the_internet_client_capability()
    {
        var plan = PluginIsolationPlanner.Build(PluginDirectory, DataDirectory, [PluginPermissions.Network]);

        Assert.True(plan.Network);
        Assert.Equal([PluginIsolationPlanner.InternetClientSid], plan.CapabilitySids);
    }

    [Fact]
    public void Without_network_permission_no_network_capability_is_attached()
    {
        // 这是 D-1 的核心承诺：没授权就是真的没有出站网络，而不是"口头禁止"。
        var plan = PluginIsolationPlanner.Build(
            PluginDirectory,
            DataDirectory,
            [PluginPermissions.FileRead, PluginPermissions.Process]);

        Assert.False(plan.Network);
        Assert.Empty(plan.CapabilitySids);
    }

    [Fact]
    public void Permission_names_are_case_insensitive()
    {
        var plan = PluginIsolationPlanner.Build(PluginDirectory, DataDirectory, ["NETWORK"]);

        Assert.True(plan.Network);
        Assert.Single(plan.CapabilitySids);
    }

    [Fact]
    public void Plugin_directory_is_read_only_and_data_directory_is_writable()
    {
        var plan = PluginIsolationPlanner.Build(PluginDirectory, DataDirectory, []);

        Assert.Contains(plan.Grants, grant =>
            grant.Path == PluginDirectory && grant.Kind == PluginIsolationGrantKinds.ReadExecute);
        Assert.Contains(plan.Grants, grant =>
            grant.Path == DataDirectory && grant.Kind == PluginIsolationGrantKinds.Modify);

        // 插件目录本身绝不能出现在可写清单里。
        Assert.DoesNotContain(plan.Grants, grant =>
            grant.Path == PluginDirectory && grant.Kind == PluginIsolationGrantKinds.Modify);
    }

    [Fact]
    public void Ancestors_are_never_touched()
    {
        // 实测结论：容器凭叶子目录上的继承 ACE 就能穿越整条路径，
        // 而给祖先（尤其 C:\）写 ACL 会让 Windows 在整个驱动器上递归重整继承，
        // 实测卡死数分钟。所以计划里只允许出现我们明确要授权的目录。
        var plan = PluginIsolationPlanner.Build(PluginDirectory, DataDirectory, []);

        var expected = new[] { PluginDirectory, DataDirectory };
        Assert.All(plan.Grants, grant =>
            Assert.Contains(grant.Path, expected, StringComparer.OrdinalIgnoreCase));

        Assert.DoesNotContain(plan.Grants, grant => grant.Kind == PluginIsolationGrantKinds.Traverse);
        Assert.DoesNotContain(plan.Grants, grant =>
            string.Equals(grant.Path, Path.GetPathRoot(PluginDirectory), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_same_path_keeps_only_the_strongest_grant()
    {
        var plan = PluginIsolationPlanner.Build(
            PluginDirectory,
            DataDirectory,
            [],
            readableRoots: [PluginDirectory]);

        var grants = plan.Grants.Where(grant => grant.Path == PluginDirectory).ToList();
        Assert.Single(grants);
        Assert.Equal(PluginIsolationGrantKinds.ReadExecute, grants[0].Kind);
    }

    [Fact]
    public void Redirected_user_directories_live_under_the_plugin_data_directory()
    {
        var plan = PluginIsolationPlanner.Build(PluginDirectory, DataDirectory, []);

        Assert.Equal(DataDirectory, plan.EnvironmentOverrides["USERPROFILE"]);
        Assert.Equal(Path.Combine(DataDirectory, "temp"), plan.EnvironmentOverrides["TEMP"]);
        Assert.Equal(Path.Combine(DataDirectory, "temp"), plan.EnvironmentOverrides["TMP"]);
        Assert.StartsWith(DataDirectory, plan.EnvironmentOverrides["LOCALAPPDATA"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Redirect_can_be_turned_off()
    {
        var plan = PluginIsolationPlanner.Build(
            PluginDirectory,
            DataDirectory,
            [],
            redirectUserDirectories: false);

        Assert.Empty(plan.EnvironmentOverrides);
    }

    [Theory]
    [InlineData(null, true, PluginIsolationMode.Require)]
    [InlineData("", true, PluginIsolationMode.Require)]
    [InlineData(null, false, PluginIsolationMode.Warn)]
    [InlineData("off", true, PluginIsolationMode.Off)]
    [InlineData("none", true, PluginIsolationMode.Off)]
    [InlineData("warn", true, PluginIsolationMode.Warn)]
    [InlineData("require", true, PluginIsolationMode.Require)]
    [InlineData("REQUIRED", true, PluginIsolationMode.Require)]
    [InlineData("garbage", true, PluginIsolationMode.Require)]
    [InlineData("garbage", false, PluginIsolationMode.Warn)]
    public void Mode_parsing_falls_back_to_platform_defaults(string? configured, bool isWindows, PluginIsolationMode expected)
    {
        Assert.Equal(expected, PluginIsolationPlanner.ResolveMode(configured, isWindows));
    }

    [Fact]
    public void Runtime_root_is_granted_read_only()
    {
        var runtimeRoot = Path.GetFullPath(Path.Combine("temp", "plugins", ".runtimes"));

        var plan = PluginIsolationPlanner.Build(
            PluginDirectory,
            DataDirectory,
            [],
            runtimeRoot: runtimeRoot);

        Assert.Contains(plan.Grants, grant =>
            grant.Path == runtimeRoot && grant.Kind == PluginIsolationGrantKinds.ReadExecute);
    }
}

/// <summary>启动规格的细节：命令行拼接、环境变量收窄、可执行文件解析。</summary>
public sealed class PluginProcessLaunchTests
{
    [Theory]
    [InlineData("server.js", "server.js")]
    [InlineData("C:\\plugins\\a b\\srv.js", "\"C:\\plugins\\a b\\srv.js\"")]
    [InlineData("--flag", "--flag")]
    public void Command_line_quotes_only_when_needed(string argument, string expected)
    {
        var line = AppContainerPluginProcess.BuildCommandLine("node", [argument]);

        Assert.Equal("node " + expected, line);
    }

    [Fact]
    public void Command_line_escapes_embedded_quotes_and_trailing_backslashes()
    {
        // 结尾反斜杠 + 引号是最容易拼错的组合（"C:\dir\" 会把后面的引号吃掉）。
        var line = AppContainerPluginProcess.BuildCommandLine("node", ["C:\\dir with space\\", "say \"hi\""]);

        Assert.Equal("node \"C:\\dir with space\\\\\" \"say \\\"hi\\\"\"", line);
    }

    [Fact]
    public void Environment_block_is_sorted_and_double_null_terminated()
    {
        var block = EnvironmentBlock.Build(new Dictionary<string, string>
        {
            ["ZED"] = "1",
            ["alpha"] = "2",
            ["MID"] = "3"
        });

        Assert.Equal("alpha=2\0MID=3\0ZED=1\0\0", block);
    }

    [Fact]
    public void Container_environment_drops_variables_that_are_not_allowlisted()
    {
        Environment.SetEnvironmentVariable("MYFIRSTMAFAGENT_TEST_SECRET", "leak-me");
        Environment.SetEnvironmentVariable("ConnectionStrings__KnowledgeBase", "Host=secret");

        try
        {
            var environment = PluginProcessFactory.BuildEnvironment(
                new Dictionary<string, string> { ["MYFIRSTMAFAGENT_PLUGIN_ID"] = "demo" },
                new Dictionary<string, string>());

            // 连接串不能漏给第三方程序。
            Assert.False(environment.ContainsKey("ConnectionStrings__KnowledgeBase"));

            // 但核心自己注入的插件身份必须带上。
            Assert.Equal("demo", environment["MYFIRSTMAFAGENT_PLUGIN_ID"]);
            Assert.Equal("leak-me", environment["MYFIRSTMAFAGENT_TEST_SECRET"]);

            // 系统必需变量要保留，否则运行时连自己的 DLL 都找不到。
            Assert.True(environment.ContainsKey("SystemRoot") || environment.ContainsKey("PATH"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYFIRSTMAFAGENT_TEST_SECRET", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__KnowledgeBase", null);
        }
    }

    [Fact]
    public void Bare_host_command_is_resolved_through_path()
    {
        var resolved = PluginProcessFactory.ResolveExecutable("cmd");

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved));
        Assert.True(Path.IsPathRooted(resolved));
    }

    [Fact]
    public void Unknown_command_resolves_to_null()
    {
        Assert.Null(PluginProcessFactory.ResolveExecutable("definitely-not-a-real-command-42"));
    }

    [Fact]
    public void Missing_absolute_path_resolves_to_null()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-plugin-" + Guid.NewGuid().ToString("N") + ".exe");

        Assert.Null(PluginProcessFactory.ResolveExecutable(missing));
    }
}
