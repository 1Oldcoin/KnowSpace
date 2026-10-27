using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>客户端密钥的生成、摘要与请求头解析。</summary>
public sealed class AgentClientKeyTests
{
    [Fact]
    public void Generated_keys_are_prefixed_and_unique()
    {
        var first = AgentClientKey.Create();
        var second = AgentClientKey.Create();

        Assert.StartsWith(AgentClientKey.Prefix, first.Key);
        Assert.NotEqual(first.Key, second.Key);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.Equal(64, first.Hash.Length);
    }

    [Fact]
    public void Display_prefix_is_short_enough_to_not_be_the_key()
    {
        var (key, _, display) = AgentClientKey.Create();

        Assert.True(display.Length < key.Length);
        Assert.StartsWith(display, key);
        Assert.StartsWith(AgentClientKey.Prefix, display);
    }

    [Fact]
    public void Hash_is_stable_and_case_sensitive()
    {
        const string key = "maf_abcdefghijklmnopqrstuvwxyz0123456789";

        Assert.Equal(AgentClientKey.Hash(key), AgentClientKey.Hash(key));
        Assert.NotEqual(AgentClientKey.Hash(key), AgentClientKey.Hash(key.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("Bearer maf_abc", null, "maf_abc")]
    [InlineData("bearer maf_abc", null, "maf_abc")]
    [InlineData(null, "maf_xyz", "maf_xyz")]
    [InlineData("Bearer   maf_spaced  ", null, "maf_spaced")]
    [InlineData("Basic abc", "maf_fallback", "maf_fallback")]
    [InlineData(null, null, null)]
    public void Key_is_extracted_from_authorization_or_api_key_header(
        string? authorization,
        string? apiKey,
        string? expected)
    {
        Assert.Equal(expected, AgentClientKey.ExtractFromHeaders(authorization, apiKey));
    }

    [Theory]
    [InlineData("maf_abcdefghijklmnop", true)]
    [InlineData("sk-abcdefghijklmnop", false)]
    [InlineData("maf_short", false)]
    [InlineData(null, false)]
    public void Only_plausible_keys_hit_the_database(string? candidate, bool expected)
    {
        Assert.Equal(expected, AgentClientKey.LooksLikeKey(candidate));
    }
}

/// <summary>能力匹配与工具目录。</summary>
public sealed class AgentCapabilityTests
{
    [Theory]
    [InlineData("knowledge:read", "knowledge:read", true)]
    [InlineData("knowledge:*", "knowledge:read", true)]
    [InlineData("knowledge:*", "knowledge:write", true)]
    [InlineData("*", "avatar:write", true)]
    [InlineData("knowledge:read", "knowledge:write", false)]
    [InlineData("knowledge:read", "files:read", false)]
    [InlineData("files:*", "knowledge:read", false)]
    public void Capability_matching_supports_exact_scope_and_wildcards(
        string granted,
        string required,
        bool expected)
    {
        Assert.Equal(expected, AgentClientCapabilities.Satisfies(granted, required));
    }

    [Fact]
    public void Every_catalogued_tool_requires_a_known_capability()
    {
        Assert.NotEmpty(McpToolCatalog.All);

        foreach (var descriptor in McpToolCatalog.All)
        {
            Assert.True(
                AgentClientCapabilities.IsKnown(descriptor.Capability),
                $"未知能力：{descriptor.Capability}（工具 {descriptor.Name}）");
        }
    }

    [Fact]
    public void Tool_names_are_unique()
    {
        var duplicates = McpToolCatalog.All
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Write_tools_are_all_declared_as_write()
    {
        // 写工具的能力必须带 write 后缀，否则只读客户端会意外获得写权限。
        foreach (var descriptor in McpToolCatalog.All.Where(tool => tool.IsWrite))
            Assert.EndsWith(":write", descriptor.Capability, StringComparison.Ordinal);
    }
}

/// <summary>MCP 工具准入矩阵。</summary>
public sealed class AgentCallGateTests
{
    private static readonly Guid KnowledgeBaseA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid KnowledgeBaseB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid DefaultKnowledgeBase = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static AgentClient Client(
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<Guid>? knowledgeBases = null,
        IReadOnlyList<string>? tools = null,
        bool enabled = true) =>
        new(
            "ac_test",
            "测试客户端",
            null,
            "maf_testtest",
            capabilities ?? [AgentClientCapabilities.KnowledgeRead],
            knowledgeBases ?? [],
            tools ?? [],
            RequestsPerMinute: 0,
            enabled,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null);

    [Fact]
    public void Read_capability_allows_read_tools_only()
    {
        var client = Client();

        Assert.True(AgentCallGate.Decide(client, "search_knowledge_base", null, DefaultKnowledgeBase).Allowed);
        Assert.False(AgentCallGate.Decide(client, "upload_document", null, DefaultKnowledgeBase).Allowed);
    }

    [Fact]
    public void Denial_code_is_permission_denied_and_http_is_200()
    {
        var decision = AgentCallGate.Decide(Client(), "upload_document", null, DefaultKnowledgeBase);

        Assert.False(decision.Allowed);
        Assert.Equal(AgentCallGate.PermissionDenied, decision.JsonRpcErrorCode);

        // 工具级拒绝用 200 + JSON-RPC 错误，模型才能读到可读原因。
        Assert.Equal(200, decision.HttpStatusCode);
        Assert.Contains("knowledge:write", decision.Message);
    }

    [Fact]
    public void Unknown_tools_are_rejected_even_with_wildcard_capability()
    {
        var client = Client(capabilities: [AgentClientCapabilities.All]);

        var decision = AgentCallGate.Decide(client, "rm_rf_everything", null, DefaultKnowledgeBase);

        Assert.False(decision.Allowed);
        Assert.Equal(AgentCallGate.MethodNotFound, decision.JsonRpcErrorCode);
    }

    [Fact]
    public void Tool_whitelist_narrows_the_capability()
    {
        var client = Client(tools: ["list_documents"]);

        Assert.True(AgentCallGate.Decide(client, "list_documents", null, DefaultKnowledgeBase).Allowed);
        Assert.False(AgentCallGate.Decide(client, "search_knowledge_base", null, DefaultKnowledgeBase).Allowed);
    }

    [Fact]
    public void Empty_whitelist_means_no_tool_restriction()
    {
        var client = Client();

        Assert.True(AgentCallGate.Decide(client, "list_documents", null, DefaultKnowledgeBase).Allowed);
        Assert.True(AgentCallGate.Decide(client, "get_document_content", null, DefaultKnowledgeBase).Allowed);
    }

    [Fact]
    public void Out_of_scope_knowledge_base_is_denied()
    {
        var client = Client(knowledgeBases: [KnowledgeBaseA]);

        Assert.True(AgentCallGate.Decide(client, "list_documents", KnowledgeBaseA, DefaultKnowledgeBase).Allowed);
        Assert.False(AgentCallGate.Decide(client, "list_documents", KnowledgeBaseB, DefaultKnowledgeBase).Allowed);
    }

    [Fact]
    public void Omitting_the_knowledge_base_uses_the_default_and_must_also_be_in_scope()
    {
        var client = Client(knowledgeBases: [KnowledgeBaseA]);

        var decision = AgentCallGate.Decide(client, "search_knowledge_base", null, DefaultKnowledgeBase);

        Assert.False(decision.Allowed);
        Assert.Contains("未被授权访问知识库", decision.Message);
    }

    [Fact]
    public void Effective_knowledge_base_is_reported_for_audit()
    {
        var client = Client(capabilities: [AgentClientCapabilities.KnowledgeRead]);

        var decision = AgentCallGate.Decide(client, "list_documents", KnowledgeBaseA, DefaultKnowledgeBase);

        Assert.True(decision.Allowed);
        Assert.Equal(KnowledgeBaseA, decision.EffectiveKnowledgeBaseId);
    }

    [Fact]
    public void Document_tools_are_checked_against_the_documents_owner_knowledge_base()
    {
        var client = Client(knowledgeBases: [KnowledgeBaseA]);

        Assert.True(AgentCallGate.DecideDocumentAccess(client, KnowledgeBaseA).Allowed);
        Assert.False(AgentCallGate.DecideDocumentAccess(client, KnowledgeBaseB).Allowed);
    }

    /// <summary>
    /// 「知识库可选」的工具（如 chat_search_history）曾经因为是 <c>UsesKnowledgeBase: false</c>
    /// 而完全跳过知识库校验：受限客户端只要显式传一个别的 knowledgeBaseId，
    /// 就能把未授权知识库的问答历史读出来。可选参数也必须受范围约束。
    /// </summary>
    [Fact]
    public void Optional_knowledge_base_parameter_is_still_scope_checked()
    {
        var client = Client(
            capabilities: [AgentClientCapabilities.ChatRead],
            knowledgeBases: [KnowledgeBaseA]);

        Assert.True(AgentCallGate.Decide(client, "chat_search_history", KnowledgeBaseA, DefaultKnowledgeBase).Allowed);

        var denied = AgentCallGate.Decide(client, "chat_search_history", KnowledgeBaseB, DefaultKnowledgeBase);
        Assert.False(denied.Allowed);
        Assert.Equal(AgentCallGate.PermissionDenied, denied.JsonRpcErrorCode);
        Assert.Contains("未被授权访问知识库", denied.Message);
    }

    [Fact]
    public void Optional_knowledge_base_omitted_is_allowed_for_unscoped_tools()
    {
        // 不在知识库范围内的客户端仍然可以用 chat:read 查分身/伴侣对话——
        // 这正是把 knowledgeBaseId 做成可选的目的，不能因为"没传"就要求默认库权限。
        var client = Client(
            capabilities: [AgentClientCapabilities.ChatRead],
            knowledgeBases: [KnowledgeBaseA]);

        var decision = AgentCallGate.Decide(client, "chat_search_history", null, DefaultKnowledgeBase);

        Assert.True(decision.Allowed);
        Assert.Null(decision.EffectiveKnowledgeBaseId);
    }
}

/// <summary>令牌桶限流。</summary>
public sealed class AgentRateLimiterTests
{
    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now += delta;
    }

    [Fact]
    public void Unlimited_never_rejects()
    {
        var limiter = new AgentRateLimiter(new FakeTimeProvider(DateTimeOffset.UtcNow));

        for (var i = 0; i < 1_000; i++)
            Assert.True(limiter.TryAcquire("c1", requestsPerMinute: 0, out _));
    }

    [Fact]
    public void Burst_is_capped_by_the_per_minute_budget()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AgentRateLimiter(time);

        for (var i = 0; i < 3; i++)
            Assert.True(limiter.TryAcquire("c1", 3, out _));

        Assert.False(limiter.TryAcquire("c1", 3, out var retryAfter));
        Assert.True(retryAfter >= 1);
    }

    [Fact]
    public void Tokens_refill_over_time()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AgentRateLimiter(time);

        Assert.True(limiter.TryAcquire("c1", 60, out _));
        for (var i = 0; i < 59; i++) limiter.TryAcquire("c1", 60, out _);
        Assert.False(limiter.TryAcquire("c1", 60, out _));

        // 60 次/分钟 = 1 次/秒。
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("c1", 60, out _));
    }

    [Fact]
    public void Clients_are_counted_independently()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AgentRateLimiter(time);

        Assert.True(limiter.TryAcquire("c1", 1, out _));
        Assert.False(limiter.TryAcquire("c1", 1, out _));
        Assert.True(limiter.TryAcquire("c2", 1, out _));
    }

    [Fact]
    public void Changing_the_budget_refills_the_bucket()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AgentRateLimiter(time);

        Assert.True(limiter.TryAcquire("c1", 1, out _));
        Assert.False(limiter.TryAcquire("c1", 1, out _));

        // 管理员把额度从 1 调到 10：应该立刻可用，而不是继续按旧桶算。
        Assert.True(limiter.TryAcquire("c1", 10, out _));
    }

    [Fact]
    public void Forget_resets_the_client()
    {
        var limiter = new AgentRateLimiter(new FakeTimeProvider(DateTimeOffset.UtcNow));

        Assert.True(limiter.TryAcquire("c1", 1, out _));
        Assert.False(limiter.TryAcquire("c1", 1, out _));

        limiter.Forget("c1");
        Assert.True(limiter.TryAcquire("c1", 1, out _));
    }
}

/// <summary>客户端用例层：创建、校验、轮换、更新、删除与接入校验。</summary>
public sealed class AgentClientServiceTests
{
    private static readonly Guid KnowledgeBaseA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static AgentClientService BuildService(
        FakeAgentClientStore store,
        StubDirectory? directory = null,
        int maxClients = 50,
        int defaultRate = 120)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new McpOptions
        {
            MaxAgentClients = maxClients,
            DefaultRequestsPerMinute = defaultRate
        });

        return new AgentClientService(
            store,
            directory ?? new StubDirectory(),
            options,
            NullLogger<AgentClientService>.Instance);
    }

    [Fact]
    public async Task Creating_a_client_returns_the_key_exactly_once()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var result = await service.CreateAsync(
            new CreateAgentClientCommand("Claude Desktop", "本机接入", null, null, null, null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Key);
        Assert.StartsWith(AgentClientKey.Prefix, result.Key);
        Assert.Equal(AgentClientCapabilities.DefaultForNewClient, result.Client!.Capabilities);
        Assert.Equal(120, result.Client.RequestsPerMinute);

        // 存储里只有哈希，没有明文。
        var stored = Assert.Single(store.Clients);
        Assert.Equal(AgentClientKey.Hash(result.Key!), store.Hashes[stored.Id]);
        Assert.NotEqual(result.Key, store.Hashes[stored.Id]);
    }

    [Fact]
    public async Task Name_is_required_and_unique()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var empty = await service.CreateAsync(new CreateAgentClientCommand("  ", null, null, null, null, null), CancellationToken.None);
        Assert.False(empty.Success);
        Assert.Contains(empty.Errors, error => error.Contains("必填"));

        await service.CreateAsync(new CreateAgentClientCommand("Cursor", null, null, null, null, null), CancellationToken.None);
        var duplicate = await service.CreateAsync(new CreateAgentClientCommand("cursor", null, null, null, null, null), CancellationToken.None);

        Assert.False(duplicate.Success);
        Assert.Contains(duplicate.Errors, error => error.Contains("同名"));
    }

    [Fact]
    public async Task Unknown_capabilities_and_tools_are_rejected()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var result = await service.CreateAsync(
            new CreateAgentClientCommand("Bad", null, ["knowledge:destroy"], [Guid.NewGuid()], ["rm_rf"], null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("未知能力"));
        Assert.Contains(result.Errors, error => error.Contains("未知工具"));
        Assert.Empty(store.Clients);
    }

    [Fact]
    public async Task Knowledge_base_scope_must_point_to_existing_bases()
    {
        var store = new FakeAgentClientStore();
        var directory = new StubDirectory { Existing = { KnowledgeBaseA } };
        var service = BuildService(store, directory);

        var missing = await service.CreateAsync(
            new CreateAgentClientCommand("Ghost", null, null, [Guid.NewGuid()], null, null),
            CancellationToken.None);

        Assert.False(missing.Success);
        Assert.Contains(missing.Errors, error => error.Contains("知识库不存在"));

        var known = await service.CreateAsync(
            new CreateAgentClientCommand("Scoped", null, null, [KnowledgeBaseA], null, null),
            CancellationToken.None);

        Assert.True(known.Success);
        Assert.Equal([KnowledgeBaseA], known.Client!.AllowedKnowledgeBaseIds);
    }

    [Fact]
    public async Task Empty_knowledge_base_list_means_unrestricted()
    {
        var service = BuildService(new FakeAgentClientStore());

        var result = await service.CreateAsync(
            new CreateAgentClientCommand("Open", null, null, [], null, null),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Client!.IsKnowledgeBaseRestricted);
        Assert.True(result.Client.CanAccessKnowledgeBase(Guid.NewGuid()));
    }

    [Fact]
    public async Task Client_count_is_capped()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store, maxClients: 1);

        Assert.True((await service.CreateAsync(
            new CreateAgentClientCommand("First", null, null, null, null, null), CancellationToken.None)).Success);

        var second = await service.CreateAsync(
            new CreateAgentClientCommand("Second", null, null, null, null, null), CancellationToken.None);

        Assert.False(second.Success);
        Assert.Contains(second.Errors, error => error.Contains("上限"));
    }

    [Fact]
    public async Task Rotating_the_key_invalidates_the_previous_one()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var created = await service.CreateAsync(
            new CreateAgentClientCommand("Rotate", null, null, null, null, null), CancellationToken.None);

        var rotated = await service.RotateKeyAsync(created.Client!.Id, CancellationToken.None);
        Assert.True(rotated.Success);
        Assert.NotEqual(created.Key, rotated.Key);

        Assert.Null(await service.AuthenticateAsync(created.Key, null, CancellationToken.None));
        Assert.NotNull(await service.AuthenticateAsync(rotated.Key, null, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_rejects_unknown_keys_and_garbage()
    {
        var service = BuildService(new FakeAgentClientStore());

        Assert.Null(await service.AuthenticateAsync(null, null, CancellationToken.None));
        Assert.Null(await service.AuthenticateAsync("not-a-key", null, CancellationToken.None));
        Assert.Null(await service.AuthenticateAsync("maf_" + new string('x', 40), null, CancellationToken.None));
    }

    [Fact]
    public async Task Update_keeps_the_key_and_applies_partial_changes()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var created = await service.CreateAsync(
            new CreateAgentClientCommand("Partial", "备注", null, null, null, null), CancellationToken.None);

        var updated = await service.UpdateAsync(
            created.Client!.Id,
            new UpdateAgentClientCommand(Capabilities: [AgentClientCapabilities.KnowledgeRead, AgentClientCapabilities.KnowledgeWrite]),
            CancellationToken.None);

        Assert.True(updated.Success);
        Assert.Equal("Partial", updated.Client!.Name);
        Assert.Equal("备注", updated.Client.Description);
        Assert.Equal(2, updated.Client.Capabilities.Count);

        // 密钥没变：更新作用域不应导致已接入的客户端掉线。
        Assert.NotNull(await service.AuthenticateAsync(created.Key, null, CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_clients_still_authenticate_so_the_gate_can_return_403()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var created = await service.CreateAsync(
            new CreateAgentClientCommand("Disabled", null, null, null, null, null), CancellationToken.None);

        var updated = await service.UpdateAsync(
            created.Client!.Id,
            new UpdateAgentClientCommand(Enabled: false),
            CancellationToken.None);

        Assert.True(updated.Success);
        Assert.False(updated.Client!.Enabled);

        var authenticated = await service.AuthenticateAsync(created.Key, null, CancellationToken.None);
        Assert.NotNull(authenticated);
        Assert.False(authenticated!.Enabled);
    }

    [Fact]
    public async Task Updating_a_missing_client_reports_not_found()
    {
        var service = BuildService(new FakeAgentClientStore());

        var result = await service.UpdateAsync("ac_missing", new UpdateAgentClientCommand(Name: "x"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.StartsWith("客户端不存在", StringComparison.Ordinal));

        Assert.False(await service.DeleteAsync("ac_missing", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_the_client()
    {
        var store = new FakeAgentClientStore();
        var service = BuildService(store);

        var created = await service.CreateAsync(
            new CreateAgentClientCommand("Temp", null, null, null, null, null), CancellationToken.None);

        Assert.True(await service.DeleteAsync(created.Client!.Id, CancellationToken.None));
        Assert.Null(await service.AuthenticateAsync(created.Key, null, CancellationToken.None));
    }

    private sealed class StubDirectory : IKnowledgeBaseDirectory
    {
        public HashSet<Guid> Existing { get; } = [];

        public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(Existing.Contains(knowledgeBaseId));

        public Task<IReadOnlyList<KnowledgeBaseSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeBaseSummary>>(
                Existing.Select(id => new KnowledgeBaseSummary(id, id.ToString("D"))).ToList());
    }

    private sealed class FakeAgentClientStore : IAgentClientStore
    {
        public List<AgentClient> Clients { get; } = [];

        public Dictionary<string, string> Hashes { get; } = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<AgentClient>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentClient>>(Clients.ToList());

        public Task<AgentClient?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(Clients.FirstOrDefault(client => client.Id == id));

        public Task<AgentClient?> FindByKeyHashAsync(string keyHash, CancellationToken cancellationToken)
        {
            var match = Hashes.FirstOrDefault(pair => pair.Value == keyHash);
            return Task.FromResult(match.Key is null
                ? null
                : Clients.FirstOrDefault(client => client.Id == match.Key));
        }

        public Task CreateAsync(AgentClient client, string keyHash, CancellationToken cancellationToken)
        {
            Clients.Add(client);
            Hashes[client.Id] = keyHash;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(AgentClient client, string? keyHash, CancellationToken cancellationToken)
        {
            var index = Clients.FindIndex(existing => existing.Id == client.Id);
            if (index >= 0) Clients[index] = client;
            if (keyHash is not null) Hashes[client.Id] = keyHash;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken cancellationToken)
        {
            Clients.RemoveAll(client => client.Id == id);
            Hashes.Remove(id);
            return Task.CompletedTask;
        }

        public Task TouchAsync(string id, DateTimeOffset seenAt, string? address, CancellationToken cancellationToken)
        {
            var index = Clients.FindIndex(client => client.Id == id);
            if (index >= 0)
            {
                Clients[index] = Clients[index] with { LastSeenAt = seenAt, LastSeenAddress = address };
            }

            return Task.CompletedTask;
        }
    }
}
