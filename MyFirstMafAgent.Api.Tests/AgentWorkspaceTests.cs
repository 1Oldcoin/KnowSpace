using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

public sealed class AgentWorkspaceTests
{
    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("sub/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public void Resolve_RejectsPathsOutsideTheSandbox(string candidate)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = CreateWorkspace(root);
            Assert.Throws<InvalidOperationException>(() => workspace.Resolve(candidate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_AllowsNestedRelativePaths()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = CreateWorkspace(root);
            var resolved = workspace.Resolve("notes/2024/plan.md");

            Assert.StartsWith(Path.Combine(root, "agent"), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("plan.md", resolved, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsUtf8Content()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = CreateWorkspace(root);
            await workspace.WriteAsync("日记/第一天.md", "向量模型切换成功。", append: false, CancellationToken.None);
            var content = await workspace.ReadAsync("日记/第一天.md", 0, 4000, CancellationToken.None);

            Assert.Contains("向量模型切换成功。", content, StringComparison.Ordinal);
            Assert.Contains("日记/第一天.md", content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAsync_ReportsContinuationOffsetWhenTruncated()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = CreateWorkspace(root);
            await workspace.WriteAsync("long.md", new string('字', 100), append: false, CancellationToken.None);

            var first = await workspace.ReadAsync("long.md", 0, 20, CancellationToken.None);
            Assert.Contains("offset=20", first, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AgentWorkspace CreateWorkspace(string contentRoot) =>
        new(Microsoft.Extensions.Options.Options.Create(new AgentOptions { WorkspaceRootPath = "agent" }),
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
