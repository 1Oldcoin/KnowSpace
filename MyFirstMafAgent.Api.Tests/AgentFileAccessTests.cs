using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AgentFileAccessTests
{
    [Fact]
    public void Resolve_AcceptsAbsolutePathInsideTheConfiguredRoot()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "note.md");
        File.WriteAllText(file, "hello");

        try
        {
            var access = CreateAccess(container, root);
            var resolved = access.Resolve(file);

            Assert.Equal("学习资料", resolved.Root.Name);
            Assert.Equal(file, resolved.FullPath);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"..\outside.txt")]
    [InlineData(@"sub\..\..\outside.txt")]
    public void Resolve_RejectsTraversalOutsideTheRoot(string candidate)
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);

        try
        {
            var access = CreateAccess(container, root);
            Assert.Throws<InvalidOperationException>(() => access.Resolve(Path.Combine(root, candidate)));
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RejectsAbsolutePathOutsideEveryRoot()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(container, "secret.txt");
        File.WriteAllText(outside, "secret");

        try
        {
            var access = CreateAccess(container, root);
            Assert.Throws<InvalidOperationException>(() => access.Resolve(outside));
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public void Resolve_SupportsRootNamePrefix()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(Path.Combine(root, "拆文"));
        File.WriteAllText(Path.Combine(root, "拆文", "概要.md"), "概要");

        try
        {
            var access = CreateAccess(container, root);
            var resolved = access.Resolve("学习资料/拆文/概要.md");

            Assert.Equal(Path.Combine(root, "拆文", "概要.md"), resolved.FullPath);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_DetectsUtf8AndGb18030()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);
        var utf8Path = Path.Combine(root, "utf8.md");
        var gbkPath = Path.Combine(root, "gbk.md");
        await File.WriteAllTextAsync(utf8Path, "向量模型切换成功。");
        await File.WriteAllBytesAsync(gbkPath, Gb18030.GetBytes("中文编码测试"));

        try
        {
            var access = CreateAccess(container, root);
            var utf8 = await access.ReadAsync(utf8Path, 0, 4000, CancellationToken.None);
            var gbk = await access.ReadAsync(gbkPath, 0, 4000, CancellationToken.None);

            Assert.Contains("向量模型切换成功。", utf8, StringComparison.Ordinal);
            Assert.Contains("utf-8", utf8, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("中文编码测试", gbk, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReportsContinuationOffset()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "long.md");
        await File.WriteAllTextAsync(path, new string('字', 100));

        try
        {
            var access = CreateAccess(container, root);
            var page = await access.ReadAsync(path, 0, 20, CancellationToken.None);
            Assert.Contains("offset=20", page, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DefaultsToFirstRootWhenPathIsOmitted()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.md"), "a");

        try
        {
            var access = CreateAccess(container, root);
            var resolved = access.Resolve(null);
            Assert.Equal(root, resolved.FullPath);

            var listing = access.Enumerate(null, "*.md", recursive: false, maxEntries: 10, CancellationToken.None);
            Assert.Single(listing.Entries);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_ReturnsFilesAndDirectoriesWithLimit()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(Path.Combine(root, "拆文"));
        File.WriteAllText(Path.Combine(root, "a.md"), "a");
        File.WriteAllText(Path.Combine(root, "拆文", "b.md"), "b");

        try
        {
            var access = CreateAccess(container, root);

            var flat = access.Enumerate(root, null, recursive: false, maxEntries: 100, CancellationToken.None);
            Assert.Contains(flat.Entries, entry => entry.Type == "dir");
            Assert.Contains(flat.Entries, entry => entry.Type == "file" && entry.Path.EndsWith("a.md", StringComparison.Ordinal));

            var deep = access.Enumerate(root, "*.md", recursive: true, maxEntries: 1, CancellationToken.None);
            Assert.Single(deep.Entries);
            Assert.True(deep.Truncated);
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_IsRejectedForReadOnlyRoot()
    {
        var container = CreateTemporaryDirectory();
        var root = Path.Combine(container, "学习资料");
        Directory.CreateDirectory(root);

        try
        {
            var access = CreateAccess(container, root);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                access.WriteAsync(Path.Combine(root, "new.md"), "x", append: false, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(container, recursive: true);
        }
    }

    private static System.Text.Encoding Gb18030
    {
        get
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding("GB18030");
        }
    }

    private static AgentFileAccess CreateAccess(string contentRoot, string allowedRoot) =>
        new(Microsoft.Extensions.Options.Options.Create(new AgentOptions
            {
                WorkspaceRootPath = "agent",
                FileRoots =
                [
                    new AgentFileRootOptions
                    {
                        Name = "学习资料",
                        Path = allowedRoot,
                        AllowWrite = false,
                        AllowDelete = false,
                        Enabled = true
                    }
                ]
            }),
            new TestWebHostEnvironment(contentRoot));

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
