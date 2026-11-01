# 知间桌面壳卡死诊断：后端已退出、壳进程残留、双击图标无反应

| 项 | 值 |
| --- | --- |
| 现象 | Zhijian.exe 主进程自 2026-10-31 11:51:46 存活，API 子进程（pid 14576，127.0.0.1:5056）14:35:46 正常退出后壳既没退出也没重启后端；5056 无监听；再双击图标"没反应" |
| 排查对象 | `desktop-electron/`（Electron 33.4.x + .NET API sidecar），安装版 1.0.5（`C:\Users\zhs\AppData\Local\Programs\Zhijian`） |
| 代码基线 | 仓库 git HEAD `50ae136`（1.0.5）。已核对：安装版 `resources/app.asar` 内 `main.js` 与仓库 HEAD 的 `desktop-electron/main.js` **字节相同**（SHA256 `6E7D26C353135EED18554CF24B2755677233290297946E7232BFE3B8ED7F798E`），所以下面所有 HEAD 行号就是当时运行中的代码行号 |
| 分析时间 | 2026-10-31 15:12–15:22（Asia/Shanghai） |
| 分析方式 | 只读：读代码、日志、进程/端口/文件；**未杀进程、未启停应用**（15:17:12 的现场重启与 15:20 的 `main.js` 改动均非本次分析所执行） |
| 当前状态 | 现场已由 Lead 于 15:17:12 恢复成功（新主进程 21212、API 18452、5056 监听、窗口 6.08s 出现）；工作区 `desktop-electron/main.js` 已有一份**未提交**的 P0 修复改动（15:20:37），本文 §5.0 对其做了 review |

---

## TL;DR（先看这 4 条）

1. **直接原因（P0，代码 bug）**：`stopApi()` 在"等 sidecar 退出"的循环里直接读全局 `apiProcess.exitCode`（HEAD `main.js:755`），而 `startApi()` 的 exit 回调会把 `apiProcess` 置 `null`（HEAD `main.js:353`）。14:35 的退出流程在 exit 回调置 null 之后至少还会轮询一次 → `null.exitCode` 抛 `TypeError` → `stopApi` 的 Promise reject → `before-quit` 里 `await stopApi()` 抛出 → 后面的 `app.exit(0)`（HEAD `main.js:1184`）**永远不执行**。此时 `isQuitting`、`intentionalStop` 已置真且不会再复位、看护已被停 → 进程变成"无窗口、无后端、无看护、占着单实例锁"的僵尸壳。
2. **为什么双击没反应**：僵尸仍持有 `requestSingleInstanceLock()`。新进程在 `bootstrap()` 第一行拿锁失败后直接 `app.quit()`（HEAD `main.js:1078-1080`），**在创建任何窗口之前就退出**（没有 splash、没有日志）。旧进程收到 `second-instance`，但处理函数是 `if (!mainWindow) return;`（HEAD `main.js:1083-1087`）——主窗口在 14:35 关窗时已销毁（`main.js:509` 置 null）→ 事件被直接丢弃。用户看到的就是"点了完全没反应"。
3. **14:47 又出现 GPU 子进程**：说明僵尸主进程的 Node 事件循环与 Chromium 多进程体系**并没有死锁**，仍在接收消息、仍在 fork/跟踪子进程；坏掉的是"窗口 + 恢复逻辑"，不是进程本身。这也解释了为什么任务栏/系统里还看得见它、却怎么也打不开。
4. **恢复方式（本次已实测）**：确认"无窗口僵尸 + 无 API 子进程 + 5056 无监听 + 便携 PG 已正常停止"→ 强杀旧主进程 →（可选）删 `api-ready.json` → 重新启动 exe。详见 §4，15:17:12 已按此恢复成功。**先杀进程再谈文件清理**，只删文件不杀进程没有用（锁还在僵尸手里）。

---

## 0. 证据与时间线

### 0.1 关键日志（原始）

`C:\Users\zhs\AppData\Roaming\myfirstmafagent-desktop\logs\electron-shell.log`（第 516–517 行，全部为 UTC）：

```
2026-10-31T06:35:46.404Z shutdown requested
2026-10-31T06:35:46.844Z API sidecar exited (code=0, signal=null)
```

**之后到 15:17:12（07:17:12Z）之前没有任何新记录。** 注意这里缺了一条关键日志：正常退出时应有 `API sidecar stopped gracefully`（HEAD `main.js:756`；10/27、10/28 的退出日志里都有，见 §1.5）。它的缺失是本次 bug 最直接的指纹。

同一时刻 `api-console.log`（第 2196–2197 行）：

```
2026-10-31T14:35:46.4038742+08:00  HTTP POST /api/desktop/shutdown responded 202 in 1.8929 ms
2026-10-31T14:35:46.5648601+08:00  Application is shutting down...
[startup] +9839085ms 便携 PostgreSQL 已停止。
```

`pglogs\postgres.log`：`14:35:46.621 CST received fast shutdown request` → `14:35:46.650 CST database system is shut down`。**便携 PostgreSQL 是干净停止的**，没有留下 `postmaster.pid`。

### 0.2 时间线

| 本地时间 | 事件 | 来源 |
| --- | --- | --- |
| 10-31 11:49 | 1.0.5 覆盖安装（`resources` 目录 mtime 11:49:01） | 文件系统 |
| 11:51:46 | `Zhijian.exe` 主进程启动（pid 32664，父进程 8056） | 进程表 |
| 11:51:47 | 壳拉起 API：`starting API sidecar on http://127.0.0.1:5056` | electron-shell.log |
| 11:51:57 | ready 文件出现（10.37s）；11:51:58 health ok（11.60s）；窗口 12.15s 出现 | electron-shell.log |
| 11:52:18 → 11:52:39 | 更新检查 `net::ERR_CONNECTION_TIMED_OUT`（首屏之后，不影响启动，见 §3.4） | electron-shell.log |
| **14:35:46.404** | 壳 `POST /api/desktop/shutdown` 返回 202 后记录 `shutdown requested` | shell log + api-console |
| 14:35:46.55 | API `Application is shutting down...` | api-console |
| 14:35:46.56–46.65 | 便携 PostgreSQL fast shutdown 完成 | postgres.log |
| **14:35:46.844** | API 进程 exit(0)；壳记录 `API sidecar exited (code=0, signal=null)`；**此后壳再无任何日志** | electron-shell.log |
| 约 14:47:46 | 出现新的 GPU 子进程（Lead 采集） | 现场 |
| 15:12（本次采集） | 主进程 32664 仍存活；`MainWindowHandle=0`；子进程只剩 NetworkService + GPU（无 renderer、无 `Zhijian.Api.exe`）；5056 无监听 | 现场 |
| 15:17:12 | Lead 处置后重启成功：新主进程 21212，API 18452，5056 监听，窗口 6.08s 出现 | electron-shell.log / 进程表 / api-ready.json |

### 0.3 事故时（15:12）的现场快照

| 检查项 | 实测值 | 判断 |
| --- | --- | --- |
| `Get-Process -Id 32664` | 存活；`MainWindowHandle = 0` | 无窗口的僵尸主进程 |
| `Zhijian.exe` 子进程 | 仅 `--type=utility --utility-sub-type=network.mojom.NetworkService`（26956）与 GPU（32884）；**无 renderer** | 窗口确实已销毁，只剩 Chromium 运行时 |
| `Zhijian.Api.exe` | 不存在（pid 14576 已退出，`Get-Process -Id 14576` 无结果） | 后端已死 |
| 127.0.0.1:5056 | 无 LISTEN；5055/5056/5057 均空闲 | 后端未监听 |
| `%APPDATA%\myfirstmafagent-desktop\api-ready.json` | `{"url":"http://127.0.0.1:5056","pid":14576,"startedAt":"2026-10-31T11:51:57.1031475+08:00"}`（mtime 11:51:57） | **陈旧**（pid 已死），但它不是启动阻塞项（见 §3.5） |
| `%LOCALAPPDATA%\MyFirstMafAgentData\runtime\local-port.json` | `{"port":5056}` | 正常，应保留（决定固定端口） |
| `runtime\desktop.json` / `runtime\token` | **不存在** | API 优雅退出时已由 `DesktopRuntimeFile.RemoveIfOwned` 清理（`Program.cs:45,54`） |
| `pgdata\postmaster.pid` | 不存在；55432 无监听 | 便携 PG 已干净停止，无需处理 |
| 系统 PostgreSQL 服务 | `postgresql-x64-18` 运行中（监听 5432，session 0） | **与本产品无关，禁止动** |
| `desktop-prefs.json` | `{"stopBackendOnExit": true}` | 用户勾了"退出时一并停后端"，所以退出会走 `stopApi()` |
| `app.asar` 内 `main.js` | 与仓库 HEAD 字节相同（SHA256 见文首） | 事故代码 = 仓库 HEAD，不是旧版本 |
| 更新缓存 | `%LOCALAPPDATA%\myfirstmafagent-desktop-updater\installer.exe`（291 MB）、`pending\temp-Zhijian-1.0.2-setup.exe`（270 MB） | 历史更新残留，与本次无关（§4.6） |

### 0.4 安装版一致性（回答"是不是旧代码"）

- 安装版版本：`FileVersion 1.0.5 / ProductVersion 1.0.5.0`，`ProductName 知间-本地知识库`。
- 安装版 `resources/app.asar` 里的 `main.js`：1196 行，SHA256 与仓库 HEAD `desktop-electron/main.js` 完全一致。
- 结论：**事故不是"用户装的是旧包"**，1.0.5 的 HEAD 代码本身就带这个 bug。

---

## 1. 14:35 的 shutdown 之后，主进程本应如何退出？为什么没退出？

### 1.1 正常路径（`stopBackendOnExit=true` 时）

1. 用户关闭主窗口（X / Alt+F4）→ `mainWindow` 触发 `closed`，`mainWindow = null`（HEAD `main.js:509`）。
2. 最后一个窗口关闭 → `window-all-closed`（HEAD `main.js:1187-1189`）→ `app.quit()`。
3. `before-quit`（HEAD `main.js:1169`）：
   - `event.preventDefault()`（1171）—— 这是**设计**：要等后端优雅停完再退；
   - `isQuitting = true`（1172）；`stopWatchdog()`（1173）；`updater.dispose()`（1174）；
   - `readPrefs().stopBackendOnExit` 为 true → `await stopApi()`（1179）。
4. `stopApi()`（HEAD `main.js:728-776`）：
   - `intentionalStop = true`（733）、`stopWatchdog()`（734）；
   - `POST /api/desktop/shutdown`（739-743），成功后 `log('shutdown requested')`（745）；
   - 若 `!apiOwned || !apiProcess || apiProcess.exitCode !== null` 则直接返回（751）；
   - 否则每 150ms 轮询 `apiProcess.exitCode !== null`（753-760），检测到就 `log('API sidecar stopped gracefully')` 并返回；
   - 超时（90s）则先执行 `--stop-portable-postgres` 钩子，再 `apiProcess.kill()`（762-775）。
5. 回到 `before-quit` → `app.exit(0)`（1184）→ 进程退出。

按本机实测，便携 PG fast shutdown 只需几十毫秒，**正常应在 shutdown 请求后 1 秒内退出**。

### 1.2 实际发生的事（带时间戳和代码行）

| 时刻 | 代码位置 | 动作 |
| --- | --- | --- |
| 14:35:46.404 | `main.js:745` | `shutdown requested` 记录；此时 `apiProcess` 仍非 null、`exitCode` 为 null（API 在 .844 才退出） |
| .404 | `main.js:751` | 早退检查：`apiProcess` 非 null → **不返回**，进入等待循环 |
| .554 | `main.js:754-760`（第 1 轮） | `apiProcess.exitCode` 仍 null → `sleep(150)` |
| .704 | 同上（第 2 轮） | 仍 null → `sleep(150)` |
| **.844** | `main.js:351-354`（exit 回调） | 记录 `API sidecar exited (code=0, signal=null)`；`if (apiProcess === child) apiProcess = null;` ← **全局变量被置 null**；因 `isQuitting || intentionalStop` 为真，直接 return（不触发恢复） |
| **.854** | `main.js:755`（第 3 轮） | 代码是 `if (apiProcess.exitCode !== null)`，而 `apiProcess` 已是 `null` → **抛 `TypeError: Cannot read properties of null (reading 'exitCode')`** |
| .854 | `main.js:1179-1184` | `stopApi()` 的 Promise reject → `await stopApi()` 抛出 → `app.exit(0)` **不执行**（处理函数里没有 try/catch） |

关键点：`/api/desktop/shutdown` 是先返回 202、再延迟 150ms 调 `StopApplication`（`DesktopEndpoints.cs:32-45`），API 还要停便携 PG，所以到进程真正退出有几百毫秒；而 `stopApi` 的轮询间隔是 150ms，**exit 回调（把 apiProcess 置 null）必然先于下一次轮询**。也就是说，只要退出时 `stopBackendOnExit=true` 且 PG 需要一点时间停止，这个竞态几乎必然命中。

### 1.3 根因

`apiProcess` 是可变全局（HEAD `main.js:82`），被两处并发读写：

- `startApi()` 的 exit 回调：`if (apiProcess === child) apiProcess = null;`（HEAD `main.js:353`）；
- `stopApi()` 的等待循环：`if (apiProcess.exitCode !== null)`（HEAD `main.js:755`，此处**没有 null 保护**）。

修复方向很简单：`stopApi()` 进入时先把子进程句柄抓到局部变量（`const child = apiProcess`），循环与 kill 都用 `child`，并在循环里判 `!child || child.exitCode !== null`。工作区已有这份修复（§5.0）。

### 1.4 为什么错误会"吞掉"整个退出，且壳没有任何日志

- `before-quit` 的监听器是 `async` 函数，EventEmitter **不会 await** 它的返回值；`stopApi()` 的 rejection 成为**未处理的 Promise rejection**，`app.exit(0)` 永远不会执行。
- 现场事实：这个异常**没有终止主进程**（PID 32664 一直活到 15:17）。因此退出流程停在了 `event.preventDefault()` 之后、`app.exit(0)` 之前——退出被永久取消。
- 主进程的错误输出走 stderr，而本产品只把 API 子进程的 stdout/stderr 重定向到 `api-console.log`（HEAD `main.js:320-349`），**壳自己的 stderr 没有落盘**，所以日志里看不到任何异常。如果当时 Electron 弹了默认的 "A JavaScript error occurred in the main process" 错误框，用户可能看到过一次并点掉了——这一点无法从现有日志证实，修复时补上 `process.on('uncaughtException'/'unhandledRejection')` 落盘即可确认（见 §5.2 F10）。
- 僵尸态的"三无"是连锁的：`stopWatchdog()` 已在 734 执行（无看护）；`isQuitting` 已置 true（`before-quit` 与 `recoverBackend` 都会直接返回）；`intentionalStop` 已置 true（`startApi` exit 回调与 `recoverBackend` 也会直接返回）。**全文没有任何一处把 `intentionalStop` 复位为 false**（HEAD 中只有 102 声明、733 置真、354/977/1037 读取），所以一旦走到这条路径，进程内所有自愈入口全部失效。

### 1.5 回归定位（为什么"以前关得掉"）

- `grep` 历史日志：10-27、10-28 的多次退出都有 `API sidecar stopped gracefully`，说明老版本 `stopApi` 的等待循环能正常工作。
- `git log -S 'if (apiProcess === child) apiProcess = null;'` 显示这行是提交 **`0c1d6e5`（fix(desktop): 后端丢失后自动重连，不再需要重启整个应用，v1.0.3，2026-10-29 16:19）** 加进来的；等待循环本身更早就存在（`9a410e8`）。也就是说：**"后端丢失自动恢复"这个特性把全局 `apiProcess` 置 null 与退出等待循环拼在一起，引入了这个退出竞态**。
- 旁证：10-30 09:07:42 的退出已经出现过同样签名——`shutdown requested` → 3 秒后 `API sidecar exited (code=0)` → **没有** `stopped gracefully` → 之后也没有任何日志；直到 09:12:35 才出现一次完整的新启动（推测：中间旧壳被用户在任务管理器结束后重开——否则僵尸持有单实例锁，09:12 不可能出现完整的新启动日志）。这与用户"关掉再打开"的习惯吻合，也是本次卡死更早的一次同 bug 演练。

### 1.6 排除其他解释

| 假设 | 结论 |
| --- | --- |
| 窗口 hide 到托盘 | **排除**：`main.js` 全文无 `Tray`、无 `.hide(`、无 `close` 拦截；现场 `MainWindowHandle=0`，窗口是真的销毁了 |
| `will-quit` 被 preventDefault | **排除**：没有注册 `will-quit` |
| `window-all-closed` 未注册 | **排除**：已注册（HEAD `main.js:1187-1189`） |
| `before-quit` 里 await 后端导致慢 | **不是主因**：本机 PG 停止只要几十毫秒，正常 1 秒内能退；慢是"卡在异常上"，不是"等太久" |
| 更新检查阻塞退出/启动 | **排除**：见 §3.4；事件顺序上更新检查 11:52:39 就结束了，14:35 的退出与它无关 |
| 用户点了"重启并安装"触发 stopApi | **排除**：`updater.installNow()` 会先记 `update: stopping API before install`、之后记 `update: quitAndInstall`（`updater.js:210-213`），日志里都没有；且 14:35 的 shutdown 前没有下载完成记录 |
| 便携 PG 残留导致后端起不来 | **排除（本次）**：`postmaster.pid` 不存在，postgres.log 显示正常 shut down；55432 无监听 |

---

## 2. 再次双击图标会发生什么？为什么"没反应"？14:47 的 GPU 进程说明什么？

### 2.1 新实例的路径

双击 → 新的 `Zhijian.exe` 进程启动 → `bootstrap()` 第一件事：

```js
if (!app.requestSingleInstanceLock()) {   // HEAD main.js:1078
  app.quit();                              // 1079
  return;                                  // 1080
}
```

因为僵尸主进程（32664）还活着并持有单实例锁，新进程**拿不到锁 → 立刻 `app.quit()` → 在 `app.whenReady()`、`createSplash()`、写任何日志之前就退出**。用户角度看：图标可能闪一下、任务栏无窗口、无启动画面、无错误提示。这就是"一点反应都没有"。

### 2.2 旧实例的路径

Electron 会把 `second-instance` 事件投递给锁的持有者（僵尸）。旧进程处理函数：

```js
app.on('second-instance', () => {   // HEAD main.js:1083-1087
  if (!mainWindow) return;          // ← 主窗口已在 14:35 关窗时销毁（main.js:509）
  if (mainWindow.isMinimized()) mainWindow.restore();
  mainWindow.focus();
});
```

`mainWindow` 为 null → 直接 return。**没有日志、没有窗口、没有恢复。** 于是新实例退出、旧实例无动作——用户反复点也永远没有反馈。

补充：此时即使把 `mainWindow.show()/focus()` 补上也没用——`apiBaseUrl` 仍指向已死的 5056，看护已停，页面加载会失败。正确做法是"重建后端 + 重建窗口"（见 §5.1 F3）。

### 2.3 14:47 的 GPU 子进程说明什么

- 现场快照里 GPU 进程（pid 32884）的父进程是 32664，即它属于僵尸主进程，不是某个新实例。
- GPU 进程在 14:47 才出现/被重新拉起，说明：**僵尸主进程的 Node 事件循环和 Chromium 多进程体系仍在正常调度**——它能接收 IPC/`second-instance` 消息，能继续 fork/跟踪子进程。所以"卡死"不是主线程死锁或无响应，而是"窗口没了 + 事件处理逻辑把请求丢弃 + 后端不会再被拉起"。
- 这也解释了为什么系统里进程还在、任务管理器里杀得掉、但用户点图标永远打不开。

---

## 3. 后端在运行中死掉：现有恢复/重连逻辑覆盖了什么、漏了什么

### 3.1 现有四层机制

| 层 | 代码位置（HEAD） | 作用 |
| --- | --- | --- |
| ① sidecar exit 回调 | `main.js:351-359` | 子进程退出且 `!isQuitting && !intentionalStop` 时调 `recoverBackend('后端进程意外退出', { allowSpawn: true })` |
| ② 看护定时器 | `main.js:1025-1068`（15s 周期，`WATCHDOG_*` 53-55） | `/api/desktop/info` 连续 3 次失败（≈45s）→ 恢复；`/api/health` 连续 6 次失败（≈90s）→ 恢复（覆盖"进程活着但数据库/连接池不可用"） |
| ③ 恢复流程 | `main.js:966-1022` | 5s 冷却、最多 5 次；先尝试接管已存在实例（`attachExistingBackend`），否则 `ensureBackend()` 重启；成功后重置计数、重装令牌头、`mainWindow.loadURL` 回新地址 |
| ④ 启动时接管/自启 | `main.js:917-955` / `870-893` | 已有后端（`runtime/desktop.json` + 探活成功）就接管；没有才 spawn；固定端口被占时 5056→5057… 回退；spawn 前删除旧 ready 文件 |

### 3.2 覆盖到的情况（已有日志为证）

- 后端崩溃（非 0 退出）→ 自动重启：10-30 16:14 `API sidecar exited (code=1073807364)` → `recovery attempt 1` → 重新 spawn（虽随后因系统关机失败，见 §6.1）。
- 后端活着但不能查库（连接池耗尽）→ `/api/health` 连续失败 → 重启：这套是 1.0.4/1.0.5 专门加的（提交 `6306a64`）。
- 已有后台服务（命令行/服务方式启动）→ 窗口启动时接管，不重复 spawn（`attachExistingBackend`）。
- 专属端口被占 → 回退 5057…（`chooseListenUrl`，`main.js:278-305`）。
- 安装/更新前停后端（`updater.installNow` → `beforeInstall: stopApi`）。

### 3.3 漏掉的情况（按严重程度）

| 编号 | 缺口 | 说明 / 代码位置 |
| --- | --- | --- |
| **G1** | **退出过程中的异常没有兜底，退出可被永久取消** | 本次事故；`stopApi` 的 TypeError + `before-quit` 没有 try/finally（HEAD `main.js:728-776, 1169-1185`） |
| **G2** | 退出被中止后没有回滚 | 看护已在 `stopApi:734` 停掉且不会重启；`isQuitting`/`intentionalStop` 永久为真，后续所有恢复入口（`966-969`）直接返回 false |
| **G3** | 壳"没有窗口"时没有自愈入口 | `second-instance` 在无窗口时直接 return（`1083-1087`）；代码里没有任何"重建窗口"的路径 |
| **G4** | 僵尸壳没有超时自杀 | `before-quit` 一旦卡住或抛错，进程没有任何退出看门狗（本次活了 2h42m） |
| **G5** | `intentionalStop` 是粘性全局 | 只有置 true，没有复位；一次被中断的停止会让本进程剩余生命内所有恢复失效 |
| **G6** | ready 文件不校验、退出不清理 | `ensureBackend` 只在 spawn 前 `rmSync`（`939-944`），`waitForReadyFile` 接受任何有 `url` 的旧文件（`369-392`），不校验 `pid`/`url`/写入时间；退出路径不删除 → 现场遗留 pid=14576 的陈旧文件 |
| **G7** | `runtime/desktop.json` 陈旧 | `attachExistingBackend` 探活失败会忽略（`882-886`）但**不删除**；若其中的 pid 还活着却探不通，`ensureBackend` 会等 5×2s 后抛"已有后端进程但不响应"（`923-932`），用户看到"启动失败" |
| **G8** | 恢复次数用尽后彻底停摆 | 5 次后 `stopWatchdog()` + `dialog.showErrorBox`（`978-986`），之后不再重试；若用户没点到弹窗，就停在"壳活着、后端没了"的半坏状态 |
| **G9** | 没有"侧车退出→立即重建"的强监督 | exit 回调与看护并发靠 `recoveryInFlight` 互斥（`968`）；某次恢复失败后要等下一轮 3×15s，没有立即重试/退避策略 |
| **G10** | 更新安装失败无回滚 | `updater.installNow` 先 `beforeInstall()`（停后端）再 `markQuitting()` 再 `quitAndInstall()`（`updater.js:203-221`）；如果安装器没能启动/安装失败，壳会带着"后端已停、isQuitting=true"继续活着——与本次同族的"半退出"状态 |
| **G11** | 日志不足以自证 | 壳的 stderr/未处理异常不落盘；`second-instance` 不记日志；退出各阶段（before-quit 进入、stopApi 各步、finally）日志不足——本次只能靠代码推断 |

### 3.4 更新检查是否在启动关键路径上（结论：**不在**）

- 当前版本：窗口创建、`startWatchdog()` 完成后才 `createUpdater(...)` + `startPeriodicChecks()`（HEAD `main.js:1151-1158`）。
- `startPeriodicChecks` 用 `setTimeout(FIRST_CHECK_DELAY_MS=20s)` 延迟首次检查，之后每 6h 一次（`updater.js:29-31, 234-249`）。
- 日志印证：11:51:58 窗口已出现，11:52:18 才开始 `update: checking`，11:52:39 超时失败（`net::ERR_CONNECTION_TIMED_OUT`，GitHub 更新源不可达）——**首屏不受影响，用户已能用**。它只是每 6 小时产生一次约 20s 的悬挂请求和一条错误日志。
- 真正需要修的不是"更新检查阻塞首屏"，而是 **G10 的安装失败回滚**（那才是能造成同款"壳活着、后端没了"的路径）。

### 3.5 `api-ready.json` / `local-port.json` 的实际作用（别被"陈旧文件"带偏）

- 壳启动时**不读** `api-ready.json`，只读 `runtime/desktop.json` 做"接管已有后端"（HEAD `main.js:870-893`）。`api-ready.json` 只用于 `waitForReadyFile`（spawn 之后等新后端写出来），而且 `ensureBackend` 在 spawn 前会先删掉它（`939-944`）。
- 因此现场那个 pid=14576 的陈旧 `api-ready.json` **不是"打不开"的原因**，它只是证据/噪音；启动时会被删掉重建。
- `runtime/local-port.json` 是**故意持久化**的固定端口记忆（`main.js:231-253`）：删了会回落到 `appsettings.json` 的 `Server:FixedLocalPort`，可能换端口，导致 MCP 配置/书签里写死的 5056 失效。**不要删。**

---

## 4. 现在如何安全恢复（判据 + 命令）

> 只读分析者不执行杀进程/启动；以下命令供 Lead 现场处置使用。本次已在 15:17:12 按此流程恢复成功（§4.4）。

### 4.1 判据清单（四条同时满足 = 判定为"无窗口僵尸壳"）

1. `Zhijian.exe` 主进程存在（无 `--type=` 参数的那个），且 `MainWindowHandle = 0`，子进程里没有 `renderer`；
2. 同一进程树下**没有** `Zhijian.Api.exe`（或 5056 无监听）；
3. `electron-shell.log` 尾部是 `shutdown requested` + `API sidecar exited`，之后没有 `API sidecar stopped gracefully`、没有新的启动记录；
4. 便携 PG 已停止：`pgdata\postmaster.pid` 不存在、55432 无监听（若 PG 还在跑，先停 PG 再杀 API，见 §4.5）。

### 4.2 只读取证命令（先做快照，再动手）

```powershell
$d = "$env:APPDATA\myfirstmafagent-desktop"
# 1) 进程树：主进程 = CommandLine 里没有 --type= 的那个
Get-CimInstance Win32_Process -Filter "Name LIKE 'Zhijian%'" |
  Select-Object ProcessId, ParentProcessId, Name, CreationDate, CommandLine | Format-List
# 2) 窗口/端口
Get-Process Zhijian -ErrorAction SilentlyContinue | Select-Object Id, MainWindowHandle, StartTime
Get-NetTCPConnection -LocalPort 5056 -State Listen -ErrorAction SilentlyContinue
# 3) 日志尾部（关键：有没有 stopped gracefully / 新启动）
Get-Content "$d\logs\electron-shell.log" -Tail 20
# 4) 陈旧文件与 PG 状态
Get-Content "$d\api-ready.json" -Raw -ErrorAction SilentlyContinue
Get-Content "$env:LOCALAPPDATA\MyFirstMafAgentData\runtime\local-port.json" -Raw -ErrorAction SilentlyContinue
Test-Path "$env:LOCALAPPDATA\MyFirstMafAgentData\pgdata\postmaster.pid"
Get-NetTCPConnection -LocalPort 55432 -State Listen -ErrorAction SilentlyContinue
```

### 4.3 处置步骤

1. **杀掉僵尸主进程**（主进程一死，Chromium 子进程和单实例锁都会随之释放；Windows 上单实例锁是内核命名互斥体，不需要也不能"删文件"释放）：
   ```powershell
   # 只杀本产品的主进程；先确认 PID 来自 4.2 的第 1 步
   Stop-Process -Id <主进程PID> -Force
   Start-Sleep -Seconds 2
   Get-Process Zhijian -ErrorAction SilentlyContinue   # 期望为空
   ```
   若还有残留的 `Zhijian.exe` 子进程，可 `Get-Process Zhijian | Stop-Process -Force`（只会命中本产品）。
   若存在孤儿 `Zhijian.Api.exe`（主进程被强杀后可能出现），单独 `Stop-Process -Name Zhijian.Api -Force`。
   **绝不要** `taskkill /IM postgres.exe`——本机还有系统 PostgreSQL 服务（`postgresql-x64-18`，5432），与本产品无关。
2. **清理陈旧状态（可选，但建议做以便看新文件）**：
   - 删 `%APPDATA%\myfirstmafagent-desktop\api-ready.json`——**安全**：壳每次 spawn 前本来就会删除并重建它；保留反而容易误导排障。
   - `runtime\desktop.json` / `runtime\token` 如果存在且 pid 已死，也删除（本次不存在）。
   - **保留** `runtime\local-port.json`（固定端口 5056 的记忆）、`desktop-prefs.json`（用户设置）。
   - **Windows 上没有 `SingletonLock`**：那是 Linux/macOS 的 symlink 形态；`%APPDATA%\myfirstmafagent-desktop\lockfile`（0 字节）是 Electron/Chromium 在 userData 下写的锁/标记文件，正常情况下**不要删**；只有启动明确报 "profile in use" 且已确认没有 Zhijian 进程时才处理（本次恢复没有动它，也正常启动）。
   - 更新缓存 `%LOCALAPPDATA%\myfirstmafagent-desktop-updater`（约 560 MB 历史残留）与本次恢复无关，可稍后清理。
3. **重新启动**：
   ```powershell
   Start-Process "C:\Users\zhs\AppData\Local\Programs\Zhijian\Zhijian.exe"
   ```
4. **验证（按顺序，一般 5–15 秒内完成）**：
   ```powershell
   $d = "$env:APPDATA\myfirstmafagent-desktop"
   Get-Content "$d\logs\electron-shell.log" -Tail 12
   #   期望依次出现：starting API sidecar -> ready file detected -> health ok -> window loaded -> API ready -> main window shown
   Get-NetTCPConnection -LocalPort 5056 -State Listen      # 期望 127.0.0.1:5056 LISTEN
   Get-Content "$d\api-ready.json" -Raw                    # pid 应为新 Zhijian.Api 的 PID
   Get-Process Zhijian | Select-Object Id, MainWindowHandle # 主进程 MainWindowHandle 应为非 0
   ```
5. 若启动仍然失败：看 `api-console.log` 尾部与 `%LOCALAPPDATA%\MyFirstMafAgentData\logs\api-crash.log`；检查 5056 是否被别的进程占用（壳会自动回退 5057，但 MCP 里写死的 5056 就失效了）；检查 PG 是否残留（§4.5）；不要反复盲目重试。

### 4.4 本次实测记录（15:17:12，Lead 处置后）

```
07:17:12.583Z using fixed local port 5056
07:17:12.587Z starting API sidecar on http://127.0.0.1:5056
07:17:13.264Z splash shown at 810ms
07:17:16.226Z ready file detected after 3772ms
07:17:17.977Z health ok after 5523ms
07:17:18.103Z window loaded http://127.0.0.1:5056
07:17:18.104Z API ready at http://127.0.0.1:5056 (spawned) after 5650ms
07:17:18.534Z main window shown at 6080ms
```

复核：主进程 21212、API `Zhijian.Api.exe` 18452、127.0.0.1:5056 LISTEN（owner=18452）、`api-ready.json` 更新为 `pid:18452`。启动后 `update: checking` 仍会 20s 超时（GitHub 不可达），属既有现象，不影响使用。

### 4.5 若便携 PostgreSQL 残留（本次没有）

判据：`pgdata\postmaster.pid` 存在，或 55432 在监听但 5056 没有。处理：用安装目录里的同一个钩子停（它只停库、不起 Web）：

```powershell
& "C:\Users\zhs\AppData\Local\Programs\Zhijian\resources\api\Zhijian.Api.exe" --stop-portable-postgres
```

看到 `[installer] done.` 即可；**不要**直接 kill `postgres.exe`（会留下"进程在、不接受连接"的中间态，下次启动可能直接崩）。

### 4.6 禁止/不必要操作

- ❌ 不杀进程、只删 `api-ready.json` → 没用，单实例锁仍在僵尸手里，双击依旧无窗口。
- ❌ 删 `runtime\local-port.json` → 可能改变固定端口，MCP 配置失效。
- ❌ 删整个 `%APPDATA%\myfirstmafagent-desktop` / `MyFirstMafAgentData` → 丢失设置、缓存、数据库目录关联，代价远大于收益。
- ❌ 动系统 PostgreSQL 服务 / 杀 `postgres.exe`。
- ❌ 在没有确认僵尸已死的情况下反复双击图标（只会多几次瞬时进程，日志无记录，无助于恢复）。

---

## 5. 防复发修复建议

### 5.0 当前工作区已有改动（未提交，2026-10-31 15:20:37）

`git diff`：`desktop-electron/main.js` +39/−14（HEAD blob `4f5080a` → 工作区 `c7ee667`，1221 行；`node --check` 通过）。内容与本文结论高度一致：

| 改动 | 位置（工作区行号） | 评价 |
| --- | --- | --- |
| `stopApi` 抓局部 `const child = apiOwned ? apiProcess : null;`，循环/早退/`kill()` 全部改用 `child` 并判 null | `main.js:736-783` | ✅ 正确修掉 G1 的直接竞态 |
| `before-quit` 加 `try/catch/finally`，`finally` 里 `app.exit(0)`；另加 `hardExit = setTimeout(..., SHUTDOWN_TIMEOUT_MS + 10_000)` 兜底 | `main.js:1179-1210` | ✅ 方向正确；⚠️ 见下方 review 点 1 |
| `second-instance` 增加 `mainWindow.isDestroyed()` 保护 | `main.js:1091-1097` | ⚠️ 只避免了抛异常，**仍然 `return`、不会重建窗口** → G3 未解决，同类事故下用户依旧"双击没反应" |

**Review 点 1（重要）**：`hardExit` 超时 = `SHUTDOWN_TIMEOUT_MS + 10_000` = **100s**，但 `stopApi` 最坏路径是 `fetch abort 5s + 等待 90s + stopPortablePostgresQuietly 90s ≈ 185s`。若真卡在停 PG 阶段，100s 的 `app.exit(1)` 会在 PG 停止钩子还没跑完时结束主进程，最坏情况把"PG 半停止"的中间态留给下次启动（正是 `SHUTDOWN_TIMEOUT_MS` 改成 90s 想避免的问题）。建议二选一：
- 把兜底超时设为 `5_000 + SHUTDOWN_TIMEOUT_MS + 90_000 + 15_000`（约 200s）；或
- 把 `hardExit` 只在"已经进入 stopApi 之后仍无进展"时启用（例如 `stopApi` 内部按阶段上报进度，外层只在阶段间超时）。

**Review 点 2**：`second-instance` 仍是单实例锁吞掉用户点击的路径。应改成"无窗口则重建"（见 F3），否则本次事故即使换一种触发方式复现，用户依旧只能去任务管理器。

**Review 点 3**：改动没有覆盖 `isQuitting`/`intentionalStop` 粘性、ready 文件校验、更新安装回滚、壳异常日志落盘（见下）。

### 5.1 P0（本次事故必须修）

**F1 退出路径禁止读可能为 null 的全局句柄** — `desktop-electron/main.js:728-776`

```js
async function stopApi() {
  if (!apiBaseUrl) return;
  intentionalStop = true;
  stopWatchdog();
  const child = apiOwned ? apiProcess : null;   // 先抓句柄
  try { /* fetch shutdown */ } catch (e) { log(...) }
  if (!child || child.exitCode !== null) return;
  const deadline = Date.now() + SHUTDOWN_TIMEOUT_MS;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) { log('API sidecar stopped gracefully'); return; }
    await sleep(150);
  }
  log('API sidecar did not stop in time, killing');
  await stopPortablePostgresQuietly();
  try { child.kill(); } catch (e) { log(`kill failed: ${e.message}`) }
}
```
（工作区已实现，保留此条作为回归基线。）

**F2 退出必须有"一定能退"的保证** — `main.js:1169-1185`

- 把 `before-quit` 主体包进 `try { ... } catch (e) { log(...) } finally { clearTimeout(hardExit); app.exit(0); }`；
- 加硬超时兜底（超时时间要覆盖 `fetch + SHUTDOWN_TIMEOUT_MS + PG 钩子` 的最坏路径，见 review 点 1）；
- 兜底里先 `log('quit cleanup timed out; forcing exit')` 再 `app.exit(1)`，保证日志落盘（`log()` 是同步 `appendFileSync`，安全）。

**F3 单实例被拒绝时必须能重建窗口** — `main.js:1083-1087`

```js
app.on('second-instance', () => {
  log('second-instance received');
  if (mainWindow && !mainWindow.isDestroyed()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.focus();
    return;
  }
  void reopenFromScratch();     // 见下
});

async function reopenFromScratch() {
  // 退出流程已经开始了就不要复活（但要让用户看到提示）
  if (isQuitting) { log('second-instance ignored: app is quitting'); return; }
  try {
    closeSplash();
    if (!(await attachExistingBackend())) await ensureBackend(); // 后端已死则重启
    await waitForHealth(apiBaseUrl);
    installTokenHeader();
    createWindow(apiBaseUrl);      // createWindow 内部已有 ready-to-show/show
    startWatchdog();
    log(`window recreated on ${apiBaseUrl}`);
  } catch (error) {
    log(`reopen failed: ${error.stack || error.message}`);
    dialog.showErrorBox('无法重新打开知间', error.message);
  }
}
```
注意：`createWindow()` 会重置 `mainWindow`，但 `mainWindow.on('closed')` 已把它置 null，可安全复用；窗口里的旧监听（`will-download` 等）由 `session.defaultSession` 持有，注意别重复注册（`will-download` 每次 `createWindow` 都注册了一次，重建窗口会叠加——顺手用 `session.defaultSession.removeAllListeners('will-download')` 或把注册提到 `whenReady` 一次）。

**F4 粘性状态必须可复位** — `main.js:733, 882-893, 1179`

- `intentionalStop` 用 `try/finally` 包住 `stopApi` 的"主动停止窗口期"，`finally { intentionalStop = false }`；
- 若 `before-quit` 的清理失败（catch 分支），不要把 `isQuitting`/`intentionalStop` 留在 true：要么明确 `app.exit(1)`（进程一定要走），要么回滚为 `false` 并恢复看护（`startWatchdog()`），由二次 `app.quit()` 再试；
- 同理 `updater.installNow()` 失败时（F9）要回滚这两个标志。

**F5 sidecar 非预期退出必须重启（区分"预期停止"）** — `main.js:351-359`

- 现在靠 `isQuitting || intentionalStop` 两个全局布尔判断"预期"，太粗且会被粘住。改成"停止令牌"：`stopApi`/`installNow` 在发 shutdown 前记录 `expectedStopReason = 'user-quit' | 'install'`，exit 回调消费一次（`expectedStopReason = null`）后按"非预期"处理；
- 对 `code === 0` 也一样：只要不是我们发起的停止，就当作"后端自己没了"去恢复（当前逻辑其实覆盖了，但被 G1 的粘性状态短路）；
- 恢复失败要有指数退避（如 5s→15s→60s，上限 5 次），而不是 5 秒冷却后立刻重试。

### 5.2 P1（强烈建议）

**F6 ready 文件按"这一次 spawn"校验** — `main.js:369-392, 939-944`

- `startApi` 时记录 `spawnedAt = Date.now()` 和子进程 pid；
- `waitForReadyFile(readyFile, { since: spawnedAt, pid: child.pid, url: listenUrl })`：要求 `fs.statSync(readyFile).mtimeMs >= since`、`payload.pid === child.pid`、`payload.url === listenUrl`（或至少同端口），任何一项不满足都当陈旧文件忽略并继续等；
- 退出/安装前在 `stopApi` 里 `fs.rmSync(readyFile, { force: true })`，避免现场长期留一个 pid 已死的 `api-ready.json` 误导排障；
- `ensureBackend` 的 `rmSync` 保留，但要处理"删不掉"（文件被占用/杀软）的情况：此时不要继续 `waitForReadyFile` 读旧文件，改为换一个带随机后缀的 ready 文件名传给子进程。

**F7 `runtime/desktop.json` 陈旧时清理** — `main.js:870-893`

- `attachExistingBackend` 探活失败时：若 `isProcessAlive(info.processId)` 为 false，直接删除 `desktop.json`/`token`（`DesktopRuntimeFile.RemoveIfOwned` 语义），并在日志里写"stale runtime file removed"；
- 若 pid 活着但探不通，保留现有的"等待 5×2s 再报错"（避免双实例抢库），但错误提示里直接给出"可安全结束该 PID"的指引，不要只弹一句"启动失败"。

**F8 退出阶段要有用户反馈** — `main.js:728-776, 74`

- 关窗后用户看不到任何东西，最坏要等 100–200s；建议在 `before-quit` 里保留一个极小的"正在退出…"窗口（或复用 splash，`SPLASH_DELAY_MS` 之后再显示），并在 `stopApi` 各阶段更新文案（"正在停止知识库服务…"/"正在停止数据库…"）；
- 这样既能解释等待，也能在卡住时让用户看到卡在哪一步（比只看日志友好）。

**F9 更新安装失败必须回滚** — `desktop-electron/updater.js:203-221`

```js
async function installNow() {
  ...
  installing = true;
  try {
    await beforeInstall();           // 停后端（可能已停便携 PG）
    log('update: quitAndInstall');
    autoUpdater.quitAndInstall(false, true);   // 同步返回，不代表安装器已成功启动
    markQuitting();                  // 尽量后置；或先 on('before-quit-for-update') 确认
    return { ok: true };
  } catch (error) {
    installing = false;
    log(`update: install failed ${error.message}`);
    await afterInstallFailed?.();    // 新增注入点：恢复后端 + 回滚 isQuitting/intentionalStop
    return { ok: false, error: error.message };
  }
}
```
宿主侧（`main.js:1162-1167`）新增 `afterInstallFailed: async () => { isQuitting = false; intentionalStop = false; await recoverBackend('更新安装失败', { allowSpawn: true }); startWatchdog(); }`。

**F10 壳异常必须落盘** — `main.js:1192-1196` 附近

```js
process.on('unhandledRejection', (reason) => {
  log(`unhandledRejection: ${reason && reason.stack ? reason.stack : String(reason)}`);
});
process.on('uncaughtException', (error) => {
  log(`uncaughtException: ${error.stack || error.message}`);
});
```
（顺序上要在 `bootstrap()` 之前注册；注意 `log()` 自身失败已被吞掉，安全。）

### 5.3 P2（可选，但能让故障更快被发现）

- **F11 看护与恢复策略**：恢复成功后 `startWatchdog()`（当前只在启动时启动一次，`recoverBackend` 成功后没有重启看护——若看护因 `stopApi` 被停掉，恢复成功也不会再有 watchdog）；`RECOVERY_MAX_ATTEMPTS` 用尽后改为指数退避 + 界面常驻"服务已断开，点此重试"，而不是只弹一次框。
- **F12 更新检查加超时/可达性预检**：`updater.js:166-196` 在 `checkForUpdates()` 前用 `AbortController` 3s 探测 feed 可达性（或给 `autoUpdater` 设置 `requestHeaders`/`net` 超时），避免每次启动 +20s 的 `ERR_CONNECTION_TIMED_OUT` 噪音；保持"窗口就绪后再检查"的现有顺序不动。
- **F13 日志**：`second-instance`、`before-quit` 进入、`stopApi` 每阶段、`hardExit` 触发、恢复成功/失败都记一行；`electron-shell.log` 加按大小轮转（和 `api-console.log` 的 2MB 轮转保持一致）。
- **F14 单实例行为**：`app.requestSingleInstanceLock()` 失败时，主进程在退出前可按 `--second-instance` 语义通知旧进程（Electron 已自动做），但建议在开发版加一条 stderr 输出，便于排查"双击无反应"。

### 5.4 测试建议（现有 `test/updater.test.js` 是纯 Node + assert，可扩展）

- 新增 `desktop-electron/test/desktop-shell.test.js`，用假 `app`/假 child 复现本次竞态：模拟 exit 回调把句柄置 null 后，等待循环不再抛异常、`finally` 一定调用 `app.exit`；
- 断言 `before-quit` 在 `stopApi` 抛错时仍 `app.exit(0)`（用注入的 `app.exit` 计数器）；
- 断言 `second-instance` 在 `mainWindow=null` 时调用"重建"路径（注入 mock）；
- `updater.test.js` 增加"`beforeInstall` 抛错 → 走 `afterInstallFailed` 恢复，不进入 quitting"用例。

---

## 6. 附录

### 6.1 历史退出码（同一 shell 日志中出现过）

| 退出码 | 十六进制 | 含义 | 出现时间与解读 |
| --- | --- | --- | --- |
| 0 | 0x00000000 | 正常退出 | 多次；其中 10/31 14:35 这次是本次事故（缺少 `stopped gracefully`） |
| 1073807364 | 0x40010004 | `DBG_TERMINATE_PROCESS`：进程被强制终止（调试器/工具/会话结束） | 10/26 15:12、10/30 16:14（后者紧接系统关机，见下一行） |
| 3762504530 | 0xE0434352 | .NET CLR 未处理异常 | 10/30 06:05：旧版 20s `SHUTDOWN_TIMEOUT` 强杀导致 PG 未干净停止后，新进程启动即崩；这正是现在改成 90s + `--stop-portable-postgres` 钩子的原因（`main.js:34-44, 764-776`） |
| 3221226091 | 0xC000026B | `STATUS_DLL_INIT_FAILED_LOGOFF`：会话注销/关机期间进程初始化失败 | 10/30 16:14Z = 本地 10-31 00:14，用户正在关机/注销；看护拉起的新 sidecar 注定起不来，**属环境因素，不是产品缺陷**（也解释了为什么 10/30 夜里到 10/31 11:51 之间没有日志） |
| 4294967295 | 0xFFFFFFFF | 未知的外部终止 | 10/27 04:10，仅一条，暂无法归因 |

外部参考：
- `0xC000026B = STATUS_DLL_INIT_FAILED_LOGOFF`：<https://lore.kernel.org/linux-cifs/20240620093436.411889-3-chenxiaosong@chenxiaosong.com/#8>
- `0x40010004` 常见于"任务计划/进程被终止"：<https://learn.microsoft.com/en-us/answers/questions/2703904/task-scheduler-last-run-result-code-0x40010004>

### 6.2 关键代码位置速查（HEAD = 事故版本）

| 关注点 | `desktop-electron/main.js`（HEAD） | `desktop-electron/updater.js` |
| --- | --- | --- |
| 单实例锁 / second-instance | 1078-1081 / 1083-1087 | — |
| 拉 sidecar、exit 回调 | 307-365（exit 351-359） | — |
| ready 文件等待 / health 等待 | 369-392 / 394-417 | — |
| 窗口 / splash | 448-513 / 538-664 | — |
| stopApi / 便携 PG 停止 | 728-776 / 782-816 | — |
| 探活 / 接管 / save | 822-838 / 870-893 / 917-955 | — |
| 看护 / 恢复 | 1025-1068 / 966-1022 | — |
| 启动流程 / 退出流程 | 1077-1190（before-quit 1169-1185，window-all-closed 1187-1189） | — |
| 更新检查 / 安装 | 1151-1158 | 29-31、166-196、203-221、234-249 |
| API 侧 shutdown / ready / 运行时文件清理 | `MyFirstMafAgent.Api/Endpoints/DesktopEndpoints.cs:32-45`、`Hosting/DesktopHostArguments.cs:22-49`、`Program.cs:24-26,40-46,54` | — |

### 6.3 待确认事项（建议后续跟进）

1. **14:47 GPU 进程的精确创建时间**没有在进程消失前复核（本次分析开始时 32664 仍在，但未记录其子进程 `CreationDate`；待复核时旧进程已被处置）。下次再遇到僵尸壳，建议先跑 `Get-CimInstance Win32_Process -Filter "Name LIKE 'Zhijian%'" | Select ProcessId,ParentProcessId,CreationDate,CommandLine` 存证。
2. **15:17 重启后出现约 15 秒的 401 风暴**：15:17:34–15:17:49 `api-console.log` 里有约 50 条 `Rejected local API request without a valid desktop token: GET /api/health`（间隔约 0.27s，来源是应用内渲染进程的 network service，pid 32772），随后恢复 200。疑似页面加载早期"令牌头注入尚未生效/地址匹配不上"的短窗口；与本次卡死无直接关系，但值得单独立项确认是否会导致首屏短暂"离线"观感。
3. **Electron 默认异常框是否弹出过**：本次无 stderr 落盘，无法确认 14:35 是否弹过 "A JavaScript error occurred in the main process"。补上 F10 的日志后即可自证。
4. **`stopBackendOnExit=true` 的默认值**：默认是 `false`（`main.js:135-138`），本机用户显式勾了 true，才走到 `stopApi`。如果产品希望"关闭所有服务"成为默认，这个竞态的影响面会更大；建议在 F1 修复合入后再评估默认值。
5. **工作区当前未提交改动**需要在评审后决定是否补上 F3/F4/F6/F9/F10 再合并；仅合入当前补丁能修掉"本次退出竞态"，但"无窗口僵尸后双击无反应"的另一半（G3）仍在。
