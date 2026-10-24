using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>媒体时间轴上的一个片段：某一时刻的一句话（转写）或一帧画面的描述。</summary>
/// <param name="Start">片段在媒体中的起始时间。</param>
/// <param name="Text">片段文本。</param>
public sealed record MediaSegment(TimeSpan Start, string Text);

/// <summary>
/// 音视频的时间轴切片：把「转写 + 画面描述」按时间排好，再切成**每片都以时间戳开头**的切片。
///
/// <para>
/// 为什么不让通用分块器直接切：通用分块按字符数在标点处断句，切断之后一个切片可能从
/// 某句话的中间开始，前面那句话的时间戳就丢了——用户看到引用却不知道跳到第几分钟。
/// 这里的切片边界只落在片段之间，所以「切片的第一行时间戳」就是这一片的起始时间，
/// 可以无歧义地回填到引用里，前端据此 <c>seek</c>。
/// </para>
///
/// <para>
/// 反过来，切片里的时间戳也是**可检索的**：问「三分半的时候讲了什么」能在词法检索里命中。
/// </para>
/// </summary>
public static class MediaTimeline
{
    /// <summary>单片目标字符数。比纯文本分块小一些，因为时间轴切片要保证时间上的局部性。</summary>
    public const int DefaultMaxChars = 900;

    /// <summary>
    /// 匹配行首或任意位置的 <c>[hh:mm:ss]</c> / <c>[hh:mm:ss-mm:ss]</c>。
    /// 小时允许两位以上（超长录音/直播回放）。
    /// </summary>
    private static readonly Regex TimeMarkerPattern = new(
        @"\[(?<hours>\d{1,4}):(?<minutes>\d{2}):(?<seconds>\d{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 把片段按时间排序后合并成切片。每片以 <c>[hh:mm:ss]</c> 开头。
    /// 空片段被丢弃；全空时返回空列表（调用方据此判断"没有任何可用内容"）。
    /// </summary>
    public static IReadOnlyList<string> BuildChunks(
        IReadOnlyList<MediaSegment> segments,
        int maxChars = DefaultMaxChars)
    {
        if (segments.Count == 0) return [];
        if (maxChars <= 0) maxChars = DefaultMaxChars;

        var ordered = segments
            .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.Start)
            .ToList();
        if (ordered.Count == 0) return [];

        var chunks = new List<string>();
        var builder = new StringBuilder();

        foreach (var segment in ordered)
        {
            var line = $"[{FormatTime(segment.Start)}] {Collapse(segment.Text)}";

            // 单行超过上限时仍然自成一片（宁可超一点，也不能把时间戳和文本分开）。
            if (builder.Length > 0 && builder.Length + line.Length + 1 > maxChars)
            {
                chunks.Add(builder.ToString());
                builder.Clear();
            }

            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }

        if (builder.Length > 0) chunks.Add(builder.ToString());
        return chunks;
    }

    /// <summary>
    /// 从切片文本里读出起始时间（第一个 <c>[hh:mm:ss]</c>）。
    /// 读到就返回 true；纯文本切片（摘要、普通资料）返回 false，前端据此决定要不要显示"跳转"控件。
    /// </summary>
    public static bool TryReadStartSeconds(string? content, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(content)) return false;

        var match = TimeMarkerPattern.Match(content);
        if (!match.Success) return false;

        var hours = int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture);
        var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        var secs = int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        if (minutes is < 0 or > 59 || secs is < 0 or > 59) return false;

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }

    /// <summary>
    /// 读出切片起始秒数；纯文本切片返回 null。
    ///
    /// <para>
    /// <b>0 是合法值</b>：媒体切片的第一片就从 <c>[00:00:00]</c> 开始。
    /// 曾经把 0 当作"没有时间戳"返回 null，结果从 0 秒开始的整片引用拿不到跳转点，
    /// 前端只能把可播放的引用当成普通文字片段处理。null 只能表示"这不是媒体切片"。
    /// </para>
    /// </summary>
    public static int? ReadStartSeconds(string? content) =>
        TryReadStartSeconds(content, out var seconds) ? seconds : null;

    public static string FormatTime(TimeSpan time)
    {
        var totalSeconds = Math.Max(0, (long)time.TotalSeconds);
        return $"{totalSeconds / 3600:00}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}";
    }

    /// <summary>把多行文本压成一行：切片里保留换行会让"一片一行"的假设失效。</summary>
    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
