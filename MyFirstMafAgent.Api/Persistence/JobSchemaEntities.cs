using SqlSugar;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 后台任务表。
///
/// <para>
/// 之所以落库而不是放内存队列：长任务动辄几分钟，进程一旦重启（更新、崩溃、用户关窗口）
/// 内存队列里的任务就彻底消失，用户只会看到"资料一直排队中"。
/// 落库 + 租约 + 补投，才能保证"排队中的活一定会被干完"。
/// </para>
/// </summary>
[SugarTable("jobs")]
[SugarIndex("ix_jobs_status_next_run", nameof(Status), OrderByType.Asc, nameof(NextRunAt), OrderByType.Asc, false)]
[SugarIndex("ix_jobs_type_status", nameof(Type), OrderByType.Asc, nameof(Status), OrderByType.Asc, false)]
[SugarIndex("ix_jobs_created_at", nameof(CreatedAt), OrderByType.Desc, false)]
[SugarIndex("ux_jobs_idempotency_key", nameof(IdempotencyKey), OrderByType.Asc, true)]
public sealed class JobSchemaEntity
{
    [SugarColumn(ColumnName = "id", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Id { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "type", ColumnDataType = "text", IsNullable = false)]
    public string Type { get; set; } = string.Empty;

    /// <summary><c>Queued</c> / <c>Running</c> / <c>Succeeded</c> / <c>Failed</c> / <c>Cancelled</c>。</summary>
    [SugarColumn(ColumnName = "status", ColumnDataType = "text", IsNullable = false)]
    public string Status { get; set; } = string.Empty;

    /// <summary>数值越小越先执行。</summary>
    [SugarColumn(ColumnName = "priority", ColumnDataType = "integer", IsNullable = false, DefaultValue = "10")]
    public int Priority { get; set; }

    /// <summary>已尝试次数（含首次）。领取时自增。</summary>
    [SugarColumn(ColumnName = "attempts", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Attempts { get; set; }

    [SugarColumn(ColumnName = "max_attempts", ColumnDataType = "integer", IsNullable = false, DefaultValue = "3")]
    public int MaxAttempts { get; set; }

    /// <summary>任务参数（JSON 文本）。</summary>
    [SugarColumn(ColumnName = "payload", ColumnDataType = "text", IsNullable = true)]
    public string? Payload { get; set; }

    /// <summary>成功时的结果摘要（JSON 或纯文本）。</summary>
    [SugarColumn(ColumnName = "result", ColumnDataType = "text", IsNullable = true)]
    public string? Result { get; set; }

    [SugarColumn(ColumnName = "progress", ColumnDataType = "integer", IsNullable = false, DefaultValue = "0")]
    public int Progress { get; set; }

    [SugarColumn(ColumnName = "progress_message", ColumnDataType = "text", IsNullable = true)]
    public string? ProgressMessage { get; set; }

    [SugarColumn(ColumnName = "last_error", ColumnDataType = "text", IsNullable = true)]
    public string? LastError { get; set; }

    /// <summary>当前（或最后一个）执行者的标识。</summary>
    [SugarColumn(ColumnName = "worker_id", ColumnDataType = "text", IsNullable = true)]
    public string? WorkerId { get; set; }

    /// <summary>幂等键：同键只允许存在一条任务，从根上消除重复投递。</summary>
    [SugarColumn(ColumnName = "idempotency_key", ColumnDataType = "text", IsNullable = true)]
    public string? IdempotencyKey { get; set; }

    [SugarColumn(ColumnName = "knowledge_base_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? KnowledgeBaseId { get; set; }

    /// <summary>任务主体 ID（例如资料 ID），便于按主体过滤与追溯。</summary>
    [SugarColumn(ColumnName = "subject_id", ColumnDataType = "uuid", IsNullable = true)]
    public Guid? SubjectId { get; set; }

    /// <summary>用户请求取消；由执行者在心跳时读取并真正中断。</summary>
    [SugarColumn(ColumnName = "cancel_requested", ColumnDataType = "boolean", IsNullable = false, DefaultValue = "false")]
    public bool CancelRequested { get; set; }

    [SugarColumn(ColumnName = "created_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset CreatedAt { get; set; }

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>最早可执行时间；退避重试就是把它往后推。</summary>
    [SugarColumn(ColumnName = "next_run_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset NextRunAt { get; set; }

    [SugarColumn(ColumnName = "started_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? StartedAt { get; set; }

    [SugarColumn(ColumnName = "completed_at", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>租约到期时间；执行者崩溃后据此回收任务。</summary>
    [SugarColumn(ColumnName = "lease_until", ColumnDataType = "timestamptz", IsNullable = true)]
    public DateTimeOffset? LeaseUntil { get; set; }
}
