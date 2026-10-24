using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Application.Plugins;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Plugins;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 插件模块：manifest 加载、进程宿主、工具桥接与摄取下沉。
///
/// <para>
/// 插件是独立子进程，通过 JSON-RPC over stdio 与核心通信，
/// 不会把第三方程序集加载进核心进程。
/// </para>
/// </summary>
public static class PluginModule
{
    public static IServiceCollection AddPluginModule(this IServiceCollection services)
    {
        services.AddSingleton<PluginPaths>();
        services.AddSingleton<PluginManifestLoader>();

        // 受信任公钥：配置 + 数据库合并视图。签名校验在关键路径上是同步的，所以用快照。
        services.AddSingleton<IPluginTrustedKeyStore, SqlSugarPluginTrustedKeyStore>();
        services.AddSingleton<PluginTrustedKeyRegistry>();

        services.AddSingleton<PluginIntegrityVerifier>();
        services.AddSingleton<PluginInstaller>();
        services.AddSingleton<PluginAuditWriter>();
        services.AddSingleton<PluginManager>();
        services.AddSingleton<IPluginRuntime>(provider => provider.GetRequiredService<PluginManager>());
        // 摄取链路是 scoped 的：它最终要用到 KnowledgeIngestionService（内部走 Outbox + 索引写入，
        // 依赖请求作用域）。之前挂成 singleton 时 DI 校验会报
        // "Cannot consume scoped service ... from singleton"，只是生产环境默认不做作用域校验，
        // 于是变成一个"能跑但依赖根容器的 scoped 对象"的隐性 bug。
        services.AddScoped<IPluginContentStore, KnowledgePluginContentStore>();
        services.AddScoped<IPluginIngestionSink, KnowledgePluginIngestionSink>();

        // 同步用例：手动同步与定时同步共用这一份逻辑。
        services.AddScoped<PluginSyncService>();
        services.AddScoped<IJobHandler, PluginSyncJobHandler>();
        services.AddSingleton<IJobFeeder, PluginSyncJobFeeder>();

        // 与其他工具来源一致：按请求作用域创建，避免跨请求持有状态。
        services.AddScoped<IAgentToolSource, PluginToolSource>();

        return services;
    }

    /// <summary>
    /// 启动已启用的插件。单个插件失败只影响自身，不阻止核心启动。
    /// </summary>
    public static async Task InitializePluginsAsync(WebApplication app, CancellationToken cancellationToken)
    {
        var manager = app.Services.GetRequiredService<PluginManager>();

        // 插件安装态建表必须先于插件启动，否则读不到用户意图（启用/配置/授权）。
        try
        {
            await app.Services.GetRequiredService<IPluginRegistry>()
                .InitializeAsync(cancellationToken)
                .ConfigureAwait(false);

            // 受信任公钥同样要建表并加载：安装插件的第一件事就是验签。
            await app.Services.GetRequiredService<IPluginTrustedKeyStore>()
                .InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            app.Logger.LogError(exception, "Plugin registry initialisation failed; plugins will run with configuration defaults.");
        }

        try
        {
            await app.Services.GetRequiredService<PluginTrustedKeyRegistry>()
                .ReloadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            app.Logger.LogWarning(exception, "Failed to load trusted plugin keys; signature verification will rely on configuration only.");
        }

        // 停机时先停插件，再停 Worker 与数据库，保证插件不会在数据库关闭后继续调用。
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                manager.StopAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Failed to stop plugins during shutdown.");
            }
        });

        try
        {
            await manager.InitializeAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // 插件系统整体不可用也不应阻止核心 API 提供知识库服务。
            app.Logger.LogError(exception, "Plugin runtime initialization failed; continuing without plugins.");
        }
    }
}
