namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 人格洞察的候选版本。人格归纳不再直接覆盖当前洞察：
/// 先写候选，证据一致且置信度足够（或用户确认）后才升级为稳定人格。
/// </summary>
public sealed class AvatarInsightVersion
{
    public const string StatusCandidate = "candidate";
    public const string StatusConfirmed = "confirmed";
    public const string StatusSuperseded = "superseded";

    private AvatarInsightVersion()
    {
    }

    private AvatarInsightVersion(
        Guid id,
        Guid? insightId,
        string dimension,
        string title,
        string content,
        int confidence,
        string status,
        string scope,
        IReadOnlyList<Guid> evidenceIds,
        Guid? supersedesId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        InsightId = insightId;
        Dimension = dimension;
        Title = title;
        Content = content;
        Confidence = confidence;
        Status = status;
        Scope = scope;
        EvidenceIds = evidenceIds;
        SupersedesId = supersedesId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>升级为稳定人格后对应的 avatar_insights.id。</summary>
    public Guid? InsightId { get; private set; }

    public string Dimension { get; private set; } = "summary";
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public int Confidence { get; private set; } = 50;
    public string Status { get; private set; } = StatusCandidate;

    /// <summary>适用范围，例如“工作场景”“面对亲密关系”。</summary>
    public string Scope { get; private set; } = string.Empty;

    public IReadOnlyList<Guid> EvidenceIds { get; private set; } = [];
    public Guid? SupersedesId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarInsightVersion Create(
        AvatarInsight candidate,
        IReadOnlyList<Guid> evidenceIds,
        string? scope,
        string status,
        Guid? supersedesId,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            null,
            candidate.Dimension,
            candidate.Title,
            candidate.Content,
            Math.Clamp(candidate.Confidence, 0, 100),
            NormalizeStatus(status),
            (scope ?? string.Empty).Trim() is { Length: > 0 } value
                ? (value.Length > 200 ? value[..200] : value)
                : string.Empty,
            evidenceIds.Where(id => id != Guid.Empty).Distinct().Take(50).ToList(),
            supersedesId,
            now,
            now);

    public static AvatarInsightVersion Restore(
        Guid id,
        Guid? insightId,
        string dimension,
        string title,
        string content,
        int confidence,
        string status,
        string scope,
        IReadOnlyList<Guid> evidenceIds,
        Guid? supersedesId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, insightId, dimension, title, content, confidence, NormalizeStatus(status), scope,
            evidenceIds, supersedesId, createdAt, updatedAt);

    public void MarkConfirmed(Guid insightId, DateTimeOffset now)
    {
        Status = StatusConfirmed;
        InsightId = insightId;
        UpdatedAt = now;
    }

    public void MarkSuperseded(DateTimeOffset now)
    {
        Status = StatusSuperseded;
        UpdatedAt = now;
    }

    private static string NormalizeStatus(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            StatusConfirmed or "confirmed" or "已确认" => StatusConfirmed,
            StatusSuperseded or "superseded" => StatusSuperseded,
            _ => StatusCandidate
        };
}

/// <summary>模型在一次投喂中给出的候选人格洞察。</summary>
public sealed record AvatarInsightCandidate(
    AvatarInsight Insight,
    IReadOnlyList<Guid> EvidenceMemoryIds,
    string? Scope);
