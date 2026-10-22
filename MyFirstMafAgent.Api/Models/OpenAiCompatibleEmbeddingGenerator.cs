using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MyFirstMafAgent.Api.Domain.Knowledge;

namespace MyFirstMafAgent.Api.Models;

/// <summary>通过 OpenAI 兼容 /embeddings 接口生成查询与索引向量。</summary>
public sealed class OpenAiCompatibleEmbeddingGenerator : IEmbeddingGenerator
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingSpec _spec;

    public OpenAiCompatibleEmbeddingGenerator(HttpClient httpClient, EmbeddingSpec spec)
    {
        _httpClient = httpClient;
        _spec = spec;
    }

    public async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Embedding input cannot be empty.", nameof(text));

        using var request = CreateRequest();
        request.Content = JsonContent.Create(new { model = _spec.Model, input = text });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 500) body = body[..500];
            throw new InvalidOperationException($"Embedding API returned {(int)response.StatusCode}: {body}");
        }

        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var embedding = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
            .EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (embedding.Length != _spec.Dimensions)
            throw new InvalidDataException(
                $"Embedding API returned {embedding.Length} dimensions but the spec requires {_spec.Dimensions}.");
        return embedding;
    }

    private HttpRequestMessage CreateRequest()
    {
        var endpoint = _spec.Runtime.Endpoint
            ?? throw new InvalidOperationException($"Embedding spec '{_spec.Name}' has no endpoint configured.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"Embedding endpoint '{endpoint}' is invalid.");

        var path = baseUri.AbsolutePath.TrimEnd('/');
        var uri = path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/embeddings");

        var request = new HttpRequestMessage(HttpMethod.Post, uri);
        var key = string.IsNullOrWhiteSpace(_spec.Runtime.ApiKey) ? "local" : _spec.Runtime.ApiKey;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
