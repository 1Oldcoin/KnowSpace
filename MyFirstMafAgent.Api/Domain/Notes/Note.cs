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
    public const int MaxSourceCount = 20;
    public const int MaxSourceFileNameLength = 200;
    public const int MaxSourceSnippetLength = 240;

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
        Guid? topicId,
        IReadOnlyList<NoteSource> sources,
        Guid? sourceSessionId,
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
        TopicId = topicId;
        Sources = sources;
        SourceSessionId = sourceSessionId;
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

    /// <summary>首选资料来源（向后兼容：只引用一份资料时就是它）。</summary>
    public Guid? SourceDocumentId { get; private set; }

    /// <summary>笔记归属的专题；null 表示未归类。</summary>
    public Guid? TopicId { get; private set; }

    /// <summary>笔记引用的资料片段（可能来自多份资料）。</summary>
    public IReadOnlyList<NoteSource> Sources { get; private set; } = Array.Empty<NoteSource>();

    /// <summary>笔记是从哪一轮问答生成的；null 表示手工创建。</summary>
    public Guid? SourceSessionId { get; private set; }

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
        DateTimeOffset now,
        Guid? topicId = null,
        IEnumerable<NoteSource>? sources = null,
        Guid? sourceSessionId = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var normalizedContent = NormalizeContent(content);
        var normalizedSources = NormalizeSources(sources);
        return new Note(
            Guid.NewGuid(),
            knowledgeBaseId,
            normalizedTitle,
            normalizedContent,
            NormalizeCategory(category),
            NoteDefaults.NormalizeColor(color),
            NormalizeTags(tags),
            false,
            sourceDocumentId ?? normalizedSources.FirstOrDefault()?.DocumentId,
            topicId,
            normalizedSources,
            sourceSessionId,
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
        DateTimeOffset updatedAt,
        Guid? topicId = null,
        IEnumerable<NoteSource>? sources = null,
        Guid? sourceSessionId = null) =>
        new(id, knowledgeBaseId, title, content, category, color, NormalizeTags(tags), isPinned,
            sourceDocumentId, topicId, NormalizeSources(sources), sourceSessionId, createdAt, updatedAt);

    /// <summary>
    /// 更新笔记正文。
    /// <paramref name="topicId"/> / <paramref name="sources"/> 传 null 表示“保持不变”，
    /// 否则无法把专题改成“未归类”或清空引用。清空请传空集合 / <see cref="Guid.Empty"/>。
    /// </summary>
    public void Update(
        string title,
        string content,
        string? category,
        string? color,
        IEnumerable<string>? tags,
        DateTimeOffset now,
        Guid? topicId = null,
        IEnumerable<NoteSource>? sources = null)
    {
        Title = NormalizeTitle(title);
        Content = NormalizeContent(content);
        Category = NormalizeCategory(category);
        Color = NoteDefaults.NormalizeColor(color);
        Tags = NormalizeTags(tags);

        if (topicId is not null) TopicId = topicId == Guid.Empty ? null : topicId;
        if (sources is not null)
        {
            Sources = NormalizeSources(sources);
            SourceDocumentId = Sources.FirstOrDefault()?.DocumentId;
        }

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

    /// <summary>
    /// 归一化引用片段：丢掉空文档 ID，按（资料 + 切片序号）去重，超量直接截断而不是报错。
    /// 引用只是“依据”，堆到 20 条以上对读者没有意义，也不该因为模型多返回两条就保存失败。
    /// </summary>
    internal static IReadOnlyList<NoteSource> NormalizeSources(IEnumerable<NoteSource>? sources)
    {
        if (sources is null) return Array.Empty<NoteSource>();

        var normalized = new List<NoteSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source is null || source.DocumentId == Guid.Empty) continue;

            var fileName = source.FileName?.Trim() ?? string.Empty;
            if (fileName.Length > MaxSourceFileNameLength) fileName = fileName[..MaxSourceFileNameLength];
            var snippet = source.Snippet?.Trim();
            if (string.IsNullOrEmpty(snippet)) snippet = null;
            else if (snippet.Length > MaxSourceSnippetLength) snippet = snippet[..MaxSourceSnippetLength] + "…";

            var key = $"{source.DocumentId:N}:{source.ChunkIndex}";
            if (!seen.Add(key)) continue;

            normalized.Add(source with
            {
                FileName = fileName.Length == 0 ? "未命名资料" : fileName,
                Similarity = double.IsFinite(source.Similarity) ? source.Similarity : 0,
                Snippet = snippet
            });

            if (normalized.Count >= MaxSourceCount) break;
        }

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
