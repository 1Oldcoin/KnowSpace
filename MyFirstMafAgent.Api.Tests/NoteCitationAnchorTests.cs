using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Domain.Notes;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// T-5「笔记正文引用锚点」的规则。
/// 关注四点：锚点要能解析出"第几条引用"、悬空锚点不能变成死链、导出要变成真脚注、
/// 以及没有锚点的老笔记导出格式不能变（否则旧导出稿全都变样）。
/// </summary>
public sealed class NoteCitationAnchorTests
{
    private static readonly Guid KnowledgeBaseId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Segment_splits_text_and_anchor()
    {
        var segments = NoteCitationAnchor.Segment("结论是这样[1]，原因见后文。", 2);

        Assert.Equal(3, segments.Count);
        Assert.Equal("结论是这样", segments[0].Text);
        Assert.False(segments[0].IsAnchor);
        Assert.True(segments[1].IsAnchor);
        Assert.Equal(new[] { 1 }, segments[1].Indices);
        Assert.Equal("，原因见后文。", segments[2].Text);
    }

    [Fact]
    public void Grouped_anchor_yields_every_index()
    {
        var segments = NoteCitationAnchor.Segment("两种说法[1,3]", 3);

        var anchor = Assert.Single(segments, segment => segment.IsAnchor);
        Assert.Equal(new[] { 1, 3 }, anchor.Indices);
        Assert.Null(anchor.Dangling);
    }

    [Fact]
    public void Dangling_anchor_is_kept_as_text_so_the_user_can_see_the_typo()
    {
        var segments = NoteCitationAnchor.Segment("出处[5]", 2);

        // 相邻纯文本会合并：用户看到的是一整句，只是其中的 [5] 标成悬空。
        var segment = Assert.Single(segments);
        Assert.False(segment.IsAnchor);
        Assert.Equal("出处[5]", segment.Text);
        Assert.Equal(new[] { 5 }, segment.Dangling);
    }

    [Fact]
    public void Mixed_group_keeps_the_resolvable_part_and_flags_the_rest()
    {
        var segments = NoteCitationAnchor.Segment("比对[1,5]", 2);

        var anchor = Assert.Single(segments, segment => segment.IsAnchor);
        Assert.Equal(new[] { 1 }, anchor.Indices);
        Assert.Equal(new[] { 5 }, anchor.Dangling);
    }

    [Fact]
    public void Links_and_tags_are_not_anchors()
    {
        // "[2024](url)" 是链接，"[TODO]" 是标签，两者都不该被当成引用。
        Assert.Empty(NoteCitationAnchor.ReferencedSourceIndices("见 [2024](https://example.invalid) 与 [TODO]", 5));
        Assert.Equal(0, NoteCitationAnchor.CountDangling("见 [2024](https://example.invalid) 与 [TODO]", 5));
    }

    [Fact]
    public void Referenced_indices_are_distinct_and_sorted()
    {
        var indices = NoteCitationAnchor.ReferencedSourceIndices("先说[3]，再说[1]，重复[3]，越界[9]", 3);

        Assert.Equal(new[] { 1, 3 }, indices);
        Assert.Equal(1, NoteCitationAnchor.CountDangling("先说[3]，再说[1]，重复[3]，越界[9]", 3));
    }

    [Fact]
    public void Zero_and_out_of_range_markers_are_not_anchors()
    {
        Assert.Empty(NoteCitationAnchor.ReferencedSourceIndices("[0] 与 [99]", 5));
        Assert.Equal(0, NoteCitationAnchor.CountDangling("[0] 与 [99]", 5));
    }

    [Theory]
    [InlineData("结论[1]，补充[2]", 2, "结论[^1]，补充[^2]")]
    [InlineData("两种来源[1,2]", 2, "两种来源[^1][^2]")]
    [InlineData("悬空[5]", 2, "悬空[5]")]
    [InlineData("链接 [2024](https://example.invalid)", 2, "链接 [2024](https://example.invalid)")]
    [InlineData("没有锚点", 2, "没有锚点")]
    public void Markdown_export_turns_valid_anchors_into_footnotes(string content, int sourceCount, string expected)
    {
        Assert.Equal(expected, NoteCitationAnchor.ToMarkdownReferences(content, sourceCount));
    }

    [Fact]
    public void Export_uses_footnote_definitions_when_the_body_has_anchors()
    {
        var note = BuildNote("结论来自这两段[1]，反面证据见[2]。", 3);

        var markdown = NoteExportFormatter.FormatMarkdown(new[] { note });

        Assert.Contains("结论来自这两段[^1]，反面证据见[^2]。", markdown, StringComparison.Ordinal);
        Assert.Contains("[^1]: a.md（第 2 段） · 相关度 82%", markdown, StringComparison.Ordinal);
        Assert.Contains("[^2]: b.md（第 1 段） · 资料 ID：", markdown, StringComparison.Ordinal);
        // 第 3 条没被正文引用 → 单独列出，不能静默丢掉
        Assert.Contains("未被正文引用的资料：", markdown, StringComparison.Ordinal);
        Assert.Contains("- [3] c.md", markdown, StringComparison.Ordinal);
        // 脚注模式下不再输出旧的编号列表，避免同一份导出里出现两套编号
        Assert.DoesNotContain("### 引用资料", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_keeps_the_old_bullet_list_when_the_body_has_no_anchors()
    {
        var note = BuildNote("纯文字笔记，没有写锚点。", 2);

        var markdown = NoteExportFormatter.FormatMarkdown(new[] { note });

        Assert.Contains("### 引用资料（2 条）", markdown, StringComparison.Ordinal);
        Assert.Contains("- [1] a.md（第 2 段） · 相关度 82%", markdown, StringComparison.Ordinal);
        Assert.Contains("  - 资料 ID：", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("[^1]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Note_without_sources_export_is_unchanged()
    {
        var note = Note.Create(KnowledgeBaseId, "无引用", "正文[1]里写了锚点但没有引用。", null, null, null, null, Now);

        var markdown = NoteExportFormatter.FormatMarkdown(new[] { NoteDto.FromDomain(note) });

        Assert.Contains("正文[1]里写了锚点但没有引用。", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("引用资料", markdown, StringComparison.Ordinal);
    }

    private static NoteDto BuildNote(string content, int sourceCount)
    {
        var sources = new List<NoteSource>
        {
            new(Guid.NewGuid(), "a.md", 1, 0.82, "第一段摘录"),
            new(Guid.NewGuid(), "b.md", 0, 0, null),
            new(Guid.NewGuid(), "c.md", 4, 0.41, "第三段摘录")
        }.Take(sourceCount).ToList();

        var note = Note.Create(KnowledgeBaseId, "锚点笔记", content, null, null, null, null, Now, sources: sources);
        return NoteDto.FromDomain(note);
    }
}
