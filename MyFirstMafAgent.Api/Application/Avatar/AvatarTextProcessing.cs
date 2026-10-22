using System.Text;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 分身记忆的轻量词面相似度：中文按二元组、拉丁词按整词切分。
/// 向量检索不可用时仍能工作，二者结果在 <see cref="AvatarMemoryRetriever"/> 中融合。
/// </summary>
public static class AvatarTextSimilarity
{
    public static Dictionary<string, int> BuildGrams(string? text)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            else builder.Append(' ');
        }

        foreach (var token in builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 1)
            {
                AddGram(result, token);
                continue;
            }

            if (token.All(character => character < 128))
            {
                AddGram(result, token);
                continue;
            }

            for (var index = 0; index + 1 < token.Length; index++)
                AddGram(result, token.Substring(index, 2));
        }

        return result;
    }

    /// <summary>标题命中权重更高，正文命中按次数封顶，避免长文本碾压短记忆。</summary>
    public static double Score(Dictionary<string, int> queryGrams, string title, string content)
    {
        if (queryGrams.Count == 0) return 0;

        var titleGrams = BuildGrams(title);
        var contentGrams = BuildGrams(content);
        double score = 0;

        foreach (var gram in queryGrams.Keys)
        {
            if (titleGrams.ContainsKey(gram)) score += 3;
            if (contentGrams.TryGetValue(gram, out var hits) && hits > 0)
                score += Math.Min(hits, 4);
        }

        return score;
    }

    private static void AddGram(Dictionary<string, int> grams, string gram) =>
        grams[gram] = grams.GetValueOrDefault(gram) + 1;

    /// <summary>二元组 Jaccard 相似度，用于判断两条记忆是不是同一件事。</summary>
    public static double Jaccard(string? left, string? right)
    {
        var leftGrams = BuildGrams(left);
        var rightGrams = BuildGrams(right);
        if (leftGrams.Count == 0 || rightGrams.Count == 0) return 0;

        var intersection = 0;
        foreach (var gram in leftGrams.Keys)
        {
            if (rightGrams.ContainsKey(gram)) intersection++;
        }

        var union = leftGrams.Count + rightGrams.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }
}

/// <summary>
/// 把自由文本切成适合模型整理的段落。不要求用户按任何格式填写：
/// 优先按空行分段，过长时再按长度截断。
/// </summary>
public static class AvatarTextSplitter
{
    public const int DefaultMaxSegmentChars = 3_000;
    public const int DefaultMaxSegments = 12;

    public static IReadOnlyList<string> Split(
        string? text,
        int maxSegmentChars = DefaultMaxSegmentChars,
        int maxSegments = DefaultMaxSegments)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            var value = buffer.ToString().Trim();
            buffer.Clear();
            if (value.Length == 0) return;
            if (value.Length > maxSegmentChars) value = value[..maxSegmentChars];
            segments.Add(value);
        }

        foreach (var line in normalized.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                if (segments.Count >= maxSegments) return segments;
                continue;
            }

            if (buffer.Length + line.Length + 1 > maxSegmentChars) Flush();
            if (buffer.Length > 0) buffer.Append('\n');
            buffer.Append(line);
        }

        Flush();
        return segments.Take(maxSegments).ToList();
    }
}
