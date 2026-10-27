using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.OAuth;

/// <summary>OAuth 协议层的纯函数：PKCE、重定向地址、资源/签发者标识。</summary>
public static class OAuthProtocol
{
    /// <summary>RFC 7636：code_verifier 是 43–128 个 unreserved 字符。</summary>
    public static bool IsValidCodeVerifier(string? verifier)
    {
        if (verifier is null) return false;
        if (verifier.Length is < 43 or > 128) return false;

        foreach (var c in verifier)
        {
            var ok = c is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '.' or '_' or '~';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>S256 challenge = BASE64URL(SHA256(ASCII(verifier)))。</summary>
    public static string ComputeCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    /// <summary>
    /// 校验 PKCE。<c>plain</c> 被拒绝：OAuth 2.1 要求 S256，
    /// 而 public client 没有 secret，plain 等于把 verifier 明文放进授权请求。
    /// </summary>
    public static bool VerifyCodeChallenge(string? verifier, string? challenge, string? method)
    {
        if (!IsValidCodeVerifier(verifier) || string.IsNullOrEmpty(challenge)) return false;
        if (!string.Equals(method, "S256", StringComparison.Ordinal)) return false;

        var computed = ComputeCodeChallenge(verifier!);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(challenge));
    }

    /// <summary>
    /// 重定向地址是否可注册：绝对地址、无 fragment、无 userinfo；
    /// 回环地址允许 http（RFC 8252 原生客户端），其余必须 https。
    /// </summary>
    public static bool IsValidRedirectUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Fragment.Length > 0) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;

        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && (uri.IsLoopback
                || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>注册时保存的地址必须与授权/换码时传来的完全一致。</summary>
    public static bool RedirectUriMatches(IEnumerable<string> registered, string? requested) =>
        requested is not null && registered.Contains(requested, StringComparer.Ordinal);

    internal static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    internal static byte[]? Base64UrlDecode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;

        try
        {
            return System.Buffers.Text.Base64Url.DecodeFromChars(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// 一次 OAuth 响应所需的绝对地址：签发者（<c>iss</c>）与资源标识（<c>aud</c> / RFC 8707 <c>resource</c>）。
///
/// <para>
/// 全部从配置的 <see cref="McpOAuthOptions.Issuer"/> 推导，<b>不从请求头推导</b>：
/// 反代没配好时 <c>Host</c> 是可控输入，用它拼出来的 <c>aud</c> 等于没有受众绑定。
/// </para>
/// </summary>
public sealed record OAuthServerContext(Uri Issuer, Uri Resource, string McpPath)
{
    public string IssuerString => Issuer.GetLeftPart(UriPartial.Path).TrimEnd('/');

    public string ResourceString => Resource.GetLeftPart(UriPartial.Path).TrimEnd('/');

    /// <summary>RFC 9728 受保护资源元数据地址。</summary>
    public string ProtectedResourceMetadataUrl => IssuerString + OAuthEndpoints.ProtectedResourceMetadataPath;

    /// <summary>RFC 8414 授权服务器元数据地址。</summary>
    public string AuthorizationServerMetadataUrl => IssuerString + OAuthEndpoints.AuthorizationServerMetadataPath;

    public string AuthorizationEndpoint => IssuerString + OAuthEndpoints.AuthorizationPath;

    public string TokenEndpoint => IssuerString + OAuthEndpoints.TokenPath;

    public string RegistrationEndpoint => IssuerString + OAuthEndpoints.RegistrationPath;

    public string RevocationEndpoint => IssuerString + OAuthEndpoints.RevocationPath;

    public string JwksUri => IssuerString + OAuthEndpoints.JwksPath;

    /// <summary>配置不完整时返回 null（OAuth 视为未启用）。</summary>
    public static OAuthServerContext? TryCreate(McpOAuthOptions options, string mcpPath)
    {
        if (!options.Enabled) return null;
        if (!Uri.TryCreate(options.Issuer?.Trim(), UriKind.Absolute, out var issuer)) return null;
        if (issuer.Scheme != Uri.UriSchemeHttps && !IsLoopbackAuthority(issuer)) return null;

        var path = string.IsNullOrWhiteSpace(mcpPath) ? "/mcp" : mcpPath;

        Uri resource;
        if (!string.IsNullOrWhiteSpace(options.Resource))
        {
            if (!Uri.TryCreate(options.Resource.Trim(), UriKind.Absolute, out var configured)) return null;
            resource = configured;
        }
        else
        {
            resource = new Uri(issuer.GetLeftPart(UriPartial.Authority) + path);
        }

        return new OAuthServerContext(issuer, resource, path);
    }

    private static bool IsLoopbackAuthority(Uri uri) =>
        uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}

/// <summary>OAuth 错误响应：<c>error</c> 用规范值，<c>description</c> 给人看。</summary>
public sealed record OAuthError(string Error, string Description, int StatusCode = StatusCodes.Status400BadRequest)
{
    public const string InvalidRequest = "invalid_request";
    public const string InvalidClient = "invalid_client";
    public const string InvalidGrant = "invalid_grant";
    public const string UnauthorizedClient = "unauthorized_client";
    public const string UnsupportedGrantType = "unsupported_grant_type";
    public const string UnsupportedResponseType = "unsupported_response_type";
    public const string InvalidScope = "invalid_scope";
    public const string InvalidTarget = "invalid_target";
    public const string AccessDenied = "access_denied";
    public const string ServerError = "server_error";
    public const string TemporarilyUnavailable = "temporarily_unavailable";

    public static OAuthError InvalidRequestError(string description) =>
        new(InvalidRequest, description);

    public static OAuthError InvalidClientError(string description) =>
        new(InvalidClient, description, StatusCodes.Status401Unauthorized);

    public static OAuthError InvalidGrantError(string description) =>
        new(InvalidGrant, description);

    public static OAuthError InvalidScopeError(string description) =>
        new(InvalidScope, description);

    public static OAuthError InvalidTargetError(string description) =>
        new(InvalidTarget, description);
}
