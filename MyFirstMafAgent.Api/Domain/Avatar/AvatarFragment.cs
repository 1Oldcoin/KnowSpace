namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 一段碎片化投喂素材：日记、聊天记录、对形象的描述、性格自述、观点、习惯记录等。
/// 这是分身的原始记忆，不进入知识库检索。
/// </summary>
public sealed class AvatarFragment
{
    public const int MaxTitleLength = 200;
    public const int MaxContentLength = 200_000;

    private AvatarFragment()
    {
    }

    private AvatarFragment(
        Guid id,
        string kind,
        string title,
        string content,
        DateTimeOffset occurredAt,
        string source,
        IReadOnlyList<string> tags,
        bool isDigested,
        DateTimeOffset createdAt)
    {
        Id = id;
        Kind = kind;
        Title = title;
        Content = content;
        OccurredAt = occurredAt;
        Source = source;
        Tags = tags;
        IsDigested = isDigested;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    /// <summary>journal 日记 / chat 聊天 / personality 性格 / appearance 形象 / opinion 观点 / habit 习惯 / knowledge 见闻 / relationship 人际 / other 其它。</summary>
    public string Kind { get; private set; } = "other";

    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>manual 手动 / upload 文件上传 / chat 对话投喂。</summary>
    public string Source { get; private set; } = "manual";

    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>是否已被归纳进人格画像。</summary>
    public bool IsDigested { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarFragment Create(AvatarFragmentDraft draft, DateTimeOffset now, string defaultSource = "manual")
    {
        var content = (draft.Content ?? string.Empty).Trim();
        if (content.Length == 0) throw new AvatarValidationException("投喂内容不能为空。");
        if (content.Length > MaxContentLength)
            throw new AvatarValidationException($"单条投喂不能超过 {MaxContentLength} 个字符。");

        var title = (draft.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            // 未给标题时用内容首行，保证列表可读。
            var firstLine = content.Split('\n', 2)[0].Trim();
            title = firstLine.Length <= 60 ? firstLine : firstLine[..60] + "…";
        }
        if (title.Length > MaxTitleLength)
            throw new AvatarValidationException($"标题不能超过 {MaxTitleLength} 个字符。");

        return new AvatarFragment(
            Guid.NewGuid(),
            NormalizeKind(draft.Kind),
            title,
            content,
            draft.OccurredAt ?? now,
            NormalizeSource(draft.Source, defaultSource),
            AvatarExperience.NormalizeTags(draft.Tags),
            false,
            now);
    }

    public static AvatarFragment Restore(
        Guid id,
        string kind,
        string title,
        string content,
        DateTimeOffset occurredAt,
        string source,
        IReadOnlyList<string> tags,
        bool isDigested,
        DateTimeOffset createdAt) =>
        new(id, kind, title, content, occurredAt, source, tags, isDigested, createdAt);

    private static string NormalizeKind(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "journal" or "日记" => "journal",
            "chat" or "聊天" or "聊天记录" => "chat",
            "personality" or "性格" => "personality",
            "appearance" or "形象" => "appearance",
            "opinion" or "观点" => "opinion",
            "habit" or "习惯" => "habit",
            "knowledge" or "见闻" or "见识" => "knowledge",
            "relationship" or "人际" => "relationship",
            _ => "other"
        };

    private static string NormalizeSource(string? value, string fallback) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "upload" => "upload",
            "chat" => "chat",
            "manual" => "manual",
            _ => fallback
        };
}

public sealed record AvatarFragmentDraft(
    string? Kind,
    string? Title,
    string? Content,
    DateTimeOffset? OccurredAt,
    string? Source,
    IReadOnlyList<string>? Tags);

public static class AvatarFragmentKinds
{
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("journal", "日记"),
        ("chat", "聊天记录"),
        ("personality", "性格"),
        ("appearance", "形象"),
        ("opinion", "观点"),
        ("habit", "习惯"),
        ("knowledge", "见闻"),
        ("relationship", "人际"),
        ("other", "其它")
    ];

    public static string Label(string kind) =>
        All.FirstOrDefault(item => item.Value == kind).Label ?? "其它";
}
