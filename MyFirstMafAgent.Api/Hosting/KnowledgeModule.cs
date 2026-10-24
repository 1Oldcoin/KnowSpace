using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.KnowledgePackages;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Infrastructure.Embedding;
using MyFirstMafAgent.Api.Models;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 知识模块：向量规格、索引生命周期、检索路由、摘要、摄取与检索应用服务。
/// 统一知识数据面——REST、MCP 和未来插件都复用这里的应用服务。
/// </summary>
public static class KnowledgeModule
{
    public static IServiceCollection AddKnowledgeModule(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingGeneratorProvider, EmbeddingModelRegistry>();
        services.AddSingleton<EmbeddingSpecService>();
        // 重排是无状态纯计算；换神经重排只需替换这一行注册。
        services.AddSingleton<IChunkReranker, CoverageReranker>();
        services.AddSingleton<RetrievalRouter>();
        services.AddSingleton<IndexLifecycleManager>();
        services.AddScoped<ISummaryModelClient, SummaryModelClient>();

        services.AddScoped<KnowledgeIngestionService>();
        services.AddScoped<SummaryService>();
        services.AddScoped<KnowledgeSearchService>();
        services.AddScoped<DocumentContentService>();

        // 知识库包：导出是纯读，导入会写文件与数据库，按请求生命周期注册。
        services.AddScoped<KnowledgePackageExporter>();
        services.AddScoped<KnowledgePackageImporter>();

        return services;
    }
}
