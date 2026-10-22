namespace MyFirstMafAgent.Api.Contracts;

/// <summary>聊天式投喂：只要一段自由文本，其余交给 AI 整理。</summary>
public sealed record FeedAvatarRequest(
    string? Text,
    DateTimeOffset? OccurredAt);

public sealed record ReindexAvatarMemoriesRequest(
    bool? All,
    int? Limit);

/// <summary>回答反馈 / 纠正 / 补充。</summary>
public sealed record AvatarFeedbackRequest(
    long? ChatMessageId,
    List<Guid>? MemoryIds,
    string? FeedbackType,
    string? ReasonCode,
    string? Comment,
    string? CorrectedText);

/// <summary>新增固定评测题。</summary>
public sealed record CreateAvatarEvalCaseRequest(
    string? Category,
    string? Question,
    string? ExpectedAnswer,
    List<Guid>? MustMemoryIds,
    List<Guid>? ForbiddenMemoryIds,
    List<string>? Rubric,
    string? Split,
    bool? Active);

public sealed record RunAvatarEvalRequest(
    bool? IncludeHidden,
    int? Limit);

public sealed record CreateAvatarFragmentRequest(
    string? Kind,
    string? Title,
    string? Content,
    DateTimeOffset? OccurredAt,
    List<string>? Tags);

public sealed record BulkCreateAvatarFragmentsRequest(
    string? Kind,
    List<string>? Contents,
    DateTimeOffset? OccurredAt);

public sealed record AvatarDigestRequest(bool IncludeDigested);

public sealed record CreateAvatarChatSessionRequest(string? Title);

public sealed record AvatarChatRequest(Guid? SessionId, string? Message);

public sealed record SaveAvatarProfileRequest(
    string? DisplayName,
    string? Headline,
    string? Personality,
    string? Values,
    string? Strengths,
    string? Weaknesses,
    string? Skills,
    string? Resources,
    string? Constraints,
    string? LongTermGoals,
    string? RiskPreference);

public sealed record CreateAvatarExperienceRequest(
    DateTimeOffset? OccurredAt,
    string? Category,
    string? Title,
    string? Content,
    string? Outcome,
    string? Emotion,
    int Impact,
    List<string>? Tags);

public sealed record CreateAvatarBehaviorRequest(
    DateTimeOffset? OccurredOn,
    string? Category,
    string? Description,
    int DurationMinutes,
    int? Mood,
    List<string>? Tags);

public sealed record SaveAvatarRelationshipRequest(
    string? Name,
    string? Role,
    int Closeness,
    int Influence,
    string? Notes);

public sealed record RunAvatarSimulationRequest(
    string? Scenario,
    string? Goal,
    int HorizonMonths);
