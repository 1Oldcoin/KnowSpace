using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 本机/服务端 API 安全设置。
///
/// <para>
/// 监听 127.0.0.1 不等同于鉴权：同一台机器上的其它进程同样可以访问回环端口。
/// 因此桌面模式必须携带一次性随机令牌。令牌由宿主进程生成并通过环境变量传入，
/// <b>不</b>写入 URL、日志、前端持久化存储，也不出现在 ready 文件中。
/// </para>
///
/// <para>
/// 服务端部署（云主机/容器/内网）没有宿主来生成令牌，改用长期令牌
/// <c>Server:ApiToken</c> / 环境变量 <c>MAF_SERVER_TOKEN</c>，走同一个中间件、同一个请求头。
/// 反向代理也可以在自己的认证通过后注入 <c>X-Api-Token</c>，浏览器端就不需要改代码。
/// </para>
///
/// <para>
/// 两者都没配置时（纯本机控制台调试），本层整体禁用，行为与之前完全一致。
/// </para>
/// </summary>
public sealed record DesktopSecurityOptions(
    bool Enabled,
    string? Token,
    bool StrictContentSecurityPolicy,
    bool AllowAnonymousHealth = false)
{
    public const string TokenEnvironmentVariable = "DESKTOP_API_TOKEN";
    public const string StrictCspEnvironmentVariable = "DESKTOP_STRICT_CSP";
    public const string TokenHeaderName = "X-Api-Token";

    /// <summary>健康检查路径：负载均衡与容器探活拿不到令牌，需要单独放行。</summary>
    public const string HealthPath = "/api/health";

    /// <summary>受限内容安全策略：用于桌面窗口，禁止内联脚本与外部资源。</summary>
    public const string StrictContentSecurityPolicyValue =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "media-src 'self' blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    public static DesktopSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        // 优先级：桌面宿主注入的环境变量 > 服务端长期令牌环境变量 > 配置文件。
        // 桌面令牌是每次启动重新生成的，一旦存在就应该压过任何写在文件里的静态值。
        var configured = configuration["Desktop:ApiToken"];
        var token = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable(TokenEnvironmentVariable)
            : configured;

        if (string.IsNullOrWhiteSpace(token))
            token = Environment.GetEnvironmentVariable(ServerSecurityOptions.TokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(token))
            token = configuration[$"{ServerSecurityOptions.SectionName}:ApiToken"];

        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        var strictCsp = configuration.GetValue<bool?>("Desktop:StrictContentSecurityPolicy")
            ?? ParseBoolean(Environment.GetEnvironmentVariable(StrictCspEnvironmentVariable));

        var allowAnonymousHealth = configuration.GetValue<bool?>($"{ServerSecurityOptions.SectionName}:AllowAnonymousHealth") ?? false;

        return new DesktopSecurityOptions(!string.IsNullOrEmpty(token), token, strictCsp, allowAnonymousHealth);
    }

    private static bool ParseBoolean(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// API 令牌校验。只有 REST 数据面（<c>/api</c>）需要令牌；
/// 静态资源与 SPA 文档本身不需要，避免窗口加载首页时依赖自定义请求头。
///
/// <para>
/// <c>/mcp</c> <b>不在</b>此列：它是给外部智能体用的长期接口，
/// 而桌面令牌按设计不下发、不落盘，外部客户端根本拿不到。
/// MCP 的鉴权改由客户端密钥完成，见 <see cref="AgentClientGate"/>。
/// </para>
/// </summary>
public static class LocalApiTokenValidator
{
    private static readonly string[] ProtectedPrefixes = ["/api"];

    /// <summary>
    /// 该请求路径是否属于必须鉴权的数据面。
    /// <paramref name="allowAnonymousHealth"/> 为 true 时放行 <c>/api/health</c>（负载均衡探活）。
    /// </summary>
    public static bool RequiresToken(PathString path, bool allowAnonymousHealth = false)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            if (!path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (allowAnonymousHealth && IsHealthPath(path)) return false;
            return true;
        }

        return false;
    }

    private static bool IsHealthPath(PathString path) =>
        path.Equals(new PathString(DesktopSecurityOptions.HealthPath), StringComparison.OrdinalIgnoreCase)
        || path.Equals(new PathString(DesktopSecurityOptions.HealthPath + "/"), StringComparison.OrdinalIgnoreCase);

    /// <summary>令牌是否匹配。使用固定时间比较，避免计时侧信道。</summary>
    public static bool IsAuthorized(DesktopSecurityOptions options, PathString path, StringValues headerValue)
    {
        if (!options.Enabled) return true;
        if (!RequiresToken(path, options.AllowAnonymousHealth)) return true;
        if (StringValues.IsNullOrEmpty(headerValue)) return false;

        var provided = headerValue.ToString();
        if (string.IsNullOrEmpty(provided)) return false;

        return FixedTimeEquals(provided, options.Token!);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>桌面安全中间件：本机令牌校验 + 严格 CSP。</summary>
public static class DesktopSecurityMiddleware
{
    public static void UseDesktopSecurity(this WebApplication app, DesktopSecurityOptions options)
    {
        if (!options.Enabled && !options.StrictContentSecurityPolicy) return;

        if (options.StrictContentSecurityPolicy)
        {
            app.Use(async (context, next) =>
            {
                context.Response.OnStarting(() =>
                {
                    var contentType = context.Response.ContentType;
                    if (contentType is not null
                        && contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.Headers["Content-Security-Policy"] =
                            DesktopSecurityOptions.StrictContentSecurityPolicyValue;
                        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                        context.Response.Headers["Referrer-Policy"] = "no-referrer";
                    }

                    return Task.CompletedTask;
                });

                await next();
            });
        }

        if (!options.Enabled) return;

        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.DesktopSecurity");

        app.Use(async (context, next) =>
        {
            if (!LocalApiTokenValidator.RequiresToken(context.Request.Path, options.AllowAnonymousHealth))
            {
                await next();
                return;
            }

            var header = context.Request.Headers[DesktopSecurityOptions.TokenHeaderName];
            if (LocalApiTokenValidator.IsAuthorized(options, context.Request.Path, header))
            {
                await next();
                return;
            }

            // 不记录令牌内容，只记录被拒绝的方法与路径。
            logger.LogWarning("Rejected local API request without a valid desktop token: {Method} {Path}",
                context.Request.Method, context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Results.Problem(
                title: "A valid local API token is required.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
                .ExecuteAsync(context);
        });
    }
}
