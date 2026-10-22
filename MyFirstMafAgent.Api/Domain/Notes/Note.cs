namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>
/// 学习笔记聚合根。负责维护自身不变量，持久化细节由仓储实现。
/// </summary>
public sealed class Note
{
    public const int MaxTitleLength = 120;
    public const int MaxContentLength = 50_000;
    public const int MaxCategoryLength = 40;
    public const int MaxTagCount = 10;
    public const int MaxTagLength = 24;

    private Note()
    {
    }

    private Note(
        Guid id,
        Guid knowledgeBaseId,
        string title,
        string content,
        string category,
        string color,
        IReadOnlyList<string> tags,
        bool isPinned,
        Guid? sourceDocumentId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        KnowledgeBaseId = knowledgeBaseId;
        Title = title;
        Content = content;
        Category = category;
        Color = color;
        Tags = tags;
        IsPinned = isPinned;
        SourceDocumentId = sourceDocumentId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public Guid KnowledgeBaseId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public string Category { get; private set; } = NoteDefaults.Category;
    public string Color { get; private set; } = NoteDefaults.Color;
    public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();
    public bool IsPinned { get; private set; }
    public Guid? SourceDocumentId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>用于列表展示的摘要（去掉换行后的前 180 个字符）。</summary>
    public string Excerpt
    {
        get
        {
            var normalized = Content.Replace("\r", " ").Replace("\n", " ").Trim();
            return normalized.Length <= 180 ? normalized : normalized[..180] + "…";
        }
    }

    public static Note Create(
        Guid knowledgeBaseId,
        string title,
        string content,
        string? category,
        string? color,
        IEnumerable<string>? tags,
        Guid? sourceDocumentId,
        DateTimeOffset now)
    {
        var normalizedTitle = NormalizeTitle(title);
        var normalizedContent = NormalizeContent(content);
        return new Note(
            Guid.NewGuid(),
            knowledgeBaseId,
            normalizedTitle,
            normalizedContent,
            NormalizeCategory(category),
            NoteDefaults.NormalizeColor(color),
            NormalizeTags(tags),
            false,
            sourceDocumentId,
            now,
            now);
    }

    /// <summary>用于从持久化状态重建聚合。</summary>
    public static Note Restore(
        Guid id,
        Guid knowledgeBaseId,
        string title,
        string content,
        string category,
        string color,
        IEnumerable<string>? tags,
        bool isPinned,
        Guid? sourceDocumentId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, knowledgeBaseId, title, content, category, color, NormalizeTags(tags), isPinned,
            sourceDocumentId, createdAt, updatedAt);

    public void Update(
        string title,
        string content,
        string? category,
        string? color,
        IEnumerable<string>? tags,
        DateTimeOffset now)
    {
        Title = NormalizeTitle(title);
        Content = NormalizeContent(content);
        Category = NormalizeCategory(category);
        Color = NoteDefaults.NormalizeColor(color);
        Tags = NormalizeTags(tags);
        UpdatedAt = now;
    }

    public void SetPinned(bool pinned, DateTimeOffset now)
    {
        if (IsPinned == pinned) return;
        IsPinned = pinned;
        UpdatedAt = now;
    }

    internal static string NormalizeTitle(string? title)
    {
        var value = title?.Trim() ?? string.Empty;
        if (value.Length == 0) throw new NoteValidationException("笔记标题不能为空。");
        if (value.Length > MaxTitleLength) throw new NoteValidationException($"笔记标题不能超过 {MaxTitleLength} 个字符。");
        return value;
    }

    internal static string NormalizeContent(string? content)
    {
        var value = content?.Replace("\r\n", "\n") ?? string.Empty;
        if (value.Length > MaxContentLength)
            throw new NoteValidationException($"笔记内容不能超过 {MaxContentLength:N0} 个字符。");
        return value;
    }

    internal static string NormalizeCategory(string? category)
    {
        var value = category?.Trim() ?? string.Empty;
        if (value.Length == 0) return NoteDefaults.Category;
        if (value.Length > MaxCategoryLength) throw new NoteValidationException($"笔记分类不能超过 {MaxCategoryLength} 个字符。");
        return value;
    }

    internal static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null) return Array.Empty<string>();
        var normalized = tags
            .Select(tag => tag?.Trim().TrimStart('#').Trim() ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count > MaxTagCount) throw new NoteValidationException($"标签最多 {MaxTagCount} 个。");
        if (normalized.Any(tag => tag.Length > MaxTagLength))
            throw new NoteValidationException($"单个标签不能超过 {MaxTagLength} 个字符。");
        return normalized;
    }
}

public static class NoteDefaults
{
    public const string Category = "灵感随记";
    public const string Color = "lilac";

    public static readonly string[] Colors = ["lilac", "mint", "peach", "blue", "rose", "sage"];

    public static string NormalizeColor(string? color) =>
        !string.IsNullOrWhiteSpace(color) && Colors.Contains(color.Trim(), StringComparer.OrdinalIgnoreCase)
            ? color.Trim().ToLowerInvariant()
            : Color;
}

public sealed class NoteValidationException(string message) : Exception(message);
