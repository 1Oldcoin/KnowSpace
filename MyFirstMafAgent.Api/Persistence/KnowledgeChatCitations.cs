using System.Text.Json;
using System.Text.Json.Serialization;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Persistence;

/// <summary>
/// 一条问答引用元数据：定位到“哪份资料的哪一段”，并带上相似度与文本片段。
/// <para>
/// 落库而不是只在响应里返回，是为了让历史会话在重开、导出、复盘时都能看到同一条引用；
/// 这是“答案可信”的最小闭环，也是后续评估集（P2-1）判断检索是否命中的基础。
/// </para>
/// </summary>
/// <param name="DocumentId">资料 ID，可回跳到资料库。</param>
/// <param name="FileName">资料文件名（展示用）。</param>
/// <param name="Similarity">余弦相似度，0~1。</param>
/// <param name="Rank">本轮检索中的排名，从 1 开始；0 表示未知（历史数据）。</param>
/// <param name="ChunkId">kb_chunks 主键；0 表示未知（历史数据）。</param>
/// <param name="ChunkIndex">切片在资料内的序号；-1 表示未知。</param>
/// <param name="Snippet">切片文本片段（截断），用于在前端悬浮预览。</param>
/// <param name="MediaStartSeconds">
/// 音视频切片在媒体中的起始秒数；null 表示这不是一条时间轴切片（普通文档）。
/// 前端据此把引用变成"跳到这里播放"而不是只展示文件名。
/// </param>
public sealed record KnowledgeChatCitation(
    Guid DocumentId,
    string FileName,
    double Similarity,
    int Rank = 0,
    long ChunkId = 0,
    int ChunkIndex = -1,
    string? Snippet = null,
    int? MediaStartSeconds = null);

/// <summary>本轮检索使用的索引上下文，随答案一起落库，便于回答“为什么是这几条资料”。</summary>
/// <param name="IndexId">命中的索引实例；null 表示走了旧版检索。</param>
/// <param name="EmbeddingSpecName">向量模型规格名（模型切换会导致引用不可比）。</param>
/// <param name="Warning">检索过程中的降级提示（例如词法不可用、索引回退）。</param>
/// <param name="LegacyIndex">是否使用了旧版直查 kb_chunks 的检索路径。</param>
/// <param name="Strategy">召回方式：vector / lexical / hybrid（RRF 融合）/ rerank（融合后重排）。</param>
/// <param name="Abstained">本轮是否因为"最高相关度不足"而拒答（此时引用为空，答案应说明资料里没有）。</param>
/// <param name="TopScore">本轮最高重排得分；未重排时为 null。</param>
public sealed record KnowledgeChatRetrieval(
    Guid? IndexId = null,
    string? EmbeddingSpecName = null,
    string? Warning = null,
    bool LegacyIndex = false,
    string? Strategy = null,
    bool Abstained = false,
    double? TopScore = null);

/// <summary>
/// 助手消息附带的答案元数据：检索上下文 + 工具调用审计 + 网页来源。
/// 这些都是“答案可信”的证据链，历史会话重开时同样需要还原。
/// </summary>
public sealed record KnowledgeChatAnswerMeta(
    KnowledgeChatRetrieval? Retrieval = null,
    IReadOnlyList<AgentToolCallTrace>? ToolCalls = null,
    IReadOnlyList<string>? Sources = null);

/// <summary>引用与元数据的 JSON 编解码。读写都做防御：历史脏数据不应该让整个会话无法打开。</summary>
public static class KnowledgeChatJson
{
    /// <summary>引用片段的最大长度，避免一条切片把整行对话撑满。</summary>
    public const int MaxSnippetLength = 240;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>把检索结果转换成引用列表；<paramref name="rank"/> 从 1 开始。</summary>
    public static IReadOnlyList<KnowledgeChatCitation> FromChunks(IReadOnlyList<RetrievedChunk> chunks)
    {
        if (chunks.Count == 0) return Array.Empty<KnowledgeChatCitation>();

        var citations = new List<KnowledgeChatCitation>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            citations.Add(new KnowledgeChatCitation(
                chunk.DocumentId,
                chunk.FileName,
                Math.Round(chunk.Similarity, 4),
                index + 1,
                chunk.ChunkId,
                chunk.ChunkIndex,
                BuildSnippet(chunk.Content, MaxSnippetLength),
                // 时间轴切片一律以 [hh:mm:ss] 开头，这里直接从文本读回来，
                // 不需要给 kb_chunks 加列，也不会漏掉历史切片。
                MediaTimeline.ReadStartSeconds(chunk.Content)));
        }

        return citations;
    }

    /// <summary>截断切片文本，并压平换行，便于在气泡中单行悬浮展示。</summary>
    public static string? BuildSnippet(string? content, int maxLength = MaxSnippetLength)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var normalized = string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0) return null;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    public static string? SerializeCitations(IReadOnlyList<KnowledgeChatCitation>? citations) =>
        citations is null ? null : JsonSerializer.Serialize(citations, Options);

    public static IReadOnlyList<KnowledgeChatCitation> DeserializeCitations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KnowledgeChatCitation>();
        try
        {
            return JsonSerializer.Deserialize<List<KnowledgeChatCitation>>(json, Options)
                ?? (IReadOnlyList<KnowledgeChatCitation>)Array.Empty<KnowledgeChatCitation>();
        }
        catch (JsonException)
        {
            return Array.Empty<KnowledgeChatCitation>();
        }
    }

    public static string? SerializeMeta(KnowledgeChatAnswerMeta? meta)
    {
        if (meta is null) return null;
        if (meta.Retrieval is null && meta.ToolCalls is not { Count: > 0 } && meta.Sources is not { Count: > 0 }) return null;
        return JsonSerializer.Serialize(meta, Options);
    }

    public static KnowledgeChatAnswerMeta? DeserializeMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<KnowledgeChatAnswerMeta>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
