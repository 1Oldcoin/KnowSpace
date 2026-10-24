using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MyFirstMafAgent.Plugins.Sdk;

/// <summary>
/// 平台连接器提交给核心的统一摄取数据。
/// 插件<b>不</b>直接写数据库：核心据此调用既有摄取用例，复用版本、幂等、Outbox 与索引生命周期。
/// </summary>
public sealed record PlatformIngestionItem
{
    /// <summary>来源标识，通常等于插件 ID，用于溯源与去重分区。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    /// <summary>平台内的稳定 ID。同一 source + externalId 视为同一条内容。</summary>
    [JsonPropertyName("externalId")]
    public string ExternalId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>内容类型：note / article / message / file。</summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = PlatformItemTypes.Note;

    /// <summary>可被检索的正文（纯文本或 Markdown）。</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>平台侧最后更新时间，用于判断是否需要重新摄取。</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>内容哈希（sha256 十六进制）。用于幂等与变更检测；为空时核心会自行计算。</summary>
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }

    /// <summary>平台侧已删除/撤回。核心据此移除或标记对应内容，而不是忽略。</summary>
    [JsonPropertyName("retracted")]
    public bool Retracted { get; init; }

    /// <summary>额外元数据（作者、URL、标签等）。只用于展示与调试，不参与检索加权。</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>计算内容哈希（UTF-8 + SHA-256，小写十六进制）。</summary>
    public static string ComputeContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>结构校验。失败项返回用户可读原因，核心会拒绝该条目并记录审计。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Source))
            errors.Add("source is required");
        if (string.IsNullOrWhiteSpace(ExternalId))
            errors.Add("externalId is required");
        else if (ExternalId.Length > 256)
            errors.Add("externalId must be at most 256 characters");

        if (!PlatformItemTypes.IsKnown(Type))
            errors.Add($"type '{Type}' is unknown; expected one of {PlatformItemTypes.All}");

        if (!Retracted && string.IsNullOrWhiteSpace(Content))
            errors.Add("content is required unless retracted is true");

        if (ContentHash is { Length: > 0 } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            errors.Add("contentHash must be a 64 character hex string");

        return errors;
    }

    /// <summary>返回补齐哈希后的副本，便于核心做幂等比较。</summary>
    public PlatformIngestionItem WithComputedHash() =>
        string.IsNullOrWhiteSpace(ContentHash)
            ? this with { ContentHash = ComputeContentHash(Content) }
            : this;
}

public static class PlatformItemTypes
{
    public const string Note = "note";
    public const string Article = "article";
    public const string Message = "message";
    public const string File = "file";

    public static readonly IReadOnlyList<string> All = [Note, Article, Message, File];

    public static bool IsKnown(string? type) =>
        type is Note or Article or Message or File;
}

/// <summary>平台连接器一次同步的结果。核心按 batch 处理，避免单次响应过大。</summary>
public sealed record PlatformIngestionBatch
{
    [JsonPropertyName("items")]
    public IReadOnlyList<PlatformIngestionItem> Items { get; init; } = [];

    /// <summary>下一页游标；为空表示同步完成。</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    /// <summary>插件建议的下次同步时间（可选）。</summary>
    [JsonPropertyName("nextSyncAfterSeconds")]
    public int? NextSyncAfterSeconds { get; init; }
}
