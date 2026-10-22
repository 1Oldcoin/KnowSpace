using System.Security.Cryptography;
using System.Text;

namespace MyFirstMafAgent.Api.Domain.Knowledge;

public static class EmbeddingProviders
{
    public const string LocalOnnx = "onnx-local";
    public const string OpenAiCompatible = "openai-compatible";
}

/// <summary>
/// 向量模型运行参数。模型目录、HTTP 端点和密钥只用于构建生成器，不会出现在任何 API 响应中。
/// </summary>
public sealed record EmbeddingRuntimeConfig(
    string? ModelDirectory = null,
    string? Endpoint = null,
    string? ApiKey = null,
    int IntraOpNumThreads = 0);

/// <summary>
/// 不可变的向量模型规格。指纹是「查询向量」与「索引向量」是否可比的唯一依据：
/// 指纹不同的向量永远不允许互相检索。
/// </summary>
public sealed record EmbeddingSpec(
    Guid Id,
    string Name,
    string Provider,
    string Model,
    int Dimensions,
    int MaxSequenceLength,
    string Revision,
    string Fingerprint,
    EmbeddingRuntimeConfig Runtime,
    DateTimeOffset CreatedAt)
{
    public static string CreateFingerprint(
        string provider, string model, int dimensions, int maxSequenceLength, string revision, string? variant = null)
    {
        var parts = new List<string>
        {
            provider.Trim().ToLowerInvariant(),
            model.Trim(),
            dimensions.ToString(),
            maxSequenceLength.ToString(),
            revision.Trim()
        };
        // 云端同名模型在服务商变更时可能不是同一向量空间，用端点作为附加区分。
        if (!string.IsNullOrWhiteSpace(variant)) parts.Add(variant.Trim().TrimEnd('/').ToLowerInvariant());
        var canonical = string.Join('|', parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

public sealed record EmbeddingSpecDraft(
    string Name,
    string Provider,
    string Model,
    int Dimensions,
    int MaxSequenceLength,
    string Revision,
    EmbeddingRuntimeConfig Runtime,
    string? FingerprintVariant = null);
