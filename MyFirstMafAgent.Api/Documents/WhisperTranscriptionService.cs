using MyFirstMafAgent.Api.Options;
using Whisper.net;

namespace MyFirstMafAgent.Api.Documents;

public sealed class WhisperTranscriptionService(
    WhisperModelManager modelManager) : IAsyncDisposable
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim transcriptionLock = new(1, 1);
    private WhisperFactory? factory;
    private WhisperProcessor? processor;

    /// <summary>
    /// 当前推理对象绑定的模型文件指纹。导入/下载会替换文件，
    /// 指纹变了就必须重建，否则会一直用旧模型（或用一个已被删除的文件句柄）。
    /// </summary>
    private (string Path, long Length, DateTime ModifiedAt)? loadedIdentity;

    public async Task<IReadOnlyList<WhisperTranscriptionSegment>> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await transcriptionLock.WaitAsync(cancellationToken);
        try
        {
            await using var audioStream = File.OpenRead(audioPath);
            var transcript = new List<WhisperTranscriptionSegment>();
            await foreach (var segment in processor!.ProcessAsync(audioStream, cancellationToken))
            {
                var text = segment.Text.Trim();
                if (text.Length > 0)
                    transcript.Add(new WhisperTranscriptionSegment(segment.Start, segment.End, new string(text.AsSpan())));
            }
            return transcript;
        }
        finally
        {
            transcriptionLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        // 先问管理器要路径：缺模型时它要么下载、要么抛出带处置建议的异常。
        var modelPath = await modelManager.EnsureAvailableAsync(cancellationToken);
        var info = new FileInfo(modelPath);
        var identity = (modelPath, info.Length, info.LastWriteTimeUtc);
        if (processor is not null && loadedIdentity == identity) return;

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (processor is not null && loadedIdentity == identity) return;

            // 模型被换掉了：先释放旧对象，再懒加载新文件。
            await ReleaseProcessorAsync();

            var whisperFactory = WhisperFactory.FromPath(modelPath);
            try
            {
                var whisperProcessor = whisperFactory.CreateBuilder().WithLanguageDetection().Build();
                factory = whisperFactory;
                processor = whisperProcessor;
                loadedIdentity = identity;
            }
            catch
            {
                whisperFactory.Dispose();
                throw;
            }
        }
        finally
        {
            initializationLock.Release();
        }
    }

    private async Task ReleaseProcessorAsync()
    {
        if (processor is not null)
        {
            await processor.DisposeAsync();
            processor = null;
        }
        factory?.Dispose();
        factory = null;
        loadedIdentity = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseProcessorAsync();
        initializationLock.Dispose();
        transcriptionLock.Dispose();
    }
}

public sealed record WhisperTranscriptionSegment(TimeSpan Start, TimeSpan End, string Text);
