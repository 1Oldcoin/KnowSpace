using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.Settings;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 启动期数据库初始化：建表/补列、恢复中断资料、加载保存的设置、注册向量规格并准备默认索引。
/// 这些步骤必须按顺序执行，且在任何端点接受请求之前完成。
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(WebApplication app, CancellationToken cancellationToken = default)
    {
        app.Logger.LogInformation("Initializing database schema");
        try
        {
            await app.Services.GetRequiredService<KnowledgeBaseStore>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<KnowledgeBaseStore>().RecoverProcessingDocumentsAsync(cancellationToken);
            await app.Services.GetRequiredService<KnowledgeChatStore>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<CompanionStore>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<SqlSugarTopicRepository>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<SqlSugarNoteRepository>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<SqlSugarStudyRepository>().InitializeAsync(cancellationToken);
            await app.Services.GetRequiredService<SqlSugarAvatarRepository>().InitializeAsync(cancellationToken);

            // 用户在设置页面保存的模型配置优先于 appsettings.json。
            var settingsRepository = app.Services.GetRequiredService<SqlSugarSettingsRepository>();
            await settingsRepository.InitializeAsync(cancellationToken);
            var savedSettings = await settingsRepository.GetAsync(cancellationToken);
            if (savedSettings?.Llm is not null)
                app.Services.GetRequiredService<ILlmSettingsAccessor>().Update(savedSettings.Llm);

            // 注册配置中的向量规格，回填历史版本，并为每个知识库准备默认阴影索引。
            // 设置页保存的默认向量模型优先于 appsettings.json 的 Embedding:DefaultSpecName。
            await app.Services.GetRequiredService<EmbeddingSpecService>().EnsureConfiguredAsync(cancellationToken);
            await app.Services.GetRequiredService<IndexLifecycleManager>()
                .EnsureDefaultIndexesAsync(savedSettings?.Embedding?.DefaultSpecId, cancellationToken);

            app.Logger.LogInformation("Database schema initialization completed");
        }
        catch (Exception exception)
        {
            app.Logger.LogCritical(exception, "Database schema initialization failed");
            throw;
        }
    }
}
