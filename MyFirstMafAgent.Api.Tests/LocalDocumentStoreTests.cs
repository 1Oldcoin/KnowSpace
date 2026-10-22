using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

public sealed class LocalDocumentStoreTests
{
    [Fact]
    public async Task SaveAsync_SavesAllowedFileWithSafeGeneratedPath()
    {
        var contentRoot = CreateTemporaryDirectory();
        var content = Encoding.UTF8.GetBytes("test document");
        await using var input = new MemoryStream(content);
        var file = CreateFile(input, "../notes.txt");

        try
        {
            var store = CreateStore(contentRoot, 1024);
            var stored = await store.SaveAsync(file, CancellationToken.None);

            Assert.Equal("notes.txt", stored.FileName);
            Assert.Equal(".txt", stored.Extension);
            Assert.Equal(content.Length, stored.SizeBytes);
            Assert.StartsWith(Path.Combine(contentRoot, "uploads"), stored.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(content, await File.ReadAllBytesAsync(stored.Path));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_StoresFilesInSeparateKnowledgeBaseDirectories()
    {
        var contentRoot = CreateTemporaryDirectory();
        var firstKnowledgeBaseId = Guid.NewGuid();
        var secondKnowledgeBaseId = Guid.NewGuid();

        try
        {
            var store = CreateStore(contentRoot, 1024);
            using var firstInput = new MemoryStream(Encoding.UTF8.GetBytes("first"));
            using var secondInput = new MemoryStream(Encoding.UTF8.GetBytes("second"));

            var first = await store.SaveAsync(CreateFile(firstInput, "notes.txt"), firstKnowledgeBaseId, CancellationToken.None);
            var second = await store.SaveAsync(CreateFile(secondInput, "notes.txt"), secondKnowledgeBaseId, CancellationToken.None);

            Assert.Contains(firstKnowledgeBaseId.ToString("N"), first.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(secondKnowledgeBaseId.ToString("N"), second.Path, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual(Path.GetDirectoryName(first.Path), Path.GetDirectoryName(second.Path));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsUnsupportedExtension()
    {
        var contentRoot = CreateTemporaryDirectory();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("content"));

        try
        {
            var store = CreateStore(contentRoot, 1024);
            var exception = await Assert.ThrowsAsync<DocumentUploadException>(
                () => store.SaveAsync(CreateFile(input, "payload.exe"), CancellationToken.None));

            Assert.Contains("Supported file types", exception.Message);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_RejectsFilesOverConfiguredLimit()
    {
        var contentRoot = CreateTemporaryDirectory();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("too large"));

        try
        {
            var store = CreateStore(contentRoot, 2);
            var exception = await Assert.ThrowsAsync<DocumentUploadException>(
                () => store.SaveAsync(CreateFile(input, "payload.txt"), CancellationToken.None));

            Assert.Contains("exceeds", exception.Message);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateUploadSession_EnforcesFiveGiBVideoLimit()
    {
        var contentRoot = CreateTemporaryDirectory();
        var maxVideoSize = 5L * 1024 * 1024 * 1024;

        try
        {
            var store = CreateStore(contentRoot, 1024, maxVideoSize, 4);
            var session = store.CreateUploadSession(Guid.NewGuid(), Guid.NewGuid(), "lesson.mp4", "video/mp4", maxVideoSize);

            Assert.Equal(maxVideoSize, session.SizeBytes);
            Assert.True(File.Exists(session.StagingPath));
            Assert.Throws<DocumentUploadException>(() =>
                store.CreateUploadSession(Guid.NewGuid(), Guid.NewGuid(), "too-large.mp4", "video/mp4", maxVideoSize + 1));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AppendUploadChunkAsync_ResumesAtSavedOffsetAndArchivesFile()
    {
        var contentRoot = CreateTemporaryDirectory();
        var knowledgeBaseId = Guid.NewGuid();
        var store = CreateStore(contentRoot, 1024, 1024, 4);
        var session = store.CreateUploadSession(Guid.NewGuid(), knowledgeBaseId, "lesson.mp4", "video/mp4", 6);
        var expected = new byte[] { 1, 2, 3, 4, 5, 6 };

        try
        {
            await using var firstChunk = new MemoryStream(expected[..3]);
            var uploadedBytes = await store.AppendUploadChunkAsync(session, 0, firstChunk, CancellationToken.None);
            Assert.Equal(3, uploadedBytes);

            session = session with { UploadedBytes = uploadedBytes };
            await using var secondChunk = new MemoryStream(expected[3..]);
            uploadedBytes = await store.AppendUploadChunkAsync(session, 3, secondChunk, CancellationToken.None);
            Assert.Equal(6, uploadedBytes);

            session = session with { UploadedBytes = uploadedBytes };
            var stored = store.CompleteUpload(session);

            Assert.Equal(expected, await File.ReadAllBytesAsync(stored.Path));
            Assert.False(File.Exists(session.StagingPath));
            Assert.Equal(session.Id, stored.Id);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AppendUploadChunkAsync_RejectsOversizedChunkWithoutChangingStagedFile()
    {
        var contentRoot = CreateTemporaryDirectory();
        var store = CreateStore(contentRoot, 1024, 1024, 4);
        var session = store.CreateUploadSession(Guid.NewGuid(), Guid.NewGuid(), "lesson.mp4", "video/mp4", 5);

        try
        {
            await using var chunk = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
            await Assert.ThrowsAsync<DocumentUploadException>(
                () => store.AppendUploadChunkAsync(session, 0, chunk, CancellationToken.None));

            Assert.Equal(0, new FileInfo(session.StagingPath).Length);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AppendUploadChunkAsync_RejectsUnexpectedOffset()
    {
        var contentRoot = CreateTemporaryDirectory();
        var store = CreateStore(contentRoot, 1024, 1024, 4);
        var session = store.CreateUploadSession(Guid.NewGuid(), Guid.NewGuid(), "lesson.mp4", "video/mp4", 5);

        try
        {
            await using var chunk = new MemoryStream(new byte[] { 1 });
            await Assert.ThrowsAsync<DocumentUploadException>(
                () => store.AppendUploadChunkAsync(session, 1, chunk, CancellationToken.None));

            Assert.Equal(0, new FileInfo(session.StagingPath).Length);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    private static LocalDocumentStore CreateStore(
        string contentRoot,
        long maxFileSizeBytes,
        long maxVideoFileSizeBytes = 52_428_800,
        int uploadChunkSizeBytes = 16_777_216) =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                RootPath = "uploads",
                MaxFileSizeBytes = maxFileSizeBytes,
                MaxVideoFileSizeBytes = maxVideoFileSizeBytes,
                UploadChunkSizeBytes = uploadChunkSizeBytes
            }),
            new TestWebHostEnvironment(contentRoot));

    private static FormFile CreateFile(Stream content, string fileName) =>
        new(content, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MyFirstMafAgent.Api.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MyFirstMafAgent.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
