using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Contracts;
using MyFirstMafAgent.Api.Domain.Settings;

namespace MyFirstMafAgent.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var routes = endpoints.MapGroup("/api/settings").WithTags("设置");

        routes.MapGet("", (SettingsService settings, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
                Results.Ok(await settings.GetAsync(cancellationToken))));

        routes.MapPut("", (UpdateAppSettingsRequest request, SettingsService settings, CancellationToken cancellationToken) =>
            ApiResults.ExecuteAsync(async () =>
            {
                var command = new SaveSettingsCommand(
                    request.UseCloudApi,
                    ToInput(request.Local),
                    ToInput(request.Online),
                    request.Preferences?.DisplayName,
                    request.Preferences?.DailyGoalMinutes,
                    request.EmbeddingSpecId,
                    ToAgentPreferences(request.Agent));
                return Results.Ok(await settings.SaveAsync(command, cancellationToken));
            }));
    }

    private static EndpointInput? ToInput(UpdateModelEndpointRequest? request) =>
        request is null ? null : new EndpointInput(request.Endpoint, request.Model, request.ApiKey);

    /// <summary>请求未携带智能体开关时返回 null，保存逻辑会保留原有偏好。</summary>
    private static AgentPreferences? ToAgentPreferences(AgentPreferencesRequest? request) =>
        request is null
            ? null
            : AgentPreferences.Create(request.EnableToolCalling, request.EnableWebSearch, request.EnableFileAccess);
}
