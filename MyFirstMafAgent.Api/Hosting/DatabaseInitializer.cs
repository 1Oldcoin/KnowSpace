using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.Settings;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

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
            // 每一次 store 初始化都是一轮「查 catalog → 缺什么建什么」，往返次数不少。
            // 打点是为了在启动变慢时能直接指到具体是哪个 store，而不是笼统地说"数据库慢"。
            async Task Step(string name, Func<Task> action)
            {
                var since = StartupTimeline.Mark($"  schema: {name}");
                await action();
                StartupTimeline.Step($"  schema: {name}", since);
            }

            StoredAppSettings? savedSettings = null;

            // 结构指纹：与上次初始化成功后记下的值一致，就说明所有表/列/索引都已经是最新，
            // 可以跳过全部建表检查（实测占启动耗时 ~2.8s / 3.1s）。
            var database = app.Services.GetRequiredService<SqlSugarScope>();
            var stamp = SchemaStamp.Current;
            var storedStamp = SchemaStamp.ForceFullInitialization
                ? null
                : await SchemaStamp.TryReadAsync(database, cancellationToken);
            var schemaUpToDate = string.Equals(storedStamp, stamp, StringComparison.Ordinal);

            StartupTimeline.Mark(schemaUpToDate
                ? "schema stamp matches; skipping DDL checks"
                : "schema stamp missing or changed; running full DDL initialization");

            var knowledgeBaseStore = app.Services.GetRequiredService<KnowledgeBaseStore>();
            await Step("kb store", () => knowledgeBaseStore.InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("kb recover documents", () => knowledgeBaseStore.RecoverProcessingDocumentsAsync(cancellationToken));
            await Step("kb chat store", () => app.Services.GetRequiredService<KnowledgeChatStore>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("companion store", () => app.Services.GetRequiredService<CompanionStore>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("topic repository", () => app.Services.GetRequiredService<SqlSugarTopicRepository>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("note repository", () => app.Services.GetRequiredService<SqlSugarNoteRepository>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("study repository", () => app.Services.GetRequiredService<SqlSugarStudyRepository>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));
            await Step("avatar repository", () => app.Services.GetRequiredService<SqlSugarAvatarRepository>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));

            // 外部智能体客户端与 MCP 调用审计。
            await Step("agent client store", () => app.Services.GetRequiredService<SqlSugarAgentClientStore>().InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate));

            // 长任务表：必须在使用之前建好，否则调度器第一轮会报表不存在。
            await Step("job queue", () =>
            {
                var queue = app.Services.GetRequiredService<IJobQueue>();
                return queue is SqlSugarJobQueue sqlQueue
                    ? sqlQueue.InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate)
                    : queue.InitializeAsync(cancellationToken);
            });

            // 用户在设置页面保存的模型配置优先于 appsettings.json。
            var settingsRepository = app.Services.GetRequiredService<SqlSugarSettingsRepository>();
            await Step("settings", async () =>
            {
                await settingsRepository.InitializeAsync(cancellationToken, ensureSchema: !schemaUpToDate);
                var saved = await settingsRepository.GetAsync(cancellationToken);
                if (saved?.Llm is not null)
                    app.Services.GetRequiredService<ILlmSettingsAccessor>().Update(saved.Llm);
                savedSettings = saved;
            });

            // 注册配置中的向量规格，回填历史版本，并为每个知识库准备默认阴影索引。
            // 设置页保存的默认向量模型优先于 appsettings.json 的 Embedding:DefaultSpecName。
            await Step("embedding specs", () => app.Services.GetRequiredService<EmbeddingSpecService>().EnsureConfiguredAsync(cancellationToken));
            await Step("default indexes", () => app.Services.GetRequiredService<IndexLifecycleManager>()
                .EnsureDefaultIndexesAsync(savedSettings?.Embedding?.DefaultSpecId, cancellationToken));

            // 指纹只在**完整初始化成功之后**才写：中途抛错时库里不会有"已经最新"的假凭证。
            if (!schemaUpToDate)
                await Step("schema stamp", () => SchemaStamp.SaveAsync(database, stamp, cancellationToken));

            app.Logger.LogInformation("Database schema initialization completed");
        }
        catch (Exception exception)
        {
            app.Logger.LogCritical(exception, "Database schema initialization failed");
            throw;
        }
    }
}
