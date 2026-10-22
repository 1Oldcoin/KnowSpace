using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Models;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Infrastructure.Embedding;

/// <summary>
/// 按向量规格缓存生成器。查询与索引构建使用同一个注册表，确保「同一指纹 = 同一模型实例」。
/// 模型切换只需要创建新的规格和阴影索引，不影响正在服务的活动索引。
/// </summary>
public sealed class EmbeddingModelRegistry(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<EmbeddingModelRegistry> logger) : IEmbeddingGeneratorProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, IEmbeddingGenerator> generators = new(StringComparer.Ordinal);

    public IEmbeddingGenerator GetOrCreate(EmbeddingSpec spec)
    {
        return generators.GetOrAdd(spec.Fingerprint, _ => Create(spec));
    }

    private IEmbeddingGenerator Create(EmbeddingSpec spec)
    {
        logger.LogInformation("Creating embedding generator for spec {SpecName} ({Provider}/{Model}, {Dimensions}d, fingerprint {Fingerprint})",
            spec.Name, spec.Provider, spec.Model, spec.Dimensions, spec.Fingerprint);

        return spec.Provider switch
        {
            EmbeddingProviders.LocalOnnx => new LocalOnnxEmbeddingService(new LocalOnnxEmbeddingSettings(
                spec.Runtime.ModelDirectory ?? embeddingOptions.Value.ModelDirectory,
                spec.Dimensions,
                spec.MaxSequenceLength,
                spec.Runtime.IntraOpNumThreads > 0
                    ? spec.Runtime.IntraOpNumThreads
                    : embeddingOptions.Value.IntraOpNumThreads)),
            EmbeddingProviders.OpenAiCompatible => new OpenAiCompatibleEmbeddingGenerator(
                httpClientFactory.CreateClient("embedding"), spec),
            _ => throw new NotSupportedException($"Unsupported embedding provider '{spec.Provider}'.")
        };
    }

    public void Dispose()
    {
        foreach (var generator in generators.Values)
        {
            if (generator is IDisposable disposable) disposable.Dispose();
        }
        generators.Clear();
    }
}
