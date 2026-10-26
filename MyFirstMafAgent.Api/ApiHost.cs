using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;
using MyFirstMafAgent.Api.Options;

namespace MyFirstMafAgent.Api;

/// <summary>
/// 业务组合根。控制台宿主（Program.cs）与桌面宿主共用同一套 API、后台任务和静态文件托管。
/// 具体注册项按模块拆分在 <c>Hosting/</c>；本文件只保留启动顺序与生命周期。
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

        HostBootstrap.ApplyDataRoot(builder, dataRoot);
        if (!string.IsNullOrWhiteSpace(urls))
            builder.WebHost.UseUrls(urls);

        // 桌面宿主生成的一次性令牌通过环境变量传入；未提供时本层禁用。
        var desktopSecurity = DesktopSecurityOptions.FromConfiguration(builder.Configuration);

        var fileLogger = HostBootstrap.ConfigureLogging(builder, enableFileLogging);

        void LogStartup(string message)
        {
            Console.WriteLine($"[startup] {message}");
            fileLogger?.Write($"{DateTimeOffset.Now:O} [startup] {message}");
        }

        HostBootstrap.ConfigureHttp(builder);
        builder.AddValidatedOptions();

        // 便携数据库：没有外部连接串时自动拉起随包的便携 PostgreSQL（initdb / pg_ctl 全在这里），
        // 等它就绪之后再建 Web 主机——Electron 只等 /api/health，拿不到 200 就说明数据库也没起来。
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

        // 外部数据库预检：便携库没被拉起时，先用一条真实连接确认它真的能用。
        // 否则“库没启动/口令错”会在 5 秒后表现为一条 SqlSugar 堆栈，而用户看不出是哪种。
        if (portablePostgres is null)
        {
            var preflightSeconds = builder.Configuration.GetValue<double?>(
                $"{PortablePostgresOptions.SectionName}:PreflightTimeoutSeconds") ?? 5d;
            var probe = await ConnectionPreflight.CheckAsync(
                connectionString, TimeSpan.FromSeconds(preflightSeconds));

            if (!probe.Succeeded)
            {
                // 完整诊断（端点 / 原因 / 便携库为何被跳过 / 三种修法）进日志与文件，
                // 异常本身只留一行：控制台上就不会把同一大段中文打印两遍。
                var failure = ConnectionPreflight.BuildMessage(probe, connectionString);
                LogStartup(failure);
                throw new InvalidOperationException(
                    $"外部数据库不可用（{probe.Endpoint}）：{probe.Detail} 完整诊断与修法见上方的 [startup] 输出。");
            }

            LogStartup($"外部数据库连接正常：{probe.Endpoint}");
        }

        builder.Services.AddSqlSugarPersistence(connectionString);
        builder.Services.AddKnowledgeModule();
        builder.Services.AddDocumentsChatsModule();
        builder.Services.AddAvatarModule();
        builder.Services.AddAgentModule();
        builder.Services.AddPluginModule();
        builder.Services.AddJobsModule();
        var mcp = builder.Services.AddMcpModule(builder.Configuration);
        builder.Services.AddBackgroundWorkers();

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

        var hasStaticFrontend = HostPipeline.Configure(app, desktopSecurity, mcp);

        app.Logger.LogInformation("Starting API in {EnvironmentName}", app.Environment.EnvironmentName);
        await DatabaseInitializer.InitializeAsync(app, CancellationToken.None);

        // 插件在数据库就绪后启动：单个插件失败不影响核心。
        await PluginModule.InitializePluginsAsync(app, CancellationToken.None);

        HostPipeline.MapEndpoints(app, hasStaticFrontend, mcp, desktopSecurity);
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
