using System.Globalization;

namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>重排后的一条候选：分数、可解释的组成与原始命中。</summary>
/// <param name="Chunk">命中的切片（<c>Similarity</c> 已被改写为重排得分）。</param>
    /// <param name="Score">最终得分，落在 [0,1]；等于 <see cref="BaseScore"/> 与 <see cref="Coverage"/> 的凸组合。</param>
    /// <param name="Coverage">查询词的 idf 加权覆盖率，落在 [0,1]。</param>
    /// <param name="MatchedTokens">命中的查询词元数量（不含被当作通用词剔除的词元）。</param>
    /// <param name="QueryTokens">参与打分的查询词元总数（去重、剔除通用词后）。</param>
    /// <param name="BaseScore">重排前的相关性先验，落在 [0,1]：融合名次与余弦相似度各占一半。</param>
    public sealed record RerankedChunk(
        RetrievedChunk Chunk,
        double Score,
        double Coverage,
        int MatchedTokens,
        int QueryTokens,
        double BaseScore = 0d);
/// <param name="Abstained">是否应当拒答（明确回答“资料里没有”）。</param>
/// <param name="TopScore">最高得分；没有候选时为 0。</param>
/// <param name="Threshold">当时的阈值，便于把判定写进日志与响应。</param>
public sealed record AbstainDecision(bool Abstained, double TopScore, double Threshold);

/// <summary>
/// 候选重排与拒答的**纯函数**打分核心。
///
/// <para>
/// 为什么需要重排：RRF 只看排名不看内容，所以「词法排第 5、但正文确实完整回答了问题」的片段
/// 会被「两路都排得靠后、实际只沾到一个词」的候选挤下去。
/// </para>
///
/// <para>
/// 为什么最终得分不是纯覆盖率：实测（`scripts/retrieval-eval`，42 条有答案 + 8 条无答案）
/// 纯覆盖率排序会**把好结果挤下去**——一句话只要沾满虚词就能拿高分。所以最终得分是
/// 「名次先验 + 余弦先验」与覆盖率按权重 <c>w</c> 的凸组合，覆盖率只在接近的区间里起决定作用。
/// </para>
///
/// <para>
/// 为什么拒答默认不用分数阈值：实测这份语料上「有答案但换了说法」与「真的没有答案」的得分
/// **完全重叠**（有答案最低 0.361，无答案最高 0.474；要拒掉全部 8 条无答案必须误拒 16/42 条
/// 有答案）。这是词面/单向量信号的上限，不是实现缺陷：区分它们需要真正的语义判定模型。
/// 因此 <see cref="Decide"/> 只提供机制，是否启用由 <c>Retrieval:AbstainEnabled</c> 决定，
/// 默认关闭。人工开启前请先看 `docs/ROADMAP.md` 的 D-4 拒绝率曲线。
/// </para>
///
/// <para>
/// 所有函数都不碰数据库、不碰时间、不用随机数：同一输入永远同一输出，可直接单测与复跑。
/// </para>
/// </summary>
public static class RerankScoring
{
    /// <summary>覆盖率在最终得分里的默认权重；由真实语料扫出来的平台区中点（见 <see cref="Rank"/>）。</summary>
    public const double DefaultLexicalWeight = 0.65d;

    /// <summary>
    /// 文档频率超过这个比例的查询词元被视为"通用词"，不参与覆盖率。
    /// 0.7 的取法：候选窗口是 20 条左右，出现 14 条以上的词元，区分度已经接近掷硬币。
    /// </summary>
    public const double UbiquitousRatio = 0.7d;

    /// <summary>
    /// 候选少于这个数量时**不**做通用词剔除。
    /// 只看 1~2 条候选时，任何出现过的词元的文档频率都会高于 0.7，"剔除通用词"会把
    /// 所有词元一起剔光，覆盖率恒为 0——那等于把重排关掉了。
    /// </summary>
    public const int MinCandidatesForUbiquitousFilter = 5;

    /// <summary>单个查询词元的权重上限，避免某个超长罕见串独占全部权重。</summary>
    private const double MaxTokenWeight = 6d;

    /// <summary>命中"数字/编号类"词元（如 30000、v1、json-rpc）的加权倍数。</summary>
    private const double CodeTokenBoost = 1.6d;

    /// <summary>把查询切成重排用的词元（复用全文检索同一套中文二元组，保证口径一致）。</summary>
    public static IReadOnlyList<string> QueryTokens(string? query) =>
        LexicalTokens.Tokenize(query, LexicalTokens.MaxQueryTokens).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// 用候选集自身的文档频率估算 idf：<c>idf(t) = ln(1 + N / (1 + df))</c>。
    /// <para>
    /// 候选集就是"这一轮的全部竞争文档"，所以「任务」「索引」这类到处都有的词权重自动变低，
    /// 「汇报」「保留期」这类只在少数片段出现的词权重自动变高——不需要外部语料统计。
    /// </para>
    /// <para>查询里有、候选里一个都没有的词元，用 <c>df = 0</c> 的最大权重参与分母：它必然拉低覆盖率。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, double> BuildIdf(IReadOnlyList<IReadOnlyList<string>> candidateTokens)
    {
        var idf = new Dictionary<string, double>(StringComparer.Ordinal);
        var count = candidateTokens.Count;
        if (count == 0) return idf;

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tokens in candidateTokens)
        {
            foreach (var token in tokens.Distinct(StringComparer.Ordinal))
                documentFrequency[token] = documentFrequency.GetValueOrDefault(token) + 1;
        }

        foreach (var (token, frequency) in documentFrequency)
        {
            idf[token] = Math.Min(
                Math.Log(1d + count / (1d + frequency)) * WeightMultiplier(token),
                MaxTokenWeight);
        }

        return idf;
    }

    /// <summary>查询词元的权重；候选集里不存在的词元按 <c>df = 0</c> 计算（最大权重）。</summary>
    public static double WeightOf(string token, IReadOnlyDictionary<string, double> idf, int candidateCount) =>
        idf.TryGetValue(token, out var weight)
            ? weight
            : Math.Min(Math.Log(1d + Math.Max(candidateCount, 1)) * WeightMultiplier(token), MaxTokenWeight);

    /// <summary>
    /// 对候选集重排并取前 <paramref name="limit"/> 条。
    ///
    /// <para>
    /// 得分的形状是<b>「先验 + 证据」的凸组合</b>：
    /// </para>
    /// <code>
    /// Score = (1 - w) * Base + w * Coverage
    /// Base  = 0.5 * 名次先验 + 0.5 * 余弦先验      （两者都归一到 [0,1]）
    /// </code>
    /// <para>
    /// 拆开算而不是直接拿覆盖率排序，是因为实测覆盖率单独做排序信号会**把好结果挤下去**：
    /// 覆盖率只看「问题的词出现过没有」，一句话只要沾满虚词就能拿高分，而 RRF 名次和余弦
    /// 里其实含有覆盖率看不见的信息。加回先验后，覆盖率只在这种「同分/接近」的区间里起作用，
    /// 于是既修掉了 RRF 的弱命中，又不会推翻本来正确的排序。
    /// </para>
    /// <para>
    /// 权重 <paramref name="lexicalWeight"/> 由 `scripts/retrieval-eval` 在真实语料上扫出来
    /// （见 `docs/ROADMAP.md` 的 D-4 曲线）：0.60~0.85 之间 recall@5 都是满分，取中间的 0.65。
    /// 传 1.0 会退化成"纯覆盖率排序"，传 0.0 会退化成"保持原顺序"。
    /// </para>
    /// <para>
    /// 排序：得分降序 → 覆盖率降序 → 原有顺序（RRF 名次）。最后一项保证同分时结果稳定，
    /// 也让"重排没有把握时不要乱动"成为默认行为。
    /// </para>
    /// </summary>
    public static IReadOnlyList<RerankedChunk> Rank(
        string? query,
        IReadOnlyList<RetrievedChunk> candidates,
        int limit,
        double lexicalWeight = DefaultLexicalWeight)
    {
        if (limit <= 0 || candidates.Count == 0) return Array.Empty<RerankedChunk>();

        var weight = Math.Clamp(double.IsNaN(lexicalWeight) ? DefaultLexicalWeight : lexicalWeight, 0d, 1d);
        var count = candidates.Count;

        var candidateTokens = candidates
            .Select(chunk => (IReadOnlyList<string>)LexicalTokens.Tokenize(chunk.Content).Distinct(StringComparer.Ordinal).ToList())
            .ToList();
        var idf = BuildIdf(candidateTokens);

        // 通用词（在这批候选里到处都是的词元）对"区分谁更相关"没有信息量，
        // 留着它们只会把分母撑大、把分数压扁，所以从分子分母里一起去掉。
        // 候选太少时不做剔除，否则会把所有词元剔光（见 MinCandidatesForUbiquitousFilter）。
        var allQueryTokens = QueryTokens(query);
        var applyUbiquitousFilter = count >= MinCandidatesForUbiquitousFilter;
        var ubiquitousCeiling = UbiquitousRatio * count;
        var queryTokens = applyUbiquitousFilter
            ? allQueryTokens.Where(token => DocumentFrequency(token, candidateTokens) <= ubiquitousCeiling).ToList()
            : allQueryTokens;

        var totalWeight = queryTokens.Sum(token => WeightOf(token, idf, count));
        var coverage = new double[count];
        var matchedTokens = new int[count];
        for (var index = 0; index < count; index++)
        {
            var present = candidateTokens[index].ToHashSet(StringComparer.Ordinal);
            var matchedWeight = 0d;
            foreach (var token in queryTokens)
            {
                if (!present.Contains(token)) continue;
                matchedWeight += WeightOf(token, idf, count);
                matchedTokens[index]++;
            }
            coverage[index] = totalWeight > 0 ? Math.Clamp(matchedWeight / totalWeight, 0d, 1d) : 0d;
        }

        var similarityPrior = SimilarityPriors(candidates);

        var scored = new List<RerankedChunk>(count);
        for (var index = 0; index < count; index++)
        {
            var positionPrior = count > 1 ? 1d - index / (double)(count - 1) : 1d;
            var baseScore = 0.5d * positionPrior + 0.5d * similarityPrior[index];
            var score = (1d - weight) * baseScore + weight * coverage[index];

            scored.Add(new RerankedChunk(
                candidates[index],
                Math.Round(Math.Clamp(score, 0d, 1d), 4),
                Math.Round(coverage[index], 4),
                matchedTokens[index],
                queryTokens.Count,
                Math.Round(Math.Clamp(baseScore, 0d, 1d), 4)));
        }

        return scored
            .Select((entry, index) => (entry, index))
            .OrderByDescending(item => item.entry.Score)
            .ThenByDescending(item => item.entry.Coverage)
            .ThenBy(item => item.index)
            .Take(limit)
            // 把重排得分写回 Similarity：调用方（提示词里的"相似度"、引用芯片、评估脚本）
            // 看到的就是这一次排序真正使用的分数，而不是上一轮召回的余弦或 ts_rank。
            .Select(item => item.entry with { Chunk = item.entry.Chunk with { Similarity = item.entry.Score } })
            .ToList();
    }

    /// <summary>
    /// 把候选的余弦相似度归一到 [0,1]：减去最小、除以跨度。
    /// <para>
    /// 之所以要归一：余弦的绝对值取决于模型与语料（bge-small-zh 上同一个问题的相关/不相关
    /// 片段往往只差 0.05），跨查询不可比；而**同一批候选内部**的相对高低是有意义的。
    /// 全部相等时统一给 1，让排序退化为"只看名次与覆盖率"，不会因为除零产生 NaN。
    /// </para>
    /// </summary>
    private static double[] SimilarityPriors(IReadOnlyList<RetrievedChunk> candidates)
    {
        var count = candidates.Count;
        var priors = new double[count];
        if (count == 0) return priors;

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var index = 0; index < count; index++)
        {
            var similarity = candidates[index].Similarity;
            if (similarity < min) min = similarity;
            if (similarity > max) max = similarity;
        }

        var span = max - min;
        for (var index = 0; index < count; index++)
        {
            priors[index] = span > 1e-9
                ? Math.Clamp((candidates[index].Similarity - min) / span, 0d, 1d)
                : 1d;
        }

        return priors;
    }

    private static int DocumentFrequency(string token, IReadOnlyList<IReadOnlyList<string>> candidateTokens)
    {
        var frequency = 0;
        foreach (var tokens in candidateTokens)
            if (tokens.Contains(token, StringComparer.Ordinal)) frequency++;
        return frequency;
    }

    /// <summary>
    /// 拒答判定：没有候选，或最高分低于阈值。
    /// <para>
    /// 阈值必须由语料标定（见 `docs/ROADMAP.md` 的 D-4 与 `scripts/retrieval-eval/README.md`），
    /// 这里只负责按给定阈值判定，不内置"魔法数字"。
    /// </para>
    /// </summary>
    public static AbstainDecision Decide(double threshold, params double[] scores)
    {
        var top = scores.Length == 0 ? 0d : scores.Max();
        var effective = Math.Clamp(threshold, 0d, 1d);
        return new AbstainDecision(top < effective, Math.Round(top, 4), effective);
    }

    /// <summary>编号/数字类词元更值得命中：问「配置项 30000」时，数字匹配远比虚词匹配重要。</summary>
    private static double WeightMultiplier(string token) =>
        token.Any(character => character is >= '0' and <= '9') ? CodeTokenBoost : 1d;

    /// <summary>把得分格式化成两位小数，供日志与提示词使用（区域无关，避免逗号当小数点）。</summary>
    public static string FormatScore(double score) =>
        score.ToString("F3", CultureInfo.InvariantCulture);
}
