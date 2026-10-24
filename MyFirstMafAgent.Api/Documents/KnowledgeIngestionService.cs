using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
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

    /// <summary>场景帧少于这个数就认为"场景检测在这段视频上不适用"，改用均匀采样。</summary>
    private const int MinimumSceneFrames = 2;

    private static readonly Regex ShowInfoTimePattern = new(
        @"pts_time:\s*(?<time>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            IReadOnlyList<string>? timelineChunks = null;
            if (LocalDocumentStore.IsVideoExtension(file.Extension))
            {
                var video = await ProcessVideoAsync(file, cancellationToken);
                var transcript = FormatTranscript(video.Transcript);
                var sourceSections = new List<string>();
                if (!string.IsNullOrWhiteSpace(transcript)) sourceSections.Add($"语音转写：\n{transcript}");
                if (video.Frames.Count > 0)
                    sourceSections.Add($"关键画面：\n{string.Join("\n", video.Frames.Select(frame => $"[{MediaTimeline.FormatTime(frame.Start)}] {frame.Text}"))}");
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

                // 时间轴切片：转写与画面描述按时间合并，每片都以 [hh:mm:ss] 开头，
                // 引用因此能直接定位到"第几分几秒"。摘要本身没有时间坐标，单独放前面。
                var segments = video.Transcript
                    .Select(segment => new MediaSegment(segment.Start, segment.Text))
                    .Concat(video.Frames)
                    .ToList();
                var timeline = MediaTimeline.BuildChunks(segments);
                timelineChunks = string.IsNullOrWhiteSpace(summary)
                    ? timeline
                    : [$"视频摘要：\n{summary}", .. timeline];

                text = string.IsNullOrWhiteSpace(summary)
                    ? $"视频转写：\n{summarySource}"
                    : $"视频摘要：\n{summary}\n\n{summarySource}";
            }
            else if (LocalDocumentStore.IsAudioExtension(file.Extension))
            {
                var audio = await ProcessAudioAsync(file, cancellationToken);
                text = audio.Text;
                timelineChunks = audio.Chunks;
            }
            else
            {
                await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 10, "提取文档内容", cancellationToken);
                text = await ExtractTextAsync(file, cancellationToken);
            }

            var parts = timelineChunks is { Count: > 0 } ? timelineChunks : SplitIntoChunks(text);
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
                return await DescribeImageSafelyAsync(file, cancellationToken);
            default:
                throw new InvalidOperationException($"Unsupported file type: {file.Extension}");
        }
    }

    /// <summary>
    /// 图片描述。
    ///
    /// <para>
    /// 视觉描述需要支持图片输入的聊天模型，而离线部署、未配置模型、或模型不支持视觉都会让它失败。
    /// 图片本身已经是“资产”（表情包、截图、扫描件），不能因为描述不出来就整篇 <c>Failed</c>——
    /// 那样用户连原图都看不到。所以失败时退化为一段可检索的兜底文本，并记一条 warning。
    /// </para>
    /// </summary>
    private async Task<string> DescribeImageSafelyAsync(QueuedDocument file, CancellationToken cancellationToken)
    {
        await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 30, "识别图片内容", cancellationToken);
        try
        {
            var description = await modelClient.DescribeImageAsync(file.StoragePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            throw new InvalidOperationException("The vision model returned an empty description.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception,
                "Image description skipped for {DocumentId}; storing the image without a description", file.Id);
            return $"图片资料：{file.FileName}（未生成图片描述：视觉模型不可用）";
        }
    }

    /// <summary>
    /// 音频（语音、录音、表情包配套音效）→ 归一化采样率 → Whisper 转写 → 可选摘要。
    ///
    /// <para>
    /// 与视频分开的理由：视频还要抽画面、还要描述帧，音频只需要音轨。
    /// 但两者共用同一条归一化命令（单声道 16 kHz），因为 Whisper 就吃这个格式，
    /// 直接喂 mp3/m4a 会随格式与环境变化而失败。
    /// </para>
    ///
    /// <para>没有语音内容（纯音乐/环境音）时不报错，而是发布一条可检索的兜底文本——用户要的是“能存下来”。</para>
    /// </summary>
    private async Task<(string Text, IReadOnlyList<string>? Chunks)> ProcessAudioAsync(QueuedDocument file, CancellationToken cancellationToken)
    {
        var media = mediaOptions.Value;
        var tempDirectory = Path.Combine(Path.GetTempPath(), "MyFirstMafAgent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var audioPath = Path.Combine(tempDirectory, "audio.wav");
        try
        {
            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 15, "提取音频", cancellationToken);
            var ffmpegPath = GetFfmpegPath(media.FfmpegPath);
            await RunProcessAsync(ffmpegPath,
                ["-y", "-i", file.StoragePath, "-vn", "-ac", "1", "-ar", "16000", audioPath], cancellationToken);

            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 40, "Whisper 语音转写", cancellationToken);
            var segments = await whisperTranscriber.TranscribeAsync(audioPath, cancellationToken);
            var transcript = FormatTranscript(segments);

            if (string.IsNullOrWhiteSpace(transcript))
                return ($"音频资料：{file.FileName}（未检测到语音内容）", null);

            await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 65, "生成音频摘要", cancellationToken);
            var summary = string.Empty;
            try
            {
                summary = await SummarizeAudioAsync(transcript, cancellationToken);
                await knowledgeBase.SetDocumentSummaryAsync(file.Id, summary, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception,
                    "Audio summary skipped for {DocumentId}; publishing transcript only", file.Id);
            }

            // 与视频一致：转写按时间轴切片，引用才能定位到具体秒数。
            var timeline = MediaTimeline.BuildChunks(segments
                .Select(segment => new MediaSegment(segment.Start, segment.Text))
                .ToList());
            var chunks = string.IsNullOrWhiteSpace(summary)
                ? timeline
                : [$"音频摘要：\n{summary}", .. timeline];

            var text = string.IsNullOrWhiteSpace(summary)
                ? $"音频转写：\n{transcript}"
                : $"音频摘要：\n{summary}\n\n音频转写：\n{transcript}";
            return (text, chunks);
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private async Task<string> SummarizeAudioAsync(string transcript, CancellationToken cancellationToken)
    {
        var parts = SplitText(transcript, SummaryPartSize, 0);
        var partialSummaries = new List<string>(parts.Count);
        for (var index = 0; index < parts.Count; index++)
        {
            var summary = await modelClient.CompleteChatAsync(
                "你是音频知识库整理助手。根据给定的语音转写，用中文准确提炼内容；不要补充原文没有的信息。",
                $"请总结这段音频内容，保留核心主题、结论和重要名词。片段 {index + 1}/{parts.Count}：\n\n{parts[index]}",
                cancellationToken);
            partialSummaries.Add(summary.Trim());
        }

        if (partialSummaries.Count == 1) return partialSummaries[0];
        return await modelClient.CompleteChatAsync(
            "你是音频知识库整理助手。请把同一段音频的分段摘要合并为一份完整、去重、结构清楚的中文摘要，不要臆测。",
            string.Join("\n\n", partialSummaries.Select((summary, index) => $"分段摘要 {index + 1}：\n{summary}")),
            cancellationToken);
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
            var frames = media.EnableVideoFrameAnalysis
                ? await DescribeKeyFramesAsync(file, tempDirectory, ffmpegPath, media, cancellationToken)
                : [];
            return new VideoContent(transcript, frames);
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 抽关键画面并让视觉模型描述。
    ///
    /// <para>
    /// 两件事必须同时成立才算“能用画面内容检索”：<b>时间戳要准</b>、<b>画面要有变化的地方</b>。
    /// 所以先按场景切换抽（<c>select='gt(scene,阈值)'</c>），再用 <c>showinfo</c> 把 ffmpeg
    /// 真实输出的 <c>pts_time</c> 读回来——按"第几个文件 × 固定间隔"推算在场景检测下是错的
    /// （帧会被大量丢弃，序号与时间不成比例）。
    /// </para>
    ///
    /// <para>
    /// 抽不到足够场景帧时（幻灯片、固定机位、纯色背景）退化成均匀采样，否则整段视频一片空白。
    /// 视觉模型不可用时只记 warning 并返回已拿到的部分：<b>转写已经成功，不能因为描述失败让整篇资料变 Failed</b>。
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<MediaSegment>> DescribeKeyFramesAsync(
        QueuedDocument file,
        string tempDirectory,
        string ffmpegPath,
        MediaOptions media,
        CancellationToken cancellationToken)
    {
        await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 45, "抽取关键画面", cancellationToken);

        var framesDirectory = Path.Combine(tempDirectory, "frames");
        Directory.CreateDirectory(framesDirectory);

        var frames = new List<(string Path, TimeSpan Start)>();

        // 1) 场景切换优先。
        var sceneFrames = await TryExtractFramesAsync(
            file, ffmpegPath, framesDirectory, media,
            $"select='gt(scene,{media.FrameSceneThreshold.ToString(CultureInfo.InvariantCulture)})',showinfo,scale=768:-2",
            cancellationToken);
        if (sceneFrames.Count >= MinimumSceneFrames)
        {
            frames = sceneFrames;
        }
        else
        {
            // 2) 退化为均匀采样。先清干净上一次的残留，否则序号会混在一起。
            foreach (var existing in Directory.GetFiles(framesDirectory, "frame-*.jpg"))
            {
                try { File.Delete(existing); } catch (IOException) { }
            }

            frames = await TryExtractFramesAsync(
                file, ffmpegPath, framesDirectory, media,
                $"fps=1/{media.FrameSampleIntervalSeconds},showinfo,scale=768:-2",
                cancellationToken);
            logger.LogInformation(
                "Scene detection produced {SceneFrames} frame(s) for {DocumentId}; fell back to uniform sampling ({Sampled} frame(s))",
                sceneFrames.Count, file.Id, frames.Count);
        }

        if (frames.Count == 0) return [];

        await knowledgeBase.UpdateDocumentProgressAsync(file.Id, 55, "分析关键画面", cancellationToken);

        var descriptions = new List<MediaSegment>(frames.Count);
        var visionUnavailable = false;

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visionUnavailable) break;

            try
            {
                var description = await modelClient.DescribeImageAsync(frames[index].Path, cancellationToken);
                if (string.IsNullOrWhiteSpace(description)) continue;
                descriptions.Add(new MediaSegment(frames[index].Start, description.Trim()));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 视觉模型通常要么全能用要么全不能用；第一帧失败就不再打 N 次同样的请求。
                visionUnavailable = true;
                logger.LogWarning(exception,
                    "Vision model unavailable for {DocumentId}; keyframes stored without descriptions", file.Id);
            }

            if (index == 0 || (index + 1) % 5 == 0 || index == frames.Count - 1)
            {
                var progress = 55 + (int)(10d * (index + 1) / frames.Count);
                await knowledgeBase.UpdateDocumentProgressAsync(file.Id, progress, "分析关键画面", cancellationToken);
            }
        }

        if (visionUnavailable && descriptions.Count == 0)
            logger.LogInformation(
                "No keyframe descriptions for {DocumentId}; the video stays searchable by transcript only", file.Id);

        return descriptions;
    }

    /// <summary>
    /// 跑一次抽帧，返回"文件 + 时间点"。任何失败（滤镜不支持、编解码器异常）都只记 warning 并返回空，
    /// 由调用方决定是否退化——绝不能让抽帧失败把整段视频的转写一起带走。
    ///
    /// <para>
    /// <paramref name="filter"/> 是**滤镜链本身**，不包含 <c>-vf</c>：外置进程用 ArgumentList 逐个入参，
    /// 把 <c>"-vf ..."</c> 拼成一个字符串会变成 ffmpeg 不认识的单一选项。
    /// 单引号要保留在表达式里，它属于 filtergraph 语法（<c>gt(scene,0.25)</c> 的逗号不能当作分隔符）。
    /// </para>
    /// </summary>
    private async Task<List<(string Path, TimeSpan Start)>> TryExtractFramesAsync(
        QueuedDocument file,
        string ffmpegPath,
        string framesDirectory,
        MediaOptions media,
        string filter,
        CancellationToken cancellationToken)
    {
        var pattern = Path.Combine(framesDirectory, "frame-%05d.jpg");
        string standardError;
        try
        {
            standardError = await RunProcessAsync(ffmpegPath,
                ["-y", "-i", file.StoragePath, "-vf", filter, "-fps_mode", "vfr",
                    "-frames:v", media.MaxVideoFrames.ToString(CultureInfo.InvariantCulture), "-q:v", "5", pattern],
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Keyframe extraction failed for {DocumentId}", file.Id);
            return [];
        }

        var timestamps = ParseShowInfoTimes(standardError);
        var files = Directory.GetFiles(framesDirectory, "frame-*.jpg")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(media.MaxVideoFrames)
            .ToList();

        var result = new List<(string, TimeSpan)>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            // showinfo 与输出文件一一对应；对不上时按采样间隔兜底，宁可略偏也不能没有时间点。
            var start = index < timestamps.Count
                ? timestamps[index]
                : TimeSpan.FromSeconds((index + 1) * media.FrameSampleIntervalSeconds);
            result.Add((files[index], start));
        }

        return result;
    }

    /// <summary>从 ffmpeg <c>showinfo</c> 的 stderr 里按顺序取出 <c>pts_time</c>。</summary>
    internal static IReadOnlyList<TimeSpan> ParseShowInfoTimes(string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError)) return [];

        var times = new List<TimeSpan>();
        foreach (var line in standardError.Split('\n'))
        {
            var match = ShowInfoTimePattern.Match(line);
            if (!match.Success) continue;
            if (!double.TryParse(match.Groups["time"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            if (value < 0 || double.IsNaN(value) || double.IsInfinity(value)) continue;
            times.Add(TimeSpan.FromSeconds(value));
        }

        return times;
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
            $"[{MediaTimeline.FormatTime(segment.Start)}-{MediaTimeline.FormatTime(segment.End)}] {segment.Text}"));

    private static string FormatTime(TimeSpan time) => MediaTimeline.FormatTime(time);

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

    /// <summary>跑一个外部进程并返回它的 stderr（抽帧时需要从 showinfo 里读时间点）。</summary>
    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
            throw new InvalidOperationException($"Media processor '{executable}' exited with {process.ExitCode}: {Tail(error)}");
        return error;
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
        IReadOnlyList<MediaSegment> Frames);

    /// <summary>
    /// 取 stderr 的**尾部**：ffmpeg/whisper 把版本号、编译器参数、库列表全打在开头，
    /// 真正的原因永远在最后几行。截前面 500 个字符只会让人看到一屏横幅而看不到错误。
    /// </summary>
    private static string Tail(string text) => text.Length > 800 ? "…" + text[^800..] : text;

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
