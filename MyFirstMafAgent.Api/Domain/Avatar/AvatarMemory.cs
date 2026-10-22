namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>
/// AI 整理后的一条「记忆卡」。用户投喂的是自由文本，这里保存被理解、被归纳后的结果：
/// 一条记忆可以是一段经历、一个观点、一个偏好、一段关系、一种说话方式或一条自我认知。
/// 记忆卡是分身问答的主要检索单位，原始碎片仍保留在 <see cref="AvatarFragment"/> 中作为底稿。
/// </summary>
public sealed class AvatarMemory
{
    public const int MaxTitleLength = 160;
    public const int MaxSummaryLength = 4_000;
    public const int MaxContentLength = 20_000;
    public const int MaxPeopleCount = 12;
    public const int MaxTagCount = 12;
    public const int MaxLabelLength = 24;

    /// <summary>结构化数据 JSON 上限，避免异常模型输出撑爆字段。</summary>
    public const int MaxStructuredDataLength = 8_000;

    /// <summary>向量状态：等待向量化。</summary>
    public const string VectorPending = "pending";

    /// <summary>向量状态：已完成向量化。</summary>
    public const string VectorReady = "ready";

    /// <summary>向量状态：向量化失败，可重试。</summary>
    public const string VectorFailed = "failed";

    private AvatarMemory()
    {
    }

    private AvatarMemory(
        Guid id,
        Guid? fragmentId,
        string kind,
        string title,
        string summary,
        string content,
        DateTimeOffset occurredAt,
        IReadOnlyList<string> people,
        IReadOnlyList<string> tags,
        string emotion,
        int importance,
        string source,
        string vectorState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        FragmentId = fragmentId;
        Kind = kind;
        Title = title;
        Summary = summary;
        Content = content;
        OccurredAt = occurredAt;
        People = people;
        Tags = tags;
        Emotion = emotion;
        Importance = importance;
        Source = source;
        VectorState = vectorState;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }

    /// <summary>这条记忆来自哪一段原始投喂（可为空，例如自动记录）。</summary>
    public Guid? FragmentId { get; private set; }

    /// <summary>
    /// 记忆类型。由 AI 自由判断，常见值见 <see cref="AvatarMemoryKinds"/>；
    /// 不限制为固定枚举，未知值会原样保留。
    /// </summary>
    public string Kind { get; private set; } = "other";

    public string Title { get; private set; } = string.Empty;

    /// <summary>被整理后的完整陈述，是向量化的主要文本。</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>保留下来的原始细节/原话，便于分身模仿语气。</summary>
    public string Content { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>涉及的人（家人、朋友、同事、作者等）。</summary>
    public IReadOnlyList<string> People { get; private set; } = [];

    public IReadOnlyList<string> Tags { get; private set; } = [];

    /// <summary>情绪标记，例如“焦虑”“兴奋”；没有则为空。</summary>
    public string Emotion { get; private set; } = string.Empty;

    /// <summary>对“像我”的重要程度 1-5，影响问答检索时的排序。</summary>
    public int Importance { get; private set; } = 3;

    /// <summary>chat 聊天投喂 / upload 文件导入 / auto 自动记录。</summary>
    public string Source { get; private set; } = "chat";

    /// <summary>pending / ready / failed。</summary>
    public string VectorState { get; private set; } = VectorPending;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    // ---- v2：证据链记忆 ----

    /// <summary>记忆类别（比 kind 更稳定）：fact/value/semantic/episodic/procedural/relationship/expression_style/goal_habit。</summary>
    public string MemoryType { get; private set; } = AvatarMemoryTypes.Fact;

    /// <summary>stable 稳定 / phase 阶段性 / situational 情境性。</summary>
    public string Stability { get; private set; } = AvatarMemoryStability.Situational;

    /// <summary>提取或推断的置信度 0-100。</summary>
    public int Confidence { get; private set; } = 60;

    /// <summary>
    /// 生命周期：candidate 候选 / active 有效 / pending_confirmation 待确认 /
    /// historical 历史 / superseded 已被取代 / corrected 已纠正 / archived 已归档。
    /// 与 <see cref="VectorState"/> 完全独立，后者只表示向量化状态。
    /// </summary>
    public string LifecycleStatus { get; private set; } = AvatarMemoryLifecycle.Active;

    public DateTimeOffset? ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }

    /// <summary>这条记忆取代了哪条旧记忆。</summary>
    public Guid? SupersedesId { get; private set; }

    /// <summary>主体/关系/属性/事件细节等结构化信息（JSON 文本，读取时容错）。</summary>
    public string StructuredData { get; private set; } = "{}";

    /// <summary>normal / private / sensitive。</summary>
    public string PrivacyLevel { get; private set; } = AvatarPrivacyLevels.Normal;

    public DateTimeOffset? LastConfirmedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>关联的原文证据条数（由仓储在写入证据后维护）。</summary>
    public int EvidenceCount { get; private set; }

    public static AvatarMemory Create(AvatarMemoryDraft draft, DateTimeOffset now)
    {
        var summary = (draft.Summary ?? string.Empty).Trim();
        var content = (draft.Content ?? string.Empty).Trim();
        if (summary.Length == 0) summary = content;
        if (summary.Length == 0) throw new AvatarValidationException("记忆内容不能为空。");
        if (summary.Length > MaxSummaryLength) summary = summary[..MaxSummaryLength];
        if (content.Length > MaxContentLength) content = content[..MaxContentLength];

        var title = (draft.Title ?? string.Empty).Trim();
        if (title.Length == 0) title = BuildTitle(summary);
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];

        var memory = new AvatarMemory(
            Guid.NewGuid(),
            draft.FragmentId,
            NormalizeKind(draft.Kind),
            title,
            summary,
            content,
            draft.OccurredAt ?? now,
            NormalizeLabels(draft.People, MaxPeopleCount),
            NormalizeLabels(draft.Tags, MaxTagCount),
            Truncate((draft.Emotion ?? string.Empty).Trim(), MaxLabelLength),
            Math.Clamp(draft.Importance <= 0 ? 3 : draft.Importance, 1, 5),
            NormalizeSource(draft.Source),
            VectorPending,
            now,
            now);

        memory.ApplyV2(draft);
        return memory;
    }

    public static AvatarMemory Restore(
        Guid id,
        Guid? fragmentId,
        string kind,
        string title,
        string summary,
        string content,
        DateTimeOffset occurredAt,
        IReadOnlyList<string> people,
        IReadOnlyList<string> tags,
        string emotion,
        int importance,
        string source,
        string vectorState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(id, fragmentId, kind, title, summary, content, occurredAt, people, tags, emotion,
            importance, source, NormalizeVectorState(vectorState), createdAt, updatedAt);

    /// <summary>还原 v2 字段。旧数据缺失时给出安全默认值。</summary>
    public static AvatarMemory RestoreV2(
        Guid id,
        Guid? fragmentId,
        string kind,
        string title,
        string summary,
        string content,
        DateTimeOffset occurredAt,
        IReadOnlyList<string> people,
        IReadOnlyList<string> tags,
        string emotion,
        int importance,
        string source,
        string vectorState,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string memoryType,
        string stability,
        int confidence,
        string lifecycleStatus,
        DateTimeOffset? validFrom,
        DateTimeOffset? validTo,
        Guid? supersedesId,
        string structuredData,
        string privacyLevel,
        DateTimeOffset? lastConfirmedAt,
        DateTimeOffset? lastUsedAt,
        int evidenceCount)
    {
        var memory = new AvatarMemory(
            id,
            fragmentId,
            NormalizeKind(kind),
            title,
            summary,
            content,
            occurredAt,
            NormalizeLabels(people, MaxPeopleCount),
            NormalizeLabels(tags, MaxTagCount),
            emotion,
            importance,
            source,
            NormalizeVectorState(vectorState),
            createdAt,
            updatedAt);

        memory.MemoryType = AvatarMemoryTypes.Normalize(memoryType, memory.Kind);
        memory.Stability = AvatarMemoryStability.Normalize(stability);
        memory.Confidence = Math.Clamp(confidence <= 0 ? 60 : confidence, 0, 100);
        memory.LifecycleStatus = AvatarMemoryLifecycle.Normalize(lifecycleStatus);
        memory.ValidFrom = validFrom ?? occurredAt;
        memory.ValidTo = validTo;
        memory.SupersedesId = supersedesId;
        memory.StructuredData = NormalizeStructuredData(structuredData);
        memory.PrivacyLevel = AvatarPrivacyLevels.Normalize(privacyLevel);
        memory.LastConfirmedAt = lastConfirmedAt;
        memory.LastUsedAt = lastUsedAt;
        memory.EvidenceCount = Math.Max(0, evidenceCount);
        return memory;
    }

    /// <summary>标记向量化结果。仓储层负责持久化。</summary>
    public void MarkVectorState(string state, DateTimeOffset now)
    {
        VectorState = NormalizeVectorState(state);
        UpdatedAt = now;
    }

    /// <summary>变更生命周期。旧记忆只改状态，不删除。</summary>
    public void MarkLifecycle(string status, Guid? supersedesId, DateTimeOffset now)
    {
        LifecycleStatus = AvatarMemoryLifecycle.Normalize(status);
        if (supersedesId is not null) SupersedesId = supersedesId;
        if (LifecycleStatus is AvatarMemoryLifecycle.Historical
            or AvatarMemoryLifecycle.Superseded
            or AvatarMemoryLifecycle.Corrected)
        {
            ValidTo ??= now;
        }

        UpdatedAt = now;
    }

    public void TouchUsed(DateTimeOffset now)
    {
        LastUsedAt = now;
        UpdatedAt = now;
    }

    public void Confirm(DateTimeOffset now)
    {
        LifecycleStatus = AvatarMemoryLifecycle.Active;
        LastConfirmedAt = now;
        UpdatedAt = now;
    }

    public void IncrementEvidenceCount(DateTimeOffset now)
    {
        EvidenceCount++;
        UpdatedAt = now;
    }

    private void ApplyV2(AvatarMemoryDraft draft)
    {
        MemoryType = AvatarMemoryTypes.Normalize(draft.MemoryType, Kind);
        Stability = AvatarMemoryStability.Normalize(draft.Stability);
        Confidence = Math.Clamp(draft.Confidence ?? 60, 0, 100);
        LifecycleStatus = AvatarMemoryLifecycle.Normalize(draft.LifecycleStatus);
        ValidFrom = draft.ValidFrom ?? OccurredAt;
        ValidTo = draft.ValidTo;
        SupersedesId = draft.SupersedesId;
        StructuredData = NormalizeStructuredData(draft.StructuredData);
        PrivacyLevel = AvatarPrivacyLevels.Normalize(draft.PrivacyLevel);
    }

    private static string NormalizeStructuredData(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0) return "{}";
        return normalized.Length > MaxStructuredDataLength
            ? normalized[..MaxStructuredDataLength]
            : normalized;
    }

    /// <summary>可检索文本：标题 + 归纳 + 原话细节。</summary>
    public string ToRetrievalText()
    {
        var parts = new List<string>(3) { Title, Summary };
        if (Content.Length > 0 && !string.Equals(Content, Summary, StringComparison.Ordinal)) parts.Add(Content);
        return string.Join('\n', parts);
    }

    private static string BuildTitle(string summary)
    {
        var firstLine = summary.Split('\n', 2)[0].Trim();
        return firstLine.Length <= 40 ? firstLine : firstLine[..40] + "…";
    }

    private static string NormalizeKind(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return "other";
        return normalized switch
        {
            "fact" or "事实" => "fact",
            "experience" or "经历" or "event" or "事件" => "experience",
            "emotion" or "情绪" or "感受" => "emotion",
            "preference" or "偏好" or "喜欢" or "讨厌" => "preference",
            "opinion" or "观点" or "看法" => "opinion",
            "relationship" or "人际" or "关系" => "relationship",
            "habit" or "习惯" => "habit",
            "skill" or "能力" or "技能" => "skill",
            "knowledge" or "知识" or "见闻" => "knowledge",
            "identity" or "自我" or "性格" => "identity",
            "style" or "语气" or "语言风格" => "style",
            "goal" or "目标" or "计划" => "goal",
            "value" or "价值观" => "value",
            _ => Truncate(normalized, MaxLabelLength)
        };
    }

    private static string NormalizeSource(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "upload" => "upload",
            "auto" => "auto",
            "manual" => "manual",
            _ => "chat"
        };

    private static string NormalizeVectorState(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            VectorReady => VectorReady,
            VectorFailed => VectorFailed,
            _ => VectorPending
        };

    private static IReadOnlyList<string> NormalizeLabels(IEnumerable<string>? values, int maxCount) =>
        (values ?? [])
            .Select(value => (value ?? string.Empty).Trim())
            .Where(value => value.Length is > 0 and <= MaxLabelLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

public sealed record AvatarMemoryDraft(
    Guid? FragmentId,
    string? Kind,
    string? Title,
    string? Summary,
    string? Content,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<string>? People,
    IReadOnlyList<string>? Tags,
    string? Emotion,
    int Importance,
    string? Source,
    string? MemoryType = null,
    string? Stability = null,
    int? Confidence = null,
    string? LifecycleStatus = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidTo = null,
    Guid? SupersedesId = null,
    string? StructuredData = null,
    string? PrivacyLevel = null);

/// <summary>记忆类别。比自由 kind 更稳定，用于按问题类型做结构化召回。</summary>
public static class AvatarMemoryTypes
{
    public const string Fact = "fact";
    public const string Value = "value";
    public const string Semantic = "semantic";
    public const string Episodic = "episodic";
    public const string Procedural = "procedural";
    public const string Relationship = "relationship";
    public const string ExpressionStyle = "expression_style";
    public const string GoalHabit = "goal_habit";

    public static readonly IReadOnlyList<(string Value, string Label)> All =
    [
        (Fact, "事实"),
        (Value, "价值判断"),
        (Semantic, "观点知识"),
        (Episodic, "经历事件"),
        (Procedural, "决策方式"),
        (Relationship, "人际关系"),
        (ExpressionStyle, "表达风格"),
        (GoalHabit, "目标习惯")
    ];

    public static string Normalize(string? value, string? kind = null)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length > 0)
        {
            return normalized switch
            {
                "fact" or "事实" => Fact,
                "value" or "价值观" or "preference" or "偏好" => Value,
                "semantic" or "opinion" or "knowledge" or "观点" or "知识" => Semantic,
                "episodic" or "experience" or "event" or "经历" or "事件" => Episodic,
                "procedural" or "decision_pattern" or "decision" or "决策" => Procedural,
                "relationship" or "人际" or "关系" => Relationship,
                "expression_style" or "style" or "语气" or "表达风格" => ExpressionStyle,
                "goal_habit" or "goal" or "habit" or "目标" or "习惯" => GoalHabit,
                _ => Fact
            };
        }

        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "value" => Value,
            "opinion" or "knowledge" => Semantic,
            "experience" or "event" or "emotion" => Episodic,
            "habit" or "goal" => GoalHabit,
            "relationship" => Relationship,
            "style" => ExpressionStyle,
            "identity" or "preference" => Value,
            _ => Fact
        };
    }

    public static string Label(string? value)
    {
        var normalized = Normalize(value);
        foreach (var item in All)
        {
            if (string.Equals(item.Value, normalized, StringComparison.Ordinal)) return item.Label;
        }
        return "事实";
    }
}

public static class AvatarMemoryStability
{
    public const string Stable = "stable";
    public const string Phase = "phase";
    public const string Situational = "situational";

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "stable" or "稳定" => Stable,
            "phase" or "阶段性" => Phase,
            _ => Situational
        };
}

public static class AvatarMemoryLifecycle
{
    public const string Candidate = "candidate";
    public const string Active = "active";
    public const string PendingConfirmation = "pending_confirmation";
    public const string Historical = "historical";
    public const string Superseded = "superseded";
    public const string Corrected = "corrected";
    public const string Archived = "archived";

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Candidate or "候选" => Candidate,
            PendingConfirmation or "待确认" => PendingConfirmation,
            Historical or "历史" => Historical,
            Superseded or "已被取代" => Superseded,
            Corrected or "已纠正" => Corrected,
            Archived or "已归档" => Archived,
            _ => Active
        };

    public static string Label(string? value) =>
        Normalize(value) switch
        {
            Candidate => "候选",
            PendingConfirmation => "待确认",
            Historical => "历史",
            Superseded => "已被取代",
            Corrected => "已纠正",
            Archived => "已归档",
            _ => "有效"
        };

    /// <summary>当前回答可以默认采用的记忆状态。</summary>
    public static bool IsCurrent(string? value) =>
        Normalize(value) is Active or Candidate or PendingConfirmation;
}

public static class AvatarPrivacyLevels
{
    public const string Normal = "normal";
    public const string Private = "private";
    public const string Sensitive = "sensitive";

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            Private or "私密" => Private,
            Sensitive or "敏感" => Sensitive,
            _ => Normal
        };
}

/// <summary>
/// 记忆类型的展示名。AI 可以自由命名类型，未收录的类型会直接显示原值。
/// </summary>
public static class AvatarMemoryKinds
{
    public static readonly IReadOnlyList<(string Value, string Label)> Suggested =
    [
        ("identity", "自我"),
        ("experience", "经历"),
        ("fact", "事实"),
        ("preference", "偏好"),
        ("opinion", "观点"),
        ("relationship", "人际"),
        ("habit", "习惯"),
        ("skill", "能力"),
        ("knowledge", "见闻"),
        ("emotion", "情绪"),
        ("style", "语气"),
        ("goal", "目标"),
        ("value", "价值观"),
        ("other", "其它")
    ];

    public static string Label(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0) return "其它";
        foreach (var item in Suggested)
        {
            if (string.Equals(item.Value, normalized, StringComparison.Ordinal)) return item.Label;
        }
        return normalized;
    }
}
