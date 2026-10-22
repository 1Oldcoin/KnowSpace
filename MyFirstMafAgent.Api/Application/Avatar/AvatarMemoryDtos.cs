using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>整理后的记忆（API 输出）。</summary>
public sealed record AvatarMemoryDto(
    Guid Id,
    Guid? FragmentId,
    string Kind,
    string KindLabel,
    string MemoryType,
    string MemoryTypeLabel,
    string Stability,
    string Title,
    string Summary,
    string Content,
    DateTimeOffset OccurredAt,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Tags,
    string Emotion,
    int Importance,
    int Confidence,
    string LifecycleStatus,
    string LifecycleLabel,
    string PrivacyLevel,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidTo,
    Guid? SupersedesId,
    int EvidenceCount,
    DateTimeOffset? LastUsedAt,
    string Source,
    string VectorState,
    DateTimeOffset CreatedAt)
{
    public static AvatarMemoryDto FromDomain(AvatarMemory memory) => new(
        memory.Id,
        memory.FragmentId,
        memory.Kind,
        AvatarMemoryKinds.Label(memory.Kind),
        memory.MemoryType,
        AvatarMemoryTypes.Label(memory.MemoryType),
        memory.Stability,
        memory.Title,
        memory.Summary,
        memory.Content,
        memory.OccurredAt,
        memory.People,
        memory.Tags,
        memory.Emotion,
        memory.Importance,
        memory.Confidence,
        memory.LifecycleStatus,
        AvatarMemoryLifecycle.Label(memory.LifecycleStatus),
        memory.PrivacyLevel,
        memory.ValidFrom,
        memory.ValidTo,
        memory.SupersedesId,
        memory.EvidenceCount,
        memory.LastUsedAt,
        memory.Source,
        memory.VectorState,
        memory.CreatedAt);
}

/// <summary>投喂页的对话记录。</summary>
public sealed record AvatarFeedEntryDto(
    long Id,
    string Role,
    string Content,
    DateTimeOffset CreatedAt)
{
    public static AvatarFeedEntryDto FromDomain(AvatarFeedEntry entry) =>
        new(entry.Id, entry.Role, entry.Content, entry.CreatedAt);
}

/// <summary>一次投喂的结果：AI 回执 + 新整理出的记忆与洞察。</summary>
public sealed record AvatarFeedResultDto(
    string Reply,
    IReadOnlyList<AvatarMemoryDto> Memories,
    IReadOnlyList<AvatarInsightDto> Insights,
    bool UsedFallback,
    string? Warning,
    int IndexedCount,
    int PendingVectorCount,
    Guid? IngestionId = null,
    string? IngestionStatus = null);

/// <summary>投喂任务状态。</summary>
public sealed record AvatarIngestionDto(
    Guid Id,
    string SourceType,
    string? SourceRef,
    string Status,
    string Stage,
    string? Error,
    int MemoryCount,
    int InsightCount,
    string StatsJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt)
{
    public static AvatarIngestionDto FromDomain(AvatarIngestion ingestion) => new(
        ingestion.Id,
        ingestion.SourceType,
        ingestion.SourceRef,
        ingestion.Status,
        ingestion.Stage,
        ingestion.Error,
        ingestion.MemoryCount,
        ingestion.InsightCount,
        ingestion.StatsJson,
        ingestion.CreatedAt,
        ingestion.StartedAt,
        ingestion.CompletedAt);
}

/// <summary>记忆的原文证据。</summary>
public sealed record AvatarMemoryEvidenceDto(
    Guid Id,
    Guid MemoryId,
    Guid? FragmentId,
    string EvidenceText,
    int StartOffset,
    int EndOffset,
    int? PageNo,
    string? MessageId,
    DateTimeOffset? OccurredAt,
    DateTimeOffset RecordedAt)
{
    public static AvatarMemoryEvidenceDto FromDomain(AvatarMemoryEvidence evidence) => new(
        evidence.Id,
        evidence.MemoryId,
        evidence.FragmentId,
        evidence.EvidenceText,
        evidence.StartOffset,
        evidence.EndOffset,
        evidence.PageNo,
        evidence.MessageId,
        evidence.OccurredAt,
        evidence.RecordedAt);
}

/// <summary>记忆修订历史。</summary>
public sealed record AvatarMemoryVersionDto(
    Guid Id,
    Guid MemoryId,
    int Version,
    string ChangeType,
    Guid? SupersedesId,
    string Reason,
    string SnapshotJson,
    DateTimeOffset CreatedAt)
{
    public static AvatarMemoryVersionDto FromDomain(AvatarMemoryVersion version) => new(
        version.Id,
        version.MemoryId,
        version.Version,
        version.ChangeType,
        version.SupersedesId,
        version.Reason,
        version.SnapshotJson,
        version.CreatedAt);
}

/// <summary>人格洞察候选版本。</summary>
public sealed record AvatarInsightVersionDto(
    Guid Id,
    Guid? InsightId,
    string Dimension,
    string DimensionLabel,
    string Title,
    string Content,
    int Confidence,
    string Status,
    string Scope,
    IReadOnlyList<Guid> EvidenceIds,
    Guid? SupersedesId,
    DateTimeOffset CreatedAt)
{
    public static AvatarInsightVersionDto FromDomain(AvatarInsightVersion version) => new(
        version.Id,
        version.InsightId,
        version.Dimension,
        AvatarInsightDimensions.Label(version.Dimension),
        version.Title,
        version.Content,
        version.Confidence,
        version.Status,
        version.Scope,
        version.EvidenceIds,
        version.SupersedesId,
        version.CreatedAt);
}

/// <summary>回答反馈。</summary>
public sealed record AvatarFeedbackDto(
    Guid Id,
    long? ChatMessageId,
    IReadOnlyList<Guid> MemoryIds,
    string FeedbackType,
    string ReasonCode,
    string Comment,
    string CorrectedText,
    Guid? IngestionId,
    DateTimeOffset CreatedAt)
{
    public static AvatarFeedbackDto FromDomain(AvatarFeedback feedback) => new(
        feedback.Id,
        feedback.ChatMessageId,
        feedback.MemoryIds,
        feedback.FeedbackType,
        feedback.ReasonCode,
        feedback.Comment,
        feedback.CorrectedText,
        feedback.IngestionId,
        feedback.CreatedAt);
}

/// <summary>固定评测题。</summary>
public sealed record AvatarEvalCaseDto(
    Guid Id,
    string Category,
    string Question,
    string ExpectedAnswer,
    IReadOnlyList<Guid> MustMemoryIds,
    IReadOnlyList<Guid> ForbiddenMemoryIds,
    IReadOnlyList<string> Rubric,
    string Split,
    bool Active,
    DateTimeOffset CreatedAt)
{
    public static AvatarEvalCaseDto FromDomain(AvatarEvalCase evalCase) => new(
        evalCase.Id,
        evalCase.Category,
        evalCase.Question,
        evalCase.ExpectedAnswer,
        evalCase.MustMemoryIds,
        evalCase.ForbiddenMemoryIds,
        evalCase.Rubric,
        evalCase.Split,
        evalCase.Active,
        evalCase.CreatedAt);
}

public sealed record AvatarEvalRunDto(
    Guid Id,
    string Status,
    int Total,
    int Passed,
    int FactErrorCount,
    double CitationAccuracy,
    int StyleScore,
    string Summary,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AvatarEvalResultDto>? Results = null)
{
    public static AvatarEvalRunDto FromDomain(
        AvatarEvalRun run,
        IReadOnlyList<AvatarEvalResult>? results = null) => new(
        run.Id,
        run.Status,
        run.Total,
        run.Passed,
        run.FactErrorCount,
        run.CitationAccuracy,
        run.StyleScore,
        run.Summary,
        run.StartedAt,
        run.CompletedAt,
        results?.Select(AvatarEvalResultDto.FromDomain).ToList());
}

public sealed record AvatarEvalResultDto(
    Guid Id,
    Guid CaseId,
    string Question,
    string Answer,
    IReadOnlyList<Guid> CitationIds,
    bool Passed,
    int Score,
    string Detail,
    DateTimeOffset CreatedAt)
{
    public static AvatarEvalResultDto FromDomain(AvatarEvalResult result) => new(
        result.Id,
        result.CaseId,
        result.Question,
        result.Answer,
        result.CitationIds,
        result.Passed,
        result.Score,
        result.Detail,
        result.CreatedAt);
}

public sealed record AvatarReindexResultDto(
    int Indexed,
    int Pending,
    int Total,
    string? Warning);
