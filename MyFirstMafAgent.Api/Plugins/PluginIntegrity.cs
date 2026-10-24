using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Plugins.Sdk;

namespace MyFirstMafAgent.Api.Plugins;

/// <summary>完整性校验结论。</summary>
public sealed record PluginIntegrityReport(
    string Status,
    string? PackageDigest,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public const string StatusSkipped = "skipped";
    public const string StatusNotProvided = "not-provided";
    public const string StatusVerified = "verified";
    public const string StatusDigestMismatch = "digest-mismatch";
    public const string StatusSignatureUntrusted = "untrusted-publisher";
    public const string StatusSignatureInvalid = "signature-invalid";

    public bool IsAcceptable => Errors.Count == 0;

    public static PluginIntegrityReport Skipped() => new(StatusSkipped, null, [], []);

    public static PluginIntegrityReport Verified(string digest) => new(StatusVerified, digest, [], []);
}

/// <summary>
/// 插件包完整性校验：包摘要（SHA-256）与发布者签名。
///
/// <para>
/// 摘要的算法是<b>规范化</b>的，必须与插件作者一致，否则任何重新打包都会失败：
/// 取包目录下所有普通文件（跳过 <c>manifest.json</c>、<c>active.json</c> 与点开头的目录），
/// 按相对路径（<c>/</c> 分隔、大小写敏感、Ordinal 排序）排序，
/// 对每个文件依次写入 UTF-8 相对路径、<c>\0</c>、UTF-8 十进制字节数、<c>\0</c>、文件内容。
/// 最终 SHA-256 以小写十六进制表示。
/// </para>
///
/// <para>
/// <c>manifest.json</c> 不参与摘要：摘要就写在 manifest 里，包含它会造成自引用。
/// 因此 manifest 本身的身份由签名（对摘要签名）与运行期握手校验（id/version/能力）共同保证。
/// </para>
///
/// <para>
/// 签名是对上述十六进制字符串的 UTF-8 字节做 SHA-256 签名
/// （ECDSA P-256 接受 P1363 与 DER 两种编码，RSA 使用 PKCS#1 v1.5）。
/// </para>
/// </summary>
public sealed class PluginIntegrityVerifier(
    IOptions<PluginOptions> options,
    PluginTrustedKeyRegistry trustedKeys,
    ILogger<PluginIntegrityVerifier> logger)
{
    /// <summary>校验策略：<c>off</c> / <c>warn</c> / <c>enforce</c>。</summary>
    public string Policy => (options.Value.IntegrityPolicy ?? "warn").Trim().ToLowerInvariant();

    public async Task<PluginIntegrityReport> VerifyAsync(
        string packageDirectory,
        PluginManifest manifest,
        CancellationToken cancellationToken)
    {
        var policy = Policy;
        if (policy is "off" or "none" or "disabled")
            return PluginIntegrityReport.Skipped();

        var integrity = manifest.Integrity;
        var hasDigest = !string.IsNullOrWhiteSpace(integrity?.Sha256);
        var hasSignature = !string.IsNullOrWhiteSpace(integrity?.Signature);

        if (!hasDigest && !hasSignature)
        {
            if (policy == "enforce")
                return new PluginIntegrityReport(
                    PluginIntegrityReport.StatusNotProvided,
                    null,
                    ["enforce 策略下 manifest 必须提供 integrity.sha256"],
                    []);

            return new PluginIntegrityReport(
                PluginIntegrityReport.StatusNotProvided,
                null,
                [],
                ["manifest 未提供 integrity.sha256，无法校验插件包完整性"]);
        }

        string digest;
        try
        {
            digest = await ComputePackageDigestAsync(packageDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PluginIntegrityReport(
                PluginIntegrityReport.StatusDigestMismatch,
                null,
                [$"计算插件包摘要失败：{exception.Message}"],
                []);
        }

        var errors = new List<string>();
        var warnings = new List<string>();
        var digestMismatch = false;
        var signatureFailure = false;

        if (hasDigest)
        {
            var expected = integrity!.Sha256!.Trim().ToLowerInvariant();
            if (!string.Equals(expected, digest, StringComparison.Ordinal))
            {
                digestMismatch = true;
                var message = $"插件包摘要不匹配：manifest 声明 {expected}，实际 {digest}";
                if (policy == "enforce") errors.Add(message);
                else warnings.Add(message);
            }
        }
        else if (policy == "enforce")
        {
            digestMismatch = true;
            errors.Add("enforce 策略下 manifest 必须提供 integrity.sha256");
        }

        if (hasSignature)
        {
            var (verified, reason) = VerifySignature(manifest, digest);
            if (!verified)
            {
                signatureFailure = true;
                var message = $"签名校验失败：{reason}";
                if (policy == "enforce") errors.Add(message);
                else warnings.Add(message);
            }
        }
        else if (policy == "enforce")
        {
            signatureFailure = true;
            errors.Add("enforce 策略下 manifest 必须提供 integrity.signature");
        }

        var status = digestMismatch
            ? PluginIntegrityReport.StatusDigestMismatch
            : signatureFailure
                ? PluginIntegrityReport.StatusSignatureInvalid
                : PluginIntegrityReport.StatusVerified;

        logger.Log(
            errors.Count > 0 ? LogLevel.Warning : LogLevel.Information,
            "Plugin {PluginId} integrity {Status} (digest {Digest})",
            manifest.Id,
            status,
            digest);

        return new PluginIntegrityReport(status, digest, errors, warnings);
    }

    /// <summary>按规范化算法计算包摘要（小写十六进制）。</summary>
    public static async Task<string> ComputePackageDigestAsync(
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var file in EnumeratePackageFiles(packageDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(packageDirectory, file).Replace('\\', '/');
            var info = new FileInfo(file);

            Append(hash, Encoding.UTF8.GetBytes(relative));
            Append(hash, "\0"u8);
            Append(hash, Encoding.UTF8.GetBytes(info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Append(hash, "\0"u8);

            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// 枚举参与摘要的文件：跳过 <c>manifest.json</c>、<c>active.json</c> 与点开头的目录（<c>.data</c>、<c>.staging</c>）。
    /// </summary>
    private static IEnumerable<string> EnumeratePackageFiles(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory)) return [];

        return Directory.EnumerateFiles(packageDirectory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            })
            .Select(file => (File: file, Relative: Path.GetRelativePath(packageDirectory, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Equals("active.json", StringComparison.Ordinal))
            .Where(item => !item.Relative.Equals("manifest.json", StringComparison.Ordinal))
            .Where(item => !item.Relative.Split('/').Any(segment => segment.StartsWith('.')))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => item.File)
            .ToList();
    }

    /// <summary>按受信任公钥验证签名。</summary>
    public (bool Verified, string Reason) VerifySignature(PluginManifest manifest, string digest)
    {
        var signature = manifest.Integrity?.Signature;
        if (string.IsNullOrWhiteSpace(signature)) return (false, "签名缺失");

        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromBase64String(signature);
        }
        catch (FormatException)
        {
            return (false, "签名不是合法的 Base64");
        }

        var key = FindKey(manifest.Id);
        if (key is null) return (false, $"没有为 '{manifest.Id}' 配置受信任公钥");

        var payload = Encoding.UTF8.GetBytes(digest);

        // 受信任公钥的算法以小写形式保存在库里；配置项可能写成 "ECDSA"。
        var algorithm = string.IsNullOrWhiteSpace(key.Algorithm) ? PluginTrustedKeys.AlgorithmEcdsa : key.Algorithm.Trim().ToLowerInvariant();

        try
        {
            if (string.Equals(algorithm, PluginTrustedKeys.AlgorithmRsa, StringComparison.Ordinal))
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(key.PublicKeyPem);
                return rsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                    ? (true, "ok")
                    : (false, "RSA 签名不匹配");
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);

            if (ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256))
                return (true, "ok");

            // openssl 默认输出 DER 编码，.NET 默认使用 P1363，两种都接受。
            return ecdsa.VerifyData(
                payload,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence)
                ? (true, "ok")
                : (false, "ECDSA 签名不匹配");
        }
        catch (CryptographicException exception)
        {
            return (false, $"公钥或签名格式无效：{exception.Message}");
        }
    }

    /// <summary>
    /// 按域名段前缀匹配受信任公钥，取最长前缀。
    /// 公钥来源是配置与数据库的合并视图（见 <see cref="PluginTrustedKeyRegistry"/>）。
    /// </summary>
    public PluginTrustedKeyRecord? FindKey(string pluginId) => trustedKeys.Find(pluginId);

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> data) => hash.AppendData(data);
}
