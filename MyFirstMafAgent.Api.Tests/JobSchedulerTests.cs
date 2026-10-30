using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Domain.Jobs;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>重试与退避策略：纯函数，必须能穷举。</summary>
public sealed class JobRetryPolicyTests
{
    [Theory]
    [InlineData(1, 10, 600, 10)]
    [InlineData(2, 10, 600, 20)]
    [InlineData(3, 10, 600, 40)]
    [InlineData(4, 10, 600, 80)]
    [InlineData(0, 10, 600, 0)]
    public void Backoff_grows_exponentially(int attempts, int baseSeconds, int maxSeconds, int expected)
    {
        Assert.Equal(expected, JobRetryPolicy.BackoffSeconds(attempts, baseSeconds, maxSeconds));
    }

    [Fact]
    public void Backoff_is_capped_and_never_overflows()
    {
        Assert.Equal(600, JobRetryPolicy.BackoffSeconds(50, 10, 600));

        // 极大尝试次数也不能算出负数或溢出。
        var huge = JobRetryPolicy.BackoffSeconds(int.MaxValue, 10, 600);
        Assert.Equal(600, huge);
    }

    [Fact]
    public void Backoff_never_drops_below_the_base()
    {
        Assert.Equal(5, JobRetryPolicy.BackoffSeconds(1, 5, 1));
    }

    [Theory]
    [InlineData(1, 3, false, true)]   // 还有两次机会
    [InlineData(2, 3, false, true)]   // 最后一次机会
    [InlineData(3, 3, false, false)]  // 用尽
    [InlineData(1, 1, false, false)]  // 只允许一次
    [InlineData(1, 3, true, false)]   // 致命错误不重试
    public void ShouldRetry_respects_attempts_and_fatal_flag(int attempts, int maxAttempts, bool fatal, bool expected)
    {
        Assert.Equal(expected, JobRetryPolicy.ShouldRetry(attempts, maxAttempts, fatal));
    }

    [Theory]
    [InlineData(JobStatuses.Queued, true)]
    [InlineData(JobStatuses.Running, true)]
    [InlineData(JobStatuses.Succeeded, false)]
    [InlineData(JobStatuses.Failed, false)]
    [InlineData(JobStatuses.Cancelled, false)]
    public void Only_unfinished_jobs_can_be_cancelled(string status, bool expected)
    {
        Assert.Equal(expected, JobTransitions.CanCancel(status));
    }

    [Theory]
    [InlineData(JobStatuses.Queued, false)]
    [InlineData(JobStatuses.Running, false)]
    [InlineData(JobStatuses.Succeeded, true)]
    [InlineData(JobStatuses.Failed, true)]
    [InlineData(JobStatuses.Cancelled, true)]
    public void Only_finished_jobs_can_be_retried(string status, bool expected)
    {
        Assert.Equal(expected, JobTransitions.CanRetry(status));
    }

    [Fact]
    public void Lease_expiry_is_reported_only_for_running_jobs()
    {
        var now = DateTimeOffset.UtcNow;
        var running = Sample(status: JobStatuses.Running, leaseUntil: now.AddSeconds(-1));
        var alive = Sample(status: JobStatuses.Running, leaseUntil: now.AddMinutes(1));
        var queued = Sample(status: JobStatuses.Queued, leaseUntil: null);

        Assert.True(running.IsLeaseExpired(now));
        Assert.False(alive.IsLeaseExpired(now));
        Assert.False(queued.IsLeaseExpired(now));
    }

    internal static JobRecord Sample(
        string id = "job_test",
        string type = "test.echo",
        string status = JobStatuses.Queued,
        DateTimeOffset? leaseUntil = null,
        int attempts = 0,
        int maxAttempts = 3) =>
        new(id, type, status, JobPriorities.Normal, attempts, maxAttempts, null, null, 0, null, null,
            null, null, null, null, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, leaseUntil);
}

/// <summary>事件流与唤醒信号。</summary>
public sealed class JobEventStreamTests
{
    [Fact]
    public async Task Subscribers_receive_published_events()
    {
        var stream = new JobEventStream();
        var subscription = stream.Subscribe();

        stream.Publish(new JobEvent("job", "job_1", "test.echo", JobStatuses.Running, 10, "开始", null, DateTimeOffset.UtcNow));

        var received = await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("job_1", received.JobId);
        Assert.Equal(10, received.Progress);
    }

    [Fact]
    public async Task Slow_subscribers_drop_old_events_instead_of_blocking_publishers()
    {
        var stream = new JobEventStream();
        var subscription = stream.Subscribe();

        // 远超通道容量：发布方不能被阻塞，慢客户端只保留最新的那批。
        for (var i = 0; i < 500; i++)
        {
            stream.Publish(new JobEvent("progress", "job_1", "test.echo", JobStatuses.Running, i % 101, null, null, DateTimeOffset.UtcNow));
        }

        var read = 0;
        while (subscription.Reader.TryRead(out _)) read++;

        Assert.True(read is > 0 and <= 64, $"expected a bounded backlog, got {read}");
    }

    [Fact]
    public async Task Unsubscribed_clients_stop_receiving()
    {
        var stream = new JobEventStream();
        var subscription = stream.Subscribe();
        stream.Unsubscribe(subscription.Id);

        stream.Publish(new JobEvent("job", "job_1", "test.echo", JobStatuses.Running, 0, null, null, DateTimeOffset.UtcNow));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await subscription.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Wake_signal_wakes_as_many_waiters_as_notified()
    {
        var signal = new JobWakeSignal();
        signal.Notify(3);

        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.True(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        // 第四次没有信号，只能超时。
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}

/// <summary>
/// 调度器行为：并发上限、失败分类、取消、停机。
///
/// <para>
/// 这里用内存队列替身验证「派活」逻辑；真实 SQL 队列的领取与租约由端到端脚本验证。
/// </para>
/// </summary>
public sealed class JobSchedulerTests
{
    private const string Echo = "test.echo";

    [Fact]
    public async Task Jobs_run_in_parallel_but_never_above_the_lane_count()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { Delay = TimeSpan.FromMilliseconds(150) };

        await using var harness = new SchedulerHarness(queue, handler, lanes: 2);

        for (var i = 0; i < 6; i++) queue.EnqueueDirectly(Echo, $"p{i}");

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Succeeded) == 6, TimeSpan.FromSeconds(10));

        Assert.Equal(6, queue.CountOf(JobStatuses.Succeeded));
        Assert.InRange(handler.PeakConcurrency, 2, 2);   // 既用满并发，也不越过上限
    }

    [Fact]
    public async Task Retryable_failure_requeues_then_eventually_fails()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { FailWith = () => new JobExecutionException("下游暂时不可用") };

        await using var harness = new SchedulerHarness(queue, handler, lanes: 1, maxAttempts: 2, retryBaseSeconds: 1);
        queue.EnqueueDirectly(Echo, "boom", maxAttempts: 2);

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Failed) == 1, TimeSpan.FromSeconds(10));

        var job = queue.Single();
        Assert.Equal(JobStatuses.Failed, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Contains("下游暂时不可用", job.LastError);
    }

    [Fact]
    public async Task Fatal_failure_is_not_retried()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { FailWith = () => JobExecutionException.NotRetryable("参数错误") };

        await using var harness = new SchedulerHarness(queue, handler, lanes: 1, maxAttempts: 5);
        queue.EnqueueDirectly(Echo, "fatal", maxAttempts: 5);

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Failed) == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(1, queue.Single().Attempts);
    }

    [Fact]
    public async Task Cancellation_request_interrupts_a_running_job()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { IgnoreCancellation = false, Delay = TimeSpan.FromSeconds(30) };

        // 心跳周期 = 租约/3，所以租约给 6 秒心跳就是 2 秒一次，取消才能及时生效。
        await using var harness = new SchedulerHarness(queue, handler, lanes: 1, leaseSeconds: 6);
        queue.EnqueueDirectly(Echo, "long");

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Running) == 1, TimeSpan.FromSeconds(10));

        await queue.RequestCancelAsync(queue.Single().Id, CancellationToken.None);
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Cancelled) == 1, TimeSpan.FromSeconds(10));

        Assert.Equal(JobStatuses.Cancelled, queue.Single().Status);
    }

    [Fact]
    public async Task Jobs_without_a_handler_are_failed_at_startup()
    {
        var queue = new FakeJobQueue();
        queue.EnqueueDirectly("test.removed", "ghost");

        await using var harness = new SchedulerHarness(queue, new RecordingHandler(), lanes: 1);

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Failed) == 1, TimeSpan.FromSeconds(10));

        Assert.Contains("没有为任务类型", queue.Single().LastError);
    }

    [Fact]
    public async Task Shutdown_leaves_the_job_running_for_recovery()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { Delay = TimeSpan.FromSeconds(30) };

        var harness = new SchedulerHarness(queue, handler, lanes: 1);
        queue.EnqueueDirectly(Echo, "interrupted");

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Running) == 1, TimeSpan.FromSeconds(10));

        await harness.StopAsync();
        await harness.DisposeAsync();

        // 停机不写 Failed：任务仍是 Running，等租约过期后被回收重跑。
        Assert.Equal(JobStatuses.Running, queue.Single().Status);
    }

    [Fact]
    public async Task Feeder_count_wakes_idle_lanes()
    {
        var queue = new FakeJobQueue();
        var handler = new RecordingHandler { Delay = TimeSpan.FromMilliseconds(20) };
        var feeder = new CountingFeeder(queue, Echo, jobsToCreate: 3);

        await using var harness = new SchedulerHarness(queue, handler, lanes: 2, feeders: [feeder]);

        await harness.StartAsync();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Succeeded) == 3, TimeSpan.FromSeconds(10));

        Assert.Equal(3, queue.CountOf(JobStatuses.Succeeded));
    }

    [Fact]
    public async Task Type_limit_blocks_only_its_own_type()
    {
        const string Heavy = "test.heavy";

        var queue = new FakeJobQueue();
        var meter = new ConcurrencyMeter();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heavy = new MeteredHandler(Heavy, TimeSpan.Zero, meter, gate.Task);
        var light = new MeteredHandler(Echo, TimeSpan.FromMilliseconds(10), meter);

        // 三个槽位，但 test.heavy 只允许 1 个。
        await using var harness = new SchedulerHarness(
            queue, heavy, lanes: 3, additionalHandler: light,
            typeLimits: new Dictionary<string, int>(StringComparer.Ordinal) { [Heavy] = 1 });

        // 第一个 heavy 会卡在 gate 上不放；第二个 heavy 只能排队。
        queue.EnqueueDirectly(Heavy, "blocked-1");
        queue.EnqueueDirectly(Heavy, "blocked-2");
        for (var i = 0; i < 3; i++) queue.EnqueueDirectly(Echo, $"light{i}");

        await harness.StartAsync();

        // heavy 占着它唯一的槽位不动，echo 必须照跑完：证明限流不会连带卡死其它类型。
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Succeeded) == 3, TimeSpan.FromSeconds(15));
        Assert.Equal(1, harness.Quota.RunningFor(Heavy));
        Assert.Equal(1, meter.PeakFor(Heavy));

        // 放开第二个任务：两个 heavy 必须串行执行，峰值仍为 1。
        gate.SetResult();
        await harness.WaitUntilAsync(() => queue.CountOf(JobStatuses.Succeeded) == 5, TimeSpan.FromSeconds(15));

        Assert.Equal(5, queue.CountOf(JobStatuses.Succeeded));
        Assert.Equal(1, meter.PeakFor(Heavy));          // 硬上限，从未并发
        Assert.Equal(0, harness.Quota.RunningFor(Heavy)); // 结束后名额已归还
        Assert.Equal(0, harness.Quota.RunningFor(Echo));
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private sealed class RecordingHandler : IJobHandler
    {
        private int _concurrency;

        public string Type => Echo;

        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        public bool IgnoreCancellation { get; init; }

        public Func<Exception>? FailWith { get; init; }

        public int PeakConcurrency { get; private set; }

        public Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _concurrency);
            lock (this)
            {
                if (current > PeakConcurrency) PeakConcurrency = current;
            }

            return RunAsync(context, cancellationToken);
        }

        private async Task<string?> RunAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    if (IgnoreCancellation) await Task.Delay(Delay);
                    else await Task.Delay(Delay, cancellationToken);
                }

                if (FailWith is not null) throw FailWith();

                await context.ReportProgressAsync(100, "完成", cancellationToken);
                return "ok";
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }
    }

    /// <summary>记录每个类型的历史峰值并发。</summary>
    private sealed class ConcurrencyMeter
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _current = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _peak = new(StringComparer.Ordinal);

        public void Enter(string type)
        {
            lock (_gate)
            {
                var now = _current.GetValueOrDefault(type) + 1;
                _current[type] = now;
                if (now > _peak.GetValueOrDefault(type)) _peak[type] = now;
            }
        }

        public void Exit(string type)
        {
            lock (_gate)
            {
                _current[type] = Math.Max(0, _current.GetValueOrDefault(type) - 1);
            }
        }

        public int PeakFor(string type)
        {
            lock (_gate) return _peak.GetValueOrDefault(type);
        }
    }

    private sealed class MeteredHandler(string type, TimeSpan delay, ConcurrencyMeter meter, Task? gate = null) : IJobHandler
    {
        public string Type => type;

        public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
        {
            meter.Enter(type);
            try
            {
                // 卡在 gate 上：让测试能在“槽位被占满”那一刻断言别的类型还能不能跑。
                if (gate is not null) await gate;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);

                await context.ReportProgressAsync(100, "完成", cancellationToken);
                return "ok";
            }
            finally
            {
                meter.Exit(type);
            }
        }
    }

    private sealed class CountingFeeder(FakeJobQueue queue, string type, int jobsToCreate) : IJobFeeder
    {
        private int _created;

        public Task<int> FeedAsync(CancellationToken cancellationToken)
        {
            // 两个槽位会并发调用补投：先自增再判定额，才能保证总数恰好是 jobsToCreate。
            if (Interlocked.Increment(ref _created) > jobsToCreate) return Task.FromResult(0);

            queue.EnqueueDirectly(type, "feeder");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// 连接归还替身：调度器每轮结束都会调用它，测试里没有真实连接池，直接空转。
    /// </summary>
    private sealed class NoOpConnectionReleaser : IConnectionReleaser
    {
        public int ReleaseCount { get; private set; }

        public void Release() => ReleaseCount++;
    }

    /// <summary>
    /// 内存队列替身：语义与 SqlSugar 实现保持一致（CAS 领取、租约、重试策略）。
    /// </summary>
    private sealed class FakeJobQueue : IJobQueue
    {
        private readonly object _gate = new();
        private readonly List<JobRecord> _jobs = [];
        private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);
        private int _sequence;

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void EnqueueDirectly(string type, string payload, int maxAttempts = 3)
        {
            lock (_gate)
            {
                _jobs.Add(JobRetryPolicyTests.Sample(
                    id: $"job_{++_sequence}",
                    type: type,
                    maxAttempts: maxAttempts) with { Payload = payload });
            }
        }

        public Task<JobRecord> EnqueueAsync(EnqueueJobCommand command, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (command.IdempotencyKey is { } key && _idempotency.TryGetValue(key, out var existingId))
                    return Task.FromResult(_jobs.First(job => job.Id == existingId));

                var record = JobRetryPolicyTests.Sample(
                    id: $"job_{++_sequence}",
                    type: command.Type,
                    maxAttempts: command.MaxAttempts ?? 3) with
                {
                    Payload = command.Payload,
                    Priority = command.Priority
                };

                _jobs.Add(record);
                if (command.IdempotencyKey is { } idempotencyKey) _idempotency[idempotencyKey] = record.Id;
                return Task.FromResult(record);
            }
        }

        public Task<JobLease?> ClaimAsync(
            string workerId,
            IReadOnlyList<string> types,
            TimeSpan lease,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var candidate = _jobs
                    .Where(job => job.Status == JobStatuses.Queued
                        && job.NextRunAt <= now
                        && types.Contains(job.Type, StringComparer.Ordinal))
                    .OrderBy(job => job.Priority)
                    .ThenBy(job => job.NextRunAt)
                    .ThenBy(job => job.CreatedAt)
                    .FirstOrDefault();

                if (candidate is null) return Task.FromResult<JobLease?>(null);

                var claimed = candidate with
                {
                    Status = JobStatuses.Running,
                    WorkerId = workerId,
                    LeaseUntil = now.Add(lease),
                    Attempts = candidate.Attempts + 1,
                    StartedAt = candidate.StartedAt ?? now,
                    ProgressMessage = "开始执行"
                };

                Replace(claimed);
                return Task.FromResult<JobLease?>(new JobLease(claimed, workerId));
            }
        }

        public Task<bool> HeartbeatAsync(string jobId, string workerId, TimeSpan lease, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null || job.Status != JobStatuses.Running || job.WorkerId != workerId)
                    return Task.FromResult(false);

                Replace(job with { LeaseUntil = DateTimeOffset.UtcNow.Add(lease) });
                return Task.FromResult(job.CancelRequested);
            }
        }

        public Task ReportProgressAsync(
            string jobId,
            string workerId,
            int progress,
            string? message,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null || job.Status != JobStatuses.Running) return Task.CompletedTask;

                Replace(job with
                {
                    Progress = Math.Clamp(progress, 0, 100),
                    ProgressMessage = message,
                    LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(120)
                });
                return Task.CompletedTask;
            }
        }

        public Task CompleteAsync(string jobId, string workerId, string? result, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null || job.Status != JobStatuses.Running || job.WorkerId != workerId)
                    return Task.CompletedTask;

                Replace(job with
                {
                    Status = JobStatuses.Succeeded,
                    Progress = 100,
                    Result = result,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LeaseUntil = null
                });
                return Task.CompletedTask;
            }
        }

        public Task FailAsync(string jobId, string workerId, JobFailure failure, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null) return Task.CompletedTask;
                if (job.Status == JobStatuses.Running && job.WorkerId is not null && job.WorkerId != workerId)
                    return Task.CompletedTask;

                if (failure.Cancelled)
                {
                    Replace(job with
                    {
                        Status = JobStatuses.Cancelled,
                        LastError = failure.Message,
                        CompletedAt = DateTimeOffset.UtcNow,
                        LeaseUntil = null
                    });
                    return Task.CompletedTask;
                }

                if (JobRetryPolicy.ShouldRetry(job.Attempts, job.MaxAttempts, failure.Fatal))
                {
                    Replace(job with
                    {
                        Status = JobStatuses.Queued,
                        LastError = failure.Message,
                        NextRunAt = DateTimeOffset.UtcNow.AddSeconds(
                            JobRetryPolicy.BackoffSeconds(job.Attempts, 1, 5)),
                        LeaseUntil = null,
                        WorkerId = null
                    });
                    return Task.CompletedTask;
                }

                Replace(job with
                {
                    Status = JobStatuses.Failed,
                    LastError = failure.Message,
                    CompletedAt = DateTimeOffset.UtcNow,
                    LeaseUntil = null,
                    WorkerId = null
                });
                return Task.CompletedTask;
            }
        }

        public Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
        {
            lock (_gate) return Task.FromResult(Find(jobId));
        }

        public Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var rows = _jobs.AsEnumerable();
                if (query.Status is not null) rows = rows.Where(job => job.Status == query.Status);
                if (query.Type is not null) rows = rows.Where(job => job.Type == query.Type);

                return Task.FromResult<IReadOnlyList<JobRecord>>(rows.Take(query.Limit).ToList());
            }
        }

        public Task<JobCounts> CountAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(new JobCounts(
                    CountOf(JobStatuses.Queued),
                    CountOf(JobStatuses.Running),
                    CountOf(JobStatuses.Succeeded),
                    CountOf(JobStatuses.Failed),
                    CountOf(JobStatuses.Cancelled),
                    null));
            }
        }

        public Task<bool> RequestCancelAsync(string jobId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null || !JobTransitions.CanCancel(job.Status)) return Task.FromResult(false);

                Replace(job with
                {
                    CancelRequested = true,
                    Status = job.Status == JobStatuses.Queued ? JobStatuses.Cancelled : job.Status,
                    CompletedAt = job.Status == JobStatuses.Queued ? DateTimeOffset.UtcNow : job.CompletedAt
                });
                return Task.FromResult(true);
            }
        }

        public Task<JobRecord?> RetryAsync(string jobId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var job = Find(jobId);
                if (job is null || !JobTransitions.CanRetry(job.Status)) return Task.FromResult<JobRecord?>(null);

                var retried = job with
                {
                    Status = JobStatuses.Queued,
                    Attempts = 0,
                    Progress = 0,
                    LastError = null,
                    CancelRequested = false,
                    NextRunAt = DateTimeOffset.UtcNow,
                    WorkerId = null,
                    LeaseUntil = null,
                    CompletedAt = null
                };

                Replace(retried);
                return Task.FromResult<JobRecord?>(retried);
            }
        }

        public Task<int> RecoverStaleAsync(TimeSpan grace, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var stale = _jobs.Where(job => job.IsLeaseExpired(now.Subtract(grace))).ToList();

                foreach (var job in stale)
                {
                    Replace(job with
                    {
                        Status = JobStatuses.Queued,
                        WorkerId = null,
                        LeaseUntil = null,
                        NextRunAt = now
                    });
                }

                return Task.FromResult(stale.Count);
            }
        }

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var removed = _jobs.RemoveAll(job =>
                    job.CompletedAt is { } completed && completed <= completedBefore && job.IsTerminal);
                return Task.FromResult(removed);
            }
        }

        public int CountOf(string status)
        {
            lock (_gate) return _jobs.Count(job => job.Status == status);
        }

        public JobRecord Single()
        {
            lock (_gate) return _jobs.Single();
        }

        private JobRecord? Find(string jobId) => _jobs.FirstOrDefault(job => job.Id == jobId);

        private void Replace(JobRecord job)
        {
            var index = _jobs.FindIndex(existing => existing.Id == job.Id);
            if (index >= 0) _jobs[index] = job;
        }
    }

    /// <summary>用真实 DI + 真实调度器跑内存队列，避免测到"替身的调度器"。</summary>
    private sealed class SchedulerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly JobScheduler _scheduler;

        public SchedulerHarness(
            FakeJobQueue queue,
            IJobHandler handler,
            int lanes,
            int maxAttempts = 3,
            int retryBaseSeconds = 1,
            int leaseSeconds = 30,
            IReadOnlyList<IJobFeeder>? feeders = null,
            Dictionary<string, int>? typeLimits = null,
            IJobHandler? additionalHandler = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IJobQueue>(queue);
            services.AddSingleton<IJobHandler>(handler);
            if (additionalHandler is not null) services.AddSingleton<IJobHandler>(additionalHandler);
            services.AddSingleton<JobEventStream>();
            services.AddSingleton<JobWakeSignal>();
            services.AddSingleton<JobHandlerRegistry>();
            services.AddSingleton<JobTypeQuota>();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new JobOptions
            {
                Enabled = true,
                MaxConcurrentJobs = lanes,
                PollIntervalMs = 200,
                LeaseSeconds = leaseSeconds,
                MaxAttempts = maxAttempts,
                RetryBaseSeconds = retryBaseSeconds,
                RetryMaxSeconds = 2,
                FeederIntervalSeconds = 0,
                PurgeIntervalMinutes = 60,
                RetentionHours = 24,
                MaxQueuedJobs = 1000,
                TypeLimits = typeLimits is null
                    ? new Dictionary<string, int>(StringComparer.Ordinal)
                    : new Dictionary<string, int>(typeLimits, StringComparer.Ordinal)
            }));

            _provider = services.BuildServiceProvider();
            Quota = _provider.GetRequiredService<JobTypeQuota>();
            _scheduler = new JobScheduler(
                queue,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                _provider.GetRequiredService<JobHandlerRegistry>(),
                feeders ?? [],
                _provider.GetRequiredService<JobWakeSignal>(),
                Quota,
                _provider.GetRequiredService<IOptions<JobOptions>>(),
                new NoOpConnectionReleaser(),
                NullLogger<JobScheduler>.Instance);
        }

        public JobTypeQuota Quota { get; }

        public Task StartAsync() => _scheduler.StartAsync(CancellationToken.None);

        public Task StopAsync() => _scheduler.StopAsync(CancellationToken.None);

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition()) return;
                await Task.Delay(25);
            }

            Assert.Fail($"条件在 {timeout.TotalSeconds:0.#} 秒内没有满足。");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _scheduler.StopAsync(CancellationToken.None);
            }
            catch
            {
                // 已停止。
            }

            _scheduler.Dispose();
            await _provider.DisposeAsync();
        }
    }
}
