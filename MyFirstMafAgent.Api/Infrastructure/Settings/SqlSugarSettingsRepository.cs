using System.Text.Json;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Domain.Settings;
using MyFirstMafAgent.Api.Options;
using SqlSugar;

namespace MyFirstMafAgent.Api.Infrastructure.Settings;

[SugarTable("app_settings")]
public sealed class AppSettingSchemaEntity
{
    [SugarColumn(ColumnName = "setting_key", ColumnDataType = "text", IsPrimaryKey = true, IsNullable = false)]
    public string Key { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "setting_value", ColumnDataType = "text", IsNullable = false)]
    public string Value { get; set; } = string.Empty;

    [SugarColumn(ColumnName = "updated_at", ColumnDataType = "timestamptz", IsNullable = false, DefaultValue = "now()")]
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SqlSugarSettingsRepository(SqlSugarScope database) : ISettingsRepository
{
    private const string SettingsKey = "app.settings";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync(CancellationToken cancellationToken, bool ensureSchema = true)
    {
        if (!ensureSchema) return Task.CompletedTask;

        database.CodeFirst.InitTables<AppSettingSchemaEntity>();
        return Task.CompletedTask;
    }

    public async Task<StoredAppSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await database.Queryable<AppSettingSchemaEntity>()
            .Where(item => item.Key == SettingsKey)
            .FirstAsync(cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return null;

        try
        {
            var settings = JsonSerializer.Deserialize<StoredAppSettings>(row.Value, JsonOptions);
            if (settings is null) return null;

            var llm = settings.Llm ?? new LlmOptions();
            llm.Local ??= new ModelEndpointOptions();
            llm.Online ??= new ModelEndpointOptions();
            var preferences = settings.Preferences ?? UserPreferences.Create(null, null);
            // 旧版设置 JSON 没有 Agent 字段，兜底为默认偏好（不调用工具、不联网）。
            var agent = settings.Agent ?? AgentPreferences.Default;
            return new StoredAppSettings(llm, preferences, settings.Embedding, agent);
        }
        catch (JsonException)
        {
            // 设置内容损坏时回退到 appsettings.json 的初始配置。
            return null;
        }
    }

    public async Task SaveAsync(StoredAppSettings settings, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var exists = await database.Queryable<AppSettingSchemaEntity>()
            .AnyAsync(item => item.Key == SettingsKey, cancellationToken);

        if (!exists)
        {
            await database.Insertable(new AppSettingSchemaEntity
            {
                Key = SettingsKey,
                Value = json,
                UpdatedAt = DateTimeOffset.UtcNow
            }).ExecuteCommandAsync(cancellationToken);
            return;
        }

        await database.Updateable<AppSettingSchemaEntity>()
            .SetColumns(item => new AppSettingSchemaEntity
            {
                Value = json,
                UpdatedAt = SqlFunc.GetDate()
            })
            .Where(item => item.Key == SettingsKey)
            .ExecuteCommandAsync(cancellationToken);
    }
}

/// <summary>
/// 保存当前生效的聊天模型配置。启动时来自 appsettings，之后可被设置页覆盖；
/// 模型客户端每次调用都会读取最新值，无需重启 API。
/// </summary>
public sealed class LlmSettingsProvider : ILlmSettingsAccessor
{
    private volatile LlmOptions _current;

    public LlmSettingsProvider(IOptions<LlmOptions> options) => _current = Clone(options.Value);

    public LlmOptions Current => _current;

    public ModelEndpointOptions Selected => Current.UseCloudApi ? Current.Online : Current.Local;

    public void Update(LlmOptions settings) => _current = Clone(settings);

    private static LlmOptions Clone(LlmOptions source) => new()
    {
        UseCloudApi = source.UseCloudApi,
        Local = CloneEndpoint(source.Local),
        Online = CloneEndpoint(source.Online)
    };

    private static ModelEndpointOptions CloneEndpoint(ModelEndpointOptions? source) => new()
    {
        Endpoint = source?.Endpoint ?? string.Empty,
        Model = source?.Model ?? string.Empty,
        ApiKey = source?.ApiKey ?? string.Empty
    };
}

public sealed class RuntimeSettingsProvider(
    IOptions<EmbeddingOptions> embedding,
    IOptions<StorageOptions> storage) : IRuntimeSettingsProvider
{
    public RuntimeSettings Get() => new(
        embedding.Value.ModelDirectory,
        embedding.Value.Dimensions,
        storage.Value.RootPath,
        storage.Value.MaxFileSizeBytes,
        storage.Value.MaxVideoFileSizeBytes);
}
