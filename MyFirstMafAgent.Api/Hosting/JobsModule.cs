using MyFirstMafAgent.Api.Application.Jobs;
using MyFirstMafAgent.Api.Documents;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 任务模块：队列、调度器、处理器与「补投器」的装配。
///
/// <para>
/// 处理器一律 <b>scoped</b>：每个任务在自己的 DI 作用域里执行，
/// 不共享任何跨任务状态；调度器本身是单例，只负责派活。
/// </para>
/// </summary>
public static class JobsModule
{
    public static IServiceCollection AddJobsModule(this IServiceCollection services)
    {
        services.AddSingleton<JobEventStream>();
        services.AddSingleton<JobWakeSignal>();
        services.AddSingleton<JobHandlerRegistry>();
        services.AddSingleton<JobTypeQuota>();

        // 处理器：scoped，每个任务一个作用域。
        services.AddScoped<IJobHandler, DocumentIngestJobHandler>();

        // 补投器：依赖全部是单例（仓储 + 队列），所以注册为单例，
        // 避免被惰性灌进单例调度器时触发「单例不能消费 scoped」的校验。
        services.AddSingleton<IJobFeeder, DocumentIngestJobFeeder>();

        services.AddHostedService<JobScheduler>();
        return services;
    }
}
