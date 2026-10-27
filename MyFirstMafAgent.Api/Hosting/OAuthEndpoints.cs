using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// OAuth 2.1 的 HTTP 面：元数据发现、同意页、令牌、动态注册、撤销、JWKS。
///
/// <para>
/// 这些端点<b>不</b>走 <c>/api</c> 令牌，也<b>不</b>走 <c>/mcp</c> 客户端密钥：
/// 它们是授权流程本身，凭据由各自的请求参数和 PKCE 提供。
/// 业务数据面仍然只有 <c>/mcp</c>，鉴权仍由 <see cref="AgentClientGate"/> 统一判定。
/// </para>
///
/// <para>
/// 与 Cursor 的已知兼容性问题相关，这里刻意把授权服务器与资源服务器放在<b>同一域名</b>下：
/// 资源元数据里的 <c>authorization_servers</c> 指向同一个 issuer。
/// </para>
/// </summary>
public static class OAuthEndpoints
{
    public const string ProtectedResourceMetadataPath = "/.well-known/oauth-protected-resource";
    public const string AuthorizationServerMetadataPath = "/.well-known/oauth-authorization-server";
    public const string AuthorizationPath = "/oauth/authorize";
    public const string TokenPath = "/oauth/token";
    public const string RegistrationPath = "/oauth/register";
    public const string RevocationPath = "/oauth/revoke";
    public const string JwksPath = "/oauth/jwks";

    /// <summary>401 挑战头：指向 RFC 9728 元数据，客户端据此自动发现授权服务器。</summary>
    public static string BuildChallenge(OAuthServerContext context) =>
        $"Bearer realm=\"MyFirstMafAgent\", resource_metadata=\"{context.ProtectedResourceMetadataUrl}\"";

    public static void MapOAuthEndpoints(this WebApplication app, OAuthServerContext context)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.OAuth");

        MapAbuseGuard(app, logger);

        // ── 受保护资源元数据（RFC 9728） ──
        // 同时提供根路径与「资源路径后缀」两种形式：客户端两种探测方式都有。
        app.MapGet(ProtectedResourceMetadataPath, (HttpContext http) =>
            WriteJson(http, BuildProtectedResourceMetadata(context), StatusCodes.Status200OK));

        app.MapGet(ProtectedResourceMetadataPath + "/{**rest}", (HttpContext http) =>
            WriteJson(http, BuildProtectedResourceMetadata(context), StatusCodes.Status200OK));

        // ── 授权服务器元数据（RFC 8414） ──
        app.MapGet(AuthorizationServerMetadataPath, (HttpContext http) =>
            WriteJson(http, BuildAuthorizationServerMetadata(context), StatusCodes.Status200OK));

        // ── JWKS ──
        app.MapGet(JwksPath, (HttpContext http, OAuthTokenService tokens) =>
            WriteJson(http, tokens.CreateJwks(), StatusCodes.Status200OK));

        // ── 授权页 ──
        app.MapGet(AuthorizationPath, async (
            HttpContext http,
            OAuthService oauth,
            IKnowledgeBaseDirectory knowledgeBases,
            CancellationToken cancellationToken) =>
        {
            var query = http.Request.Query;
            var pending = await oauth.BeginAuthorizationAsync(
                new AuthorizeBeginRequest(
                    query["client_id"],
                    query["redirect_uri"],
                    query["response_type"],
                    query["scope"],
                    query["state"],
                    query["code_challenge"],
                    query["code_challenge_method"],
                    query["resource"]),
                context,
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            if (pending.Error is not null || pending.Request is null)
            {
                // 这里**不跳转**：redirect_uri 尚未与注册值核对过，跳转等于开放重定向。
                return WriteAuthorizeErrorPage(http, pending.Error!);
            }

            var knowledgeBaseList = await knowledgeBases.ListAsync(cancellationToken);
            WriteNoStore(http.Response);
            return Results.Content(
                RenderConsentPage(pending.Request, knowledgeBaseList),
                "text/html; charset=utf-8");
        });

        // ── 同意/拒绝 ──
        app.MapPost(AuthorizationPath, async (
            HttpContext http,
            OAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var requestId = form["request_id"].ToString();
            var decision = form["decision"].ToString();

            if (string.IsNullOrWhiteSpace(requestId))
                return Results.BadRequest(new { error = OAuthError.InvalidRequest, error_description = "缺少 request_id。" });

            if (string.Equals(decision, "deny", StringComparison.OrdinalIgnoreCase))
            {
                var denied = await oauth.DenyAsync(requestId, http.Connection.RemoteIpAddress?.ToString(), cancellationToken);
                if (denied.Error is not null || denied.Request is null)
                    return Results.BadRequest(new { error = denied.Error });

                return RedirectWithError(denied.Request, OAuthError.AccessDenied, "用户拒绝了授权请求。");
            }

            var approved = await oauth.ApproveAsync(
                requestId,
                form["scopes"].Select(value => value ?? string.Empty).Where(value => value.Length > 0).ToList(),
                ParseGuids(form["knowledgeBaseIds"]),
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            if (approved.Error is not null || approved.Request is null || approved.Code is null)
            {
                // 授权请求本身已失效（过期/不存在）：没有可信的客户端与回调地址，只能留在本地报错。
                if (approved.Request is null)
                    return WriteAuthorizeErrorPage(http, approved.Error ?? OAuthError.InvalidRequestError("授权请求已失效。"));

                // 同意页校验失败：留在本页重新选择，不跳回客户端。
                var knowledgeBaseList = await http.RequestServices
                    .GetRequiredService<IKnowledgeBaseDirectory>()
                    .ListAsync(cancellationToken);

                WriteNoStore(http.Response);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Results.Content(
                    RenderConsentPage(
                        approved.Request,
                        knowledgeBaseList,
                        approved.Error!.Description),
                    "text/html; charset=utf-8");
            }

            var redirect = BuildRedirect(approved.Request.RedirectUri, approved.Request.State,
                new Dictionary<string, string> { ["code"] = approved.Code });

            logger.LogInformation("OAuth 授权通过：client {ClientId}", approved.Request.ClientId);
            return Results.Redirect(redirect);
        });

        // ── 令牌端点 ──
        app.MapPost(TokenPath, async (
            HttpContext http,
            OAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var remote = http.Connection.RemoteIpAddress?.ToString();

            // client_id 允许放在表单里（公开客户端），也兼容 HTTP Basic 里的用户名。
            var clientId = form["client_id"].ToString();
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = ReadBasicClientId(http.Request.Headers.Authorization.ToString());

            WriteNoStore(http.Response);

            var grantType = form["grant_type"].ToString();

            if (string.Equals(grantType, "authorization_code", StringComparison.Ordinal))
            {
                var result = await oauth.ExchangeCodeAsync(
                    form["code"].ToString(),
                    clientId,
                    form["redirect_uri"].ToString(),
                    form["code_verifier"].ToString(),
                    form["resource"].ToString(),
                    context,
                    remote,
                    cancellationToken);

                return result.Error is not null
                    ? WriteJson(http, ErrorPayload(result.Error), result.Error.StatusCode)
                    : WriteJson(http, TokenPayload(result.Tokens!), StatusCodes.Status200OK);
            }

            if (string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
            {
                var result = await oauth.RefreshAsync(
                    form["refresh_token"].ToString(),
                    clientId,
                    form["scope"].ToString(),
                    form["resource"].ToString(),
                    context,
                    remote,
                    cancellationToken);

                return result.Error is not null
                    ? WriteJson(http, ErrorPayload(result.Error), result.Error.StatusCode)
                    : WriteJson(http, TokenPayload(result.Tokens!), StatusCodes.Status200OK);
            }

            return WriteJson(http,
                ErrorPayload(new OAuthError(OAuthError.UnsupportedGrantType, $"不支持的 grant_type：{grantType}")),
                StatusCodes.Status400BadRequest);
        });

        // ── 动态客户端注册（DCR，兜底路径） ──
        app.MapPost(RegistrationPath, async (
            HttpContext http,
            OAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            DynamicClientRegistrationRequest request;
            try
            {
                var dto = await http.Request.ReadFromJsonAsync<DcrRequestDto>(cancellationToken);
                request = new DynamicClientRegistrationRequest(
                    dto?.ClientName,
                    dto?.RedirectUris,
                    dto?.GrantTypes,
                    dto?.Scope,
                    dto?.TokenEndpointAuthMethod);
            }
            catch (JsonException)
            {
                return WriteJson(http,
                    ErrorPayload(new OAuthError(OAuthError.InvalidRequest, "请求体不是合法 JSON。")),
                    StatusCodes.Status400BadRequest);
            }

            var result = await oauth.RegisterClientAsync(
                request,
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            if (result.Error is not null || result.Client is null)
                return WriteJson(http, ErrorPayload(result.Error!), StatusCodes.Status400BadRequest);

            WriteNoStore(http.Response);
            return WriteJson(http, new JsonObject
            {
                ["client_id"] = result.Client.ClientId,
                ["client_name"] = result.Client.ClientName,
                ["redirect_uris"] = new JsonArray(result.Client.RedirectUris.Select(uri => (JsonNode)uri!).ToArray()),
                ["grant_types"] = new JsonArray(result.Client.GrantTypes.Select(type => (JsonNode)type!).ToArray()),
                ["scope"] = string.Join(' ', result.Client.Scopes),
                ["token_endpoint_auth_method"] = result.Client.TokenEndpointAuthMethod
            }, StatusCodes.Status201Created);
        });

        // ── 撤销（RFC 7009） ──
        app.MapPost(RevocationPath, async (
            HttpContext http,
            OAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            var form = await http.Request.ReadFormAsync(cancellationToken);
            var clientId = form["client_id"].ToString();
            if (string.IsNullOrWhiteSpace(clientId))
                clientId = ReadBasicClientId(http.Request.Headers.Authorization.ToString());

            await oauth.RevokeAsync(
                form["token"].ToString(),
                string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                context,
                http.Connection.RemoteIpAddress?.ToString(),
                cancellationToken);

            WriteNoStore(http.Response);
            return Results.Ok(new { });
        });

        logger.LogInformation(
            "OAuth 已启用：issuer={Issuer} resource={Resource}（授权服务器与资源服务器同源）",
            context.IssuerString, context.ResourceString);
    }

    // ───────────────────────── 滥用防护 ─────────────────────────

    /// <summary>
    /// 这些端点<b>不需要任何既有凭据</b>就能被调用，因此是天然的滥用入口：
    /// <c>/oauth/register</c> 每成功一次就往库里写一行客户端，
    /// <c>/oauth/token</c> 每被调用一次就要做一次哈希比对与数据库事务。
    ///
    /// <para>
    /// <c>/mcp</c> 早就有按来源 IP 的鉴权失败闸门（<see cref="AgentAuthThrottle"/>），
    /// 这两个端点原本没有——等于留了两个免费的打库入口。这里复用同一个闸门实例，
    /// 口径与 <c>/mcp</c> 完全一致（同一份 <c>Server:FailedAuthLimitPerMinute</c> /
    /// <c>FailedAuthBlockSeconds</c>），失败计数按来源 IP 共享。
    /// </para>
    ///
    /// <para>
    /// 只对 4xx 计数、2xx 清零：正常客户端偶发一次 <c>invalid_grant</c>（例如并发的刷新竞争）
    /// 不会被牵连封禁；而集中爆破一定会撞上 4xx。
    /// </para>
    /// </summary>
    private static void MapAbuseGuard(WebApplication app, ILogger logger)
    {
        app.Use(async (http, next) =>
        {
            if (!IsAbuseGuarded(http.Request.Path) || !HttpMethods.IsPost(http.Request.Method))
            {
                await next();
                return;
            }

            var throttle = http.RequestServices.GetRequiredService<AgentAuthThrottle>();
            var limits = http.RequestServices.GetRequiredService<IOptions<ServerSecurityOptions>>().Value;
            var remote = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            if (throttle.IsBlocked(remote, out var retryAfterSeconds))
            {
                logger.LogWarning(
                    "OAuth 端点 {Path} 拒绝了被封禁的来源 {Remote}（剩余 {RetryAfter}s）。",
                    http.Request.Path, remote, retryAfterSeconds);

                http.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
                await WriteJson(http,
                    ErrorPayload(new OAuthError(
                        OAuthError.TemporarilyUnavailable,
                        $"鉴权失败次数过多，请 {retryAfterSeconds} 秒后重试。",
                        StatusCodes.Status429TooManyRequests)),
                    StatusCodes.Status429TooManyRequests).ExecuteAsync(http);
                return;
            }

            await next();

            var status = http.Response.StatusCode;
            if (status is >= 200 and < 300)
            {
                throttle.RecordSuccess(remote);
            }
            else if (status is >= 400 and < 500)
            {
                if (throttle.RecordFailure(remote, limits.FailedAuthLimitPerMinute, limits.FailedAuthBlockSeconds))
                {
                    logger.LogWarning(
                        "OAuth 端点失败次数超限，已封禁来源 {Remote} {Seconds}s（上限 {Limit} 次/分钟）。",
                        remote, limits.FailedAuthBlockSeconds, limits.FailedAuthLimitPerMinute);
                }
            }
        });
    }

    /// <summary>需要按来源 IP 限速的「无凭据可打」端点。</summary>
    private static bool IsAbuseGuarded(PathString path) =>
        path.Equals(new PathString(TokenPath), StringComparison.OrdinalIgnoreCase)
        || path.Equals(new PathString(RegistrationPath), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── 元数据 ─────────────────────────

    private static JsonObject BuildProtectedResourceMetadata(OAuthServerContext context) => new()
    {
        ["resource"] = context.ResourceString,
        ["authorization_servers"] = new JsonArray(context.IssuerString),
        ["bearer_methods_supported"] = new JsonArray("header"),
        ["scopes_supported"] = new JsonArray(OAuthScopes.Supported.Select(scope => (JsonNode)scope!).ToArray()),
        ["resource_name"] = "知间知识库（MCP）",
        ["resource_documentation"] = context.IssuerString + "/docs/MCP-ACCESS.md"
    };

    private static JsonObject BuildAuthorizationServerMetadata(OAuthServerContext context) => new()
    {
        ["issuer"] = context.IssuerString,
        ["authorization_endpoint"] = context.AuthorizationEndpoint,
        ["token_endpoint"] = context.TokenEndpoint,
        ["registration_endpoint"] = context.RegistrationEndpoint,
        ["revocation_endpoint"] = context.RevocationEndpoint,
        ["jwks_uri"] = context.JwksUri,
        ["response_types_supported"] = new JsonArray("code"),
        ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
        ["code_challenge_methods_supported"] = new JsonArray("S256"),
        ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
        ["scopes_supported"] = new JsonArray(OAuthScopes.Supported.Select(scope => (JsonNode)scope!).ToArray()),
        // CIMD 优先：声明支持后，客户端会把 client_id 设成 HTTPS URL。
        ["client_id_metadata_document_supported"] = true
    };

    // ───────────────────────── 同意页 ─────────────────────────

    private static string RenderConsentPage(
        OAuthAuthorizationRequest request,
        IReadOnlyList<KnowledgeBaseSummary> knowledgeBases,
        string? error = null)
    {
        var scopes = OAuthScopes.Parse(request.Scope);
        var builder = new StringBuilder();

        builder.Append("""
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>授权请求 · 知间</title>
<style>
  :root { color-scheme: light dark; }
  body { font-family: system-ui, -apple-system, "Segoe UI", "Microsoft YaHei", sans-serif;
         margin: 0; padding: 32px 16px; background: #f6f7f9; color: #1f2328; }
  @media (prefers-color-scheme: dark) { body { background: #0d1117; color: #e6edf3; } .card { background: #161b22 !important; border-color: #30363d !important; } }
  .card { max-width: 560px; margin: 0 auto; background: #fff; border: 1px solid #d8dee4;
          border-radius: 12px; padding: 24px; box-shadow: 0 8px 24px rgba(15, 23, 42, .06); }
  h1 { font-size: 18px; margin: 0 0 4px; }
  .sub { color: #6e7781; font-size: 13px; margin: 0 0 20px; }
  .client { padding: 12px 14px; border: 1px solid #d8dee4; border-radius: 10px; margin-bottom: 16px; }
  .client strong { font-size: 15px; }
  code { font-size: 12px; word-break: break-all; color: #6e7781; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .04em; color: #6e7781; margin: 20px 0 8px; }
  label { display: flex; gap: 10px; align-items: flex-start; padding: 8px 4px; border-radius: 8px; }
  label:hover { background: rgba(127, 127, 127, .08); }
  label span small { display: block; color: #6e7781; font-size: 12px; }
  .warn { background: #fff8c5; color: #4d3800; border: 1px solid #d4a72c66;
          border-radius: 8px; padding: 10px 12px; font-size: 13px; margin-top: 12px; }
  @media (prefers-color-scheme: dark) { .warn { background: #3a2d00; color: #f0d98a; } }
  .actions { display: flex; gap: 10px; margin-top: 24px; justify-content: flex-end; }
  button { font: inherit; padding: 9px 18px; border-radius: 8px; border: 1px solid #d8dee4;
           background: #fff; cursor: pointer; }
  button.primary { background: #1f6feb; border-color: #1f6feb; color: #fff; font-weight: 600; }
  .error { background: #ffebe9; border: 1px solid #ff818266; color: #82071e;
           border-radius: 8px; padding: 10px 12px; font-size: 13px; margin-bottom: 16px; }
</style>
</head>
<body>
<div class="card">
  <h1>授权请求</h1>
  <p class="sub">以下客户端想访问你的知间知识库。请确认范围后再批准。</p>
""");

        if (!string.IsNullOrWhiteSpace(error))
            builder.Append("<div class=\"error\">").Append(WebUtility.HtmlEncode(error)).Append("</div>");

        builder.Append("<div class=\"client\"><strong>")
            .Append(WebUtility.HtmlEncode(request.ClientName))
            .Append("</strong><br><code>")
            .Append(WebUtility.HtmlEncode(request.ClientId))
            .Append("</code></div>");

        builder.Append("<form method=\"post\" action=\"").Append(AuthorizationPath).Append("\">");
        builder.Append("<input type=\"hidden\" name=\"request_id\" value=\"")
            .Append(WebUtility.HtmlEncode(request.Id)).Append("\">");

        builder.Append("<h2>权限</h2>");
        foreach (var scope in scopes)
        {
            var isWrite = OAuthScopes.IsWriteScope(scope);
            builder.Append("<label><input type=\"checkbox\" name=\"scopes\" value=\"")
                .Append(WebUtility.HtmlEncode(scope)).Append('"')
                .Append(isWrite ? string.Empty : " checked")
                .Append("><span>")
                .Append(WebUtility.HtmlEncode(DescribeScope(scope)))
                .Append("<small>").Append(WebUtility.HtmlEncode(scope)).Append("</small></span></label>");
        }

        if (OAuthScopes.ContainsWriteScope(scopes))
        {
            builder.Append("<div class=\"warn\">写权限默认<b>不勾选</b>。勾选后该客户端可以修改或新增内容，请确认你信任它。</div>");
        }

        builder.Append("<h2>可访问的知识库</h2>");
        if (knowledgeBases.Count == 0)
        {
            builder.Append("<p class=\"sub\">当前还没有知识库，授权后仍然为空范围。</p>");
        }
        else
        {
            foreach (var knowledgeBase in knowledgeBases)
            {
                builder.Append("<label><input type=\"checkbox\" name=\"knowledgeBaseIds\" value=\"")
                    .Append(knowledgeBase.Id.ToString("D")).Append("\" checked><span>")
                    .Append(WebUtility.HtmlEncode(knowledgeBase.Name)).Append("</span></label>");
            }
        }

        builder.Append("""
  <div class="actions">
    <button type="submit" name="decision" value="deny">拒绝</button>
    <button type="submit" name="decision" value="approve" class="primary">同意授权</button>
  </div>
</form>
</div>
</body>
</html>
""");

        return builder.ToString();

        static string DescribeScope(string scope) => scope switch
        {
            "knowledge:read" => "读取知识库（搜索、查看资料）",
            "knowledge:write" => "写入知识库（上传资料）",
            "workspace:read" => "读取 Agent 工作区",
            "workspace:write" => "写入 Agent 工作区",
            "files:read" => "读取你授权的本地目录",
            "files:write" => "写入你授权的本地目录",
            "avatar:read" => "读取分身记忆",
            "avatar:write" => "写入分身记忆",
            "chat:read" => "读取聊天历史",
            _ => scope
        };
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static IResult WriteJson(HttpContext http, JsonNode payload, int statusCode)
    {
        http.Response.StatusCode = statusCode;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        return Results.Text(payload.ToJsonString(), "application/json; charset=utf-8", Encoding.UTF8, statusCode);
    }

    private static JsonObject ErrorPayload(OAuthError error) => new()
    {
        ["error"] = error.Error,
        ["error_description"] = error.Description
    };

    private static JsonObject TokenPayload(OAuthTokenResponse tokens) => new()
    {
        ["access_token"] = tokens.AccessToken,
        ["token_type"] = tokens.TokenType,
        ["expires_in"] = tokens.ExpiresIn,
        ["scope"] = tokens.Scope,
        ["refresh_token"] = tokens.RefreshToken
    };

    private static void WriteNoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Frame-Options"] = "DENY";
    }

    private static IResult WriteAuthorizeErrorPage(HttpContext http, OAuthError error)
    {
        WriteNoStore(http.Response);
        http.Response.StatusCode = error.StatusCode;
        return Results.Content(
            "<html><body style=\"font-family:system-ui;padding:32px\"><h1>授权请求无法继续</h1>" +
            $"<p>{WebUtility.HtmlEncode(error.Description)}</p></body></html>",
            "text/html; charset=utf-8");
    }

    private static IResult RedirectWithError(OAuthAuthorizationRequest request, string error, string description) =>
        Results.Redirect(BuildRedirect(request.RedirectUri, request.State, new Dictionary<string, string>
        {
            ["error"] = error,
            ["error_description"] = description
        }));

    private static string BuildRedirect(string redirectUri, string? state, Dictionary<string, string> parameters)
    {
        var builder = new StringBuilder(redirectUri);
        builder.Append(redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?');

        var first = true;
        foreach (var (key, value) in parameters)
        {
            if (!first) builder.Append('&');
            first = false;
            builder.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }

        if (!string.IsNullOrEmpty(state))
        {
            if (!first) builder.Append('&');
            builder.Append("state=").Append(Uri.EscapeDataString(state));
        }

        return builder.ToString();
    }

    private static string ReadBasicClientId(string? authorization)
    {
        const string prefix = "Basic ";
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            var raw = Convert.FromBase64String(authorization[prefix.Length..].Trim());
            var decoded = Encoding.UTF8.GetString(raw);
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            return separator < 0 ? string.Empty : Uri.UnescapeDataString(decoded[..separator]);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<Guid> ParseGuids(IEnumerable<string?> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Guid.TryParse(value, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

    /// <summary>
    /// DCR 请求体（RFC 7591 的 snake_case 字段）。
    /// 不能用位置记录直接反序列化：构造函数参数名是 PascalCase，
    /// 与 <c>redirect_uris</c> 这类 snake_case 字段匹配不上，会静默变成 null。
    /// </summary>
    private sealed record DcrRequestDto(
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
        [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);
}
