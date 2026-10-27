using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Domain.Jobs;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Plugins;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 测试用的“只含配置公钥”的信任视图。用于只想验证签名算法、不想搭数据库的场景。
/// </summary>
public static class TestTrustedKeys
{
    public static PluginTrustedKeyRegistry FromOptions(PluginOptions options)
    {
        var registry = new PluginTrustedKeyRegistry(
            Microsoft.Extensions.Options.Options.Create(options),
            new EmptyStore(),
            NullLogger<PluginTrustedKeyRegistry>.Instance);

        // 注册表只在 ReloadAsync 里读配置，测试里同步等它完成。
        registry.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
        return registry;
    }

    private sealed class EmptyStore : IPluginTrustedKeyStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginTrustedKeyRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginTrustedKeyRecord>>([]);

        public Task<PluginTrustedKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult<PluginTrustedKeyRecord?>(null);

        public Task SaveAsync(PluginTrustedKeyRecord record, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}

/// <summary>受信任发布者公钥：规范化、指纹、前缀匹配。</summary>
public sealed class PluginTrustedKeyTests
{
    [Fact]
    public void Ecdsa_public_key_is_accepted_and_fingerprinted()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var ok = PluginTrustedKeys.TryNormalize("ECDSA", pem, out var algorithm, out var normalized, out var fingerprint, out var error);

        Assert.True(ok, error);
        Assert.Equal("ecdsa", algorithm);
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));
        Assert.Equal(16, fingerprint.Length);

        // 指纹必须稳定：同一把钥匙换个换行风格不能算出不同指纹。
        var again = PluginTrustedKeys.TryNormalize("ecdsa", pem.Replace("\n", "\r\n"), out _, out _, out var fingerprint2, out _);
        Assert.True(again);
        Assert.Equal(fingerprint, fingerprint2);
    }

    [Fact]
    public void Rsa_public_key_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportSubjectPublicKeyInfoPem();

        Assert.True(PluginTrustedKeys.TryNormalize("rsa", pem, out var algorithm, out _, out _, out var error), error);
        Assert.Equal("rsa", algorithm);
    }

    [Theory]
    [InlineData("ecdsa", null, "必填")]
    [InlineData("ecdsa", "   ", "必填")]
    [InlineData("dsa", "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", "algorithm")]
    [InlineData("ecdsa", "not a pem at all", "公钥格式无效")]
    public void Invalid_input_is_rejected_with_a_readable_reason(string algorithm, string? pem, string expected)
    {
        var ok = PluginTrustedKeys.TryNormalize(algorithm, pem, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains(expected, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_keys_are_rejected()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = ecdsa.ExportPkcs8PrivateKeyPem();

        var ok = PluginTrustedKeys.TryNormalize("ecdsa", privatePem, out _, out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("私钥", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("com.example", "com.example.notes", true)]
    [InlineData("com.example", "com.example", true)]
    [InlineData("com.example", "com.example2.notes", false)]
    [InlineData("com.example", "org.example.notes", false)]
    [InlineData("COM.example", "com.example.notes", true)]
    public void Prefix_matching_respects_domain_segments(string prefix, string pluginId, bool expected)
    {
        Assert.Equal(expected, PluginTrustedKeys.Matches(prefix, pluginId));
    }
}

/// <summary>配置 + 数据库公钥的合并视图。</summary>
public sealed class PluginTrustedKeyRegistryTests
{
    [Fact]
    public async Task Database_keys_are_merged_with_configuration_keys()
    {
        using var fromConfig = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fromDatabase = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var store = new FakeTrustedKeyStore
        {
            ["db-1"] = new PluginTrustedKeyRecord(
                "db-1", "com.stored", "ecdsa", fromDatabase.ExportSubjectPublicKeyInfoPem(),
                PluginTrustedKeys.ComputeFingerprint(fromDatabase.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n").Trim()),
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };

        var registry = new PluginTrustedKeyRegistry(
            Microsoft.Extensions.Options.Options.Create(new PluginOptions
            {
                TrustedPublisherKeys =
                [
                    new PluginTrustedKey { PluginId = "com.config", Algorithm = "ecdsa", PublicKeyPem = fromConfig.ExportSubjectPublicKeyInfoPem() }
                ]
            }),
            store,
            NullLogger<PluginTrustedKeyRegistry>.Instance);

        await registry.ReloadAsync(CancellationToken.None);

        Assert.Equal(2, registry.Keys.Count);
        Assert.Equal("db-1", registry.Find("com.stored.notes")?.KeyId);
        Assert.Equal("config:com.config", registry.Find("com.config")?.KeyId);
        Assert.Null(registry.Find("com.unknown"));
    }

    [Fact]
    public async Task The_longest_matching_prefix_wins()
    {
        using var broad = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var narrow = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var store = new FakeTrustedKeyStore
        {
            ["broad"] = Record("broad", "com.example", broad),
            ["narrow"] = Record("narrow", "com.example.notes", narrow)
        };

        var registry = new PluginTrustedKeyRegistry(
            Microsoft.Extensions.Options.Options.Create(new PluginOptions()),
            store,
            NullLogger<PluginTrustedKeyRegistry>.Instance);

        await registry.ReloadAsync(CancellationToken.None);

        Assert.Equal("narrow", registry.Find("com.example.notes.pro")?.KeyId);
        Assert.Equal("broad", registry.Find("com.example.chat")?.KeyId);
    }

    [Fact]
    public async Task A_broken_store_degrades_to_configuration_keys()
    {
        using var fromConfig = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var registry = new PluginTrustedKeyRegistry(
            Microsoft.Extensions.Options.Options.Create(new PluginOptions
            {
                TrustedPublisherKeys =
                [
                    new PluginTrustedKey { PluginId = "com.config", Algorithm = "ecdsa", PublicKeyPem = fromConfig.ExportSubjectPublicKeyInfoPem() }
                ]
            }),
            new ExplodingTrustedKeyStore(),
            NullLogger<PluginTrustedKeyRegistry>.Instance);

        await registry.ReloadAsync(CancellationToken.None);

        // 数据库挂了不能让“已经配置好的公钥”也一起失效。
        Assert.Single(registry.Keys);
        Assert.NotNull(registry.Find("com.config"));
    }

    private static PluginTrustedKeyRecord Record(string keyId, string pluginId, ECDsa key)
    {
        var pem = key.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n").Trim();
        return new PluginTrustedKeyRecord(
            keyId, pluginId, "ecdsa", pem, PluginTrustedKeys.ComputeFingerprint(pem),
            null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class FakeTrustedKeyStore : IPluginTrustedKeyStore
    {
        private readonly Dictionary<string, PluginTrustedKeyRecord> _rows = new(StringComparer.Ordinal);

        public PluginTrustedKeyRecord this[string keyId]
        {
            set => _rows[keyId] = value;
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginTrustedKeyRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginTrustedKeyRecord>>(_rows.Values.ToList());

        public Task<PluginTrustedKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(keyId, out var row) ? row : null);

        public Task SaveAsync(PluginTrustedKeyRecord record, CancellationToken cancellationToken)
        {
            _rows[record.KeyId] = record;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.Remove(keyId));
    }

    private sealed class ExplodingTrustedKeyStore : IPluginTrustedKeyStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginTrustedKeyRecord>> ListAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is down");

        public Task<PluginTrustedKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is down");

        public Task SaveAsync(PluginTrustedKeyRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is down");

        public Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("database is down");
    }
}

/// <summary>同步用例：游标、批次上限、错误分类。</summary>
public sealed class PluginSyncServiceTests
{
    private static readonly Guid KnowledgeBase = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_successful_sync_advances_the_cursor_and_saves_the_interval()
    {
        var harness = new SyncHarness();
        harness.Runtime.Result = PluginJson.ToNode(Batch(("note-1", "hello")));

        var outcome = await harness.SyncAsync();

        Assert.Equal(1, outcome.Created);
        Assert.Equal("next-page", outcome.NextCursor);
        Assert.Equal(300, outcome.NextSyncAfterSeconds);
        Assert.Equal(PluginSyncStatuses.Success, outcome.Status);

        var stored = harness.States.Stored;
        Assert.NotNull(stored);
        Assert.Equal("next-page", stored!.Cursor);
        Assert.Equal(300, stored.NextSyncAfterSeconds);
        Assert.NotNull(stored.LastSyncedAt);

        // 插件收到的游标：第一次同步没有任何游标。
        Assert.Null(harness.Runtime.LastArguments?["cursor"]);
    }

    [Fact]
    public async Task The_persisted_cursor_is_used_when_none_is_supplied()
    {
        var harness = new SyncHarness();
        harness.States.Seed = new PluginSyncState(
            SyncHarness.ScopeKey, SyncHarness.PluginId, "platform.notes.sync", KnowledgeBase,
            "cursor-42", 300, DateTimeOffset.UtcNow.AddMinutes(-10), PluginSyncStatuses.Success, null, DateTimeOffset.UtcNow);
        harness.Runtime.Result = PluginJson.ToNode(Batch(("note-1", "hello")));

        await harness.SyncAsync();

        Assert.Equal("cursor-42", harness.Runtime.LastArguments?["cursor"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_plugin_asking_for_a_too_short_interval_is_clamped_to_the_minimum()
    {
        var harness = new SyncHarness();
        harness.Runtime.Result = PluginJson.ToNode(Batch(("note-1", "hello")) with { NextSyncAfterSeconds = 1 });

        var outcome = await harness.SyncAsync();

        // 插件写错单位（1 秒）会把任务队列刷爆，所以夹到最小值 60 秒。
        Assert.Equal(60, outcome.NextSyncAfterSeconds);
    }

    [Fact]
    public async Task Scheduled_sync_disabled_means_no_interval_is_recorded()
    {
        var harness = new SyncHarness(scheduledSyncEnabled: false);
        harness.Runtime.Result = PluginJson.ToNode(Batch(("note-1", "hello")));

        var outcome = await harness.SyncAsync();

        Assert.Null(outcome.NextSyncAfterSeconds);
        Assert.Null(harness.States.Stored?.NextSyncAfterSeconds);
    }

    [Fact]
    public async Task An_oversized_batch_is_rejected_without_moving_the_cursor()
    {
        var harness = new SyncHarness(maxItems: 2);
        harness.States.Seed = new PluginSyncState(
            SyncHarness.ScopeKey, SyncHarness.PluginId, "platform.notes.sync", KnowledgeBase,
            "cursor-1", 300, DateTimeOffset.UtcNow.AddMinutes(-10), PluginSyncStatuses.Success, null, DateTimeOffset.UtcNow);
        harness.Runtime.Result = PluginJson.ToNode(Batch(("a", "1"), ("b", "2"), ("c", "3")));

        var exception = await Assert.ThrowsAsync<PluginSyncException>(() => harness.SyncAsync());

        Assert.False(exception.Retryable);
        Assert.Contains("超过单批上限", exception.Message, StringComparison.Ordinal);

        // 截断会永久丢数据，所以游标必须原地不动。
        Assert.Equal("cursor-1", harness.States.Stored?.Cursor);
        Assert.Equal(PluginSyncStatuses.Failed, harness.States.Stored?.LastStatus);
        Assert.Equal(0, harness.Sink.Calls);
    }

    [Fact]
    public async Task An_unparseable_result_is_a_non_retryable_failure()
    {
        var harness = new SyncHarness();
        harness.Runtime.Result = JsonValue.Create("not a batch");

        var exception = await Assert.ThrowsAsync<PluginSyncException>(() => harness.SyncAsync());

        Assert.False(exception.Retryable);
        Assert.Contains("有效的摄取结构", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unhealthy_plugin_is_retryable_but_a_permission_error_is_not()
    {
        var unhealthy = new SyncHarness();
        unhealthy.Runtime.Failure = new PluginRpcException(PluginProtocol.ErrorUnhealthy, "process is down");
        var retryable = await Assert.ThrowsAsync<PluginSyncException>(() => unhealthy.SyncAsync());
        Assert.True(retryable.Retryable);

        var denied = new SyncHarness();
        denied.Runtime.Failure = new PluginRpcException(PluginProtocol.ErrorPermissionDenied, "not allowed");
        var fatal = await Assert.ThrowsAsync<PluginSyncException>(() => denied.SyncAsync());
        Assert.False(fatal.Retryable);
    }

    [Fact]
    public async Task A_missing_knowledge_base_fails_before_calling_the_plugin()
    {
        var harness = new SyncHarness(knowledgeBaseExists: false);

        var exception = await Assert.ThrowsAsync<PluginSyncException>(() => harness.SyncAsync());

        Assert.False(exception.Retryable);
        Assert.Equal(0, harness.Runtime.Calls);
    }

    [Fact]
    public void Batch_parsing_accepts_both_shapes()
    {
        var direct = PluginSyncService.ParseBatch(JsonNode.Parse(
            """{"items":[{"source":"p","externalId":"1","content":"x"}],"nextCursor":"c"}"""));
        Assert.NotNull(direct);
        Assert.Single(direct!.Items);
        Assert.Equal("c", direct.NextCursor);

        var nested = PluginSyncService.ParseBatch(JsonNode.Parse(
            """{"result":{"items":[{"source":"p","externalId":"2","content":"y"}],"nextSyncAfterSeconds":120}}"""));
        Assert.NotNull(nested);
        Assert.Single(nested!.Items);
        Assert.Equal(120, nested.NextSyncAfterSeconds);

        Assert.Null(PluginSyncService.ParseBatch(null));
        Assert.Null(PluginSyncService.ParseBatch(JsonNode.Parse("\"just a string\"")));
    }

    private static PlatformIngestionBatch Batch(params (string ExternalId, string Content)[] items) => new()
    {
        Items = items.Select(item => new PlatformIngestionItem
        {
            Source = SyncHarness.PluginId,
            ExternalId = item.ExternalId,
            Title = item.ExternalId,
            Content = item.Content
        }).ToList(),
        NextCursor = "next-page",
        NextSyncAfterSeconds = 300
    };

    private sealed class SyncHarness
    {
        public const string PluginId = "com.example.test";

        public static readonly string ScopeKey =
            IPluginSyncStateStore.BuildScopeKey(PluginId, "platform.notes.sync", KnowledgeBase);

        public SyncHarness(int maxItems = 200, bool scheduledSyncEnabled = true, bool knowledgeBaseExists = true)
        {
            Runtime = new FakeRuntime();
            States = new FakeSyncStateStore();
            Sink = new FakeSink();

            var options = Microsoft.Extensions.Options.Options.Create(new PluginOptions
            {
                MaxIngestionItemsPerBatch = maxItems,
                ScheduledSyncEnabled = scheduledSyncEnabled,
                MinScheduledSyncIntervalSeconds = 60
            });

            Service = new PluginSyncService(
                Runtime,
                Sink,
                States,
                new PluginAuditWriter(new FakeRegistry(), NullLogger<PluginAuditWriter>.Instance),
                new FakeDirectory(knowledgeBaseExists),
                options,
                NullLogger<PluginSyncService>.Instance);
        }

        public FakeRuntime Runtime { get; }

        public FakeSyncStateStore States { get; }

        public FakeSink Sink { get; }

        public PluginSyncService Service { get; }

        public Task<PluginSyncOutcome> SyncAsync() =>
            Service.SyncAsync(PluginId, "platform.notes.sync", KnowledgeBase, null, progress: null, CancellationToken.None);
    }

    private sealed class FakeRuntime : IPluginRuntime
    {
        public JsonNode? Result { get; set; }

        public Exception? Failure { get; set; }

        public JsonNode? LastArguments { get; private set; }

        public int Calls { get; private set; }

        public Task<PluginInvocationResult> InvokeAsync(
            string pluginId,
            string capabilityId,
            JsonNode? arguments,
            PluginInvocationContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastArguments = arguments;

            if (Failure is not null) throw Failure;

            return Task.FromResult(new PluginInvocationResult(pluginId, capabilityId, Result, 5));
        }

        public bool IsScheduledSyncEligible(string pluginId) => true;
    }

    private sealed class FakeSink : IPluginIngestionSink
    {
        public int Calls { get; private set; }

        public Task<PluginIngestionOutcome> IngestAsync(
            string pluginId,
            PlatformIngestionBatch batch,
            Guid? knowledgeBaseId,
            CancellationToken cancellationToken)
        {
            Calls++;
            var count = batch.Items.Count;
            return Task.FromResult(new PluginIngestionOutcome(count, 0, 0, 0, 0, 0, []));
        }
    }

    private sealed class FakeSyncStateStore : IPluginSyncStateStore
    {
        private readonly Dictionary<string, PluginSyncState> _rows = new(StringComparer.Ordinal);

        public PluginSyncState? Seed
        {
            set
            {
                if (value is not null) _rows[value.ScopeKey] = value;
            }
        }

        public PluginSyncState? Stored => _rows.Values.FirstOrDefault();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PluginSyncState?> GetByScopeAsync(string scopeKey, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.TryGetValue(scopeKey, out var row) ? row : null);

        public Task SaveAsync(PluginSyncState state, CancellationToken cancellationToken)
        {
            _rows[state.ScopeKey] = state;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PluginSyncState>> ListAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSyncState>>(_rows.Values.Where(row => row.PluginId == pluginId).ToList());

        public Task<IReadOnlyList<PluginSyncState>> ListScheduledAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSyncState>>(
                _rows.Values.Where(row => row.NextSyncAfterSeconds is > 0).Take(limit).ToList());

        public Task DeleteAllForPluginAsync(string pluginId, CancellationToken cancellationToken)
        {
            foreach (var key in _rows.Where(row => row.Value.PluginId == pluginId).Select(row => row.Key).ToList())
                _rows.Remove(key);

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDirectory(bool exists) : IKnowledgeBaseDirectory
    {
        public Task<bool> ExistsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(exists);

        public Task<IReadOnlyList<KnowledgeBaseSummary>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgeBaseSummary>>([]);
    }

    private sealed class FakeRegistry : IPluginRegistry
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<PluginRegistration>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginRegistration>>([]);

        public Task<PluginRegistration?> GetAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.FromResult<PluginRegistration?>(null);

        public Task SaveAsync(PluginRegistration registration, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(string pluginId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RecordAuditAsync(PluginAuditRecord record, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginAuditRecord>> QueryAuditAsync(
            string? pluginId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginAuditRecord>>([]);
    }
}

/// <summary>定时同步补投：到点才投、停用不投、终态按间隔重新武装。</summary>
public sealed class PluginSyncJobFeederTests
{
    private static readonly Guid KnowledgeBase = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_due_scope_is_enqueued_once()
    {
        var harness = new FeederHarness();
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromMinutes(10), intervalSeconds: 300));
        harness.Queue.EnqueueFactory = command => FakeJobQueue.Record("job_1", command);

        var created = await harness.FeedAsync();

        Assert.Equal(1, created);
        Assert.Single(harness.Queue.Enqueued);
        Assert.Equal(JobTypes.PluginSync, harness.Queue.Enqueued[0].Type);
        Assert.Equal($"{JobTypes.PluginSync}:{harness.States.States[0].ScopeKey}", harness.Queue.Enqueued[0].IdempotencyKey);
    }

    [Fact]
    public async Task A_scope_that_is_not_due_yet_is_skipped()
    {
        var harness = new FeederHarness();
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromSeconds(30), intervalSeconds: 300));

        var created = await harness.FeedAsync();

        Assert.Equal(0, created);
        Assert.Empty(harness.Queue.Enqueued);
    }

    [Fact]
    public async Task A_scope_with_a_short_plugin_interval_is_clamped()
    {
        var harness = new FeederHarness();
        // 插件要求 5 秒（小于最小值 60 秒），且已过 10 秒：按 60 秒算，还不到点。
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromSeconds(10), intervalSeconds: 5));

        Assert.Equal(0, await harness.FeedAsync());

        harness.States.Reset();
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromSeconds(61), intervalSeconds: 5));
        harness.Queue.EnqueueFactory = command => FakeJobQueue.Record("job_1", command);

        Assert.Equal(1, await harness.FeedAsync());
    }

    [Fact]
    public async Task Disabled_or_unknown_plugins_are_not_scheduled()
    {
        var harness = new FeederHarness();
        harness.Runtime.Eligible = false;
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromHours(1), intervalSeconds: 60));

        Assert.Equal(0, await harness.FeedAsync());
        Assert.Empty(harness.Queue.Enqueued);
    }

    [Fact]
    public async Task A_queued_or_running_job_is_never_duplicated()
    {
        var harness = new FeederHarness();
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromHours(1), intervalSeconds: 60));
        harness.Queue.EnqueueFactory = command => FakeJobQueue.Record("job_1", command, status: JobStatuses.Running, attempts: 1);

        Assert.Equal(0, await harness.FeedAsync());
        Assert.Empty(harness.Queue.Retried);
    }

    [Fact]
    public async Task A_finished_job_is_rearmed_only_after_one_more_interval()
    {
        var harness = new FeederHarness();
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromHours(1), intervalSeconds: 60));

        // 刚结束：还没到下一个周期。
        harness.Queue.EnqueueFactory = command => FakeJobQueue.Record(
            "job_1", command, status: JobStatuses.Failed, attempts: 3, completedAt: DateTimeOffset.UtcNow);
        Assert.Equal(0, await harness.FeedAsync());
        Assert.Empty(harness.Queue.Retried);

        // 结束已经两分钟：该再来一次了。
        harness.Queue.EnqueueFactory = command => FakeJobQueue.Record(
            "job_1", command, status: JobStatuses.Failed, attempts: 3, completedAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        Assert.Equal(1, await harness.FeedAsync());
        Assert.Single(harness.Queue.Retried);
    }

    [Fact]
    public async Task Scheduled_sync_can_be_switched_off_globally()
    {
        var harness = new FeederHarness(scheduledEnabled: false);
        harness.States.Add(State(lastSyncedAgo: TimeSpan.FromDays(1), intervalSeconds: 60));

        Assert.Equal(0, await harness.FeedAsync());
        Assert.Empty(harness.Queue.Enqueued);
    }

    private static PluginSyncState State(TimeSpan lastSyncedAgo, int intervalSeconds) => new(
        IPluginSyncStateStore.BuildScopeKey("com.example.test", "platform.notes.sync", KnowledgeBase),
        "com.example.test",
        "platform.notes.sync",
        KnowledgeBase,
        "cursor-1",
        intervalSeconds,
        DateTimeOffset.UtcNow - lastSyncedAgo,
        PluginSyncStatuses.Success,
        null,
        DateTimeOffset.UtcNow - lastSyncedAgo);

    private sealed class FeederHarness
    {
        public FeederHarness(bool scheduledEnabled = true)
        {
            Queue = new FakeJobQueue();
            States = new FakeStateStore();
            Runtime = new FakeRuntime();

            Feeder = new PluginSyncJobFeeder(
                States,
                Runtime,
                Queue,
                Microsoft.Extensions.Options.Options.Create(new PluginOptions
                {
                    ScheduledSyncEnabled = scheduledEnabled,
                    MinScheduledSyncIntervalSeconds = 60
                }),
                NullLogger<PluginSyncJobFeeder>.Instance);
        }

        public FakeJobQueue Queue { get; }

        public FakeStateStore States { get; }

        public FakeRuntime Runtime { get; }

        public PluginSyncJobFeeder Feeder { get; }

        public Task<int> FeedAsync() => Feeder.FeedAsync(CancellationToken.None);
    }

    private sealed class FakeRuntime : IPluginRuntime
    {
        public bool Eligible { get; set; } = true;

        public Task<PluginInvocationResult> InvokeAsync(
            string pluginId,
            string capabilityId,
            JsonNode? arguments,
            PluginInvocationContext context,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("the feeder never invokes the plugin directly");

        public bool IsScheduledSyncEligible(string pluginId) => Eligible;
    }

    private sealed class FakeStateStore : IPluginSyncStateStore
    {
        private readonly List<PluginSyncState> _rows = [];

        public IReadOnlyList<PluginSyncState> States => _rows;

        public void Add(PluginSyncState state) => _rows.Add(state);

        public void Reset() => _rows.Clear();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<PluginSyncState?> GetByScopeAsync(string scopeKey, CancellationToken cancellationToken) =>
            Task.FromResult(_rows.FirstOrDefault(row => row.ScopeKey == scopeKey));

        public Task SaveAsync(PluginSyncState state, CancellationToken cancellationToken)
        {
            _rows.Add(state);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PluginSyncState>> ListAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSyncState>>(_rows.Where(row => row.PluginId == pluginId).ToList());

        public Task<IReadOnlyList<PluginSyncState>> ListScheduledAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PluginSyncState>>(
                _rows.Where(row => row.NextSyncAfterSeconds is > 0).Take(limit).ToList());

        public Task DeleteAllForPluginAsync(string pluginId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    /// <summary>只关心“补投器发了什么”，领取/执行由 JobSchedulerTests 覆盖。</summary>
    private sealed class FakeJobQueue : IJobQueue
    {
        public List<EnqueueJobCommand> Enqueued { get; } = [];

        public List<string> Retried { get; } = [];

        /// <summary>决定入队返回一个什么状态的既有任务（模拟幂等键命中）。</summary>
        public Func<EnqueueJobCommand, JobRecord> EnqueueFactory { get; set; } =
            command => Record("job_new", command);

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<JobRecord> EnqueueAsync(EnqueueJobCommand command, CancellationToken cancellationToken)
        {
            Enqueued.Add(command);
            return Task.FromResult(EnqueueFactory(command));
        }

        public Task<JobRecord?> RetryAsync(string jobId, CancellationToken cancellationToken)
        {
            Retried.Add(jobId);
            return Task.FromResult<JobRecord?>(Record(jobId, new EnqueueJobCommand(JobTypes.PluginSync),
                status: JobStatuses.Queued));
        }

        public static JobRecord Record(
            string id,
            EnqueueJobCommand command,
            string status = JobStatuses.Queued,
            int attempts = 0,
            DateTimeOffset? completedAt = null) => new(
            id,
            command.Type,
            status,
            command.Priority,
            attempts,
            command.MaxAttempts ?? 3,
            command.Payload,
            null,
            0,
            null,
            null,
            null,
            command.IdempotencyKey,
            command.KnowledgeBaseId,
            command.SubjectId,
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            completedAt,
            null);

        public Task<JobLease?> ClaimAsync(string workerId, IReadOnlyList<string> types, TimeSpan lease, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> HeartbeatAsync(string jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReportProgressAsync(string jobId, string workerId, int progress, string? message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CompleteAsync(string jobId, string workerId, string? result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailAsync(string jobId, string workerId, JobFailure failure, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<JobCounts> CountAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> RequestCancelAsync(string jobId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RecoverStaleAsync(TimeSpan grace, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

/// <summary>同步任务的参数解析。</summary>
public sealed class PluginSyncJobHandlerTests
{
    [Fact]
    public void A_valid_payload_is_parsed()
    {
        var documentId = Guid.NewGuid();
        var job = Job(documentId, DefaultPayload(documentId));

        var payload = PluginSyncJobHandler.ReadPayload(job);

        Assert.NotNull(payload);
        Assert.Equal("com.example.test", payload!.PluginId);
        Assert.Equal("platform.notes.sync", payload.CapabilityId);
        Assert.Equal(documentId, payload.KnowledgeBaseId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"pluginId\":\"p\",\"capabilityId\":\"c\"}")]
    [InlineData("{\"pluginId\":\"p\",\"capabilityId\":\"c\",\"knowledgeBaseId\":\"not-a-guid\"}")]
    public void A_malformed_payload_is_rejected(string payload)
    {
        Assert.Null(PluginSyncJobHandler.ReadPayload(Job(Guid.NewGuid(), payload)));
    }

    [Fact]
    public void A_null_payload_is_rejected()
    {
        Assert.Null(PluginSyncJobHandler.ReadPayload(Job(Guid.NewGuid(), null)));
    }

    private static string DefaultPayload(Guid knowledgeBaseId) =>
        $"{{\"pluginId\":\"com.example.test\",\"capabilityId\":\"platform.notes.sync\",\"knowledgeBaseId\":\"{knowledgeBaseId:D}\"}}";

    private static JobRecord Job(Guid knowledgeBaseId, string? payload) => new(
        "job_1",
        JobTypes.PluginSync,
        JobStatuses.Queued,
        JobPriorities.Low,
        0,
        3,
        payload,
        null,
        0,
        null,
        null,
        null,
        null,
        knowledgeBaseId,
        null,
        false,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        null,
        null);
}
