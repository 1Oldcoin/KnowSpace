using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;

namespace MyFirstMafAgent.Api.Documents;

public sealed class ResumableUploadService(
    LocalDocumentStore documentStore,
    KnowledgeBaseStore knowledgeBase,
    IOptions<StorageOptions> storageOptions)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> sessionLocks = new();

    public int ChunkSizeBytes => storageOptions.Value.UploadChunkSizeBytes;

    public async Task<UploadSession> StartAsync(
        Guid knowledgeBaseId,
        string fileName,
        string? contentType,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        if (await knowledgeBase.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken) is null)
            throw new UploadKnowledgeBaseNotFoundException();

        var sessionId = Guid.NewGuid();
        var session = documentStore.CreateUploadSession(sessionId, knowledgeBaseId, fileName, contentType ?? "", sizeBytes);
        try
        {
            await knowledgeBase.CreateUploadSessionAsync(session, cancellationToken);
            return session;
        }
        catch
        {
            documentStore.DeleteUploadStagingFile(sessionId);
            throw;
        }
    }

    public Task<UploadSession?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        knowledgeBase.GetUploadSessionAsync(id, cancellationToken);

    public async Task<long> AppendChunkAsync(Guid id, long offset, Stream content, CancellationToken cancellationToken)
    {
        var sessionLock = sessionLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var session = await knowledgeBase.GetUploadSessionAsync(id, cancellationToken)
                ?? throw new UploadSessionNotFoundException();
            if (!string.Equals(session.Status, "Uploading", StringComparison.Ordinal))
                throw new InvalidOperationException("The upload session is no longer accepting chunks.");
            if (offset != session.UploadedBytes)
                throw new UploadOffsetConflictException(session.UploadedBytes);

            var uploadedBytes = await documentStore.AppendUploadChunkAsync(session, offset, content, cancellationToken);
            await knowledgeBase.UpdateUploadSessionProgressAsync(id, uploadedBytes, cancellationToken);
            return uploadedBytes;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public async Task<DocumentUploadResponse?> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var sessionLock = sessionLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken);
        try
        {
            var session = await knowledgeBase.GetUploadSessionAsync(id, cancellationToken);
            if (session is null) return null;

            if (string.Equals(session.Status, "Completed", StringComparison.Ordinal))
            {
                var completedDocument = await knowledgeBase.GetDocumentAsync(session.DocumentId ?? session.Id, cancellationToken)
                    ?? throw new InvalidOperationException("The completed upload has no document record.");
                return ToUploadResponse(completedDocument);
            }
            if (!string.Equals(session.Status, "Uploading", StringComparison.Ordinal))
                throw new InvalidOperationException("The upload session cannot be completed in its current state.");
            if (session.UploadedBytes != session.SizeBytes)
                throw new UploadIncompleteException(session.UploadedBytes, session.SizeBytes);

            var stored = documentStore.CompleteUpload(session);
            var document = new KnowledgeDocument(stored.Id, session.KnowledgeBaseId, stored.FileName, stored.Extension,
                stored.ContentType, stored.SizeBytes, stored.Path, "Queued");
            await knowledgeBase.CompleteUploadSessionAsync(session.Id, document, cancellationToken);

            var queuedDocument = await knowledgeBase.GetDocumentAsync(session.Id, cancellationToken)
                ?? throw new InvalidOperationException("The uploaded file was archived without a document record.");
            return ToUploadResponse(queuedDocument);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private static DocumentUploadResponse ToUploadResponse(DocumentListItem document) =>
        new(document.Id.ToString(), document.FileName, document.Extension, document.SizeBytes, document.Status, document.Error);
}

public sealed class UploadKnowledgeBaseNotFoundException : Exception { }

public sealed class UploadSessionNotFoundException : Exception { }

public sealed class UploadOffsetConflictException(long expectedOffset) : Exception
{
    public long ExpectedOffset { get; } = expectedOffset;
}

public sealed class UploadIncompleteException(long uploadedBytes, long expectedBytes) : Exception
{
    public long UploadedBytes { get; } = uploadedBytes;
    public long ExpectedBytes { get; } = expectedBytes;
}
