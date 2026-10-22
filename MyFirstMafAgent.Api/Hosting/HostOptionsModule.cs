using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 配置项绑定与启动校验。配置不合法时在启动阶段直接失败，避免运行期出现半配置状态。
/// </summary>
public static class HostOptionsModule
{
    public static void AddValidatedOptions(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.SectionName)
            .Validate(options =>
            {
                var selected = options.UseCloudApi ? options.Online : options.Local;
                return Uri.TryCreate(selected.Endpoint, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(selected.Model);
            }, "The selected Llm endpoint and model must be configured.")
            .ValidateOnStart();
        services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .Validate(options => options.MaxFileSizeBytes > 0
                && options.MaxVideoFileSizeBytes > 0
                && options.UploadChunkSizeBytes is >= 1_048_576 and <= 67_108_864,
                "Storage size limits must be positive and upload chunks must be between 1 MiB and 64 MiB.")
            .ValidateOnStart();
        services.AddOptions<EmbeddingOptions>()
            .BindConfiguration(EmbeddingOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ModelDirectory)
                && options.Dimensions > 0
                && options.MaxSequenceLength is >= 2 and <= 512
                && options.IntraOpNumThreads >= 0,
                "Embedding model directory, dimensions, sequence length, and thread count must be valid.")
            .ValidateOnStart();
        services.AddOptions<MediaOptions>()
            .BindConfiguration(MediaOptions.SectionName)
            .Validate(options => options.FrameSampleIntervalSeconds is >= 1 and <= 3600
                && options.MaxVideoFrames is >= 1 and <= 240,
                "Video frame sampling must be between 1 and 3,600 seconds with 1 to 240 frames per video.")
            .ValidateOnStart();
        services.AddOptions<IndexingOptions>()
            .BindConfiguration(IndexingOptions.SectionName)
            .Validate(options => options.RollbackWindowMinutes > 0
                && options.OutboxRetentionHours > 0
                && options.PollIntervalSeconds > 0
                && options.QualityProbeCount >= 0,
                "Indexing windows, retention, poll interval, and quality probe count must be positive.")
            .ValidateOnStart();
        services.AddOptions<AgentOptions>()
            .BindConfiguration(AgentOptions.SectionName)
            .Validate(options => options.MaxToolIterations is >= 1 and <= 12
                && options.MaxReadChars > 0
                && options.MaxWriteChars > 0
                && options.MaxToolResultChars > 0
                && !string.IsNullOrWhiteSpace(options.WorkspaceRootPath)
                && options.FileRoots.All(root => !root.Enabled || !string.IsNullOrWhiteSpace(root.Path)),
                "Agent tool iteration limit and size limits must be positive; every enabled file root needs a path.")
            .ValidateOnStart();
        services.AddOptions<McpOptions>()
            .BindConfiguration(McpOptions.SectionName)
            .Validate(options => !options.EnableServer || (!string.IsNullOrWhiteSpace(options.ServerPath) && options.ServerPath.StartsWith('/')),
                "Mcp:ServerPath must start with '/'.")
            .ValidateOnStart();
        services.AddOptions<WebSearchOptions>()
            .BindConfiguration(WebSearchOptions.SectionName)
            .Validate(options => options.MaxResults is >= 1 and <= 10
                && options.TimeoutSeconds is >= 1 and <= 120,
                "WebSearch max results must be between 1 and 10 and the timeout must be between 1 and 120 seconds.")
            .ValidateOnStart();
        services.AddOptions<PortablePostgresOptions>()
            .BindConfiguration(PortablePostgresOptions.SectionName)
            .Validate(options => !options.Enabled
                || (options.Port is >= 1024 and <= 65535
                    && options.StartupTimeoutSeconds is >= 10 and <= 900
                    && !string.IsNullOrWhiteSpace(options.InstanceName)
                    && !string.IsNullOrWhiteSpace(options.Username)
                    && !string.IsNullOrWhiteSpace(options.Database)
                    && !string.IsNullOrWhiteSpace(options.ExecutablePath)),
                "PortablePostgres 的端口、超时、实例名、用户名、数据库名与可执行文件路径必须有效。")
            .ValidateOnStart();
    }
}
