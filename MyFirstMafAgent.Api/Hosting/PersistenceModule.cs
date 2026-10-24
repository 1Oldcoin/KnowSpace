using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Study;
using MyFirstMafAgent.Api.Domain.Topics;
using MyFirstMafAgent.Api.Application.Agents;
using MyFirstMafAgent.Api.Application.Health;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.Settings;
using MyFirstMafAgent.Api.Persistence;
using SqlSugar;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 持久化模块：SqlSugar 客户端与所有仓储实现。
/// 端口（Domain/Application 接口）在这里绑定到 Infrastructure 实现，端点只注入应用服务。
/// </summary>
public static class PersistenceModule
{
    public static IServiceCollection AddSqlSugarPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        }));

        // 旧结构（Documents/Chats/Companions）仍使用这两个聚合根存储。
        services.AddSingleton<KnowledgeBaseStore>();
        services.AddSingleton<IDocumentIndexSource>(sp => sp.GetRequiredService<KnowledgeBaseStore>());
        services.AddSingleton<KnowledgeChatStore>();
        services.AddSingleton<CompanionStore>();

        // 向量规格、索引生命周期、事务性 Outbox 与摘要缓存。
        services.AddSingleton<SqlSugarEmbeddingSpecStore>();
        services.AddSingleton<IEmbeddingSpecStore>(sp => sp.GetRequiredService<SqlSugarEmbeddingSpecStore>());
        services.AddSingleton<IModelBindingStore>(sp => sp.GetRequiredService<SqlSugarEmbeddingSpecStore>());
        services.AddSingleton<SqlSugarIndexStore>();
        services.AddSingleton<IKnowledgeIndexStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        services.AddSingleton<IIndexVectorStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        services.AddSingleton<SqlSugarSummaryStore>();
        services.AddSingleton<ISummaryStore>(sp => sp.GetRequiredService<SqlSugarSummaryStore>());

        // DDD 分层：领域仓储端口 → 基础设施实现。
        services.AddSingleton<SqlSugarNoteRepository>();
        services.AddSingleton<INoteRepository>(sp => sp.GetRequiredService<SqlSugarNoteRepository>());
        services.AddSingleton<SqlSugarTopicRepository>();
        services.AddSingleton<ITopicRepository>(sp => sp.GetRequiredService<SqlSugarTopicRepository>());
        services.AddSingleton<SqlSugarStudyRepository>();
        services.AddSingleton<IStudyRepository>(sp => sp.GetRequiredService<SqlSugarStudyRepository>());
        services.AddSingleton<SqlSugarAvatarRepository>();
        services.AddSingleton<IAvatarRepository>(sp => sp.GetRequiredService<SqlSugarAvatarRepository>());

        services.AddSingleton<SqlSugarSettingsRepository>();
        services.AddSingleton<ISettingsRepository>(sp => sp.GetRequiredService<SqlSugarSettingsRepository>());
        services.AddSingleton<SqlSugarKnowledgeSearchRepository>();
        services.AddSingleton<IDocumentSearchRepository>(sp => sp.GetRequiredService<SqlSugarKnowledgeSearchRepository>());
        services.AddSingleton<SqlSugarDocumentTextRepository>();
        services.AddSingleton<IDocumentTextRepository>(sp => sp.GetRequiredService<SqlSugarDocumentTextRepository>());
        services.AddSingleton<IKnowledgeBaseDirectory, SqlSugarKnowledgeBaseDirectory>();
        services.AddSingleton<IDatabaseHealthProbe, SqlSugarDatabaseHealthProbe>();

        // 插件安装态（启用/配置/授权/范围）与调用审计。
        services.AddSingleton<SqlSugarPluginRegistry>();
        services.AddSingleton<IPluginRegistry>(sp => sp.GetRequiredService<SqlSugarPluginRegistry>());
        services.AddSingleton<IPluginItemMap>(sp => sp.GetRequiredService<SqlSugarPluginRegistry>());
        services.AddSingleton<IPluginSyncStateStore>(sp => sp.GetRequiredService<SqlSugarPluginRegistry>());

        // 外部智能体客户端（MCP 接入方）与调用审计。
        services.AddSingleton<SqlSugarAgentClientStore>();
        services.AddSingleton<IAgentClientStore>(sp => sp.GetRequiredService<SqlSugarAgentClientStore>());
        services.AddSingleton<IAgentCallLogStore>(sp => sp.GetRequiredService<SqlSugarAgentClientStore>());

        // 长任务队列（落库 + 租约，重启不丢任务）。
        services.AddSingleton<SqlSugarJobQueue>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<SqlSugarJobQueue>());

        return services;
    }
}
