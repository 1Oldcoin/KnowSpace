namespace MyFirstMafAgent.Api.Domain.Jobs;

/// <summary>
/// 任务类型常量。
///
/// <para>
/// 类型是「谁来做」的唯一键：调度器只把任务交给声明了同一类型的
/// <c>IJobHandler</c>，因此新增类型必须同时注册处理器，否则任务会被判为不可执行。
/// </para>
/// </summary>
public static class JobTypes
{
    /// <summary>资料解析：把一篇已排队的资料解析、分块、发布内容版本并生成摘要。</summary>
    public const string DocumentIngest = "document.ingest";

    /// <summary>
    /// 插件同步：调用一次平台连接器能力并把结果幂等摄取到知识库。
    /// 手动同步与定时同步共用同一个处理器，因此任务中心能看到每一次同步。
    /// </summary>
    public const string PluginSync = "plugin.sync";
}

/// <summary>任务状态。</summary>
public static class JobStatuses
{
    /// <summary>等待执行（可能带 <c>NextRunAt</c> 退避时间）。</summary>
    public const string Queued = "Queued";

    /// <summary>已被某个执行者领取，持有租约。</summary>
    public const string Running = "Running";

    /// <summary>执行完成。</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>执行失败且不再重试（或错误不可重试）。</summary>
    public const string Failed = "Failed";

    /// <summary>被用户取消。</summary>
    public const string Cancelled = "Cancelled";

    public static bool IsTerminal(string status) =>
        status is Succeeded or Failed or Cancelled;
}

/// <summary>优先级：数值越小越先执行。</summary>
public static class JobPriorities
{
    /// <summary>用户正在等待的任务（例如刚上传的资料）。</summary>
    public const int High = 0;

    public const int Normal = 10;

    /// <summary>后台补数据、重建索引等可延后的任务。</summary>
    public const int Low = 20;
}

/// <summary>
/// 重试与退避策略。做成纯函数是为了能穷举——
/// 「重试次数」与「退避时间」算错，表现为任务抖动或静默丢弃，线上极难发现。
/// </summary>
public static class JobRetryPolicy
{
    /// <summary>
    /// 第 <paramref name="attempts"/> 次失败后的退避秒数（指数增长，封顶）。
    /// attempts=1 → base，2 → 2×base，3 → 4×base …
    /// </summary>
    public static int BackoffSeconds(int attempts, int baseSeconds, int maxSeconds)
    {
        if (attempts <= 0) return 0;

        var baseValue = Math.Max(1, baseSeconds);
        var cap = Math.Max(baseValue, maxSeconds);

        // 用 double 计算再封顶，避免 attempts 很大时 2^n 溢出。
        var seconds = baseValue * Math.Pow(2, Math.Min(attempts - 1, 20));
        return (int)Math.Min(cap, Math.Max(baseValue, seconds));
    }

    /// <summary>本次失败后是否还应该重试。</summary>
    public static bool ShouldRetry(int attempts, int maxAttempts, bool fatal)
    {
        if (fatal) return false;
        return attempts < Math.Max(1, maxAttempts);
    }
}

/// <summary>状态迁移规则（谁可以取消、谁可以重试）。</summary>
public static class JobTransitions
{
    /// <summary>只有排队中或运行中的任务可以取消；已结束的任务取消没有意义。</summary>
    public static bool CanCancel(string status) => status is JobStatuses.Queued or JobStatuses.Running;

    /// <summary>已结束（失败/取消/成功）的任务可以重跑；运行中的不行。</summary>
    public static bool CanRetry(string status) => JobStatuses.IsTerminal(status);
}
