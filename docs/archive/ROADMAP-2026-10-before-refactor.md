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

### ✅ 核心-1 插件数量不设上限 + 热发现（已完成，2026-10-24）

- **结论先说**：代码里本来就没有「最多装多少个插件」的限制（`PluginManifestLoader.Scan` 遍历全部目录不截断、`GetStatuses()` 不分页、注册表不设配额）。真正的问题是两个“看起来像限制”的点，现已处理。
- **落点**：`PluginOptions.StartupConcurrency`（默认 4）；`PluginManager.InitializeAsync` 的串行循环改 `Parallel.ForEachAsync`；新增 `PluginManager.RescanAsync` + `POST /api/plugins/rescan` + `PluginRescanResult`；`PluginsView.vue` 新增「重新扫描」按钮与 `rescanPlugins()`。
- **为什么启动要限流**：原来 `foreach ... await StartSafelyAsync` 是串行的，每插件握手最长 `StartupTimeoutSeconds`=30s，50 个插件最坏 25 分钟。现在并行 4 路，但**数量本身仍不截断**——限的是“同时 spawn 多少进程”，因为每个插件就是一个子进程。
- **热发现**：把插件文件夹拷进 `App_Data/plugins` → 点「重新扫描」→ 直接启动，不必重启 API（重启会中断正在跑的解析任务与对话）。
- **实测证据**（release 版 API，真实插件包）：
  - 拷入 `com.example.sample-platform`（扁平布局，未调安装接口）→ `POST /api/plugins/rescan` → `discovered:["com.example.sample-platform"]`，插件 `Running`、`healthy`、3 个能力。
  - 停用 → 删目录 → 再 rescan → `removed:["com.example.sample-platform"]`，列表回到空。
  - 重新拷入后 rescan → 因为上次停用过（用户意图落库），停在 `Disabled` 且 `lastError=插件已停用`；调 `/enabled` 启用后立即 `Running`。**这是刻意行为：扫描不能覆盖用户的停用意图。**
  - 浏览器实测：「重新扫描」按钮存在且可用，插件卡片正常展示。
- **签名不是门槛（与参考项目一致）**：默认 `Plugins:IntegrityPolicy=warn`，无摘要/无签名的插件**照常安装与启动**，只回一条 `warnings`（`manifest 未提供 integrity.sha256…`），`integrity` 状态显示 `not-provided`。只有显式设成 `enforce` 才会拒绝未签名包。
- **没做**：不做目录文件监听（要靠轮询/手动点）；不做插件市场/远程源（P3-2）；Windows 上运行中的插件目录无法删除（文件锁）。

### ✅ 核心-2 图片 / 音频存储（已完成，2026-10-24）

- **落点**：`LocalDocumentStore`（音频扩展名 + `IsAudioExtension`/`IsImageExtension` + `NormalizeContentType` + `GetMaxSize` 分档）；`Storage:MaxAudioFileSizeBytes`（300 MiB，并计入 Kestrel 请求体上限）；`KnowledgeIngestionService.ProcessAudioAsync` + `SummarizeAudioAsync` + `DescribeImageSafelyAsync`；`fileTypes.ts`（`AUDIO_EXTENSIONS`）；`LibraryView.vue`（图片缩略图、音频内联播放器、「图片」「音频」筛选）。
- **四个直正的缺陷（都是真实使用会撞到的）**：
  1. 音频扩展名**根本不在白名单**，语音消息/音效直接被 400 拒掉。
  2. Content-Type **直接采信客户端值**。浏览器/脚本常给 `application/octet-stream`，导致 `/download` 把图片当二进制下载、`<audio>` 播不出来。现改为按扩展名归一化。
  3. 图片解析**强依赖视觉模型**，没配模型就整篇 `Failed`——连原图都看不到。现在视觉不可用时只丢描述，图片照常保存并显示缩略图。
  4. 音频大小共用文档 50 MiB 上限，一条长录音就被拒。现单独一档 300 MiB。
- **音频链路**：FFmpeg 归一化成单声道 16 kHz wav → Whisper 转写 → 摘要可选（失败只丢摘要）→ 发布。**没检测到语音内容时不报错**，存成 `音频资料：xxx.mp3（未检测到语音内容）`，所以纯音乐/音效/表情包配套音频都能原样存下来。
- **实测证据**（release 版 API + 真实 ffmpeg + 真实 141 MB Whisper 模型）：
  - 上传 `voice.mp3`（故意把 multipart 类型报成 `application/octet-stream`）→ `contentType` 存为 `audio/mpeg`，`GET /download` 返回 `Content-Type: audio/mpeg`，带 `Accept-Ranges: bytes`，`Range: bytes=0-99` → **`206 Partial Content` + `Content-Range: bytes 0-99/48500`**（音频可拖进度条）。
  - 上传 `meme.png` → `contentType: image/png`，`/download` 返回 `Content-Type: image/png`（1029 B 完整）；浏览器 `<img class="cover-thumb">` 解码为 **320×320**，即缩略图真的渲染出来了。
  - 两者都跑完且 `Ready`：音频发布文本 `音频资料：voice.mp3（未检测到语音内容）`（测试音频是纯音调，正好验证了兑底分支），图片发布 `图片资料：meme.png（未生成图片描述：视觉模型不可用）`，均可用 `POST /api/search` 检索到。
  - 自动化：**590 个测试全绿**，新增 `MediaAssetStorageTests`（19 个：9 个音频扩展名分类/接受、图片分类、octet-stream → audio/mpeg 归一化、空类型 → image/png、音频独立额度、未知类型傅底、提示文案含音频）。
- **没做**：表情包分类/标签/收藏夹（现在就是普通资料，靠专题分类）；不做图片自动缩略图生成（直接缩原图）；不做音频波形/时长提取；不做视频关键帧→独立图片资产。

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

### ✅ P1-4 插件级并发配额（已完成，2026-10-24）

- **落点**：新增 `Application/Plugins/PluginInvocationLimiter.cs`（单例，`AcquireAsync` 返回 lease）；`PluginProcessHost.InvokeAsync` 用它替代原来的 `SemaphoreSlim`；`PluginOptions` 新增 `MaxQueuedInvocationsPerPlugin`(16) / `MaxQueuedInvocationsPerCapability`(8) / `CapabilityConcurrencyLimits`；新增错误码 `PluginProtocol.ErrorOverloaded`(-32006) → HTTP **429**；`GET /api/plugins/concurrency` + 详情里的 `concurrency` 字段；`PluginsView.vue` 概览显示「并发占用 / 排队上限 / 能力级并发」。详见 `docs/PLUGIN-LIFECYCLE.md` §14。
- **做法（关键在“有界队列”）**：原来的 `SemaphoreSlim.WaitAsync` 排队**没有上限**，所以「插件只允许 2 并发」在真实使用中没有可感知的表现——10 个并发请求是 2 个跑完、8 个慢慢超时，看起来像卡死。现在先占「队位」（在途 = 并发 + 队列），占不到立刻 429；再拿能力闸门（窄）→ 插件闸门（宽）。
  - `MaxQueuedInvocationsPerPlugin = 0` 的语义是「没有空闲槽位就拒绝」，不是「一律拒绝」——判据是 `running + queued >= capacity + queueLimit`。
  - **先能力后插件**：顺序反了会让调用一边占着插件容量一边空等能力槽位，插件级 `running` 虚高，还会挡住同插件的其它能力（第一版就是这个毛病，被 e2e 采样抓到）。
  - 三条失败路径（占队位失败 / 排队超时 / 调用方取消）都必须把计数与信号量还回去，否则一次拒绝会永久吃掉一个名额。
  - 计数是进程内的（每进程上限，不是集群上限），改配置需重启。
- **验收证据**（release 版 API + 真实插件子进程 `com.example.sample-platform`，`notes.slow-echo` 每次 1500 ms，manifest `maxConcurrentInvocations: 2`）：
  - `MaxQueuedInvocationsPerPlugin=0`，10 个并发 → **`200×2 + 429×8`**，拒绝消息「并发已满（2），且排队已达上限 0」，采样 `running` 峰值 2，结束后 `running=0 queued=0`。这正是验收标准要的「后 8 次得到明确拒绝而非超时」。
  - `MaxQueuedInvocationsPerPlugin=2` + `CapabilityConcurrencyLimits["notes.slow-echo"]=1`，10 个并发 → **`200×4 + 429×6`**（在途上限 2+2），插件级 `running` 峰值 **1**、能力级 `notes.slow-echo 1/1`，结束后归零——证明能力级上限是在插件预算里再切一刀，而不是替换它。
  - 启动日志打印生效值：`Plugin invocation limits: queue per plugin=2, queue per capability=8, capability limits=notes.slow-echo=1`。
  - 自动化：**571 个测试全绿**，新增 `PluginInvocationLimiterTests`（9 个，含「queue=0 只拒多余那个」「排队超时不挂死」「调用方取消不算超载」「拒绝不吃名额」「重复 Dispose 不放大容量」）。
  - 界面实测（浏览器）：插件详情概览显示「并发占用 0/2」「排队上限 2」「能力级并发 notes.slow-echo 0/1」。
- **没做**：不做跨进程/集群配额；`CapabilityConcurrencyLimits` 没有热更新（改完重启）；队列只计数量、不提供优先级（插件同步走任务队列，那一层已有 `JobPriorities` 与 `TypeLimits`）。

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

### ✅ P2-1 混合检索与重排（已完成第一版，2026-10-24）

- **做了什么**：
  - **中文分词在应用侧做**：PostgreSQL 默认解析器会把整段中文当成一个词，pg0 自带的 PG 也没有 `zhparser` / `pg_jieba`，所以 `Domain/Knowledge/LexicalTokens.cs` 把中文切成二元组、拉丁词转小写，写入新列 `kb_chunks.lexical_tokens`。
  - **GIN 表达式索引**：`ix_kb_chunks_lexical ON kb_chunks USING GIN (to_tsvector('simple', coalesce(lexical_tokens, '')))`，启动时 `CREATE INDEX IF NOT EXISTS`。新切片在 `PublishChunksAsync` 就带分词；历史切片由 `LexicalIndexWorker` 后台分批补齐（空转只发一条 `LIMIT 1` 探测）。
  - **词法检索**：`SqlSugarLexicalSearchRepository` + `LexicalSearchSql.SearchSql`，参数化 `to_tsquery('simple', @query)`、`ts_rank` 排序，返回与向量链路同形状的 `RetrievedChunk`（含 `ChunkId` / `ChunkIndex`）。
  - **RRF 融合**：`ReciprocalRankFusion.Fuse`，`score = Σ 1/(k + rank)`，k 默认 60。只吃排名不吃分数——余弦在 0–1、`ts_rank` 常见 0.0x，线性加权必须按语料重新调参。两路都命中的切片标记 `matchSource=hybrid`。
  - **三种模式**：`POST /api/search` 的 `mode`（`hybrid` 默认 / `vector` / `lexical`）并返回 `strategy`；`Retrieval:HybridEnabled=false` 可整体退回纯向量。回答侧 `retrieval.strategy` 随引用元数据落库并写进导出。
  - **可复跑评估集**：`scripts/Evaluate-Retrieval.ps1` + `scripts/retrieval-eval/default.json`，按 fixture 跑指定模式，输出 recall@K / MRR 与逐题排名表，`-OutFile` 存档 JSON 以便改动前后 diff。
- **验证**：
  - 单测 614 通过（`HybridRetrievalTests` 13 项：中文二元组、拉丁/数字切分、tsquery 去重与 OR、RRF 排序与去重、单路降级、模式解析、SQL 参数化与索引表达式一致性）。
  - 真机（发布版 console + pg0 库 + 真 bge-small-zh 向量）：启动日志 `Lexical token backfill: +9 chunks` → `finished: 9 chunk(s) tokenized`，GIN 索引创建成功；`mode=vector` 返回 3 条（含无关的 `voice.mp3`），`mode=lexical` 返回 2 条且 `ts_rank` 0.068 / 0.030，`mode=hybrid` 把两路都命中的两条标记为 `matchSource=hybrid`；评估脚本跑通三种模式并写出 JSON 报告。
- **诚实的局限**：
  - 当前语料只有 3 份资料，**三种模式 recall@5 都是 1.0、MRR 都是 1.0——这是烟雾测试，不是效果证据**。有意义的对比需要几十份同主题资料 + 会真正混淆的问题集（`scripts/retrieval-eval/README.md` 写了怎么造）。
  - **交叉编码器重排（cross-encoder rerank）与拒答阈值未做**：rerank 需要专门的 rerank 模型（本地 ONNX 或云端），当前没有可用模型；拒答阈值要按语料统计相似度分布才有意义，先留空。
  - 中文分词是二元组而非词典分词：召回偏高、精度依赖 `ts_rank`；长文档中的同义改写（"协议" vs "约定"）仍然只能靠向量。

### ✅ P2-2 学习笔记与资料关联（已完成，2026-10-24）

- **做了什么**：
  - **笔记能记住"依据"**：新增 `Domain/Notes/NoteSource.cs`（documentId / fileName / chunkIndex / similarity / snippet）。笔记从"关于某份资料"变成"这句话出自那份资料的第几段"，半年后重读也能核对。
  - **聚合扩字段**：`Note` 增加 `TopicId`、`Sources`、`SourceSessionId`；`Create` / `Restore` / `Update` 的新参数全部是可选尾参（既有 8 参数调用与测试不受影响）。`Update` 里 `null` = 保持不变、空集合 / `Guid.Empty` = 显式清空——否则"取消归类"无法表达。
  - **归一化在领域层**：按（资料 + 切片序号）去重、丢掉空 ID、文件名截 200、片段截 240 并折叠换行；超过 20 条直接截断而不是报错（引用是依据，不该让保存失败）。
  - **落库**：`kb_notes` 增加 `topic_id` / `sources`（JSON 文本）/ `source_session_id`；`SqlSugarNoteRepository.EnsureColumns()` 显式补列——`CodeFirst` 只在建表时生效，旧库不会自动多列（与 `knowledge_chat_messages` 同一个坑）。
  - **反向查询**：`GET .../notes?documentId=&topicId=`，在内存里过滤（与既有 query/category 一致）。资料详情页由此可以回答"哪些笔记引用了它"。
  - **问答 → 笔记**：助手消息下方新增「存为笔记」，`App.vue` 组装 `## 问题 / ## 回答 / ## 引用资料（序号 + 段落 + 相关度）`，并把引用写成 `sources`、会话 ID 写成 `sourceSessionId`；不选分类决策，固定 `问答笔记` + `问答/有引用` 标签。
  - **批量导出**：`POST .../notes/export`（`noteIds` 为空=全部）→ 单份 Markdown，含分类/标签/置顶/来源会话/引用块；`NoteExportFormatter` 是纯函数，可单测。
  - **前端**：笔记卡片显示专题 chip 与引用 chip（前 2 条 + `+N`），点引用 chip 直接打开该资料原文；编辑器增加「所属专题」下拉；页头「批量导出（N）」只导出当前筛选结果。
- **验证**：
  - 单测 639 通过（新增 `NoteSourceTests` 9 项：来源派生、去重、上限、片段截断与 NaN、显式清空、往返；`NoteExportFormatterTests` 5 项）。
  - 真机端到端（发布版 console + pg0 库）：对**已存在**的 `kb_notes` 表写入带 `topicId`/`sources`/`sourceSessionId` 的笔记成功（证明补列生效）；`?documentId=` 命中 1 条、另一份资料命中 0 条、`?topicId=` 命中 1 条；`POST .../notes/export` 返回 `text/markdown`，正文含 `### 引用资料（2 条）`、`- [1] 插件协议要点-ea65061f.md（第 1 段） · 相关度 83%` 与摘录。
  - 浏览器：笔记页卡片显示专题「插件体系」+ 两条引用 chip（`插件协议要点… · 第 1 段`）、`批量导出（2）`；点引用 chip 打开原文阅读弹层；点助手消息「存为笔记」提示 `已存为笔记，附带 5 条引用。`，新卡片标题取问题、正文含引用清单、标签 `问答/有引用`、chips 显示 2 条 + `+3`；编辑器「所属专题」下拉正确回显。
- **诚实的局限**：
  - 反向查询与搜索过滤仍在内存里做（先把整个知识库的笔记读出来再过滤）。单库笔记上千条以后需要下推 SQL。
  - 笔记正文里没有做「引用锚点」渲染——引用是结构化数据 + 卡片上的 chip，正文里仍是一段文字。

### ✅ P2-6 知识库包导出 / 导入（已完成，2026-10-24）

- **做了什么**：
  - **格式**：`.zjpack.zip` = `manifest.json` + `files/NNNN.ext`。清单记录 format/version、知识库名、专题、资料元数据（含 SHA-256 与包内路径）、笔记（含引用与专题）、`warnings`。**不导出切片与向量**：向量绑定具体模型版本，换机器必然失效；导入端重新解析建索引，慢一点但对得上。
  - **导出**：`GET /api/knowledge-bases/{id}/package`，临时文件 + `DeleteOnClose` 流式返回（不把整包读进内存），统计写进 `X-Package-*` 响应头；磁盘上找不到的原始文件不中断导出，记进 `warnings`。
  - **导入**：`POST /api/knowledge-bases/import`（multipart）→ 一律**新建**知识库（重名自动让路：`名称（导入）`、`名称（导入 2）`…），所有 ID 重新生成并按包内映射重写笔记的 `sources` / `topicId` / `sourceDocumentId`；`sourceSessionId` 清空（会话不随包走）；资料以 `Queued` 状态入队，由既有解析队列重新切片建索引。
  - **校验**：格式标识 → 版本上限 → 清单可解析 → SHA-256 逐个比对（不一致跳过并告警）→ 扩展名/大小走与上传同一套 `LocalDocumentStore` 规则（落盘后再核一次实际长度，声明值不可信）；包上限 4 GB、资料 5000、笔记 20000。
  - **分层**：新增领域端口 `Domain/Knowledge/IKnowledgePackageStore`，实现 `Persistence/KnowledgePackageStore` 把 `KnowledgeBaseStore` 收窄——应用层不认识持久化类型（分层检查因此从 2 处违规回到 0）。
  - **前端**：设置页新增「知识库备份与分享」卡片（选库 → 导出 / 选文件 → 导入），导入成功后自动刷新知识库列表、切到新库并跳到「我的资料」看解析进度。
- **验证**：
  - 单测 639 通过（新增 `KnowledgePackageTests` 11 项：最小包、未知字段前向兼容、缺清单、外来格式、版本过高、坏 JSON、条目哈希、重名让路（含大小写）、camelCase 与中文不转义）。
  - 真机端到端：导出 11 份资料 + 2 篇笔记 + 1 个专题（`Content-Length: 11499855`，`X-Package-Documents: 11 / Notes: 2 / Topics: 1 / Warnings: 0`），包内 12 个条目、`manifest.json` 带 SHA-256；导入得到 `默认知识库（导入）`（11/1/1），再导一次得到 `（导入 2）`，第三次 `（导入 3）`（11/2/1）——说明重名让路与笔记增量都正确。
  - 导入库里的资料全部 `Ready` 且有切片，`POST /api/search`（`mode=hybrid`）在导入库返回 `插件协议要点…`（hybrid）、`欢迎使用…`（hybrid）、`meme.png`（vector）——**确实重建了索引并被检索到**；导入笔记的 `sources` 指向新资料 ID、`topicId` 指向新专题 ID，`?documentId=` 反向查询命中 1 条；资料与专题映射正确（原库资料归入专题后导出，导入库该资料的 `topicId` 是新建的专题 ID，`documentCount=1`）。
  - 错误路径：外来格式 → 400 `不是本应用的知识库包（format=other-app）`；非 zip → 400 `导入包不是有效的 zip 文件，可能选错了文件。`；畸形 multipart → 400 `上传内容不完整或格式错误，请重试。`
  - 浏览器：设置页卡片渲染出库选择器（5 个库）、说明文案与两个按钮；点「导出知识库包」提示 `已导出「默认知识库」：资料 11 份、笔记 2 篇、专题 1 个。`
- **诚实的局限**：
  - **没有合并模式**：只能导入为新库。合并要处理同名资料/专题/笔记的冲突策略，每一种都要用户决策，一期先不猜。
  - **失败不回滚**：导入到一半失败（例如磁盘写满）会留下"半成品知识库"和已落盘的文件，需要用户自己删库；没有事务包住文件系统操作。
  - **不导出对话历史**（故 `sourceSessionId` 清空）与插件数据；"市场分享"目前只是"把 zip 发给别人"，还没有托管、评分、一键安装。

### P2-3 视频关键帧与视觉分析

- **现状**：`ARCHITECTURE.md` §3 标注为"当前 MVP"，默认关闭。
- **要做**：关键帧抽取 + 视觉模型描述 + 时间戳对齐；离线模型准备说明。
- **验收**：一段 5 分钟视频能按"画面内容"检索到具体时间点。

### P2-4 AI 伴侣格式适配

- **现状**：只支持 UTF-8 JSON/TXT 导出；微信/QQ 私有格式未适配；伴侣检索是关键词而非向量。
- **要做**：常见导出格式解析器；把伴侣历史纳入向量检索（独立索引，不进知识库）。

### ✅ P2-5 问答引用元数据（已完成，2026-10-24）

- **做了什么**：
  - `RetrievedChunk` 增加 `ChunkId` / `ChunkIndex`，两条向量检索 SQL（新版索引 / 旧版直查）都投影 `c.id`、`c.chunk_index`，引用第一次能定位到"哪份资料的哪一段"。
  - 新增 `Persistence/KnowledgeChatCitations.cs`：`KnowledgeChatCitation`（documentId / fileName / similarity / rank / chunkId / chunkIndex / snippet）、`KnowledgeChatRetrieval`（indexId / embeddingSpecName / warning / legacyIndex）、`KnowledgeChatAnswerMeta`（检索上下文 + 工具调用审计 + 网页来源）与 `KnowledgeChatJson` 编解码（读写都防御脏数据）。
  - `knowledge_chat_messages` 增加 `citations`、`answer_meta` 两列；`KnowledgeChatStore.EnsureMessageColumns()` 对旧库显式补列（`CodeFirst` 只在建表时生效）。
  - `KnowledgeChatService` 改走 `SearchWithDiagnosticsAsync`，引用 + 检索上下文随助手消息一起落库；`KnowledgeChatResult` 增加 `Retrieval`。
  - `GET /{session}/messages` 直接返回引用与元数据；`KnowledgeChatExportFormatter` 为每条助手消息追加「引用资料（序号 / 段落 / 相关度 / 片段）」与「网页来源」块。
  - 前端：`Citation` / `KnowledgeChatMessage` / `AssistantMessage` 契约补齐；`AssistantView` 渲染带序号与相关度的引用 chip（悬浮显示切片片段），历史会话重开时同样还原；`extended.css` 新增 `.citation-*` 样式。
- **验证**：
  - 单元测试 601 通过（新增 `KnowledgeChatCitationTests` 8 项 + 导出格式 2 项），覆盖序号、片段截断、JSON 往返、脏数据降级、SQL 投影切片标识。
  - 真机端到端（发布版 console + pg0 + 真 bge-small-zh 向量 + 本地假 LLM 替身）：`POST .../chat` 返回 5 条引用（`rank=1..5`、`chunkId=2459/2458/1372/2462/2464`、`retrieval.indexId=2de6e1ea-…`、`embeddingSpecName=bge-small-zh-v1.5`、`legacyIndex=false`）；重开 `GET .../messages` 引用与元数据完整读回；`GET .../export` 输出 `> 引用资料（5 条，按相关度排序）` + `> - [1] 插件协议要点-ea65061f.md（第 1 段） · 相关度 50%` + 片段；浏览器打开助手页，历史消息下方渲染带序号的引用 chip。
- **遗留**：`legacyIndex=true`（旧版直查）路径与「检索提示」warning 样式仅由单元测试覆盖，未在真机上构造降级场景。

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
| ~~发布脚本清空 `App_Data`~~ | **已修（2026-10-24）**：`Publish-Portable.ps1` 以前会对整个输出目录 `Remove-Item -Recurse`，把 `App_Data`（上传文件、已装插件、媒体资产）一起删掉——数据库行在 `%LOCALAPPDATA%` 里活着，结果是“资料列表还在、点开 404”的静默数据丢失。现在发布前把 `App_Data` 移到 `.bak`、发布后合并回来；要重置数据必须显式 `-FreshData`。实测：重发布后哨兵文件存活，`-FreshData` 后消失。 |
| `publish/` 目录 | `console`/`desktop`/`releases` 都是构建产物，可随时删除重建（安装包约 310 MB） |
| 根目录临时文件 | `probe-out.txt`、`probe-out2.txt`、`rowdump.txt`、`api.ts` 是早期排查留下的，确认无用可删 |
| `App_Data/probeB` | 旧探针数据库目录，确认无用可删 |
| 测试项目录冲突 | 在 Desktop 路径下 `dotnet test` 的 testhost 会崩，必须用 `-p:OutputPath=C:\Temp\maftests\`（已记录在仓库记忆） |

---

## 建议的下一步

> 阶段 11 之后的状态：P0（除代码签名）、P1（除签名工作流）、P2-1、P2-2、P2-5、P2-6 已完成；跨平台是「代码完成、非 Windows 未实测」。
> 用户口径：证书先跳过，先把功能补齐，之后开源到 GitHub 让使用者自行安装/打包。

1. **在真实语料上跑一遍 `scripts/Evaluate-Retrieval.ps1`**——P2-1 的检索链路和评估集已经就位，但 3 份资料的库跑不出信号；把几十份同主题资料导进去，才有"混合到底比纯向量好在哪"的答案。要提升的话优先补 cross-encoder 重排与拒答阈值。
2. **知识库市场（P3-2 的前置）**——包格式与导入导出已经打通（P2-6），缺的是托管：包的上传/浏览/下载/评分、一键安装、版本与作者信息。建议先在 GitHub Releases 上发 `.zjpack.zip` 手动分享，把格式用一阵再决定要不要平台。
3. **P1-6 签名密钥工作流**——界面已经能粘公钥了，但没有"生成密钥 → 算摘要 → 签包"的工具链，`IntegrityPolicy=enforce` 实际上没人能走通。纯本地、可验收、不依赖证书。
5. **把跨平台从「代码完成」推到「实测完成」**——需要能联网的环境下载 pg0/ffmpeg 各平台二进制，再跑 `docker compose up -d --build` 与一次真机 Linux 启动。
6. **P0-5 代码签名**——目前卡在证书；没有证书只能先写配置口径，不能验收。
7. P2-3/P2-4 与 P3 按需推进；P3 只在真的要多用户/对外提供服务时再启动。
