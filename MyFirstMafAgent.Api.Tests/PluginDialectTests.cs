using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// D-2：清单词汇别名 + 方言识别。
/// 这些用例钉住的是"别人的插件包丢进来就能用"这件事到底认哪些写法。
/// </summary>
public sealed class PluginManifestDialectTests
{
    [Fact]
    public void Mcp_dialect_is_declared_with_a_plain_string()
    {
        var (manifest, result) = PluginManifestValidator.Parse("""
        {
          "id": "filesystem",
          "protocol": "mcp",
          "entry": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
        }
        """);

        Assert.NotNull(manifest);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(PluginDialects.Mcp, manifest!.Dialect);

        // 缺的字段按保守默认值补上，并作为 warning 透出（不静默生效）。
        Assert.Equal("filesystem", manifest.Name);
        Assert.Equal("0.0.0", manifest.Version);
        Assert.Equal("unknown", manifest.Publisher);
        Assert.Contains(result.Warnings, warning => warning.Contains("version", StringComparison.Ordinal));
    }

    [Fact]
    public void Mcp_manifest_may_declare_no_capabilities()
    {
        var (manifest, result) = PluginManifestValidator.Parse("""
        { "id": "demo", "protocol": "mcp", "entry": { "command": "node", "arguments": ["server.js"] } }
        """);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.Capabilities);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void Native_manifest_still_requires_capabilities()
    {
        var (_, result) = PluginManifestValidator.Parse("""
        {
          "id": "com.example.empty", "name": "E", "version": "1.0.0", "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" }
        }
        """);

        Assert.Contains(result.Errors, error => error.Contains("at least one capability", StringComparison.Ordinal));
    }

    [Fact]
    public void Entry_accepts_args_cwd_env_and_a_command_array()
    {
        var (manifest, _) = PluginManifestValidator.Parse("""
        {
          "id": "demo", "protocol": "mcp",
          "entry": {
            "command": ["npx", "-y", "pkg"],
            "args": ["--flag"],
            "cwd": "server",
            "env": { "API_KEY": "k", "RETRIES": 3, "DEBUG": true }
          }
        }
        """);

        Assert.NotNull(manifest);
        Assert.Equal("npx", manifest!.Entry!.Command);
        Assert.Equal(["-y", "pkg", "--flag"], manifest.Entry.Arguments);
        Assert.Equal("server", manifest.Entry.WorkingDirectory);

        // 环境变量的值必须都是字符串：JSON 里写数字/布尔是很常见的。
        Assert.Equal("k", manifest.Entry.Env["API_KEY"]);
        Assert.Equal("3", manifest.Entry.Env["RETRIES"]);
        Assert.Equal("true", manifest.Entry.Env["DEBUG"]);
    }

    [Theory]
    [InlineData("net.outbound", new[] { "network" })]
    [InlineData("proc", new[] { "process" })]
    [InlineData("fs.read", new[] { "file-read" })]
    [InlineData("fs", new[] { "file-read", "file-write" })]
    [InlineData("clipboard", new[] { "clipboard" })]
    public void Permission_aliases_expand_to_internal_vocabulary(string alias, string[] expected)
    {
        Assert.True(PluginPermissions.TryNormalize(alias, out var expanded));
        Assert.Equal(expected, expanded);
    }

    [Fact]
    public void Capability_requires_and_fs_permission_are_normalized()
    {
        var (manifest, result) = PluginManifestValidator.Parse("""
        {
          "id": "com.example.sync", "name": "S", "version": "1.0.0", "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
          "capabilities": [
            { "id": "sync.run", "kind": "connector", "name": "Sync", "requires": ["net.outbound", "fs"], "sideEffect": "write" }
          ]
        }
        """);

        Assert.NotNull(manifest);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var capability = Assert.Single(manifest!.Capabilities);
        Assert.Equal(PluginCapabilityKinds.PlatformConnector, capability.Kind);
        Assert.Equal(PluginSideEffectLevels.WriteLocal, capability.SideEffect);
        Assert.Equal(["network", "file-read", "file-write"], capability.Permissions);
    }

    [Fact]
    public void Unknown_permission_is_preserved_and_reported()
    {
        var (_, result) = PluginManifestValidator.Parse("""
        {
          "id": "com.example.bad", "name": "B", "version": "1.0.0", "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
          "capabilities": [
            { "id": "a.b", "kind": "agent-tool", "name": "x", "requires": ["telepathy"], "sideEffect": "read" }
          ]
        }
        """);

        // 认不出来的词不能被"静默丢掉"，作者得看见自己写错了什么。
        Assert.Contains(result.Errors, error => error.Contains("telepathy", StringComparison.Ordinal));
    }

    [Fact]
    public void Input_schema_object_is_read_as_parameters()
    {
        var (manifest, result) = PluginManifestValidator.Parse("""
        {
          "id": "demo", "protocol": "mcp",
          "entry": { "command": "node", "arguments": ["server.js"] },
          "capabilities": [
            { "id": "read_file", "inputSchema": { "type": "object", "properties": { "path": { "type": "string" } } } }
          ]
        }
        """);

        Assert.NotNull(manifest);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        var capability = Assert.Single(manifest!.Capabilities);
        Assert.NotNull(capability.Parameters);
        Assert.Contains("\"path\"", capability.Parameters!, StringComparison.Ordinal);
        // 只写了 inputSchema 的能力默认按 agent-tool 处理（MCP 只有工具）。
        Assert.Equal(PluginCapabilityKinds.AgentTool, capability.Kind);
    }

    [Fact]
    public void Capability_list_may_be_plain_names()
    {
        var (manifest, _) = PluginManifestValidator.Parse("""
        {
          "id": "com.example.names", "name": "N", "version": "1.0.0", "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
          "capabilities": ["search", "Fetch Page"]
        }
        """);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Capabilities.Count);
        Assert.Equal("search", manifest.Capabilities[0].Id);
        Assert.Equal("fetch-page", manifest.Capabilities[1].Id);
        Assert.Equal("Fetch Page", manifest.Capabilities[1].Name);
    }

    [Fact]
    public void Unknown_dialect_is_an_error()
    {
        var (_, result) = PluginManifestValidator.Parse("""
        { "id": "demo", "protocol": "carrier-pigeon", "entry": { "command": "node" } }
        """);

        Assert.Contains(result.Errors, error => error.Contains("dialect", StringComparison.Ordinal));
    }

    [Fact]
    public void Top_level_permissions_are_kept_for_mcp_plugins()
    {
        var (manifest, result) = PluginManifestValidator.Parse("""
        {
          "id": "demo", "protocol": "mcp", "permissions": ["net.outbound"],
          "entry": { "command": "node", "arguments": ["server.js"] }
        }
        """);

        Assert.NotNull(manifest);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(["network"], manifest!.Permissions);
    }
}

/// <summary>清单文件名别名 + 目录热扫描。</summary>
public sealed class PluginDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maf-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("plugin.json")]
    [InlineData("skill.json")]
    public void Any_known_manifest_file_name_is_accepted(string fileName)
    {
        var directory = Path.Combine(_root, "demo");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), MinimalManifest());
        File.WriteAllText(Path.Combine(directory, "plugin.exe"), "stub");

        var load = CreateLoader().Load(directory);

        Assert.True(load.IsUsable, string.Join("; ", load.Validation.Errors));
        Assert.Equal("com.example.demo", load.Id);
    }

    [Fact]
    public void Scan_looks_through_a_single_wrapper_directory()
    {
        // 解压时常多出一层：plugins/demo/demo/manifest.json
        var wrapper = Path.Combine(_root, "demo", "demo");
        Directory.CreateDirectory(wrapper);
        File.WriteAllText(Path.Combine(wrapper, "manifest.json"), MinimalManifest());
        File.WriteAllText(Path.Combine(wrapper, "plugin.exe"), "stub");

        var results = CreateLoader().Scan(_root);

        var load = Assert.Single(results);
        Assert.True(load.IsUsable, string.Join("; ", load.Validation.Errors));
        Assert.Equal(wrapper, load.Directory);
    }

    [Fact]
    public void Scan_does_not_guess_when_there_are_several_candidates()
    {
        var wrapper = Path.Combine(_root, "demo");
        foreach (var name in new[] { "a", "b" })
        {
            var child = Path.Combine(wrapper, name);
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "manifest.json"), MinimalManifest());
        }

        var results = CreateLoader().Scan(_root);

        var load = Assert.Single(results);
        Assert.False(load.IsUsable);
    }

    [Fact]
    public void Host_command_without_a_file_argument_counts_as_present()
    {
        // npx -y @modelcontextprotocol/server-filesystem：入口由包管理器在运行期决定，
        // 安装期根本无从检查，不能因此判插件不可用。
        var directory = Path.Combine(_root, "fs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), """
        {
          "id": "filesystem", "protocol": "mcp",
          "entry": { "command": "npx", "arguments": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
        }
        """);

        var load = CreateLoader().Load(directory);

        Assert.True(load.EntryExists);
        Assert.True(load.IsUsable, string.Join("; ", load.Validation.Errors));
    }

    private static PluginManifestLoader CreateLoader() =>
        new(NullLogger<PluginManifestLoader>.Instance);

    private static string MinimalManifest() => """
    {
      "schemaVersion": "1.0",
      "id": "com.example.demo",
      "name": "Demo",
      "version": "1.0.0",
      "publisher": "Example",
      "entry": { "command": "plugin.exe" },
      "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
      "capabilities": [
        { "id": "demo.echo", "kind": "agent-tool", "name": "Echo", "parameters": "{\"type\":\"object\"}", "sideEffect": "read" }
      ]
    }
    """;
}

/// <summary>MCP 方言下取消通知要换成 MCP 的方法名与字段。</summary>
public sealed class McpCancellationTests
{
    [Fact]
    public async Task Mcp_dialect_sends_notifications_cancelled_with_request_id()
    {
        var output = new MemoryStream();
        await using var peer = new JsonRpcPeer(new SilentStdioStream(), output)
        {
            CancelNotificationMethod = McpProtocol.MethodCancelled,
            CancelNotificationParameters = id => new JsonObject
            {
                ["requestId"] = JsonNode.Parse(id.ToJsonString()),
                ["reason"] = "cancelled by host"
            }
        };
        peer.Start();

        await Assert.ThrowsAsync<PluginRpcException>(() =>
            peer.SendRequestAsync(McpProtocol.MethodCallTool, null, TimeSpan.FromMilliseconds(80), CancellationToken.None));

        // 取消通知是异步副作用，全量跑时可能晚到；轮询而不是立即断言。
        var written = await WaitForLineAsync(output, "\"notifications/cancelled\"", TimeSpan.FromSeconds(5));
        Assert.Contains(written, line => line.Contains("\"requestId\"", StringComparison.Ordinal));
        Assert.DoesNotContain(written, line => line.Contains("\"plugin/cancel\"", StringComparison.Ordinal));
    }

    private static async Task<List<string>> WaitForLineAsync(MemoryStream stream, string fragment, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var lines = ReadLines(stream);
            if (lines.Any(line => line.Contains(fragment, StringComparison.Ordinal))) return lines;
            await Task.Delay(25);
        }

        var final = ReadLines(stream);
        Assert.Contains(final, line => line.Contains(fragment, StringComparison.Ordinal));
        return final;
    }

    private static List<string> ReadLines(MemoryStream stream) =>
        Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
}
