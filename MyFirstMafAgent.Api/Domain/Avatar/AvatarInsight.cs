namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// 从碎片素材中归纳出的人格洞察。一个维度一条，用于让分身“像本人一样”思考和表达。
/// </summary>
public sealed class AvatarInsight
{
    public const int MaxTitleLength = 120;
    public const int MaxContentLength = 4_000;

    private AvatarInsight()
    {
    }

    private AvatarInsight(
        Guid id,
        string dimension,
        string title,
        string content,
        int confidence,
        int evidenceCount,
        IReadOnlyList<string> evidence,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Dimension = dimension;
        Title = title;
        Content = content;
        Confidence = confidence;
        EvidenceCount = evidenceCount;
        Evidence = evidence;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>personality 性格 / values 价值观 / habits 习惯 / language 语言风格 / decision 决策方式 / knowledge 知识见识 / relationship 人际 / preference 偏好 / summary 总体总结。</summary>
    public string Dimension { get; private set; } = "summary";

    public string Title { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;

    /// <summary>置信度 0-100，取决于证据数量与一致性。</summary>
    public int Confidence { get; private set; }

    public int EvidenceCount { get; private set; }

    /// <summary>支撑该洞察的碎片标题（用于追溯）。</summary>
    public IReadOnlyList<string> Evidence { get; private set; } = [];

    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarInsight Create(
        string? dimension,
        string? title,
        string? content,
        int confidence,
        IReadOnlyList<string>? evidence,
        DateTimeOffset now)
    {
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length == 0) throw new AvatarValidationException("洞察标题不能为空。");
        if (normalizedTitle.Length > MaxTitleLength)
            normalizedTitle = normalizedTitle[..MaxTitleLength];

        var normalizedContent = (content ?? string.Empty).Trim();
        if (normalizedContent.Length == 0) throw new AvatarValidationException("洞察内容不能为空。");
        if (normalizedContent.Length > MaxContentLength)
            normalizedContent = normalizedContent[..MaxContentLength];

        var evidenceList = (evidence ?? [])
            .Select(item => (item ?? string.Empty).Trim())
            .Where(item => item.Length > 0)
            .Take(8)
            .ToList();

        return new AvatarInsight(
            Guid.NewGuid(),
            NormalizeDimension(dimension),
            normalizedTitle,
            normalizedContent,
            Math.Clamp(confidence, 0, 100),
            evidenceList.Count,
            evidenceList,
            now);
    }

    public static AvatarInsight Restore(
        Guid id,
        string dimension,
        string title,
        string content,
        int confidence,
        int evidenceCount,
        IReadOnlyList<string> evidence,
        DateTimeOffset updatedAt) =>
        new(id, dimension, title, content, confidence, evidenceCount, evidence, updatedAt);

    private static string NormalizeDimension(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "personality" or "性格" => "personality",
            "values" or "价值观" => "values",
            "habits" or "习惯" => "habits",
            "language" or "语言风格" => "language",
            "decision" or "决策方式" => "decision",
            "knowledge" or "知识见识" => "knowledge",
            "relationship" or "人际" => "relationship",
            "preference" or "偏好" => "preference",
            _ => "summary"
        };
}

public static class AvatarInsightDimensions
{
    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        ("summary", "总体总结"),
        ("personality", "性格"),
        ("values", "价值观"),
        ("habits", "习惯"),
        ("language", "语言风格"),
        ("decision", "决策方式"),
        ("knowledge", "知识见识"),
        ("relationship", "人际"),
        ("preference", "偏好")
    ];

    public static string Label(string dimension) =>
        All.FirstOrDefault(item => item.Value == dimension).Label ?? dimension;
}
