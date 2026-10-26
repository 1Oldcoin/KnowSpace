using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Domain.Jobs;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Persistence;

/// <summary>
/// 任务队列的 SqlSugar 实现。
///
/// <para>
/// 两个关键点：
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>领取用乐观 CAS</b>：先取一批候选，再逐个 <c>WHERE id = ? AND status = 'Queued'</c> 抢，
///     抢到才返回。多个执行者并发时只有一个人成功，败者换下一个候选，
///     不需要数据库特有的 <c>SKIP LOCKED</c>，也不用写裸 SQL。
///   </description></item>
///   <item><description>
///     <b>失败由策略决定归属</b>：可重试的回 Queued 并退避，不可重试或次数用尽才落 Failed/Cancelled。
///     状态机集中在这里，处理器只负责说"失败了，原因是什么"。
///   </description></item>
/// </list>
/// </para>
/// </summary>
public sealed class SqlSugarJobQueue(
    SqlSugarScope database,
    IOptions<JobOptions> options,
    JobEventStream events,
    ILogger<SqlSugarJobQueue> logger) : IJobQueue
{
    /// <summary>一次领取尝试的候选数量：既避免全表扫描，也不至于频繁扑空。</summary>
    private const int ClaimBatchSize = 20;

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        InitializeAsync(cancellationToken, ensureSchema: true);

    /// <summary>结构指纹一致时跳过建表检查（见 <see cref="Hosting.SchemaStamp"/>）。</summary>
    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<JobSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<JobRecord> EnqueueAsync(EnqueueJobCommand command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey))
        {
            var existing = await FindByIdempotencyKeyAsync(command.IdempotencyKey!, cancellationToken);
            if (existing is not null) return ToRecord(existing);
        }

        var record = new JobSchemaEntity
        {
            Id = "job_" + Guid.NewGuid().ToString("N")[..20],
            Type = command.Type,
            Status = JobStatuses.Queued,
            Priority = command.Priority,
            Attempts = 0,
            MaxAttempts = Math.Max(1, command.MaxAttempts ?? settings.MaxAttempts),
            Payload = command.Payload,
            Progress = 0,
            KnowledgeBaseId = command.KnowledgeBaseId,
            SubjectId = command.SubjectId,
            IdempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey,
            CreatedAt = now,
            UpdatedAt = now,
            NextRunAt = command.NextRunAt ?? now
        };

        try
        {
            await database.Insertable(record).ExecuteCommandAsync(cancellationToken);
        }
        catch (Exception exception) when (record.IdempotencyKey is not null)
        {
            // 并发入队撞上唯一索引：这是"已经有人在跑"，不是错误。
            var existing = await FindByIdempotencyKeyAsync(record.IdempotencyKey!, cancellationToken);
            if (existing is not null) return ToRecord(existing);

            logger.LogWarning(exception, "Failed to enqueue job {Type} with key {Key}", record.Type, record.IdempotencyKey);
            throw;
        }

        var created = ToRecord(record);
        events.Publish(JobEvent.From(created));
        return created;
    }

    public async Task<JobLease?> ClaimAsync(
        string workerId,
        IReadOnlyList<string> types,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        if (types.Count == 0) return null;

        var now = DateTimeOffset.UtcNow;
        var candidates = await database.Queryable<JobSchemaEntity>()
            .Where(job => job.Status == JobStatuses.Queued && job.NextRunAt <= now && types.Contains(job.Type))
            .OrderBy(job => job.Priority, OrderByType.Asc)
            .OrderBy(job => job.NextRunAt, OrderByType.Asc)
            .OrderBy(job => job.CreatedAt, OrderByType.Asc)
            .Take(ClaimBatchSize)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            // 条件更新 = 抢占成功与否的唯一判据；抢不到说明别的执行者更快。
            var affected = await database.Updateable<JobSchemaEntity>()
                .SetColumns(job => new JobSchemaEntity
                {
                    Status = JobStatuses.Running,
                    WorkerId = workerId,
                    LeaseUntil = now.Add(lease),
                    Attempts = job.Attempts + 1,
                    StartedAt = job.StartedAt ?? now,
                    LastError = null,
                    ProgressMessage = "开始执行",
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(job => job.Id == candidate.Id && job.Status == JobStatuses.Queued)
                .ExecuteCommandAsync(cancellationToken);

            if (affected == 0) continue;

            var claimed = await GetEntityAsync(candidate.Id, cancellationToken);
            if (claimed is null) continue;

            var record = ToRecord(claimed);
            events.Publish(JobEvent.From(record));
            return new JobLease(record, workerId);
        }

        return null;
    }

    public async Task<bool> HeartbeatAsync(
        string jobId,
        string workerId,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 只有仍由自己持有的 Running 任务才续租；被回收后不续，避免"复活"。
        var affected = await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                LeaseUntil = now.Add(lease),
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId && job.WorkerId == workerId && job.Status == JobStatuses.Running)
            .ExecuteCommandAsync(cancellationToken);

        if (affected == 0) return false;

        var current = await GetEntityAsync(jobId, cancellationToken);
        return current?.CancelRequested ?? false;
    }

    public async Task ReportProgressAsync(
        string jobId,
        string workerId,
        int progress,
        string? message,
        CancellationToken cancellationToken)
    {
        var clamped = Math.Clamp(progress, 0, 100);

        var affected = await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Progress = clamped,
                ProgressMessage = message,
                LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(options.Value.LeaseSeconds),
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId && job.WorkerId == workerId && job.Status == JobStatuses.Running)
            .ExecuteCommandAsync(cancellationToken);

        // 进度写不进去（已被回收/取消）不抛异常：任务本身还在跑，让它自然结束。
        if (affected == 0) return;

        events.Publish(new JobEvent(
            "progress",
            jobId,
            string.Empty,
            JobStatuses.Running,
            clamped,
            message,
            null,
            DateTimeOffset.UtcNow));
    }

    public async Task CompleteAsync(
        string jobId,
        string workerId,
        string? result,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var affected = await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Status = JobStatuses.Succeeded,
                Progress = 100,
                ProgressMessage = "已完成",
                Result = result,
                LastError = null,
                CompletedAt = now,
                LeaseUntil = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId && job.WorkerId == workerId && job.Status == JobStatuses.Running)
            .ExecuteCommandAsync(cancellationToken);

        if (affected == 0)
        {
            logger.LogWarning("Job {JobId} completed after losing its lease; result discarded", jobId);
            return;
        }

        await PublishCurrentAsync(jobId, cancellationToken);
    }

    public async Task FailAsync(
        string jobId,
        string workerId,
        JobFailure failure,
        CancellationToken cancellationToken)
    {
        var entity = await database.Queryable<JobSchemaEntity>()
            .Where(job => job.Id == jobId)
            .FirstAsync(cancellationToken);
        if (entity is null) return;

        // 已被他人接管（租约被回收）时不再写入，避免两个执行者互相覆盖状态。
        if (entity.Status == JobStatuses.Running
            && entity.WorkerId is not null
            && !string.Equals(entity.WorkerId, workerId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Job {JobId} failure reported by {WorkerId} but owned by {Owner}; ignored",
                jobId, workerId, entity.WorkerId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = Trim(failure.Message);

        if (failure.Cancelled)
        {
            await database.Updateable<JobSchemaEntity>()
                .SetColumns(job => new JobSchemaEntity
                {
                    Status = JobStatuses.Cancelled,
                    ProgressMessage = "已取消",
                    LastError = message,
                    CompletedAt = now,
                    LeaseUntil = null,
                    WorkerId = null,
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(job => job.Id == jobId)
                .ExecuteCommandAsync(cancellationToken);

            await PublishCurrentAsync(jobId, cancellationToken);
            return;
        }

        if (JobRetryPolicy.ShouldRetry(entity.Attempts, entity.MaxAttempts, failure.Fatal))
        {
            var backoff = JobRetryPolicy.BackoffSeconds(
                entity.Attempts,
                options.Value.RetryBaseSeconds,
                options.Value.RetryMaxSeconds);

            await database.Updateable<JobSchemaEntity>()
                .SetColumns(job => new JobSchemaEntity
                {
                    Status = JobStatuses.Queued,
                    ProgressMessage = $"将在 {backoff} 秒后重试（第 {entity.Attempts + 1} 次）",
                    LastError = message,
                    NextRunAt = now.AddSeconds(backoff),
                    LeaseUntil = null,
                    WorkerId = null,
                    UpdatedAt = SqlFunc.GetDate()
                })
                .Where(job => job.Id == jobId)
                .ExecuteCommandAsync(cancellationToken);

            logger.LogInformation(
                "Job {JobId} failed on attempt {Attempts}/{MaxAttempts}; retrying in {Backoff}s: {Message}",
                jobId, entity.Attempts, entity.MaxAttempts, backoff, message);

            await PublishCurrentAsync(jobId, cancellationToken);
            return;
        }

        await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Status = JobStatuses.Failed,
                ProgressMessage = "已失败",
                LastError = message,
                CompletedAt = now,
                LeaseUntil = null,
                WorkerId = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId)
            .ExecuteCommandAsync(cancellationToken);

        logger.LogWarning(
            "Job {JobId} failed permanently after {Attempts} attempt(s): {Message}",
            jobId, entity.Attempts, message);

        await PublishCurrentAsync(jobId, cancellationToken);
    }

    public async Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken) =>
        await GetEntityAsync(jobId, cancellationToken) is { } entity ? ToRecord(entity) : null;

    public async Task<IReadOnlyList<JobRecord>> ListAsync(JobQuery query, CancellationToken cancellationToken)
    {
        var queryable = database.Queryable<JobSchemaEntity>();

        if (!string.IsNullOrWhiteSpace(query.Status))
            queryable = queryable.Where(job => job.Status == query.Status);

        if (!string.IsNullOrWhiteSpace(query.Type))
            queryable = queryable.Where(job => job.Type == query.Type);

        if (query.SubjectId is { } subjectId)
            queryable = queryable.Where(job => job.SubjectId == subjectId);

        var rows = await queryable
            .OrderBy(job => job.CreatedAt, OrderByType.Desc)
            .Take(Math.Clamp(query.Limit, 1, 500))
            .ToListAsync(cancellationToken);

        return rows.Select(ToRecord).ToList();
    }

    public async Task<JobCounts> CountAsync(CancellationToken cancellationToken)
    {
        // 逐个状态计数而不是 group by 投影：索引覆盖下代价很低，
        // 也避开了 SqlSugar 分组投影与自定义类型物化之间的坑。
        async Task<int> CountOf(string status) =>
            await database.Queryable<JobSchemaEntity>()
                .Where(job => job.Status == status)
                .CountAsync(cancellationToken);

        var oldest = await database.Queryable<JobSchemaEntity>()
            .Where(job => job.Status == JobStatuses.Queued)
            .OrderBy(job => job.CreatedAt, OrderByType.Asc)
            .FirstAsync(cancellationToken);

        return new JobCounts(
            await CountOf(JobStatuses.Queued),
            await CountOf(JobStatuses.Running),
            await CountOf(JobStatuses.Succeeded),
            await CountOf(JobStatuses.Failed),
            await CountOf(JobStatuses.Cancelled),
            oldest?.CreatedAt);
    }

    public async Task<bool> RequestCancelAsync(string jobId, CancellationToken cancellationToken)
    {
        var affected = await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                CancelRequested = true,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId && (job.Status == JobStatuses.Queued || job.Status == JobStatuses.Running))
            .ExecuteCommandAsync(cancellationToken);

        if (affected == 0) return false;

        // 排队中的任务没人会去读取消标志，直接落 Cancelled；运行中的交给执行者中断。
        await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Status = JobStatuses.Cancelled,
                ProgressMessage = "已取消",
                LastError = "任务已取消。",
                CompletedAt = DateTimeOffset.UtcNow,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId && job.Status == JobStatuses.Queued)
            .ExecuteCommandAsync(cancellationToken);

        await PublishCurrentAsync(jobId, cancellationToken);
        return true;
    }

    public async Task<JobRecord?> RetryAsync(string jobId, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(jobId, cancellationToken);
        if (entity is null) return null;
        if (!JobTransitions.CanRetry(entity.Status)) return null;

        var now = DateTimeOffset.UtcNow;
        await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Status = JobStatuses.Queued,
                Attempts = 0,
                Progress = 0,
                ProgressMessage = "重新排队",
                LastError = null,
                Result = null,
                CancelRequested = false,
                WorkerId = null,
                LeaseUntil = null,
                NextRunAt = now,
                StartedAt = null,
                CompletedAt = null,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Id == jobId)
            .ExecuteCommandAsync(cancellationToken);

        var updated = await GetAsync(jobId, cancellationToken);
        if (updated is not null) events.Publish(JobEvent.From(updated));
        return updated;
    }

    public async Task<int> RecoverStaleAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(grace);

        // 只回收"租约过期且超过宽限期"的任务：宽限期用来容忍时钟漂移与短暂卡顿。
        var affected = await database.Updateable<JobSchemaEntity>()
            .SetColumns(job => new JobSchemaEntity
            {
                Status = JobStatuses.Queued,
                WorkerId = null,
                LeaseUntil = null,
                NextRunAt = SqlFunc.GetDate(),
                ProgressMessage = "执行者失联，重新排队",
                LastError = "任务租约过期（执行进程可能已退出）。",
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(job => job.Status == JobStatuses.Running && job.LeaseUntil != null && job.LeaseUntil <= cutoff)
            .ExecuteCommandAsync(cancellationToken);

        if (affected > 0)
            logger.LogWarning("Recovered {Count} stale job(s) whose lease expired", affected);

        return affected;
    }

    public async Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken) =>
        await database.Deleteable<JobSchemaEntity>()
            .Where(job => job.CompletedAt != null
                && job.CompletedAt <= completedBefore
                && (job.Status == JobStatuses.Succeeded
                    || job.Status == JobStatuses.Failed
                    || job.Status == JobStatuses.Cancelled))
            .ExecuteCommandAsync(cancellationToken);

    private async Task<JobSchemaEntity?> GetEntityAsync(string jobId, CancellationToken cancellationToken) =>
        await database.Queryable<JobSchemaEntity>()
            .Where(job => job.Id == jobId)
            .FirstAsync(cancellationToken);

    private async Task<JobSchemaEntity?> FindByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        await database.Queryable<JobSchemaEntity>()
            .Where(job => job.IdempotencyKey == key)
            .FirstAsync(cancellationToken);

    private async Task PublishCurrentAsync(string jobId, CancellationToken cancellationToken)
    {
        var entity = await GetEntityAsync(jobId, cancellationToken);
        if (entity is not null) events.Publish(JobEvent.From(ToRecord(entity)));
    }

    private static string Trim(string value) =>
        value.Length <= 2000 ? value : value[..2000];

    private static JobRecord ToRecord(JobSchemaEntity entity) => new(
        entity.Id,
        entity.Type,
        entity.Status,
        entity.Priority,
        entity.Attempts,
        entity.MaxAttempts,
        entity.Payload,
        entity.Result,
        entity.Progress,
        entity.ProgressMessage,
        entity.LastError,
        entity.WorkerId,
        entity.IdempotencyKey,
        entity.KnowledgeBaseId,
        entity.SubjectId,
        entity.CancelRequested,
        entity.CreatedAt,
        entity.UpdatedAt,
        entity.NextRunAt,
        entity.StartedAt,
        entity.CompletedAt,
        entity.LeaseUntil);
}
