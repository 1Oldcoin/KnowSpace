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

var app = await ApiHost.BuildAsync(args, dataRoot: desktopArguments.DataRoot);
await app.StartAsync();

// 端口由系统随机分配，只有启动后才知道；宿主轮询该文件即可拿到真实地址。
var listeningUrl = ApiHost.GetListeningUrl(app);
if (listeningUrl is not null)
    await desktopArguments.WriteReadyFileAsync(listeningUrl);

if (ApiHost.ShouldOpenBrowser(app, args))
    ApiHost.OpenBrowser(app);

await app.WaitForShutdownAsync();
return 0;
