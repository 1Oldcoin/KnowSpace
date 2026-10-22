using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Common;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Settings;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Application.Settings;

/// <summary>设置仓储端口。LLM 配置模型沿用 <see cref="LlmOptions"/>，它与 appsettings 结构一致。</summary>
public sealed record StoredAppSettings(
    LlmOptions Llm,
    UserPreferences Preferences,
    EmbeddingPreference? Embedding = null,
    // 旧版本 JSON 中没有该字段，读取时需要兜底。
    AgentPreferences? Agent = null);

/// <summary>持久化的向量模型偏好：默认规格不变时保持为空。</summary>
public sealed record EmbeddingPreference(Guid? DefaultSpecId);

public interface ISettingsRepository
{
    Task<StoredAppSettings?> GetAsync(CancellationToken cancellationToken);

    Task SaveAsync(StoredAppSettings settings, CancellationToken cancellationToken);
}

/// <summary>当前生效的聊天模型配置访问端口。</summary>
public interface ILlmSettingsAccessor
{
    LlmOptions Current { get; }

    ModelEndpointOptions Selected { get; }

    void Update(LlmOptions options);
}

/// <summary>只读运行信息端口。</summary>
public interface IRuntimeSettingsProvider
{
    RuntimeSettings Get();
}

public sealed record RuntimeSettings(
    string EmbeddingModelDirectory,
    int EmbeddingDimensions,
    string StorageRootPath,
    long MaxFileSizeBytes,
    long MaxVideoFileSizeBytes);

public sealed record EndpointInput(string? Endpoint, string? Model, string? ApiKey);

public sealed record SaveSettingsCommand(
    bool UseCloudApi,
    EndpointInput? Local,
    EndpointInput? Online,
    string? DisplayName,
    int? DailyGoalMinutes,
    Guid? EmbeddingSpecId = null,
    // 为 null 表示请求未涉及智能体开关，保存时保留原有偏好。
    AgentPreferences? Agent = null);

public sealed record EndpointSnapshot(string Endpoint, string Model, bool HasApiKey, string ApiKeyHint);

public sealed record EmbeddingSpecSnapshot(
    Guid Id,
    string Name,
    string Provider,
    string Model,
    int Dimensions,
    int MaxSequenceLength,
    string Revision,
    string? Endpoint,
    bool HasApiKey);

public sealed record EmbeddingIndexSnapshot(
    Guid KnowledgeBaseId,
    Guid IndexId,
    string? SpecName,
    string Status,
    int Progress,
    string? Stage);

public sealed record EmbeddingSettingsSnapshot(
    Guid? DefaultSpecId,
    string? DefaultSpecName,
    IReadOnlyList<EmbeddingSpecSnapshot> Specs,
    IReadOnlyList<EmbeddingIndexSnapshot> Indexes);

public sealed record SettingsSnapshot(
    bool UseCloudApi,
    EndpointSnapshot Local,
    EndpointSnapshot Online,
    UserPreferences Preferences,
    RuntimeSettings Runtime,
    EmbeddingSettingsSnapshot Embedding,
    AgentPreferences Agent)
{
    public static SettingsSnapshot Create(
        LlmOptions llm,
        UserPreferences preferences,
        RuntimeSettings runtime,
        EmbeddingSettingsSnapshot embedding,
        AgentPreferences? agent = null) => new(
        llm.UseCloudApi,
        ToSnapshot(llm.Local),
        ToSnapshot(llm.Online),
        preferences,
        runtime,
        embedding,
        agent ?? AgentPreferences.Default);

    private static EndpointSnapshot ToSnapshot(ModelEndpointOptions endpoint)
    {
        var hasKey = !string.IsNullOrWhiteSpace(endpoint.ApiKey);
        var hint = !hasKey ? string.Empty : endpoint.ApiKey.Length <= 4 ? "••••" : $"••••{endpoint.ApiKey[^4..]}";
        return new EndpointSnapshot(endpoint.Endpoint, endpoint.Model, hasKey, hint);
    }
}

/// <summary>设置应用服务：校验、合并密钥、持久化并刷新运行时模型配置。</summary>
public sealed class SettingsService(
    ISettingsRepository repository,
    ILlmSettingsAccessor llm,
    IRuntimeSettingsProvider runtime,
    IEmbeddingSpecStore embeddingSpecs,
    IKnowledgeIndexStore indexStore,
    IDocumentIndexSource documentSource,
    IndexLifecycleManager lifecycle,
    IOptions<EmbeddingOptions> embeddingOptions,
    AgentPreferenceProvider agentPreferences)
{
    public async Task<SettingsSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(cancellationToken);
        var preferences = stored?.Preferences ?? UserPreferences.Create(null, null);
        var agent = stored?.Agent ?? AgentPreferences.Default;
        var embedding = await BuildEmbeddingSnapshotAsync(stored?.Embedding?.DefaultSpecId, cancellationToken);
        return SettingsSnapshot.Create(llm.Current, preferences, runtime.Get(), embedding, agent);
    }

    /// <summary>读取用户级智能体开关；尚未保存过设置时返回默认值（全部关闭工具调用与联网）。</summary>
    public async Task<AgentPreferences> GetAgentPreferencesAsync(CancellationToken cancellationToken)
    {
        var stored = await repository.GetAsync(cancellationToken);
        return stored?.Agent ?? AgentPreferences.Default;
    }

    public async Task<SettingsSnapshot> SaveAsync(SaveSettingsCommand command, CancellationToken cancellationToken)
    {
        var current = llm.Current;
        var local = Merge(current.Local, command.Local);
        var online = Merge(current.Online, command.Online);

        if (!IsValidEndpoint(local))
            throw new SettingsValidationException("本地模型地址必须是有效的绝对 URL，并填写模型名称。");
        if (!IsValidEndpoint(online))
            throw new SettingsValidationException("在线模型地址必须是有效的绝对 URL，并填写模型名称。");

        var stored = await repository.GetAsync(cancellationToken);
        var preferences = UserPreferences.Create(
            command.DisplayName ?? stored?.Preferences.DisplayName,
            command.DailyGoalMinutes ?? stored?.Preferences.DailyGoalMinutes);

        var updatedLlm = new LlmOptions
        {
            UseCloudApi = command.UseCloudApi,
            Local = local,
            Online = online
        };
        var embeddingPreference = command.EmbeddingSpecId is Guid specId
            ? new EmbeddingPreference(specId)
            : stored?.Embedding;
        // 请求未携带智能体开关时保留旧值，避免旧版前端保存设置时把用户开关重置。
        var agent = command.Agent ?? stored?.Agent ?? AgentPreferences.Default;
        var updated = new StoredAppSettings(updatedLlm, preferences, embeddingPreference, agent);

        llm.Update(updatedLlm);
        await repository.SaveAsync(updated, cancellationToken);
        agentPreferences.Invalidate();

        if (command.EmbeddingSpecId is Guid targetSpecId)
            await ApplyEmbeddingSpecAsync(targetSpecId, cancellationToken);

        return await GetAsync(cancellationToken);
    }

    /// <summary>
    /// 切换向量模型：为每个尚未使用该规格的知识库创建阴影索引并排队重建。
    /// 活动索引在重建与校验期间继续服务，完成后由索引生命周期原子切换。
    /// </summary>
    private async Task ApplyEmbeddingSpecAsync(Guid specId, CancellationToken cancellationToken)
    {
        _ = await embeddingSpecs.GetAsync(specId, cancellationToken)
            ?? throw new EmbeddingSpecNotFoundException(specId);

        var knowledgeBaseIds = await documentSource.ListKnowledgeBaseIdsAsync(cancellationToken);
        foreach (var knowledgeBaseId in knowledgeBaseIds)
        {
            var active = await indexStore.GetActiveAsync(knowledgeBaseId, cancellationToken);
            if (active?.EmbeddingSpecId == specId) continue;

            await lifecycle.CreateIndexAsync(knowledgeBaseId, specId, name: null,
                activateWhenReady: true, cancellationToken);
        }
    }

    private async Task<EmbeddingSettingsSnapshot> BuildEmbeddingSnapshotAsync(
        Guid? preferredSpecId, CancellationToken cancellationToken)
    {
        var specs = await embeddingSpecs.ListAsync(cancellationToken);
        var defaultSpec = preferredSpecId is Guid id
            ? specs.FirstOrDefault(spec => spec.Id == id)
            : null;
        defaultSpec ??= (!string.IsNullOrWhiteSpace(embeddingOptions.Value.DefaultSpecName)
                ? specs.FirstOrDefault(spec => string.Equals(spec.Name, embeddingOptions.Value.DefaultSpecName,
                    StringComparison.OrdinalIgnoreCase))
                : null)
            ?? specs.FirstOrDefault();

        var specViews = specs.Select(spec => new EmbeddingSpecSnapshot(
            spec.Id, spec.Name, spec.Provider, spec.Model, spec.Dimensions,
            spec.MaxSequenceLength, spec.Revision, spec.Runtime.Endpoint,
            !string.IsNullOrWhiteSpace(spec.Runtime.ApiKey))).ToList();

        var indexes = new List<EmbeddingIndexSnapshot>();
        var knowledgeBaseIds = await documentSource.ListKnowledgeBaseIdsAsync(cancellationToken);
        foreach (var knowledgeBaseId in knowledgeBaseIds)
        {
            var list = await indexStore.ListAsync(knowledgeBaseId, cancellationToken);
            var display = list.FirstOrDefault(index => index.Status == KnowledgeIndexStatuses.Building)
                ?? list.FirstOrDefault(index => index.Status == KnowledgeIndexStatuses.Active);
            if (display is null) continue;

            indexes.Add(new EmbeddingIndexSnapshot(
                knowledgeBaseId,
                display.Id,
                specs.FirstOrDefault(spec => spec.Id == display.EmbeddingSpecId)?.Name,
                display.Status,
                display.BuildProgress,
                display.BuildStage));
        }

        return new EmbeddingSettingsSnapshot(defaultSpec?.Id, defaultSpec?.Name, specViews, indexes);
    }

    private static ModelEndpointOptions Merge(ModelEndpointOptions current, EndpointInput? input) => new()
    {
        Endpoint = input?.Endpoint?.Trim() ?? current.Endpoint,
        Model = input?.Model?.Trim() ?? current.Model,
        ApiKey = input?.ApiKey is null ? current.ApiKey : input.ApiKey.Trim()
    };

    private static bool IsValidEndpoint(ModelEndpointOptions endpoint) =>
        Uri.TryCreate(endpoint.Endpoint, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(endpoint.Model);
}

public sealed class SettingsValidationException(string message) : Exception(message);
