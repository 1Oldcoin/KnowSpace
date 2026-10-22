using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 人格归纳：输入多条相关记忆及证据，输出候选洞察。
/// 与现有洞察一致时增强证据；矛盾时只写版本，不覆盖当前快照；
/// 只有证据一致且置信度足够，或用户明确确认，才升级为稳定人格。
/// </summary>
public sealed class AvatarInsightService(
    IAvatarRepository repository,
    ILogger<AvatarInsightService> logger)
{
    public const int PromoteConfidenceThreshold = 75;
    public const int PromoteEvidenceThreshold = 2;

    public async Task<AvatarInsightVersion> ProposeAsync(
        AvatarInsight candidate,
        IReadOnlyList<Guid> evidenceMemoryIds,
        string? scope,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var distinctEvidence = evidenceMemoryIds.Where(id => id != Guid.Empty).Distinct().ToList();

        // 同一标题、同一结论的候选不重复堆积。
        var existingCandidates = await repository.ListInsightVersionsAsync(
            AvatarInsightVersion.StatusCandidate, 200, cancellationToken);
        var duplicate = existingCandidates.FirstOrDefault(version =>
            version.Dimension == candidate.Dimension
            && string.Equals(version.Title, candidate.Title, StringComparison.Ordinal)
            && string.Equals(version.Content, candidate.Content, StringComparison.Ordinal));
        if (duplicate is not null) return duplicate;

        var currentInsights = await repository.ListInsightsAsync(cancellationToken);
        var match = currentInsights.FirstOrDefault(insight =>
            insight.Dimension == candidate.Dimension
            && string.Equals(insight.Title, candidate.Title, StringComparison.Ordinal));

        var promote = candidate.Confidence >= PromoteConfidenceThreshold
            && distinctEvidence.Count >= PromoteEvidenceThreshold;

        var version = AvatarInsightVersion.Create(
            candidate,
            distinctEvidence,
            scope,
            promote ? AvatarInsightVersion.StatusConfirmed : AvatarInsightVersion.StatusCandidate,
            match?.Id,
            now);

        await repository.AddInsightVersionAsync(version, cancellationToken);

        if (promote)
        {
            await ApplyToSnapshotAsync(version, candidate, distinctEvidence, cancellationToken);
            logger.LogInformation(
                "Avatar insight promoted: {Dimension}/{Title} (confidence {Confidence})",
                candidate.Dimension, candidate.Title, candidate.Confidence);
        }

        return version;
    }

    /// <summary>用户确认候选洞察后，升级为当前有效人格快照。</summary>
    public async Task<AvatarInsightVersion?> ConfirmAsync(Guid versionId, CancellationToken cancellationToken)
    {
        var version = await repository.GetInsightVersionAsync(versionId, cancellationToken);
        if (version is null) return null;

        var now = DateTimeOffset.UtcNow;
        var insight = AvatarInsight.Create(
            version.Dimension,
            version.Title,
            version.Content,
            Math.Max(version.Confidence, PromoteConfidenceThreshold),
            [],
            now);

        await repository.SaveInsightsAsync([insight], cancellationToken);
        var refreshed = await repository.ListInsightsAsync(cancellationToken);
        var saved = refreshed.FirstOrDefault(item =>
            item.Dimension == version.Dimension
            && string.Equals(item.Title, version.Title, StringComparison.Ordinal));

        version.MarkConfirmed(saved?.Id ?? Guid.Empty, now);
        await repository.UpdateInsightVersionAsync(version, cancellationToken);
        return version;
    }

    private async Task ApplyToSnapshotAsync(
        AvatarInsightVersion version,
        AvatarInsight candidate,
        IReadOnlyList<Guid> evidenceMemoryIds,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var evidences = await repository.GetMemoriesAsync(evidenceMemoryIds, cancellationToken);
        var titles = evidences.Select(memory => memory.Title).Where(title => title.Length > 0).Take(8).ToList();

        var insight = AvatarInsight.Create(
            candidate.Dimension,
            candidate.Title,
            candidate.Content,
            candidate.Confidence,
            titles,
            now);

        await repository.SaveInsightsAsync([insight], cancellationToken);

        var refreshed = await repository.ListInsightsAsync(cancellationToken);
        var saved = refreshed.FirstOrDefault(item =>
            item.Dimension == candidate.Dimension
            && string.Equals(item.Title, candidate.Title, StringComparison.Ordinal));

        version.MarkConfirmed(saved?.Id ?? Guid.Empty, now);
        await repository.UpdateInsightVersionAsync(version, cancellationToken);
    }
}
