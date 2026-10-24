using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Tests;

/// <summary>
/// 图片/音频作为「资产」存储的行为。
///
/// <para>
/// 这些用例针对两个具体缺陷：
/// <list type="number">
///   <item>音频扩展名根本不在白名单里，语音消息/表情包音频直接被拒（400）。</item>
///   <item>Content-Type 直接采信客户端值。浏览器与脚本常给 <c>application/octet-stream</c>，
///         导致 <c>/download</c> 把图片当二进制下载、<c>&lt;audio&gt;</c> 播不出来。</item>
/// </list>
/// </para>
/// </summary>
public sealed class MediaAssetStorageTests
{
    private static readonly string[] AudioExtensions =
        [".mp3", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".weba", ".amr"];

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".wav")]
    [InlineData(".m4a")]
    [InlineData(".aac")]
    [InlineData(".ogg")]
    [InlineData(".opus")]
    [InlineData(".flac")]
    [InlineData(".weba")]
    [InlineData(".amr")]
    public void Audio_extensions_are_classified_and_accepted(string extension)
    {
        Assert.True(LocalDocumentStore.IsAudioExtension(extension));
        Assert.False(LocalDocumentStore.IsVideoExtension(extension));
        Assert.False(LocalDocumentStore.IsImageExtension(extension));

        var contentRoot = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(contentRoot);
            // 走 CreateUploadSession（与真实的断点续传入口一致），只验证校验通过。
            var session = store.CreateUploadSession(
                Guid.NewGuid(), Guid.NewGuid(), $"voice{extension}", "application/octet-stream", 1024);

            Assert.Equal(extension, session.Extension);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".gif")]
    public void Image_extensions_are_classified(string extension)
    {
        Assert.True(LocalDocumentStore.IsImageExtension(extension));
        Assert.False(LocalDocumentStore.IsAudioExtension(extension));
    }

    [Fact]
    public async Task Saving_an_audio_file_normalizes_the_content_type()
    {
        var contentRoot = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(contentRoot);
            var bytes = Encoding.UTF8.GetBytes("fake-mp3-bytes");
            using var input = new MemoryStream(bytes);

            // 客户端上报的是 octet-stream —— 真实浏览器里非常常见。
            var stored = await store.SaveAsync(
                CreateFile(input, "voice.mp3", "application/octet-stream"), CancellationToken.None);

            Assert.Equal(".mp3", stored.Extension);
            // 归一化成 audio/mpeg，否则 /download 出来的响应无法直接在 <audio> 里播放。
            Assert.Equal("audio/mpeg", stored.ContentType);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Saving_an_image_without_a_reported_content_type_still_previews()
    {
        var contentRoot = CreateTemporaryDirectory();
        try
        {
            var store = CreateStore(contentRoot);
            using var input = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);

            var stored = await store.SaveAsync(
                CreateFile(input, "meme.png", contentType: null), CancellationToken.None);

            Assert.Equal("image/png", stored.ContentType);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Audio_gets_its_own_size_budget_instead_of_the_50_MiB_document_limit()
    {
        var contentRoot = CreateTemporaryDirectory();
        try
        {
            const long documentLimit = 1024;
            const long audioLimit = 8 * 1024;
            var store = CreateStore(contentRoot, documentLimit, audioMaxSizeBytes: audioLimit);

            Assert.Equal(audioLimit, store.GetMaxSize(".mp3"));
            Assert.Equal(audioLimit, store.GetMaxSize(".wav"));
            Assert.Equal(documentLimit, store.GetMaxSize(".txt"));
            Assert.Equal(documentLimit, store.GetMaxSize(".png"));

            // 说明音频没有被算进"普通文件"：超过文档上限、但在音频上限内必须通过。
            var session = store.CreateUploadSession(
                Guid.NewGuid(), Guid.NewGuid(), "lecture.m4a", "audio/mp4", audioLimit);
            Assert.Equal(audioLimit, session.SizeBytes);

            Assert.Throws<DocumentUploadException>(() =>
                store.CreateUploadSession(Guid.NewGuid(), Guid.NewGuid(), "lecture.m4a", "audio/mp4", audioLimit + 1));
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Unknown_client_content_type_falls_back_and_known_ones_win()
    {
        Assert.Equal("application/zip", LocalDocumentStore.NormalizeContentType(".zip", "application/zip"));
        Assert.Equal("application/octet-stream", LocalDocumentStore.NormalizeContentType(".zip", null));
        // 客户端值不能覆盖已知扩展名的正确类型。
        Assert.Equal("image/webp", LocalDocumentStore.NormalizeContentType(".webp", "text/plain"));
        Assert.Equal("audio/ogg", LocalDocumentStore.NormalizeContentType(".opus", "application/octet-stream"));
    }

    [Fact]
    public void The_supported_types_message_mentions_audio()
    {
        Assert.Contains("MP3", LocalDocumentStore.SupportedTypesMessage, StringComparison.Ordinal);
        Assert.Contains("WAV", LocalDocumentStore.SupportedTypesMessage, StringComparison.Ordinal);
        foreach (var extension in AudioExtensions)
            Assert.True(LocalDocumentStore.IsAudioExtension(extension), extension);
    }

    private static LocalDocumentStore CreateStore(
        string contentRoot,
        long maxFileSizeBytes = 52_428_800,
        long maxVideoFileSizeBytes = 5_368_709_120,
        long audioMaxSizeBytes = 314_572_800) =>
        new(Microsoft.Extensions.Options.Options.Create(new StorageOptions
            {
                RootPath = "uploads",
                MaxFileSizeBytes = maxFileSizeBytes,
                MaxVideoFileSizeBytes = maxVideoFileSizeBytes,
                MaxAudioFileSizeBytes = audioMaxSizeBytes
            }),
            new TestWebHostEnvironment(contentRoot));

    private static FormFile CreateFile(Stream content, string fileName, string? contentType) =>
        new(content, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType ?? string.Empty
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
