using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Options;
using Xunit;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 删除知识库：规则判定 + 文件清理的先后顺序。
///
/// <para>
/// 这里不碰数据库（用假实现顶替端口），只钉住两件容易写错的事：
/// 默认知识库必须被拒绝；数据库报告删除成功后，磁盘目录必须跟着清掉。
/// 顺序反了（先删文件再删库）会出现「资料记录在、文件没了」的坏状态，所以也一并断言。
/// </para>
/// </summary>
public sealed class KnowledgeBaseDeletionTests : IDisposable
{
    private readonly string _root;
    private readonly LocalDocumentStore _documentStore;

    public KnowledgeBaseDeletionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maf-kb-delete-" + Guid.NewGuid().ToString("N"));
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
    public async Task Default_knowledge_base_cannot_be_deleted()
    {
        var store = new RecordingMaintenanceStore(KnowledgeBaseDeletionReportFor(KnowledgeBaseDefaults.DefaultKnowledgeBaseId));
        var service = CreateService(store);

        var outcome = await service.DeleteAsync(KnowledgeBaseDefaults.DefaultKnowledgeBaseId, CancellationToken.None);

        Assert.False(outcome.Deleted);
        Assert.False(outcome.NotFound);
        Assert.Contains("默认知识库", outcome.Error);
        // 一行都不该删：拒绝必须在落到存储层之前发生。
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Missing_knowledge_base_reports_not_found()
    {
        var store = new RecordingMaintenanceStore(null);
        var service = CreateService(store);

        var outcome = await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(outcome.Deleted);
        Assert.True(outcome.NotFound);
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task Successful_delete_removes_database_rows_and_the_document_directory()
    {
        var knowledgeBaseId = Guid.NewGuid();
        var documentDirectory = Path.Combine(_root, knowledgeBaseId.ToString("N"));
        Directory.CreateDirectory(documentDirectory);
        var filePath = Path.Combine(documentDirectory, Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(filePath, "# 资料正文");
        // 另一个知识库的目录必须原样保留：删除是按目录粒度做的，不能误伤邻居。
        var otherDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherDirectory);
        var otherFile = Path.Combine(otherDirectory, "keep.md");
        await File.WriteAllTextAsync(otherFile, "别的库");

        var store = new RecordingMaintenanceStore(KnowledgeBaseDeletionReportFor(knowledgeBaseId));
        var service = CreateService(store);

        var outcome = await service.DeleteAsync(knowledgeBaseId, CancellationToken.None);

        Assert.True(outcome.Deleted);
        Assert.Equal(knowledgeBaseId, outcome.Report!.KnowledgeBaseId);
        Assert.False(Directory.Exists(documentDirectory));
        Assert.True(File.Exists(otherFile));
        Assert.Single(store.Calls);
    }

    [Fact]
    public async Task Files_are_left_alone_when_the_database_step_fails()
    {
        var knowledgeBaseId = Guid.NewGuid();
        var documentDirectory = Path.Combine(_root, knowledgeBaseId.ToString("N"));
        Directory.CreateDirectory(documentDirectory);
        var filePath = Path.Combine(documentDirectory, "keep.md");
        await File.WriteAllTextAsync(filePath, "库还在");

        var store = new RecordingMaintenanceStore(KnowledgeBaseDeletionReportFor(knowledgeBaseId))
        {
            FailWith = new InvalidOperationException("事务回滚")
        };
        var service = CreateService(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(knowledgeBaseId, CancellationToken.None));

        // 数据库没删成，文件必须还在——否则会留下点不开的「幽灵资料」。
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public void Deleting_a_directory_that_does_not_exist_is_not_an_error()
    {
        Assert.True(_documentStore.DeleteKnowledgeBaseDirectory(Guid.NewGuid()));
    }

    private KnowledgeBaseDeletionService CreateService(RecordingMaintenanceStore store) =>
        new(store, _documentStore, NullLogger<KnowledgeBaseDeletionService>.Instance);

    private static KnowledgeBaseDeletionReport KnowledgeBaseDeletionReportFor(Guid knowledgeBaseId) =>
        new(knowledgeBaseId, "测试知识库", DocumentCount: 2, ChunkCount: 9, NoteCount: 1, TopicCount: 1, ChatSessionCount: 3);

    private sealed class RecordingMaintenanceStore(KnowledgeBaseDeletionReport? report) : IKnowledgeBaseMaintenanceStore
    {
        public List<Guid> Calls { get; } = [];

        public Exception? FailWith { get; init; }

        public Task<KnowledgeBaseDeletionReport?> DeleteKnowledgeBaseAsync(
            Guid knowledgeBaseId,
            CancellationToken cancellationToken)
        {
            Calls.Add(knowledgeBaseId);
            if (FailWith is not null) throw FailWith;
            return Task.FromResult(report);
        }
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
}
