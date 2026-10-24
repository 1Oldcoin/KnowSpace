using MyFirstMafAgent.Api.Domain.Jobs;

namespace MyFirstMafAgent.Api.Application.Jobs;

/// <summary>
/// 任务队列端口。所有状态迁移都必须经过这里，端点与调度器不直接写表。
///
/// <para>
/// 领取语义是「带租约的乐观 CAS」：先取一批候选，再逐个用
/// <c>WHERE id = ? AND status = 'Queued'</c> 抢；抢不到就换下一个。
/// 这样多进程/多协程并存时不会重复执行，也不需要数据库特有的
/// <c>SKIP LOCKED</c>（本项目约定业务 SQL 只用 SqlSugar 表达式）。
/// </para>
/// </summary>
public interface IJobQueue
{
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>入队；幂等键已存在时返回已有任务（不重复创建）。</summary>
    Task<JobRecord> EnqueueAsync(EnqueueJobCommand command, CancellationToken cancellationToken);

    /// <summary>领取一个任务；没有可执行任务时返回 null。</summary>
    Task<JobLease?> ClaimAsync(
        string workerId,
        IReadOnlyList<string> types,
        TimeSpan lease,
        CancellationToken cancellationToken);

    /// <summary>续租并返回「是否已被请求取消」。</summary>
    Task<bool> HeartbeatAsync(
        string jobId,
        string workerId,
        TimeSpan lease,
        CancellationToken cancellationToken);

    Task ReportProgressAsync(
        string jobId,
        string workerId,
        int progress,
        string? message,
        CancellationToken cancellationToken);

    Task CompleteAsync(string jobId, string workerId, string? result, CancellationToken cancellationToken);

    /// <summary>记录失败：可重试则回到队列并退避，否则置为 Failed/Cancelled。</summary>
    Task FailAsync(string jobId, string workerId, JobFailure failure, CancellationToken cancellationToken);

    Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);

    Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken cancellationToken);

    Task<JobCounts> CountAsync(CancellationToken cancellationToken);

    Task<bool> RequestCancelAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>把已结束的任务重置回队列，用于手动重跑。</summary>
    Task<JobRecord?> RetryAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>把租约过期（执行者崩溃）的任务放回队列；返回受影响条数。</summary>
    Task<int> RecoverStaleAsync(TimeSpan grace, CancellationToken cancellationToken);

    /// <summary>清理早于指定时间的已结束任务；返回删除条数。</summary>
    Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// 任务来源：在调度循环里「补投」任务的钩子。
///
/// <para>
/// 资料解析这类任务的真实状态在业务表里（<c>kb_documents.status = Queued</c>），
/// 所以任务不是上传接口写出来的，而是从业务状态<b>推导</b>出来的。
/// 好处是双写不可能不一致：漏了任务，下一轮补投自动补上；重复投递被幂等键挡住。
/// </para>
/// </summary>
public interface IJobFeeder
{
    /// <summary>补投任务；返回本次新建的数量。</summary>
    Task<int> FeedAsync(CancellationToken cancellationToken);
}
