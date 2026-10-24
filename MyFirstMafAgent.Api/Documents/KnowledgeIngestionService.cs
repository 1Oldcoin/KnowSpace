using System.Diagnostics;
using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Models;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using UglyToad.PdfPig;

namespace MyFirstMafAgent.Api.Documents;

public sealed class KnowledgeIngestionService(
    LocalDocumentStore documentStore,
    KnowledgeBaseStore knowledgeBase,
    OpenAiCompatibleModelClient modelClient,
    SummaryService summaryService,
    WhisperTranscriptionService whisperTranscriber,
    IWebHostEnvironment environment,
    IOptions<MediaOptions> mediaOptions,
    ILogger<KnowledgeIngestionService> logger)
{
    private const int ChunkSize = 1400;
    private const int ChunkOverlap = 180;
    private const int SummaryPartSize = 6000;

    public async Task<DocumentUploadResponse> UploadAsync(IFormFile file, CancellationToken cancellationToken)
        => await UploadAsync(file, KnowledgeBaseStore.DefaultKnowledgeBaseId, cancellationToken);

    public async Task<DocumentUploadResponse> UploadAsync(IFormFile file, Guid knowledgeBaseId, CancellationToken cancellationToken)
    {
        if (await knowledgeBase.GetKnowledgeBaseAsync(knowledgeBaseId, cancellationToken) is null)
            throw new ArgumentException("The selected knowledge base does not exist.", nameof(knowledgeBaseId));

        var stored = await documentStore.SaveAsync(file, knowledgeBaseId, cancellationToken);
        var document = new KnowledgeDocument(stored.Id, knowledgeBaseId, stored.FileName, stored.Extension, stored.ContentType,
            stored.SizeBytes, stored.Path, "Queued");
        try
        {
            await knowledgeBase.AddDocumentAsync(document, cancellationToken);
        }
        catch
        {
            if (File.Exists(stored.Path)) File.Delete(stored.Path);
            throw;
        }

        logger.LogInformation("Queued document ingestion for {DocumentId} in knowledge base {KnowledgeBaseId}, {FileName}, {FileSizeBytes} bytes",
            stored.Id, knowledgeBaseId, stored.FileName, stored.SizeBytes);
        return new DocumentUploadResponse(stored.Id.ToString(), stored.FileName, stored.Extension, stored.SizeBytes, "Queued");
    }

    /// <summary>
    /// 用新内容替换已有资料的源文件并重新排队解析。
    ///
    /// <para>
    /// 复用既有链路：Worker 重新解析后由 <c>PublishChunksAsync</c> 生成新的内容版本，
    /// 旧版本标记为 Superseded，索引生命周期据此增量更新。资料 ID 保持不变，
    /// 因此外部的 <c>externalId → documentId</c> 映射不会断。
    /// </para>
    ///
    /// <para>返回 false 表示资料正在排队/处理中，调用方应稍后重试而不是覆盖。</para>
    /// </summary>
    public async Task<bool> ReplaceAsync(
        Guid documentId,
        IFormFile file,
        Guid knowledgeBaseId,
        CancellationToken cancellationToken)
    {
        if (await knowledgeBase.GetDocumentKnowledgeBaseIdAsync(documentId, cancellationToken) is not { } existingKnowledgeBase)
            throw new ArgumentException($"Document '{documentId}' does not exist.", nameof(documentId));

        if (existingKnowledgeBase != knowledgeBaseId)
            throw new ArgumentException(
                $"Document '{documentId}' belongs to knowledge base '{existingKnowledgeBase}', not '{knowledgeBaseId}'.",
                nameof(knowledgeBaseId));

        var previousPath = await knowledgeBase.GetDocumentStoragePathAsync(documentId, cancellationToken);
        var stored = await documentStore.SaveAsync(file, knowledgeBaseId, cancellationToken);

        var replaced = await knowledgeBase.ReplaceDocumentSourceAsync(
            documentId,
            stored.Path,
            stored.FileName,
            stored.ContentType,
            stored.SizeBytes,
            cancellationToken);

        if (!replaced)
        {
            // 资料正在处理中：丢弃刚写入的新文件，让下次同步重试。
            documentStore.DeleteStoredFile(stored.Path);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(previousPath)
            && !string.Equals(previousPath, stored.Path, StringComparison.OrdinalIgnoreCase))
        {
            documentStore.DeleteStoredFile(previousPath);
        }

        logger.LogInformation(
            "Replaced source of document {DocumentId} in knowledge base {KnowledgeBaseId}, {FileName}, {FileSizeBytes} bytes",
            documentId, knowledgeBaseId, stored.FileName, stored.SizeBytes);

        return true;
    }

    public async Task ProcessAsync(QueuedDocument file, CancellationToken cancellationToken)
    {
        try
        {
            string text;
            if (LocalDocumentStore.IsVideoExtension(file.Extension))
            {
                var video = await ProcessVideoAsync(file, cancellationToken);
                var transcript = FormatTranscript(video.Transcript);
                var sourceSections = new List<string>();
                if (!string.IsNullOrWhiteSpace(transcript)) sourceSections.Add($"语音转写：\n{transcript}");
                if (video.FrameDescriptions.Count > 0)
                    sourceSections.Add($"关键画面：\n{string.Join("\n", video.FrameDescriptions)}");
                var summarySource = string.Join("\n\n", sourceSections);
                if (string.IsNullOrWhiteSpace(summarySource))
                    throw new InvalidOperationException("视频中没有可用于总结的语音或关键画面内容。");

                await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 65, "生成视频摘要", cancellationToken);
                // 摘要是"锦上添花"：它需要聊天模型，离线环境配不到。
                // 之前这里任其抛出，结果明明已经转写成功的字幕被一起丢掉，整篇资料变成 Failed。
                var summary = string.Empty;
                try
                {
                    summary = await SummarizeVideoAsync(summarySource, cancellationToken);
                    await knowledgeBase.SetDocumentSummaryAsync(file.Id, summary, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "Video summary skipped for {DocumentId}; publishing transcript only", file.Id);
                }

                text = string.IsNullOrWhiteSpace(summary)
                    ? $"视频转写：\n{summarySource}"
                    : $"视频摘要：\n{summary}\n\n{summarySource}";
            }
            else
            {
                await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 10, "提取文档内容", cancellationToken);
                text = await ExtractTextAsync(file, cancellationToken);
            }

            var parts = SplitIntoChunks(text);
            if (parts.Count == 0)
                throw new InvalidOperationException("No searchable text could be extracted from the file.");

            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 70, "写入内容版本", cancellationToken);
            var fileHash = await ComputeFileHashAsync(file.StoragePath, cancellationToken);
            var contentHash = ComputeSha256(text);
            var version = await knowledgeBase.PublishChunksAsync(
                file.Id, file.KnowledgeBaseId, fileHash, contentHash, parts, cancellationToken);
            logger.LogInformation("Published document {DocumentId} content version {Version} with {ChunkCount} chunks",
                file.Id, version, parts.Count);

            // 摘要与索引生命周期解耦：摘要失败只记录日志，不影响资料发布和后续索引构建。
            await summaryService.TryGenerateAsync(file.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Document ingestion failed for {DocumentId}, {FileName}", file.Id, file.FileName);
            await knowledgeBase.SetDocumentStatusAsync(file.Id, "Failed", exception.Message, cancellationToken);
        }
    }

    private async Task<string> ExtractTextAsync(QueuedDocument file, CancellationToken cancellationToken)
    {
        switch (file.Extension)
        {
            case ".txt":
            case ".md":
                return await File.ReadAllTextAsync(file.StoragePath, cancellationToken);
            case ".docx":
                using (var document = WordprocessingDocument.Open(file.StoragePath, false))
                    return document.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
            case ".pdf":
                using (var document = PdfDocument.Open(file.StoragePath))
                    return string.Join("\n", document.GetPages().Select(page => page.Text));
            case ".html":
            case ".htm":
                return await HtmlTextExtractor.ExtractTextFromFileAsync(file.StoragePath, cancellationToken);
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".webp":
            case ".gif":
                return await modelClient.DescribeImageAsync(file.StoragePath, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported file type: {file.Extension}");
        }
    }

    private async Task<VideoContent> ProcessVideoAsync(QueuedDocument file, CancellationToken cancellationToken)
    {
        var media = mediaOptions.Value;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MyFirstMafAgent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var audioPath = Path.Combine(tempDirectory, "audio.wav");
        try
        {
            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 10, "提取视频音轨", cancellationToken);
            var ffmpegPath = GetFfmpegPath(media.FfmpegPath);
            await RunProcessAsync(ffmpegPath,
                ["-y", "-i", file.StoragePath, "-vn", "-ac", "1", "-ar", "16000", audioPath], cancellationToken);

            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 35, "Whisper 语音转写", cancellationToken);
            var transcript = await whisperTranscriber.TranscribeAsync(audioPath, cancellationToken);
            var frameDescriptions = media.EnableVideoFrameAnalysis
                ? await DescribeKeyFramesAsync(file, tempDirectory, ffmpegPath, media, cancellationToken)
                : [];
            return new VideoContent(transcript, frameDescriptions);
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private async Task<List<string>> DescribeKeyFramesAsync(
        QueuedDocument file,
        string tempDirectory,
        string ffmpegPath,
        MediaOptions media,
        CancellationToken cancellationToken)
    {
        await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 50, "抽取关键画面", cancellationToken);
        var framesDirectory = Path.Combine(tempDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);
        var framePattern = Path.Combine(framesDirectory, "frame-%05d.jpg");
        await RunProcessAsync(ffmpegPath,
            ["-y", "-i", file.StoragePath, "-vf", $"fps=1/{media.FrameSampleIntervalSeconds},scale=768:-2",
                "-frames:v", media.MaxVideoFrames.ToString(), "-q:v", "5", framePattern], cancellationToken);

        var frames = Directory.GetFiles(framesDirectory, "frame-*.jpg")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(media.MaxVideoFrames)
            .ToArray();
        var descriptions = new List<string>(frames.Length);
        for (var index = 0; index < frames.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = await modelClient.DescribeImageAsync(frames[index], cancellationToken);
            var timestamp = TimeSpan.FromSeconds((long)index * media.FrameSampleIntervalSeconds);
            descriptions.Add($"[{FormatTime(timestamp)}] {description.Trim()}");
            if (index == 0 || (index + 1) % 5 == 0 || index == frames.Length - 1)
            {
                var progress = frames.Length == 0 ? 60 : 50 + (int)(10d * (index + 1) / frames.Length);
                await knowledgeBase.UpdateDocumentProgressAsync(file.Id, progress, "分析关键画面", cancellationToken);
            }
        }
        return descriptions;
    }

    private async Task<string> SummarizeVideoAsync(string source, CancellationToken cancellationToken)
    {
        var parts = SplitText(source, SummaryPartSize, 0);
        var partialSummaries = new List<string>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var summary = await modelClient.CompleteChatAsync(
                "你是视频知识库整理助手。根据给定的语音转写和关键画面描述，用中文准确提炼内容；不要补充原文没有的信息。",
                $"请总结这段视频内容，保留核心主题、关键步骤、结论和重要名词。片段 {index + 1}/{parts.Count}：\n\n{parts[index]}",
                cancellationToken);
            partialSummaries.Add(summary.Trim());
        }

        if (partialSummaries.Count == 1) return partialSummaries[0];
        return await modelClient.CompleteChatAsync(
            "你是视频知识库整理助手。请把同一视频的分段摘要合并为一份完整、去重、结构清楚的中文摘要，不要臆测。",
            string.Join("\n\n", partialSummaries.Select((summary, index) => $"分段摘要 {index + 1}：\n{summary}")),
            cancellationToken);
    }

    private static string FormatTranscript(IReadOnlyList<WhisperTranscriptionSegment> segments) =>
        string.Join("\n", segments.Select(segment =>
            $"[{FormatTime(segment.Start)}-{FormatTime(segment.End)}] {segment.Text}"));

    private static string FormatTime(TimeSpan time)
    {
        var totalSeconds = Math.Max(0, (long)time.TotalSeconds);
        return $"{totalSeconds / 3600:00}:{totalSeconds % 3600 / 60:00}:{totalSeconds % 60:00}";
    }

    /// <summary>
    /// 定位 ffmpeg。
    ///
    /// <para>
    /// 优先用配置的路径；配置为空或文件不存在时按当前平台自动找
    /// （<c>ExternalTools/ffmpeg/{rid}/ffmpeg[.exe]</c> 等）；
    /// 最后回落到裸命令 <c>ffmpeg</c> 交给 PATH —— 容器镜像里 ffmpeg 通常由包管理器安装，
    /// 没必要再随包分发一份 100 MB 的副本。
    /// </para>
    /// </summary>
    private string GetFfmpegPath(string ffmpegPath)
    {
        var resolved = PlatformRuntime.ResolveTool(
            environment.ContentRootPath, ffmpegPath, "ExternalTools/ffmpeg", "ffmpeg");
        if (resolved is not null) return resolved;

        // 明确配置了一个不存在的绝对/相对路径时按原值返回，让真正的启动失败带上原始配置，
        // 而不是被静默改成 PATH 查找。
        if (!string.IsNullOrWhiteSpace(ffmpegPath)
            && (Path.IsPathRooted(ffmpegPath) || ffmpegPath.Contains('/') || ffmpegPath.Contains('\\')))
        {
            return PlatformRuntime.ToAbsolute(environment.ContentRootPath, ffmpegPath);
        }

        return PlatformRuntime.WithSuffix("ffmpeg");
    }

    private static async Task RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Could not start media processor '{executable}'.");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new InvalidOperationException($"Media processor '{executable}' was not found or could not start.", exception);
        }

        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        var error = await standardError;
        _ = await standardOutput;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Media processor '{executable}' failed: {Limit(error)}");
    }

    private static List<string> SplitIntoChunks(string text) => SplitText(text, ChunkSize, ChunkOverlap);

    private static List<string> SplitText(string text, int chunkSize, int overlap)
    {
        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > 0)
        {
            var length = Math.Min(chunkSize, remaining.Length);
            if (length < remaining.Length)
            {
                var boundary = remaining.LastIndexOfAny(['\n', '。', '！', '？', '.', '!', '?', ' '], length - 1, Math.Min(length, 300));
                if (boundary > chunkSize / 2) length = boundary + 1;
            }
            var chunk = remaining[..length].Trim();
            if (chunk.Length > 0) chunks.Add(chunk);
            if (length >= remaining.Length) break;
            remaining = remaining[Math.Max(1, length - overlap)..].TrimStart();
        }
        return chunks;
    }

    private sealed record VideoContent(
        IReadOnlyList<WhisperTranscriptionSegment> Transcript,
        IReadOnlyList<string> FrameDescriptions);

    private static string Limit(string text) => text.Length > 500 ? text[..500] : text;

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
