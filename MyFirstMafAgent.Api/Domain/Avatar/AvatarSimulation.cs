namespace MyFirstMafAgent.Api.Domain.Avatar;

/// <summary>一次“未来轨迹推演”的结果聚合。</summary>
public sealed class AvatarSimulation
{
    public const int MaxScenarioLength = 2_000;
    public const int MaxGoalLength = 500;
    public const int MaxReportLength = 60_000;

    private AvatarSimulation()
    {
    }

    private AvatarSimulation(
        Guid id,
        string scenario,
        string goal,
        int horizonMonths,
        string status,
        int? successProbability,
        int? confidence,
        string summary,
        string report,
        IReadOnlyList<AvatarKeyFactor> keyFactors,
        IReadOnlyList<AvatarMilestone> milestones,
        IReadOnlyList<AvatarRisk> risks,
        IReadOnlyList<string> nextActions,
        string model,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt)
    {
        Id = id;
        Scenario = scenario;
        Goal = goal;
        HorizonMonths = horizonMonths;
        Status = status;
        SuccessProbability = successProbability;
        Confidence = confidence;
        Summary = summary;
        Report = report;
        KeyFactors = keyFactors;
        Milestones = milestones;
        Risks = risks;
        NextActions = nextActions;
        Model = model;
        ErrorMessage = errorMessage;
        CreatedAt = createdAt;
        CompletedAt = completedAt;
    }

    public Guid Id { get; private set; }
    public string Scenario { get; private set; } = string.Empty;
    public string Goal { get; private set; } = string.Empty;
    public int HorizonMonths { get; private set; }

    /// <summary>running / completed / failed。</summary>
    public string Status { get; private set; } = "running";

    /// <summary>推演的成功率百分比 0-100。</summary>
    public int? SuccessProbability { get; private set; }

    /// <summary>模型自评置信度百分比 0-100。</summary>
    public int? Confidence { get; private set; }

    public string Summary { get; private set; } = string.Empty;
    public string Report { get; private set; } = string.Empty;
    public IReadOnlyList<AvatarKeyFactor> KeyFactors { get; private set; } = [];
    public IReadOnlyList<AvatarMilestone> Milestones { get; private set; } = [];
    public IReadOnlyList<AvatarRisk> Risks { get; private set; } = [];
    public IReadOnlyList<string> NextActions { get; private set; } = [];
    public string Model { get; private set; } = string.Empty;
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static AvatarSimulation CreatePending(
        AvatarSimulationDraft draft,
        string model,
        DateTimeOffset now)
    {
        var scenario = (draft.Scenario ?? string.Empty).Trim();
        if (scenario.Length == 0) throw new AvatarValidationException("推演情景不能为空。");
        if (scenario.Length > MaxScenarioLength)
            throw new AvatarValidationException($"推演情景不能超过 {MaxScenarioLength} 个字符。");

        var goal = (draft.Goal ?? string.Empty).Trim();
        if (goal.Length > MaxGoalLength) throw new AvatarValidationException($"推演目标不能超过 {MaxGoalLength} 个字符。");

        return new AvatarSimulation(
            Guid.NewGuid(),
            scenario,
            goal,
            Math.Clamp(draft.HorizonMonths <= 0 ? 6 : draft.HorizonMonths, 1, 120),
            "running",
            null,
            null,
            string.Empty,
            string.Empty,
            [],
            [],
            [],
            [],
            model,
            null,
            now,
            null);
    }

    public void Complete(AvatarSimulationResult result, string model, DateTimeOffset now)
    {
        Status = "completed";
        SuccessProbability = result.SuccessProbability is null ? null : Math.Clamp(result.SuccessProbability.Value, 0, 100);
        Confidence = result.Confidence is null ? null : Math.Clamp(result.Confidence.Value, 0, 100);
        Summary = Trim(result.Summary, 2_000);
        Report = Trim(result.Report, MaxReportLength);
        KeyFactors = result.KeyFactors.Take(12).ToList();
        Milestones = result.Milestones.Take(24).ToList();
        Risks = result.Risks.Take(12).ToList();
        NextActions = result.NextActions
            .Select(item => (item ?? string.Empty).Trim())
            .Where(item => item.Length > 0)
            .Take(12)
            .ToList();
        Model = model;
        ErrorMessage = null;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = "failed";
        ErrorMessage = Trim(error, 2_000);
        CompletedAt = now;
    }

    public static AvatarSimulation Restore(
        Guid id,
        string scenario,
        string goal,
        int horizonMonths,
        string status,
        int? successProbability,
        int? confidence,
        string summary,
        string report,
        IReadOnlyList<AvatarKeyFactor> keyFactors,
        IReadOnlyList<AvatarMilestone> milestones,
        IReadOnlyList<AvatarRisk> risks,
        IReadOnlyList<string> nextActions,
        string model,
        string? errorMessage,
        DateTimeOffset createdAt,
        DateTimeOffset? completedAt) =>
        new(id, scenario, goal, horizonMonths, status, successProbability, confidence, summary, report,
            keyFactors, milestones, risks, nextActions, model, errorMessage, createdAt, completedAt);

    private static string Trim(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

public sealed record AvatarSimulationDraft(string? Scenario, string? Goal, int HorizonMonths);

public sealed record AvatarKeyFactor(string Name, string Direction, int Weight, string Reason);

public sealed record AvatarMilestone(string Period, string Action, string Indicator);

public sealed record AvatarRisk(string Risk, int Probability, string Mitigation);

public sealed record AvatarSimulationResult(
    string Summary,
    string Report,
    int? SuccessProbability,
    int? Confidence,
    IReadOnlyList<AvatarKeyFactor> KeyFactors,
    IReadOnlyList<AvatarMilestone> Milestones,
    IReadOnlyList<AvatarRisk> Risks,
    IReadOnlyList<string> NextActions)
{
    public static AvatarSimulationResult FromReport(string report) =>
        new(string.Empty, report, null, null, [], [], [], []);
}
