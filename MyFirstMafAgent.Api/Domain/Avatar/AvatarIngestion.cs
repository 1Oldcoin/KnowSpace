namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 一次投喂/纠正任务。投喂入口只负责落原文并创建任务，真正的记忆提取在后台异步完成，
/// 因此长文件不会阻塞问答，也不会因为单次模型失败而丢掉原文。
/// </summary>
public sealed class AvatarIngestion
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Partial = "partial";
    public const string Failed = "failed";

    public const string StageReceived = "received";
    public const string StageParsing = "parsing";
    public const string StageExtracting = "extracting";
    public const string StageCommitting = "committing";
    public const string StageIndexing = "indexing";
    public const string StageCompleted = "completed";
    public const string StageFailed = "failed";

    public const string SourceText = "text";
    public const string SourceFile = "file";
    public const string SourceCorrection = "correction";
    public const string SourceSupplement = "supplement";

    private AvatarIngestion()
    {
    }

    private AvatarIngestion(
        Guid id,
        string sourceType,
        string? sourceRef,
        string? idempotencyKey,
        IReadOnlyList<Guid> fragmentIds,
        IReadOnlyList<Guid> supersedesMemoryIds,
        string status,
        string stage,
        string? error,
        int memoryCount,
        int insightCount,
        string statsJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        SourceType = sourceType;
        SourceRef = sourceRef;
        IdempotencyKey = idempotencyKey;
        FragmentIds = fragmentIds;
        SupersedesMemoryIds = supersedesMemoryIds;
        Status = status;
        Stage = stage;
        Error = error;
        MemoryCount = memoryCount;
        InsightCount = insightCount;
        StatsJson = statsJson;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>text 自然语言 / file 文件 / correction 纠正 / supplement 补充。</summary>
    public string SourceType { get; private set; } = SourceText;

    /// <summary>文件名或用户可读的来源说明。</summary>
    public string? SourceRef { get; private set; }

    /// <summary>幂等键：source_hash + segment_index + extractor_version。</summary>
    public string? IdempotencyKey { get; private set; }

    public IReadOnlyList<Guid> FragmentIds { get; private set; } = [];

    /// <summary>本次纠正/补充要取代的旧记忆。</summary>
    public IReadOnlyList<Guid> SupersedesMemoryIds { get; private set; } = [];

    public string Status { get; private set; } = Queued;

    public string Stage { get; private set; } = StageReceived;

    public string? Error { get; private set; }

    public int MemoryCount { get; private set; }
    public int InsightCount { get; private set; }

    public string StatsJson { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsTerminal => Status is Completed or Partial or Failed;

    public static AvatarIngestion Create(
        string sourceType,
        string? sourceRef,
        string? idempotencyKey,
        IReadOnlyList<Guid> fragmentIds,
        IReadOnlyList<Guid>? supersedesMemoryIds,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            NormalizeSourceType(sourceType),
            TrimOrNull(sourceRef, 400),
            TrimOrNull(idempotencyKey, 400),
            fragmentIds.ToList(),
            (supersedesMemoryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList(),
            Queued,
            StageReceived,
            null,
            0,
            0,
            "{}",
            now,
            now,
            null,
            null);

    public static AvatarIngestion Restore(
        Guid id,
        string sourceType,
        string? sourceRef,
        string? idempotencyKey,
        IReadOnlyList<Guid> fragmentIds,
        IReadOnlyList<Guid> supersedesMemoryIds,
        string status,
        string stage,
        string? error,
        int memoryCount,
        int insightCount,
        string statsJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt) =>
        new(id,
            NormalizeSourceType(sourceType),
            sourceRef,
            idempotencyKey,
            fragmentIds,
            supersedesMemoryIds,
            NormalizeStatus(status),
            NormalizeStage(stage),
            error,
            memoryCount,
            insightCount,
            string.IsNullOrWhiteSpace(statsJson) ? "{}" : statsJson,
            createdAt,
            updatedAt,
            startedAt,
            completedAt);

    public void MarkRunning(string stage, DateTimeOffset now)
    {
        Status = Running;
        Stage = NormalizeStage(stage);
        StartedAt ??= now;
        UpdatedAt = now;
        Error = null;
    }

    public void Complete(string stage, string statsJson, int memoryCount, int insightCount, DateTimeOffset now)
    {
        Status = Completed;
        Stage = stage;
        StatsJson = string.IsNullOrWhiteSpace(statsJson) ? "{}" : statsJson;
        MemoryCount = memoryCount;
        InsightCount = insightCount;
        CompletedAt = now;
        UpdatedAt = now;
        Error = null;
    }

    public void MarkPartial(string error, string statsJson, int memoryCount, int insightCount, DateTimeOffset now)
    {
        Status = Partial;
        Stage = StageCompleted;
        Error = TrimOrNull(error, 2_000);
        StatsJson = string.IsNullOrWhiteSpace(statsJson) ? "{}" : statsJson;
        MemoryCount = memoryCount;
        InsightCount = insightCount;
        CompletedAt = now;
        UpdatedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = Failed;
        Stage = StageFailed;
        Error = TrimOrNull(error, 2_000);
        CompletedAt = now;
        UpdatedAt = now;
    }

    /// <summary>失败任务可重试：保留原文与证据，只重置状态。</summary>
    public void ResetForRetry(DateTimeOffset now)
    {
        Status = Queued;
        Stage = StageReceived;
        Error = null;
        StartedAt = null;
        CompletedAt = null;
        UpdatedAt = now;
    }

    private static string NormalizeSourceType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            SourceFile => SourceFile,
            SourceCorrection => SourceCorrection,
            SourceSupplement => SourceSupplement,
            _ => SourceText
        };

    private static string NormalizeStatus(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Running => Running,
            Completed => Completed,
            Partial => Partial,
            Failed => Failed,
            _ => Queued
        };

    private static string NormalizeStage(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            StageParsing => StageParsing,
            StageExtracting => StageExtracting,
            StageCommitting => StageCommitting,
            StageIndexing => StageIndexing,
            StageCompleted => StageCompleted,
            StageFailed => StageFailed,
            _ => StageReceived
        };

    private static string? TrimOrNull(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return null;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
