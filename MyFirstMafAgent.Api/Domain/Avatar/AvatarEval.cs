namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>固定评测题。每题保存认可答案、必须引用/禁止引用的记忆和评分维度。</summary>
public sealed class AvatarEvalCase
{
    public const string SplitPublic = "public";
    public const string SplitHidden = "hidden";

    private AvatarEvalCase()
    {
    }

    private AvatarEvalCase(
        Guid id,
        string category,
        string question,
        string expectedAnswer,
        IReadOnlyList<Guid> mustMemoryIds,
        IReadOnlyList<Guid> forbiddenMemoryIds,
        IReadOnlyList<string> rubric,
        string split,
        bool active,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        Category = category;
        Question = question;
        ExpectedAnswer = expectedAnswer;
        MustMemoryIds = mustMemoryIds;
        ForbiddenMemoryIds = forbiddenMemoryIds;
        Rubric = rubric;
        Split = split;
        Active = active;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>values 价值观 / experience 重要经历 / relationship 人际关系 / decision 决策 / style 表达 / refusal 拒绝 / conflict 冲突 / memory_gap 记忆不足。</summary>
    public string Category { get; private set; } = "values";

    public string Question { get; private set; } = string.Empty;
    public string ExpectedAnswer { get; private set; } = string.Empty;
    public IReadOnlyList<Guid> MustMemoryIds { get; private set; } = [];
    public IReadOnlyList<Guid> ForbiddenMemoryIds { get; private set; } = [];
    public IReadOnlyList<string> Rubric { get; private set; } = [];
    public string Split { get; private set; } = SplitPublic;
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AvatarEvalCase Create(
        string? category,
        string? question,
        string? expectedAnswer,
        IReadOnlyList<Guid>? mustMemoryIds,
        IReadOnlyList<Guid>? forbiddenMemoryIds,
        IReadOnlyList<string>? rubric,
        string? split,
        bool? active,
        DateTimeOffset now)
    {
        var normalizedQuestion = (question ?? string.Empty).Trim();
        if (normalizedQuestion.Length == 0)
            throw new AvatarValidationException("评测题的问题不能为空。");
        if (normalizedQuestion.Length > 2_000)
            normalizedQuestion = normalizedQuestion[..2_000];

        return new AvatarEvalCase(
            Guid.NewGuid(),
            NormalizeCategory(category),
            normalizedQuestion,
            Truncate((expectedAnswer ?? string.Empty).Trim(), 8_000),
            (mustMemoryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList(),
            (forbiddenMemoryIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList(),
            (rubric ?? []).Select(item => (item ?? string.Empty).Trim())
                .Where(item => item.Length > 0).Take(12).ToList(),
            (split ?? string.Empty).Trim().ToLowerInvariant() == SplitHidden ? SplitHidden : SplitPublic,
            active ?? true,
            now,
            now);
    }

    public static AvatarEvalCase Restore(
        Guid id,
        string category,
        string question,
        string expectedAnswer,
        IReadOnlyList<Guid> mustMemoryIds,
        IReadOnlyList<Guid> forbiddenMemoryIds,
        IReadOnlyList<string> rubric,
        string split,
        bool active,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, category, question, expectedAnswer, mustMemoryIds, forbiddenMemoryIds, rubric, split, active, createdAt, updatedAt);

    private static string NormalizeCategory(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "experience" or "经历" => "experience",
            "relationship" or "关系" => "relationship",
            "decision" or "决策" => "decision",
            "style" or "表达" => "style",
            "refusal" or "拒绝" => "refusal",
            "conflict" or "冲突" => "conflict",
            "memory_gap" or "记忆不足" => "memory_gap",
            _ => "values"
        };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>一次评测运行。</summary>
public sealed class AvatarEvalRun
{
    public const string RunningStatus = "running";
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";

    private AvatarEvalRun()
    {
    }

    private AvatarEvalRun(
        Guid id,
        string status,
        int total,
        int passed,
        int factErrorCount,
        double citationAccuracy,
        int styleScore,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        Status = status;
        Total = total;
        Passed = passed;
        FactErrorCount = factErrorCount;
        CitationAccuracy = citationAccuracy;
        StyleScore = styleScore;
        Summary = summary;
        StartedAt = startedAt;
        CompletedAt = completedAt;
    }

    public Guid Id { get; private set; }
    public string Status { get; private set; } = RunningStatus;
    public int Total { get; private set; }
    public int Passed { get; private set; }
    public int FactErrorCount { get; private set; }
    public double CitationAccuracy { get; private set; }
    public int StyleScore { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static AvatarEvalRun Create(int total, DateTimeOffset now) =>
        new(Guid.NewGuid(), RunningStatus, total, 0, 0, 0, 0, string.Empty, now, null);

    public static AvatarEvalRun Restore(
        Guid id,
        string status,
        int total,
        int passed,
        int factErrorCount,
        double citationAccuracy,
        int styleScore,
        string summary,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt) =>
        new(id, status, total, passed, factErrorCount, citationAccuracy, styleScore, summary, startedAt, completedAt);

    public void Complete(
        int passed,
        int factErrorCount,
        double citationAccuracy,
        int styleScore,
        string summary,
        DateTimeOffset now)
    {
        Status = CompletedStatus;
        Passed = passed;
        FactErrorCount = factErrorCount;
        CitationAccuracy = Math.Round(Math.Clamp(citationAccuracy, 0, 1), 4);
        StyleScore = Math.Clamp(styleScore, 0, 100);
        Summary = Truncate(summary ?? string.Empty, 4_000);
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = FailedStatus;
        Summary = Truncate(error ?? string.Empty, 2_000);
        CompletedAt = now;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>单题评测结果。</summary>
public sealed class AvatarEvalResult
{
    private AvatarEvalResult()
    {
    }

    private AvatarEvalResult(
        Guid id,
        Guid runId,
        Guid caseId,
        string question,
        string answer,
        IReadOnlyList<Guid> citationIds,
        bool passed,
        int score,
        string detail,
        DateTimeOffset createdAt)
    {
        Id = id;
        RunId = runId;
        CaseId = caseId;
        Question = question;
        Answer = answer;
        CitationIds = citationIds;
        Passed = passed;
        Score = score;
        Detail = detail;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid RunId { get; private set; }
    public Guid CaseId { get; private set; }
    public string Question { get; private set; } = string.Empty;
    public string Answer { get; private set; } = string.Empty;
    public IReadOnlyList<Guid> CitationIds { get; private set; } = [];
    public bool Passed { get; private set; }
    public int Score { get; private set; }
    public string Detail { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    public static AvatarEvalResult Create(
        Guid runId,
        Guid caseId,
        string question,
        string answer,
        IReadOnlyList<Guid> citationIds,
        bool passed,
        int score,
        string detail,
        DateTimeOffset now) =>
        new(Guid.NewGuid(), runId, caseId, question, Truncate(answer ?? string.Empty, 20_000),
            citationIds.Distinct().ToList(), passed, Math.Clamp(score, 0, 100),
            Truncate(detail ?? string.Empty, 4_000), now);

    public static AvatarEvalResult Restore(
        Guid id,
        Guid runId,
        Guid caseId,
        string question,
        string answer,
        IReadOnlyList<Guid> citationIds,
        bool passed,
        int score,
        string detail,
        DateTimeOffset createdAt) =>
        new(id, runId, caseId, question, answer, citationIds, passed, score, detail, createdAt);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
