namespace MyFirstMafAgent.Api.Contracts;

public sealed record CreateEmbeddingSpecRequest(
    string? Name,
    string? Provider,
    string? Model,
    int? Dimensions,
    int? MaxSequenceLength,
    string? Revision,
    string? ModelDirectory,
    string? Endpoint,
    string? ApiKey,
    int? IntraOpNumThreads);

public sealed record CreateKnowledgeIndexRequest(
    Guid? EmbeddingSpecId,
    string? Name,
    bool ActivateWhenReady = false);

public sealed record RegisterCloudEmbeddingSpecRequest(
    string? Name,
    string? Endpoint,
    string? Model,
    string? ApiKey,
    int? Dimensions,
    int? MaxSequenceLength,
    string? Revision);

public sealed record EmbeddingProbeResponse(int Dimensions, long ElapsedMilliseconds);

public sealed record SummaryModelInput(
    bool? UseCloudApi,
    string? LocalEndpoint,
    string? LocalModel,
    string? LocalApiKey,
    string? OnlineEndpoint,
    string? OnlineModel,
    string? OnlineApiKey);

public sealed record UpdateModelBindingRequest(
    Guid? EmbeddingSpecId,
    bool ClearEmbeddingSpec = false,
    bool ActivateWhenReady = true,
    SummaryModelInput? Summary = null);

public sealed record EmbeddingSpecView(
    Guid Id,
    string Name,
    string Provider,
    string Model,
    int Dimensions,
    int MaxSequenceLength,
    string Revision,
    string Fingerprint,
    string? Endpoint,
    bool HasApiKey,
    DateTimeOffset CreatedAt);

public sealed record SummaryModelView(
    bool UseCloudApi,
    string? LocalEndpoint,
    string? LocalModel,
    bool LocalHasApiKey,
    string? OnlineEndpoint,
    string? OnlineModel,
    bool OnlineHasApiKey);

public sealed record ModelBindingView(
    Guid KnowledgeBaseId,
    Guid? EmbeddingSpecId,
    string? EmbeddingSpecName,
    SummaryModelView? Summary);
