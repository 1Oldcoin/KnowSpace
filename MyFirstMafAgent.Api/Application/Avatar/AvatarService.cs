using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

public sealed record AvatarProfileDto(
    Guid Id,
    string DisplayName,
    string Headline,
    string Personality,
    string Values,
    string Strengths,
    string Weaknesses,
    string Skills,
    string Resources,
    string Constraints,
    string LongTermGoals,
    string RiskPreference,
    string RiskPreferenceLabel,
    bool IsConfigured,
    DateTimeOffset UpdatedAt)
{
    public static AvatarProfileDto FromDomain(AvatarProfile profile) => new(
        profile.Id,
        profile.DisplayName,
        profile.Headline,
        profile.Personality,
        profile.Values,
        profile.Strengths,
        profile.Weaknesses,
        profile.Skills,
        profile.Resources,
        profile.Constraints,
        profile.LongTermGoals,
        profile.RiskPreference,
        AvatarDefaults.DescribeRiskPreference(profile.RiskPreference),
        profile.IsConfigured,
        profile.UpdatedAt);
}

public sealed record AvatarFragmentDto(
    Guid Id,
    string Kind,
    string KindLabel,
    string Title,
    string Content,
    DateTimeOffset OccurredAt,
    string Source,
    IReadOnlyList<string> Tags,
    bool IsDigested)
{
    public static AvatarFragmentDto FromDomain(AvatarFragment fragment) => new(
        fragment.Id,
        fragment.Kind,
        AvatarFragmentKinds.Label(fragment.Kind),
        fragment.Title,
        fragment.Content,
        fragment.OccurredAt,
        fragment.Source,
        fragment.Tags,
        fragment.IsDigested);
}

public sealed record AvatarInsightDto(
    Guid Id,
    string Dimension,
    string DimensionLabel,
    string Title,
    string Content,
    int Confidence,
    int EvidenceCount,
    IReadOnlyList<string> Evidence,
    DateTimeOffset UpdatedAt)
{
    public static AvatarInsightDto FromDomain(AvatarInsight insight) => new(
        insight.Id,
        insight.Dimension,
        AvatarInsightDimensions.Label(insight.Dimension),
        insight.Title,
        insight.Content,
        insight.Confidence,
        insight.EvidenceCount,
        insight.Evidence,
        insight.UpdatedAt);
}

public sealed record AvatarChatMessageDto(
    long Id,
    string Role,
    string Content,
    IReadOnlyList<AvatarChatCitation> Citations,
    DateTimeOffset CreatedAt)
{
    public static AvatarChatMessageDto FromDomain(AvatarChatMessage message) => new(
        message.Id,
        message.Role,
        message.Content,
        message.Citations,
        message.CreatedAt);
}

public sealed record AvatarChatSessionDto(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static AvatarChatSessionDto FromDomain(AvatarChatSession session) => new(
        session.Id,
        session.Title,
        session.CreatedAt,
        session.UpdatedAt);
}

public sealed record AvatarExperienceDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Category,
    string Title,
    string Content,
    string Outcome,
    string Emotion,
    int Impact,
    IReadOnlyList<string> Tags)
{
    public static AvatarExperienceDto FromDomain(AvatarExperience experience) => new(
        experience.Id,
        experience.OccurredAt,
        experience.Category,
        experience.Title,
        experience.Content,
        experience.Outcome,
        experience.Emotion,
        experience.Impact,
        experience.Tags);
}

public sealed record AvatarBehaviorDto(
    Guid Id,
    DateTimeOffset OccurredOn,
    string Category,
    string Description,
    int DurationMinutes,
    int? Mood,
    IReadOnlyList<string> Tags,
    string Source)
{
    public static AvatarBehaviorDto FromDomain(AvatarBehavior behavior) => new(
        behavior.Id,
        behavior.OccurredOn,
        behavior.Category,
        behavior.Description,
        behavior.DurationMinutes,
        behavior.Mood,
        behavior.Tags,
        behavior.Source);
}

public sealed record AvatarRelationshipDto(
    Guid Id,
    string Name,
    string Role,
    int Closeness,
    int Influence,
    string Notes)
{
    public static AvatarRelationshipDto FromDomain(AvatarRelationship relationship) => new(
        relationship.Id,
        relationship.Name,
        relationship.Role,
        relationship.Closeness,
        relationship.Influence,
        relationship.Notes);
}

public sealed record AvatarSimulationDto(
    Guid Id,
    string Scenario,
    string Goal,
    int HorizonMonths,
    string Status,
    int? SuccessProbability,
    int? Confidence,
    string Summary,
    string Report,
    IReadOnlyList<AvatarKeyFactor> KeyFactors,
    IReadOnlyList<AvatarMilestone> Milestones,
    IReadOnlyList<AvatarRisk> Risks,
    IReadOnlyList<string> NextActions,
    string Model,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static AvatarSimulationDto FromDomain(AvatarSimulation simulation) => new(
        simulation.Id,
        simulation.Scenario,
        simulation.Goal,
        simulation.HorizonMonths,
        simulation.Status,
        simulation.SuccessProbability,
        simulation.Confidence,
        simulation.Summary,
        simulation.Report,
        simulation.KeyFactors,
        simulation.Milestones,
        simulation.Risks,
        simulation.NextActions,
        simulation.Model,
        simulation.ErrorMessage,
        simulation.CreatedAt,
        simulation.CompletedAt);
}

public sealed record AvatarBehaviorStat(string Category, int Minutes, int Count);

/// <summary>分身首页数据：记忆、洞察与对话概览。</summary>
public sealed record AvatarOverviewDto(
    AvatarProfileDto Profile,
    int FragmentCount,
    int UndigestedCount,
    int InsightCount,
    int ChatSessionCount,
    int ReadinessScore,
    IReadOnlyList<string> MissingInputs,
    IReadOnlyList<AvatarFragmentDto> RecentFragments,
    IReadOnlyList<AvatarInsightDto> Insights,
    IReadOnlyList<AvatarChatSessionDto> ChatSessions,
    IReadOnlyList<AvatarBehaviorStat> BehaviorStats,
    int MemoryCount,
    int PendingVectorCount,
    IReadOnlyList<AvatarMemoryDto> RecentMemories,
    IReadOnlyList<AvatarFeedEntryDto> FeedEntries);

/// <summary>分身的应用服务：碎片素材、人格洞察、对话会话与基础画像。</summary>
public sealed class AvatarService(IAvatarRepository repository)
{
    private static readonly TimeSpan BehaviorWindow = TimeSpan.FromDays(90);

    public async Task<AvatarOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = await repository.GetProfileAsync(cancellationToken) ?? AvatarProfile.Empty(now);
        var fragments = await repository.ListFragmentsAsync(50, cancellationToken);
        var insights = await repository.ListInsightsAsync(cancellationToken);
        var sessions = await repository.ListChatSessionsAsync(cancellationToken);
        var behaviors = await repository.ListBehaviorsAsync(now - BehaviorWindow, now.AddDays(1), 1_000, cancellationToken);

        var fragmentCount = await repository.CountFragmentsAsync(cancellationToken);
        var undigested = await repository.CountUndigestedFragmentsAsync(cancellationToken);
        var memories = await repository.ListMemoriesForRetrievalAsync(30, false, cancellationToken);
        var memoryCount = await repository.CountMemoriesAsync(cancellationToken);
        var pendingVectors = await repository.CountPendingMemoryVectorsAsync(cancellationToken);
        var feedEntries = await repository.ListFeedEntriesAsync(60, cancellationToken);

        var stats = behaviors
            .GroupBy(behavior => behavior.Category, StringComparer.Ordinal)
            .Select(group => new AvatarBehaviorStat(
                group.Key,
                group.Sum(behavior => behavior.DurationMinutes),
                group.Count()))
            .OrderByDescending(stat => stat.Minutes)
            .ThenByDescending(stat => stat.Count)
            .ToList();

        // 记忆是投喂后的有效单位；旧版只有碎片时也计入完备度。
        var (readiness, missing) = EvaluateReadiness(
            profile, Math.Max(fragmentCount, memoryCount), insights.Count, sessions.Count);

        return new AvatarOverviewDto(
            AvatarProfileDto.FromDomain(profile),
            fragmentCount,
            undigested,
            insights.Count,
            sessions.Count,
            readiness,
            missing,
            fragments.Select(AvatarFragmentDto.FromDomain).ToList(),
            insights.Select(AvatarInsightDto.FromDomain).ToList(),
            sessions.Select(AvatarChatSessionDto.FromDomain).ToList(),
            stats,
            memoryCount,
            pendingVectors,
            memories.Select(AvatarMemoryDto.FromDomain).ToList(),
            feedEntries.Select(AvatarFeedEntryDto.FromDomain).ToList());
    }

    public async Task<AvatarProfileDto> SaveProfileAsync(AvatarProfileDraft draft, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = await repository.GetProfileAsync(cancellationToken) ?? AvatarProfile.Empty(now);
        profile.Update(draft, now);
        await repository.SaveProfileAsync(profile, cancellationToken);
        return AvatarProfileDto.FromDomain(profile);
    }

    // ---- 碎片素材 ----

    public async Task<IReadOnlyList<AvatarFragmentDto>> ListFragmentsAsync(int limit, CancellationToken cancellationToken) =>
        (await repository.ListFragmentsAsync(limit, cancellationToken))
        .Select(AvatarFragmentDto.FromDomain)
        .ToList();

    public async Task<AvatarFragmentDto> AddFragmentAsync(AvatarFragmentDraft draft, CancellationToken cancellationToken)
    {
        var fragment = AvatarFragment.Create(draft, DateTimeOffset.UtcNow);
        await repository.AddFragmentsAsync([fragment], cancellationToken);
        return AvatarFragmentDto.FromDomain(fragment);
    }

    /// <summary>批量投喂：上传的日记/聊天记录按段落切分成多条碎片。</summary>
    public async Task<IReadOnlyList<AvatarFragmentDto>> AddFragmentsAsync(
        string? kind,
        IReadOnlyList<string> contents,
        DateTimeOffset? occurredAt,
        string source,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var fragments = contents
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .Select(content => AvatarFragment.Create(
                new AvatarFragmentDraft(kind, null, content, occurredAt, source, null),
                now,
                source))
            .ToList();

        if (fragments.Count == 0) throw new AvatarValidationException("没有可投喂的内容。");

        await repository.AddFragmentsAsync(fragments, cancellationToken);
        return fragments.Select(AvatarFragmentDto.FromDomain).ToList();
    }

    public Task<bool> DeleteFragmentAsync(Guid fragmentId, CancellationToken cancellationToken) =>
        repository.DeleteFragmentAsync(fragmentId, cancellationToken);

    // ---- 人格洞察 ----

    public async Task<IReadOnlyList<AvatarInsightDto>> ListInsightsAsync(CancellationToken cancellationToken) =>
        (await repository.ListInsightsAsync(cancellationToken))
        .Select(AvatarInsightDto.FromDomain)
        .ToList();

    public Task ClearInsightsAsync(CancellationToken cancellationToken) =>
        repository.ClearInsightsAsync(cancellationToken);

    // ---- 对话会话 ----

    public async Task<IReadOnlyList<AvatarChatSessionDto>> ListChatSessionsAsync(CancellationToken cancellationToken) =>
        (await repository.ListChatSessionsAsync(cancellationToken))
        .Select(AvatarChatSessionDto.FromDomain)
        .ToList();

    public async Task<AvatarChatSessionDto> CreateChatSessionAsync(string? title, CancellationToken cancellationToken)
    {
        var session = AvatarChatSession.Create(title, DateTimeOffset.UtcNow);
        await repository.AddChatSessionAsync(session, cancellationToken);
        return AvatarChatSessionDto.FromDomain(session);
    }

    public async Task<IReadOnlyList<AvatarChatMessageDto>?> ListChatMessagesAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        if (await repository.GetChatSessionAsync(sessionId, cancellationToken) is null) return null;
        return (await repository.ListChatMessagesAsync(sessionId, 200, cancellationToken))
            .Select(AvatarChatMessageDto.FromDomain)
            .ToList();
    }

    public Task<bool> DeleteChatSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        repository.DeleteChatSessionAsync(sessionId, cancellationToken);

    // ---- 补充输入：经历 / 行为 / 关系 ----

    public async Task<IReadOnlyList<AvatarExperienceDto>> ListExperiencesAsync(int limit, CancellationToken cancellationToken) =>
        (await repository.ListExperiencesAsync(limit, cancellationToken))
        .Select(AvatarExperienceDto.FromDomain)
        .ToList();

    public async Task<AvatarExperienceDto> AddExperienceAsync(AvatarExperienceDraft draft, CancellationToken cancellationToken)
    {
        var experience = AvatarExperience.Create(draft, DateTimeOffset.UtcNow);
        await repository.AddExperienceAsync(experience, cancellationToken);
        return AvatarExperienceDto.FromDomain(experience);
    }

    public Task<bool> DeleteExperienceAsync(Guid experienceId, CancellationToken cancellationToken) =>
        repository.DeleteExperienceAsync(experienceId, cancellationToken);

    public async Task<IReadOnlyList<AvatarBehaviorDto>> ListBehaviorsAsync(int days, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var window = TimeSpan.FromDays(Math.Clamp(days, 1, 365));
        return (await repository.ListBehaviorsAsync(now - window, now.AddDays(1), 1_000, cancellationToken))
            .Select(AvatarBehaviorDto.FromDomain)
            .ToList();
    }

    public async Task<AvatarBehaviorDto> AddBehaviorAsync(AvatarBehaviorDraft draft, CancellationToken cancellationToken)
    {
        var behavior = AvatarBehavior.Create(draft, DateTimeOffset.UtcNow);
        await repository.AddBehaviorsAsync([behavior], cancellationToken);
        return AvatarBehaviorDto.FromDomain(behavior);
    }

    public Task<bool> DeleteBehaviorAsync(Guid behaviorId, CancellationToken cancellationToken) =>
        repository.DeleteBehaviorAsync(behaviorId, cancellationToken);

    public async Task<IReadOnlyList<AvatarRelationshipDto>> ListRelationshipsAsync(CancellationToken cancellationToken) =>
        (await repository.ListRelationshipsAsync(cancellationToken))
        .Select(AvatarRelationshipDto.FromDomain)
        .ToList();

    public async Task<AvatarRelationshipDto> AddRelationshipAsync(AvatarRelationshipDraft draft, CancellationToken cancellationToken)
    {
        var relationship = AvatarRelationship.Create(draft, DateTimeOffset.UtcNow);
        await repository.AddRelationshipAsync(relationship, cancellationToken);
        return AvatarRelationshipDto.FromDomain(relationship);
    }

    public async Task<AvatarRelationshipDto?> UpdateRelationshipAsync(
        Guid relationshipId,
        AvatarRelationshipDraft draft,
        CancellationToken cancellationToken)
    {
        var relationships = await repository.ListRelationshipsAsync(cancellationToken);
        var relationship = relationships.FirstOrDefault(item => item.Id == relationshipId);
        if (relationship is null) return null;

        relationship.Update(draft, DateTimeOffset.UtcNow);
        return await repository.UpdateRelationshipAsync(relationship, cancellationToken)
            ? AvatarRelationshipDto.FromDomain(relationship)
            : null;
    }

    public Task<bool> DeleteRelationshipAsync(Guid relationshipId, CancellationToken cancellationToken) =>
        repository.DeleteRelationshipAsync(relationshipId, cancellationToken);

    // ---- 推演（保留能力） ----

    public async Task<IReadOnlyList<AvatarSimulationDto>> ListSimulationsAsync(int limit, CancellationToken cancellationToken) =>
        (await repository.ListSimulationsAsync(limit, cancellationToken))
        .Select(AvatarSimulationDto.FromDomain)
        .ToList();

    public async Task<AvatarSimulationDto?> GetSimulationAsync(Guid simulationId, CancellationToken cancellationToken)
    {
        var simulation = await repository.GetSimulationAsync(simulationId, cancellationToken);
        return simulation is null ? null : AvatarSimulationDto.FromDomain(simulation);
    }

    public Task<bool> DeleteSimulationAsync(Guid simulationId, CancellationToken cancellationToken) =>
        repository.DeleteSimulationAsync(simulationId, cancellationToken);

    /// <summary>素材完备度（0-100）：记忆 45、人格洞察 25、画像 20、对话 10。</summary>
    public static (int Score, IReadOnlyList<string> Missing) EvaluateReadiness(
        AvatarProfile profile,
        int fragmentCount,
        int insightCount,
        int chatCount)
    {
        var score = 0;
        var missing = new List<string>();

        if (fragmentCount >= 10) score += 45;
        else if (fragmentCount > 0)
        {
            score += fragmentCount * 4;
            missing.Add($"再多喂 {10 - fragmentCount} 段内容（随手写或导入文件都行），记忆会更完整。");
        }
        else
        {
            missing.Add("先喂点什么：一段日记、一段聊天记录，或直接说说你的性格与习惯。");
        }

        if (insightCount >= 5) score += 25;
        else if (insightCount > 0)
        {
            score += insightCount * 5;
            missing.Add("继续投喂，人格画像会自动变得更完整。");
        }
        else
        {
            missing.Add("投喂后 AI 会自动归纳你的人格，不需要手动分析。");
        }

        if (profile.IsConfigured) score += 20;
        else missing.Add("填写称呼与一句话定位，分身回答会更像你。");

        if (chatCount > 0) score += 10;

        return (Math.Clamp(score, 0, 100), missing);
    }
}
