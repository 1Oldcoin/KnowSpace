using System.Text;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using Whisper.net.Ggml;

namespace MyFirstMafAgent.Api.Documents;

/// <summary>Whisper 模型文件的当前状态，供设置页展示与轮询下载进度。</summary>
public sealed record WhisperModelStatus(
    string Model,
    string Path,
    bool Exists,
    long SizeBytes,
    DateTimeOffset? ModifiedAt,
    bool AutoDownload,
    bool Downloading,
    long DownloadedBytes,
    string? LastError)
{
    public bool Ready => Exists && !Downloading;
}

/// <summary>模型缺失且不允许自动下载时抛出：消息本身就是给用户的处置建议。</summary>
public sealed class WhisperModelUnavailableException(string path) : InvalidOperationException(
    $"Whisper 模型未安装（{path}）。离线环境请到「设置 → 语音转写（Whisper）」导入 ggml 模型文件（如 ggml-base.bin），" +
    "或把 Media:AutoDownloadWhisperModel 设为 true 让它自动下载。")
{
    public string ModelPath { get; } = path;
}

/// <summary>
/// Whisper 模型文件的生命周期：定位、校验、导入、下载、删除。
///
/// <para>
/// 只负责"文件"，不碰推理。这样做的原因是推理对象（<see cref="WhisperFactory"/>）
/// 与模型文件强绑定，导入新文件时必须重建；让 <see cref="WhisperTranscriptionService"/>
/// 用文件指纹（路径 + 长度 + 修改时间）判断是否要重建，比跨服务发失效通知更不容易出错。
/// </para>
/// </summary>
public sealed class WhisperModelManager(
    IOptions<MediaOptions> mediaOptions,
    IWebHostEnvironment environment,
    ILogger<WhisperModelManager> logger)
{
    /// <summary>
    /// whisper.cpp 把魔数 <c>0x67676d6c</c>（拼出来是 "ggml"）当 <c>uint32</c> 小端写入文件，
    /// 所以磁盘上前四个字节实际是 <c>6C 6D 67 67</c>（ASCII "lmgg"）。
    /// 官方 ggml-*.bin 全都长这样；这里兼容两种字节序，避免把正确文件拒掉。
    /// </summary>
    private static readonly byte[][] GgmlMagics =
    [
        [0x6C, 0x6D, 0x67, 0x67], // uint32 小端（真实文件的形态）
        Encoding.ASCII.GetBytes("ggml"), // 大端/手写文件的形态
    ];

    private readonly SemaphoreSlim gate = new(1, 1);
    private long downloadedBytes;
    private volatile bool downloading;
    private volatile string? lastError;

    public string ResolvePath()
    {
        var configured = mediaOptions.Value.WhisperModelPath;
        return Path.GetFullPath(Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }

    public WhisperModelStatus GetStatus()
    {
        var path = ResolvePath();
        var info = new FileInfo(path);
        var exists = info.Exists;
        return new WhisperModelStatus(
            mediaOptions.Value.WhisperModel,
            path,
            exists,
            exists ? info.Length : 0,
            exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null,
            mediaOptions.Value.AutoDownloadWhisperModel,
            downloading,
            Interlocked.Read(ref downloadedBytes),
            lastError);
    }

    /// <summary>
    /// 返回可用的模型文件路径；缺失时按配置决定是自动下载还是抛 <see cref="WhisperModelUnavailableException"/>。
    /// </summary>
    public async Task<string> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        if (File.Exists(path)) return path;

        if (!mediaOptions.Value.AutoDownloadWhisperModel)
            throw new WhisperModelUnavailableException(path);

        logger.LogInformation("Whisper model missing at {Path}, downloading {Model}", path, mediaOptions.Value.WhisperModel);
        await DownloadCoreAsync(path, cancellationToken);
        return path;
    }

    /// <summary>显式下载（设置页按钮）。已存在时直接返回，不重复下载。</summary>
    public async Task<WhisperModelStatus> DownloadAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        if (!File.Exists(path)) await DownloadCoreAsync(path, cancellationToken);
        return GetStatus();
    }

    /// <summary>
    /// 后台下载：141 MB 起、离线时可能挂很久，不能让 HTTP 请求等着。
    /// 进度与错误都通过 <see cref="GetStatus"/> 暴露，前端轮询即可。
    /// </summary>
    public WhisperModelStatus StartBackgroundDownload()
    {
        var path = ResolvePath();
        if (downloading || File.Exists(path)) return GetStatus();

        downloading = true;
        lastError = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await DownloadCoreAsync(path, CancellationToken.None);
            }
            catch (Exception exception)
            {
                lastError = exception.Message;
                logger.LogWarning(exception, "Background Whisper model download failed");
            }
            finally
            {
                downloading = false;
            }
        });
        return GetStatus();
    }

    private async Task DownloadCoreAsync(string path, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<GgmlType>(mediaOptions.Value.WhisperModel, true, out var modelType))
            throw new InvalidOperationException($"Unsupported Whisper model '{mediaOptions.Value.WhisperModel}'.");

        await gate.WaitAsync(cancellationToken);
        downloading = true;
        Interlocked.Exchange(ref downloadedBytes, 0);
        lastError = null;
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.download";
        try
        {
            // 双检：等锁期间可能已被另一个请求（或用户导入）填好。
            if (File.Exists(path)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                modelType, cancellationToken: cancellationToken);
            await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await modelStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    Interlocked.Add(ref downloadedBytes, read);
                }
            }

            var length = new FileInfo(temporaryPath).Length;
            if (length == 0) throw new InvalidDataException("下载到的 Whisper 模型文件为空。");
            await MoveWithRetryAsync(temporaryPath, path);
            logger.LogInformation("Whisper model downloaded to {Path} ({Bytes} bytes)", path, length);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not InvalidDataException
                                              and not InvalidOperationException)
        {
            // 这里最常见的是离线/代理不通。把"该怎么办"直接写进消息，比抛原始 HttpRequestException 有用得多。
            throw new InvalidOperationException(
                $"下载 Whisper 模型失败：{exception.Message}。" +
                "离线环境请到「设置 → 语音转写（Whisper）」导入本地 ggml 模型文件（如 ggml-base.bin）。",
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath)) TryDelete(temporaryPath);
            downloading = false;
            gate.Release();
        }
    }

    /// <summary>
    /// 导入本地 ggml 模型。先落盘到临时文件，校验魔数与体积后再原子替换，
    /// 避免一个坏文件把原本能用的模型顶掉。
    /// </summary>
    public async Task<WhisperModelStatus> ImportAsync(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".bin", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Whisper 模型必须是 .bin 文件（whisper.cpp 的 ggml 格式，如 ggml-base.bin）。");

        var path = ResolvePath();
        await gate.WaitAsync(cancellationToken);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.import";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = await CopyToFileAsync(stream, temporaryPath, mediaOptions.Value.MaxWhisperModelBytes, cancellationToken);

            if (!await HasGgmlMagicAsync(temporaryPath, cancellationToken))
                throw new InvalidDataException(
                    $"「{fileName}」不是 whisper.cpp 的 ggml 模型（文件头应为 ggml / lmgg）。请确认下载的是 ggml-*.bin，而不是 PyTorch/ONNX 权重。");

            await MoveWithRetryAsync(temporaryPath, path);
            logger.LogInformation("Whisper model imported from {FileName} to {Path} ({Bytes} bytes)", fileName, path, bytes);
            lastError = null;
            return GetStatus();        }
        finally
        {
            if (File.Exists(temporaryPath)) TryDelete(temporaryPath);
            gate.Release();
        }
    }

    /// <summary>删除模型（用于验证离线行为或回收磁盘）。</summary>
    public WhisperModelStatus Delete()
    {
        var path = ResolvePath();
        if (File.Exists(path))
        {
            TryDelete(path);
            logger.LogInformation("Whisper model deleted at {Path}", path);
        }
        return GetStatus();
    }

    private static async Task<long> CopyToFileAsync(Stream source, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        await using var fileStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidDataException($"模型文件超过 {maxBytes / (1024 * 1024)} MB 上限。");
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (total == 0) throw new InvalidDataException("上传的模型文件为空。");
        return total;
    }

    private static async Task<bool> HasGgmlMagicAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4, useAsync: true);
        var header = new byte[4];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (read != header.Length) return false;
        return GgmlMagics.Any(magic => header.AsSpan().SequenceEqual(magic));
    }

    /// <summary>
    /// 原子替换目标文件，但允许重试：刚替换完 141 MB 文件时，杀毒软件/索引服务
    /// 经常短暂锁住目标，直接 Move 会抛 UnauthorizedAccessException（实测过一次）。
    /// </summary>
    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        const int maxAttempts = 6;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts)
                    throw new IOException(
                        $"无法替换模型文件（{Path.GetFileName(destination)}）：它正被其他程序占用（常见于杀毒软件正在扫描刚写入的大文件）。请稍后重试。",
                        exception);
                await Task.Delay(200 * attempt);
            }
        }
    }

    private static void TryDelete(string path)    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 文件被占用时留给下次；模型目录由系统清理。
        }
    }
}
