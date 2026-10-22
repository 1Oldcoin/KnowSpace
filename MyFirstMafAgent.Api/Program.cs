using MyFirstMafAgent.Api;
using MyFirstMafAgent.Api.Hosting;

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
