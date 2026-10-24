namespace MyFirstMafAgent.Api.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "App_Data/uploads";
    public long MaxFileSizeBytes { get; set; } = 52_428_800;
    public long MaxVideoFileSizeBytes { get; set; } = 5_368_709_120;

    /// <summary>
    /// 音频文件上限（字节）。语音消息、录音、播客都可能很大，所以单独给一档，
    /// 不能跟普通文档共用 50 MB——否则一条长语音就会被拒。
    /// </summary>
    public long MaxAudioFileSizeBytes { get; set; } = 314_572_800;

    public int UploadChunkSizeBytes { get; set; } = 16_777_216;
}
