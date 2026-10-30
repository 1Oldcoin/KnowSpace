using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Infrastructure.Persistence;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;
using MyFirstMafAgent.Api.Options;
using Npgsql;

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
            // 统一带上「距进程启动」的毫秒数：启动慢的时候能一眼看出是哪一段慢。
            var line = $"[startup] +{StartupTimeline.ElapsedMilliseconds,7:F0}ms {message}";
            Console.WriteLine(line);
            fileLogger?.Write($"{DateTimeOffset.Now:O} {line}");
        }

        StartupTimeline.Sink = LogStartup;
        StartupTimeline.Mark("process arguments parsed");

        // 在拉起数据库之前做：配置不安全就别浪费几十秒启动 PostgreSQL。
        var hostProfile = HostProfileResolver.Resolve(builder.Configuration, urls);
        SecurityPostureGuard.Enforce(
            hostProfile,
            SecurityPostureGuard.Evaluate(hostProfile, builder.Configuration, desktopSecurity),
            LogStartup);
        builder.Services.AddSingleton(hostProfile);
        builder.Services.AddSingleton(desktopSecurity);

        HostBootstrap.ConfigureHttp(builder);
        builder.AddValidatedOptions();

        // 便携数据库：没有外部连接串时自动拉起随包的便携 PostgreSQL（initdb / pg_ctl 全在这里），
        // 等它就绪之后再建 Web 主机——Electron 只等 /api/health，拿不到 200 就说明数据库也没起来。
        PortablePostgresHost? portablePostgres = null;
        if (builder.Configuration.GetValue<bool?>($"{PortablePostgresOptions.SectionName}:Enabled") ?? true)
        {
            var beforePostgres = StartupTimeline.Mark("portable PostgreSQL starting");
            portablePostgres = await PortablePostgresHost.StartAsync(
                builder.Configuration, root, LogStartup);
            StartupTimeline.Step("portable PostgreSQL ready", beforePostgres);
            if (portablePostgres is not null)
            {
                builder.Configuration["ConnectionStrings:KnowledgeBase"] = portablePostgres.ConnectionString;
                LogStartup($"便携 PostgreSQL 已就绪：127.0.0.1:{portablePostgres.Port}/{portablePostgres.Database}");
            }
        }

        var connectionString = builder.Configuration.GetConnectionString("KnowledgeBase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Set ConnectionStrings:KnowledgeBase (for example via ConnectionStrings__KnowledgeBase) before starting the API.");

        // 外部连接串是用户自己写的，通常只有 Host/Port/Database/Username/Password。
        // Npgsql 的默认值里 ConnectionIdleLifetime=0（永不回收），于是「连接只增不减」的
        // 问题在外部库上同样会发生，而且用户完全看不到原因：进程活多久，池子就涨到多大。
        // 这里统一补上池子参数；用户显式写了的键不覆盖（NpgsqlConnectionStringBuilder
        // 用 ContainsKey 判断，能区分「没写」和「写了默认值」）。
        connectionString = ApplyPoolDefaults(connectionString, builder.Configuration);

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
        var mcp = builder.Services.AddMcpModule(builder.Configuration, hostProfile);
        builder.Services.AddBackgroundWorkers();

        var beforeBuild = StartupTimeline.Mark("service registration done");
        var app = builder.Build();
        StartupTimeline.Step("host built", beforeBuild);

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

        var serverSecurity = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerSecurityOptions>>().Value;
        var hasStaticFrontend = HostPipeline.Configure(app, desktopSecurity, mcp, serverSecurity);

        app.Logger.LogInformation("Starting API in {EnvironmentName}", app.Environment.EnvironmentName);
        var beforeSchema = StartupTimeline.Mark("database schema initialization starting");
        await DatabaseInitializer.InitializeAsync(app, CancellationToken.None);
        StartupTimeline.Step("database schema ready", beforeSchema);

        // 库里的模型配置此时已经加载完，判断「公网端点缺密钥」才不会误报。
        app.WarnMissingLlmApiKey();


        // 插件在数据库就绪后启动：单个插件失败不影响核心。
        // 这段曾阻塞 Kestrel 监听（插件含 AppContainer 启动与 30s 握手超时），
        // 现在由 PluginModule 自己挪到「主机已监听」之后，不再占用首屏时间。
        var beforePlugins = StartupTimeline.Mark("plugin initialization queued");
        PluginModule.QueueInitialization(app);
        StartupTimeline.Step("plugin initialization queued", beforePlugins);

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

    /// <summary>
    /// 给外部连接串补上连接池参数。用户没写的键才补，写了的一律尊重。
    ///
    /// <para>
    /// 为什么必须补：Npgsql 的默认值是 <c>ConnectionIdleLifetime = 0</c>（永不回收）+
    /// <c>MaxPoolSize = 100</c>。而本应用的用法会持续产生新的 ExecutionContext
    /// （HTTP 请求、后台 Worker 循环、任务槽位），SqlSugar 在每个上下文里缓存一个
    /// <c>SqlSugarClient</c>，各自带一条物理连接且只还不关。于是「只增不减」，
    /// 挂机几小时后池子被灌满，任务管理器里出现几十个 postgres.exe。
    /// 用户自己写的连接串基本不会包含这两个键，所以必须由程序兜底。
    /// </para>
    ///
    /// <para>
    /// <b>不能用 <c>builder.ContainsKey("Maximum Pool Size")</c> 判断用户是否写过。</b>
    /// 实测（Npgsql 9.0.4）：对任何<b>合法键名</b>，即使连接串里根本没有这个键，
    /// <c>ContainsKey</c> 也返回 <c>true</c>——它是"这个名字合法吗"，不是"用户写了吗"。
    /// 照它写会让下面的赋值全部跳过，用户配的值反而原样保留、默认值永不生效。
    /// 只能回原始串做解析判断。
    /// </para>
    ///
    /// <para>
    /// 另一个坑：这种解析只认 <b>带空格</b> 的拼写（<c>Maximum Pool Size</c>）。
    /// 写成 <c>maximumpoolsize</c> 会让 <c>new NpgsqlConnectionStringBuilder(...)</c>
    /// 直接抛 <c>ArgumentException</c>。所以两种拼写都要在别名里覆盖。
    /// </para>
    ///
    /// <para>
    /// 解析失败时原样返回，交给后面的预检去报错——这里不该抢先抛异常，
    /// 否则「连接串写错了」会变成一条看不懂的 <c>ArgumentException</c>。
    /// </para>
    /// </summary>
    internal const int ConnectionAcquisitionTimeoutSeconds = 5;

    internal static string ApplyPoolDefaults(string connectionString, IConfiguration configuration)
    {
        var options = configuration.GetSection(PortablePostgresOptions.SectionName)
            .Get<PortablePostgresOptions>() ?? new PortablePostgresOptions();

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            if (!ConnectionStringDeclaresKey(connectionString, "maximumpoolsize", "maxpoolsize"))
                builder.MaxPoolSize = Math.Max(1, options.MaxPoolSize);
            if (!ConnectionStringDeclaresKey(connectionString, "connectionidlelifetime"))
                builder.ConnectionIdleLifetime = Math.Max(0, options.ConnectionIdleLifetimeSeconds);
            if (!ConnectionStringDeclaresKey(connectionString, "connectionpruninginterval"))
                builder.ConnectionPruningInterval = Math.Max(1, options.ConnectionPruningIntervalSeconds);

            // 连接**获取**超时（不是命令超时）。本地/容器内的 PostgreSQL 建连都是毫秒级，
            // 15 秒的唯一效果就是让「连接池已满」这种已知失败拖上 15 秒才报错——
            // 现象就是界面转圈 15 秒然后报错，看门狗也要等好几个周期才反应过来。
            // 5 秒足够任何真实建连，而且失败得干脆。
            if (!ConnectionStringDeclaresKey(connectionString, "timeout", "connectiontimeout"))
                builder.Timeout = ConnectionAcquisitionTimeoutSeconds;

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return connectionString;
        }
    }

    /// <summary>
    /// 判断原始连接串里是否**显式写了**某个键。大小写、空格、下划线都不敏感。
    ///
    /// <para>
    /// 用原始文本而不是 <c>NpgsqlConnectionStringBuilder.ContainsKey</c>，原因见
    /// <see cref="ApplyPoolDefaults"/> 的说明：那个方法对合法键名恒返回 true。
    /// </para>
    /// </summary>
    internal static bool ConnectionStringDeclaresKey(string connectionString, params string[] aliases)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;

            var name = segment[..separator].Trim().Replace(" ", "").Replace("_", "");
            foreach (var alias in aliases)
            {
                if (name.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
