using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using MyFirstMafAgent.Api.Application.KnowledgePackages;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Topics;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 知识库包导入的补偿回滚（技术债 T-2）。
///
/// <para>
/// 这个用例的意义：导入要同时写数据库和文件系统，事务包不住文件。
/// 所以必须证明"中途失败会把已经写进去的东西收回去"，而不是只靠代码里看起来有个 catch。
/// </para>
/// </summary>
public sealed class KnowledgePackageRollbackTests : IDisposable
{
    private readonly string _root;
    private readonly LocalDocumentStore _documentStore;

    public KnowledgePackageRollbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maf-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _documentStore = new LocalDocumentStore(
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { RootPath = _root }),
            new FakeEnvironment(_root));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言结果。
        }
    }

    [Fact]
    public async Task Import_failure_rolls_back_documents_topics_and_knowledge_base()
    {
        var store = new RecordingPackageStore();
        var topics = new RecordingTopicRepository();
        // 第 1 篇正常写入，第 2 篇抛错 —— 模拟"导到一半炸了"。
        var notes = new RecordingNoteRepository { FailOnAdd = 2 };

        var importer = new KnowledgePackageImporter(
            store, _documentStore, topics, notes, NullLogger<KnowledgePackageImporter>.Instance);

        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            KnowledgeBase = new KnowledgePackageSource { Name = "回滚测试库", CreatedAt = DateTimeOffset.UtcNow },
            Topics = [new KnowledgePackageTopic { Id = Guid.NewGuid(), Name = "专题A", Color = "mint" }],
            Documents =
            [
                new KnowledgePackageDocument
                {
                    Id = Guid.NewGuid(), FileName = "a.md", Extension = ".md", ContentType = "text/markdown",
                    SizeBytes = 5, File = "files/0001.md"
                }
            ],
            Notes =
            [
                new KnowledgePackageNote { Id = Guid.NewGuid(), Title = "笔记一", Content = "内容一" },
                new KnowledgePackageNote { Id = Guid.NewGuid(), Title = "笔记二", Content = "内容二" }
            ]
        };

        await using var package = BuildPackage(manifest, ("files/0001.md", "hello"));

        var exception = await Assert.ThrowsAsync<KnowledgePackageException>(
            () => importer.ImportAsync(package, CancellationToken.None));

        // 1) 错误信息要说清"已经回滚了什么"，而不是把原始异常直接抛给用户。
        Assert.Contains("导入失败，已回滚", exception.Message);
        Assert.Contains("1 份资料", exception.Message);
        Assert.NotNull(exception.InnerException);

        // 2) 已建的专题、资料行、知识库全部被删。
        Assert.Single(store.DeletedDocuments);
        Assert.Single(store.DeletedKnowledgeBases);
        Assert.Equal(store.CreatedKnowledgeBaseId, store.DeletedKnowledgeBases[0]);
        Assert.Single(topics.Deleted);
        Assert.Equal(store.CreatedKnowledgeBaseId, topics.Deleted[0].KnowledgeBaseId);

        // 3) 第一条笔记也回滚了（第二条根本没写进去）。
        Assert.Equal(1, notes.Added);
        Assert.Equal(2, notes.Attempts);
        Assert.Single(notes.Deleted);

        // 4) 落盘的文件被删掉，目录里不残留孤儿文件。
        var remaining = Directory.Exists(Path.Combine(_root, store.CreatedKnowledgeBaseId.ToString("N")))
            ? Directory.GetFiles(Path.Combine(_root, store.CreatedKnowledgeBaseId.ToString("N")))
            : [];
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Import_success_keeps_everything_and_does_not_roll_back()
    {
        var store = new RecordingPackageStore();
        var topics = new RecordingTopicRepository();
        var notes = new RecordingNoteRepository();

        var importer = new KnowledgePackageImporter(
            store, _documentStore, topics, notes, NullLogger<KnowledgePackageImporter>.Instance);

        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            KnowledgeBase = new KnowledgePackageSource { Name = "正常库" },
            Documents =
            [
                new KnowledgePackageDocument
                {
                    Id = Guid.NewGuid(), FileName = "a.md", Extension = ".md", ContentType = "text/markdown",
                    SizeBytes = 5, File = "files/0001.md"
                }
            ],
            Notes = [new KnowledgePackageNote { Id = Guid.NewGuid(), Title = "笔记", Content = "正文" }]
        };

        await using var package = BuildPackage(manifest, ("files/0001.md", "hello"));

        var result = await importer.ImportAsync(package, CancellationToken.None);

        Assert.Equal(1, result.Documents);
        Assert.Equal(1, result.Notes);
        Assert.Empty(store.DeletedDocuments);
        Assert.Empty(store.DeletedKnowledgeBases);
        Assert.Empty(notes.Deleted);
        Assert.True(Directory.Exists(Path.Combine(_root, result.KnowledgeBaseId.ToString("N"))));
    }

    [Fact]
    public async Task Import_that_yields_nothing_rolls_back_and_reports_why()
    {
        var store = new RecordingPackageStore();
        var topics = new RecordingTopicRepository();
        var notes = new RecordingNoteRepository();

        var importer = new KnowledgePackageImporter(
            store, _documentStore, topics, notes, NullLogger<KnowledgePackageImporter>.Instance);

        // 清单里写了一份资料，但包里根本没有这个条目 —— 真实场景：包被手工改过，
        // 或者打包器把条目名写成了别的形式而清单没同步。
        var manifest = new KnowledgePackageManifest
        {
            ExportedAt = DateTimeOffset.UtcNow,
            KnowledgeBase = new KnowledgePackageSource { Name = "空内容库" },
            Topics = [new KnowledgePackageTopic { Id = Guid.NewGuid(), Name = "专题B" }],
            Documents =
            [
                new KnowledgePackageDocument
                {
                    Id = Guid.NewGuid(), FileName = "missing.md", Extension = ".md",
                    ContentType = "text/markdown", SizeBytes = 5, File = "files/missing.md"
                }
            ]
        };

        await using var package = BuildPackage(manifest);

        var exception = await Assert.ThrowsAsync<KnowledgePackageException>(
            () => importer.ImportAsync(package, CancellationToken.None));

        // 必须是"没有可导入的内容"而不是"导入成功 0 份"——后者会留下一个空知识库。
        Assert.Contains("没有可导入的内容", exception.Message);
        Assert.Contains("缺少文件", exception.Message);

        // 专题与知识库壳子都被回滚掉。
        Assert.Single(store.DeletedKnowledgeBases);
        Assert.Single(topics.Deleted);
        Assert.Empty(notes.Deleted);
        Assert.Equal(0, notes.Added);
    }

    private static MemoryStream BuildPackage(KnowledgePackageManifest manifest, params (string Path, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = archive.CreateEntry(KnowledgePackageExporter.ManifestEntryName);
            using (var writer = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(manifest, KnowledgePackageManifest.JsonOptions));

            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private sealed class FakeEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NullFileProvider : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => throw new NotSupportedException();
        public IDirectoryContents GetDirectoryContents(string subpath) => throw new NotSupportedException();
        public IChangeToken Watch(string filter) => throw new NotSupportedException();
    }

    private sealed class RecordingPackageStore : IKnowledgePackageStore
    {
        public Guid CreatedKnowledgeBaseId { get; private set; }
        public List<Guid> DeletedDocuments { get; } = [];
        public List<Guid> DeletedKnowledgeBases { get; } = [];
        private readonly Dictionary<Guid, string> _paths = [];

        public Task<KnowledgePackageBase?> GetKnowledgeBaseAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<KnowledgePackageBase?>(null);

        public Task<IReadOnlyList<KnowledgePackageBase>> ListKnowledgeBasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgePackageBase>>([]);

        public Task<KnowledgePackageBase?> CreateKnowledgeBaseAsync(string name, CancellationToken cancellationToken)
        {
            CreatedKnowledgeBaseId = Guid.NewGuid();
            return Task.FromResult<KnowledgePackageBase?>(
                new KnowledgePackageBase(CreatedKnowledgeBaseId, name, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<KnowledgePackageDocumentInfo>> ListDocumentsAsync(
            Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnowledgePackageDocumentInfo>>([]);

        public Task AddQueuedDocumentAsync(
            Guid documentId, Guid knowledgeBaseId, string fileName, string extension, string contentType,
            long sizeBytes, string storagePath, CancellationToken cancellationToken)
        {
            _paths[documentId] = storagePath;
            return Task.CompletedTask;
        }

        public Task SetDocumentSummaryAsync(Guid documentId, string summary, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string?> DeleteDocumentAsync(Guid documentId, CancellationToken cancellationToken)
        {
            DeletedDocuments.Add(documentId);
            return Task.FromResult(_paths.TryGetValue(documentId, out var path) ? path : null);
        }

        public Task DeleteKnowledgeBaseAsync(Guid knowledgeBaseId, CancellationToken cancellationToken)
        {
            DeletedKnowledgeBases.Add(knowledgeBaseId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingTopicRepository : ITopicRepository
    {
        public List<(Guid KnowledgeBaseId, Guid TopicId)> Deleted { get; } = [];

        public Task<IReadOnlyList<Topic>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Topic>>([]);

        public Task<Topic?> GetAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken) =>
            Task.FromResult<Topic?>(null);

        public Task<bool> NameExistsAsync(Guid knowledgeBaseId, string name, Guid? excludeTopicId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task AddAsync(Topic topic, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateAsync(Topic topic, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid knowledgeBaseId, Guid topicId, CancellationToken cancellationToken)
        {
            Deleted.Add((knowledgeBaseId, topicId));
            return Task.FromResult(true);
        }

        public Task<DocumentTopicAssignResult> AssignDocumentAsync(Guid documentId, Guid? topicId, CancellationToken cancellationToken) =>
            Task.FromResult(DocumentTopicAssignResult.Success);

        public Task<Guid?> GetDocumentTopicAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);
    }

    private sealed class RecordingNoteRepository : INoteRepository
    {
        /// <summary>第几次 AddAsync 抛错（1 = 第一次就失败）。0 表示从不失败。</summary>
        public int FailOnAdd { get; init; }

        /// <summary>尝试次数（包含失败的那一次）。</summary>
        public int Attempts { get; private set; }

        /// <summary>真正写入成功的次数。</summary>
        public int Added { get; private set; }

        public List<Guid> Deleted { get; } = [];

        public Task<IReadOnlyList<Note>> ListAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Note>>([]);

        public Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Note>>([]);

        public Task<Note?> GetAsync(Guid noteId, CancellationToken cancellationToken) =>
            Task.FromResult<Note?>(null);

        public Task<int> CountAsync(Guid knowledgeBaseId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task AddAsync(Note note, CancellationToken cancellationToken)
        {
            Attempts++;
            if (FailOnAdd > 0 && Attempts >= FailOnAdd)
                throw new InvalidOperationException("模拟导入中途失败");

            Added++;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Note note, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(Guid noteId, Guid knowledgeBaseId, CancellationToken cancellationToken)
        {
            Deleted.Add(noteId);
            return Task.FromResult(true);
        }
    }
}
