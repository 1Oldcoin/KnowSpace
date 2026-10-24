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
    /// <summary>
    /// 是否对视频做关键画面分析（抽帧 + 视觉模型描述）。
    ///
    /// <para>
    /// 默认开启：画面内容不进索引，视频就只能"按字幕搜"，而很多视频的关键信息在画面里。
    /// 视觉模型不可用时不会让资料失败——只记 warning，转写照常入库。
    /// </para>
    /// </summary>
    public bool EnableVideoFrameAnalysis { get; set; } = true;

    /// <summary>均匀采样回退时每帧间隔（秒）。</summary>
    public int FrameSampleIntervalSeconds { get; set; } = 30;

    /// <summary>单次最多分析多少帧（每帧一次视觉模型调用，是真实成本）。</summary>
    public int MaxVideoFrames { get; set; } = 60;

    /// <summary>
    /// 场景切换阈值：<c>select='gt(scene,阈值)'</c>。越小抽得越多。
    /// 0.25 对应"镜头明显变化"，对讲课录像/幻灯片偏敏感，对电影偏粗。
    /// </summary>
    public double FrameSceneThreshold { get; set; } = 0.25;
}