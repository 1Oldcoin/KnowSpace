using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 插件调用准入控制（P1-4）。
///
/// <para>
/// 这些用例存在的理由：原来的实现用 <c>SemaphoreSlim.WaitAsync</c> 排队，<b>队列没有上限</b>，
/// 所以「插件只允许 2 并发」在真实使用中没有可感知的表现——10 个并发请求是 2 个跑完、
/// 8 个慢慢超时，看起来像卡死。这里断言的是「超额立刻拒绝」与「拒绝不吃名额」。
/// </para>
/// </summary>
public sealed class PluginInvocationLimiterTests
{
    private static PluginInvocationLimiter Create(int queuePerPlugin = 16, int queuePerCapability = 8, Dictionary<string, int>? capabilityLimits = null)
    {
        var options = new PluginOptions
        {
            MaxQueuedInvocationsPerPlugin = queuePerPlugin,
            MaxQueuedInvocationsPerCapability = queuePerCapability,
            CapabilityConcurrencyLimits = capabilityLimits is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(capabilityLimits, StringComparer.Ordinal)
        };

        return new PluginInvocationLimiter(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<PluginInvocationLimiter>.Instance);
    }

    private static bool IsOverloaded(Exception exception) =>
        exception is PluginRpcException rpc && rpc.Code == PluginProtocol.ErrorOverloaded;

    [Fact]
    public async Task A_zero_queue_limit_rejects_everything_beyond_capacity()
    {
        // 队列上限 0 的含义是「没有空闲槽位就拒绝」，不是「总是拒绝」——这两种语义差一个 bug。
        using var limiter = Create(queuePerPlugin: 0);

        const int capacity = 2;
        const int attempts = 10;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var rejected = 0;

        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            try
            {
                using var lease = await limiter.AcquireAsync("com.example.notes", "sync", capacity, TimeSpan.FromSeconds(30), CancellationToken.None);
                Interlocked.Increment(ref admitted);
                await gate.Task;
            }
            catch (PluginRpcException exception) when (IsOverloaded(exception))
            {
                Interlocked.Increment(ref rejected);
            }
        }).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref rejected) == attempts - capacity, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => limiter.Snapshot()[0].Running == capacity, TimeSpan.FromSeconds(5));

        Assert.Equal(capacity, Volatile.Read(ref admitted));
        Assert.Equal(attempts - capacity, Volatile.Read(ref rejected));

        var snapshot = Assert.Single(limiter.Snapshot());
        Assert.Equal(capacity, snapshot.ConcurrencyLimit);
        Assert.Equal(capacity, snapshot.Running);
        Assert.Equal(0, snapshot.Queued);

        gate.SetResult();
        await Task.WhenAll(tasks);

        // 全部释放后必须回到 0，否则一次拒绝/一次成功会永久吃掉名额。
        Assert.Equal(0, limiter.Snapshot()[0].Running);
        Assert.Equal(0, limiter.Snapshot()[0].Queued);
    }

    [Fact]
    public async Task The_queue_limit_caps_waiters_instead_of_letting_them_pile_up()
    {
        using var limiter = Create(queuePerPlugin: 2);

        const int capacity = 1;
        const int attempts = 10;
        var expectedAdmitted = capacity + 2;

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = 0;
        var rejected = 0;

        var tasks = Enumerable.Range(0, attempts).Select(async _ =>
        {
            try
            {
                using var lease = await limiter.AcquireAsync("com.example.notes", "sync", capacity, TimeSpan.FromSeconds(30), CancellationToken.None);
                Interlocked.Increment(ref admitted);
                await gate.Task;
            }
            catch (PluginRpcException exception) when (IsOverloaded(exception))
            {
                Interlocked.Increment(ref rejected);
            }
        }).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref rejected) == attempts - expectedAdmitted, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => limiter.Snapshot()[0].Running == capacity, TimeSpan.FromSeconds(5));

        var snapshot = Assert.Single(limiter.Snapshot());
        Assert.Equal(capacity, snapshot.Running);
        Assert.Equal(2, snapshot.Queued);

        gate.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(expectedAdmitted, Volatile.Read(ref admitted));
        Assert.Equal(attempts - expectedAdmitted, Volatile.Read(ref rejected));
    }

    [Fact]
    public async Task Queue_wait_timeout_reports_overloaded_rather_than_hanging()
    {
        using var limiter = Create(queuePerPlugin: 4);

        // 一个长调用占住唯一槽位。
        using var busy = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<PluginRpcException>(() =>
            limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        stopwatch.Stop();

        Assert.Equal(PluginProtocol.ErrorOverloaded, exception.Code);
        Assert.Contains("排队", exception.Message);
        Assert.True(stopwatch.ElapsedMilliseconds < 3000, $"排队应在约 150ms 后放弃，实际 {stopwatch.ElapsedMilliseconds}ms");

        // 超时的那个不能占着队列位置。
        Assert.Equal(0, limiter.Snapshot()[0].Queued);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_overload()
    {
        using var limiter = Create();

        using var busy = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(30), cts.Token));

        Assert.Equal(0, limiter.Snapshot()[0].Queued);
    }

    [Fact]
    public async Task A_capability_limit_subdivides_the_plugin_budget()
    {
        using var limiter = Create(capabilityLimits: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sync"] = 1
        });

        // 插件本身允许 4 并发，但 sync 只允许 1。
        using var syncLease = await limiter.AcquireAsync("com.example.notes", "sync", 4, TimeSpan.FromSeconds(5), CancellationToken.None);

        var rejected = await Assert.ThrowsAsync<PluginRpcException>(() =>
            limiter.AcquireAsync("com.example.notes", "sync", 4, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        Assert.Equal(PluginProtocol.ErrorOverloaded, rejected.Code);

        // 同一个插件的另一个能力不受影响：能力级限流不该变成插件级限流。
        using var other = await limiter.AcquireAsync("com.example.notes", "search", 4, TimeSpan.FromSeconds(5), CancellationToken.None);

        var plugin = Assert.Single(limiter.Snapshot());
        Assert.Equal(2, plugin.Running);
        var capability = Assert.Single(plugin.Capabilities);
        Assert.Equal("sync", capability.CapabilityId);
        Assert.Equal(1, capability.ConcurrencyLimit);
        Assert.Equal(1, capability.Running);
    }

    [Fact]
    public async Task A_plugin_qualified_key_only_affects_that_plugin()
    {
        using var limiter = Create(capabilityLimits: new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["com.example.notes/sync"] = 1
        });

        using var notesSync = await limiter.AcquireAsync("com.example.notes", "sync", 4, TimeSpan.FromSeconds(5), CancellationToken.None);

        // 另一个插件同名能力不受约束。
        using var otherSync = await limiter.AcquireAsync("com.other.chat", "sync", 4, TimeSpan.FromSeconds(5), CancellationToken.None);

        var snapshots = limiter.Snapshot();
        Assert.Equal(2, snapshots.Count);
        Assert.Equal("com.example.notes", snapshots[0].PluginId);
        Assert.Single(snapshots[0].Capabilities);
        Assert.Equal("com.other.chat", snapshots[1].PluginId);
        Assert.Empty(snapshots[1].Capabilities);
    }

    [Fact]
    public async Task Plugins_are_counted_independently()
    {
        using var limiter = Create(queuePerPlugin: 0);

        using var first = await limiter.AcquireAsync("plugin.a", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        // 一个插件的拥堵不该影响另一个插件。
        using var second = await limiter.AcquireAsync("plugin.b", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.ThrowsAsync<PluginRpcException>(() =>
            limiter.AcquireAsync("plugin.a", "sync", 1, TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Equal(2, limiter.Snapshot().Count);
    }

    [Fact]
    public async Task Releasing_restores_capacity_and_counters()
    {
        using var limiter = Create();

        for (var round = 0; round < 3; round++)
        {
            var lease = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.Equal(1, limiter.Snapshot()[0].Running);
            lease.Dispose();

            // 重复 Dispose 不能多还一次闸门（否则容量会被慢慢放大）。
            lease.Dispose();
            Assert.Equal(0, limiter.Snapshot()[0].Running);
        }

        // 若重复 Dispose 多释放了信号量，这里会同时跑起多个调用。
        var running = 0;
        var maxRunning = 0;
        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            using var lease = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);
            var current = Interlocked.Increment(ref running);
            int observed;
            while (current > (observed = Volatile.Read(ref maxRunning)))
                Interlocked.CompareExchange(ref maxRunning, current, observed);

            await Task.Delay(20);
            Interlocked.Decrement(ref running);
        }).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1, Volatile.Read(ref maxRunning));
    }

    [Fact]
    public async Task Forget_drops_the_counters_of_an_uninstalled_plugin()
    {
        using var limiter = Create();

        using (var lease = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None))
            Assert.Single(limiter.Snapshot());

        limiter.Forget("com.example.notes");
        Assert.Empty(limiter.Snapshot());

        // 重装后重新从 0 开始计数。
        using var reinstalled = await limiter.AcquireAsync("com.example.notes", "sync", 1, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, Assert.Single(limiter.Snapshot()).Running);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        throw new TimeoutException("condition was not satisfied in time");
    }
}
