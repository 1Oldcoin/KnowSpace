namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>用户投喂的一段真实经历，用于让分身理解“我经历过什么”。</summary>
public sealed class AvatarExperience
{
    public const int MaxTitleLength = 160;
    public const int MaxContentLength = 20_000;
    public const int MaxTagCount = 10;
    public const int MaxTagLength = 24;

    private AvatarExperience()
    {
    }

    private AvatarExperience(
        Guid id,
        DateTimeOffset occurredAt,
        string category,
        string title,
        string content,
        string outcome,
        string emotion,
        int impact,
        IReadOnlyList<string> tags,
        DateTimeOffset createdAt)
    {
        Id = id;
        OccurredAt = occurredAt;
        Category = category;
        Title = title;
        Content = content;
        Outcome = outcome;
        Emotion = emotion;
        Impact = impact;
        Tags = tags;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string Category { get; private set; } = "其它";
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;

    /// <summary>结果：success / failure / ongoing。</summary>
    public string Outcome { get; private set; } = "ongoing";

    /// <summary>情绪：positive / negative / mixed / neutral。</summary>
    public string Emotion { get; private set; } = "neutral";

    /// <summary>对个人轨迹的影响强度 1-5。</summary>
    public int Impact { get; private set; } = 3;

    public IReadOnlyList<string> Tags { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarExperience Create(AvatarExperienceDraft draft, DateTimeOffset now)
    {
        var title = (draft.Title ?? string.Empty).Trim();
        if (title.Length == 0) throw new AvatarValidationException("经历标题不能为空。");
        if (title.Length > MaxTitleLength) throw new AvatarValidationException($"经历标题不能超过 {MaxTitleLength} 个字符。");

        var content = (draft.Content ?? string.Empty).Trim();
        if (content.Length > MaxContentLength) throw new AvatarValidationException($"经历内容不能超过 {MaxContentLength} 个字符。");

        var tags = NormalizeTags(draft.Tags);
        return new AvatarExperience(
            Guid.NewGuid(),
            draft.OccurredAt ?? now,
            NormalizeCategory(draft.Category),
            title,
            content,
            NormalizeOutcome(draft.Outcome),
            NormalizeEmotion(draft.Emotion),
            Math.Clamp(draft.Impact <= 0 ? 3 : draft.Impact, 1, 5),
            tags,
            now);
    }

    public static AvatarExperience Restore(
        Guid id,
        DateTimeOffset occurredAt,
        string category,
        string title,
        string content,
        string outcome,
        string emotion,
        int impact,
        IReadOnlyList<string> tags,
        DateTimeOffset createdAt) =>
        new(id, occurredAt, category, title, content, outcome, emotion, impact, tags, createdAt);

    private static string NormalizeCategory(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return AvatarDefaults.ExperienceCategories.Contains(trimmed) ? trimmed : "其它";
    }

    private static string NormalizeOutcome(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "success" or "成功" => "success",
            "failure" or "失败" => "failure",
            _ => "ongoing"
        };

    private static string NormalizeEmotion(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "positive" or "积极" => "positive",
            "negative" or "消极" => "negative",
            "mixed" or "复杂" => "mixed",
            _ => "neutral"
        };

    internal static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
            .Select(tag => (tag ?? string.Empty).Trim())
            .Where(tag => tag.Length is > 0 and <= MaxTagLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTagCount)
            .ToList();
}

public sealed record AvatarExperienceDraft(
    DateTimeOffset? OccurredAt,
    string? Category,
    string? Title,
    string? Content,
    string? Outcome,
    string? Emotion,
    int Impact,
    IReadOnlyList<string>? Tags);
