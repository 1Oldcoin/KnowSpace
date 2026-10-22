using System.Text;
using MyFirstMafAgent.Api.Domain.Avatar;

namespace MyFirstMafAgent.Api.Application.Avatar;

/// <summary>
/// 碎片素材的轻量检索器。中文按二元词组切分，配合标题加权与时间衰减，
/// 用于在分身问答时挑出与问题最相关的记忆，不依赖向量索引（与知识库完全隔离）。
/// </summary>
public static class AvatarFragmentRetriever
{
    private const int MaxFragmentChars = 1_200;

    public static IReadOnlyList<(AvatarFragment Fragment, double Score)> Rank(
        string query,
        IReadOnlyList<AvatarFragment> fragments,
        int take)
    {
        var grams = BuildGrams(query);
        if (grams.Count == 0 || fragments.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;
        var scored = new List<(AvatarFragment Fragment, double Score)>(fragments.Count);

        foreach (var fragment in fragments)
        {
            var titleGrams = BuildGrams(fragment.Title);
            var contentGrams = BuildGrams(fragment.Content);
            double score = 0;

            foreach (var gram in grams.Keys)
            {
                if (titleGrams.ContainsKey(gram)) score += 3;
                if (contentGrams.TryGetValue(gram, out var contentHits) && contentHits > 0)
                    score += Math.Min(contentHits, 4);
            }

            if (score <= 0) continue;

            // 近 90 天内的记忆略微加权，避免旧素材淹没当下状态。
            var ageDays = Math.Max(0, (now - fragment.OccurredAt).TotalDays);
            score *= 1 + Math.Max(0, (90 - ageDays) / 90) * 0.15;

            scored.Add((fragment, score));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Fragment.OccurredAt)
            .Take(Math.Clamp(take, 1, 30))
            .ToList();
    }

    /// <summary>把碎片压缩成可放进提示词的片段。</summary>
    public static string Render(AvatarFragment fragment)
    {
        var content = fragment.Content.Trim();
        if (content.Length > MaxFragmentChars) content = content[..MaxFragmentChars] + "…";
        return $"[{AvatarFragmentKinds.Label(fragment.Kind)} · {fragment.OccurredAt:yyyy-MM-dd} · {fragment.Title}]\n{content}";
    }

    private static Dictionary<string, int> BuildGrams(string text)
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

            // 拉丁词整体作为一个词元；中文按二元组切分。
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

    private static void AddGram(Dictionary<string, int> grams, string gram) =>
        grams[gram] = grams.GetValueOrDefault(gram) + 1;
}
