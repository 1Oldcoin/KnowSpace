using System.Text;
using MyFirstMafAgent.Api.Domain.Notes;

namespace MyFirstMafAgent.Api.Application.Notes;

/// <summary>一次批量导出的结果：正文 + 建议文件名 + 笔记条数。</summary>
public sealed record NoteExport(string Content, string FileName, int Count);

/// <summary>
/// 把笔记渲染成一份可直接阅读、可直接贴进其他工具的 Markdown。
///
/// <para>
/// 纯函数、无依赖，只吃 <see cref="NoteDto"/>，所以可以单测。
/// 格式对齐问答导出（<c>KnowledgeChatExportFormatter</c>）的风格：
/// 一级标题分节、元信息用 blockquote，避免和正文混在一起。
/// </para>
/// </summary>
public static class NoteExportFormatter
{
    public static string FormatMarkdown(IReadOnlyList<NoteDto> notes)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# 学习笔记导出");
        builder.AppendLine();
        builder.AppendLine($"> 导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        builder.AppendLine($"> 笔记数量：{notes.Count}");
        builder.AppendLine();

        for (var index = 0; index < notes.Count; index++)
        {
            AppendNote(builder, notes[index], index + 1);
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void AppendNote(StringBuilder builder, NoteDto note, int order)
    {
        builder.AppendLine($"## {order}. {note.Title}");
        builder.AppendLine();

        var meta = new List<string>
        {
            $"分类：{note.Category}",
            $"更新时间：{note.UpdatedAt:yyyy-MM-dd HH:mm}"
        };
        if (note.IsPinned) meta.Add("置顶");
        if (note.Tags.Count > 0) meta.Add($"标签：{string.Join(" / ", note.Tags)}");
        if (note.SourceSessionId is { } session && session != Guid.Empty)
            meta.Add($"来源问答：{session}");

        builder.AppendLine($"> {string.Join(" · ", meta)}");
        builder.AppendLine();

        if (!string.IsNullOrWhiteSpace(note.Content))
        {
            // 正文里写了 [1] 就输出真正的脚注引用：阅读器里能点回定义，
            // 而不是把 [1] 留成一段“看起来像链接点不动”的文本。
            var sources = note.Sources ?? Array.Empty<NoteSource>();
            var body = NoteCitationAnchor.ToMarkdownReferences(note.Content.TrimEnd(), sources.Count);
            builder.AppendLine(body);
            builder.AppendLine();
        }

        AppendSources(builder, note);

        builder.AppendLine("---");
        builder.AppendLine();
    }

    /// <summary>
    /// 引用资料：正文有锚点时输出脚注定义（只列被引用到的），否则输出普通列表。
    /// 两种形态都保留“文件名 + 第几段 + 相关度 + 摘录”，因为核对出处的信息量不能因为排版变化而丢。
    /// </summary>
    private static void AppendSources(StringBuilder builder, NoteDto note)
    {
        var sources = note.Sources ?? Array.Empty<NoteSource>();
        if (sources.Count == 0) return;

        var referenced = NoteCitationAnchor.ReferencedSourceIndices(note.Content, sources.Count);

        if (referenced.Count > 0)
        {
            foreach (var index in referenced)
            {
                builder.AppendLine($"[^{index}]: {Describe(sources[index - 1])}");
            }

            var unused = Enumerable.Range(1, sources.Count).Where(index => !referenced.Contains(index)).ToList();
            if (unused.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("未被正文引用的资料：");
                foreach (var index in unused) builder.AppendLine($"- [{index}] {Describe(sources[index - 1])}");
            }

            builder.AppendLine();
            return;
        }

        builder.AppendLine($"### 引用资料（{sources.Count} 条）");
        builder.AppendLine();
        for (var index = 0; index < sources.Count; index++)
        {
            builder.AppendLine($"- [{index + 1}] {Describe(sources[index])}");
            builder.AppendLine($"  - 资料 ID：{sources[index].DocumentId}");
            if (!string.IsNullOrWhiteSpace(sources[index].Snippet))
                builder.AppendLine($"  - 摘录：{Collapse(sources[index].Snippet!)}");
        }

        builder.AppendLine();
    }

    /// <summary>一行说清“出自哪份资料的哪一段、当时多少分”。</summary>
    private static string Describe(NoteSource source)
    {
        var location = source.ChunkIndex >= 0 ? $"（第 {source.ChunkIndex + 1} 段）" : string.Empty;
        var parts = new List<string> { $"{source.FileName}{location}" };
        if (source.Similarity > 0) parts.Add($"相关度 {source.Similarity:P0}");
        parts.Add($"资料 ID：{source.DocumentId}");
        if (!string.IsNullOrWhiteSpace(source.Snippet)) parts.Add(Collapse(source.Snippet!));
        return string.Join(" · ", parts);
    }

    /// <summary>片段里的换行会撑散列表，折叠成空格更像“引用”而不是“正文”。</summary>
    private static string Collapse(string value) =>
        string.Join(' ', value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
