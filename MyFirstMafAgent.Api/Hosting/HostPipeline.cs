using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Endpoints;
using MyFirstMafAgent.Api.OAuth;
using MyFirstMafAgent.Api.Options;
using Scalar.AspNetCore;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// HTTP 管线与端点映射：请求日志、异常处理、静态前端、REST/MCP/OpenAPI 与 SPA 回退。
/// 业务组合根（控制台、桌面壳、未来 Electron 宿主）共用同一管线，避免多套行为。
/// </summary>
public static class HostPipeline
{
    /// <summary>注册中间件与静态文件，返回前端是否已发布。</summary>
    public static bool Configure(
        WebApplication app,
        DesktopSecurityOptions desktopSecurity,
        McpServerRegistration mcp,
        ServerSecurityOptions serverSecurity)
    {
        // 转发头必须在最前面：后面所有读 RemoteIpAddress 的逻辑（审计、限流、回环判定）都靠它。
        app.UseTrustedForwardedHeaders(serverSecurity);
        app.UseSecurityHeaders(serverSecurity);

        // 配置项与实现不一致时，宁可吵一句也不要静默（见方法注释）。
        app.WarnUnimplementedMdns(serverSecurity);

        var requestLogger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("MyFirstMafAgent.Api.Requests");
        app.Use(async (context, next) =>
        {
            var stopwatch = Stopwatch.StartNew();
            using (requestLogger.BeginScope(new Dictionary<string, object?>
            {
                ["TraceId"] = context.TraceIdentifier,
                ["RequestMethod"] = context.Request.Method,
                ["RequestPath"] = context.Request.Path.Value ?? "/"
            }))
            {
                try
                {
                    await next();
                }
                finally
                {
                    var logLevel = context.Response.StatusCode >= 500
                        ? LogLevel.Error
                        : context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
                    requestLogger.Log(logLevel,
                        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMilliseconds} ms",
                        context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
                }
            }
        });

        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("MyFirstMafAgent.Api.UnhandledExceptions");
            logger.LogError(exception, "Unhandled API exception for {RequestMethod} {RequestPath}. TraceId={TraceId}",
                context.Request.Method, context.Request.Path, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Results.Problem(
                title: "An unexpected error occurred.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
                .ExecuteAsync(context);
        }));
        app.UseCors("Vue");

        // 桌面模式：本机令牌校验（仅 /api）与严格 CSP。控制台模式为无操作。
        app.UseDesktopSecurity(desktopSecurity);

        // MCP 传输层：Host / Origin 白名单（防 DNS rebinding 与跨站调用），必须在鉴权之前。
        app.UseMcpTransportGuard(mcp);

        // MCP 数据面：客户端密钥、启停、限流、工具准入与调用审计。
        app.UseAgentClientGate(mcp);

        // 托管 Vue 构建产物：开发时不存在 wwwroot 也不影响 API；发布脚本会把 dist 复制进来。
        var hasStaticFrontend = HasStaticFrontend(app);
        if (hasStaticFrontend)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        context.Context.Response.Headers.CacheControl = "no-cache";
                }
            });
        }
        else
        {
            app.Logger.LogWarning("未找到 wwwroot/index.html，本次仅提供 API。发布前端请执行 scripts/Publish-Portable.ps1。");
        }

        return hasStaticFrontend;
    }

    /// <summary>映射 REST、MCP、开发期 OpenAPI/Scalar 与前端回退。</summary>
    public static void MapEndpoints(
        WebApplication app,
        bool hasStaticFrontend,
        McpServerRegistration mcp,
        DesktopSecurityOptions desktopSecurity)
    {
        app.MapApiEndpoints();

        // 桌面控制面始终注册：即使这次是命令行/服务方式启动（没有宿主令牌），
        // 后开的窗口也要能探活（/info）并按设置让服务优雅退出（/shutdown）。
        // 两个动作都只允许回环来源，见 DesktopEndpoints。
        app.MapDesktopEndpoints();

        if (mcp.Enabled)
            app.MapMcp(mcp.Path);

        // OAuth 授权服务器（与资源服务器同源）：只有配好 Issuer 才映射。
        var oauthContext = OAuthServerContext.TryCreate(
            app.Services.GetRequiredService<IOptions<McpOAuthOptions>>().Value,
            mcp.Path);

        if (oauthContext is not null)
            app.MapOAuthEndpoints(oauthContext);

        // 诊断 / 本地配置 / Server Card / 认证事件。
        app.MapMcpAccessEndpoints(mcp, oauthContext);

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        if (hasStaticFrontend)
        {
            var indexPath = Path.Combine(app.Environment.WebRootPath, "index.html");
            // 桌面/本地前端使用 Hash 路由；这里仍为根路径和未知前端路径回退到 index.html。
            app.MapFallback(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api")
                    || context.Request.Path.StartsWithSegments(mcp.Path)
                    || context.Request.Path.StartsWithSegments("/oauth")
                    || context.Request.Path.StartsWithSegments("/.well-known")
                    || context.Request.Path.StartsWithSegments("/openapi")
                    || context.Request.Path.StartsWithSegments("/scalar"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(indexPath);
            });
        }
        else
        {
            app.MapGet("/", () => Results.Text(
                "MyFirstMafAgent API 正在运行。前端尚未发布：请先执行 scripts/Publish-Portable.ps1，或使用 Vite 开发服务器（http://localhost:5173）。",
                "text/plain; charset=utf-8"));
        }

        app.Logger.LogInformation("Frontend static files: {Status}",
            hasStaticFrontend ? app.Environment.WebRootPath : "not published");
    }

    private static bool HasStaticFrontend(WebApplication app)
    {
        var webRootPath = app.Environment.WebRootPath;
        return !string.IsNullOrWhiteSpace(webRootPath)
            && File.Exists(Path.Combine(webRootPath, "index.html"));
    }
}
