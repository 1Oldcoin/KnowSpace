using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 转写服务的空模型行为：必须把"该怎么办"透给调用方。
/// P0-4 的实测缺口是"离线首次导入视频直接 Failed，错误里只有一串下载异常"，
/// 这个测试锁住修复后的语义。
/// </summary>
public sealed class WhisperTranscriptionServiceTests : IDisposable
{
    private readonly string contentRoot = Path.Combine(
        Path.GetTempPath(), "MyFirstMafAgent.Api.Tests", Guid.NewGuid().ToString("N"));

    public WhisperTranscriptionServiceTests() => Directory.CreateDirectory(contentRoot);

    public void Dispose()
    {
        if (Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
    }

    [Fact]
    public async Task Transcribe_without_model_reports_where_to_get_one()
    {
        var manager = new WhisperModelManager(
            Microsoft.Extensions.Options.Options.Create(new MediaOptions
            {
                WhisperModelPath = "models/ggml-base.bin",
                AutoDownloadWhisperModel = false,
            }),
            new TestWebHostEnvironment(contentRoot),
            NullLogger<WhisperModelManager>.Instance);

        await using var service = new WhisperTranscriptionService(manager);

        var exception = await Assert.ThrowsAsync<WhisperModelUnavailableException>(
            () => service.TranscribeAsync(Path.Combine(contentRoot, "audio.wav"), CancellationToken.None));

        Assert.Contains("导入", exception.Message);
        Assert.Contains("AutoDownloadWhisperModel", exception.Message);
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
