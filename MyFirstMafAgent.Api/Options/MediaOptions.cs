namespace MyFirstMafAgent.Api.Options;

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public string FfmpegPath { get; set; } = "ExternalTools/ffmpeg/ffmpeg.exe";
    public string WhisperModel { get; set; } = "Base";
    public string WhisperModelPath { get; set; } = "App_Data/models/ggml-base.bin";
    public bool EnableVideoFrameAnalysis { get; set; }
    public int FrameSampleIntervalSeconds { get; set; } = 30;
    public int MaxVideoFrames { get; set; } = 60;
}