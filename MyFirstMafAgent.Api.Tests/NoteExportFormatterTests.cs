using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Domain.Notes;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>批量导出格式：人读得懂、机器也还能解析出关键信息。</summary>
public sealed class NoteExportFormatterTests
{
    private static readonly Guid KnowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 10, 0, 0, TimeSpan.Zero);

    private static NoteDto BuildNote(
        string title,
        string content,
        bool isPinned = false,
        Guid? documentId = null,
        IReadOnlyList<NoteSource>? sources = null,
        Guid? topicId = null,
        Guid? sessionId = null)
    {
        var note = Note.Create(
            KnowledgeBaseId, title, content, "资料摘录", "rose", new[] { "插件", "协议" }, documentId, Now,
            topicId, sources, sessionId);
        if (isPinned) note.SetPinned(true, Now);
        return NoteDto.FromDomain(note);
    }

    [Fact]
    public void Format_includes_header_and_each_note()
    {
        var notes = new[]
        {
            BuildNote("第一篇", "正文一"),
            BuildNote("第二篇", "正文二")
        };

        var markdown = NoteExportFormatter.FormatMarkdown(notes);

        Assert.StartsWith("# 学习笔记导出", markdown);
        Assert.Contains("> 笔记数量：2", markdown);
        Assert.Contains("## 1. 第一篇", markdown);
        Assert.Contains("## 2. 第二篇", markdown);
        Assert.Contains("正文一", markdown);
        Assert.Contains("分类：资料摘录", markdown);
        Assert.Contains("标签：插件 / 协议", markdown);
    }

    [Fact]
    public void Format_marks_pinned_notes_and_source_session()
    {
        var sessionId = Guid.NewGuid();
        var markdown = NoteExportFormatter.FormatMarkdown(new[] { BuildNote("置顶笔记", "内容", true, sessionId: sessionId) });

        Assert.Contains("置顶", markdown);
        Assert.Contains($"来源问答：{sessionId}", markdown);
    }

    [Fact]
    public void Format_renders_sources_with_rank_location_and_snippet()
    {
        var documentId = Guid.NewGuid();
        var markdown = NoteExportFormatter.FormatMarkdown(new[]
        {
            BuildNote("带依据", "结论", sources: new[]
            {
                new NoteSource(documentId, "插件协议.md", 1, 0.834, "第一行\n第二行"),
                new NoteSource(documentId, "插件协议.md", -1, 0, null)
            })
        });

        Assert.Contains("### 引用资料（2 条）", markdown);
        Assert.Contains("- [1] 插件协议.md（第 2 段） · 相关度 83%", markdown);
        Assert.Contains($"  - 资料 ID：{documentId}", markdown);
        Assert.Contains("  - 摘录：第一行 第二行", markdown);
        // 未知段落号不写“第 0 段”，未知相似度不写相关度
        Assert.Contains("- [2] 插件协议.md", markdown);
        Assert.DoesNotContain("（第 0 段）", markdown);
    }

    [Fact]
    public void Format_omits_sources_block_when_there_are_none()
    {
        var markdown = NoteExportFormatter.FormatMarkdown(new[] { BuildNote("无依据", "内容") });

        Assert.DoesNotContain("引用资料", markdown);
    }

    [Fact]
    public void Format_ends_with_single_trailing_newline()
    {
        var markdown = NoteExportFormatter.FormatMarkdown(new[] { BuildNote("一篇", "内容") });

        Assert.EndsWith(Environment.NewLine, markdown);
        Assert.False(markdown.EndsWith(Environment.NewLine + Environment.NewLine));
    }
}
