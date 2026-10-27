using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 服务端部署加固：可信代理头 + 响应安全头。
///
/// <para>
/// <b>为什么必须做转发头</b>：放在 nginx / Caddy / 云负载均衡后面时，
/// <c>Connection.RemoteIpAddress</c> 永远是代理的地址。这带来两个后果，
/// 第二个是真实的可利用漏洞：
/// </para>
/// <list type="number">
/// <item>MCP 调用审计与客户端 <c>LastSeenAddress</c> 全变成 127.0.0.1，出事时无法溯源。</item>
/// <item><c>DesktopEndpoints</c> 用回环地址判断「是不是本机」，于是经代理转进来的
/// <c>POST /api/desktop/shutdown</c> 会被当成本机请求——<b>公网任何人都能关掉服务</b>。</item>
/// </list>
/// </summary>
public static class ServerSecurity
{
    /// <summary>
    /// 启用可信代理头。必须在任何读取 <c>RemoteIpAddress</c> 的中间件之前调用，
    /// 因此放在管线最前面。
    /// </summary>
    public static void UseTrustedForwardedHeaders(this WebApplication app, ServerSecurityOptions options)
    {
        if (!options.TrustForwardedHeaders) return;

        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };

        // 默认值里含回环网络，先清掉再按配置重建，避免「以为只信某个代理，其实还信一堆」。
        forwarded.KnownNetworks.Clear();
        forwarded.KnownProxies.Clear();

        var added = 0;
        foreach (var proxy in options.KnownProxies)
        {
            if (IPAddress.TryParse(proxy?.Trim(), out var address))
            {
                forwarded.KnownProxies.Add(address);
                added++;
            }
            else if (!string.IsNullOrWhiteSpace(proxy))
            {
                app.Logger.LogWarning(
                    "Server:KnownProxies 里的 '{Proxy}' 不是合法 IP，已忽略。转发头可能被伪造。", proxy);
            }
        }

        if (added == 0)
        {
            // 最常见的部署：反向代理与 API 同机（docker compose、同一台 nginx）。
            forwarded.KnownProxies.Add(IPAddress.Loopback);
            forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
        }

        forwarded.ForwardLimit = 2;

        app.UseForwardedHeaders(forwarded);

        app.Logger.LogInformation(
            "已启用可信代理头：ForwardLimit=2，KnownProxies={Proxies}",
            string.Join(", ", forwarded.KnownProxies));
    }

    /// <summary>
    /// <c>Server:EnableMdns</c> 目前只是配置项，没有真正的 DNS-SD 广播器。
    ///
    /// <para>
    /// <b>为什么必须报警而不是静默</b>：一个能改却没有任何效果的开关是「假契约」——
    /// 用户填 true、重启、期待局域网能自动发现，结果什么都不会发生，也不会有人告诉他为什么。
    /// 这比「本版本不支持」更糟，因为排障时没有任何线索。
    /// </para>
    /// <para>
    /// 顺带把边界说清：mDNS 是<b>发现</b>协议不是<b>授权</b>协议，即使将来实现，
    /// 也不会替代 <c>maf_</c> 客户端密钥或 OAuth；而且 Cursor / Claude Desktop 这两个主流
    /// MCP 客户端目前都不做 mDNS 发现，它们只认一条写死的 URL 或 stdio 命令。
    /// 真正的入口是 <c>/api/mcp/local-config</c> 与 <c>/.well-known/mcp/server-card.json</c>。
    /// </para>
    /// </summary>
    public static void WarnUnimplementedMdns(this WebApplication app, ServerSecurityOptions options)
    {
        if (!options.EnableMdns) return;

        app.Logger.LogWarning(
            "Server:EnableMdns=true，但本版本未实现 mDNS/DNS-SD 广播：服务名 '{ServiceName}' 不会出现在局域网上，" +
            "本次启动也不会有任何发现行为。请改用 GET /api/mcp/local-config 或 " +
            "GET /.well-known/mcp/server-card.json 获取客户端配置。若确实需要局域网自动发现，" +
            "请提需求而不是改这个开关。",
            options.MdnsServiceName);
    }

    /// <summary>
    /// 给所有响应补安全头。只补缺、不覆盖，避免和已有的严格 CSP 打架。
    /// </summary>
    public static void UseSecurityHeaders(this WebApplication app, ServerSecurityOptions options)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // 不让浏览器猜 MIME：上传的资料会被原样回放，猜错类型可能变成可执行内容。
                if (!headers.ContainsKey("X-Content-Type-Options"))
                    headers["X-Content-Type-Options"] = "nosniff";

                // 不允许被任何站点 iframe 嵌入（防点击劫持）。
                if (!headers.ContainsKey("X-Frame-Options"))
                    headers["X-Frame-Options"] = "DENY";

                if (!headers.ContainsKey("Referrer-Policy"))
                    headers["Referrer-Policy"] = "no-referrer";

                if (options.HideServerHeader && headers.ContainsKey("Server"))
                    headers.Remove("Server");

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
