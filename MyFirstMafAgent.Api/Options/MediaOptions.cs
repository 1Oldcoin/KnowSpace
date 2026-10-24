namespace MyFirstMafAgent.Api.Options;

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// ffmpeg 可执行文件路径。
    ///
    /// <para>
    /// 留空（推荐）= 按当前平台自动定位：
    /// <c>ExternalTools/ffmpeg/{rid}/ffmpeg[.exe]</c> → <c>ExternalTools/ffmpeg/ffmpeg[.exe]</c>
    /// → PATH 里的 <c>ffmpeg</c>（容器里通常用 apt/brew 装，不用随包分发）。
    /// </para>
    /// </summary>
    public string FfmpegPath { get; set; } = "";
    public string WhisperModel { get; set; } = "Base";
    public string WhisperModelPath { get; set; } = "App_Data/models/ggml-base.bin";

    /// <summary>
    /// 模型缺失时是否允许自动联网下载（默认允许，保持升级前的行为）。
    /// 离线部署应设为 false：此时缺模型会立刻给出"去设置页导入"的明确提示，而不是挂在一个下载请求上。
    /// </summary>
    public bool AutoDownloadWhisperModel { get; set; } = true;

    /// <summary>导入本地 Whisper 模型的体积上限。base 约 141 MB，large-v3 约 3 GB。</summary>
    public long MaxWhisperModelBytes { get; set; } = 3L * 1024 * 1024 * 1024;
    public bool EnableVideoFrameAnalysis { get; set; }
    public int FrameSampleIntervalSeconds { get; set; } = 30;
    public int MaxVideoFrames { get; set; } = 60;
}