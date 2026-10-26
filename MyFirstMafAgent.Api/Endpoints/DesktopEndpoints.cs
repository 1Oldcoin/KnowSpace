using System.Net;
using MyFirstMafAgent.Api.Hosting;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 桌面宿主控制面：探活 + 优雅停止。
///
/// <para>
/// 这两个动作在<b>所有</b>启动方式下都存在，因为「窗口后开、后台先跑」要求窗口能：
/// ① 探出后台还活着（/info），② 用户勾了「关闭所有服务」时让后台自己退出（/shutdown）。
/// 数据面有令牌时它们自动受令牌保护（都在 <c>/api</c> 之下）；没有令牌的纯本机服务
/// 由下面的回环来源校验兜底——把服务绑到 0.0.0.0 时外部地址连不上这两个端点。
/// </para>
/// </summary>
public static class DesktopEndpoints
{
    public static void MapDesktopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/desktop")
            .WithTags("桌面宿主");

        group.MapGet("/info", (HttpContext context, IHostEnvironment environment) =>
            IsLoopback(context) ? Results.Ok(new
            {
                platform = Environment.OSVersion.Platform.ToString(),
                runtime = Environment.Version.ToString(),
                environment = environment.EnvironmentName,
                processId = Environment.ProcessId
            }) : Results.NotFound());

        group.MapPost("/shutdown", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            // 不是本机来的就直接当作不存在，避免服务绑到局域网时被人远程关掉。
            if (!IsLoopback(context)) return Results.NotFound();

            // 不阻塞响应，先返回再停止，避免宿主拿不到 HTTP 结果。
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                lifetime.StopApplication();
            });

            return Results.Accepted(value: new { status = "stopping" });
        });
    }

    private static bool IsLoopback(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null) return false;

        // IPv4-mapped IPv6（::ffff:127.0.0.1）要先还原再判断。
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();

        return IPAddress.IsLoopback(remote);
    }
}
