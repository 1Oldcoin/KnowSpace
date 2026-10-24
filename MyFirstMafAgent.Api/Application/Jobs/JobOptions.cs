namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>
/// 任务调度与任务中心的配置。
///
/// <para>
/// <see cref="MaxConcurrentJobs"/> 直接决定并发度，也直接决定同时占用的数据库连接数——
/// 每个执行槽位是一个独立的 ExecutionContext（见 <c>BackgroundLoop</c>），
/// 因此不要把默认值调得比数据库连接池还大。
/// </para>
/// </summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>是否启用调度器。关闭后任务只入队不执行（用于排查）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>同时执行的任务数，即并发度。</summary>
    public int MaxConcurrentJobs { get; set; } = 4;

    /// <summary>空闲轮询间隔；有任务完成时会被立即唤醒，不必等满此间隔。</summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>租约秒数。执行者崩溃后超过该时间未续租，任务会被放回队列。</summary>
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>默认最大尝试次数（含首次）。</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>重试退避基数秒数（指数增长）。</summary>
    public int RetryBaseSeconds { get; set; } = 10;

    /// <summary>退避上限秒数。</summary>
    public int RetryMaxSeconds { get; set; } = 600;

    /// <summary>已结束任务的保留小时数，超过后由清理任务删除。</summary>
    public int RetentionHours { get; set; } = 168;

    /// <summary>排队任务上限，防止异常循环把库撑爆。</summary>
    public int MaxQueuedJobs { get; set; } = 5000;

    /// <summary>补投（feeder）最小间隔秒数，避免每个轮询周期都扫业务表。</summary>
    public int FeederIntervalSeconds { get; set; } = 2;

    /// <summary>清理任务的执行间隔分钟数。</summary>
    public int PurgeIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// 按任务类型的并发上限，例如 <c>{ "plugin.sync": 2 }</c>。
    ///
    /// <para>
    /// 不配或配 0 表示不单独限制（只受 <see cref="MaxConcurrentJobs"/> 约束）。
    /// 作用见 <see cref="JobTypeQuota"/>：给某一类任务留出保底槽位，
    /// 避免它占满所有槽位后高优先级任务只能干等。修改后需重启 API。
    /// </para>
    /// </summary>
    public Dictionary<string, int> TypeLimits { get; set; } = new(StringComparer.Ordinal);
}
