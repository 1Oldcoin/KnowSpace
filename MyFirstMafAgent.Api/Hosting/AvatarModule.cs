using MyFirstMafAgent.Api.Application.Avatar;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 分身模块：画像、记忆、投喂摄取、证据链、候选人格、反馈与评测。
/// 分身数据与知识库（kb_*）完全隔离，不参与知识检索。
/// </summary>
public static class AvatarModule
{
    public static IServiceCollection AddAvatarModule(this IServiceCollection services)
    {
        services.AddScoped<AvatarActivityRecorder>();
        services.AddScoped<AvatarService>();
        services.AddScoped<AvatarDigestService>();
        services.AddScoped<AvatarPersonaChatService>();
        services.AddScoped<AvatarSimulationService>();

        // 自由投喂：AI 整理 → 记忆卡 → 向量化。
        services.AddScoped<AvatarOrganizer>();
        services.AddScoped<AvatarMemoryIndexer>();
        services.AddScoped<AvatarMemoryRetriever>();
        services.AddScoped<AvatarIntakeService>();

        // v2：异步投喂、证据链、人格候选、反馈与评测。
        services.AddScoped<AvatarIngestionService>();
        services.AddScoped<AvatarInsightService>();
        services.AddScoped<AvatarFeedbackService>();
        services.AddScoped<AvatarEvalService>();

        return services;
    }
}
