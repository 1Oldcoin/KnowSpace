using MyFirstMafAgent.Api;
using MyFirstMafAgent.Api.Hosting;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;

// 安装器钩子（electron-builder / NSIS 的卸载与升级）：只停便携数据库就退出，不启动 Web 主机。
// 卸载时 postgres.exe 还在运行会锁住程序目录，导致安装器删不掉文件；升级同理。
if (InstallerArguments.IsStopPortablePostgresRequested(args))
{
    Console.WriteLine("[installer] stopping portable PostgreSQL...");
    PortablePostgresHost.TryStopForUninstall(AppContext.BaseDirectory);
    Console.WriteLine("[installer] done.");
    return 0;
}

// 桌面宿主会把 API 当作子进程拉起：额外接收“就绪文件”与“数据目录”参数。
var desktopArguments = DesktopHostArguments.Parse(args);

try
{
    var app = await ApiHost.BuildAsync(args, dataRoot: desktopArguments.DataRoot);
    await app.StartAsync();

    // 端口由系统随机分配，只有启动后才知道；宿主轮询该文件即可拿到真实地址。
    var listeningUrl = ApiHost.GetListeningUrl(app);
    if (listeningUrl is not null)
        await desktopArguments.WriteReadyFileAsync(listeningUrl);

    // 发布运行时发现文件：后台服务先跑起来时，后开的窗口据此直接接管，
    // 不会再拉起第二个后端（两个实例会抢同一个便携 PostgreSQL 与同一份数据）。
    // 桌面宿主生成的令牌也一并落地，窗口关掉再打开时才认得出这个后台。
    var runtimeRoot = DesktopRuntimeFile.ResolveRoot(
        desktopArguments.DataRoot,
        MyFirstMafAgent.Api.Infrastructure.AppPaths.ResolveContentRoot());
    if (runtimeRoot is not null && listeningUrl is not null)
    {
        var effectiveToken = DesktopSecurityOptions
            .FromConfiguration(app.Configuration)
            .Token;

        DesktopRuntimeFile.Publish(runtimeRoot, listeningUrl, effectiveToken);

        // 优雅停止（含 Kestrel 收到 /api/desktop/shutdown）之后把发现文件收掉，
        // 免得下次开窗去连一个已经死掉的端口。
        app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>()
            .ApplicationStopping.Register(() => DesktopRuntimeFile.RemoveIfOwned(runtimeRoot));
    }

    if (ApiHost.ShouldOpenBrowser(app, args))
        ApiHost.OpenBrowser(app);

    await app.WaitForShutdownAsync();

    if (runtimeRoot is not null)
        DesktopRuntimeFile.RemoveIfOwned(runtimeRoot);

    return 0;
}
catch (Exception exception)
{
    // 桌面宿主用 stdio:'ignore' 拉起子进程，未处理异常只会留下一个看不懂的退出码
    // （0xE0434352），堆栈完全丢失——「关掉再打开就起不来」因此无从排查。
    // 这里把完整堆栈落到数据目录的日志里；文件可能写不进去，但绝不能再因此吞掉退出。
    try
    {
        var crashRoot = DesktopRuntimeFile.ResolveRoot(
            desktopArguments.DataRoot,
            MyFirstMafAgent.Api.Infrastructure.AppPaths.ResolveContentRoot());
        if (crashRoot is not null)
        {
            var logDirectory = Path.Combine(crashRoot, "logs");
            Directory.CreateDirectory(logDirectory);
            var crashFile = Path.Combine(logDirectory, "api-crash.log");
            File.AppendAllText(
                crashFile,
                $"[{DateTimeOffset.Now:O}] 启动失败（未处理异常）{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}",
                new System.Text.UTF8Encoding(false));
            Console.Error.WriteLine($"[startup] 启动失败，堆栈已写入 {crashFile}");
        }
    }
    catch
    {
        // 崩溃日志本身失败不应掩盖真正原因。
    }

    Console.Error.WriteLine(exception);
    return 1;
}
