using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>单个任务类型的配额快照，供 <c>GET /api/jobs/quotas</c> 与界面展示。</summary>
public sealed record JobTypeQuotaSnapshot(string Type, int? Limit, int Running);

/// <summary>
/// 按任务类型的并发配额（P1-5）。
///
/// <para>
/// 为什么需要它：槽位数是全局的。10 个 <c>plugin.sync</c> 排在最前面时会占满所有槽位，
/// 之后到达的 3 个 <c>document.ingest</c>（哪怕优先级更高）只能干等——
/// 「优先级」只在<b>选谁先跑</b>时有用，管不了<b>谁已经占着槽</b>。
/// 给类型设上限，等于给每类任务留出保底的槽位，高优先级任务才真的能"插队"。
/// </para>
///
/// <para>
/// 实现是<b>先原子占坑、再领取</b>（见 <see cref="TryReserve"/>）。
/// 如果只做“领取前看一眼计数”，N 个槽位会同时通过检查再一起去领取，
/// 上限立刻被穿透（实测 4 槽位会把 limit=1 的类型跑成 4 并发）。
/// 占坑失败就换下一个候选类型，所以永远不超发，也不需要把已领取的任务退回去
/// （退回去会导致租约与尝试次数反复抖动）。
/// </para>
///
/// <para>
/// 边界要说清楚：计数是<b>进程内</b>的。多进程部署时每进程各自计数，
/// 即“每进程上限”而非“集群上限”。对本项目（单机单实例、或每副本独立限流）够用，
/// 且不引入任何跨进程锁。
/// </para>
/// </summary>
public sealed class JobTypeQuota
{
    private readonly IReadOnlyDictionary<string, int> limits;
    private readonly ConcurrentDictionary<string, int> running = new(StringComparer.Ordinal);

    public JobTypeQuota(IOptions<JobOptions> options)
    {
        var configured = options.Value.TypeLimits ?? [];
        var normalized = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (type, limit) in configured)
        {
            // 0 或负数当成"不限制"：配置文件里写错一个 0 不该让整类任务永远跑不起来。
            if (string.IsNullOrWhiteSpace(type) || limit <= 0) continue;
            normalized[type.Trim()] = limit;
        }

        limits = normalized;
    }

    /// <summary>是否配了任何类型上限。没配时调度器走「全局优先级」快路径，行为与旧版一致。</summary>
    public bool HasLimits => limits.Count > 0;

    public int? LimitFor(string type) => limits.TryGetValue(type, out var limit) ? limit : null;

    public int RunningFor(string type) => running.TryGetValue(type, out var count) ? count : 0;

    public bool HasCapacity(string type) =>
        LimitFor(type) is not { } limit || RunningFor(type) < limit;

    /// <summary>
    /// 抢占一个名额；成功返回 true。不限流的类型永远成功（不计数）。
    /// 容量判断与占位必须在同一次原子操作里完成——这是上限不被穿透的关键。
    /// </summary>
    public bool TryReserve(string type)
    {
        if (LimitFor(type) is not { } limit) return true;
        if (running.TryAdd(type, 1)) return true;

        while (true)
        {
            var current = RunningFor(type);
            if (current >= limit) return false;
            if (running.TryUpdate(type, current + 1, current)) return true;
        }
    }

    /// <summary>释放名额；必须在 finally 里调，否则一次异常就永久吃掉一个槽。</summary>
    public void Release(string type)
    {
        if (LimitFor(type) is null) return;
        running.AddOrUpdate(type, 0, (_, current) => Math.Max(0, current - 1));
    }

    /// <summary>过滤掉已满的类型；没配额时原样返回（零分配）。</summary>
    public IReadOnlyList<string> EligibleTypes(IReadOnlyList<string> types)
    {
        if (!HasLimits) return types;

        var eligible = new List<string>(types.Count);
        foreach (var type in types)
        {
            if (HasCapacity(type)) eligible.Add(type);
        }

        return eligible;
    }

    /// <summary>所有类型都被配额挡住（用来区分“暂时没名额”与“队列里真没活儿”）。</summary>
    public bool AllExhausted(IReadOnlyList<string> types) =>
        types.Count > 0 && HasLimits && EligibleTypes(types).Count == 0;

    /// <summary>限流配置 + 实时占用，按类型名排序，便于稳定展示。</summary>
    public IReadOnlyList<JobTypeQuotaSnapshot> Snapshot(IEnumerable<string> knownTypes)
    {
        var types = new SortedSet<string>(knownTypes, StringComparer.Ordinal);
        foreach (var type in limits.Keys) types.Add(type);
        foreach (var type in running.Keys) types.Add(type);

        return types
            .Select(type => new JobTypeQuotaSnapshot(type, LimitFor(type), RunningFor(type)))
            .ToList();
    }
}
