using System.Text;

namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>
/// 中文友好的轻量分词器，供全文（词法）检索使用。
///
/// <para>
/// PostgreSQL 自带的 <c>simple</c> 解析器不理解中文：一段没有空格的中文会被切成一个词，
/// "插件协议" 与 "插件" 之间永远匹配不上；而 pg0 自带的 PG 并不包含 <c>zhparser</c> / <c>pg_jieba</c>。
/// 因此这里在应用侧把中文切成 <b>二元组（bigram）</b> 并用空格分隔后写入
/// <c>kb_chunks.lexical_tokens</c>，再由 PostgreSQL 的 <c>to_tsvector('simple', …)</c> 建 GIN 索引。
/// </para>
///
/// <para>
/// 规则：
/// 1. 连续的中日韩字符切成相邻两字组合（长度 1 的孤立字保留单字）；
/// 2. 连续的拉丁字母 / 数字整体保留并转小写（"JSON-RPC" → "json" + "rpc"）；
/// 3. 其它字符（标点、空白、符号）视为分隔符，不进入索引。
/// </para>
/// </summary>
public static class LexicalTokens
{
    /// <summary>单个切片最多保留的 token 数；超长切片不该让索引无限膨胀。</summary>
    public const int MaxTokensPerChunk = 4_096;

    /// <summary>查询最多使用的 token 数；太多会被 OR 稀释成噪声。</summary>
    public const int MaxQueryTokens = 64;

    /// <summary>把文本切成检索 token。</summary>
    public static IReadOnlyList<string> Tokenize(string? text, int maxTokens = MaxTokensPerChunk)
    {
        if (string.IsNullOrWhiteSpace(text) || maxTokens <= 0) return Array.Empty<string>();

        var tokens = new List<string>();
        var latin = new StringBuilder();
        var cjk = new List<char>(8);

        void FlushLatin()
        {
            if (latin.Length == 0) return;
            tokens.Add(latin.ToString().ToLowerInvariant());
            latin.Clear();
        }

        void FlushCjk()
        {
            if (cjk.Count == 0) return;
            if (cjk.Count == 1)
            {
                tokens.Add(cjk[0].ToString());
            }
            else
            {
                for (var index = 0; index + 1 < cjk.Count; index++)
                    tokens.Add(new string(new[] { cjk[index], cjk[index + 1] }));
            }
            cjk.Clear();
        }

        foreach (var character in text)
        {
            if (IsCjk(character))
            {
                FlushLatin();
                cjk.Add(character);
            }
            else if (char.IsLetterOrDigit(character))
            {
                FlushCjk();
                latin.Append(character);
            }
            else
            {
                FlushLatin();
                FlushCjk();
            }

            if (tokens.Count >= maxTokens)
            {
                FlushLatin();
                FlushCjk();
                return tokens.Count > maxTokens ? tokens.GetRange(0, maxTokens) : tokens;
            }
        }

        FlushLatin();
        FlushCjk();

        if (tokens.Count > maxTokens) tokens.RemoveRange(maxTokens, tokens.Count - maxTokens);
        return tokens;
    }

    /// <summary>把文本转成写入 <c>kb_chunks.lexical_tokens</c> 的空格分隔 token 串。</summary>
    public static string Build(string? text) => string.Join(' ', Tokenize(text));

    /// <summary>
    /// 构造 <c>to_tsquery('simple', …)</c> 的表达式：token 之间用 OR 连接，
    /// 由 <c>ts_rank</c> 负责"命中越多越靠前"。token 只含字母/数字/中日韩字符，不会引入 tsquery 语法字符。
    /// </summary>
    public static string? BuildTsQuery(string? query)
    {
        var tokens = Tokenize(query, MaxQueryTokens);
        if (tokens.Count == 0) return null;

        var distinct = new List<string>(tokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
            if (seen.Add(token)) distinct.Add(token);

        return string.Join(" | ", distinct);
    }

    /// <summary>中日韩统一表意文字（含扩展 A、兼容区）、假名与韩文。</summary>
    private static bool IsCjk(char character) =>
        character is >= '\u4E00' and <= '\u9FFF'          // CJK Unified Ideographs
            or >= '\u3400' and <= '\u4DBF'                // Extension A
            or >= '\uF900' and <= '\uFAFF'                // Compatibility Ideographs
            or >= '\u3040' and <= '\u30FF'                // Hiragana + Katakana
            or >= '\uAC00' and <= '\uD7AF';               // Hangul Syllables
}
