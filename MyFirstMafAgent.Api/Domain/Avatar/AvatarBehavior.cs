namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>一条日常行为记录，用于刻画真实的时间分配与习惯。</summary>
public sealed class AvatarBehavior
{
    public const int MaxDescriptionLength = 500;

    private AvatarBehavior()
    {
    }

    private AvatarBehavior(
        Guid id,
        DateTimeOffset occurredOn,
        string category,
        string description,
        int durationMinutes,
        int? mood,
        IReadOnlyList<string> tags,
        string source,
        DateTimeOffset createdAt)
    {
        Id = id;
        OccurredOn = occurredOn;
        Category = category;
        Description = description;
        DurationMinutes = durationMinutes;
        Mood = mood;
        Tags = tags;
        Source = source;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset OccurredOn { get; private set; }
    public string Category { get; private set; } = "其它";
    public string Description { get; private set; } = string.Empty;

    /// <summary>投入分钟数，0 表示未记录时长。</summary>
    public int DurationMinutes { get; private set; }

    /// <summary>当时情绪 1-5，null 表示未记录。</summary>
    public int? Mood { get; private set; }

    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>来源：manual（手动）/ chat（问答自动记录）/ tool（模型记录）/ import。</summary>
    public string Source { get; private set; } = "manual";

    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarBehavior Create(AvatarBehaviorDraft draft, DateTimeOffset now)
    {
        var description = (draft.Description ?? string.Empty).Trim();
        if (description.Length == 0) throw new AvatarValidationException("行为描述不能为空。");
        if (description.Length > MaxDescriptionLength)
            throw new AvatarValidationException($"行为描述不能超过 {MaxDescriptionLength} 个字符。");

        return new AvatarBehavior(
            Guid.NewGuid(),
            draft.OccurredOn ?? now,
            NormalizeCategory(draft.Category),
            description,
            Math.Clamp(draft.DurationMinutes, 0, 24 * 60),
            draft.Mood is null ? null : Math.Clamp(draft.Mood.Value, 1, 5),
            AvatarExperience.NormalizeTags(draft.Tags),
            NormalizeSource(draft.Source),
            now);
    }

    public static AvatarBehavior Restore(
        Guid id,
        DateTimeOffset occurredOn,
        string category,
        string description,
        int durationMinutes,
        int? mood,
        IReadOnlyList<string> tags,
        string source,
        DateTimeOffset createdAt) =>
        new(id, occurredOn, category, description, durationMinutes, mood, tags, source, createdAt);

    private static string NormalizeCategory(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return AvatarDefaults.BehaviorCategories.Contains(trimmed) ? trimmed : "其它";
    }

    private static string NormalizeSource(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "chat" => "chat",
            "tool" => "tool",
            "import" => "import",
            _ => "manual"
        };
}

public sealed record AvatarBehaviorDraft(
    DateTimeOffset? OccurredOn,
    string? Category,
    string? Description,
    int DurationMinutes,
    int? Mood,
    IReadOnlyList<string>? Tags,
    string? Source);
