using MyFirstMafAgent.Api;

var app = await ApiHost.BuildAsync(args);
await app.StartAsync();

if (ApiHost.ShouldOpenBrowser(app, args))
    ApiHost.OpenBrowser(app);

await app.WaitForShutdownAsync();
