using System.Globalization;
using System.Text;
using System.Text.Json;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Models;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 未来轨迹推演服务：把画像、经历、行为与人际关系压缩成一份上下文，
/// 让当前聊天模型扮演“轨迹推演分析师”，输出结构化概率与轨迹报告。
/// </summary>
public sealed class AvatarSimulationService(
    IAvatarRepository repository,
    OpenAiCompatibleModelClient modelClient,
    ILlmSettingsAccessor llmSettings,
    ILogger<AvatarSimulationService> logger)
{
    private const int MaxExperiences = 40;
    private const int MaxBehaviorEntries = 60;
    private static readonly TimeSpan BehaviorWindow = TimeSpan.FromDays(90);

    private const string SystemPrompt = """
        你是一名严谨的个人轨迹推演分析师。你会基于用户提供的真实画像、经历、行为记录和人际关系，
        推演在给定时间跨度内最可能发生的路径，并给出可校准的成功概率。

        要求：
        1. 只使用给定材料，不编造经历、人脉或数据；材料不足时明确指出不确定来源。
        2. 概率必须是 0-100 的整数，并给出置信度；不要给出虚假的精确感，区间越模糊就降低置信度。
        3. 区分“最可能路径”与“理想路径”，说明关键分叉点与触发条件。
        4. 因素要具体到材料中的证据，不要写“努力就会有回报”这类空话。
        5. 建议必须是未来 2-4 周可执行的动作，可验证、可量化。
        6. 全部使用中文。只输出一个 JSON 对象，不要输出解释性文字或 Markdown 代码围栏。

        JSON 结构：
        {
          "summary": "一段 100-200 字的结论",
          "successProbability": 62,
          "confidence": 70,
          "keyFactors": [
            { "name": "因素名称", "direction": "positive|negative", "weight": 30, "reason": "材料中的证据" }
          ],
          "milestones": [
            { "period": "第 1 个月", "action": "该阶段要做的事", "indicator": "可验证的完成标准" }
          ],
          "risks": [
            { "risk": "风险描述", "probability": 40, "mitigation": "应对办法" }
          ],
          "nextActions": ["两周内可执行的动作"],
          "report": "完整的中文 Markdown 报告，包含：现状判断、最可能轨迹、分叉点、风险、行动清单、结论"
        }
        """;

    public async Task<AvatarSimulationDto> RunAsync(AvatarSimulationDraft draft, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = await repository.GetProfileAsync(cancellationToken);
        var experiences = await repository.ListExperiencesAsync(MaxExperiences, cancellationToken);
        var behaviors = await repository.ListBehaviorsAsync(now - BehaviorWindow, now.AddDays(1), 1_000, cancellationToken);
        var relationships = await repository.ListRelationshipsAsync(cancellationToken);
        var fragments = await repository.ListFragmentsAsync(40, cancellationToken);
        var insights = await repository.ListInsightsAsync(cancellationToken);

        var hasMaterial = (profile?.IsConfigured ?? false) || insights.Count > 0 || fragments.Count > 0
                          || experiences.Count > 0 || behaviors.Count > 0;
        if (!hasMaterial)
            throw new AvatarValidationException("请先投喂分身素材（日记、聊天记录、性格自述等）后再推演。");

        var model = llmSettings.Selected.Model;
        var simulation = AvatarSimulation.CreatePending(draft, model, now);
        await repository.AddSimulationAsync(simulation, cancellationToken);

        try
        {
            var userPrompt = BuildPrompt(draft, profile, experiences, behaviors, relationships, fragments, insights);
            var raw = await modelClient.CompleteChatAsync(SystemPrompt, userPrompt, cancellationToken);
            simulation.Complete(ParseResult(raw), model, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Avatar simulation {SimulationId} failed", simulation.Id);
            simulation.Fail(exception.Message, DateTimeOffset.UtcNow);
        }

        await repository.UpdateSimulationAsync(simulation, cancellationToken);
        return AvatarSimulationDto.FromDomain(simulation);
    }

    private static string BuildPrompt(
        AvatarSimulationDraft draft,
        AvatarProfile? profile,
        IReadOnlyList<AvatarExperience> experiences,
        IReadOnlyList<AvatarBehavior> behaviors,
        IReadOnlyList<AvatarRelationship> relationships,
        IReadOnlyList<AvatarFragment> fragments,
        IReadOnlyList<AvatarInsight> insights)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## 推演请求");
        builder.AppendLine($"情景：{draft.Scenario!.Trim()}");
        if (!string.IsNullOrWhiteSpace(draft.Goal)) builder.AppendLine($"期望目标：{draft.Goal.Trim()}");
        builder.AppendLine($"推演跨度：{Math.Clamp(draft.HorizonMonths <= 0 ? 6 : draft.HorizonMonths, 1, 120)} 个月");
        builder.AppendLine();

        builder.AppendLine("## 一、本人画像");
        if (profile is null || !profile.IsConfigured)
        {
            builder.AppendLine("（尚未填写画像，只能依据经历与行为推断，请降低置信度。）");
        }
        else
        {
            AppendField(builder, "称呼", profile.DisplayName);
            AppendField(builder, "一句话定位", profile.Headline);
            AppendField(builder, "性格特质", profile.Personality);
            AppendField(builder, "价值观与动机", profile.Values);
            AppendField(builder, "优势", profile.Strengths);
            AppendField(builder, "短板", profile.Weaknesses);
            AppendField(builder, "技能", profile.Skills);
            AppendField(builder, "可支配资源", profile.Resources);
            AppendField(builder, "现实约束", profile.Constraints);
            AppendField(builder, "长期目标", profile.LongTermGoals);
            AppendField(builder, "风险偏好", AvatarDefaults.DescribeRiskPreference(profile.RiskPreference));
        }
        builder.AppendLine();

        builder.AppendLine("## 二、人格洞察（从碎片素材归纳）");
        if (insights.Count == 0)
        {
            builder.AppendLine("（尚未归纳，建议先运行人格分析。）");
        }
        else
        {
            foreach (var insight in insights)
                builder.AppendLine($"- 【{AvatarInsightDimensions.Label(insight.Dimension)}】{insight.Title}（置信度 {insight.Confidence}）：{insight.Content}");
        }
        builder.AppendLine();

        builder.AppendLine("## 三、碎片素材摘录");
        if (fragments.Count == 0)
        {
            builder.AppendLine("（无碎片素材。）");
        }
        else
        {
            foreach (var fragment in fragments)
            {
                builder.AppendLine($"- [{AvatarFragmentKinds.Label(fragment.Kind)} · {fragment.OccurredAt:yyyy-MM-dd}] {fragment.Title}");
                builder.AppendLine($"  {Truncate(fragment.Content, 300)}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 四、关键经历（由近及远）");
        if (experiences.Count == 0)
        {
            builder.AppendLine("（无经历记录。）");
        }
        else
        {
            foreach (var experience in experiences)
            {
                builder.AppendLine($"- [{experience.OccurredAt:yyyy-MM-dd}][{experience.Category}][{DescribeOutcome(experience.Outcome)}][情绪{DescribeEmotion(experience.Emotion)}][影响{experience.Impact}/5] {experience.Title}");
                if (!string.IsNullOrWhiteSpace(experience.Content))
                    builder.AppendLine($"  {Truncate(experience.Content, 400)}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 五、近 90 天行为统计");
        if (behaviors.Count == 0)
        {
            builder.AppendLine("（无行为记录。）");
        }
        else
        {
            foreach (var group in behaviors
                         .GroupBy(behavior => behavior.Category, StringComparer.Ordinal)
                         .OrderByDescending(group => group.Sum(behavior => behavior.DurationMinutes)))
            {
                var minutes = group.Sum(behavior => behavior.DurationMinutes);
                var moods = group.Where(behavior => behavior.Mood is not null).Select(behavior => behavior.Mood!.Value).ToList();
                var moodText = moods.Count == 0
                    ? "情绪未记录"
                    : $"平均情绪 {moods.Average():F1}/5";
                builder.AppendLine($"- {group.Key}：{group.Count()} 次，累计 {minutes} 分钟（约 {minutes / 60.0:F1} 小时），{moodText}");
            }

            builder.AppendLine();
            builder.AppendLine("近期行为样本：");
            foreach (var behavior in behaviors.Take(MaxBehaviorEntries))
            {
                builder.AppendLine($"- [{behavior.OccurredOn:yyyy-MM-dd}][{behavior.Category}] {Truncate(behavior.Description, 120)}" +
                                   $"{(behavior.DurationMinutes > 0 ? $"（{behavior.DurationMinutes} 分钟）" : string.Empty)}");
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 六、人际关系");
        if (relationships.Count == 0)
        {
            builder.AppendLine("（无关系记录。）");
        }
        else
        {
            foreach (var relationship in relationships)
            {
                builder.AppendLine($"- {relationship.Name}（{relationship.Role}）：亲近 {relationship.Closeness}/5，影响 {relationship.Influence}/5" +
                                   (string.IsNullOrWhiteSpace(relationship.Notes) ? string.Empty : $"；{Truncate(relationship.Notes, 200)}"));
            }
        }
        builder.AppendLine();

        builder.AppendLine("## 七、输出要求");
        builder.AppendLine("严格按系统提示中的 JSON 结构输出；successProbability 针对“情景中最核心的目标在跨度内达成”这一事件。");
        return builder.ToString();
    }

    private static void AppendField(StringBuilder builder, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) builder.AppendLine($"- {label}：{value.Trim()}");
    }

    private static string DescribeOutcome(string outcome) => outcome switch
    {
        "success" => "成功",
        "failure" => "失败",
        _ => "进行中"
    };

    private static string DescribeEmotion(string emotion) => emotion switch
    {
        "positive" => "积极",
        "negative" => "消极",
        "mixed" => "复杂",
        _ => "中性"
    };

    /// <summary>解析模型输出。无法解析为 JSON 时退化为纯文本报告，不抛异常。</summary>
    internal static AvatarSimulationResult ParseResult(string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var json = ExtractJson(text);
        if (json is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var summary = GetString(root, "summary") ?? string.Empty;
                    var keyFactors = ReadArray(root, "keyFactors", element => new AvatarKeyFactor(
                        GetString(element, "name") ?? "未命名因素",
                        NormalizeDirection(GetString(element, "direction")),
                        GetInt(element, "weight") ?? 0,
                        GetString(element, "reason") ?? string.Empty));
                    var milestones = ReadArray(root, "milestones", element => new AvatarMilestone(
                        GetString(element, "period") ?? string.Empty,
                        GetString(element, "action") ?? string.Empty,
                        GetString(element, "indicator") ?? string.Empty));
                    var risks = ReadArray(root, "risks", element => new AvatarRisk(
                        GetString(element, "risk") ?? string.Empty,
                        GetInt(element, "probability") ?? 0,
                        GetString(element, "mitigation") ?? string.Empty));
                    var nextActions = ReadArray(root, "nextActions", element => element.ValueKind == JsonValueKind.String
                        ? element.GetString() ?? string.Empty
                        : GetString(element, "action") ?? string.Empty);

                    var report = GetString(root, "report");
                    if (string.IsNullOrWhiteSpace(report))
                        report = BuildFallbackReport(summary, keyFactors, milestones, risks, nextActions);

                    return new AvatarSimulationResult(
                        summary,
                        report!,
                        GetInt(root, "successProbability"),
                        GetInt(root, "confidence"),
                        keyFactors,
                        milestones,
                        risks,
                        nextActions.Where(item => !string.IsNullOrWhiteSpace(item)).ToList());
                }
            }
            catch (JsonException)
            {
                // 落到纯文本报告分支。
            }
        }

        return AvatarSimulationResult.FromReport(text);
    }

    private static string? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text[start..(end + 1)];
    }

    private static string NormalizeDirection(string? value) =>
        string.Equals(value, "negative", StringComparison.OrdinalIgnoreCase) || value == "负"
            ? "negative"
            : "positive";

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var real) => (int)Math.Round(real),
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static IReadOnlyList<T> ReadArray<T>(JsonElement root, string name, Func<JsonElement, T> factory)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(factory).ToList();
    }

    private static string BuildFallbackReport(
        string summary,
        IReadOnlyList<AvatarKeyFactor> factors,
        IReadOnlyList<AvatarMilestone> milestones,
        IReadOnlyList<AvatarRisk> risks,
        IReadOnlyList<string> nextActions)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(summary)) builder.AppendLine(summary).AppendLine();

        if (factors.Count > 0)
        {
            builder.AppendLine("### 关键因素");
            foreach (var factor in factors)
                builder.AppendLine($"- {(factor.Direction == "negative" ? "↓" : "↑")} {factor.Name}（权重 {factor.Weight}）：{factor.Reason}");
            builder.AppendLine();
        }

        if (milestones.Count > 0)
        {
            builder.AppendLine("### 轨迹节点");
            foreach (var milestone in milestones)
                builder.AppendLine($"- **{milestone.Period}**：{milestone.Action}（完成标准：{milestone.Indicator}）");
            builder.AppendLine();
        }

        if (risks.Count > 0)
        {
            builder.AppendLine("### 风险");
            foreach (var risk in risks)
                builder.AppendLine($"- {risk.Risk}（概率 {risk.Probability}%）：{risk.Mitigation}");
            builder.AppendLine();
        }

        if (nextActions.Count > 0)
        {
            builder.AppendLine("### 下一步");
            foreach (var action in nextActions) builder.AppendLine($"- {action}");
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
