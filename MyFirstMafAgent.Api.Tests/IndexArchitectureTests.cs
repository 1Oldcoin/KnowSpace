using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Domain.Knowledge;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

public sealed class EmbeddingSpecFingerprintTests
{
    [Fact]
    public void Fingerprint_is_deterministic_and_provider_case_insensitive()
    {
        var first = EmbeddingSpec.CreateFingerprint("ONNX-Local", "bge-small", 512, 512, "1");
        var second = EmbeddingSpec.CreateFingerprint("onnx-local", "bge-small", 512, 512, "1");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Fingerprint_changes_when_dimensions_or_revision_change()
    {
        var baseline = EmbeddingSpec.CreateFingerprint("onnx-local", "bge-small", 512, 512, "1");

        Assert.NotEqual(baseline, EmbeddingSpec.CreateFingerprint("onnx-local", "bge-small", 768, 512, "1"));
        Assert.NotEqual(baseline, EmbeddingSpec.CreateFingerprint("onnx-local", "bge-small", 512, 512, "2"));
    }
}

public sealed class SummaryServiceTests
{
    [Fact]
    public async Task Generate_uses_cached_summary_until_forced()
    {
        var documentId = Guid.NewGuid();
        var store = new InMemorySummaryStore();
        var source = new StubDocumentIndexSource(documentId, "hash-1");
        var client = new CountingSummaryModelClient();
        var service = new SummaryService(store, source, client, NullLogger<SummaryService>.Instance);

        var first = await service.GenerateAsync(documentId, force: false, CancellationToken.None);
        var second = await service.GenerateAsync(documentId, force: false, CancellationToken.None);

        Assert.Equal(1, client.Calls);
        Assert.Equal(first.Id, second.Id);

        var forced = await service.GenerateAsync(documentId, force: true, CancellationToken.None);

        Assert.NotEqual(second.Id, forced.Id);
        Assert.Equal(2, client.Calls);
        Assert.Equal("summary result", first.Summary);
    }

    [Fact]
    public async Task Generate_creates_new_summary_when_content_hash_changes()
    {
        var documentId = Guid.NewGuid();
        var store = new InMemorySummaryStore();
        var source = new StubDocumentIndexSource(documentId, "hash-1");
        var client = new CountingSummaryModelClient();
        var service = new SummaryService(store, source, client, NullLogger<SummaryService>.Instance);

        await service.GenerateAsync(documentId, force: false, CancellationToken.None);
        source.ContentHash = "hash-2";
        await service.GenerateAsync(documentId, force: false, CancellationToken.None);

        Assert.Equal(2, client.Calls);
    }

    private sealed class CountingSummaryModelClient : ISummaryModelClient
    {
        public int Calls { get; private set; }

        public Task<string> GetModelFingerprintAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult("model-fingerprint");

        public Task<string> CompleteAsync(Guid knowledgeBaseId, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult("summary result");
        }
    }

    private sealed class InMemorySummaryStore : ISummaryStore
    {
        private readonly List<SummaryRecord> _records = [];

        public Task<SummaryRecord?> GetLatestAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult(_records.LastOrDefault(record => record.DocumentId == documentId));

        public Task<SummaryRecord?> FindAsync(Guid documentId, string contentHash, string modelFingerprint, CancellationToken cancellationToken) =>
            Task.FromResult(_records.LastOrDefault(record => record.DocumentId == documentId
                && record.ContentHash == contentHash
                && record.ModelFingerprint == modelFingerprint));

        public Task SaveAsync(SummaryRecord record, CancellationToken cancellationToken)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }

        public Task SetDisplaySummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubDocumentIndexSource(Guid documentId, string contentHash) : IDocumentIndexSource
    {
        public string ContentHash { get; set; } = contentHash;

        public Task<int> BackfillLegacyVersionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<IReadOnlyList<Guid>> ListKnowledgeBaseIdsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([Guid.NewGuid()]);

        public Task<IReadOnlyList<IndexSourceDocument>> ListPublishedDocumentsAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexSourceDocument>>([Document]);

        public Task<IndexSourceDocument?> GetPublishedDocumentAsync(Guid requestedDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<IndexSourceDocument?>(requestedDocumentId == documentId ? Document : null);

        public Task<IReadOnlyList<IndexSourceChunk>> GetPublishedChunksAsync(Guid requestedDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IndexSourceChunk>>(
                [new IndexSourceChunk(1, documentId, 1, 0, "chunk content", null)]);

        public Task<int> CountPublishedChunksAsync(Guid requestedDocumentId, CancellationToken cancellationToken) =>
            Task.FromResult(1);

        private IndexSourceDocument Document => new(documentId, Guid.Empty, "test.txt", ".txt", 1, ContentHash, "Ready");
    }
}
