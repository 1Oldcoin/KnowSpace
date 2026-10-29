using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Agents;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// MCP 接入的可观测性与便捷性端点：协议诊断、本地配置一键复制、Server Card、认证事件。
///
/// <para>
/// 除 Server Card 外都在 <c>/api</c> 下，因此继承 <c>/api</c> 的令牌保护。
/// Server Card 是公开的静态信息卡：它<b>不</b>承载任何业务数据，也不构成功能依赖。
/// </para>
/// </summary>
public static class McpAccessEndpoints
{
    /// <summary>Server Card（Draft）。客户端支持与否都不影响正常连接。</summary>
    public const string ServerCardPath = "/.well-known/mcp/server-card.json";

    public static void MapMcpAccessEndpoints(
        this WebApplication app,
        McpServerRegistration mcp,
        OAuthServerContext? oauthContext)
    {
        var group = app.MapGroup("/api/mcp").WithTags("MCP");

        // ── 协议兼容性诊断（阶段一的验收凭据） ──
        group.MapGet("/diagnostics", (
            McpProtocolDiagnostics diagnostics,
            IOptions<McpOptions> mcpOptions,
            IOptions<McpOAuthOptions> oauthOptions) =>
        {
            var oauth = oauthOptions.Value;

            return Results.Ok(new
            {
                server = new
                {
                    enabled = mcp.Enabled,
                    path = mcp.Path,
                    requireClientKey = mcpOptions.Value.RequireClientKey
                },
                protocol = diagnostics.Snapshot(),
                channels = new
                {
                    clientKey = new
                    {
                        enabled = mcpOptions.Value.RequireClientKey,
                        header = "Authorization: Bearer maf_… 或 X-Api-Key: maf_…"
                    },
                    oauth = new
                    {
                        enabled = oauthContext is not null,
                        issuer = oauthContext?.IssuerString,
                        resource = oauthContext?.ResourceString,
                        cimd = oauthOptions.Value.Cimd.Enabled,
                        dynamicRegistration = oauthOptions.Value.AllowDynamicRegistration,
                        protectedResourceMetadata = oauthContext?.ProtectedResourceMetadataUrl,
                        authorizationServerMetadata = oauthContext?.AuthorizationServerMetadataUrl
                    }
                },
                // 阶段一验收清单：照着做就能留下「Legacy / Modern 各一条成功记录」。
                verificationChecklist = new[]
                {
                    "用目标 Cursor 版本连一次 /mcp：看 recent[0].firstMethod 是 initialize 还是 server/discover",
                    "用 Claude Desktop 连一次：同上，并记录 protocolVersion",
                    "调用一次 search_knowledge_base：recent 里应出现 tools/call 且 authenticated=true",
                    "若 firstMethod=server/discover 且返回 200：Modern 通道正常（SDK 已实现 server/discover）",
                    "若某请求返回 UnsupportedProtocolVersionError：把该客户端版本与错误体一并记录，作为兼容性结论"
                }
            });
        });

        // ── 本地配置一键复制 ──
        group.MapGet("/local-config", (
            HttpContext http,
            IServer server,
            IOptions<ServerSecurityOptions> serverOptions) =>
        {
            var security = serverOptions.Value;
            var resolved = ResolveBaseUrl(http, server, oauthContext, security.PublicBaseUrl);
            var baseUrl = resolved.Url;

            var clientKey = http.Request.Query["clientKey"].ToString();
            if (string.IsNullOrWhiteSpace(clientKey)) clientKey = null;

            var fixedPort = security.FixedLocalPort;

            return Results.Ok(new
            {
                endpoint = LocalMcpConfig.BuildEndpoint(baseUrl, mcp.Path),
                baseUrl,
                // 地址来源：让前端/用户能判断这份配置是不是「本机可用但对方连不上」。
                baseUrlSource = resolved.Source,
                port = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Port : (int?)null,
                fixedLocalPort = fixedPort > 0 ? fixedPort : (int?)null,
                cursor = LocalMcpConfig.BuildCursorConfig(baseUrl, mcp.Path, clientKey),
                claudeDesktop = LocalMcpConfig.BuildClaudeDesktopConfig(baseUrl, mcp.Path, clientKey),
                curl = LocalMcpConfig.BuildCurlSnippet(baseUrl, mcp.Path, clientKey),
                notes = BuildLocalConfigNotes(
                    baseUrl,
                    resolved.Source,
                    fixedPort,
                    callerIsLoopback: http.Connection.RemoteIpAddress is null
                        || System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress))
            });
        });

        // ── 认证事件审计（与工具调用审计分开查） ──
        group.MapGet("/auth-events", async (
            int? limit,
            IOAuthStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                events = await store.QueryAuthEventsAsync(limit ?? 50, cancellationToken)
            }));

        // ── 授权同意列表与撤销 ──
        group.MapGet("/oauth-consents", async (
            IOptions<McpOAuthOptions> oauthOptions,
            IOAuthStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(new
            {
                consents = await store.ListConsentsAsync(oauthOptions.Value.OwnerSubject, cancellationToken)
            }));

        group.MapPost("/oauth-consents/{id}/revoke", async (
            string id,
            HttpContext http,
            OAuthService oauth,
            CancellationToken cancellationToken) =>
        {
            var revoked = await oauth.RevokeConsentAsync(
                id, http.Connection.RemoteIpAddress?.ToString(), cancellationToken);

            return revoked ? Results.NoContent() : Results.NotFound();
        });

        // ── Server Card（公开、静态、无功能依赖） ──
        app.MapGet(ServerCardPath, (HttpContext http, IOptions<ServerSecurityOptions> serverOptions) =>
        {
            var baseUrl = ResolveBaseUrl(http, null, oauthContext, serverOptions.Value.PublicBaseUrl).Url;
            return Results.Json(BuildServerCard(baseUrl, mcp, oauthContext));
        });
    }

    /// <summary>
    /// 「一键复制」里给人看的提示语。
    ///
    /// <para>
    /// 只有当「调用方不是回环」+「解析出来的是回环地址」同时成立时才告警：
    /// 这正好是「服务端部署、但没配 PublicBaseUrl、OAuth 也没开」的形状。
    /// 桌面版（调用方也是回环）不会看到这条噪音。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<string> BuildLocalConfigNotes(
        string baseUrl, string source, int fixedPort, bool callerIsLoopback = true)
    {
        var notes = new List<string>
        {
            "没有密钥时先调 POST /api/agent-clients 建一个，或在设置页新建；密钥明文只出现一次。",
            "Cursor：把 cursor 字段内容合并进 ~/.cursor/mcp.json。",
            "Claude Desktop：把 claudeDesktop 字段内容合并进 claude_desktop_config.json（需要 Node/npx）。"
        };

        var loopback = Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;
        if (loopback && !callerIsLoopback
            && !string.Equals(source, "public-base-url", StringComparison.Ordinal))
        {
            notes.Add($"当前生成的是回环地址 {baseUrl}，别的机器连不上。你正在从外部访问本服务，请在配置里填 Server:PublicBaseUrl（或启用 OAuth 并配好 Issuer）。");
        }

        notes.Add(fixedPort > 0
            ? $"桌面版使用专属端口 {fixedPort}（Server:FixedLocalPort），正常情况下长期不变；被占用时会自动改用随机端口，届时以本页显示的地址为准。"
            : "桌面版当前使用随机回环端口：重启后可能变化，地址变了重新复制即可。");

        return notes;
    }

    /// <summary>Server Card 内容。刻意只描述，不驱动任何功能。</summary>
    internal static JsonObject BuildServerCard(
        string baseUrl,
        McpServerRegistration mcp,
        OAuthServerContext? oauthContext)
    {
        var endpoint = LocalMcpConfig.BuildEndpoint(baseUrl, mcp.Path);

        var categories = McpToolCatalog.All
            .GroupBy(tool => AgentClientCapabilities.ScopeOf(tool.Capability), StringComparer.Ordinal)
            .Select(group => new JsonObject
            {
                ["name"] = group.Key,
                ["tools"] = new JsonArray(group.Select(tool => (JsonNode)tool.Name!).ToArray()),
                ["hasWriteTools"] = group.Any(tool => tool.IsWrite)
            })
            .ToArray();

        var authSchemes = new JsonArray();
        if (oauthContext is not null)
        {
            authSchemes.Add(new JsonObject
            {
                ["type"] = "oauth2",
                ["authorizationServerMetadata"] = oauthContext.AuthorizationServerMetadataUrl,
                ["protectedResourceMetadata"] = oauthContext.ProtectedResourceMetadataUrl,
                ["scopes"] = new JsonArray(OAuthScopes.Supported.Select(scope => (JsonNode)scope!).ToArray())
            });
        }

        authSchemes.Add(new JsonObject
        {
            ["type"] = "apiKey",
            ["in"] = "header",
            ["name"] = "Authorization",
            ["description"] = "客户端密钥（maf_…），可用 X-Api-Key 头替代"
        });

        return new JsonObject
        {
            ["$schema"] = "https://static.modelcontextprotocol.io/schemas/mcp-server-card/v1.json",
            ["version"] = "1.0",
            ["protocolVersion"] = new JsonArray(
                McpProtocolDiagnostics.SupportedProtocolVersions.Select(version => (JsonNode)version!).ToArray()),
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "zhijian-knowledge",
                ["version"] = "1.0.0",
                ["title"] = "知间知识库"
            },
            ["description"] = "把本地知识库以 Streamable HTTP 暴露给外部智能体。OAuth 只决定谁被授权；每次工具调用仍由能力、工具白名单、知识库范围与限流逐次判定。",
            ["transports"] = new JsonArray(new JsonObject
            {
                ["type"] = "streamable-http",
                ["url"] = endpoint
            }),
            ["authentication"] = new JsonObject
            {
                ["required"] = true,
                ["schemes"] = authSchemes
            },
            ["tools"] = new JsonObject
            {
                ["count"] = McpToolCatalog.All.Count,
                ["categories"] = new JsonArray(categories)
            },
            ["documentation"] = baseUrl.TrimEnd('/') + "/docs/MCP-ACCESS.md"
        };
    }

    /// <summary>
    /// 计算对外可用的基地址。优先级：
    /// <list type="number">
    ///   <item><description><c>Mcp:OAuth:Issuer</c>：开启 OAuth 时它是权威值，令牌的 <c>aud</c> 按它签发。</description></item>
    ///   <item><description><c>Server:PublicBaseUrl</c>：显式声明的公网地址（服务端部署、未开 OAuth）。</description></item>
    ///   <item><description>Kestrel 实际监听地址（桌面版随机回环端口）。</description></item>
    ///   <item><description>请求自身的 scheme/host（最后的兜底）。</description></item>
    /// </list>
    /// <para>
    /// <paramref name="publicBaseUrl"/> 只影响「给人看的配置片段」，不参与任何鉴权判定。
    /// </para>
    /// </summary>
    /// <returns>解析出的地址，以及它的来源（写入 <c>baseUrlSource</c> 供前端提示）。</returns>
    private static (string Url, string Source) ResolveBaseUrl(
        HttpContext http,
        IServer? server,
        OAuthServerContext? oauthContext,
        string? publicBaseUrl)
    {
        if (oauthContext is not null) return (oauthContext.IssuerString, "oauth-issuer");

        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
            return (publicBaseUrl.Trim().TrimEnd('/'), "public-base-url");

        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(item =>
            item.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(address))
        {
            return (address
                .Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal)
                .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
                .Replace("://localhost", "://127.0.0.1", StringComparison.Ordinal)
                .TrimEnd('/'), "kestrel");
        }

        return ($"{http.Request.Scheme}://{http.Request.Host}".TrimEnd('/'), "request");
    }
}
