using MyFirstMafAgent.Api.Application.Jobs;
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
                && options.MaxVideoFrames is >= 1 and <= 240
                && options.FrameSceneThreshold is > 0 and < 1,
                "Video frame sampling must be between 1 and 3,600 seconds with 1 to 240 frames per video, and the scene threshold must be between 0 and 1 (exclusive).")
            .ValidateOnStart();
        services.AddOptions<IndexingOptions>()
            .BindConfiguration(IndexingOptions.SectionName)
            .Validate(options => options.RollbackWindowMinutes > 0
                && options.OutboxRetentionHours > 0
                && options.PollIntervalSeconds > 0
                && options.QualityProbeCount >= 0,
                "Indexing windows, retention, poll interval, and quality probe count must be positive.")
            .ValidateOnStart();
        services.AddOptions<RetrievalOptions>()
            .BindConfiguration(RetrievalOptions.SectionName)
            .Validate(options => options.RrfK >= 1
                && options.CandidateMultiplier is >= 1 and <= 10
                && options.MaxCandidates is >= 1 and <= 200
                && options.RerankCandidateLimit is >= 1 and <= 200
                && options.RerankLexicalWeight is >= 0 and <= 1
                && options.AbstainMinScore is >= 0 and <= 1,
                "Retrieval RRF k, candidate multiplier, candidate ceiling, rerank candidate limit, rerank lexical weight and abstain threshold must be within range.")
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
            .Validate(options => !options.EnableServer
                || (!string.IsNullOrWhiteSpace(options.ServerPath)
                    && options.ServerPath.StartsWith('/')
                    && options.DefaultRequestsPerMinute is >= 0 and <= 100_000
                    && options.MaxAgentClients is >= 1 and <= 1_000
                    && options.MaxRequestBodyBytes is >= 65_536 and <= 67_108_864),
                "Mcp:ServerPath must start with '/'; rate limit, client cap and body size must be in range.")
            .ValidateOnStart();
        services.AddOptions<WebSearchOptions>()
            .BindConfiguration(WebSearchOptions.SectionName)
            .Validate(options => options.MaxResults is >= 1 and <= 10
                && options.TimeoutSeconds is >= 1 and <= 120,
                "WebSearch max results must be between 1 and 10 and the timeout must be between 1 and 120 seconds.")
            .ValidateOnStart();
        // 服务端部署安全：令牌长度、代理列表与失败闸门必须有效。
        services.AddOptions<ServerSecurityOptions>()
            .BindConfiguration(ServerSecurityOptions.SectionName)
            .Validate(options => (string.IsNullOrWhiteSpace(options.ApiToken) || options.ApiToken.Trim().Length >= 16)
                && options.FailedAuthLimitPerMinute is >= 0 and <= 100_000
                && options.FailedAuthBlockSeconds is >= 0 and <= 86_400
                && options.KnownProxies.All(proxy => System.Net.IPAddress.TryParse(proxy?.Trim(), out _))
                && IsValidPublicBaseUrl(options.PublicBaseUrl),
                "Server:ApiToken must be empty or at least 16 characters; the failed-auth limit, block seconds and every KnownProxies entry must be valid; Server:PublicBaseUrl must be empty or an absolute http/https URL without query or fragment.")
            .ValidateOnStart();
        // 插件运行时：目录、超时与重启退避必须有效，否则启动即失败。
        services.AddOptions<PluginOptions>()
            .BindConfiguration(PluginOptions.SectionName)
            .Validate(options => !options.Enabled
                || (!string.IsNullOrWhiteSpace(options.RootPath)
                    && options.StartupTimeoutSeconds is >= 5 and <= 600
                    && options.MaxInvocationTimeoutMs is >= 1000 and <= 600_000
                    && options.HealthIntervalSeconds is >= 5 and <= 3600
                    && options.MaxRestartBackoffSeconds is >= 5 and <= 3600
                    && options.MaxIngestionItemsPerBatch is >= 1 and <= 1000
                    && options.DefaultMaxDocumentsPerPlugin is >= 0 and <= 1_000_000
                    && options.KeepVersions is >= 1 and <= 20
                    && options.MaxPackageBytes is >= 1_048_576
                    && options.DrainTimeoutSeconds is >= 0 and <= 600
                    && options.MinScheduledSyncIntervalSeconds is >= 30 and <= 86_400
                    && options.IntegrityPolicy is not null
                    && options.IntegrityPolicy.Trim().ToLowerInvariant() is "off" or "warn" or "enforce"),
                "Plugins: root path, timeouts, backoff, batch size, keep-versions, package size, drain timeout, sync interval and integrity policy (off|warn|enforce) must be valid.")
            .ValidateOnStart();
        // 任务调度：并发度、租约、重试与保留期都必须在合理区间，否则运行期才暴露会很脆。
        services.AddOptions<JobOptions>()
            .BindConfiguration(JobOptions.SectionName)
            .Validate(options => !options.Enabled
                || (options.MaxConcurrentJobs is >= 1 and <= 32
                    && options.PollIntervalMs is >= 200 and <= 60_000
                    && options.LeaseSeconds is >= 30 and <= 3600
                    && options.MaxAttempts is >= 1 and <= 10
                    && options.RetryBaseSeconds is >= 1 and <= 3600
                    && options.RetryMaxSeconds >= options.RetryBaseSeconds
                    && options.RetentionHours is >= 1 and <= 8760
                    && options.MaxQueuedJobs is >= 100 and <= 1_000_000
                    && options.FeederIntervalSeconds is >= 0 and <= 600
                    && options.PurgeIntervalMinutes is >= 1 and <= 1440),
                "Jobs: concurrency, lease, attempts, backoff, retention, queue cap and intervals must be valid.")
            .ValidateOnStart();
        services.AddOptions<PortablePostgresOptions>()
            .BindConfiguration(PortablePostgresOptions.SectionName)
            .Validate(options => !options.Enabled
                || (options.Port is >= 1024 and <= 65535
                    && options.StartupTimeoutSeconds is >= 10 and <= 900
                    && !string.IsNullOrWhiteSpace(options.Username)
                    && !string.IsNullOrWhiteSpace(options.Database)),
                "PortablePostgres 的端口、超时、用户名与数据库名必须有效。")
            // 连接池上限只用来封顶，不设下限会让人把它当成「调优旋钮」往上调——
            // 调大只会让泄漏更快吃掉 max_connections，缓过来更慢。
            .Validate(options => options.MaxPoolSize is >= 1 and <= 100,
                "PortablePostgres:MaxPoolSize 必须在 1~100 之间（默认 16）。这个值是上限而非目标，调大没有好处。")
            .Validate(options => options.ConnectionIdleLifetimeSeconds is >= 0 and <= 86400,
                "PortablePostgres:ConnectionIdleLifetimeSeconds 必须在 0~86400 之间（默认 300，0 = 永不回收）。")
            .Validate(options => options.ConnectionPruningIntervalSeconds is >= 1 and <= 3600,
                "PortablePostgres:ConnectionPruningIntervalSeconds 必须在 1~3600 之间（默认 60）。")
            .ValidateOnStart();
    }

    /// <summary>
    /// <c>Server:PublicBaseUrl</c> 允许留空；填了就必须是「绝对 http/https 地址」，
    /// 且不带查询串或片段——它会被拼成 <c>{base}/mcp</c> 直接粘进客户端配置，
    /// 多一个 <c>?x=1</c> 就会生成连不上的地址。
    /// </summary>
    private static bool IsValidPublicBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;

        return System.Uri.TryCreate(value.Trim(), System.UriKind.Absolute, out var uri)
            && (uri.Scheme == System.Uri.UriSchemeHttp || uri.Scheme == System.Uri.UriSchemeHttps)
            && uri.Host.Length > 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;
    }
}
