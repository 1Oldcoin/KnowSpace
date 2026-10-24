using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// Whisper 模型文件管理器：导入校验、原子替换、离线提示。
/// 这些是 P0-4 里"离线首次导入视频必然失败"的直接修复点，必须可重复验证。
/// </summary>
public sealed class WhisperModelManagerTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(
        Path.GetTempPath(), "MyFirstMafAgent.Api.Tests", Guid.NewGuid().ToString("N"));

    public WhisperModelManagerTests() => Directory.CreateDirectory(contentRoot);

    public void Dispose()
    {
        if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
    }

    [Fact]
    public async Task Import_rejects_files_that_are_not_bin()
    {
        var manager = CreateManager();
        using var stream = new MemoryStream(GgmlFile(64));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => manager.ImportAsync("ggml-base.pt", stream, CancellationToken.None));

        Assert.Contains(".bin", exception.Message);
        Assert.False(manager.GetStatus().Exists);
    }

    [Fact]
    public async Task Import_rejects_files_without_ggml_magic()
    {
        var manager = CreateManager();
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("PK\u0003\u0004 this is really a zip"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None));

        Assert.Contains("ggml", exception.Message);
        Assert.False(manager.GetStatus().Exists);
    }

    [Fact]
    public async Task Import_writes_model_and_reports_status()
    {
        var manager = CreateManager();
        var payload = GgmlFile(1024);
        using var stream = new MemoryStream(payload);

        var status = await manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None);

        Assert.True(status.Exists);
        Assert.Equal(payload.Length, status.SizeBytes);
        Assert.False(status.Downloading);
        Assert.Equal(Path.Combine(contentRoot, "models", "ggml-base.bin"), status.Path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(status.Path, CancellationToken.None));
    }

    [Fact]
    public async Task Import_failure_keeps_the_previously_working_model()
    {
        var manager = CreateManager();
        var original = GgmlFile(512);
        using (var first = new MemoryStream(original))
            await manager.ImportAsync("ggml-base.bin", first, CancellationToken.None);

        using var broken = new MemoryStream(Encoding.ASCII.GetBytes("not a model at all"));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync("ggml-base.bin", broken, CancellationToken.None));

        // 关键点：坏文件不能把能用的模型顶掉。
        Assert.Equal(original, await File.ReadAllBytesAsync(manager.ResolvePath(), CancellationToken.None));
    }

    [Fact]
    public async Task Import_rejects_oversized_files()
    {
        var manager = CreateManager(maxBytes: 256);
        using var stream = new MemoryStream(GgmlFile(4096));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None));

        Assert.Contains("上限", exception.Message);
    }

    [Fact]
    public async Task EnsureAvailable_throws_actionable_error_when_offline_and_missing()
    {
        var manager = CreateManager(autoDownload: false);

        var exception = await Assert.ThrowsAsync<WhisperModelUnavailableException>(
            () => manager.EnsureAvailableAsync(CancellationToken.None));

        // 消息必须能直接指导用户操作，否则离线用户只能看日志猜。
        Assert.Contains("设置 → 语音转写", exception.Message);
        Assert.Contains("AutoDownloadWhisperModel", exception.Message);
        Assert.Contains("ggml-base.bin", exception.Message);
        Assert.Equal(manager.ResolvePath(), exception.ModelPath);
    }

    [Fact]
    public async Task EnsureAvailable_returns_path_when_model_exists()
    {
        var manager = CreateManager();
        using (var stream = new MemoryStream(GgmlFile(128)))
            await manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None);

        var path = await manager.EnsureAvailableAsync(CancellationToken.None);

        Assert.Equal(manager.ResolvePath(), path);
    }

    [Fact]
    public async Task Delete_removes_the_model()
    {
        var manager = CreateManager();
        using (var stream = new MemoryStream(GgmlFile(128)))
            await manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None);

        var status = manager.Delete();

        Assert.False(status.Exists);
        Assert.Equal(0, status.SizeBytes);
        Assert.False(File.Exists(manager.ResolvePath()));
    }

    [Fact]
    public void Status_reports_missing_file_without_throwing()
    {
        var status = CreateManager().GetStatus();

        Assert.False(status.Exists);
        Assert.False(status.Ready);
        Assert.Null(status.ModifiedAt);
        Assert.Null(status.LastError);
        Assert.True(status.AutoDownload);
    }

    private WhisperModelManager CreateManager(bool autoDownload = true, long? maxBytes = null) =>
        new(Microsoft.Extensions.Options.Options.Create(new MediaOptions
            {
                WhisperModel = "Base",
                WhisperModelPath = "models/ggml-base.bin",
                AutoDownloadWhisperModel = autoDownload,
                MaxWhisperModelBytes = maxBytes ?? 3L * 1024 * 1024 * 1024,
            }),
            new TestWebHostEnvironment(contentRoot),
            NullLogger<WhisperModelManager>.Instance);

    /// <summary>
    /// 构造一个文件头合法的假模型，后面填充无关字节即可（本测试不加载推理）。
    /// 前四个字节用真实 ggml 文件的形态：uint32 小端写出的 <c>6C 6D 67 67</c>。
    /// </summary>
    private static byte[] GgmlFile(int size)
    {
        var bytes = new byte[Math.Max(size, 8)];
        new byte[] { 0x6C, 0x6D, 0x67, 0x67 }.CopyTo(bytes, 0);
        for (var index = 4; index < bytes.Length; index++) bytes[index] = (byte)(index % 251);
        return bytes;
    }

    [Fact]
    public async Task Import_accepts_the_big_endian_header_too()
    {
        var manager = CreateManager();
        var payload = GgmlFile(64);
        Encoding.ASCII.GetBytes("ggml").CopyTo(payload, 0);
        using var stream = new MemoryStream(payload);

        var status = await manager.ImportAsync("ggml-base.bin", stream, CancellationToken.None);

        Assert.True(status.Exists);
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
