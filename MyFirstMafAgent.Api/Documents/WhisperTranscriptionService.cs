using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Options;
using Whisper.net;
using Whisper.net.Ggml;

namespace MyFirstMafAgent.Api.Documents;

public sealed class WhisperTranscriptionService(
    IOptions<MediaOptions> mediaOptions,
    IWebHostEnvironment environment) : IAsyncDisposable
{
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim transcriptionLock = new(1, 1);
    private WhisperFactory? factory;
    private WhisperProcessor? processor;

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
        if (processor is not null) return;

        await initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (processor is not null) return;

            var options = mediaOptions.Value;
            if (!Enum.TryParse<GgmlType>(options.WhisperModel, true, out var modelType))
                throw new InvalidOperationException($"Unsupported Whisper model '{options.WhisperModel}'.");

            var modelPath = Path.GetFullPath(Path.IsPathRooted(options.WhisperModelPath)
                ? options.WhisperModelPath
                : Path.Combine(environment.ContentRootPath, options.WhisperModelPath));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);

            if (!File.Exists(modelPath))
            {
                var temporaryPath = $"{modelPath}.{Guid.NewGuid():N}.download";
                try
                {
                    await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                        modelType, cancellationToken: cancellationToken);
                    await using (var fileStream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 81920, useAsync: true))
                    {
                        await modelStream.CopyToAsync(fileStream, cancellationToken);
                    }
                    File.Move(temporaryPath, modelPath);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }

            var whisperFactory = WhisperFactory.FromPath(modelPath);
            try
            {
                var whisperProcessor = whisperFactory.CreateBuilder().WithLanguageDetection().Build();
                factory = whisperFactory;
                processor = whisperProcessor;
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

    public async ValueTask DisposeAsync()
    {
        if (processor is not null) await processor.DisposeAsync();
        factory?.Dispose();
        initializationLock.Dispose();
        transcriptionLock.Dispose();
    }
}

public sealed record WhisperTranscriptionSegment(TimeSpan Start, TimeSpan End, string Text);
