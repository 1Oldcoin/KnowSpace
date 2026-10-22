namespace MyFirstMafAgent.Api.Domain.Knowledge;

/// <summary>文本向量化端口，由基础设施层的 ONNX 实现。</summary>
public interface IEmbeddingGenerator
{
    Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken);
}
