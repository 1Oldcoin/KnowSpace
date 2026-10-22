using SqlSugar;

namespace MyFirstMafAgent.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/api/health")
            .WithTags("系统")
            .MapGet("", async (SqlSugarScope database, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
            {
                try
                {
                    await Task.Run(() => database.Ado.CheckConnection(), cancellationToken);
                    return Results.Ok(new { status = "ok", database = "connected" });
                }
                catch (Exception exception)
                {
                    loggerFactory.CreateLogger("MyFirstMafAgent.Api.Health").LogError(exception, "Database health check failed");
                    return Results.Json(new { status = "error", database = "unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });
    }
}
