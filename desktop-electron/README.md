# MyFirstMafAgent 桌面壳（Electron sidecar）

原生 Electron 主进程 + .NET API sidecar。**不**使用 Electron.NET：`ElectronNET.Core` 目前只有 0.6.0 预览重写版，`ElectronNET.API` 仍停留在 Electron 23，均无法作为 .NET 10 + 现代 Electron 的稳定基线（详见 `docs/PHASE0-BASELINE.md`）。sidecar 方案保持 REST/MCP 契约不变。

> 这是项目**唯一**的桌面壳。旧的 Photino 桌面工程（`MyFirstMafAgent.Desktop`）与 Velopack 打包已删除；“回滚路径”现在只剩下控制台入口（`MyFirstMafAgent.Api.exe`）。

## 职责划分

| 进程 | 负责 |
| --- | --- |
| Electron 主进程 | 窗口、菜单、单实例锁、应用退出、随机端口、一次性令牌、API 子进程生命周期、外链拦截、**自动更新** |
| Preload | 只暴露 `window.desktopHost.{platform, openExternal, getAppInfo, getUpdateState, checkForUpdates, downloadUpdate, installUpdate, onUpdateStatus}`（全部是白名单化的极窄接口） |
| Renderer | Vue 构建产物，由 Kestrel 托管，`contextIsolation` 开启、`nodeIntegration` 关闭、`sandbox` 开启 |
| .NET API | REST、MCP、业务编排、后台 Worker、便携 PostgreSQL |

令牌既不在 URL、日志、ready 文件里，也不下发给渲染进程：主进程通过 `webRequest.onBeforeSendHeaders` 注入 `X-Api-Token`。

## 启动

```powershell
# 1) 先发布 API（含 wwwroot）——desktop 目标就是 Electron 的 sidecar
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target desktop -SkipFrontend

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
启动：创建启动画面（本地 HTML，~100ms）→ 接管已跑的后端 或 拉起 API
      （便携库由 API 自己的启动流程拉起）→ 轮询 ready 文件（60ms）
      → 轮询 /api/health（120ms）→ 创建主窗口 → 主窗口 ready-to-show
      → 关闭启动画面
退出：POST /api/desktop/shutdown → 等待 API 退出（最长 20s）→ 强制结束 → app.exit
```

**启动画面**（`splash.html`）是刻意放在最前面的：冷启动要等「.NET 主机 + 便携 PostgreSQL + 建表检查 + Kestrel 监听」，秒级不可避免；这段时间里如果屏幕上什么都没有，用户会以为双击没生效而再点一次。

- 延迟 `SPLASH_DELAY_MS`（180ms）才显示：接管已在跑的后端时只要几百毫秒，此时不该闪一下画面。
- 状态文案由主进程用 `executeJavaScript` 写进 DOM，所以画面页不需要 preload、不开 `nodeIntegration`。
- 想换成自己的 GIF 动画：把 `splash.gif` 放到 `desktop-electron\` 下即可（`electron-builder.yml` 已包含该可选文件），内置图标动画会自动让位。

壳不知道数据库存在：便携 PostgreSQL 的 `initdb`、`pg_ctl start`、`CREATE EXTENSION vector` 全在 API 进程里做。`ApplicationStopping` 只停止**由本次启动**拉起的实例（`PortablePostgres:StopOnExit` 控制），不会误停用户已有的外部 PostgreSQL。

## 安全边界

- Renderer：`contextIsolation: true`、`nodeIntegration: false`、`sandbox: true`、`webSecurity: true`。
- 严格 CSP 由 API 在桌面模式（`DESKTOP_STRICT_CSP=1`）下以响应头下发，见 `DesktopSecurityOptions.StrictContentSecurityPolicyValue`。
- `setWindowOpenHandler` 一律 deny，`http/https` 链接交给系统浏览器；`will-navigate` 禁止离开 API 源；`will-attach-webview` 拒绝。
- 令牌使用固定时间比较，仅覆盖 `/api` 与 `/mcp`；静态资源不需要令牌（窗口首页加载不依赖自定义头）。

## 验证

启动耗时可以用脚本量（同一台机器前后对比）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-Startup.ps1 -Runs 3
```

它按 `[startup] +1234ms …` 前缀打印每一段（便携库 / 建表 / 监听）；第二次之后的数字才是用户日常感受到的（第一次要付页缓存与杀软扫描）。

不需要 Electron 也能验证契约的关键部分：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-DesktopContract.ps1 `
    -ApiExe C:\Temp\mafapi\MyFirstMafAgent.Api.exe
```

检查项：随机回环端口、ready 文件不含令牌、无令牌 401、错误令牌 401、正确令牌 200、`/mcp` 无令牌 401、静态首页 200、严格 CSP 头存在、`POST /api/desktop/shutdown` 后进程自行退出。

## 打包

配置在 `electron-builder.yml`（NSIS 安装包）。壳只打包 `main.js` / `preload.js` / `package.json`；API 以 sidecar 形式整体放到 `resources\api`（`extraResources` 指向 `../publish/desktop`），不打进 asar——asar 里的 exe 无法启动。

```powershell
# 一步到位：发布 sidecar + 打包（推荐，路径受限时会自动改在 %TEMP% 构建）
cd desktop-electron
npm run dist:full

# 已经发布过 sidecar，只想重新打包
npm run dist
```

产物在 `publish\releases\MyFirstMafAgent-<version>-setup.exe`（约 310 MB，未压缩的自包含 .NET 后端占大头）。

**安装**：双击向导，或静默安装 `<installer> /S` → 默认装到 `%LOCALAPPDATA%\Programs\MyFirstMafAgent`（每用户，不需要管理员）。
**卸载**：`"%LOCALAPPDATA%\Programs\MyFirstMafAgent\Uninstall MyFirstMafAgent.exe" /S`。

### 卸载/升级前停便携数据库

`build/installer.nsh` 在 NSIS 的 `customUnInstall` 阶段执行：

```
resources\api\MyFirstMafAgent.Api.exe --stop-portable-postgres
```

该参数由 `InstallerArguments` 识别，在 `Program.cs` 最前面处理：只调用 `PortablePostgresHost.TryStopForUninstall` 就退出，**不会**启动 Web 主机。electron-updater 的升级也是先静默运行旧版卸载器，因此升级同样覆盖到了。

### 已在真实安装环境下验证（阶段 10）

静默安装 → 启动（sidecar 从 `resources\api` 拉起、`/api/health` 200、SPA 正常）→ 强杀进程留下 `postgres.exe` → 静默卸载：安装目录被删除干净、`postgres.exe` 归零、`%LOCALAPPDATA%\MyFirstMafAgentData`（含 `pgdata`、`pglogs`）完整保留。

### 已知环境限制（不是安装包的问题）

- **安装包必须在非受保护目录里运行**。若 Windows 安全中心的「受控文件夹访问」开启，从 `桌面`/`文档`/`下载` 目录直接运行未签名程序会被拦下，NSIS 只会返回退出码 2。把安装包复制到 `C:\Temp` 或其它普通目录再运行。
- 同理，`electron-builder` 的 NSIS 阶段要执行一个临时安装器；当仓库路径位于受保护目录或含非 ASCII 字符时，它会在 `%TEMP%\maf-electron-build` 里构建再把产物复制回 `publish\releases`（`scripts\Build-ElectronInstaller.ps1` 自动处理）。
- 应用未签名 → SmartScreen 会提示“未知发布者”，代码签名见 `docs/ROADMAP.md` P0-5。
- 升级/卸载都不得删除 `%LOCALAPPDATA%\MyFirstMafAgentData`（`deleteAppDataOnUninstall: false` 已显式固定）。

## 自动更新（P0-3）

组件：`electron-updater`（运行时依赖）+ `updater.js`（本仓库的薄包装）。入口在 `main.js`：启动后 20 秒首次检查、之后每 6 小时；菜单「帮助 → 检查更新」与「设置 → 桌面版更新」卡片均可手动触发。

| 文件 | 作用 |
| --- | --- |
| `updater.js` | `createUpdater({ log, getWindow, beforeInstall, markQuitting, app?, autoUpdater? })`：状态机、事件转发、更新源解析、安装时机。`app` / `autoUpdater` 可注入，所以 `test/updater.test.js` 不需要 Electron 也不需要联网。 |
| `test/updater.test.js` | `npm run test:updater`，8 个用例 |
| `electron-builder.yml` 的 `publish` | `provider: generic` + 占位 URL；运行时可用 `MAF_UPDATE_URL` 覆盖 |

### 两个不能改的设计

1. **`autoInstallOnAppQuit = false`**：本应用的 `before-quit` 会 `preventDefault()` 后走 `app.exit(0)`，那条路径**不会**执行安装器。所以只在用户确认后显式 `quitAndInstall(false, true)`。
2. **安装前必须先停 API**：`installNow()` 的顺序固定为 `await beforeInstall()`（= `stopApi`，连带停便携库）→ `markQuitting()` → `quitAndInstall()`。安装器要覆盖 `resources\api`，而随包的 `postgres.exe` 就在 `resources\api\ExternalTools\pgSQL\<rid>\bin` 下运行、会锁住目录。`beforeInstall` 抛错时绝不调安装器。

### 更新源优先级

1. 环境变量 `MAF_UPDATE_URL`（部署/CI 最常用）
2. 用户数据目录 `%APPDATA%\myfirstmafagent-desktop\update-feed.json` 里的 `{ "url": "https://.../updates" }`（用户可自行改，不必重打包）
3. 安装包内 `app-update.yml`（即 `electron-builder.yml` 的 `publish.url`）

更新源目录需要同时提供：`latest.yml`、`MyFirstMafAgent-<version>-setup.exe`、对应的 `.blockmap`。

### 发布一个新版本

```powershell
# 1) 改 desktop-electron/package.json 的 version（和 Api 的版本保持一致）
# 2) 发布 sidecar + 打安装包 + 生成 latest.yml
cd desktop-electron
npm run dist:publish      # = electron-builder --win --x64 --publish always
# 3) 把 publish\releases 下的 latest.yml / setup.exe / *.blockmap 上传到更新源
```

> 未配置真实更新源时，检查更新会进 `error` 状态并在界面显示原因，**不会**弹窗骚扰。开发模式（`app.isPackaged=false`）直接 `disabled`。
> 本机实测：`electron-builder` 拉 `winCodeSign` 会超时（GitHub 不可达），所以 `latest.yml` 生成与端到端升级未验证。见 `docs/DEPLOYMENT.md` §8。
