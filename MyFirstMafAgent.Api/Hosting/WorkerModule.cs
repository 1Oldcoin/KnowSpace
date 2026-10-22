using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 后台 Worker：资料解析、索引生命周期、分身摄取。
/// 每个 Worker 通过 <c>BackgroundLoop.RunIsolatedAsync</c> 获得独立 ExecutionContext，
/// 这是 SqlSugar AsyncLocal 连接不被多个 Worker 共享的前提，不可移除。
/// </summary>
public static class WorkerModule
{
    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services)
    {
        services.AddHostedService<DocumentProcessingWorker>();
        services.AddHostedService<IndexLifecycleWorker>();
        services.AddHostedService<AvatarIngestionWorker>();
        return services;
    }
}
