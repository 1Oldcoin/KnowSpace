using MyFirstMafAgent.Api.Hosting;

namespace MyFirstMafAgent.Api.Endpoints;

/// <summary>
/// 桌面宿主控制面。仅在桌面令牌启用时注册，且与数据面一样需要令牌。
/// 宿主退出时调用 shutdown，让 Kestrel/Worker 优雅停止，再停止由本应用启动的 pg0。
/// </summary>
public static class DesktopEndpoints
{
    public static void MapDesktopEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/desktop")
            .WithTags("桌面宿主");

        group.MapGet("/info", (IHostEnvironment environment) => Results.Ok(new
        {
            platform = Environment.OSVersion.Platform.ToString(),
            runtime = Environment.Version.ToString(),
            environment = environment.EnvironmentName,
            processId = Environment.ProcessId
        }));

        group.MapPost("/shutdown", (IHostApplicationLifetime lifetime) =>
        {
            // 不阻塞响应，先返回再停止，避免宿主拿不到 HTTP 结果。
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150));
                lifetime.StopApplication();
            });

            return Results.Accepted(value: new { status = "stopping" });
        });
    }
}
