using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>一次检索的完整结果：问题理解 + 记忆命中。</summary>
public sealed record AvatarRetrievalResult(
    AvatarQueryAnalysis Analysis,
    IReadOnlyList<AvatarMemoryHit> Hits);

/// <summary>
/// 分身记忆检索：先理解问题类型，再做结构化过滤 + 向量/词面多路召回 + 重排。
/// 矛盾观点优先当前有效状态；只有问“以前为什么”时才展示历史。
/// 向量模型不可用时自动退化为词面检索。
/// </summary>
public sealed class AvatarMemoryRetriever(
    IAvatarRepository repository,
    AvatarMemoryIndexer indexer,
    ILogger<AvatarMemoryRetriever> logger)
{
    private const int MaxMemoriesScanned = 2_000;

    public async Task<IReadOnlyList<AvatarMemoryHit>> RetrieveAsync(
        string query,
        int take,
        CancellationToken cancellationToken) =>
        (await RetrieveDetailedAsync(query, take, cancellationToken)).Hits;

    public async Task<AvatarRetrievalResult> RetrieveDetailedAsync(
        string query,
        int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 30);
        var entityNames = (await repository.ListEntitiesAsync(cancellationToken))
            .Select(entity => entity.Name)
            .ToList();
        var analysis = AvatarQueryClassifier.Analyze(query, entityNames);

        var memories = await repository.ListMemoriesForRetrievalAsync(
            MaxMemoriesScanned, analysis.NeedsHistory, cancellationToken);
        if (memories.Count == 0) return new AvatarRetrievalResult(analysis, []);

        var byId = memories.ToDictionary(memory => memory.Id);
        var vectorScores = new Dictionary<Guid, double>();
        var vectorMatches = await indexer.TrySearchAsync(query, take * 5, cancellationToken);
        if (vectorMatches is not null)
        {
            foreach (var match in vectorMatches)
            {
                if (!byId.ContainsKey(match.MemoryId)) continue;
                var similarity = Math.Clamp(match.Similarity, 0, 1);
                if (similarity <= 0.05) continue;
                vectorScores[match.MemoryId] = similarity;
            }
        }

        var queryGrams = AvatarTextSimilarity.BuildGrams(query);
        var lexicalScores = new Dictionary<Guid, double>();
        var maxLexical = 0d;
        if (queryGrams.Count > 0)
        {
            foreach (var memory in memories)
            {
                var score = AvatarTextSimilarity.Score(queryGrams, memory.Title, memory.ToRetrievalText());
                if (score <= 0) continue;
                lexicalScores[memory.Id] = score;
                if (score > maxLexical) maxLexical = score;
            }
        }

        var feedback = await repository.GetMemoryFeedbackStatsAsync(
            memories.Select(memory => memory.Id).ToList(), cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var hits = new List<AvatarMemoryHit>();

        foreach (var memory in memories)
        {
            if (!analysis.NeedsHistory && !AvatarMemoryLifecycle.IsCurrent(memory.LifecycleStatus))
                continue;

            var vectorScore = vectorScores.GetValueOrDefault(memory.Id);
            var lexicalRaw = lexicalScores.GetValueOrDefault(memory.Id);
            var lexicalScore = maxLexical > 0 ? lexicalRaw / maxLexical : 0;
            var feedbackStat = feedback.GetValueOrDefault(memory.Id);
            var ageDays = Math.Max(0, (now - memory.OccurredAt).TotalDays);

            var typeMatch = analysis.NeededMemoryTypes.Contains(memory.MemoryType, StringComparer.Ordinal)
                ? 0.10
                : 0;
            var entityMatch = analysis.Entities.Count > 0
                && memory.People.Any(person => analysis.Entities.Contains(person, StringComparer.OrdinalIgnoreCase))
                    ? 0.06
                    : 0;
            var stabilityBoost = memory.Stability == AvatarMemoryStability.Stable ? 0.03 : 0;
            var confidenceBoost = memory.Confidence / 100d * 0.08;
            var importanceBoost = (memory.Importance - 3) * 0.03;
            var recencyBoost = Math.Max(0, (365 - ageDays) / 365) * 0.05;
            var feedbackBoost = Math.Min(0.09, feedbackStat.Like * 0.03)
                - Math.Min(0.15, feedbackStat.Unlike * 0.05);

            var lifecyclePenalty = memory.LifecycleStatus switch
            {
                AvatarMemoryLifecycle.Candidate => 0.02,
                AvatarMemoryLifecycle.PendingConfirmation => 0.06,
                AvatarMemoryLifecycle.Historical => analysis.NeedsHistory ? 0 : 0.35,
                AvatarMemoryLifecycle.Superseded => analysis.NeedsHistory ? 0.05 : 0.45,
                AvatarMemoryLifecycle.Corrected => analysis.NeedsHistory ? 0.10 : 0.50,
                AvatarMemoryLifecycle.Archived => 0.60,
                _ => 0
            };

            var score = vectorScore * 0.50
                + lexicalScore * 0.25
                + typeMatch
                + entityMatch
                + stabilityBoost
                + confidenceBoost
                + Math.Max(-0.06, Math.Min(0.06, importanceBoost))
                + recencyBoost
                + feedbackBoost
                - lifecyclePenalty;

            if (score <= 0.01) continue;

            var reason = BuildReason(vectorScore, lexicalScore, typeMatch > 0, entityMatch > 0);
            hits.Add(new AvatarMemoryHit(
                memory, score, vectorScore > 0, vectorScore, lexicalScore, reason));
        }

        var result = hits
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Memory.OccurredAt)
            .Take(take)
            .ToList();

        if (result.Count == 0)
        {
            logger.LogDebug("Avatar memory retrieval found no match for query; using most recent memories.");
            result = memories
                .Where(memory => AvatarMemoryLifecycle.IsCurrent(memory.LifecycleStatus))
                .OrderByDescending(memory => memory.OccurredAt)
                .Take(Math.Min(4, memories.Count))
                .Select(memory => new AvatarMemoryHit(memory, 0, false, 0, 0, "最近记忆兜底"))
                .ToList();
        }

        if (result.Count > 0)
        {
            await repository.TouchMemoriesUsedAsync(
                result.Select(hit => hit.Memory.Id).ToList(), cancellationToken);
        }

        return new AvatarRetrievalResult(analysis, result);
    }

    private static string BuildReason(
        double vectorScore,
        double lexicalScore,
        bool typeMatch,
        bool entityMatch)
    {
        var parts = new List<string>(4);
        if (vectorScore > 0) parts.Add($"语义 {vectorScore:0.00}");
        if (lexicalScore > 0) parts.Add($"词面 {lexicalScore:0.00}");
        if (typeMatch) parts.Add("类型匹配");
        if (entityMatch) parts.Add("实体匹配");
        return parts.Count == 0 ? "时间/重要度" : string.Join(" · ", parts);
    }
}
