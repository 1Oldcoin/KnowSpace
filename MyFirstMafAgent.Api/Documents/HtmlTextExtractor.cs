using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>
/// 轻量 HTML 正文抽取器：把 HTML/HTM 页面转换成纯文本，供知识库分块与向量化使用。
/// 不依赖第三方解析库，用正则加一轮线性扫描实现，够快也足够稳。
/// </summary>
public static partial class HtmlTextExtractor
{
    /// <summary>结构标签统一换成 LF，最后在 Normalize 里做空白折叠。</summary>
    private const string LineBreak = "\n";

    /// <summary>没有正文时返回空字符串，由调用方按"无法抽取文本"处理。</summary>
    public static string ExtractText(string html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;

        var text = RemoveNonContent(html);
        text = ConvertStructureTags(text);
        text = StripRemainingTags(text);
        text = WebUtility.HtmlDecode(text);
        return Normalize(text);
    }

    /// <summary>从文件读取并抽取正文：按 BOM / meta charset / UTF-8 严格校验 / GB18030 回退识别编码。</summary>
    public static async Task<string> ExtractTextFromFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return ExtractText(DecodeHtml(bytes));
    }

    /// <summary>按字节解码 HTML：BOM 优先，其次 &lt;meta charset&gt;，再严格校验 UTF-8，最后回退 GB18030。</summary>
    public static string DecodeHtml(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // 页面头部通常是最完整的 ascii 片段，够用来找 meta charset。
        var probeLength = Math.Min(bytes.Length, 8192);
        var probe = Encoding.Latin1.GetString(bytes, 0, probeLength);
        var metaEncoding = DetectMetaEncoding(probe);
        if (metaEncoding is not null)
        {
            try
            {
                return metaEncoding.GetString(bytes);
            }
            catch (ArgumentException)
            {
                // 不认识的 charset 名称，继续走默认探测。
            }
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }

    /// <summary>识别 &lt;meta charset&gt; 与 &lt;meta http-equiv="Content-Type" content="...charset=..."&gt;。</summary>
    private static Encoding? DetectMetaEncoding(string probe)
    {
        var match = MetaCharsetRegex().Match(probe);
        if (!match.Success) match = MetaContentTypeRegex().Match(probe);
        if (!match.Success) return null;
        return ResolveEncoding(match.Groups["charset"].Value.Trim());
    }

    private static Encoding? ResolveEncoding(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var normalized = name.Replace("_", "-").Trim('"', '\'').ToLowerInvariant();
        switch (normalized)
        {
            case "utf-8":
            case "utf8":
                return new UTF8Encoding(false);
            case "gb18030":
            case "gbk":
            case "gb2312":
            case "gb-2312":
            case "hz-gb-2312":
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030");
            case "big5":
            case "big-5":
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("big5");
            case "iso-8859-1":
            case "latin1":
                return Encoding.Latin1;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(normalized);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>剔除脚本、样式、内嵌框架、SVG、页面头部等内容与注释。</summary>
    private static string RemoveNonContent(string html)
    {
        var text = CommentRegex().Replace(html, " ");
        text = NonContentBlockRegex().Replace(text, " ");
        // 未闭合的 <script>/<style> 截断到结尾，避免脚本源码混入正文。
        text = UnterminatedBlockRegex().Replace(text, " ");
        return text;
    }

    /// <summary>
    /// 结构标签转文本布局：标题逐级加 "#" 前缀、列表项加 "- "、表格行内单元格用 " | " 连接、
    /// 其余块级标签与 &lt;br&gt; 一律换成换行。顺序很关键：先处理标题（并同时消费闭环标签），
    /// 再做块级替换，避免闭环标签在标题前缀之后再插一次换行。
    /// </summary>
    private static string ConvertStructureTags(string html)
    {
        // 1) 标题：开标签换成「换行 + 层级前缀」，闭环标签换成换行。
        //    分两步做，避免用非贪婪的跨标签匹配（容易把相邻标题一并吞掉）。
        var text = HeadingOpenTagRegex().Replace(html, match =>
            LineBreak + new string('#', match.Groups["level"].Value[0] - '0') + " ");
        text = HeadingCloseTagRegex().Replace(text, LineBreak);

        // 2) 表格：先按行切分（tr 开闭、table 段落标签都换成换行），
        //    再把同一行内紧邻的「</td><td>」换成 " | "。由于行已被换行隔开，
        //    单元格边界不可能跨行匹配，不会把两条记录拼成一条。
        text = TableRowRegex().Replace(text, LineBreak);
        text = CellBoundaryRegex().Replace(text, " | ");

        // 3) 列表项与换行标签。
        text = ListItemOpenTagRegex().Replace(text, LineBreak + "- ");
        text = ListItemCloseTagRegex().Replace(text, LineBreak);
        text = BrAndRuleRegex().Replace(text, LineBreak);

        // 4) 其余块级标签（已不含 h1-h6，标题在步骤 1 处理完毕）。
        return BlockTagRegex().Replace(text, LineBreak);
    }

    private static string StripRemainingTags(string html) => AnyTagRegex().Replace(html, string.Empty);

    /// <summary>
    /// 折行归一：CRLF/CR 统一成 LF，行内空白压缩、去掉首尾空白与表格竖线，
    /// 连续空行压成一个（段落之间保留一个空行），输出不带尾部空白。
    /// </summary>
    private static string Normalize(string text)
    {
        if (text.Length == 0) return string.Empty;

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        // 先把每行规整好，再按「行」而不是按「段落」拼接：
        // 相邻两行之间必定有一个换行；中间出现过空行时插入一个额外换行（即段落间隔 \n\n）。
        var cleaned = lines
            .Select(raw => TrimCellPipes(HorizontalSpaceRegex().Replace(raw, " ").Trim()))
            .ToArray();

        var builder = new StringBuilder(text.Length);
        var hasPreviousLine = false;
        var blankSeen = false;
        foreach (var line in cleaned)
        {
            if (line.Length == 0)
            {
                if (hasPreviousLine) blankSeen = true;
                continue;
            }

            if (hasPreviousLine) builder.Append(blankSeen ? "\n\n" : "\n");
            builder.Append(line);
            hasPreviousLine = true;
            blankSeen = false;
        }

        return builder.ToString();
    }

    /// <summary>去掉行首尾的多余表格竖线（例如 "| 甲 | 乙 |" → "甲 | 乙"）。</summary>
    private static string TrimCellPipes(string line)
    {
        var trimmed = line.Trim();
        while (trimmed.StartsWith('|')) trimmed = trimmed[1..].TrimStart();
        while (trimmed.EndsWith('|')) trimmed = trimmed[..^1].TrimEnd();
        return trimmed;
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"<(script|style|noscript|iframe|svg|head|template|title)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContentBlockRegex();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UnterminatedBlockRegex();

    [GeneratedRegex(@"<br\s*/?>|<hr\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrAndRuleRegex();

    [GeneratedRegex(@"<h(?<level>[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingOpenTagRegex();

    [GeneratedRegex(@"</h[1-6]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingCloseTagRegex();

    /// <summary>表格行（tr）边界以及表格结束，统一换成换行，使每条记录独占一行。</summary>
    [GeneratedRegex(@"</?tr\b[^>]*>|</?table\b[^>]*>|</?thead\b[^>]*>|</?tbody\b[^>]*>|</?tfoot\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex TableRowRegex();

    /// <summary>
    /// 单元格边界：「结束单元格标签 + 空白 + 开单元格标签」换成分隔符。
    /// 空白限定为不含换行的字符类，保证只连接同一行内的相邻单元格。
    /// </summary>
    [GeneratedRegex(@"</t[dh]>[ \t]*<t[dh]\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex CellBoundaryRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemOpenTagRegex();

    [GeneratedRegex(@"</li\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemCloseTagRegex();

    /// <summary>其余块级标签换成换行。标题已在 HeadingTagRegex 处理，表格行由 TableRowRegex 处理，这里不再重复。</summary>
    [GeneratedRegex(@"</?(p|div|section|article|header|footer|main|aside|nav|blockquote|pre|ul|ol|dl|dt|dd|caption|figure|figcaption|form|fieldset|address)\b[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.Singleline)]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[ \t\f\v\u00a0\u3000]+")]
    private static partial Regex HorizontalSpaceRegex();

    [GeneratedRegex(@"<meta\b[^>]*charset\s*=\s*[\u0022\u0027]?\s*(?<charset>[A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetRegex();

    [GeneratedRegex(@"<meta\b[^>]*content\s*=\s*[\u0022\u0027][^\u0022\u0027]*charset\s*=\s*(?<charset>[A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContentTypeRegex();
}
