using System.Text;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Domain.Notes;

/// <summary>
/// 正文里的一段内容：纯文本，或一个引用锚点。
///
/// <para>
/// 锚点 <see cref="Indices"/> 是 1 起的资料序号，对应 <see cref="Note.Sources"/> 的下标 +1。
/// <see cref="Dangling"/> 是"写了但对应的引用不存在"的序号——它必须被单独保留，
/// 既不能悄悄丢掉（用户会以为自己写错了），也不能当成可点击的链接（点了也没有去处）。
/// </para>
/// </summary>
public sealed record NoteContentSegment(
    string Text,
    bool IsAnchor = false,
    IReadOnlyList<int>? Indices = null,
    IReadOnlyList<int>? Dangling = null);

/// <summary>
/// 笔记正文里的引用锚点：<c>[1]</c>、<c>[2,3]</c>。
///
/// <para>
/// 为什么需要它：笔记常常是"从问答里带出来的结论"，卡片上的引用 chip 只能说明
/// "这篇笔记用过哪几段资料"，说明不了"这句话出自哪一段"。长笔记里要核对某一句的
/// 出处，只能自己从头找起。锚点把"句 → 段"这条线显式写下来。
/// </para>
///
/// <para>
/// 语法刻意做得很窄：只认方括号包住的 1-2 位数字（可逗号分组）。这样 <c>[2024](url)</c>
/// 这类链接、<c>[TODO]</c> 这类标签都不会被误判成锚点。
/// </para>
/// </summary>
public static partial class NoteCitationAnchor
{
    /// <summary>锚点序号上限：引用最多 20 条，超出必然是笔误。</summary>
    public const int MaxAnchorIndex = Note.MaxSourceCount;

    [GeneratedRegex(@"\[(?<list>\d{1,2}(?:\s*[,，、]\s*\d{1,2})*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorPattern();

    /// <summary>
    /// 把正文切成「文本 / 锚点」片段。<paramref name="sourceCount"/> 用来判定哪些序号是悬空的。
    /// 没有任何锚点时返回单个文本片段（调用方无需特判）。
    /// </summary>
    public static IReadOnlyList<NoteContentSegment> Segment(string? content, int sourceCount)
    {
        if (string.IsNullOrEmpty(content)) return [];

        var segments = new List<NoteContentSegment>();
        var cursor = 0;

        foreach (Match match in AnchorPattern().Matches(content))
        {
            var parsed = ParseIndices(match.Groups["list"].Value);
            if (parsed.Count == 0) continue; // 全部越界：当普通文本处理

            if (match.Index > cursor)
                segments.Add(new NoteContentSegment(content[cursor..match.Index]));

            var valid = parsed.Where(index => index <= sourceCount).ToList();
            var dangling = parsed.Where(index => index > sourceCount).ToList();

            // at least one resolvable index → render as an anchor, keep the dangling part visible
            if (valid.Count > 0)
                segments.Add(new NoteContentSegment(match.Value, true, valid, dangling.Count > 0 ? dangling : null));
            else
                segments.Add(new NoteContentSegment(match.Value, false, null, dangling));

            cursor = match.Index + match.Length;
        }

        if (cursor < content.Length) segments.Add(new NoteContentSegment(content[cursor..]));
        return MergeText(segments);
    }

    /// <summary>
    /// 合并相邻的纯文本片段。悬空锚点被当成文本时会出现“文字 + [5]”两段相邻的情况，
    /// 合并之后调用方拿到的仍是一整句，不必自己拼接。
    /// </summary>
    private static List<NoteContentSegment> MergeText(List<NoteContentSegment> segments)
    {
        if (segments.Count < 2) return segments;

        var merged = new List<NoteContentSegment>(segments.Count);
        foreach (var segment in segments)
        {
            var previous = merged.Count > 0 ? merged[^1] : null;
            if (previous is not null && !previous.IsAnchor && !segment.IsAnchor)
            {
                var dangling = (previous.Dangling ?? []).Concat(segment.Dangling ?? []).ToArray();
                merged[^1] = new NoteContentSegment(
                    previous.Text + segment.Text, false, null, dangling.Length > 0 ? dangling : null);
                continue;
            }

            merged.Add(segment);
        }

        return merged;
    }

    /// <summary>正文里真正引用到的资料序号（1 起，去重且升序，只含存在的引用）。</summary>
    public static IReadOnlyList<int> ReferencedSourceIndices(string? content, int sourceCount)
    {
        if (string.IsNullOrEmpty(content) || sourceCount <= 0) return [];

        var found = new SortedSet<int>();
        foreach (Match match in AnchorPattern().Matches(content))
        {
            foreach (var index in ParseIndices(match.Groups["list"].Value))
            {
                if (index <= sourceCount) found.Add(index);
            }
        }

        return found.Count == 0 ? [] : found.ToList();
    }

    /// <summary>悬空锚点个数（写错序号、或删掉了某条引用却没改正文）。</summary>
    public static int CountDangling(string? content, int sourceCount)
    {
        if (string.IsNullOrEmpty(content)) return 0;

        var count = 0;
        foreach (Match match in AnchorPattern().Matches(content))
        {
            foreach (var index in ParseIndices(match.Groups["list"].Value))
            {
                if (index > sourceCount) count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 导出用的转换：把有效锚点写成 Markdown 脚注引用 <c>[^1]</c>。
    /// 悬空锚点原样保留（导出稿里看得见才是对的），链接、标签不受影响。
    /// </summary>
    public static string ToMarkdownReferences(string? content, int sourceCount)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        return AnchorPattern().Replace(content, match =>
        {
            var parsed = ParseIndices(match.Groups["list"].Value);
            if (parsed.Count == 0) return match.Value;

            var valid = parsed.Where(index => index <= sourceCount).ToList();
            if (valid.Count == 0) return match.Value;

            var builder = new StringBuilder(valid.Count * 5);
            foreach (var index in valid) builder.Append($"[^{index}]");
            return builder.ToString();
        });
    }

    /// <summary>解析一串序号；越界值直接丢弃（<c>[0]</c>、<c>[99]</c> 都不是有效锚点）。</summary>
    private static List<int> ParseIndices(string list)
    {
        var indices = new List<int>();
        foreach (var part in list.Split([',', '，', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var index)) continue;
            if (index < 1 || index > MaxAnchorIndex) continue;
            if (!indices.Contains(index)) indices.Add(index);
        }

        return indices;
    }
}
