namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>问题类型。不同问题召回不同种类的记忆，避免用同一套检索回答所有问题。</summary>
public enum AvatarQuestionType
{
    Self,
    History,
    Future,
    Relationship,
    Style,
    Fact
}

/// <summary>一次问题的结构化理解：类型、实体、时间意图与需要的记忆类型。</summary>
public sealed record AvatarQueryAnalysis(
    AvatarQuestionType Type,
    string TimeIntent,
    IReadOnlyList<string> NeededMemoryTypes,
    IReadOnlyList<string> Entities)
{
    public bool NeedsHistory => Type == AvatarQuestionType.History || TimeIntent == TimeIntentHistorical;

    public bool NeedsStyle => Type == AvatarQuestionType.Style;

    public bool NeedsDecision => Type is AvatarQuestionType.Future or AvatarQuestionType.History or AvatarQuestionType.Self;

    public const string TimeIntentCurrent = "current";
    public const string TimeIntentHistorical = "historical";
    public const string TimeIntentChange = "change";
}

/// <summary>
/// 轻量问题分类器：规则优先，不额外调用模型。
/// 目标不是完美分类，而是让检索知道“这次该找人格、经历、关系还是表达样本”。
/// </summary>
public static class AvatarQueryClassifier
{
    private static readonly string[] StyleKeywords =
        ["像我", "说话", "语气", "表达", "口吻", "回复", "怎么写", "怎么回", "口头禅", "措辞"];

    private static readonly string[] HistoryKeywords =
        ["以前", "过去", "当时", "曾经", "当时为什么", "为什么", "后来", "那次", "当年", "之前"];

    private static readonly string[] ChangeKeywords =
        ["改变", "变化", "现在不同", "不再", "以前和现在", "后来怎么", "还一样吗"];

    private static readonly string[] FutureKeywords =
        ["会不会", "要不要", "应该", "如果", "未来", "选择", "接不接", "要不要去", "怎么做"];

    private static readonly string[] RelationshipKeywords =
        ["关系", "态度", "信任", "边界", "朋友", "同事", "家人", "对象", "他", "她", "这个人"];

    private static readonly string[] SelfKeywords =
        ["我是什么样", "我是怎样", "我这个人", "我的性格", "我的价值观", "到底是一个", "看重什么", "在意什么"];

    public static AvatarQueryAnalysis Analyze(string? query, IReadOnlyList<string>? knownEntities = null)
    {
        var text = (query ?? string.Empty).Trim();
        var entities = (knownEntities ?? [])
            .Where(entity => !string.IsNullOrWhiteSpace(entity))
            .Where(entity => text.Contains(entity, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var type = Classify(text, entities.Count > 0);
        var timeIntent = type == AvatarQuestionType.History
            ? (ContainsAny(text, ChangeKeywords)
                ? AvatarQueryAnalysis.TimeIntentChange
                : AvatarQueryAnalysis.TimeIntentHistorical)
            : AvatarQueryAnalysis.TimeIntentCurrent;

        return new AvatarQueryAnalysis(type, timeIntent, NeededTypes(type), entities);
    }

    private static AvatarQuestionType Classify(string text, bool hasEntity)
    {
        if (text.Length == 0) return AvatarQuestionType.Fact;
        if (ContainsAny(text, StyleKeywords)) return AvatarQuestionType.Style;
        if (ContainsAny(text, HistoryKeywords) || ContainsAny(text, ChangeKeywords)) return AvatarQuestionType.History;
        if (ContainsAny(text, FutureKeywords)) return AvatarQuestionType.Future;
        if (ContainsAny(text, RelationshipKeywords) || hasEntity) return AvatarQuestionType.Relationship;
        if (ContainsAny(text, SelfKeywords)) return AvatarQuestionType.Self;
        return AvatarQuestionType.Fact;
    }

    private static IReadOnlyList<string> NeededTypes(AvatarQuestionType type) => type switch
    {
        AvatarQuestionType.Style =>
        [
            Domain.Avatar.AvatarMemoryTypes.ExpressionStyle,
            Domain.Avatar.AvatarMemoryTypes.Episodic,
            Domain.Avatar.AvatarMemoryTypes.Value
        ],
        AvatarQuestionType.History =>
        [
            Domain.Avatar.AvatarMemoryTypes.Episodic,
            Domain.Avatar.AvatarMemoryTypes.Procedural,
            Domain.Avatar.AvatarMemoryTypes.Value,
            Domain.Avatar.AvatarMemoryTypes.Relationship
        ],
        AvatarQuestionType.Future =>
        [
            Domain.Avatar.AvatarMemoryTypes.Procedural,
            Domain.Avatar.AvatarMemoryTypes.GoalHabit,
            Domain.Avatar.AvatarMemoryTypes.Value,
            Domain.Avatar.AvatarMemoryTypes.Episodic,
            Domain.Avatar.AvatarMemoryTypes.Relationship
        ],
        AvatarQuestionType.Relationship =>
        [
            Domain.Avatar.AvatarMemoryTypes.Relationship,
            Domain.Avatar.AvatarMemoryTypes.Episodic,
            Domain.Avatar.AvatarMemoryTypes.Value
        ],
        AvatarQuestionType.Self =>
        [
            Domain.Avatar.AvatarMemoryTypes.Value,
            Domain.Avatar.AvatarMemoryTypes.Semantic,
            Domain.Avatar.AvatarMemoryTypes.Procedural,
            Domain.Avatar.AvatarMemoryTypes.GoalHabit,
            Domain.Avatar.AvatarMemoryTypes.ExpressionStyle
        ],
        _ =>
        [
            Domain.Avatar.AvatarMemoryTypes.Fact,
            Domain.Avatar.AvatarMemoryTypes.Semantic,
            Domain.Avatar.AvatarMemoryTypes.Episodic,
            Domain.Avatar.AvatarMemoryTypes.Relationship,
            Domain.Avatar.AvatarMemoryTypes.Value
        ]
    };

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
