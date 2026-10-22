using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using MyFirstMafAgent.Api;
using MyFirstMafAgent.Api.Infrastructure;
using MyFirstMafAgent.Api.Infrastructure.PortablePostgres;
using Photino.NET;
using Velopack;

namespace MyFirstMafAgent.Desktop;

internal static class Program
{
    private const uint MessageBoxIconError = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        // 极早诊断：WinExe 没有控制台，启动阶段的问题只能靠文件确认。
        // 只保留最近一次启动的记录，避免日志无限增长。
        var bootLog = Path.Combine(AppPaths.LogsDirectory, "desktop-boot.log");
        var firstBootWrite = true;
        void Boot(string message)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                var line = $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}";
                if (firstBootWrite)
                {
                    firstBootWrite = false;
                    File.WriteAllText(bootLog, line);
                    return;
                }

                File.AppendAllText(bootLog, line);
            }
            catch (Exception)
            {
                // 诊断失败不影响启动。
            }
        }

        Boot($"start apartment={Thread.CurrentThread.GetApartmentState()} args=[{string.Join(' ', args)}] base={AppContext.BaseDirectory}");

        // Velopack 必须在任何 UI / 日志 / 窗口之前运行：
        // 安装、更新、卸载等钩子会在这里被执行，可能直接结束进程。
        // 升级/卸载前先停掉便携数据库，否则 PostgreSQL 子进程会占用安装目录导致替换或卸载失败。
        VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ =>
                PortablePostgresHost.TryStopForUninstall(AppContext.BaseDirectory))
            .OnBeforeUninstallFastCallback(_ =>
                PortablePostgresHost.TryStopForUninstall(AppContext.BaseDirectory))
            .Run();
        Boot("velopack ok");

        // 从任意工作目录启动（快捷方式、文件关联）时，让相对路径（App_Data / ExternalTools）指向 exe 所在目录。
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        try
        {
            var result = Run(args,
                message => Boot(message),
                message => Boot(message));
            Boot($"exit {result}");
            return result;
        }
        catch (Exception exception)
        {
            Boot($"fatal {exception}");
            var logPath = Path.Combine(AppPaths.LogsDirectory, "desktop-crash.log");
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                File.AppendAllText(logPath, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
                logPath = $"(无法写入日志：{logException.Message})";
            }

            MessageBox(
                IntPtr.Zero,
                $"MyFirstMafAgent 启动失败：{exception.Message}{Environment.NewLine}{Environment.NewLine}详细日志：{logPath}",
                "MyFirstMafAgent",
                MessageBoxIconError);
            return 1;
        }
    }

    /// <summary>
    /// 全同步启动流程。
    /// <para>
    /// 关键：WebView2 必须在 STA 线程上创建。若在 async 方法里 await 之后再创建窗口，
    /// 续体会被调度到线程池（非 STA），WebView2 初始化失败，表现为窗口空白
    /// 且服务端收不到任何页面请求。因此所有异步操作都用 GetAwaiter().GetResult() 同步等待。
    /// </para>
    /// </summary>
    private static int Run(string[] args, Action<string> boot, Action<string> windowLog)
    {
        WebApplication? app = null;
        try
        {
            boot("building web host");
            app = ApiHost.BuildAsync(
                    args,
                    urls: "http://127.0.0.1:0",
                    contentRoot: AppContext.BaseDirectory,
                    enableFileLogging: true,
                    // 安装版：程序目录会被更新覆盖，可写数据统一放到用户目录。
                    dataRoot: AppPaths.DataDirectory)
                .GetAwaiter().GetResult();

            boot("starting kestrel");
            app.StartAsync().GetAwaiter().GetResult();

            var url = ApiHost.GetListeningUrl(app)
                ?? throw new InvalidOperationException("Kestrel 未返回可用的本地监听地址。");
            app.Logger.LogInformation("Desktop window will load {Url}", url);
            boot($"kestrel listening {url}");

            RunWindow(url, windowLog);
            return 0;
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    app.StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    app.Logger.LogWarning(exception, "Failed to stop the web host.");
                }

                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    /// <summary>
    /// 在调用线程（必须是 STA）同步运行 Photino 窗口。
    /// </summary>
    private static void RunWindow(string url, Action<string> log)
    {
        log($"window apartment={Thread.CurrentThread.GetApartmentState()} thread={Environment.CurrentManagedThreadId}");

        var window = new PhotinoWindow()
            .SetTitle("MyFirstMafAgent · 个人知识库")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .SetMinSize(1024, 700)
            .Center()
            .SetDevToolsEnabled(Debugger.IsAttached)
            .SetContextMenuEnabled(true);

        // 窗口/任务栏图标：exe 自身图标由 csproj 的 ApplicationIcon 决定，
        // 但窗口标题栏图标需要显式指定，ico 已随发布复制到 exe 同目录。
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(iconPath))
        {
            window.SetIconFile(iconPath);
        }
        else
        {
            log($"icon missing {iconPath}");
        }

        log("window created");

        window.Load(new Uri(url));
        log($"load requested {url}");

        window.WaitForClose();
        log("window closed");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
