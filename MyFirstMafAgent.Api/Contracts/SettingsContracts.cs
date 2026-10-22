namespace MyFirstMafAgent.Api.Contracts;

public sealed record UpdateAppSettingsRequest(
    bool UseCloudApi,
    UpdateModelEndpointRequest? Local,
    UpdateModelEndpointRequest? Online,
    UpdateUserPreferencesRequest? Preferences,
    Guid? EmbeddingSpecId = null,
    AgentPreferencesRequest? Agent = null);

public sealed record UpdateModelEndpointRequest(string? Endpoint, string? Model, string? ApiKey);

public sealed record UpdateUserPreferencesRequest(string? DisplayName, int? DailyGoalMinutes);

/// <summary>智能体开关：字段为 null 表示该开关本次不修改。</summary>
public sealed record AgentPreferencesRequest(bool? EnableToolCalling, bool? EnableWebSearch, bool? EnableFileAccess);

public sealed record ModelEndpointResponse(string Endpoint, string Model, bool HasApiKey, string ApiKeyHint);

public sealed record UserPreferencesResponse(string DisplayName, int DailyGoalMinutes);

public sealed record RuntimeSettingsResponse(
    string EmbeddingModelDirectory,
    int EmbeddingDimensions,
    string StorageRootPath,
    long MaxFileSizeBytes,
    long MaxVideoFileSizeBytes);

public sealed record AgentPreferencesResponse(bool EnableToolCalling, bool EnableWebSearch, bool EnableFileAccess);

public sealed record AppSettingsResponse(
    bool UseCloudApi,
    ModelEndpointResponse Local,
    ModelEndpointResponse Online,
    UserPreferencesResponse Preferences,
    RuntimeSettingsResponse Runtime,
    AgentPreferencesResponse Agent);
