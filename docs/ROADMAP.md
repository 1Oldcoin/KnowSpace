# 未完成事项清单（ROADMAP）

> 这份文件只列**目前没有实现**的东西，按"先能装能用、再补体验、最后做平台化"排序。
> 每项都写了现状、落点与验收标准，方便一件一件捡起来做，不需要重新调研。
>
> 已完成的历史阶段见 `ARCHITECTURE.md` §9 与 `PROJECT_DOCUMENTATION.md`；这里不再重复。

---

## P0 · 让 Electron 桌面版真正能交付

阶段 9 删掉了 Photino 桌面工程与 Velopack；**阶段 10 补上了 electron-builder + NSIS 安装包**。
剩下的交付缺口是自动更新、真实环境下的原生依赖验证与代码签名。

### ✅ 已完成（阶段 10，2026-10-23）

| 项 | 落点 |
| --- | --- |
| 用 electron-builder 打 NSIS 安装包 | `desktop-electron/electron-builder.yml`（`files` 只含壳文件，`extraResources` 把 `publish/desktop` 放到 `resources/api`）、`scripts/Build-ElectronInstaller.ps1`、`npm run dist` / `dist:full` |
| 卸载/升级前停止便携数据库 | `MyFirstMafAgent.Api.exe --stop-portable-postgres`（`InstallerArguments` + `Program.cs` 最前面处理，只调 `PortablePostgresHost.TryStopForUninstall` 后退出）、`desktop-electron/build/installer.nsh` 的 `customUnInstall` |

实测：静默安装 → 启动（sidecar 从 `resources/api` 拉起、`/api/health` 200、SPA 正常）→ 强杀进程留下 pg0（10 个 `postgres.exe`）→ 静默卸载：安装目录删除干净、`postgres.exe` 归零、`%LOCALAPPDATA%\MyFirstMafAgentData` 完整保留。

### ✅ P0-3 自动更新（已完成，2026-10-24）

- **落点**：`desktop-electron/updater.js`（新增，`createUpdater`）、`main.js`（菜单「帮助 → 检查更新」、5 个 IPC、退出钩子）、`preload.js`（`desktopHost.{getAppInfo,getUpdateState,checkForUpdates,downloadUpdate,installUpdate,onUpdateStatus}`）、`electron-builder.yml` 的 `publish`（generic provider 占位）、`SettingsView.vue` 的「桌面版更新」卡片、`test/updater.test.js`（8 个用例）。
- **做法（两个必须绕开的坑）**：
  1. **不能依赖 `autoInstallOnAppQuit`**：本应用的 `before-quit` 会 `preventDefault()` 后走 `app.exit(0)`，那条路径**不会**执行安装器。所以关掉自动安装，只在用户确认后显式 `quitAndInstall(false, true)`。
  2. **安装前必须先停 API**：安装器要替换 `resources/api` 下的文件，便携 `postgres.exe` 会锁住目录（P0-4 已经踩过）。因此 `installNow()` 的顺序被固定为 `await beforeInstall()（= stopApi）` → `markQuitting()` → `quitAndInstall()`，并有专门的用例断言这个顺序。
- **更新源三级覆盖**：`MAF_UPDATE_URL` 环境变量 → 用户数据目录 `update-feed.json` 的 `{ "url": ... }` → 安装包内 `app-update.yml`。同一个安装包可以服务自建静态目录/内网发布点，不必重打包。
- **节奏**：启动后 20 秒首次检查，之后每 6 小时；发现新版本 `autoDownload=false` + 手动 `downloadUpdate()`，下载完提示「重启并安装」。开发模式（`app.isPackaged=false`）直接 `disabled`，不发请求。
- **测试基础设施**：`updater.js` 接受注入的 `app` / `autoUpdater`，`node test/updater.test.js` 无需 Electron、无需联网。**教训**：本机 Node 的 `Module._load` 猴补丁对嵌套 `require()` 不生效，第一版测试因此静默拿到空监听器（假绿）。改成构造注入后才有真实断言。
- **验收证据**：`node test/updater.test.js` → `8/8 passed`（含「先 stopApi 再 markQuitting 再 quitAndInstall」与「stopApi 抛错则绝不调安装器」）；`electron-builder.yml` 通过 schema 校验；`node --check` 通过。
- **没做/未验证**：`electron-builder --publish always` 未跑通（本机拉 `winCodeSign` 超时），因此 `latest.yml` 生成与「旧版本 → 新版本」端到端升级未实测；未做差量下载（blockmap 已有但未验证）、未做灰度/回滚策略。

### ✅ P0-4 在真实安装环境里验证原生依赖（已完成，2026-10-23）

实测方式：静默安装 → 启动 Electron 壳确认 sidecar 拉起 → 用安装目录里的 `resources\api\MyFirstMafAgent.Api.exe` 单独跑一遍 API → 走真实上传链路。

| 依赖 | 结果 |
| --- | --- |
| ONNX（`bge-small-zh-v1.5`） | ✅ 从 `ExternalTools/Embedding` 加载，日志有 `Creating embedding generator ... onnx-local/bge-small-zh-v1.5`；文本资料导入到 `Ready`，生成 4 个分块 |
| FFmpeg | ✅ 包内 `ExternalTools\ffmpeg\ffmpeg.exe` 能合成测试视频、能转码；视频进入 `Whisper 语音转写` 阶段 |
| Whisper | ✅ 可运行，但**模型不随包分发**：首次使用自动下载 `ggml-base.bin`（141 MB），之后转写正常（有语音的视频产出了转写内容） |

**发现的两件事（已记入待办）**：

1. **离线环境首次导入视频会失败**：Whisper 模型需要联网下载。→ **已解决**（见下方「✅ 离线预置 Whisper 模型」）。
2. **视频链路依赖聊天模型**：转写完成后要过一次总结；未配置有效 Chat 端点时该资料会 `Failed` 并给出 `Chat API returned 401`（文本资料不受影响，只是缺摘要）。→ **已改为摘要可选**：没有聊天模型时只丢摘要，转写内容照常发布并可检索。

升级路径仍未验证（依赖 P0-3 自动更新）。

### ✅ 离线预置 Whisper 模型（已完成，2026-10-23）

- **落点**：`Documents/WhisperModelManager.cs`（定位/校验/导入/下载/删除）、`Endpoints/MediaEndpoints.cs`（`/api/media/whisper*`）、`Documents/WhisperTranscriptionService.cs`（改为按文件指纹重建推理对象）、`SettingsView.vue` 的「语音转写（Whisper）」卡片。
- **做法**：模型不塞进安装包（安装包会多 141 MB～3 GB）。改成一件可见资产：`GET /api/media/whisper` 看状态/体积/下载进度，`POST /whisper/import-raw` 导入本地 ggml 文件，`POST /whisper/download` 后台下载（202 + 轮询），`DELETE /whisper` 删除。
- **离线体验**：`Media:AutoDownloadWhisperModel=false` 时缺模型立即报
  「Whisper 模型未安装（路径）。离线环境请到「设置 → 语音转写（Whisper）」导入 ggml 模型文件…」而不是挂在一个必然失败的下载上；即使开着自动下载，下载失败也会改写为同一条处置建议。
- **顺带修掉的两个真缺口**：
  1. 视频摘要改为可选——没有聊天模型时只丢摘要，转写内容仍会发布（之前整篇 `Failed`）。
  2. ggml 魔数校验：真实文件把 `0x67676d6c` 按 `uint32` **小端**写入，磁盘上是 `6C 6D 67 67`（“lmgg”）；只认 ASCII “ggml” 会把官方模型全部拒掉（第一版就是这么错的，靠 e2e 才暴露）。
- **验收证据**（release 版 API + 真实 141 MB 模型 + 真实 mp4，浏览器实测）：
  - 空目录：`exists=false`；上传 `.pt` → 400「必须是 .bin」；上传 ZIP 改名 `.bin` → 400「文件头应为 ggml / lmgg」。
  - `POST /whisper/download` → 202，轮询到 `141 MB / exists=true`；设置页同步显示「下载中 · 已获取 15 MB」→「已就绪 / 141 MB」。
  - 缺模型时上传视频：`Failed` 且错误正是上面那句处置建议（进度停在 35% 的 Whisper 阶段）。
  - 装好模型后重传同一视频：`Processing 35% → 70% → Ready 100%`，内容为「视频转写：语音转写：[00:00:00-00:00:10] [BLANK_AUDIO]」，且**能被 `/api/search` 检索到**（样例视频本身无语音，所以只有 `[BLANK_AUDIO]`）。
  - 设置页删除模型 → `未安装`、按钮变「联网下载」；导入真模型 → 「模型已导入…」；导入后立刻再传视频 → `Ready`（证明热替换生效，无需重启）。
- **配置**：`Media:AutoDownloadWhisperModel`（默认 `true`，离线部署设 `false`）、`Media:MaxWhisperModelBytes`（默认 3 GiB，同时计入 Kestrel 的请求体上限）。
- **没做**：不在安装包里附带模型（体积）；不提供多模型并存/切换（仍由 `Media:WhisperModel` + `WhisperModelPath` 决定单个模型）。

### P0-5 代码签名（阻塞：需要证书）

- **现状**：未签名，SmartScreen 提示"未知发布者"。
- **要做**：买证书后在 electron-builder `win.signingHashAlgorithms`/`certificateFile` 配置；同时给 `MyFirstMafAgent.Api.exe` 签名。
- **验收**：安装包与 exe 属性页显示发布者；SmartScreen 不再拦截。
- **卡在哪**：这需要一张**已购买的代码签名证书**（或者走 Azure Trusted Signing / 自建 CA + 内部分发受信任根）。没有证书就没有可验的产物，本项目前只能停在“写好配置与流程文档”，不能宣布完成。

### ✅ P0-6 跨平台打包（Windows / Linux / macOS / Docker）（已完成代码部分，2026-10-24）

- **背景**：原来三处 Windows 绑定让 Linux/Docker 跑不起来：便携库写死 `pg0-windows-x86_64.exe`、ffmpeg 写死 `ffmpeg.exe`、插件入口按 `.exe` 后缀判定「是原生程序还是宿主命令」。
- **落点**：
  - `Infrastructure/PlatformRuntime.cs`（新增）：`RuntimeIdentifier`、`ExecutableSuffix`、`Pg0VendorFileName`、`ToolCandidates`（5 级查找顺序）、`ResolveTool`、`EnsureExecutable`（Unix 补 +x）。
  - `Plugins/PluginEntryCommand.cs`（新增）：按「不含路径分隔符 / 不是已知脚本扩展名 / 包内无同名文件」判定为宿主命令，替掉 `.exe` 后缀猜测。
  - `PortablePostgresHost`：按 RID 缓存为 `pg0-<rid>[.exe]`，找不到时列出全部候选路径 + 下载地址。
  - `PortablePostgres:ExecutablePath` / `Media:FfmpegPath` 默认改为空 = 自动定位；配了但不存在也回落，而不是启动失败。
  - `ExternalTools/pgSQL/<rid>/`、`ExternalTools/ffmpeg/<rid>/`：5 个平台的空目录 + 逐条下载说明；`scripts/Download-ExternalTools.ps1` 自动下载。
  - `scripts/Publish-Portable.ps1` 重写：`-Runtime` 6 个 RID、按平台输出目录名、自动清理其它平台二进制、发布后校验关键文件。
  - `Dockerfile`（3 阶段，node 构建前端 → dotnet publish → aspnet 运行时 + ffmpeg）+ `.dockerignore` + `docker-compose.yml`（api + pgvector/pg18）。
- **验收证据**：562 个测试全绿（新增 `PlatformRuntimeTests` + `PluginEntryCommandTests`）；`linux-x64` 发布产出 309 MB 自包含包并清掉 12 项 Windows 文件；`win-x64` 老布局的 `pg0-windows-x86_64.exe` 被自动识别并缓存为 `pg0-win-x64.exe`，`/api/health` → `{"status":"ok","database":"connected"}`；删掉 pg0 后报错列出 5 条候选路径与下载地址。
- **没验证**：`docker build`/`compose up` 未在本机执行（本机没装 Docker）；Linux/macOS 未真机运行；非 Windows 的 pg0/ffmpeg 二进制因网络受限未下载（目录留空 + README）。详见 `docs/DEPLOYMENT.md` §8。

---

---

## P1 · 阶段 8 的收尾（后端已就绪，差界面与几处策略）

### ✅ P1-2 任务中心界面（已完成，2026-10-23）

- **落点**：`FrontEnd/myfist-agent/src/components/JobsView.vue`、`services/api.ts` 的 `listJobs/getJobCounts/listJobTypes/getJob/cancelJob/retryJob/openJobStream`；侧栏入口在 `SidebarNav.vue`，视图注册在 `types.ts` 与 `App.vue`。
- **能力**：四个计数卡片、状态药丸（带数量）+ 类型下拉（选项来自 `/api/jobs/types`）、每行进度条 + `尝试 n/max` + 槽位、取消/重跑、详情弹窗（payload/result 格式化 JSON、七个时间戳、幂等键）、侧栏待处理角标（本页由 SSE 驱动）。
- **实时**：`EventSource` 订阅 `/api/jobs/stream`；`snapshot` 刷计数、`job/progress` 就地打补丁、终态补一次 `GET /{id}` 拿权威值；断线时角标转「轮询中」并退回 5 秒轮询。
- **细节见** `docs/JOBS.md` §9。

### ✅ P1-1 插件管理界面（已完成，2026-10-23）

- **落点**：`FrontEnd/myfist-agent/src/components/PluginsView.vue`、`services/api.ts` 的插件客户端（`listPlugins/getPlugin/installPlugin/setPluginEnabled/savePluginConfig/savePluginPermissions/savePluginScope/restartPlugin/checkPluginHealth/drainPlugin/rollbackPlugin/uninstallPlugin/syncPluginAsync/listPluginSyncStates/listPluginAudit/listTrustedKeys/saveTrustedKey/deleteTrustedKey`）；侧栏入口在 `SidebarNav.vue`，视图注册在 `types.ts` 与 `App.vue`。
- **为界面补的后端字段**：`GET /api/plugins/{id}` 原本只回状态，前端无法渲染配置表单；现已改为异步并在同一响应里补齐 `config`、`configSchema`、`configRequired`、`declaredPermissions`（能力权限并集）、`allowedKnowledgeBaseIds`、`maxDocuments`、`limits`。`declaredCapabilities` 里带 `id`，同步下拉必须用 `id` 而不是 `name`。
- **能力**：插件卡片列表（状态/版本/发布者/能力数/待授权/错误/停用态）、ZIP 上传安装（显示 `warnings`）、六页签详情弹窗——概览（健康/在途/排空/版本/目录/完整性/并发上限/能力清单 + 重启/健康检查/排空/回滚/卸载）、配置（按 manifest JSON Schema 渲染 string/integer/number/boolean/enum/string-array，含必填校验与原始 JSON 兜底）、授权（五个权限勾选）、作用域（知识库多选 + `maxDocuments`）、同步（`platform-connector` 能力 + 目标知识库 → `POST /sync-async` 进任务队列，并展示同步游标表）、审计（时间线）。
- **验收证据**（release 版 API + 真实 ZIP 包，浏览器实测）：安装 1.0.0 → `待授权`；勾上 `network` 保存 → `运行中`；配置表单存 `workspace/contentVersion/syncIntervalSeconds=3600` → `GET` 回读到同一 JSON，且插件用它算出同步间隔；作用域选知识库 + `maxDocuments=50` → 回读一致；立即同步 → 任务队列 `plugin.sync` + `document.ingest` 均 `Succeeded`，同步游标表出现 `success / 3600s / done`；装 1.0.1 后回滚 → 回到 1.0.0 且 `运行中`；停用/启用、卸载均正确。
- **顺手修掉的真问题**：数字输入经 `v-model` 会变成 `number`，`(value ?? '').trim()` 直接抛 `trim is not a function`（配置保存失败）；卡片对 `integrity=not-provided` 也显示「已校验签名」（把状态当布尔用）；卸载后详情面板拿旧 id 每 10 秒 404 弹一次提示。
- **没做**：手动 `POST /{id}/invoke` 的调试入口（Agent 工具调用走工具桥，界面上不提供裸调用）；`limits` 只读展示，不可改。

### ✅ P1-3 受信任公钥界面（已完成，并入 P1-1）

- 实现在 `PluginsView.vue` 的 `TRUSTED KEYS` 区块：列表（KeyId / 插件 / 算法 / 指纹 / 来源 db|config）+ 粘贴 PEM 新增 + 删除。
- **验收证据**：粘贴 `-----BEGIN PRIVATE KEY-----` 被后端拒绝并原样显示「这是私钥，不能作为受信任公钥保存」；粘 ECDSA/rsa 公钥后行内出现 `指纹 61abde1536c38e9f · 来源 数据库`，`GET /api/plugins/{id}` 的 `trustedKey` 立即变化；删除后回到空态。
- **没做**：暂不提供「用浏览器生成密钥对」——生成私钥属于签名工作流（P1-6），不该在渲染进程里做。

### P1-4 插件级并发配额

- **现状**：只有全局 `Jobs:MaxConcurrentJobs` 与插件 manifest 里的 `limits.maxConcurrentInvocations`（进程内信号量）。
- **要做**：按 `plugin × capability` 配置并发上限与队列上限，超额时返回 429/503 而不是无限排队。
- **验收**：同一插件并发 10 次调用、上限 2 时，后 8 次得到明确拒绝而非超时。

### ✅ P1-5 任务优先级抢占与按类型配额（已完成，2026-10-23）

- **落点**：新增 `Application/Jobs/JobTypeQuota.cs`（`TryReserve`/`Release`/`EligibleTypes`/`Snapshot`）；`JobOptions.TypeLimits`（`Jobs:TypeLimits`）；`JobScheduler` 的领取逻辑抽成 `RunOneAsync`；`GET /api/jobs/quotas`；`JobsView.vue` 顶部一行「按类型限流：插件同步 0/2 · 资料解析 1/1」；`services/api.ts` 的 `getJobQuotas`。
- **做法（关键在“先占坑再领取”）**：
  - 最初的写法是“领取前先看一眼计数”，结果 4 个槽位同时通过检查后一起去领取，`limit=1` 被跑成 **4 并发**（被 `Type_limit_blocks_only_its_own_type` 抓到）。现改为 `TryReserve` 用 `ConcurrentDictionary.TryAdd/TryUpdate` 做**原子占坑**，占不到就换下一个候迎类型，因此上限是**硬上限**，也不需要把已领取的任务退回去（退回去会让租约与尝试次数抖动）。
  - 占坑成功 → `ClaimAsync(workerId, [type], …)` → `ExecuteAsync`，`finally` 里 `Release`，所以**异常/停机都不会泄漏名额**。
  - 未配 `TypeLimits` 时走原来的快路径（一次领取所有类型，按 `Priority, NextRunAt, CreatedAt` 排序 = 全局优先级），**行为与旧版一致**；配了配额则领取粒度变为单类型，**跨类型优先级退化为“按类型依次尝试”，类型内部的优先级照旧**。
  - 计数是**进程内**的：多进程部署时每个进程各自限流（“每进程上限”，不是集群上限）。修改配置需重启 API。
- **顺手修掉的真问题（只有真跑才看得到）**：第一版在 `finally` 里无条件 `wake.Notify()`，结果两个空闲槽位**互相唤醒成热循环**——队列空时每秒产生上千次领取查询（日志 6 分钟涨到 62 万行），配额计数长期贴在 `1/1`、`2/2` 上，看起来像“名额泄漏”。改为**只有真正跑完一个任务才唤醒**后，空闲时计数稳定回 `0`。
- **验收证据**（release 版 API + 真实 PostgreSQL 队列 + 真实调度器）：
  - 空闲：`GET /api/jobs/quotas` → `{"maxConcurrentJobs":4,"quotas":[{"type":"document.ingest","limit":1,"running":0},{"type":"plugin.sync","limit":2,"running":0}]}`（`plugin.sync<=2` 来自 `appsettings.json`，`document.ingest=1` 来自环境变量 `Jobs__TypeLimits__document.ingest=1`，证明配置绑定正常）。
  - **硬上限**：4 个槽位 + `document.ingest=1`，一次投 120 个任务，高频采样 49 次：`max_document.ingest_running = 1`、`max_db_running = 1`，全部跑完后 `running` 回 `0`。
  - 未配上限（`TypeLimits` 为空）：`quotas` 两个类型 `limit` 均为 `null`，走旧快路径，60 个任务全部跑完、计数回 `0`。
  - 自动化：541 个测试全绿，其中 `JobTypeQuotaTests`（9 个，含 200 并发占坑只能拿到 3 个名额）与 `JobSchedulerTests.Type_limit_blocks_only_its_own_type`（用真实 `JobScheduler` + `TaskCompletionSource` 卡住槽位：`test.heavy` 占满唯一名额时 `test.echo` 照跑完，放开后两个 `test.heavy` 只串行、峰值恒为 1）。
- **验收标准对照**：原验收写的是“10 个 `plugin.sync`（Low）与 3 个 `document.ingest`（High）同时排队时，资料解析先完成”。现在能给出的保证是：给 `plugin.sync` 设上限后它**占不满所有槽位**，`document.ingest` 总有槽可跑；在**同一类型内部** High 一定先于 Low 被领走。跨类型的纯优先级排序，只有在**不配** `TypeLimits` 时才是全局生效的（这是刻意的取舍，见上）。
- **没做**：不做“抢占已运行任务”（只影响下一次领取）；不做跨进程/集群配额；`TypeLimits` 没有热更新（改完重启）；`JobsView` 只读展示配额，不支持在界面上改。

### P1-6 签名密钥工作流

- **现状**：只能手工用 openssl 签，再把 PEM 贴进接口。
- **要做**：`dotnet` 小工具或脚本：生成密钥对 → 计算包摘要 → 签名 → 写入 manifest 的 `integrity`；`IntegrityPolicy=enforce` 下的完整示例。
- **验收**：一个"签包脚本"能产出可直接安装的签名包；篡改任意文件后安装被拒绝。

---

## P2 · 能力补齐（PRD 里提到但当前是"够用版"〕

### P2-1 混合检索与重排

- **现状**：以 pgvector 相似度为主；`docs/PHASE0-BASELINE.md` 的 ANN 相关列为空。
- **要做**：全文检索（`tsvector` 或中文分词）与向量结果融合（RRF）、可选的 cross-encoder 重排、拒答阈值、检索效果评估脚本。
- **验收**：有一份可复跑的评估集，混合检索相对纯向量在召回/准确率上有可量化提升。

### P2-2 学习笔记与资料关联

- **现状**：笔记表与 API 已有（`/api/notes`、`/api/topics`、`/api/study`），自动摘要与知识点抽取存在，但"笔记 ↔ 资料 ↔ 主题"的联动与导出较弱。
- **要做**：从问答结果一键生成笔记、笔记反向引用资料片段、批量导出。

### P2-3 视频关键帧与视觉分析

- **现状**：`ARCHITECTURE.md` §3 标注为"当前 MVP"，默认关闭。
- **要做**：关键帧抽取 + 视觉模型描述 + 时间戳对齐；离线模型准备说明。
- **验收**：一段 5 分钟视频能按"画面内容"检索到具体时间点。

### P2-4 AI 伴侣格式适配

- **现状**：只支持 UTF-8 JSON/TXT 导出；微信/QQ 私有格式未适配；伴侣检索是关键词而非向量。
- **要做**：常见导出格式解析器；把伴侣历史纳入向量检索（独立索引，不进知识库）。

### P2-5 问答引用元数据

- **现状**：导出 Markdown 时只有角色与文本，持久化消息里没有独立引用结构。
- **要做**：消息级引用（文档 ID/分块 ID/相似度）落库，导出时带上。

---

## P3 · 平台化（"作为所有 Agent 工具的核心存储层"的长线部分）

### P3-1 多用户与认证授权

- **现状**：无认证。只能单机单用户使用，不能公网暴露。
- **要做**：用户表 + 登录（本机优先，可选 OIDC）、知识库级 ACL、API Token 管理、限流、HTTPS 终止说明。
- **验收**：两个用户互相看不到对方知识库；未登录访问 `/api/*` 返回 401。

### P3-2 插件远程源与市场

- **现状**：只支持上传本地 ZIP。
- **要做**：插件索引（签名校验 + 版本列表）、从 URL 安装、更新检查。
- **注意**：远程源会放大供应链风险，必须先有 P0-5 签名 + 强制 `IntegrityPolicy=enforce`。

### P3-3 进程隔离升级

- **现状**：插件与核心同用户同权限，权限闸门只是准入控制（`docs/PLUGIN-LIFECYCLE.md` §13）。
- **要做**：受限令牌（Windows AppContainer / Job Object）或容器化运行；网络与文件系统的最小权限。
- **验收**：一个声明"无 network 权限"的插件无法建立出站连接（不是靠自觉）。

### P3-4 分布式执行

- **现状**：任务调度与插件都是单机进程内。
- **要做**：任务队列的多机领取（现有乐观 CAS 已支持）、插件远程主机协议、时钟与租约的跨机一致性。
- **注意**：`jobs` 表已用"乐观 CAS + 租约"而非进程内状态，因此多机是可行方向，但需要先做 P1-4/P1-5 的配额控制。

### P3-5 插件 SDK 扩展

- **现状**：只有 .NET SDK（`MyFirstMafAgent.Plugins.Sdk`）。
- **要做**：TypeScript/Python SDK（JSON-RPC over stdio 协议已经完全语言无关，只差胶水）。
- **验收**：用 Node.js 20 行代码实现一个能通过 `scripts/Test-PluginContract.ps1` 的插件。

### P3-6 任务 DAG 与批量编排

- **现状**：任务彼此独立，没有依赖。
- **要做**：`depends_on` 关系、失败级联、批量导入的进度聚合。

---

## 顺手记录的小项（低优先，随时可做）

| 项 | 说明 |
| --- | --- |
| `publish/` 目录 | `console`/`desktop`/`releases` 都是构建产物，可随时删除重建（安装包约 310 MB） |
| 根目录临时文件 | `probe-out.txt`、`probe-out2.txt`、`rowdump.txt`、`api.ts` 是早期排查留下的，确认无用可删 |
| `App_Data/probeB` | 旧探针数据库目录，确认无用可删 |
| 测试项目录冲突 | 在 Desktop 路径下 `dotnet test` 的 testhost 会崩，必须用 `-p:OutputPath=C:\Temp\maftests\`（已记录在仓库记忆） |

---

## 建议的下一步

1. **P1-6 签名密钥工作流**——界面已经能粘公钥了，但没有"生成密钥 → 算摘要 → 签包"的工具链，`IntegrityPolicy=enforce` 实际上没人能走通。
2. **把跨平台从「代码完成」推到「实测完成」**——需要能联网的环境下载 pg0/ffmpeg 各平台二进制，然后 `docker compose up -d --build` 与一次真机 Linux 启动。
3. **P1-4 插件级并发配额**——与 P1-5 同一套思路，但在 `plugin × capability` 维度上。
4. **P0-5 代码签名**——目前卡在证书；没有证书只能先写配置口径，不能验收。
5. 之后按需推进 P2；P3 只在真的要多用户/对外提供服务时再启动。
