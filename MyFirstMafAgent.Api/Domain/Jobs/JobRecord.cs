namespace MyFirstMafAgent.Api.Domain.Jobs;

/// <summary>
/// 一条后台任务。
///
/// <para>
/// 这是「长任务」的唯一真实来源：进度、重试次数、取消请求、租约都写在库里，
/// 进程重启后任务不会凭空消失——这也是它与「内存里排个队」的根本差别。
/// </para>
/// </summary>
public sealed record JobRecord(
    string Id,
    string Type,
    string Status,
    int Priority,
    int Attempts,
    int MaxAttempts,
    string? Payload,
    string? Result,
    int Progress,
    string? ProgressMessage,
    string? LastError,
    string? WorkerId,
    string? IdempotencyKey,
    Guid? KnowledgeBaseId,
    Guid? SubjectId,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset NextRunAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LeaseUntil)
{
    public bool IsTerminal => JobStatuses.IsTerminal(Status);

    /// <summary>租约是否已过期（执行者可能已经崩溃）。</summary>
    public bool IsLeaseExpired(DateTimeOffset now) =>
        Status == JobStatuses.Running && LeaseUntil is { } lease && lease <= now;
}

/// <summary>各状态的任务数量，供界面显示「还有多少在跑」。</summary>
public sealed record JobCounts(
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    int Cancelled,
    DateTimeOffset? OldestQueuedAt)
{
    public int Pending => Queued + Running;
}

/// <summary>列表查询条件。</summary>
public sealed record JobQuery(
    string? Status = null,
    string? Type = null,
    Guid? SubjectId = null,
    int Limit = 50);

/// <summary>新建任务的命令。<see cref="IdempotencyKey"/> 相同则不会重复创建。</summary>
public sealed record EnqueueJobCommand(
    string Type,
    string? Payload = null,
    int Priority = JobPriorities.Normal,
    Guid? KnowledgeBaseId = null,
    Guid? SubjectId = null,
    string? IdempotencyKey = null,
    DateTimeOffset? NextRunAt = null,
    int? MaxAttempts = null);

/// <summary>领取到的任务租约。</summary>
public sealed record JobLease(JobRecord Job, string WorkerId);

/// <summary>失败信息：<c>Cancelled</c> 表示用户取消，<c>Fatal</c> 表示不该重试。</summary>
public sealed record JobFailure(string Message, bool Cancelled = false, bool Fatal = false)
{
    public static JobFailure FromCancel() => new("任务已取消。", Cancelled: true, Fatal: true);

    public static JobFailure FatalError(string message) => new(message, Cancelled: false, Fatal: true);
}

/// <summary>任务状态变化的推送事件（SSE）。</summary>
public sealed record JobEvent(
    string Kind,
    string JobId,
    string Type,
    string Status,
    int Progress,
    string? Message,
    string? Error,
    DateTimeOffset OccurredAt)
{
    public static JobEvent From(JobRecord job) => new(
        "job",
        job.Id,
        job.Type,
        job.Status,
        job.Progress,
        job.ProgressMessage,
        job.LastError,
        DateTimeOffset.UtcNow);
}
