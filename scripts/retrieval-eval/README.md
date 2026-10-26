# Retrieval evaluation (hybrid search baseline)

Re-runnable harness for comparing retrieval strategies on the **same** question set:

| mode      | what runs                                                     |
|-----------|---------------------------------------------------------------|
| `vector`  | pgvector cosine similarity only (the pre-P2-1 behaviour)      |
| `lexical` | PostgreSQL full-text only (`to_tsvector('simple', …)` + GIN)   |
| `hybrid`  | both, fused with Reciprocal Rank Fusion (k = 60, `Retrieval:RrfK`) |
| `rerank`  | hybrid, then `RerankScoring` re-scores the fused candidates (D-4) |

`rerank` is a *superset* of `hybrid`: `-Modes hybrid,rerank` is the isolation of
"what does reranking actually add", and `-Modes vector,lexical,hybrid,rerank`
gives the four-step ladder that shows where each stage earns its keep.

The report also captures `abstained` / `topScore` / `abstainThreshold` per row and
prints a **threshold sweep** at the end, so the cost of refusing a question can be
read off directly instead of guessed.

## Run

```powershell
# API must be running and the knowledge base must have Ready documents
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1

# explicit modes + archive the raw report
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 `
    -Modes vector,lexical,hybrid,rerank -OutFile C:\Temp\eval-2026-10-24.json

# what can I even ask about?
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -ListFiles
```

Metrics written to the report and printed at the end:

- **recall@K** — share of cases where the top-K contains an expected document.
- **MRR** — mean of `1/rank` of the first expected hit (0 when nothing matched).

## Authoring a fixture

`retrieval-eval/default.json`:

```json
{
  "name": "默认知识库检索基线",
  "knowledgeBaseId": "11111111-1111-1111-1111-111111111111",
  "cases": [
    { "query": "插件如何与核心通信",
      "expectFiles": ["插件协议要点-ea65061f.md"] },
    { "query": "note-1001",
      "expectFiles": ["欢迎使用插件系统-1e6b95c8.md"],
      "expectText": "note-1001" }
  ]
}
```

- `expectFiles` —文件名的可接受答案集合（命中其中之一即算命中）。
- `expectText` — 额外要求：命中切片的正文里必须出现这段文字。
- `category` — 可选，仅用于分组统计（`字面` / `语义` / `区分` / `总览陷阱`），不参与打分。
- Keep two kinds of cases:
  - **语义类**：问法与原文措辞不同（考验向量）。
  - **字面类**：编号、URL、报错码、罕见术语（考验词法，也是混合检索真正的增量）。

> **不要在跑分后回头改 fixture 让数字变好看。** 那份 fixture 就不再是评估集了。

## The confusable corpus (T-3)

`corpus/` holds 27 Chinese documents in three clusters (plugin runtime / index lifecycle / job centre,
8 + 1 overview each). Documents inside a cluster deliberately share vocabulary (超时、重试、检查、
保留期、回收…) and every cluster has an overview that answers the same question only vaguely.
`corpus.json` has 42 cases in four categories:

| category | what it tests | which mode should win |
| --- | --- | --- |
| 字面 | codes, config keys, IDs (`Jobs:LeaseSeconds`, `kb_index_vectors`) | lexical |
| 语义 | paraphrase with no literal overlap | vector |
| 区分 | a concept that appears in several docs, the answer in exactly one | hybrid |
| 总览陷阱 | the overview also "answers" it, but only the detail doc is correct | both |

### Seed it, then evaluate

```powershell
# creates (or reuses) the knowledge base, uploads every .md, waits for Ready,
# and writes corpus.kb.txt next to the fixture (idempotent: Ready names are skipped)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Seed-RetrievalCorpus.ps1

# corpus.json leaves knowledgeBaseId empty on purpose: the harness reads corpus.kb.txt
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 `
    -Fixture scripts\retrieval-eval\corpus.json -Modes vector,lexical,hybrid -OutFile C:\Temp\eval-corpus.json
```

> The knowledge base is bound to whatever embedding spec is the current default. If the default was
> switched to a stub/cloud model, create an index for the local BGE spec first
> (`POST /api/knowledge-bases/{id}/indexes { embeddingSpecId, activateWhenReady: true }`), otherwise the
> numbers describe that model, not BGE.

### Baseline (2026-10-24, bge-small-zh-v1.5, top-5, 42 answerable + 8 no-answer cases)

| mode | recall@5 | MRR | hits |
| --- | --- | --- | --- |
| `vector` | 0.8571 | 0.7270 | 36/42 |
| `lexical` | 0.9524 | 0.8460 | 40/42 |
| `hybrid` | 0.9762 | 0.8492 | 41/42 |
| `rerank` | **1.0000** | **0.9075** | **42/42** |

Per category, each single mode collapses somewhere: 字面 vector 0.60/0.52 vs lexical 1.00/1.00;
语义 vector 1.00/0.90 vs lexical 0.80/0.68. Hybrid is 1.00 recall on both.
Two runs produced identical numbers, so this table is a re-runnable baseline.

`rerank` only pays off with a deep candidate pool: at `Retrieval:RerankCandidateLimit = 20` it
*ties* hybrid (0.9762 / 0.8552 — better MRR, same recall); at `60` it reaches 1.0000 / 0.9075.
That knob is the per-path recall depth, not just a rerank input size, which is why it has to be
raised *before* fusion.

### The no-answer category and the abstain sweep

The last 8 cases carry `"expectFiles": []` and `"expectAbstain": true` (off-topic questions such as
公司年假有几天 or 番茄牛腩要炖多久, plus one same-domain-but-absent question about plugin-market revenue
splitting — the hard one). They never enter the recall denominator: a successful refusal counted as a
missed retrieval would make the two modes incomparable.

Instead the report prints a sweep (`abstainSweep` in the JSON): for each threshold, how many answerable
questions would be refused versus how many no-answer questions would be caught. Measured on this corpus:

| threshold | no-answer refused | answerable wrongly refused |
| --- | --- | --- |
| 0.35 | 3/8 | 0/42 |
| 0.40 | 5/8 | 1/42 |
| 0.45 | 7/8 | 4/42 |
| 0.50 | 8/8 | 15/42 |

There is no threshold that separates the two groups, which is why `Retrieval:AbstainEnabled` ships as
`false`. Re-run the sweep on a new corpus before enabling it — don't assume the numbers above transfer.

## Reading the numbers honestly

- 语料只有 3 份资料时，任何一路检索都能排到第 1，recall@5 恒为 1——**这是烟雾测试，不是效果证据**。
  有意义的对比需要几十份同主题资料 + 会真正混淆的问题（问题里的关键词同时出现在多份资料中）。
- 一次改动的正确用法：改动前跑一次写 `-OutFile`，改动后再跑一次，比较同一 fixture 的 recall / MRR。
- `strategy` 字段会写进报告：`vector` / `lexical` / `hybrid` / `rerank`。如果它和你想跑的模式不一致，
  说明 `Retrieval:HybridEnabled=false` / `Retrieval:RerankEnabled=false` 或服务端版本不对。
- `rerank` 模式下的 `similarity` **不再是余弦**，而是重排得分（`(1-w)*先验 + w*覆盖率`，见
  `Domain/Knowledge/RerankScoring.cs`）。它落在 [0,1] 且与排序一致，但**不要**拿去和 `vector`
  模式的余弦比大小——两个量纲。

## Implementation notes

- 分词在应用侧完成（`Domain/Knowledge/LexicalTokens.cs`，中文二元组 + 拉丁词小写），
  因为随包的 PostgreSQL 17.9 没有 `zhparser`，默认解析器会把整段中文当成一个词。
- 分词结果写入 `kb_chunks.lexical_tokens`，由 GIN 表达式索引
  `ix_kb_chunks_lexical ON kb_chunks USING GIN (to_tsvector('simple', coalesce(lexical_tokens, '')))` 支撑。
- 历史切片由 `LexicalIndexWorker` 在后台分批补分词（正常空转）。
- RRF 只用排名、不用分数：余弦相似度（0–1）与 `ts_rank`（常见 0.0x）量纲不同，线性加权必然要按语料调参。
- 重排的 `w`（覆盖率权重）是**扫出来**的，不是拍的：`w=0` 能精确复现 hybrid 基线，`w=0.20~0.90`
  的 recall@5 都在 1.0000，默认取平台区中点的 0.65。详细曲线见 `docs/ROADMAP.md` 的 D-4。
