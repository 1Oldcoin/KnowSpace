using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Settings;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>用户级智能体开关：默认值、Create 兜底与旧 JSON 兼容。</summary>
public sealed class AgentPreferencesTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Default_Disables_tool_calling_and_web_search_but_allows_file_access()
    {
        var defaults = AgentPreferences.Default;

        Assert.False(defaults.EnableToolCalling);
        Assert.False(defaults.EnableWebSearch);
        Assert.True(defaults.EnableFileAccess);

        // 直接 new 出来的值与 Default 一致，避免两处默认值漂移。
        var constructed = new AgentPreferences();
        Assert.Equal(defaults.EnableToolCalling, constructed.EnableToolCalling);
        Assert.Equal(defaults.EnableWebSearch, constructed.EnableWebSearch);
        Assert.Equal(defaults.EnableFileAccess, constructed.EnableFileAccess);
    }

    [Fact]
    public void Create_falls_back_to_defaults_for_null_values()
    {
        var preferences = AgentPreferences.Create(null, null, null);

        Assert.False(preferences.EnableToolCalling);
        Assert.False(preferences.EnableWebSearch);
        Assert.True(preferences.EnableFileAccess);
    }

    [Fact]
    public void Create_uses_explicit_values()
    {
        var preferences = AgentPreferences.Create(true, true, false);

        Assert.True(preferences.EnableToolCalling);
        Assert.True(preferences.EnableWebSearch);
        Assert.False(preferences.EnableFileAccess);
    }

    [Fact]
    public void Create_merges_partial_values_with_defaults()
    {
        var preferences = AgentPreferences.Create(true, null, null);

        Assert.True(preferences.EnableToolCalling);
        Assert.False(preferences.EnableWebSearch);
        Assert.True(preferences.EnableFileAccess);
    }

    [Fact]
    public void Stored_settings_serialize_round_trip_keeps_agent_preferences()
    {
        var original = new StoredAppSettings(
            new LlmOptions(),
            UserPreferences.Create("Lin", 60),
            null,
            AgentPreferences.Create(true, true, false));

        var json = JsonSerializer.Serialize(original, WebJson);
        var restored = JsonSerializer.Deserialize<StoredAppSettings>(json, WebJson);

        Assert.Contains("\"agent\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(restored);
        Assert.NotNull(restored!.Agent);
        Assert.True(restored.Agent!.EnableToolCalling);
        Assert.True(restored.Agent.EnableWebSearch);
        Assert.False(restored.Agent.EnableFileAccess);
    }

    [Fact]
    public void Legacy_settings_json_without_agent_field_deserializes_with_null_agent()
    {
        // 旧版本持久化的 JSON：只有 llm 与 preferences，没有 agent 字段。
        const string legacyJson = """
        {"llm":{"useCloudApi":false,"local":{"endpoint":"http://127.0.0.1:8080/v1","model":"qwen","apiKey":""},"online":{"endpoint":"https://api.example.com/v1","model":"gpt","apiKey":""}},"preferences":{"displayName":"Lin","dailyGoalMinutes":60},"embedding":null}
        """;

        var restored = JsonSerializer.Deserialize<StoredAppSettings>(legacyJson, WebJson);

        Assert.NotNull(restored);
        Assert.Null(restored!.Agent);
        // 仓储层的兜底后再补一次默认值。
        Assert.Same(AgentPreferences.Default, restored.Agent ?? AgentPreferences.Default);
    }

    [Fact]
    public void Snapshot_create_defaults_agent_preferences_when_omitted()
    {
        var snapshot = SettingsSnapshot.Create(
            new LlmOptions(),
            UserPreferences.Create(null, null),
            new RuntimeSettings(string.Empty, 512, string.Empty, 0, 0),
            new EmbeddingSettingsSnapshot(null, null, [], []));

        Assert.NotNull(snapshot.Agent);
        Assert.False(snapshot.Agent.EnableToolCalling);
        Assert.False(snapshot.Agent.EnableWebSearch);
        Assert.True(snapshot.Agent.EnableFileAccess);
    }
}

/// <summary>工具注册表按用户偏好过滤（全局开关是总闸）。</summary>
public sealed class AgentToolRegistryPreferenceTests
{
    [Fact]
    public async Task No_preferences_keeps_legacy_behaviour()
    {
        var registry = CreateRegistry(globalEnabled: true,
            new StubToolSource(("web_search", "web"), ("workspace_read_file", "workspace"), ("search_knowledge_base", "knowledge")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None);

        Assert.Equal(3, tools.Count);
    }

    [Fact]
    public async Task Null_preferences_ignores_user_switches_but_respects_global_switch()
    {
        var enabled = CreateRegistry(globalEnabled: true, new StubToolSource(("web_search", "web")));
        var disabled = CreateRegistry(globalEnabled: false, new StubToolSource(("web_search", "web")));

        Assert.Single(await enabled.GetToolsAsync(Context(), CancellationToken.None));
        Assert.Empty(await disabled.GetToolsAsync(Context(), CancellationToken.None));
    }

    [Fact]
    public async Task User_switch_off_returns_no_tools()
    {
        var registry = CreateRegistry(globalEnabled: true, new StubToolSource(("search_knowledge_base", "knowledge")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None,
            AgentPreferences.Create(enableToolCalling: false, enableWebSearch: true, enableFileAccess: true));

        Assert.Empty(tools);
    }

    [Fact]
    public async Task Global_switch_is_master_switch_over_user_preferences()
    {
        var registry = CreateRegistry(globalEnabled: false, new StubToolSource(("search_knowledge_base", "knowledge")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None,
            AgentPreferences.Create(enableToolCalling: true, enableWebSearch: true, enableFileAccess: true));

        Assert.Empty(tools);
    }

    [Fact]
    public async Task Web_and_file_switches_filter_categories_independently()
    {
        var registry = CreateRegistry(globalEnabled: true, new StubToolSource(
            ("web_search", "web"),
            ("fetch_url", "web"),
            ("workspace_read_file", "workspace"),
            ("local_read_file", "files"),
            ("search_knowledge_base", "knowledge"),
            ("avatar_get_persona", "avatar")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None,
            AgentPreferences.Create(enableToolCalling: true, enableWebSearch: false, enableFileAccess: false));

        var names = tools.Select(tool => tool.Name).ToArray();
        Assert.Equal(new[] { "search_knowledge_base", "avatar_get_persona" }, names);
    }

    [Fact]
    public async Task File_switch_off_still_allows_web_search_when_enabled()
    {
        var registry = CreateRegistry(globalEnabled: true, new StubToolSource(
            ("web_search", "web"),
            ("workspace_list_files", "workspace"),
            ("read_document_content", "knowledge")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None,
            AgentPreferences.Create(enableToolCalling: true, enableWebSearch: true, enableFileAccess: false));

        Assert.Equal(new[] { "web_search", "read_document_content" }, tools.Select(tool => tool.Name).ToArray());
    }

    [Fact]
    public async Task Failing_source_does_not_break_other_sources()
    {
        var registry = CreateRegistry(globalEnabled: true, new FailingToolSource(), new StubToolSource(("web_search", "web")));

        var tools = await registry.GetToolsAsync(Context(), CancellationToken.None,
            AgentPreferences.Create(true, true, true));

        Assert.Single(tools);
    }

    private static AgentToolContext Context() => new(Guid.NewGuid());

    private static AgentToolRegistry CreateRegistry(bool globalEnabled, params IAgentToolSource[] sources) =>
        new(sources,
            Microsoft.Extensions.Options.Options.Create(new AgentOptions { EnableToolCalling = globalEnabled }),
            NullLogger<AgentToolRegistry>.Instance);

    private sealed class StubToolSource(params (string Name, string Category)[] tools) : IAgentToolSource
    {
        public string Name => "stub";

        public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(
            AgentToolContext context, CancellationToken cancellationToken)
        {
            IReadOnlyList<AgentToolDefinition> definitions =
                tools.Select(tool => new AgentToolDefinition(
                        tool.Name,
                        $"{tool.Name} 的说明",
                        """{"type":"object","properties":{},"required":[]}""",
                        (_, _) => Task.FromResult("ok"),
                        tool.Category))
                    .ToList();
            return Task.FromResult(definitions);
        }
    }

    private sealed class FailingToolSource : IAgentToolSource
    {
        public string Name => "failing";

        public Task<IReadOnlyList<AgentToolDefinition>> GetToolsAsync(
            AgentToolContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("来源不可用");
    }
}

/// <summary>用户偏好读取器：默认值、缓存与失效。</summary>
public sealed class AgentPreferenceProviderTests
{
    [Fact]
    public async Task GetAsync_returns_defaults_when_nothing_stored()
    {
        var provider = new AgentPreferenceProvider(new RecordingSettingsRepository(null));

        var preferences = await provider.GetAsync(CancellationToken.None);

        Assert.False(preferences.EnableToolCalling);
        Assert.False(preferences.EnableWebSearch);
        Assert.True(preferences.EnableFileAccess);
    }

    [Fact]
    public async Task GetAsync_caches_until_invalidated()
    {
        var repository = new RecordingSettingsRepository(Stored(enableToolCalling: false));
        var provider = new AgentPreferenceProvider(repository);

        await provider.GetAsync(CancellationToken.None);
        await provider.GetAsync(CancellationToken.None);
        Assert.Equal(1, repository.Reads);

        // 模拟设置保存：仓储内容变了，但缓存未失效前仍返回旧值。
        repository.Stored = Stored(enableToolCalling: true);
        Assert.False((await provider.GetAsync(CancellationToken.None)).EnableToolCalling);

        provider.Invalidate();
        Assert.True((await provider.GetAsync(CancellationToken.None)).EnableToolCalling);
        Assert.Equal(2, repository.Reads);
    }

    private static StoredAppSettings Stored(bool enableToolCalling) => new(
        new LlmOptions(),
        UserPreferences.Create(null, null),
        null,
        new AgentPreferences { EnableToolCalling = enableToolCalling, EnableWebSearch = false, EnableFileAccess = true });

    private sealed class RecordingSettingsRepository(StoredAppSettings? stored) : ISettingsRepository
    {
        public StoredAppSettings? Stored { get; set; } = stored;

        public int Reads { get; private set; }

        public Task<StoredAppSettings?> GetAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Stored);
        }

        public Task SaveAsync(StoredAppSettings settings, CancellationToken cancellationToken)
        {
            Stored = settings;
            return Task.CompletedTask;
        }
    }
}
