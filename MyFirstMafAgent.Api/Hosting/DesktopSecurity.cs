using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 桌面宿主（Electron / Photino）的本机 API 安全设置。
///
/// <para>
/// 监听 127.0.0.1 不等同于鉴权：同一台机器上的其它进程同样可以访问回环端口。
/// 因此桌面模式必须携带一次性随机令牌。令牌由宿主进程生成并通过环境变量传入，
/// <b>不</b>写入 URL、日志、前端持久化存储，也不出现在 ready 文件中。
/// </para>
///
/// <para>
/// 控制台/开发模式不设置令牌时，本层整体禁用，行为与之前完全一致。
/// </para>
/// </summary>
public sealed record DesktopSecurityOptions(bool Enabled, string? Token, bool StrictContentSecurityPolicy)
{
    public const string TokenEnvironmentVariable = "DESKTOP_API_TOKEN";
    public const string StrictCspEnvironmentVariable = "DESKTOP_STRICT_CSP";
    public const string TokenHeaderName = "X-Api-Token";

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
        var configured = configuration["Desktop:ApiToken"];
        var token = string.IsNullOrWhiteSpace(configured)
            ? Environment.GetEnvironmentVariable(TokenEnvironmentVariable)
            : configured;

        token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();

        var strictCsp = configuration.GetValue<bool?>("Desktop:StrictContentSecurityPolicy")
            ?? ParseBoolean(Environment.GetEnvironmentVariable(StrictCspEnvironmentVariable));

        return new DesktopSecurityOptions(!string.IsNullOrEmpty(token), token, strictCsp);
    }

    private static bool ParseBoolean(string? value) =>
        value is not null
        && (value.Equals("1", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 本机 API 令牌校验。只有数据面（<c>/api</c>、<c>/mcp</c>）需要令牌；
/// 静态资源与 SPA 文档本身不需要，避免窗口加载首页时依赖自定义请求头。
/// </summary>
public static class LocalApiTokenValidator
{
    private static readonly string[] ProtectedPrefixes = ["/api", "/mcp"];

    /// <summary>该请求路径是否属于必须鉴权的数据面。</summary>
    public static bool RequiresToken(PathString path)
    {
        foreach (var prefix in ProtectedPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>令牌是否匹配。使用固定时间比较，避免计时侧信道。</summary>
    public static bool IsAuthorized(DesktopSecurityOptions options, PathString path, StringValues headerValue)
    {
        if (!options.Enabled) return true;
        if (!RequiresToken(path)) return true;
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
            if (!LocalApiTokenValidator.RequiresToken(context.Request.Path))
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
