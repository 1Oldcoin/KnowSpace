using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MyFirstMafAgent.Api.Agents;
using MyFirstMafAgent.Api.Agents.Tooling;
using MyFirstMafAgent.Api.Application.Avatar;
using MyFirstMafAgent.Api.Application.Knowledge;
using MyFirstMafAgent.Api.Application.Notes;
using MyFirstMafAgent.Api.Application.Settings;
using MyFirstMafAgent.Api.Application.Study;
using MyFirstMafAgent.Api.Application.Topics;
using MyFirstMafAgent.Api.Chats;
using MyFirstMafAgent.Api.Companions;
using MyFirstMafAgent.Api.Documents;
using MyFirstMafAgent.Api.Domain.Avatar;
using MyFirstMafAgent.Api.Domain.Knowledge;
using MyFirstMafAgent.Api.Domain.Notes;
using MyFirstMafAgent.Api.Domain.Study;
using MyFirstMafAgent.Api.Domain.Topics;
using MyFirstMafAgent.Api.Endpoints;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Infrastructure.Embedding;
using MyFirstMafAgent.Api.Infrastructure.Logging;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;
using MyFirstMafAgent.Api.Infrastructure.Settings;
using MyFirstMafAgent.Api.Mcp;
using MyFirstMafAgent.Api.Models;
using MyFirstMafAgent.Api.Options;
using MyFirstMafAgent.Api.Persistence;
using Scalar.AspNetCore;
using SqlSugar;

namespace MyFirstMafAgent.Api;

/// <summary>
/// 可复用的 Web 主机。控制台版（Program.cs）与 Photino 桌面版共用同一套 API 与静态文件托管逻辑。
/// </summary>
public static class ApiHost
{
    /// <summary>
    /// 构建 WebApplication（不会启动 Kestrel）。
    /// </summary>
    /// <param name="args">命令行参数。</param>
    /// <param name="urls">显式监听地址；桌面版用 http://127.0.0.1:0 让系统分配端口。</param>
    /// <param name="contentRoot">内容根目录；为空时自动解析。</param>
    /// <param name="enableFileLogging">是否把日志同时写入 %LOCALAPPDATA%\MyFirstMafAgent\logs。</param>
    /// <param name="dataRoot">
    /// 可写数据根目录。提供后，上传文件、Agent 工作区、模型缓存等相对路径配置会重定向到该目录，
    /// 适用于安装版（程序目录会在更新时被替换）。
    /// </param>
    public static async Task<WebApplication> BuildAsync(
        string[] args,
        string? urls = null,
        string? contentRoot = null,
        bool enableFileLogging = false,
        string? dataRoot = null)
    {
        var root = contentRoot ?? AppPaths.ResolveContentRoot();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = root,
            WebRootPath = Path.Combine(root, "wwwroot")
        });

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var data = Path.GetFullPath(dataRoot);
            Directory.CreateDirectory(data);
            // 安装版：程序目录会在更新时被整体替换，可写数据必须放在用户目录。
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:RootPath"] = Path.Combine(data, "uploads"),
                ["Agent:WorkspaceRootPath"] = Path.Combine(data, "agent-workspace"),
                ["Embedding:ModelRootPath"] = Path.Combine(data, "models", "embedding"),
                ["Media:WhisperModelPath"] = Path.Combine(data, "models", "ggml-base.bin")
            });
        }

        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "O";
        });

        FileLoggerProvider? fileLogger = null;
        if (enableFileLogging)
        {
            try
            {
                fileLogger = new FileLoggerProvider(AppPaths.LogsDirectory, "api");
                builder.Logging.AddProvider(fileLogger);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[startup] 文件日志不可用（{exception.Message}），仅输出到控制台。");
            }
        }

        void LogStartup(string message)
        {
            Console.WriteLine($"[startup] {message}");
            fileLogger?.Write($"{DateTimeOffset.Now:O} [startup] {message}");
        }

        var maxFileRequestSize = builder.Configuration.GetValue<long?>("Storage:MaxFileSizeBytes") ?? 52_428_800;
        var uploadChunkSize = builder.Configuration.GetValue<int?>("Storage:UploadChunkSizeBytes") ?? 16_777_216;
        var maxEmbeddingModelSize = builder.Configuration.GetValue<long?>("Embedding:MaxModelFileSizeBytes") ?? 1_073_741_824;
        var maxRequestBodySize = Math.Max(Math.Max(maxFileRequestSize, uploadChunkSize), maxEmbeddingModelSize) + 1_048_576;

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

        builder.Services.AddOptions<LlmOptions>()
            .BindConfiguration(LlmOptions.SectionName)
            .Validate(options =>
            {
                var selected = options.UseCloudApi ? options.Online : options.Local;
                return Uri.TryCreate(selected.Endpoint, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(selected.Model);
            }, "The selected Llm endpoint and model must be configured.")
            .ValidateOnStart();
        builder.Services.AddOptions<StorageOptions>()
            .BindConfiguration(StorageOptions.SectionName)
            .Validate(options => options.MaxFileSizeBytes > 0
                && options.MaxVideoFileSizeBytes > 0
                && options.UploadChunkSizeBytes is >= 1_048_576 and <= 67_108_864,
                "Storage size limits must be positive and upload chunks must be between 1 MiB and 64 MiB.")
            .ValidateOnStart();
        builder.Services.AddOptions<EmbeddingOptions>()
            .BindConfiguration(EmbeddingOptions.SectionName)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ModelDirectory)
                && options.Dimensions > 0
                && options.MaxSequenceLength is >= 2 and <= 512
                && options.IntraOpNumThreads >= 0,
                "Embedding model directory, dimensions, sequence length, and thread count must be valid.")
            .ValidateOnStart();
        builder.Services.AddOptions<MediaOptions>()
            .BindConfiguration(MediaOptions.SectionName)
            .Validate(options => options.FrameSampleIntervalSeconds is >= 1 and <= 3600
                && options.MaxVideoFrames is >= 1 and <= 240,
                "Video frame sampling must be between 1 and 3,600 seconds with 1 to 240 frames per video.")
            .ValidateOnStart();
        builder.Services.AddOptions<IndexingOptions>()
            .BindConfiguration(IndexingOptions.SectionName)
            .Validate(options => options.RollbackWindowMinutes > 0
                && options.OutboxRetentionHours > 0
                && options.PollIntervalSeconds > 0
                && options.QualityProbeCount >= 0,
                "Indexing windows, retention, poll interval, and quality probe count must be positive.")
            .ValidateOnStart();
        builder.Services.AddOptions<AgentOptions>()
            .BindConfiguration(AgentOptions.SectionName)
            .Validate(options => options.MaxToolIterations is >= 1 and <= 12
                && options.MaxReadChars > 0
                && options.MaxWriteChars > 0
                && options.MaxToolResultChars > 0
                && !string.IsNullOrWhiteSpace(options.WorkspaceRootPath)
                && options.FileRoots.All(root => !root.Enabled || !string.IsNullOrWhiteSpace(root.Path)),
                "Agent tool iteration limit and size limits must be positive; every enabled file root needs a path.")
            .ValidateOnStart();
        builder.Services.AddOptions<McpOptions>()
            .BindConfiguration(McpOptions.SectionName)
            .Validate(options => !options.EnableServer || (!string.IsNullOrWhiteSpace(options.ServerPath) && options.ServerPath.StartsWith('/')),
                "Mcp:ServerPath must start with '/'.")
            .ValidateOnStart();
        builder.Services.AddOptions<WebSearchOptions>()
            .BindConfiguration(WebSearchOptions.SectionName)
            .Validate(options => options.MaxResults is >= 1 and <= 10
                && options.TimeoutSeconds is >= 1 and <= 120,
                "WebSearch max results must be between 1 and 10 and the timeout must be between 1 and 120 seconds.")
            .ValidateOnStart();
        builder.Services.AddOptions<PortablePostgresOptions>()
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

        // 便携数据库：没有外部连接串时自动拉起 pg0，并激活 pgvector。
        PortablePostgresHost? portablePostgres = null;
        if (builder.Configuration.GetValue<bool?>($"{PortablePostgresOptions.SectionName}:Enabled") ?? true)
        {
            portablePostgres = await PortablePostgresHost.StartAsync(
                builder.Configuration, root, LogStartup);
            if (portablePostgres is not null)
            {
                builder.Configuration["ConnectionStrings:KnowledgeBase"] = portablePostgres.ConnectionString;
                LogStartup($"便携 PostgreSQL 已就绪：127.0.0.1:{portablePostgres.Port}/{portablePostgres.Database}");
            }
        }

        var connectionString = builder.Configuration.GetConnectionString("KnowledgeBase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set ConnectionStrings:KnowledgeBase (for example via ConnectionStrings__KnowledgeBase) before starting the API.");

        builder.Services.AddSingleton(_ => new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = DbType.PostgreSQL,
            IsAutoCloseConnection = true
        }));
        builder.Services.AddSingleton<KnowledgeBaseStore>();
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IDocumentIndexSource>(sp => sp.GetRequiredService<KnowledgeBaseStore>());
        builder.Services.AddSingleton<KnowledgeChatStore>();
        builder.Services.AddSingleton<CompanionStore>();

        // 模型规格、索引生命周期、事务性 Outbox 与摘要缓存。
        builder.Services.AddSingleton<SqlSugarEmbeddingSpecStore>();
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IEmbeddingSpecStore>(sp => sp.GetRequiredService<SqlSugarEmbeddingSpecStore>());
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IModelBindingStore>(sp => sp.GetRequiredService<SqlSugarEmbeddingSpecStore>());
        builder.Services.AddSingleton<SqlSugarIndexStore>();
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IKnowledgeIndexStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IIndexVectorStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IOutboxStore>(sp => sp.GetRequiredService<SqlSugarIndexStore>());
        builder.Services.AddSingleton<SqlSugarSummaryStore>();
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.ISummaryStore>(sp => sp.GetRequiredService<SqlSugarSummaryStore>());
        builder.Services.AddSingleton<MyFirstMafAgent.Api.Domain.Knowledge.IEmbeddingGeneratorProvider, EmbeddingModelRegistry>();
        builder.Services.AddSingleton<EmbeddingSpecService>();
        builder.Services.AddSingleton<RetrievalRouter>();
        builder.Services.AddSingleton<IndexLifecycleManager>();
        builder.Services.AddScoped<ISummaryModelClient, SummaryModelClient>();

        // DDD 分层：领域仓储端口 → 基础设施实现
        builder.Services.AddSingleton<SqlSugarNoteRepository>();
        builder.Services.AddSingleton<INoteRepository>(sp => sp.GetRequiredService<SqlSugarNoteRepository>());
        builder.Services.AddSingleton<SqlSugarTopicRepository>();
        builder.Services.AddSingleton<ITopicRepository>(sp => sp.GetRequiredService<SqlSugarTopicRepository>());
        builder.Services.AddSingleton<SqlSugarStudyRepository>();
        builder.Services.AddSingleton<IStudyRepository>(sp => sp.GetRequiredService<SqlSugarStudyRepository>());

        // 分身模块：画像、经历、行为、人际关系与未来轨迹推演。
        builder.Services.AddSingleton<SqlSugarAvatarRepository>();
        builder.Services.AddSingleton<IAvatarRepository>(sp => sp.GetRequiredService<SqlSugarAvatarRepository>());
        builder.Services.AddScoped<AvatarActivityRecorder>();
        builder.Services.AddScoped<AvatarService>();
        builder.Services.AddScoped<AvatarDigestService>();
        builder.Services.AddScoped<AvatarPersonaChatService>();
        builder.Services.AddScoped<AvatarSimulationService>();
        // 自由投喂：AI 整理 → 记忆卡 → 向量化。
        builder.Services.AddScoped<AvatarOrganizer>();
        builder.Services.AddScoped<AvatarMemoryIndexer>();
        builder.Services.AddScoped<AvatarMemoryRetriever>();
        builder.Services.AddScoped<AvatarIntakeService>();
        // v2：异步投喂、证据链、人格候选、反馈与评测。
        builder.Services.AddScoped<AvatarIngestionService>();
        builder.Services.AddScoped<AvatarInsightService>();
        builder.Services.AddScoped<AvatarFeedbackService>();
        builder.Services.AddScoped<AvatarEvalService>();
        builder.Services.AddSingleton<SqlSugarSettingsRepository>();
        builder.Services.AddSingleton<ISettingsRepository>(sp => sp.GetRequiredService<SqlSugarSettingsRepository>());
        builder.Services.AddSingleton<SqlSugarKnowledgeSearchRepository>();
        builder.Services.AddSingleton<IDocumentSearchRepository>(sp => sp.GetRequiredService<SqlSugarKnowledgeSearchRepository>());
        builder.Services.AddSingleton<SqlSugarDocumentTextRepository>();
        builder.Services.AddSingleton<IDocumentTextRepository>(sp => sp.GetRequiredService<SqlSugarDocumentTextRepository>());
        builder.Services.AddSingleton<IKnowledgeBaseDirectory, SqlSugarKnowledgeBaseDirectory>();

        builder.Services.AddSingleton<ILlmSettingsAccessor>(sp =>
            new LlmSettingsProvider(sp.GetRequiredService<IOptions<LlmOptions>>()));
        builder.Services.AddSingleton<IRuntimeSettingsProvider, RuntimeSettingsProvider>();
        builder.Services.AddSingleton<ChatHistoryParser>();
        builder.Services.AddSingleton<LocalDocumentStore>();
        builder.Services.AddSingleton<ResumableUploadService>();
        builder.Services.AddSingleton<WhisperTranscriptionService>();
        builder.Services.AddHttpClient<OpenAiCompatibleModelClient>(client => client.Timeout = TimeSpan.FromMinutes(5));
        builder.Services.AddHttpClient("embedding", client => client.Timeout = TimeSpan.FromMinutes(5));
        builder.Services.AddHttpClient(WebSearchToolSource.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddScoped<KnowledgeIngestionService>();
        builder.Services.AddScoped<SummaryService>();
        builder.Services.AddScoped<KnowledgeSearchService>();
        builder.Services.AddScoped<DocumentContentService>();
        builder.Services.AddScoped<NoteService>();
        builder.Services.AddScoped<TopicService>();
        builder.Services.AddScoped<StudyService>();
        builder.Services.AddSingleton<AgentPreferenceProvider>();
        builder.Services.AddScoped<SettingsService>();
        builder.Services.AddScoped<KnowledgeChatService>();
        builder.Services.AddScoped<CompanionService>();

        // 智能体工具链：本地文件沙箱、知识库工具、授权本机目录、MCP 桥接。
        builder.Services.AddSingleton<AgentWorkspace>();
        builder.Services.AddSingleton<AgentFileAccess>();
        builder.Services.AddSingleton<McpClientRegistry>();
        builder.Services.AddScoped<IAgentToolSource, WorkspaceToolSource>();
        builder.Services.AddScoped<IAgentToolSource, KnowledgeToolSource>();
        builder.Services.AddScoped<IAgentToolSource, LocalFileToolSource>();
        builder.Services.AddScoped<IAgentToolSource, AvatarToolSource>();
        builder.Services.AddScoped<IAgentToolSource, McpToolSource>();
        builder.Services.AddScoped<IAgentToolSource, WebSearchToolSource>();
        builder.Services.AddScoped<IAgentToolSource, UtilityToolSource>();
        builder.Services.AddScoped<AgentToolRegistry>();
        builder.Services.AddScoped<ToolCallingAgent>();

        // MCP 服务端：把本机知识库以 Streamable HTTP 暴露给其他大模型/智能体。
        var mcpServerEnabled = builder.Configuration.GetValue<bool?>($"{McpOptions.SectionName}:EnableServer") ?? true;
        var mcpServerPath = builder.Configuration.GetValue<string?>($"{McpOptions.SectionName}:ServerPath");
        if (string.IsNullOrWhiteSpace(mcpServerPath)) mcpServerPath = "/mcp";

        if (mcpServerEnabled)
        {
            builder.Services
                .AddMcpServer(options => options.ServerInfo = new Implementation
                {
                    Name = "MyFirstMafAgent.Knowledge",
                    Version = "1.0.0"
                })
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools<KnowledgeMcpTools>()
                .WithTools<LocalFileMcpTools>()
                .WithTools<AvatarMcpTools>();
        }

        builder.Services.AddHostedService<DocumentProcessingWorker>();
        builder.Services.AddHostedService<IndexLifecycleWorker>();
        builder.Services.AddHostedService<AvatarIngestionWorker>();

        var app = builder.Build();

        if (portablePostgres is not null)
        {
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(() =>
            {
                try
                {
                    portablePostgres.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    app.Logger.LogWarning(exception, "Failed to stop portable PostgreSQL.");
                }
            });
        }

        var requestLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("MyFirstMafAgent.Api.Requests");
        app.Use(async (context, next) =>
        {
            var stopwatch = Stopwatch.StartNew();
            using (requestLogger.BeginScope(new Dictionary<string, object?>
            {
                ["TraceId"] = context.TraceIdentifier,
                ["RequestMethod"] = context.Request.Method,
                ["RequestPath"] = context.Request.Path.Value ?? "/"
            }))
            {
                try
                {
                    await next();
                }
                finally
                {
                    var logLevel = context.Response.StatusCode >= 500
                        ? LogLevel.Error
                        : context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
                    requestLogger.Log(logLevel,
                        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMilliseconds} ms",
                        context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
                }
            }
        });
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("MyFirstMafAgent.Api.UnhandledExceptions");
            logger.LogError(exception, "Unhandled API exception for {RequestMethod} {RequestPath}. TraceId={TraceId}",
                context.Request.Method, context.Request.Path, context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await Results.Problem(
                title: "An unexpected error occurred.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier })
                .ExecuteAsync(context);
        }));
        app.UseCors("Vue");

        // 托管 Vue 构建产物：开发时不存在 wwwroot 也不影响 API；发布脚本会把 dist 复制进来。
        var webRootPath = app.Environment.WebRootPath;
        var indexPath = string.IsNullOrWhiteSpace(webRootPath) ? null : Path.Combine(webRootPath, "index.html");
        var hasStaticFrontend = indexPath is not null && File.Exists(indexPath);
        if (hasStaticFrontend)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = context =>
                {
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                        context.Context.Response.Headers.CacheControl = "no-cache";
                }
            });
        }
        else
        {
            app.Logger.LogWarning("未找到 wwwroot/index.html，本次仅提供 API。发布前端请执行 scripts/Publish-Portable.ps1。");
        }

        app.Logger.LogInformation("Starting API in {EnvironmentName}", app.Environment.EnvironmentName);
        try
        {
            app.Logger.LogInformation("Initializing database schema");
            await app.Services.GetRequiredService<KnowledgeBaseStore>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<KnowledgeBaseStore>().RecoverProcessingDocumentsAsync(CancellationToken.None);
            await app.Services.GetRequiredService<KnowledgeChatStore>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<CompanionStore>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<SqlSugarTopicRepository>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<SqlSugarNoteRepository>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<SqlSugarStudyRepository>().InitializeAsync(CancellationToken.None);
            await app.Services.GetRequiredService<SqlSugarAvatarRepository>().InitializeAsync(CancellationToken.None);

            // 用户在设置页面保存的模型配置优先于 appsettings.json。
            var settingsRepository = app.Services.GetRequiredService<SqlSugarSettingsRepository>();
            await settingsRepository.InitializeAsync(CancellationToken.None);
            var savedSettings = await settingsRepository.GetAsync(CancellationToken.None);
            if (savedSettings?.Llm is not null)
                app.Services.GetRequiredService<ILlmSettingsAccessor>().Update(savedSettings.Llm);

            // 注册配置中的向量规格，回填历史版本，并为每个知识库准备默认阴影索引。
            // 设置页保存的默认向量模型优先于 appsettings.json 的 Embedding:DefaultSpecName。
            await app.Services.GetRequiredService<EmbeddingSpecService>().EnsureConfiguredAsync(CancellationToken.None);
            await app.Services.GetRequiredService<IndexLifecycleManager>()
                .EnsureDefaultIndexesAsync(savedSettings?.Embedding?.DefaultSpecId, CancellationToken.None);

            app.Logger.LogInformation("Database schema initialization completed");
        }
        catch (Exception exception)
        {
            app.Logger.LogCritical(exception, "Database schema initialization failed");
            throw;
        }

        app.MapApiEndpoints();

        if (mcpServerEnabled)
            app.MapMcp(mcpServerPath);

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        if (hasStaticFrontend)
        {
            // 桌面/本地前端使用 Hash 路由；这里仍为根路径和未知前端路径回退到 index.html。
            app.MapFallback(async context =>
            {
                if (context.Request.Path.StartsWithSegments("/api")
                    || context.Request.Path.StartsWithSegments(mcpServerPath)
                    || context.Request.Path.StartsWithSegments("/openapi")
                    || context.Request.Path.StartsWithSegments("/scalar"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.SendFileAsync(indexPath!);
            });
        }
        else
        {
            app.MapGet("/", () => Results.Text(
                "MyFirstMafAgent API 正在运行。前端尚未发布：请先执行 scripts/Publish-Portable.ps1，或使用 Vite 开发服务器（http://localhost:5173）。",
                "text/plain; charset=utf-8"));
        }

        app.Logger.LogInformation("Frontend static files: {Status}", hasStaticFrontend ? webRootPath : "not published");
        return app;
    }

    /// <summary>读取 Kestrel 实际监听地址并规范化为浏览器可访问的 127.0.0.1 形式。</summary>
    public static string? GetListeningUrl(WebApplication app)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault(static item =>
            item.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(address)) return null;

        return address
            .Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://localhost", "://127.0.0.1", StringComparison.Ordinal)
            .TrimEnd('/');
    }

    /// <summary>控制台版是否需要在启动后自动打开浏览器。</summary>
    public static bool ShouldOpenBrowser(WebApplication app, string[] args)
    {
        if (args.Any(static arg => string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!app.Configuration.GetValue("Desktop:OpenBrowser", true))
            return false;
        if (app.Environment.IsDevelopment()
            && !app.Configuration.GetValue("Desktop:OpenBrowserInDevelopment", false))
            return false;
        return true;
    }

    /// <summary>用系统默认浏览器打开应用地址。</summary>
    public static void OpenBrowser(WebApplication app)
    {
        var url = GetListeningUrl(app);
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            app.Logger.LogInformation("Opened browser at {Url}", url);
        }
        catch (Exception exception)
        {
            app.Logger.LogWarning(exception, "Failed to open browser at {Url}", url);
        }
    }
}
