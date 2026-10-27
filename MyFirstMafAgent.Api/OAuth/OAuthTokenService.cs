using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>一次签发请求。</summary>
public sealed record OAuthAccessTokenRequest(
    string Subject,
    string ClientId,
    string ConsentId,
    IReadOnlyList<string> Scopes,
    string Resource,
    string Issuer);

/// <summary>校验通过的访问令牌（已解出声明，未做业务判定）。</summary>
public sealed record OAuthAccessToken(
    string Subject,
    string ClientId,
    string ConsentId,
    IReadOnlyList<string> Scopes,
    string Audience,
    string Issuer,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Jti);

public enum OAuthTokenValidationFailure
{
    None,
    Malformed,
    UnsupportedAlgorithm,
    UnknownKey,
    BadSignature,
    WrongIssuer,
    WrongAudience,
    Expired,
    NotYetValid,
    MissingClaim
}

/// <summary>
/// 访问令牌的签发与校验：RFC 9068 <c>at+jwt</c> + ES256。
///
/// <para>
/// 为什么不用现成的 JwtBearer 包：
/// </para>
/// <list type="bullet">
///   <item><description>本仓库要能在离线环境还原，新增包依赖是有成本的。</description></item>
///   <item><description>这里只需要「一个签发者、一把密钥、一种算法」的最小集合，
///   自己实现反而能把受众绑定、时钟偏移、算法白名单写成显式代码——出错时容易看出来。</description></item>
/// </list>
///
/// <para>
/// 关键约束：<b>算法白名单</b>（只认 ES256，杜绝 <c>alg: none</c> 与算法混淆）、
/// <b>kid 必须匹配</b>、<b><c>aud</c> 必须等于本资源标识</b>。
/// </para>
/// </summary>
public sealed class OAuthTokenService
{
    private const string Algorithm = "ES256";
    private const string TokenType = "at+jwt";

    private readonly object _gate = new();
    private readonly ILogger<OAuthTokenService> _logger;
    private readonly McpOAuthOptions _options;
    private readonly IConfiguration? _configuration;
    private ECDsa? _key;
    private string? _kid;

    public OAuthTokenService(
        IOptions<McpOAuthOptions> options,
        ILogger<OAuthTokenService> logger,
        IConfiguration? configuration = null)
    {
        _options = options.Value;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>密钥标识（JWK thumbprint 风格：公钥的 SHA-256 前 128 位 base64url）。</summary>
    public string KeyId
    {
        get { EnsureKey(); return _kid!; }
    }

    /// <summary>签发访问令牌。</summary>
    public string CreateAccessToken(OAuthAccessTokenRequest request, DateTimeOffset now)
    {
        var key = EnsureKey();
        var expires = now.AddMinutes(Math.Max(1, _options.AccessTokenLifetimeMinutes));

        var header = new JsonObject
        {
            ["alg"] = Algorithm,
            ["typ"] = TokenType,
            ["kid"] = _kid
        };

        var payload = new JsonObject
        {
            ["iss"] = request.Issuer,
            ["sub"] = request.Subject,
            ["aud"] = request.Resource,
            ["client_id"] = request.ClientId,
            ["consent_id"] = request.ConsentId,
            ["scope"] = string.Join(' ', request.Scopes),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = NewIdentifier()
        };

        var signingInput = OAuthProtocol.Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToJsonString()))
            + "."
            + OAuthProtocol.Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return signingInput + "." + OAuthProtocol.Base64UrlEncode(signature);
    }

    /// <summary>校验访问令牌。失败时给出可区分的失败原因，便于审计。</summary>
    public bool TryValidate(
        string? token,
        string expectedIssuer,
        string expectedAudience,
        DateTimeOffset now,
        out OAuthAccessToken? accessToken,
        out OAuthTokenValidationFailure failure)
    {
        accessToken = null;
        failure = OAuthTokenValidationFailure.Malformed;

        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        var headerBytes = OAuthProtocol.Base64UrlDecode(parts[0]);
        var payloadBytes = OAuthProtocol.Base64UrlDecode(parts[1]);
        var signatureBytes = OAuthProtocol.Base64UrlDecode(parts[2]);
        if (headerBytes is null || payloadBytes is null || signatureBytes is null) return false;

        JsonNode? header;
        JsonNode? payload;
        try
        {
            header = JsonNode.Parse(headerBytes);
            payload = JsonNode.Parse(payloadBytes);
        }
        catch (JsonException)
        {
            return false;
        }

        if (header is not JsonObject headerObject || payload is not JsonObject payloadObject) return false;

        // 算法白名单：不匹配就直接拒绝，不做任何降级尝试。
        if (!string.Equals(headerObject["alg"]?.GetValue<string>(), Algorithm, StringComparison.Ordinal))
        {
            failure = OAuthTokenValidationFailure.UnsupportedAlgorithm;
            return false;
        }

        var key = EnsureKey();
        if (!string.Equals(headerObject["kid"]?.GetValue<string>(), _kid, StringComparison.Ordinal))
        {
            failure = OAuthTokenValidationFailure.UnknownKey;
            return false;
        }

        var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signatureValid = key.VerifyData(
            signingInput,
            signatureBytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        if (!signatureValid)
        {
            failure = OAuthTokenValidationFailure.BadSignature;
            return false;
        }

        var skew = TimeSpan.FromSeconds(Math.Clamp(_options.ClockSkewSeconds, 0, 600));

        var issuer = payloadObject["iss"]?.GetValue<string>();
        if (!string.Equals(issuer, expectedIssuer, StringComparison.Ordinal))
        {
            failure = OAuthTokenValidationFailure.WrongIssuer;
            return false;
        }

        if (!AudienceMatches(payloadObject["aud"], expectedAudience))
        {
            failure = OAuthTokenValidationFailure.WrongAudience;
            return false;
        }

        var expiresAt = ReadInstant(payloadObject["exp"]);
        if (expiresAt is null || expiresAt.Value + skew <= now)
        {
            failure = OAuthTokenValidationFailure.Expired;
            return false;
        }

        var issuedAt = ReadInstant(payloadObject["iat"]);
        if (issuedAt is not null && issuedAt.Value - skew > now)
        {
            failure = OAuthTokenValidationFailure.NotYetValid;
            return false;
        }

        var subject = payloadObject["sub"]?.GetValue<string>();
        var clientId = payloadObject["client_id"]?.GetValue<string>();
        var jti = payloadObject["jti"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(jti))
        {
            failure = OAuthTokenValidationFailure.MissingClaim;
            return false;
        }

        var scope = payloadObject["scope"]?.GetValue<string>();

        failure = OAuthTokenValidationFailure.None;
        accessToken = new OAuthAccessToken(
            subject!,
            clientId!,
            payloadObject["consent_id"]?.GetValue<string>() ?? string.Empty,
            OAuthScopes.Parse(scope),
            expectedAudience,
            issuer!,
            issuedAt ?? now,
            expiresAt!.Value,
            jti!);

        return true;
    }

    /// <summary>公钥的 JWKS 文档（RFC 7517）。只暴露公钥参数。</summary>
    public JsonObject CreateJwks()
    {
        var key = EnsureKey();
        var parameters = key.ExportParameters(includePrivateParameters: false);

        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = OAuthProtocol.Base64UrlEncode(parameters.Q.X!),
                ["y"] = OAuthProtocol.Base64UrlEncode(parameters.Q.Y!),
                ["kid"] = _kid,
                ["use"] = "sig",
                ["alg"] = Algorithm
            })
        };
    }

    private static bool AudienceMatches(JsonNode? audience, string expected)
    {
        switch (audience)
        {
            case JsonValue value when value.TryGetValue<string>(out var single):
                return string.Equals(single, expected, StringComparison.Ordinal);

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue entry
                        && entry.TryGetValue<string>(out var itemValue)
                        && string.Equals(itemValue, expected, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    private static DateTimeOffset? ReadInstant(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (!value.TryGetValue<long>(out var seconds)) return null;
        if (seconds <= 0) return null;

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private ECDsa EnsureKey()
    {
        if (_key is not null) return _key;

        lock (_gate)
        {
            if (_key is not null) return _key;

            var path = ResolveKeyPath();
            _key = LoadOrCreate(path);
            _kid = ComputeKeyId(_key);
            return _key;
        }
    }

    private string ResolveKeyPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.SigningKeyPath)) return _options.SigningKeyPath;

        var candidates = new List<string>();

        // 桌面版把可写数据根重定向到 Storage:RootPath 的上层（见 HostBootstrap.ApplyDataRoot）。
        var storageRoot = _configuration?["Storage:RootPath"];
        if (!string.IsNullOrWhiteSpace(storageRoot))
        {
            var dataRoot = Path.GetDirectoryName(Path.GetFullPath(storageRoot));
            if (!string.IsNullOrWhiteSpace(dataRoot))
                candidates.Add(Path.Combine(dataRoot, "oauth", "signing-key.pem"));
        }

        candidates.Add(Path.Combine(AppPaths.UserDataRoot, "oauth", "signing-key.pem"));
        candidates.Add(Path.Combine(AppPaths.ResolveContentRoot(), "App_Data", "oauth", "signing-key.pem"));

        foreach (var candidate in candidates)
        {
            try
            {
                var directory = Path.GetDirectoryName(candidate);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                return candidate;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 换下一个候选：签名密钥是必须落盘的，但落在哪里可以退让。
            }
        }

        throw new InvalidOperationException(
            "找不到可写的 OAuth 签名密钥目录。请配置 Mcp:OAuth:SigningKeyPath 指向可写路径（候选："
            + string.Join("；", candidates) + "）。");
    }

    private ECDsa LoadOrCreate(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        try
        {
            if (File.Exists(path))
            {
                key.ImportFromPem(File.ReadAllText(path));
                return key;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            RestrictPermissions(path);

            _logger.LogInformation("OAuth 签名密钥已生成：{Path}", path);
            return key;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // 不能回退到「内存里的临时密钥」：重启后所有 token 失效，
            // 但更糟的是两台实例各签各的，客户端会遇到随机 401。直接失败更好定位。
            _logger.LogError(exception, "无法读取或写入 OAuth 签名密钥：{Path}", path);
            throw;
        }
    }

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // 权限收紧失败不阻断启动；文件仍在用户自己的数据目录内。
        }
    }

    private static string ComputeKeyId(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var raw = new byte[parameters.Q.X!.Length + parameters.Q.Y!.Length];
        parameters.Q.X.CopyTo(raw, 0);
        parameters.Q.Y.CopyTo(raw, parameters.Q.X.Length);
        return OAuthProtocol.Base64UrlEncode(SHA256.HashData(raw)[..16]);
    }

    private static string NewIdentifier() => OAuthProtocol.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
}
