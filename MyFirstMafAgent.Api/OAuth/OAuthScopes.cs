using MyFirstMafAgent.Api.Domain.Agents;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>
/// OAuth scope 与内部能力词表的映射。
///
/// <para>
/// <b>只有一套词表</b>：scope 的规范名就是 <see cref="AgentClientCapabilities"/> 里的能力名
/// （<c>knowledge:read</c>、<c>files:write</c> …）。多一套平行词表意味着多一个映射点，
/// 而映射点就是漏判点——「协议层批了、业务层不认」这种问题最难查。
/// </para>
///
/// <para>
/// 同时接受 <c>knowspace:read</c> 这类产品别名，写进同意页与 metadata 时统一用规范名。
/// </para>
/// </summary>
public static class OAuthScopes
{
    /// <summary>客户端一个 scope 都不请求时给的默认值（最小可用权限）。</summary>
    public const string DefaultScope = AgentClientCapabilities.KnowledgeRead;

    /// <summary>授权服务器声明的全部 scope（顺序稳定，便于快照测试）。</summary>
    public static readonly IReadOnlyList<string> Supported = AgentClientCapabilities.AllKnown;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["knowspace:read"] = AgentClientCapabilities.KnowledgeRead,
        ["knowspace:write"] = AgentClientCapabilities.KnowledgeWrite,
        ["knowspace:*"] = "knowledge:*",
        ["kb:read"] = AgentClientCapabilities.KnowledgeRead,
        ["kb:write"] = AgentClientCapabilities.KnowledgeWrite
    };

    /// <summary>把单个 scope 规范化为内部能力名；无法识别返回 null。</summary>
    public static string? Canonicalize(string? scope)
    {
        var value = scope?.Trim() ?? string.Empty;
        if (value.Length == 0) return null;

        if (Aliases.TryGetValue(value, out var alias)) return alias;

        if (AgentClientCapabilities.IsKnown(value)) return value;

        // knowledge:* / files:* 这类作用域通配也允许。
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && value.EndsWith("*", StringComparison.Ordinal))
        {
            var prefix = value[..separator];
            return AgentClientCapabilities.AllKnown.Any(known =>
                string.Equals(AgentClientCapabilities.ScopeOf(known), prefix, StringComparison.Ordinal))
                ? value
                : null;
        }

        return null;
    }

    /// <summary>解析空格分隔的 scope 参数，去重并丢弃不可识别的项。</summary>
    public static IReadOnlyList<string> Parse(string? scopeParameter)
    {
        if (string.IsNullOrWhiteSpace(scopeParameter)) return [];

        var result = new List<string>();
        foreach (var raw in scopeParameter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canonical = Canonicalize(raw);
            if (canonical is null) continue;
            if (!result.Contains(canonical, StringComparer.Ordinal)) result.Add(canonical);
        }

        return result;
    }

    /// <summary>解析并报告被丢弃的项（用于把「客户端请求了不存在的 scope」变成可读错误）。</summary>
    public static IReadOnlyList<string> ParseUnknown(string? scopeParameter)
    {
        if (string.IsNullOrWhiteSpace(scopeParameter)) return [];

        var unknown = new List<string>();
        foreach (var raw in scopeParameter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Canonicalize(raw) is null && !unknown.Contains(raw, StringComparer.Ordinal)) unknown.Add(raw);
        }

        return unknown;
    }

    /// <summary>
    /// 把批准的 scope 展开成能力列表。
    /// <c>knowledge:*</c> 展开为两项能力，而不是塞一个通配进去：
    /// 后续按能力逐个核对时，展开后的集合更容易审计。
    /// </summary>
    public static IReadOnlyList<string> ToCapabilities(IEnumerable<string> scopes)
    {
        var result = new List<string>();
        foreach (var scope in scopes)
        {
            if (string.Equals(scope, AgentClientCapabilities.All, StringComparison.Ordinal))
                return AgentClientCapabilities.AllKnown.ToList();

            if (scope.EndsWith(":*", StringComparison.Ordinal))
            {
                var prefix = scope[..^1]; // "knowledge:"
                foreach (var known in AgentClientCapabilities.AllKnown)
                {
                    if (known.StartsWith(prefix, StringComparison.Ordinal)
                        && !result.Contains(known, StringComparer.Ordinal))
                    {
                        result.Add(known);
                    }
                }

                continue;
            }

            if (!result.Contains(scope, StringComparer.Ordinal)) result.Add(scope);
        }

        return result;
    }

    /// <summary>是否包含至少一项写权限（同意页据此把写权限默认置灰/不勾选）。</summary>
    public static bool IsWriteScope(string scope) =>
        scope.EndsWith(":write", StringComparison.Ordinal)
        || string.Equals(scope, "write", StringComparison.Ordinal);

    /// <summary>是否包含写权限（展开后判断）。</summary>
    public static bool ContainsWriteScope(IEnumerable<string> scopes) => scopes.Any(IsWriteScope);

    /// <summary>规范化后按固定顺序输出，便于展示与比较。</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> scopes) =>
        scopes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => Array.IndexOf(AllScopesOrdered, scope) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(scope => scope, StringComparer.Ordinal)
            .ToList();

    private static readonly string[] AllScopesOrdered = [.. AgentClientCapabilities.AllKnown];
}
