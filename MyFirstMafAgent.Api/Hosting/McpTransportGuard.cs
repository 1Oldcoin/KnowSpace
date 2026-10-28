using System.Net;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// MCP 传输层守卫：校验 <c>Host</c> 与 <c>Origin</c>。
///
/// <para>
/// 为什么必须有：<c>/mcp</c> 是本机/内网监听的 HTTP 端点，浏览器里的任意网页都能向它发请求。
/// 攻击者把自己的域名解析到 <c>127.0.0.1</c>（DNS rebinding）之后，<b>Host 头仍是攻击者域名</b>——
/// 这正是 MCP 规范要求服务端校验 <c>Origin</c> 并「绑定到 localhost」的原因。
/// 只靠令牌不够：拿到令牌的浏览器脚本同样会带上它。
/// </para>
///
/// <para>
/// 判定规则刻意简单：
/// </para>
/// <list type="bullet">
///   <item><description>Host 必须在允许列表内（回环名 + OAuth Issuer/PublicBaseUrl 的主机 + 显式配置），否则 403。</description></item>
///   <item><description>Origin <b>出现</b>就必须在允许列表内（含回环源与显式配置），否则 403；没有 Origin 的非浏览器客户端不受影响。</description></item>
/// </list>
/// </summary>
public sealed class McpTransportGuard
{
    private readonly HashSet<string> _allowedHosts;
    private readonly HashSet<string> _allowedOrigins;
    private readonly bool _allowLoopbackOrigins;

    private McpTransportGuard(
        HashSet<string> allowedHosts,
        HashSet<string> allowedOrigins,
        bool allowLoopbackOrigins)
    {
        _allowedHosts = allowedHosts;
        _allowedOrigins = allowedOrigins;
        _allowLoopbackOrigins = allowLoopbackOrigins;
    }

    public static McpTransportGuard Create(
        McpOptions mcp,
        McpOAuthOptions oauth,
        ServerSecurityOptions server)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost",
            "127.0.0.1",
            "::1",
            "[::1]"
        };

        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in mcp.AllowedHosts)
        {
            if (!string.IsNullOrWhiteSpace(host)) hosts.Add(host.Trim());
        }

        foreach (var origin in mcp.AllowedOrigins)
        {
            if (TryNormalizeOrigin(origin, out var normalized)) origins.Add(normalized);
        }

        // 公网地址本身就是合法来源：Issuer 与 PublicBaseUrl 都是运维显式配置的。
        AddFromUrl(oauth.Issuer, hosts, origins);
        AddFromUrl(server.PublicBaseUrl, hosts, origins);

        return new McpTransportGuard(hosts, origins, allowLoopbackOrigins: true);

        static void AddFromUrl(string? value, HashSet<string> hosts, HashSet<string> origins)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return;

            hosts.Add(uri.Host);
            origins.Add(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'));
        }
    }

    /// <summary>校验通过返回 <c>null</c>，否则返回给客户端的原因。</summary>
    public string? Validate(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!string.IsNullOrEmpty(host) && !_allowedHosts.Contains(host))
        {
            return $"Host '{host}' 不在允许列表内（Mcp:AllowedHosts）。";
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return null;

        if (TryNormalizeOrigin(origin, out var normalized))
        {
            if (_allowedOrigins.Contains(normalized)) return null;
            if (_allowLoopbackOrigins && IsLoopbackOrigin(normalized)) return null;
        }

        return $"Origin '{origin}' 不在允许列表内（Mcp:AllowedOrigins）。";
    }

    internal static bool TryNormalizeOrigin(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        // "null" 是沙箱 iframe / file:// 发来的不透明来源，永远不接受。
        if (value.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        normalized = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static bool IsLoopbackOrigin(string normalized)
    {
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }
}

public static class McpTransportGuardExtensions
{
    /// <summary>
    /// 挂在 <c>AgentClientGate</c> <b>之前</b>：Host/Origin 不合格的请求连鉴权都不该进入，
    /// 也不该计数（否则攻击页面能把真实用户的 IP 一起封掉）。
    /// </summary>
    public static void UseMcpTransportGuard(this WebApplication app, McpServerRegistration mcp)
    {
        if (!mcp.Enabled) return;

        var options = app.Services.GetRequiredService<IOptions<McpOptions>>().Value;
        var guard = McpTransportGuard.Create(
            options,
            app.Services.GetRequiredService<IOptions<McpOAuthOptions>>().Value,
            app.Services.GetRequiredService<IOptions<ServerSecurityOptions>>().Value);

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.McpTransport");

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(mcp.Path, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            // 预检请求不带凭据也不碰数据，直接放行给后续处理（CORS 由反代决定）。
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                await next();
                return;
            }

            if (guard.Validate(context) is { } reason)
            {
                logger.LogWarning(
                    "Rejected MCP request from {Remote} on {Method} {Path}: {Reason}",
                    context.Connection.RemoteIpAddress, context.Request.Method, context.Request.Path, reason);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";
                var payload = new System.Text.Json.Nodes.JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = null,
                    ["error"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["code"] = -32600,
                        ["message"] = reason
                    }
                };
                await context.Response.WriteAsync(payload.ToJsonString());
                return;
            }

            await next();
        });
    }
}
