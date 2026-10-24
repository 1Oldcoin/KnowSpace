using MyFirstMafAgent.Plugins.Sdk;

// 插件入口：所有协议处理都在 SDK 中完成，这里只提供实现。
// 返回值作为进程退出码，便于宿主判断是否正常退出。
return await PluginServer.RunAsync(new MyFirstMafAgent.Plugin.SamplePlatform.SamplePlatformPlugin(), args);
