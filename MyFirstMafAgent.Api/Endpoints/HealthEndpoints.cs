using MyFirstMafAgent.Api.Application.Health;

namespace MyFirstMafAgent.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/health")
            .WithTags("系统")
            .MapGet("", async (
                IDatabaseHealthProbe serverProbe,
                IDatabasePoolProbe poolProbe,
                ILoggerFactory loggerFactory,
                CancellationToken cancellationToken) =>
            {
                var logger = loggerFactory.CreateLogger("MyFirstMafAgent.Api.Health");

                // 先看数据库本身。它挂了，后面的池探针必然也失败，报错要指向真正的原因。
                try
                {
                    await serverProbe.CheckAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Database health check failed");
                    return Results.Json(
                        new { status = "error", database = "unavailable", pool = "unknown" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                // 数据库正常，再看业务连接池。池被耗尽时这里失败、上面成功——
                // 这正是「API 活着但完全不能干活」的状态，必须报 503 让外层看门狗介入。
                try
                {
                    await poolProbe.CheckAsync(cancellationToken);
                    return Results.Ok(new { status = "ok", database = "connected", pool = "ok" });
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Database pool check failed while the server itself is reachable");
                    return Results.Json(
                        new { status = "degraded", database = "connected", pool = "exhausted" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
