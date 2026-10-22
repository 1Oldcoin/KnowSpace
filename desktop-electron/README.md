# MyFirstMafAgent 桌面壳（Electron sidecar）

原生 Electron 主进程 + .NET API sidecar。**不**使用 Electron.NET：`ElectronNET.Core` 目前只有 0.6.0 预览重写版，`ElectronNET.API` 仍停留在 Electron 23，均无法作为 .NET 10 + 现代 Electron 的稳定基线（详见 `docs/PHASE0-BASELINE.md`）。sidecar 方案保持 REST/MCP 契约不变，回滚路径（控制台 + Photino）继续可用。

## 职责划分

| 进程 | 负责 |
| --- | --- |
| Electron 主进程 | 窗口、菜单、单实例锁、应用退出、随机端口、一次性令牌、API 子进程生命周期、外链拦截 |
| Preload | 只暴露 `window.desktopHost.{platform, openExternal}` |
| Renderer | Vue 构建产物，由 Kestrel 托管，`contextIsolation` 开启、`nodeIntegration` 关闭、`sandbox` 开启 |
| .NET API | REST、MCP、业务编排、后台 Worker、便携 PostgreSQL |

令牌既不在 URL、日志、ready 文件里，也不下发给渲染进程：主进程通过 `webRequest.onBeforeSendHeaders` 注入 `X-Api-Token`。

## 启动

```powershell
# 1) 先发布 API（含 wwwroot）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -SkipFrontend

# 2) 安装 Electron（首次）
cd desktop-electron
npm install

# 3) 启动
npm start
```

`main.js` 按以下顺序查找 API 可执行文件：

1. 环境变量 `MAF_API_EXE`
2. `resources/api/MyFirstMafAgent.Api.exe`（打包后）
3. `../publish/desktop/MyFirstMafAgent.Api.exe`
4. `../publish/console/MyFirstMafAgent.Api.exe`

用户数据固定在 `%LOCALAPPDATA%\MyFirstMafAgentData`（可用 `MAF_DATA_ROOT` 覆盖），安装目录更新不会影响上传文件、模型与插件。

## 启动 / 退出顺序

```
启动：pg0 → API → 轮询 ready 文件 → 轮询 /api/health → 创建窗口
退出：POST /api/desktop/shutdown → 等待 API 退出（最长 20s）→ 强制结束 → app.exit
```

`ApplicationStopping` 只停止**由本次启动**拉起的 pg0，不会误停用户已有的外部 PostgreSQL。

## 安全边界

- Renderer：`contextIsolation: true`、`nodeIntegration: false`、`sandbox: true`、`webSecurity: true`。
- 严格 CSP 由 API 在桌面模式（`DESKTOP_STRICT_CSP=1`）下以响应头下发，见 `DesktopSecurityOptions.StrictContentSecurityPolicyValue`。
- `setWindowOpenHandler` 一律 deny，`http/https` 链接交给系统浏览器；`will-navigate` 禁止离开 API 源；`will-attach-webview` 拒绝。
- 令牌使用固定时间比较，仅覆盖 `/api` 与 `/mcp`；静态资源不需要令牌（窗口首页加载不依赖自定义头）。

## 验证

不需要 Electron 也能验证契约的关键部分：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-DesktopContract.ps1 `
    -ApiExe C:\Temp\mafapi\MyFirstMafAgent.Api.exe
```

检查项：随机回环端口、ready 文件不含令牌、无令牌 401、错误令牌 401、正确令牌 200、`/mcp` 无令牌 401、静态首页 200、严格 CSP 头存在、`POST /api/desktop/shutdown` 后进程自行退出。

## 打包

Electron 壳只打包渲染无关的 JS 与 preload；API 以 sidecar 形式随安装包分发（`extraResources` 指向 `publish/desktop`）。现有 Velopack 打包流程保持不变，升级/卸载不得删除 `%LOCALAPPDATA%\MyFirstMafAgentData`。
