using System.Security.Cryptography;
using System.Text;

namespace MyFirstMafAgent.Api.Application.Plugins;

/// <summary>
/// 受信任发布者公钥的持久化端口。
///
/// <para>
/// 与 appsettings 里的 <c>Plugins:TrustedPublisherKeys</c> 并存：配置项供"部署时固定信任"，
/// 本表供"运行期信任某个新发布者"。两者在 <see cref="PluginTrustedKeyRegistry"/> 里合并，
/// 配置项优先（运维用配置文件压过库里的临时授权）。
/// </para>
/// </summary>
public interface IPluginTrustedKeyStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PluginTrustedKeyRecord>> ListAsync(CancellationToken cancellationToken);

    Task<PluginTrustedKeyRecord?> GetAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>新增或覆盖（同 keyId 覆盖）。</summary>
    Task SaveAsync(PluginTrustedKeyRecord record, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string keyId, CancellationToken cancellationToken);
}

/// <summary>一条受信任公钥。</summary>
public sealed record PluginTrustedKeyRecord(
    string KeyId,
    string PluginId,
    string Algorithm,
    string PublicKeyPem,
    string Fingerprint,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>公钥的解析与指纹计算。纯函数，便于单测。</summary>
public static class PluginTrustedKeys
{
    public const string AlgorithmEcdsa = "ecdsa";
    public const string AlgorithmRsa = "rsa";

    /// <summary>校验并规范化 PEM 公钥；不合法时返回错误原因。</summary>
    public static bool TryNormalize(
        string? algorithm,
        string? publicKeyPem,
        out string normalizedAlgorithm,
        out string normalizedPem,
        out string fingerprint,
        out string error)
    {
        normalizedAlgorithm = (algorithm ?? AlgorithmEcdsa).Trim().ToLowerInvariant();
        normalizedPem = string.Empty;
        fingerprint = string.Empty;
        error = string.Empty;

        if (normalizedAlgorithm is not (AlgorithmEcdsa or AlgorithmRsa))
        {
            error = "algorithm 只能是 ecdsa 或 rsa。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            error = "publicKeyPem 必填。";
            return false;
        }

        var pem = publicKeyPem.Replace("\r\n", "\n").Trim();

        // 私钥绝不能进库：谁能写库谁就能自签，签名就失去意义。
        if (pem.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            error = "这是私钥，不能作为受信任公钥保存；请提供服务方给你的公钥。";
            return false;
        }

        try
        {
            if (normalizedAlgorithm == AlgorithmRsa)
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                _ = rsa.KeySize;
            }
            else
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                _ = ecdsa.KeySize;
            }
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            error = $"公钥格式无效：{exception.Message}";
            return false;
        }

        normalizedPem = pem;
        fingerprint = ComputeFingerprint(pem);
        return true;
    }

    /// <summary>
    /// 指纹 = 规范化 PEM 的 SHA-256 前 16 位十六进制。
    /// 用途是让人工核对"库里这条和发布者公布的是不是同一把钥匙"，不是密码学安全边界。
    /// </summary>
    public static string ComputeFingerprint(string normalizedPem) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPem)))[..16];

    /// <summary>插件 ID 是否命中该公钥（按域名段前缀，大小写不敏感）。</summary>
    public static bool Matches(string prefix, string pluginId) =>
        string.Equals(prefix, pluginId, StringComparison.OrdinalIgnoreCase)
        || pluginId.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
}
