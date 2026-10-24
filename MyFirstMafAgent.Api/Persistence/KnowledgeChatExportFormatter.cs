using System.Text;

namespace MyFirstMafAgent.Api.Persistence;

public static class KnowledgeChatExportFormatter
{
    public static string Format(KnowledgeChatSession session, IReadOnlyList<KnowledgeChatMessage> messages)
    {
        var export = new StringBuilder();
        export.Append("# ").AppendLine(session.Title);
        export.AppendLine();
        export.Append("创建时间：").AppendLine(session.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
        export.Append("最近更新：").AppendLine(session.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));

        foreach (var message in messages)
        {
            export.AppendLine();
            export.Append("## ").Append(message.Role == "user" ? "我" : "知间 AI").Append(" · ")
                .AppendLine(message.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            export.AppendLine();
            export.AppendLine(message.Content);

            AppendCitations(export, message);
            AppendSources(export, message);
        }

        return export.ToString();
    }

    /// <summary>
    /// 引用来源随导出落到 Markdown：导出件常被转发或存档，
    /// 没有引用的答案在离线阅读时无法核对，等于把“可信”丢了。
    /// </summary>
    private static void AppendCitations(StringBuilder export, KnowledgeChatMessage message)
    {
        if (message.Citations is not { Count: > 0 } citations) return;

        export.AppendLine();
        export.Append("> 引用资料（").Append(citations.Count).AppendLine(" 条，按相关度排序）");
        foreach (var citation in citations)
        {
            export.Append("> - ");
            if (citation.Rank > 0) export.Append('[').Append(citation.Rank).Append("] ");
            export.Append(citation.FileName);
            if (citation.ChunkIndex >= 0) export.Append("（第 ").Append(citation.ChunkIndex + 1).Append(" 段）");
            export.Append(" · 相关度 ").Append(citation.Similarity.ToString("P0"));
            export.AppendLine();
            if (!string.IsNullOrWhiteSpace(citation.Snippet))
                export.Append(">   ").AppendLine(citation.Snippet);
        }

        if (message.AnswerMeta?.Retrieval is { } retrieval)
        {
            export.Append("> 检索索引：").AppendLine(retrieval.LegacyIndex
                ? "旧版直查（kb_chunks）"
                : retrieval.IndexId?.ToString() ?? "未知");
            if (!string.IsNullOrWhiteSpace(retrieval.Strategy))
                export.Append("> 检索方式：").AppendLine(DescribeStrategy(retrieval.Strategy));
            if (!string.IsNullOrWhiteSpace(retrieval.EmbeddingSpecName))
                export.Append("> 向量模型：").AppendLine(retrieval.EmbeddingSpecName);
            if (!string.IsNullOrWhiteSpace(retrieval.Warning))
                export.Append("> 检索提示：").AppendLine(retrieval.Warning);
        }
    }

    private static string DescribeStrategy(string strategy) => strategy switch
    {
        "hybrid" => "混合（向量 + 词法 RRF）",
        "lexical" => "词法（全文）",
        _ => "向量（余弦相似度）"
    };

    /// <summary>联网检索得到的链接同样属于答案证据，导出时保留。</summary>
    private static void AppendSources(StringBuilder export, KnowledgeChatMessage message)
    {
        if (message.AnswerMeta?.Sources is not { Count: > 0 } sources) return;

        export.AppendLine();
        export.Append("> 网页来源（").Append(sources.Count).AppendLine(" 条）");
        foreach (var source in sources)
            export.Append("> - ").AppendLine(source);
    }
}
