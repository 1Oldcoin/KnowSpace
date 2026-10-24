using System.Security.Cryptography;
using System.Text;

namespace MyFirstMafAgent.Api.Domain.Agents;

/// <summary>
/// 一个被授权访问本机 MCP 服务端的外部智能体客户端。
///
/// <para>
/// 这是「谁可以接入、能做什么」的唯一真实来源。它<b>不</b>持有密钥本身，
/// 只持有密钥前缀（用于界面识别）与哈希（用于校验），因此记录泄露也不等于凭据泄露。
/// </para>
/// </summary>
public sealed record AgentClient(
    string Id,
    string Name,
    string? Description,
    string KeyPrefix,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<Guid> AllowedKnowledgeBaseIds,
    IReadOnlyList<string> AllowedTools,
    int RequestsPerMinute,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastSeenAt,
    string? LastSeenAddress)
{
    /// <summary>是否具备某项能力。</summary>
    public bool HasCapability(string required) =>
        Capabilities.Any(granted => AgentClientCapabilities.Satisfies(granted, required));

    /// <summary>工具是否在白名单内；空白名单表示「本能力范围内的工具全放开」。</summary>
    public bool IsToolAllowed(string toolName) =>
        AllowedTools.Count == 0 || AllowedTools.Contains(toolName, StringComparer.Ordinal);

    /// <summary>是否被限制了可访问的知识库。</summary>
    public bool IsKnowledgeBaseRestricted => AllowedKnowledgeBaseIds.Count > 0;

    /// <summary>是否可以访问指定知识库；未配置白名单表示不限制。</summary>
    public bool CanAccessKnowledgeBase(Guid knowledgeBaseId) =>
        !IsKnowledgeBaseRestricted || AllowedKnowledgeBaseIds.Contains(knowledgeBaseId);
}

/// <summary>
/// 客户端密钥的生成与摘要。
///
/// <para>
/// 密钥是 256 位随机值，不是用户口令，所以用一次 SHA-256 做摘要即可：
/// 不存在字典攻击面，也不需要 PBKDF2 那样的慢哈希（那会让每次 MCP 调用都变慢）。
/// </para>
///
/// <para>
/// 明文只在创建/轮换的响应里出现一次，之后系统里只剩哈希与前缀。
/// </para>
/// </summary>
public static class AgentClientKey
{
    /// <summary>可识别的固定前缀，便于在日志/泄露扫描中定位。</summary>
    public const string Prefix = "maf_";

    private const int SecretBytes = 32;
    private const int DisplayChars = 12;

    /// <summary>生成新密钥：返回明文、哈希与可展示前缀。</summary>
    public static (string Key, string Hash, string DisplayPrefix) Create()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var key = Prefix + Base64UrlEncode(secret);
        return (key, Hash(key), DisplayPrefix(key));
    }

    /// <summary>计算密钥摘要（SHA-256，十六进制小写）。</summary>
    public static string Hash(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>展示用前缀：足够区分多个客户端，又不足以暴力猜出密钥。</summary>
    public static string DisplayPrefix(string key) =>
        key.Length <= DisplayChars ? key : key[..DisplayChars];

    /// <summary>固定时间比较两个摘要，避免计时侧信道。</summary>
    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    /// <summary>从 <c>Authorization: Bearer xxx</c> 或 <c>X-Api-Key: xxx</c> 里取出候选密钥。</summary>
    public static string? ExtractFromHeaders(string? authorization, string? apiKeyHeader)
    {
        if (!string.IsNullOrWhiteSpace(authorization))
        {
            const string bearer = "Bearer ";
            var value = authorization.Trim();
            if (value.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            {
                var token = value[bearer.Length..].Trim();
                if (token.Length > 0) return token;
            }
        }

        return string.IsNullOrWhiteSpace(apiKeyHeader) ? null : apiKeyHeader.Trim();
    }

    /// <summary>看起来像不像本系统签发的密钥（用于快速短路，避免无谓的数据库查询）。</summary>
    public static bool LooksLikeKey(string? candidate) =>
        !string.IsNullOrEmpty(candidate)
        && candidate.StartsWith(Prefix, StringComparison.Ordinal)
        && candidate.Length is > DisplayChars and < 128;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
