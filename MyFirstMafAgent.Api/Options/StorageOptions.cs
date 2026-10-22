namespace MyFirstMafAgent.Api.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "App_Data/uploads";
    public long MaxFileSizeBytes { get; set; } = 52_428_800;
    public long MaxVideoFileSizeBytes { get; set; } = 5_368_709_120;
    public int UploadChunkSizeBytes { get; set; } = 16_777_216;
}
