namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>分身输入校验失败。</summary>
public sealed class AvatarValidationException(string message) : Exception(message);

/// <summary>
/// 用户分身的基础画像聚合根。整个部署只有一份画像，用于描述“我是谁”。
/// </summary>
public sealed class AvatarProfile
{
    /// <summary>单例主键：一个部署只维护一份用户分身画像。</summary>
    public static readonly Guid SingletonId = new("5f1d7c2e-9a44-4d1e-8f60-2b7f7a3c9d10");

    public const int MaxNameLength = 60;
    public const int MaxHeadlineLength = 200;
    public const int MaxTextLength = 8_000;

    private AvatarProfile()
    {
    }

    private AvatarProfile(
        Guid id,
        string displayName,
        string headline,
        string personality,
        string values,
        string strengths,
        string weaknesses,
        string skills,
        string resources,
        string constraints,
        string longTermGoals,
        string riskPreference,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        Id = id;
        DisplayName = displayName;
        Headline = headline;
        Personality = personality;
        Values = values;
        Strengths = strengths;
        Weaknesses = weaknesses;
        Skills = skills;
        Resources = resources;
        Constraints = constraints;
        LongTermGoals = longTermGoals;
        RiskPreference = riskPreference;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>一句话自我定位，例如“白天上班、晚上写网文的兼职作者”。</summary>
    public string Headline { get; private set; } = string.Empty;

    /// <summary>性格特质：内外向、情绪稳定性、自驱力、拖延倾向等。</summary>
    public string Personality { get; private set; } = string.Empty;

    /// <summary>价值观与核心动机：为什么做选择。</summary>
    public string Values { get; private set; } = string.Empty;

    public string Strengths { get; private set; } = string.Empty;
    public string Weaknesses { get; private set; } = string.Empty;

    /// <summary>技能与经验积累，例如“能独立完成 20 万字长篇”。</summary>
    public string Skills { get; private set; } = string.Empty;

    /// <summary>可支配资源：时间、金钱、人脉、设备、平台账号等。</summary>
    public string Resources { get; private set; } = string.Empty;

    /// <summary>现实约束：工作强度、家庭责任、健康、地域限制。</summary>
    public string Constraints { get; private set; } = string.Empty;

    public string LongTermGoals { get; private set; } = string.Empty;

    /// <summary>风险偏好：conservative / balanced / aggressive。</summary>
    public string RiskPreference { get; private set; } = AvatarDefaults.RiskPreference;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(DisplayName)
                                || !string.IsNullOrWhiteSpace(Headline)
                                || !string.IsNullOrWhiteSpace(Personality);

    public static AvatarProfile Empty(DateTimeOffset now) =>
        new(SingletonId, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, AvatarDefaults.RiskPreference, now, now);

    public static AvatarProfile Restore(
        Guid id,
        string displayName,
        string headline,
        string personality,
        string values,
        string strengths,
        string weaknesses,
        string skills,
        string resources,
        string constraints,
        string longTermGoals,
        string riskPreference,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt) =>
        new(
            id == Guid.Empty ? SingletonId : id,
            displayName ?? string.Empty,
            headline ?? string.Empty,
            personality ?? string.Empty,
            values ?? string.Empty,
            strengths ?? string.Empty,
            weaknesses ?? string.Empty,
            skills ?? string.Empty,
            resources ?? string.Empty,
            constraints ?? string.Empty,
            longTermGoals ?? string.Empty,
            AvatarDefaults.NormalizeRiskPreference(riskPreference),
            createdAt,
            updatedAt);

    public void Update(AvatarProfileDraft draft, DateTimeOffset now)
    {
        DisplayName = NormalizeRequired(draft.DisplayName, "分身名称", MaxNameLength);
        Headline = NormalizeOptional(draft.Headline, "一句话定位", MaxHeadlineLength);
        Personality = NormalizeOptional(draft.Personality, "性格特质", MaxTextLength);
        Values = NormalizeOptional(draft.Values, "价值观与动机", MaxTextLength);
        Strengths = NormalizeOptional(draft.Strengths, "优势", MaxTextLength);
        Weaknesses = NormalizeOptional(draft.Weaknesses, "短板", MaxTextLength);
        Skills = NormalizeOptional(draft.Skills, "技能", MaxTextLength);
        Resources = NormalizeOptional(draft.Resources, "资源", MaxTextLength);
        Constraints = NormalizeOptional(draft.Constraints, "现实约束", MaxTextLength);
        LongTermGoals = NormalizeOptional(draft.LongTermGoals, "长期目标", MaxTextLength);
        RiskPreference = AvatarDefaults.NormalizeRiskPreference(draft.RiskPreference);
        UpdatedAt = now;
    }

    private static string NormalizeRequired(string? value, string field, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0) throw new AvatarValidationException($"{field}不能为空。");
        if (trimmed.Length > maxLength) throw new AvatarValidationException($"{field}不能超过 {maxLength} 个字符。");
        return trimmed;
    }

    private static string NormalizeOptional(string? value, string field, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length > maxLength) throw new AvatarValidationException($"{field}不能超过 {maxLength} 个字符。");
        return trimmed;
    }
}

public sealed record AvatarProfileDraft(
    string? DisplayName,
    string? Headline,
    string? Personality,
    string? Values,
    string? Strengths,
    string? Weaknesses,
    string? Skills,
    string? Resources,
    string? Constraints,
    string? LongTermGoals,
    string? RiskPreference);

public static class AvatarDefaults
{
    public const string RiskPreference = "balanced";

    public static string NormalizeRiskPreference(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "conservative" or "保守" => "conservative",
            "aggressive" or "激进" => "aggressive",
            _ => RiskPreference
        };

    public static string DescribeRiskPreference(string value) => value switch
    {
        "conservative" => "保守（优先确定性，接受较慢进展）",
        "aggressive" => "激进（愿意承担高不确定性换取高回报）",
        _ => "稳健（在可控风险下追求稳定推进）"
    };

    public static readonly IReadOnlyList<string> ExperienceCategories =
        ["成长", "学习", "工作", "项目", "挫折", "关系", "健康", "家庭", "其它"];

    public static readonly IReadOnlyList<string> BehaviorCategories =
        ["作息", "学习", "工作", "创作", "运动", "社交", "娱乐", "拖延", "情绪", "消费", "其它"];

    public static readonly IReadOnlyList<string> RelationshipRoles =
        ["家人", "朋友", "同事", "导师", "伴侣", "合作方", "其它"];
}
