using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

public sealed class PluginManifestTests
{
    private const string ValidManifest = """
    {
      "schemaVersion": "1.0",
      "id": "com.example.notes",
      "name": "示例笔记",
      "version": "1.2.3",
      "publisher": "Example",
      "entry": { "command": "plugin.exe", "arguments": [] },
      "runtime": "dotnet",
      "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" },
      "capabilities": [
        { "id": "notes.search", "kind": "agent-tool", "name": "搜索", "parameters": "{\"type\":\"object\"}", "permissions": ["network"], "sideEffect": "read" }
      ],
      "limits": { "maxMemoryMb": 256, "maxConcurrentInvocations": 2, "invocationTimeoutMs": 15000, "maxRequestBytes": 262144, "maxRetries": 0 },
      "integrity": { "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }
    }
    """;

    [Fact]
    public void Valid_manifest_passes()
    {
        var (manifest, result) = PluginManifestValidator.Parse(ValidManifest);

        Assert.NotNull(manifest);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("com.example.notes", manifest!.Id);
    }

    [Theory]
    [InlineData("com.example.notes")]
    [InlineData("io.github.someone.plugin-x")]
    [InlineData("a.b")]
    public void Reverse_domain_ids_are_accepted(string id)
    {
        var json = ValidManifest.Replace("com.example.notes", id, StringComparison.Ordinal);

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("Com.Example.Notes")]
    [InlineData("example")]
    [InlineData("com..notes")]
    [InlineData("-bad.id")]
    [InlineData("com.example/notes")]
    public void Non_reverse_domain_ids_are_rejected(string id)
    {
        var json = ValidManifest.Replace("com.example.notes", id, StringComparison.Ordinal);

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Incompatible_protocol_range_is_rejected()
    {
        var json = ValidManifest.Replace("\"minVersion\": \"1.0.0\"", "\"minVersion\": \"2.0.0\"", StringComparison.Ordinal)
            .Replace("\"maxVersion\": \"1.0.999\"", "\"maxVersion\": \"2.1.0\"", StringComparison.Ordinal);

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("incompatible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Duplicate_capability_ids_are_rejected()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "id": "com.example.notes",
          "name": "n",
          "version": "1.0.0",
          "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
          "capabilities": [
            { "id": "a.b", "kind": "agent-tool", "name": "x", "parameters": "{\"type\":\"object\"}", "sideEffect": "read" },
            { "id": "a.b", "kind": "agent-tool", "name": "y", "parameters": "{\"type\":\"object\"}", "sideEffect": "read" }
          ]
        }
        """;

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("duplicated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_capability_kind_and_permission_are_rejected()
    {
        var json = ValidManifest
            .Replace("\"kind\": \"agent-tool\"", "\"kind\": \"magic\"", StringComparison.Ordinal)
            .Replace("\"network\"", "\"telepathy\"", StringComparison.Ordinal);

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unknown kind", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("unknown permission", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Limits_above_core_maximum_are_rejected()
    {
        var json = ValidManifest.Replace("\"maxMemoryMb\": 256", "\"maxMemoryMb\": 65536", StringComparison.Ordinal);

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("maxMemoryMb", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_integrity_hash_is_a_warning_not_an_error()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "id": "com.example.notes",
          "name": "n",
          "version": "1.0.0",
          "publisher": "p",
          "entry": { "command": "plugin.exe" },
          "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" },
          "capabilities": [
            { "id": "a.b", "kind": "agent-tool", "name": "x", "parameters": "{\"type\":\"object\"}", "sideEffect": "read" }
          ]
        }
        """;

        var (_, result) = PluginManifestValidator.Parse(json);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("integrity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Invalid_json_is_reported_without_throwing()
    {
        var (manifest, result) = PluginManifestValidator.Parse("{ not json ");

        Assert.Null(manifest);
        Assert.False(result.IsValid);
    }
}

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.0.0-rc1", "1.0.0", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    public void Versions_compare_numerically_and_respect_prerelease(string left, string right, int expectedSign)
    {
        var result = SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right));

        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void Invalid_versions_are_rejected(string value)
    {
        Assert.False(SemanticVersion.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.999", true)]
    [InlineData("2.0.0", "3.0.0", false)]
    public void Protocol_range_membership_is_inclusive(string min, string max, bool expected)
    {
        var range = new ProtocolRange(min, max);

        var actual = range.Contains(SemanticVersion.Parse(PluginProtocol.ProtocolVersion), out _);

        Assert.Equal(expected, actual);
    }
}

public sealed class PlatformIngestionItemTests
{
    [Fact]
    public void Valid_note_produces_no_errors()
    {
        var item = new PlatformIngestionItem
        {
            Source = "com.example",
            ExternalId = "n-1",
            Title = "t",
            Content = "content"
        };

        Assert.Empty(item.Validate());
    }

    [Fact]
    public void Missing_source_and_external_id_are_reported()
    {
        var item = new PlatformIngestionItem { Content = "x" };

        var errors = item.Validate();

        Assert.Contains(errors, error => error.Contains("source", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("externalId", StringComparison.Ordinal));
    }

    [Fact]
    public void Retracted_item_may_have_empty_content()
    {
        var item = new PlatformIngestionItem
        {
            Source = "com.example",
            ExternalId = "n-1",
            Retracted = true
        };

        Assert.Empty(item.Validate());
    }

    [Fact]
    public void Unknown_type_and_bad_hash_are_reported()
    {
        var item = new PlatformIngestionItem
        {
            Source = "com.example",
            ExternalId = "n-1",
            Type = "video",
            Content = "x",
            ContentHash = "not-a-hash"
        };

        var errors = item.Validate();

        Assert.Contains(errors, error => error.Contains("unknown", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("contentHash", StringComparison.Ordinal));
    }

    [Fact]
    public void Content_hash_is_stable_and_hex()
    {
        var first = PlatformIngestionItem.ComputeContentHash("内容");
        var second = PlatformIngestionItem.ComputeContentHash("内容");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.All(first, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void WithComputedHash_fills_missing_hash_only()
    {
        var item = new PlatformIngestionItem { Source = "s", ExternalId = "e", Content = "c" };

        var filled = item.WithComputedHash();

        Assert.NotNull(filled.ContentHash);
        Assert.Equal(filled.ContentHash, filled.WithComputedHash().ContentHash);
    }
}

public sealed class PluginToolNamingTests
{
    [Fact]
    public void Plugin_tool_names_are_prefixed_sanitized_and_unique()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = PluginToolSource.BuildToolName("com.example.notes", "notes.search", used);
        var second = PluginToolSource.BuildToolName("com.example.notes", "notes.search", used);

        Assert.Equal("plugin__com_example_notes__notes_search", first);
        Assert.Equal("plugin__com_example_notes__notes_search_2", second);
        Assert.True(first.Length <= 64);
    }

    [Fact]
    public void Non_ascii_capability_ids_are_sanitized()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var name = PluginToolSource.BuildToolName("com.example", "笔记/搜索", used);

        Assert.All(name, character => Assert.True(character < 128));
        Assert.StartsWith("plugin__com_example__", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_names_are_truncated_to_the_openai_limit()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var longId = new string('a', 200);

        var name = PluginToolSource.BuildToolName(longId, longId, used);

        Assert.True(name.Length <= 64);
    }
}

public sealed class PluginManifestLoaderTests
{
    [Fact]
    public void Missing_manifest_is_reported_as_unusable()
    {
        var directory = CreateTempDirectory();
        try
        {
            var loader = new PluginManifestLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginManifestLoader>.Instance);

            var result = loader.Load(directory);

            Assert.False(result.IsUsable);
            Assert.Contains(result.Validation.Errors, error => error.Contains("manifest.json", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_entry_binary_makes_the_plugin_unusable()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"), BuildManifest("missing.exe"));
            var loader = new PluginManifestLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginManifestLoader>.Instance);

            var result = loader.Load(directory);

            Assert.False(result.IsUsable);
            Assert.Contains(result.Validation.Errors, error => error.Contains("not found", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Existing_entry_binary_makes_the_plugin_usable()
    {
        var directory = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.json"), BuildManifest("plugin.exe"));
            File.WriteAllText(Path.Combine(directory, "plugin.exe"), "stub");
            var loader = new PluginManifestLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginManifestLoader>.Instance);

            var result = loader.Load(directory);

            Assert.True(result.IsUsable, string.Join("; ", result.Validation.Errors));
            Assert.Equal("com.example.notes", result.Id);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Host_command_resolves_to_the_first_dll_argument()
    {
        var manifest = new PluginManifest
        {
            Entry = new PluginEntry { Command = "dotnet", Arguments = ["plugin.dll", "--flag"] }
        };

        var path = PluginManifestLoader.ResolveEntryPath(@"C:\plugins\demo", manifest);

        Assert.Equal(Path.GetFullPath(@"C:\plugins\demo\plugin.dll"), path);
    }

    [Fact]
    public void Scan_ignores_hidden_directories()
    {
        var directory = CreateTempDirectory();
        try
        {
            var hidden = Path.Combine(directory, ".data");
            Directory.CreateDirectory(hidden);
            var loader = new PluginManifestLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginManifestLoader>.Instance);

            var results = loader.Scan(directory);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maf-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildManifest(string command) => $$"""
    {
      "schemaVersion": "1.0",
      "id": "com.example.notes",
      "name": "n",
      "version": "1.0.0",
      "publisher": "p",
      "entry": { "command": "{{command}}" },
      "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.0" },
      "capabilities": [
        { "id": "a.b", "kind": "agent-tool", "name": "x", "parameters": "{\"type\":\"object\"}", "sideEffect": "read" }
      ]
    }
    """;
}

/// <summary>
/// 协议层测试：用内存流驱动服务端分派，避免依赖真实子进程；
/// 真实进程链路由 scripts/Test-PluginContract.ps1 覆盖。
/// </summary>
public sealed class PluginRpcProtocolTests
{
    [Fact]
    public async Task Server_handles_initialize_invoke_health_and_shutdown()
    {
        var requests = string.Join("\n",
            """{"jsonrpc":"2.0","id":"1","method":"plugin/initialize","params":{"protocolVersion":"1.0.0","coreVersion":"10.0"}}""",
            """{"jsonrpc":"2.0","id":"2","method":"plugin/capabilities/invoke","params":{"capabilityId":"demo.echo","arguments":{"text":"hi"}}}""",
            """{"jsonrpc":"2.0","id":"3","method":"plugin/health"}""",
            """{"jsonrpc":"2.0","id":"4","method":"plugin/capabilities/list"}""",
            string.Empty);

        var responses = await RunServerAsync(requests);

        Assert.Equal("plugin-id", responses.Result("1")!["pluginId"]!.GetValue<string>());
        Assert.Equal("hi", responses.Result("2")!["result"]!["echo"]!.GetValue<string>());
        Assert.Equal("healthy", responses.Result("3")!["status"]!.GetValue<string>());
        Assert.Single(responses.Result("4")!["capabilities"]!.AsArray());
    }

    [Fact]
    public async Task Unknown_method_returns_method_not_found()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":"1","method":"plugin/does-not-exist"}""" + "\n");

        Assert.Equal(PluginProtocol.ErrorMethodNotFound, responses["1"]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Invoke_before_initialize_is_rejected()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":"1","method":"plugin/capabilities/invoke","params":{"capabilityId":"demo.echo"}}""" + "\n");

        Assert.Equal(PluginProtocol.ErrorNotConfigured, responses["1"]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Undeclared_capability_is_rejected()
    {
        var requests = string.Join("\n",
            """{"jsonrpc":"2.0","id":"1","method":"plugin/initialize","params":{"protocolVersion":"1.0.0"}}""",
            """{"jsonrpc":"2.0","id":"2","method":"plugin/capabilities/invoke","params":{"capabilityId":"demo.secret"}}""",
            string.Empty);

        var responses = await RunServerAsync(requests);

        Assert.Equal(PluginProtocol.ErrorUnknownCapability, responses["2"]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Incompatible_protocol_major_is_rejected()
    {
        var responses = await RunServerAsync(
            """{"jsonrpc":"2.0","id":"1","method":"plugin/initialize","params":{"protocolVersion":"2.0.0"}}""" + "\n");

        Assert.Equal(PluginProtocol.ErrorIncompatibleProtocol, responses["1"]!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Invalid_json_produces_a_parse_error()
    {
        var run = await RunServerAsync("{ not json }\n");

        var response = Assert.Single(run.All);
        Assert.Equal(PluginProtocol.ErrorParse, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task Client_times_out_and_sends_a_cancel_notification()
    {
        var output = new MemoryStream();
        await using var peer = new JsonRpcPeer(new MemoryStream(), output);
        peer.Start();

        var exception = await Assert.ThrowsAsync<PluginRpcException>(() =>
            peer.SendRequestAsync("plugin/slow", null, TimeSpan.FromMilliseconds(80), CancellationToken.None));

        Assert.Equal(PluginProtocol.ErrorCancelled, exception.Code);

        var written = ReadLines(output);
        Assert.Contains(written, line => line.Contains("\"plugin/slow\"", StringComparison.Ordinal));
        Assert.Contains(written, line => line.Contains("\"plugin/cancel\"", StringComparison.Ordinal));
        Assert.Contains(written, line => line.Contains("\"targetId\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_as_operation_cancelled()
    {
        var output = new MemoryStream();
        await using var peer = new JsonRpcPeer(new MemoryStream(), output);
        peer.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            peer.SendRequestAsync("plugin/slow", null, TimeSpan.FromSeconds(30), cts.Token));

        // 取消通知是挂在取消路径上的异步副产物，机器繁忙时比断言晚到，等一等再断言。
        await WaitForLineAsync(output, "\"plugin/cancel\"", TimeSpan.FromSeconds(5));
    }

    private static async Task WaitForLineAsync(MemoryStream output, string fragment, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ReadLines(output).Any(line => line.Contains(fragment, StringComparison.Ordinal))) return;
            await Task.Delay(25);
        }

        Assert.Contains(ReadLines(output), line => line.Contains(fragment, StringComparison.Ordinal));
    }

    private static async Task<ServerRun> RunServerAsync(string input)
    {
        var inputStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var outputStream = new MemoryStream();
        var plugin = new EchoPlugin();

        await PluginServer.RunAsync(plugin, [], inputStream, outputStream, redirectConsole: false, CancellationToken.None);

        var all = new List<JsonNode>();
        var byId = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var line in ReadLines(outputStream))
        {
            var node = JsonNode.Parse(line);
            if (node is null) continue;
            all.Add(node);
            if (node["id"] is { } id) byId[IdKey(id)] = node;
        }

        return new ServerRun(byId, all);
    }

    /// <summary>JSON-RPC 的 id 可以是字符串或数字，测试中统一用文本作为键。</summary>
    private static string IdKey(JsonNode id) =>
        id.GetValueKind() == JsonValueKind.String ? id.GetValue<string>() : id.ToJsonString();

    private sealed record ServerRun(Dictionary<string, JsonNode> ById, List<JsonNode> All)
    {
        public JsonNode this[string id] => ById[id];

        /// <summary>取 JSON-RPC 响应中的 result 节点。</summary>
        public JsonNode? Result(string id) => ById.TryGetValue(id, out var response) ? response["result"] : null;
    }

    /// <summary>测试用插件：只实现一个 echo 能力。</summary>
    private sealed class EchoPlugin : PluginBase
    {
        public override string Id => "plugin-id";

        public override string Version => "1.0.0";

        public override IReadOnlyList<PluginCapability> Capabilities { get; } =
        [
            new PluginCapability
            {
                Id = "demo.echo",
                Kind = PluginCapabilityKinds.AgentTool,
                Name = "echo",
                Parameters = """{"type":"object"}""",
                SideEffect = PluginSideEffectLevels.Read
            }
        ];

        public override Task<JsonNode?> InvokeAsync(string capabilityId, JsonNode? arguments, CancellationToken cancellationToken)
        {
            var text = arguments?["text"]?.GetValue<string>() ?? string.Empty;
            return Task.FromResult<JsonNode?>(new JsonObject { ["echo"] = text });
        }
    }

    private static List<string> ReadLines(MemoryStream stream)
    {
        var text = Encoding.UTF8.GetString(stream.ToArray());
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}
