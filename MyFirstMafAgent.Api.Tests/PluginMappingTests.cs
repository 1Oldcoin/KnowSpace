using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>平台条目判定矩阵：去重与撤回的规则必须能穷举验证。</summary>
public sealed class PlatformItemDecisionTests
{
    private const string PluginId = "com.example.notes";
    private const string Source = "com.example.notes";

    [Fact]
    public void Unknown_item_is_created()
    {
        var item = Item("n1", "hello");

        Assert.Equal(PlatformItemAction.Create, PlatformItemDecision.Decide(item, existing: null));
    }

    [Fact]
    public void Unknown_retracted_item_only_leaves_a_tombstone()
    {
        var item = Item("n1", content: "", retracted: true);

        Assert.Equal(PlatformItemAction.Tombstone, PlatformItemDecision.Decide(item, existing: null));
    }

    [Fact]
    public void Unchanged_content_is_skipped()
    {
        var item = Item("n1", "hello");
        var existing = Link(item, PlatformItemDecision.NormalizedHash(item));

        Assert.Equal(PlatformItemAction.Skip, PlatformItemDecision.Decide(item, existing));
    }

    [Fact]
    public void Changed_content_is_updated()
    {
        var existing = Link(Item("n1", "hello"), PlatformIngestionItem.ComputeContentHash("hello"));
        var item = Item("n1", "hello again");

        Assert.Equal(PlatformItemAction.Update, PlatformItemDecision.Decide(item, existing));
    }

    [Fact]
    public void Retracting_a_live_item_deletes_the_document()
    {
        var existing = Link(Item("n1", "hello"), PlatformIngestionItem.ComputeContentHash("hello"));
        var item = Item("n1", content: "", retracted: true);

        Assert.Equal(PlatformItemAction.Retract, PlatformItemDecision.Decide(item, existing));
    }

    [Fact]
    public void Retracting_twice_is_idempotent()
    {
        var existing = Link(Item("n1", "", retracted: true), hash: null, state: PlatformItemStates.Retracted);
        var item = Item("n1", content: "", retracted: true);

        Assert.Equal(PlatformItemAction.Skip, PlatformItemDecision.Decide(item, existing));
    }

    [Fact]
    public void Re_published_item_is_created_again()
    {
        var existing = Link(Item("n1", "", retracted: true), hash: null, state: PlatformItemStates.Retracted);

        Assert.Equal(PlatformItemAction.Create, PlatformItemDecision.Decide(Item("n1", "back again"), existing));
    }

    [Fact]
    public void Active_link_without_document_is_treated_as_new()
    {
        var existing = new PlatformItemLink(
            PluginId, Source, "n1", Guid.NewGuid(), DocumentId: null, ContentHash: "abc",
            Title: null, PlatformItemStates.Active, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        Assert.Equal(PlatformItemAction.Create, PlatformItemDecision.Decide(Item("n1", "hello"), existing));
    }

    [Fact]
    public void Invalid_item_is_rejected_before_any_io()
    {
        var item = Item("", "hello");

        Assert.Equal(PlatformItemAction.Reject, PlatformItemDecision.Decide(item, existing: null));
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        var item = Item("n1", "hello");
        var hash = PlatformIngestionItem.ComputeContentHash("hello");
        var existing = Link(item, hash);

        var upperCase = item with { ContentHash = hash.ToUpperInvariant() };

        Assert.Equal(PlatformItemAction.Skip, PlatformItemDecision.Decide(upperCase, existing));
    }

    [Fact]
    public void Malformed_hash_is_rejected_rather_than_normalized()
    {
        var item = Item("n1", "hello") with { ContentHash = "  " + new string('a', 64) + "  " };

        Assert.Equal(PlatformItemAction.Reject, PlatformItemDecision.Decide(item, existing: null));
    }

    [Fact]
    public void Missing_platform_hash_is_computed_locally()
    {
        var item = Item("n1", "hello") with { ContentHash = null };

        Assert.Equal(
            PlatformIngestionItem.ComputeContentHash("hello"),
            PlatformItemDecision.NormalizedHash(item));
    }

    private static PlatformIngestionItem Item(
        string externalId,
        string content,
        bool retracted = false) => new()
    {
        Source = Source,
        ExternalId = externalId,
        Title = "标题 " + externalId,
        Type = PlatformItemTypes.Note,
        Content = content,
        Retracted = retracted
    };

    private static PlatformItemLink Link(
        PlatformIngestionItem item,
        string? hash,
        string state = PlatformItemStates.Active) => new(
            PluginId,
            item.Source,
            item.ExternalId,
            Guid.NewGuid(),
            state == PlatformItemStates.Active ? Guid.NewGuid() : null,
            hash,
            item.Title,
            state,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
}

/// <summary>摄取编排：幂等、更新、撤回、配额、知识库范围。</summary>
public sealed class PluginIngestionSinkTests
{
    private const string PluginId = "com.example.notes";
    private static readonly Guid KnowledgeBase = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task First_sync_creates_documents_and_records_mapping()
    {
        var harness = new Harness();

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a"), Item("n2", "b")));

        Assert.Equal(2, outcome.Created);
        Assert.Equal(0, outcome.Skipped);
        Assert.Equal(2, harness.Content.Created.Count);
        Assert.Equal(2, harness.Map.Links.Count);
        Assert.All(harness.Map.Links.Values, link => Assert.Equal(PlatformItemStates.Active, link.State));
    }

    [Fact]
    public async Task Second_sync_of_same_content_creates_nothing()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a")));

        Assert.Equal(0, outcome.Created);
        Assert.Equal(1, outcome.Skipped);
        Assert.Single(harness.Content.Created);
    }

    [Fact]
    public async Task Changed_content_replaces_the_same_document()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));
        var documentId = harness.Map.Links.Values.Single().DocumentId;

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a plus more")));

        Assert.Equal(1, outcome.Updated);
        Assert.Single(harness.Content.Created);
        Assert.Equal(documentId, harness.Content.Replaced.Single().DocumentId);
        Assert.Equal(documentId, harness.Map.Links.Values.Single().DocumentId);
    }

    [Fact]
    public async Task Retraction_deletes_the_document_and_keeps_a_tombstone()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));
        var documentId = harness.Map.Links.Values.Single().DocumentId;

        var outcome = await harness.IngestAsync(Batch(Item("n1", content: "", retracted: true)));

        Assert.Equal(1, outcome.Retracted);
        Assert.Equal(documentId, harness.Content.Deleted.Single());

        var link = harness.Map.Links.Values.Single();
        Assert.Equal(PlatformItemStates.Retracted, link.State);
        Assert.Null(link.DocumentId);
    }

    [Fact]
    public async Task Stale_batch_cannot_resurrect_a_retracted_item()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));
        await harness.IngestAsync(Batch(Item("n1", content: "", retracted: true)));

        // 平台删了之后，一个迟到的旧批次又带上同一内容。
        var outcome = await harness.IngestAsync(Batch(Item("n1", content: "", retracted: true)));

        Assert.Equal(0, outcome.Created);
        Assert.Equal(1, outcome.Skipped);
        Assert.Single(harness.Content.Created);
    }

    [Fact]
    public async Task Retracting_an_unknown_item_leaves_a_tombstone_without_creating_anything()
    {
        var harness = new Harness();

        var outcome = await harness.IngestAsync(Batch(Item("ghost", content: "", retracted: true)));

        Assert.Equal(1, outcome.Retracted);
        Assert.Empty(harness.Content.Created);
        Assert.Equal(PlatformItemStates.Retracted, harness.Map.Links.Values.Single().State);
    }

    [Fact]
    public async Task Republished_item_is_created_again_after_retraction()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));
        await harness.IngestAsync(Batch(Item("n1", content: "", retracted: true)));

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a")));

        Assert.Equal(1, outcome.Created);
        Assert.Equal(2, harness.Content.Created.Count);
        Assert.Equal(PlatformItemStates.Active, harness.Map.Links.Values.Single().State);
    }

    [Fact]
    public async Task Busy_document_is_skipped_and_retried_later()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));
        harness.Content.ReplaceSucceeds = false;

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a plus more")));

        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(0, outcome.Updated);
        // 哈希没有被更新，所以下次同步还会尝试替换。
        Assert.Equal(
            PlatformIngestionItem.ComputeContentHash("a"),
            harness.Map.Links.Values.Single().ContentHash);
    }

    [Fact]
    public async Task Invalid_items_are_counted_not_thrown()
    {
        var harness = new Harness();

        var outcome = await harness.IngestAsync(Batch(
            new PlatformIngestionItem { Source = "", ExternalId = "x", Content = "c" },
            Item("n1", "a")));

        Assert.Equal(1, outcome.Invalid);
        Assert.Equal(1, outcome.Created);
        Assert.NotEmpty(outcome.Diagnostics);
    }

    [Fact]
    public async Task Quota_blocks_new_items_but_not_updates()
    {
        var harness = new Harness(maxDocuments: 1);
        await harness.IngestAsync(Batch(Item("n1", "a")));

        var blocked = await harness.IngestAsync(Batch(Item("n2", "b")));
        Assert.Equal(1, blocked.QuotaRejected);
        Assert.Contains(blocked.Diagnostics, diagnostic => diagnostic.Contains("上限"));

        // 配额只挡新建，已有条目的更新照常。
        var update = await harness.IngestAsync(Batch(Item("n1", "a changed")));
        Assert.Equal(1, update.Updated);
    }

    [Fact]
    public async Task Global_quota_applies_when_plugin_has_no_override()
    {
        var harness = new Harness(maxDocuments: 0, globalQuota: 1);
        await harness.IngestAsync(Batch(Item("n1", "a")));

        var outcome = await harness.IngestAsync(Batch(Item("n2", "b")));

        Assert.Equal(1, outcome.QuotaRejected);
    }

    [Fact]
    public async Task Writing_to_an_out_of_scope_knowledge_base_is_denied()
    {
        var harness = new Harness(allowedKnowledgeBases: [Guid.NewGuid()]);

        var exception = await Assert.ThrowsAsync<PluginRpcException>(() => harness.IngestAsync(Batch(Item("n1", "a"))));

        Assert.Equal(PluginProtocol.ErrorPermissionDenied, exception.Code);
        Assert.Empty(harness.Content.Created);
    }

    [Fact]
    public async Task Empty_scope_means_unrestricted()
    {
        var harness = new Harness(allowedKnowledgeBases: []);

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a")));

        Assert.Equal(1, outcome.Created);
    }

    [Fact]
    public async Task Missing_knowledge_base_is_rejected()
    {
        var harness = new Harness(knowledgeBaseExists: false);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.IngestAsync(Batch(Item("n1", "a"))));
    }

    [Fact]
    public async Task Recreating_a_vanished_document_also_obeys_the_quota()
    {
        var harness = new Harness(maxDocuments: 1);
        await harness.IngestAsync(Batch(Item("n1", "a")));

        // 资料被用户手工删除：映射还在，替换会失败并退化为重建。
        harness.Content.ReplaceTargetGone = true;

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a changed")));

        Assert.Equal(1, outcome.QuotaRejected);
        Assert.Equal(0, outcome.Updated);
        Assert.Single(harness.Content.Created);
    }

    [Fact]
    public async Task Recreating_a_vanished_document_is_counted_as_an_update()
    {
        var harness = new Harness();
        await harness.IngestAsync(Batch(Item("n1", "a")));

        harness.Content.ReplaceTargetGone = true;

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a changed")));

        Assert.Equal(1, outcome.Updated);
        Assert.Equal(2, harness.Content.Created.Count + harness.Content.Replaced.Count);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Contains("重新落库"));
    }

    [Fact]
    public async Task The_same_external_item_can_be_mirrored_into_two_knowledge_bases()
    {
        var harness = new Harness();
        var other = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var first = await harness.IngestAsync(Batch(Item("n1", "a")));
        var second = await harness.IngestAsync(Batch(Item("n1", "a")), other);

        // 每个知识库各一份映射与资料：映射键必须带知识库，否则第二次会误判为“已存在”。
        Assert.Equal(1, first.Created);
        Assert.Equal(1, second.Created);
        Assert.Equal(2, harness.Map.Links.Count);
        Assert.Equal(2, harness.Content.Created.Count);
    }

    [Fact]
    public async Task Retraction_only_touches_the_knowledge_base_it_targets()
    {
        var harness = new Harness();
        var other = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        await harness.IngestAsync(Batch(Item("n1", "a")));
        await harness.IngestAsync(Batch(Item("n1", "a")), other);

        var outcome = await harness.IngestAsync(Batch(Item("n1", "a", retracted: true)), other);

        Assert.Equal(1, outcome.Retracted);
        Assert.Single(harness.Content.Deleted);
        Assert.Equal(
            PlatformItemStates.Active,
            harness.Map.Links[FakeItemMap.KeyFor(PluginId, "n1", Harness.DefaultKnowledgeBase)].State);
        Assert.Equal(
            PlatformItemStates.Retracted,
            harness.Map.Links[FakeItemMap.KeyFor(PluginId, "n1", other)].State);
    }

    private static PlatformIngestionBatch Batch(params PlatformIngestionItem[] items) =>
        new() { Items = items };

    private static PlatformIngestionItem Item(
        string externalId,
        string content,
        bool retracted = false) => new()
    {
        Source = PluginId,
        ExternalId = externalId,
        Title = "标题 " + externalId,
        Type = PlatformItemTypes.Note,
        Content = content,
        Retracted = retracted,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class Harness
    {
        public Harness(
            int maxDocuments = 0,
            int globalQuota = 0,
            IReadOnlyList<Guid>? allowedKnowledgeBases = null,
            bool knowledgeBaseExists = true)
        {
            Map = new FakeItemMap();
            Content = new FakeContentStore { KnowledgeBaseExists = knowledgeBaseExists };

            var registration = PluginRegistration.Create(
                PluginId, "Notes", "1.0.0", "Example", enabled: true, "{}", []) with
            {
                AllowedKnowledgeBaseIds = allowedKnowledgeBases ?? [],
                MaxDocuments = maxDocuments
            };

            Registry = new FakeRegistry(registration);

            var options = Microsoft.Extensions.Options.Options.Create(new PluginOptions
            {
                DefaultMaxDocumentsPerPlugin = globalQuota
            });

            Sink = new KnowledgePluginIngestionSink(
                Content,
                Map,
                Registry,
                options,
                NullLogger<KnowledgePluginIngestionSink>.Instance);
        }

        public FakeItemMap Map { get; }

        /// <summary>Harness 默认写入的知识库。</summary>
        public static Guid DefaultKnowledgeBase => KnowledgeBase;

        public FakeContentStore Content { get; }

        public FakeRegistry Registry { get; }

        public KnowledgePluginIngestionSink Sink { get; }

        public Task<PluginIngestionOutcome> IngestAsync(PlatformIngestionBatch batch) =>
            Sink.IngestAsync(PluginId, batch, KnowledgeBase, CancellationToken.None);

        public Task<PluginIngestionOutcome> IngestAsync(PlatformIngestionBatch batch, Guid knowledgeBaseId) =>
            Sink.IngestAsync(PluginId, batch, knowledgeBaseId, CancellationToken.None);
    }

    private sealed class FakeItemMap : IPluginItemMap
    {
        public Dictionary<string, PlatformItemLink> Links { get; } = new(StringComparer.Ordinal);

        private static string Key(string pluginId, string source, string externalId, Guid knowledgeBaseId) =>
            $"{pluginId}|{source}|{externalId}|{knowledgeBaseId:D}";

        public static string KeyFor(string pluginId, string externalId, Guid knowledgeBaseId) =>
            Key(pluginId, pluginId, externalId, knowledgeBaseId);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PlatformItemLink?> GetAsync(
            string pluginId,
            string source,
            string externalId,
            Guid knowledgeBaseId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                Links.TryGetValue(Key(pluginId, source, externalId, knowledgeBaseId), out var link) ? link : null);

        public Task SaveAsync(PlatformItemLink link, CancellationToken cancellationToken)
        {
            Links[Key(link.PluginId, link.Source, link.ExternalId, link.KnowledgeBaseId)] = link;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlatformItemLink>> ListAsync(
            string pluginId,
            Guid? knowledgeBaseId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlatformItemLink>>(
                Links.Values.Where(link => link.PluginId == pluginId).Take(limit).ToList());

        public Task<int> CountActiveAsync(string pluginId, Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(Links.Values.Count(link =>
                link.PluginId == pluginId
                && link.KnowledgeBaseId == knowledgeBaseId
                && link.State == PlatformItemStates.Active));

        public Task DeleteByPluginAsync(string pluginId, CancellationToken cancellationToken)
        {
            foreach (var key in Links.Keys.Where(key => key.StartsWith(pluginId + "|", StringComparison.Ordinal)).ToList())
                Links.Remove(key);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeContentStore : IPluginContentStore
    {
        public List<(Guid DocumentId, PlatformIngestionItem Item)> Created { get; } = [];

        public List<(Guid DocumentId, PlatformIngestionItem Item)> Replaced { get; } = [];

        public List<Guid> Deleted { get; } = [];

        public bool ReplaceSucceeds { get; set; } = true;

        /// <summary>模拟"映射指向的资料已经被删掉"：替换会抛 ArgumentException。</summary>
        public bool ReplaceTargetGone { get; set; }

        public FakeContentStore() { }

        public bool KnowledgeBaseExists { get; set; } = true;

        public Task<bool> KnowledgeBaseExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(KnowledgeBaseExists);

        public Task<Guid?> CreateAsync(
            PlatformIngestionItem item,
            Guid knowledgeBaseId,
            CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            Created.Add((id, item));
            return Task.FromResult<Guid?>(id);
        }

        public Task<bool> ReplaceAsync(
            Guid documentId,
            PlatformIngestionItem item,
            Guid knowledgeBaseId,
            CancellationToken cancellationToken)
        {
            if (ReplaceTargetGone)
                throw new ArgumentException($"Document '{documentId}' was not found.", nameof(documentId));

            if (!ReplaceSucceeds) return Task.FromResult(false);
            Replaced.Add((documentId, item));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Deleted.Add(documentId);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRegistry(PluginRegistration registration) : IPluginRegistry
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginRegistration>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginRegistration>>([registration]);

        public Task<PluginRegistration?> GetAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.FromResult<PluginRegistration?>(registration.PluginId == pluginId ? registration : null);

        public Task SaveAsync(PluginRegistration value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string pluginId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordAuditAsync(PluginAuditRecord record, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginAuditRecord>> QueryAuditAsync(
            string? pluginId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginAuditRecord>>([]);
    }
}

/// <summary>同步游标、知识库范围与 Agent 作用域。</summary>
public sealed class PluginScopeAndCursorTests
{
    private static readonly Guid KnowledgeBaseA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid KnowledgeBaseB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Scope_key_separates_knowledge_bases_and_capabilities()
    {
        var a = IPluginSyncStateStore.BuildScopeKey("com.example.notes", "notes.sync", KnowledgeBaseA);
        var b = IPluginSyncStateStore.BuildScopeKey("com.example.notes", "notes.sync", KnowledgeBaseB);
        var c = IPluginSyncStateStore.BuildScopeKey("com.example.notes", "notes.other", KnowledgeBaseA);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.Contains(KnowledgeBaseA.ToString("D"), a);
    }

    [Fact]
    public void Empty_scope_allows_every_knowledge_base()
    {
        var registration = PluginRegistration.Create("com.example.notes", "Notes", "1.0.0", "Example", true, "", []);

        Assert.True(registration.CanWriteTo(KnowledgeBaseA));
        Assert.True(registration.CanWriteTo(KnowledgeBaseB));
    }

    [Fact]
    public void Scope_list_restricts_writes()
    {
        var registration = PluginRegistration.Create("com.example.notes", "Notes", "1.0.0", "Example", true, "", []) with
        {
            AllowedKnowledgeBaseIds = [KnowledgeBaseA]
        };

        Assert.True(registration.CanWriteTo(KnowledgeBaseA));
        Assert.False(registration.CanWriteTo(KnowledgeBaseB));
    }

    [Fact]
    public void Unscoped_agent_may_read_any_knowledge_base()
    {
        Assert.Null(AgentKnowledgeScope.Check(null, KnowledgeBaseB));
    }

    [Fact]
    public void Scoped_agent_may_read_its_own_knowledge_base()
    {
        Assert.Null(AgentKnowledgeScope.Check(KnowledgeBaseA, KnowledgeBaseA));
    }

    [Fact]
    public void Scoped_agent_is_denied_other_knowledge_bases_with_a_readable_reason()
    {
        var reason = AgentKnowledgeScope.Check(KnowledgeBaseA, KnowledgeBaseB);

        Assert.NotNull(reason);
        Assert.Contains(KnowledgeBaseA.ToString("D"), reason);
        Assert.Contains(KnowledgeBaseB.ToString("D"), reason);
        Assert.Contains("切换", reason);
    }

    [Fact]
    public void Visible_knowledge_bases_are_filtered_when_scoped()
    {
        var visible = AgentKnowledgeScope.VisibleKnowledgeBaseIds(
            KnowledgeBaseA,
            [KnowledgeBaseA, KnowledgeBaseB]);

        Assert.Equal([KnowledgeBaseA], visible);

        Assert.Equal(
            [KnowledgeBaseA, KnowledgeBaseB],
            AgentKnowledgeScope.VisibleKnowledgeBaseIds(null, [KnowledgeBaseA, KnowledgeBaseB]));
    }

    [Fact]
    public void Outcome_reports_ingested_as_created_plus_updated()
    {
        var outcome = new PluginIngestionOutcome(2, 3, 1, 0, 0, 0, []);

        Assert.Equal(5, outcome.Ingested);
        Assert.False(outcome.HasFailures);
    }

    [Fact]
    public void Outcome_flags_failures_for_invalid_or_quota_rejected_items()
    {
        Assert.True(new PluginIngestionOutcome(0, 0, 0, 0, 1, 0, []).HasFailures);
        Assert.True(new PluginIngestionOutcome(0, 0, 0, 0, 0, 1, []).HasFailures);
        Assert.False(new PluginIngestionOutcome(0, 0, 5, 2, 0, 0, []).HasFailures);
    }

    [Fact]
    public void Sync_state_starts_empty_and_carries_the_scope_key()
    {
        var state = PluginSyncState.Empty("com.example.notes", "notes.sync", KnowledgeBaseA);

        Assert.Null(state.Cursor);
        Assert.Null(state.LastSyncedAt);
        Assert.Equal(
            IPluginSyncStateStore.BuildScopeKey("com.example.notes", "notes.sync", KnowledgeBaseA),
            state.ScopeKey);
    }

    [Fact]
    public void Content_file_name_is_stable_across_content_changes()
    {
        var original = ContentItem("n1", "first body", "标题");
        var changed = ContentItem("n1", "second body", "标题");

        Assert.Equal(
            KnowledgePluginContentStore.BuildFileName(original),
            KnowledgePluginContentStore.BuildFileName(changed));
    }

    [Fact]
    public void Content_file_name_is_sanitized_and_bounded()
    {
        var item = ContentItem("n1", "body", new string('长', 200) + "../\\:*?");

        var fileName = KnowledgePluginContentStore.BuildFileName(item);

        Assert.EndsWith(".md", fileName);
        // 80 字符标题 + 连字符 + 8 位哈希 + ".md"
        Assert.True(fileName.Length <= 92);
        Assert.DoesNotContain("..", fileName);
        Assert.DoesNotContain(':', fileName);
    }

    [Fact]
    public void Composed_content_keeps_title_metadata_and_provenance()
    {
        var item = ContentItem("n1", "正文内容", "标题") with
        {
            Metadata = new Dictionary<string, string> { ["author"] = "张三", ["url"] = "https://example.invalid" }
        };

        var content = KnowledgePluginContentStore.ComposeContent(item);

        Assert.StartsWith("# 标题", content);
        Assert.Contains("正文内容", content);
        Assert.Contains("author: 张三", content);
        Assert.Contains("com.example.notes / n1", content);
    }

    private static PlatformIngestionItem ContentItem(string externalId, string content, string title) => new()
    {
        Source = "com.example.notes",
        ExternalId = externalId,
        Title = title,
        Type = PlatformItemTypes.Note,
        Content = content
    };
}


/// <summary>写入范围与配额校验：只接受真实存在的知识库、非负配额。</summary>
public sealed class PluginScopePolicyTests
{
    private static readonly Guid Known = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public async Task Empty_scope_is_always_valid()
    {
        var errors = await PluginScopePolicy.ValidateAsync([], 0, new StubDirectory([]), CancellationToken.None);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Unknown_knowledge_base_is_rejected()
    {
        var errors = await PluginScopePolicy.ValidateAsync(
            [Guid.NewGuid()], 0, new StubDirectory([]), CancellationToken.None);

        Assert.Single(errors);
        Assert.Contains("不存在", errors[0]);
    }

    [Fact]
    public async Task Known_knowledge_base_passes_and_duplicates_collapse()
    {
        var directory = new StubDirectory([Known]);
        var errors = await PluginScopePolicy.ValidateAsync([Known, Known], 100, directory, CancellationToken.None);

        Assert.Empty(errors);
        Assert.Equal(1, directory.Queries);
    }

    [Fact]
    public async Task Negative_quota_is_rejected()
    {
        var errors = await PluginScopePolicy.ValidateAsync([], -1, new StubDirectory([]), CancellationToken.None);

        Assert.Single(errors);
        Assert.Contains("负数", errors[0]);
    }

    [Fact]
    public async Task Empty_guid_is_rejected_without_a_lookup()
    {
        var directory = new StubDirectory([]);
        var errors = await PluginScopePolicy.ValidateAsync([Guid.Empty], 0, directory, CancellationToken.None);

        Assert.Single(errors);
        Assert.Equal(0, directory.Queries);
    }

    [Fact]
    public async Task All_problems_are_reported_together()
    {
        var errors = await PluginScopePolicy.ValidateAsync(
            [Guid.NewGuid(), Guid.Empty], -5, new StubDirectory([]), CancellationToken.None);

        Assert.Equal(3, errors.Count);
    }

    private sealed class StubDirectory(IReadOnlyList<Guid> known) : IKnowledgeBaseDirectory
    {
        public int Queries { get; private set; }

        public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
        {
            Queries++;
            return Task.FromResult(known.Contains(knowledgeBaseId));
        }

        public Task<IReadOnlyList<KnowledgeBaseSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeBaseSummary>>(
                known.Select(id => new KnowledgeBaseSummary(id, id.ToString("D"))).ToList());
    }
}