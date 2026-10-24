using MyFirstMafAgent.Api.Application.Jobs;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 按类型配额（P1-5）：占坑成功/失败、不限流放行、名额必然归还。
///
/// <para>
/// 这里只测配额对象的算术；「调度器会不会因此超发」由
/// <see cref="JobSchedulerTests.Type_limit_caps_one_type_without_starving_the_others"/>
/// 用真实调度器 + 真实并发验证——那条测试是本功能的第一道防线：
/// 曾经的"领取前先看一眼计数"实现就是被它抓到 limit=1 跑成了 4 并发。
/// </para>
/// </summary>
public sealed class JobTypeQuotaTests
{
    private const string Sync = "plugin.sync";
    private const string Ingest = "document.ingest";

    private static JobTypeQuota Create(Dictionary<string, int>? limits = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new JobOptions
        {
            TypeLimits = limits is null
                ? new Dictionary<string, int>(StringComparer.Ordinal)
                : new Dictionary<string, int>(limits, StringComparer.Ordinal)
        }));

    [Fact]
    public void Unconfigured_type_is_always_admitted()
    {
        var quota = Create();

        Assert.False(quota.HasLimits);
        Assert.Null(quota.LimitFor(Ingest));

        // 不限流的类型不计数：不该白占内存，也不该被上限影响。
        for (var i = 0; i < 10; i++) Assert.True(quota.TryReserve(Ingest));
        Assert.Equal(0, quota.RunningFor(Ingest));
    }

    [Fact]
    public void Reserve_stops_at_the_limit_and_release_frees_a_slot()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 2 });

        Assert.True(quota.TryReserve(Sync));
        Assert.True(quota.TryReserve(Sync));
        Assert.False(quota.TryReserve(Sync));      // 第三个必须被挡
        Assert.Equal(2, quota.RunningFor(Sync));

        quota.Release(Sync);
        Assert.True(quota.TryReserve(Sync));
    }

    [Fact]
    public void Release_without_reserve_never_goes_negative()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 1 });

        quota.Release(Sync);

        Assert.Equal(0, quota.RunningFor(Sync));
        Assert.True(quota.TryReserve(Sync));
    }

    [Fact]
    public void Zero_or_negative_limit_means_unlimited()
    {
        // 配置里误写 0 不应该让整类任务永远跑不起来。
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 0, [Ingest] = -1 });

        Assert.Null(quota.LimitFor(Sync));
        Assert.Null(quota.LimitFor(Ingest));
        Assert.True(quota.TryReserve(Sync));
        Assert.True(quota.TryReserve(Ingest));
    }

    [Fact]
    public void Concurrent_reservations_never_exceed_the_limit()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 3 });
        var granted = 0;

        Parallel.For(0, 200, _ =>
        {
            if (quota.TryReserve(Sync)) Interlocked.Increment(ref granted);
        });

        Assert.Equal(3, granted);          // 原子占坑：恰好 3 个
        Assert.Equal(3, quota.RunningFor(Sync));
    }

    [Fact]
    public void EligibleTypes_drops_only_the_full_types()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 1 });
        Assert.True(quota.TryReserve(Sync));

        var eligible = quota.EligibleTypes([Sync, Ingest]);

        Assert.Equal(new[] { Ingest }, eligible);
        Assert.False(quota.AllExhausted([Sync, Ingest]));
    }

    [Fact]
    public void EligibleTypes_returns_the_same_list_when_no_limits_exist()
    {
        var quota = Create();
        IReadOnlyList<string> types = [Sync, Ingest];

        Assert.Same(types, quota.EligibleTypes(types));
    }

    [Fact]
    public void Everything_exhausted_is_detected()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 1, [Ingest] = 1 });
        quota.TryReserve(Sync);
        quota.TryReserve(Ingest);

        Assert.Empty(quota.EligibleTypes([Sync, Ingest]));
        Assert.True(quota.AllExhausted([Sync, Ingest]));
    }

    [Fact]
    public void Snapshot_lists_limits_and_running_counts_sorted()
    {
        var quota = Create(new Dictionary<string, int>(StringComparer.Ordinal) { [Sync] = 3 });
        quota.TryReserve(Sync);
        quota.TryReserve(Sync);

        var snapshot = quota.Snapshot([Ingest]);

        Assert.Equal(new[] { "document.ingest", "plugin.sync" }, snapshot.Select(item => item.Type));
        Assert.Equal(new JobTypeQuotaSnapshot(Ingest, null, 0), snapshot[0]);
        Assert.Equal(new JobTypeQuotaSnapshot(Sync, 3, 2), snapshot[1]);
    }
}
