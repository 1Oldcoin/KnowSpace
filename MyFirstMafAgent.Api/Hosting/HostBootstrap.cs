using Microsoft.AspNetCore.Http.Features;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Infrastructure.Logging;

namespace MyFirstMafAgent.Api.Hosting;

/// <summary>
/// 主机级基础设施：数据目录重定向、日志、Kestrel/HTTP 限制与 CORS。
/// 只处理“进程/宿主”关注点，不注册任何业务服务。
/// </summary>
public static class HostBootstrap
{
    /// <summary>
    /// 把可写数据目录重定向到用户目录。安装版程序目录会在更新时被整体替换，
    /// 因此上传、Agent 工作区、模型缓存必须可配置到独立的用户数据根。
    /// </summary>
    public static void ApplyDataRoot(WebApplicationBuilder builder, string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) return;

        var data = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(data);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:RootPath"] = Path.Combine(data, "uploads"),
            ["Agent:WorkspaceRootPath"] = Path.Combine(data, "agent-workspace"),
            ["Embedding:ModelRootPath"] = Path.Combine(data, "models", "embedding"),
            ["Media:WhisperModelPath"] = Path.Combine(data, "models", "ggml-base.bin")
        });
    }

    /// <summary>
    /// JSON 控制台日志 + 可选文件日志。返回的文件 Provider 供启动阶段直接写日志。
    /// </summary>
    public static FileLoggerProvider? ConfigureLogging(WebApplicationBuilder builder, bool enableFileLogging)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "O";
        });

        if (!enableFileLogging) return null;

        try
        {
            var provider = new FileLoggerProvider(AppPaths.LogsDirectory, "api");
            builder.Logging.AddProvider(provider);
            return provider;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[startup] 文件日志不可用（{exception.Message}），仅输出到控制台。");
            return null;
        }
    }

    /// <summary>请求体上限、ProblemDetails、OpenAPI 与 CORS。</summary>
    public static void ConfigureHttp(WebApplicationBuilder builder)
    {
        var maxFileRequestSize = builder.Configuration.GetValue<long?>("Storage:MaxFileSizeBytes") ?? 52_428_800;
        var uploadChunkSize = builder.Configuration.GetValue<int?>("Storage:UploadChunkSizeBytes") ?? 16_777_216;
        var maxEmbeddingModelSize = builder.Configuration.GetValue<long?>("Embedding:MaxModelFileSizeBytes") ?? 1_073_741_824;
        // Whisper 模型（large-v3 约 3 GB）也算请求体，不能只按 Embedding 的 1 GB 算。
        var maxWhisperModelSize = builder.Configuration.GetValue<long?>("Media:MaxWhisperModelBytes") ?? 3_221_225_472;
        var maxRequestBodySize = Math.Max(Math.Max(maxFileRequestSize, uploadChunkSize),
            Math.Max(maxEmbeddingModelSize, maxWhisperModelSize)) + 1_048_576;

        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = maxRequestBodySize);
        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();
        builder.Services.AddCors(options =>
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:5173"];
            options.AddPolicy("Vue", policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod());
        });
    }
}
