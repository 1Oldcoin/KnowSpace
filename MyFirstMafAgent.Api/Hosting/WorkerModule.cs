using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 后台 Worker：索引生命周期、分身摄取。
/// 资料解析已迁移到任务中心（<c>JobsModule</c> 的 <c>JobScheduler</c> + <c>document.ingest</c>）。
/// 每个 Worker 通过 <c>BackgroundLoop.RunIsolatedAsync</c> 获得独立 ExecutionContext，
/// 这是 SqlSugar AsyncLocal 连接不被多个 Worker 共享的前提，不可移除。
/// </summary>
public static class WorkerModule
{
    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services)
    {
        // 资料解析不再由专用 Worker 跑：它已经是任务中心里的 document.ingest 任务，
        // 并发度、重试、取消、进度与观测统一由调度器负责（见 JobsModule）。
        services.AddHostedService<IndexLifecycleWorker>();
        services.AddHostedService<AvatarIngestionWorker>();
        // 词法检索的预分词回填：只为历史切片补 kb_chunks.lexical_tokens，正常空转。
        services.AddHostedService<LexicalIndexWorker>();
        return services;
    }
}
