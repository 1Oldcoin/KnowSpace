namespace MyFirstMafAgent.Api.Options;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>是否启用混合检索（向量 + 词法 RRF 融合）。关闭后退回纯向量检索。</summary>
    public bool HybridEnabled { get; set; } = true;

    /// <summary>RRF 的平滑常数 k：越大越"看回合并排名"，越小越"只看头部命中"。</summary>
    public int RrfK { get; set; } = 60;

    /// <summary>每一路召回的候选倍数（最终取 limit 条，但融合前多取一些）。</summary>
    public int CandidateMultiplier { get; set; } = 3;

    /// <summary>候选上限，避免 limit 很大时把整库拉回来。</summary>
    public int MaxCandidates { get; set; } = 60;

    /// <summary>
    /// 默认是否在混合检索之后重排。关闭后行为与 D-4 之前完全一致（纯 RRF），
    /// 显式传 <c>mode=hybrid</c> 时也不重排，方便做前后对比。
    /// </summary>
    public bool RerankEnabled { get; set; } = true;

    /// <summary>
    /// 送进重排的候选条数上限——同时也是重排模式下**每一路**的召回条数。
    /// <para>
    /// 这个值直接决定重排能不能修掉弱命中：实测（42 条有答案 / 8 条无答案）20 条候延时
    /// rerank 与 hybrid 打成平手（recall 0.9762、MRR 0.8552 vs 0.8492），60 条候延时
    /// recall 1.0000、MRR 0.9075。代价是 p50 +5ms、p90 +35ms，所以默认取满上限。
    /// </para>
    /// </summary>
    public int RerankCandidateLimit { get; set; } = 60;

    /// <summary>
    /// 重排里「词面覆盖率」相对「先验（融合名次 + 余弦）」的权重，0–1。
    /// <para>
    /// 0 = 保持 RRF 原顺序；1 = 纯覆盖率排序（实测会把好结果挤下去）。
    /// 真实语料扫出的平台区是 0.60–0.85（recall@5 都是满分），默认取中间的 0.65。
    /// 曲线见 `docs/ROADMAP.md` 的 D-4。
    /// </para>
    /// </summary>
    public double RerankLexicalWeight { get; set; } = 0.65;

    /// <summary>
    /// 是否启用拒答：最高重排得分低于阈值时明确回答“资料里没有”。
    /// 只在重排模式下生效。
    /// <para>
    /// **默认关闭**，因为实测在这份单领域语料上分数不可分：有答案的最低 0.361、无答案的最高 0.474，
    /// 要让 8 条无答案全部被拒，必须同时误拒 16/42 条有答案。硬上阈值的代价比“让模型自己说没有”高得多。
    /// 真正的“资料里没有”目前由提示词约束模型回答（见 <c>KnowledgeChatService.BuildReferenceNote</c>）。
    /// 需要人工开启时，先看 `docs/ROADMAP.md` D-4 的拒绝率曲线再选阈值。
    /// </para>
    /// </summary>
    public bool AbstainEnabled { get; set; } = false;

    /// <summary>
    /// 拒答阈值（重排得分，0–1）。仅在 <see cref="AbstainEnabled"/> 为 true 时生效。
    /// 实测取舍（42 条有答案 / 8 条无答案）：
    /// 0.35 → 拒 3/8 无答案、误拒 0/42；0.40 → 5/8、误拒 2/42；
    /// 0.45 → 7/8、误拒 8/42；0.50 → 8/8、误拒 16/42。
    /// 默认取 0.45（在“能拒一部分”与“误拒可控”之间的折中点），需要激进拒答时再往上调。
    /// </summary>
    public double AbstainMinScore { get; set; } = 0.45;
}
