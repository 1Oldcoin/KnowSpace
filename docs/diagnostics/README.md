# 知间启动问题诊断与处置（2026-10-31）

用户反馈两件事：**① 启动很慢；② 现在甚至启动不了。**

两件事是**两个独立问题**，都在今天定位并处理完。详细分析见：

- [desktop-shell-hang.md](desktop-shell-hang.md) —— "启动不了"的代码级根因（545 行，只读分析）
- [startup-slow.md](startup-slow.md) —— 12.15s 冷启动的耗时瀑布与优化清单（397 行，只读分析）

---

## 一、"启动不了" = 退出时 `stopApi()` 空指针 → 僵尸壳占住单实例锁

### 现场（15:12 采集）

| 检查项 | 实测 |
| --- | --- |
| `Zhijian.exe` 主进程 32664 | 存活（11:51:46 启动），但 `MainWindowHandle = 0`、无 renderer 子进程 |
| API 子进程（pid 14576，127.0.0.1:5056） | 已于 14:35:46 退出，5056 无监听 |
| `electron-shell.log` 末尾 | `shutdown requested` → `API sidecar exited (code=0)` 后**再无任何记录**，缺少正常退出必有的 `API sidecar stopped gracefully` |
| 便携 PostgreSQL | 已随 API 干净停止（无 `postmaster.pid`） |

用户之后每次双击图标，新进程在 `bootstrap()` 第一行 `requestSingleInstanceLock()` 失败 → 直接 `app.quit()`（**在建任何窗口、写任何日志之前**）；旧进程收到 `second-instance`，但主窗口已销毁、处理函数 `if (!mainWindow) return;` 把事件丢掉。所以表现是"点了完全没反应"。

### 根因（`desktop-electron/main.js`，HEAD 行号）

1. `startApi()` 的 `exit` 回调会把全局 `apiProcess` 置 `null`（原 :353）。
2. `stopApi()` 在 `POST /api/desktop/shutdown` 之后进入 150ms 轮询，但轮询里读的是全局 `apiProcess.exitCode`（原 :755）。子进程恰好在轮询间隙退出 → `null.exitCode` 抛 `TypeError`。
3. `before-quit` 的 `await stopApi()` 抛出 → 后面的 `app.exit(0)`（原 :1184）**永远不执行**；此时 `isQuitting`/`intentionalStop` 已置真且不会复位、看护已停 → 进程变成"无窗口、无后端、无看护、占着单实例锁"的僵尸壳。
4. 竞态窗口窄，所以是**间歇性**的（10/27、10/28 的退出都正常；今天 14:35 命中）。

回归来源：`0c1d6e5`（v1.0.3 加"后端丢失自动恢复"时把 `stopApi` 改成了轮询）。安装版 1.0.5 的 `app.asar/main.js` 与仓库 HEAD 字节相同，不是旧包。

### 已落地的修复（`desktop-electron/main.js`，工作区未提交）

| 改动 | 位置 | 作用 |
| --- | --- | --- |
| `stopApi()` 先把句柄抓到局部 `const child`，预检查/轮询/强杀都读 `child`，null 早返回 | `stopApi()` | 消除空指针竞态（P0） |
| `before-quit` 清理逻辑包 `try/catch/finally`，`finally` 里 `app.exit(0)`；另加 200s 硬退出兜底（`SHUTDOWN_TIMEOUT_MS*2+20s`，覆盖"等 sidecar 90s + 停便携库 90s"的最坏合法路径） | `before-quit` | 任何异常/卡住都不会留下僵尸 |
| `second-instance`：`isDestroyed` 保护；窗口已销毁且后端可用时重建主窗口 | `bootstrap()` | 双保险，避免"点了没反应" |
| `will-download` 监听改为只注册一次（`downloadHandlerInstalled`） | `createWindow()` | 支持重建窗口而不重复弹保存框 |

### 验证（本机实测）

- **修复前复现**：15:20 对旧版发 WM_CLOSE → 日志只到 `API sidecar exited (code=0)`，主进程 30s 后仍存活（僵尸），需强杀。
- **修复后**：关窗 → `API sidecar exited` → `API sidecar stopped gracefully` → **750ms 内主进程退出**，无残留 `Zhijian.exe`、5056/55432 已释放、`postmaster.pid` 不存在（便携库干净停止，下次开机不做崩溃恢复）。
- **双击已有实例**：第二个进程正常秒退，主窗口/后端不受影响。
- **安装版已同步**：`C:\Users\zhs\AppData\Local\Programs\Zhijian\resources\app.asar` 已替换为修复版，原文件备份为 `app.asar.bak-20261031-152054`（下次覆盖安装/自动更新会覆盖掉该修改，修复需随 1.0.6 发版）。

### 万一以后再遇到"启动不了"（旧版本/其它机器）

1. 任务管理器结束所有 `Zhijian.exe`（主进程 + 子进程）；确认 5056 无监听。
2. 直接重新打开应用（`api-ready.json` 陈旧不影响启动；`local-port.json` 不要删）。
3. 注意：**不要**在设置了 `ELECTRON_RUN_AS_NODE=1` 的终端里启动 Electron 程序——那会让 `Zhijian.exe` 以纯 Node 模式运行、几百毫秒内静默退出（本次排查中 DSH 的 shell 环境就带这个变量，所以命令行启动需要先清掉）。

---

## 二、"启动慢" = 12.15s 是"覆盖安装后首次 + 重启后首次冷机"的叠加，不是日常

| 场景 | 主窗口出现 | 说明 |
| --- | --- | --- |
| 10-31 00:09 常规启动（同程序） | **2.65s** | 日常水平 |
| **10-31 11:51 覆盖安装 + 重启后首次** | **12.15s** | 用户抱怨的那次 |
| 今天修复后实测（冷启动） | 5.2–6.4s | 冷机 JIT/杀软首扫仍在，PG 阶段 2–3.6s 波动 |

11:51 那 12.15s 的构成：**数据库结构初始化 5.42s（44.6%，其中全量 DDL 4.84s）** + 便携 PostgreSQL 3.59s（29.5%，PG 自身只 1.72s，其中 1.36s 是上次非正常关机后的崩溃恢复） + 外壳 ready→health 1.29s（含 `waitForHealth` 被调用两次） + .NET 启动 0.91s + 建窗首帧 0.55s。冷机全局放大 2.3–2.5 倍（NVMe 上说明是 JIT + Defender 首扫，不是磁盘）。

- 全量 DDL 的触发是 `SchemaStamp`（指纹含程序集 MVID，`SchemaStamp.cs:51`）。**后续启动不会重复**，只在"再次发版/TryReadAsync 读失败/开发构建共用同一 pgdata 改写指纹/`MAF_FORCE_SCHEMA_INIT=1`"时再现。本次 11:51 的具体触发证据有冲突（安装的 dll 与上次运行的 dll SHA256 相同），建议先补 3 行诊断日志定案。
- 便携 PG 的 `pg_ctl -w` 参数与 `postgresql.conf` 没问题，不建议为 1–2 秒关 fsync。

优化清单（收益/风险排序，详见 [startup-slow.md §8](startup-slow.md)）：

1. **O1** 补 stamp 失配诊断日志（零风险，先做）
2. **O2** 指纹去掉程序集 MVID，改显式 `SchemaVersion`（升级后首启 −5s）
3. **O3** 发布 ReadyToRun（冷机整体 −1～2s；当前发布参数没有 R2R）
4. **O4** 外壳 ready 文件出现即建窗、删掉重复的 `waitForHealth`（−0.45～1.2s）
5. **O5** PG 三次建连合一（−0.1～0.5s）
6. **O6** 关机时优雅停 PG，消除下次首启恢复（−1～1.4s；本次修复已让"用户主动退出"走干净停止）

预计：升级后首启 12.15s → 4.5～5.5s，常规 2.65s → 1.7～2.0s。

---

## 三、当前状态（15:26）

- `Zhijian.exe`（pid 20644）运行中，窗口「知间 · 我的学习空间」，127.0.0.1:5056 监听，本次启动到可用 5.8s。
- 知间记忆插件恢复"已连接"（此前"尚未连接知间"正是后端没起来导致的）。
- 仓库改动：仅 `desktop-electron/main.js`（未提交），`node --check` 通过。
