# 知间本地学习知识库系统｜项目技术说明

> 本文说明当前代码库中已经实现的系统组成、主要处理流程、技术选型和运行边界。规划功能不作为现状描述。

## 1. 项目概述

本项目提供本地学习资料管理与知识库问答能力。用户上传文档后，系统提取文本、切分内容并生成向量，保存到 PostgreSQL/pgvector；提问时检索相关内容，将问题、检索片段和最近对话交给配置的聊天模型，返回回答及资料引用。

系统由 Vue 前端、ASP.NET Core API、PostgreSQL 数据库和本地文件目录组成。聊天模型可配置为本地或 OpenAI 兼容的在线服务；Embedding 使用 API 进程内的 ONNX 模型生成。AI 伴侣功能是独立的数据域，支持导入通用聊天记录、生成表达风格描述并基于历史样本进行对话。

### 当前功能范围

- 创建和切换知识库；默认知识库在 API 启动时自动初始化。
- 上传 PDF、DOCX、TXT、MD、HTML/HTM、常见图片、视频与**音频**（MP3/WAV/M4A/AAC/OGG/OPUS/FLAC/WEBA/AMR），查看资料处理进度、摘要或错误；在资料卡的更多菜单中可阅读原文、重新解析、复制名称、下载原文件或删除资料。图片在卡片上直接显示缩略图、音频内联播放——两者都按原文件保存，可直接当素材/表情包/语音库使用。
- 按知识库管理专题：创建、重命名、删除专题，将资料归入专题，并在侧栏、专题页和资料页按专题筛选；删除专题只解除资料归属。
- 根据资料内容进行向量检索和带引用问答；问答会话及消息写入数据库。
- 管理问答历史：新建、重命名、单条删除、批量删除和 Markdown 导出。
- 导入 UTF-8 JSON/TXT 聊天记录，生成人物表达风格描述，并创建独立的 AI 伴侣会话。
- 前端“设置”可修改聊天模型来源（本地/在线）、接口地址、模型、API Key、显示名称和每日学习目标；配置保存在本机数据库，保存后立即生效。
- 学习打卡：记录每日学习时长与备注，统计连续学习天数、最长连续天数、累计天数和本周学习节奏；总览页展示打卡状态与本周图表。
- 学习笔记完整持久化：新建、编辑、删除、置顶、分类、标签、关键词检索，可从原文阅读器一键存为摘录笔记。
- 原文阅读：对已解析的资料按字符偏移分页加载解析文本，支持字号调整、复制已加载文本和存为笔记。
- 全局搜索支持资料、笔记文本匹配，以及基于向量检索的知识库语义片段；命中项可直接打开原文阅读。
- “我的分身”：随手写或导入文件即可投喂，AI 自动整理成记忆卡、归纳人格并向量化，再以第一人称回答“如果是我会怎么做”。分身数据与知识库完全隔离。
- 聊天模型可调用工具（知识库检索、授权本机目录读写、工作目录读写、分身投喂/分析/代答），并支持双向 MCP 互操作。

学习笔记已写入数据库（`kb_notes`），支持分类、标签、置顶与检索；原文阅读基于已解析切片重建文本页面。

## 2. 项目组成与目录结构

解决方案 `MyFirstMafAgent.slnx` 包含三个 .NET 项目；Vue 前端与 Electron 壳是独立的 npm 工程，不在 `.slnx` 项目清单中。

```text
MyFirstMafAgent/
├─ MyFirstMafAgent.slnx
├─ MyFirstMafAgent.Api/                 # 主 Web API
│  ├─ Domain/                           # 领域层：聚合根、值对象、领域规则、仓储端口
│  ├─ Application/                      # 应用层：用例编排、DTO、命令
│  ├─ Infrastructure/                   # 基础设施层：SqlSugar 仓储、设置、便携数据库、日志
│  ├─ Endpoints/                        # 按业务分组的 HTTP 路由
│  ├─ Contracts/                        # API 请求/响应契约
│  ├─ Agents/                           # 知识库 Agent
│  ├─ Chats/                            # 知识库问答编排
│  ├─ Companions/                       # 聊天记录解析与伴侣对话
│  ├─ Documents/                        # 上传、存储、解析和后台处理
│  ├─ Models/                           # ONNX Embedding 和模型客户端
│  ├─ Options/                          # 配置项和校验
│  ├─ Persistence/                      # SqlSugar Store、表结构和检索
│  ├─ Hosting/                          # 模块化组合根：选项/持久化/知识/分身/工具/MCP/插件/Worker/管线
│  ├─ Plugins/                          # 插件 manifest 发现、子进程宿主、生命周期、工具桥接、摄取下沉
│  ├─ ExternalTools/                    # FFmpeg、Embedding 模型与便携 PostgreSQL 17.9（bin/lib/share）
│  ├─ wwwroot/                          # Vue 构建产物（发布脚本自动写入）
│  ├─ App_Data/                         # 上传文件、分块暂存及模型缓存
│  ├─ ApiHost.cs                        # 业务组合根：启动顺序与生命周期
│  └─ Program.cs                        # 控制台入口：Build → Start → 打开浏览器
├─ MyFirstMafAgent.Plugins.Sdk/         # 插件 SDK：manifest 校验、SemVer、JSON-RPC 分帧、PluginServer
├─ samples/                             # 示例插件（com.example.sample-platform）
├─ desktop-electron/                    # Electron 桌面壳：主进程 + preload + .NET API sidecar
├─ MyFirstMafAgent.Api.Tests/           # xUnit 单元测试
├─ docs/                                # 开发者说明书、插件/MCP 指南、阶段执行口径
├─ assets/app.ico                       # 应用图标（exe / 窗口 / 安装包 / 快捷方式共用）
├─ scripts/                             # 发布、架构检查、基线与桌面契约脚本
└─ FrontEnd/myfist-agent/               # Vue 3 + TypeScript + Vite 前端
   └─ src/
	  ├─ App.vue                        # 页面状态与视图切换
	  ├─ services/api.ts                # HTTP API 客户端
	  ├─ components/                    # 总览、资料、笔记、问答、伴侣等页面
	  └─ types.ts                       # 前端页面数据类型
```

### 开发者文档入口

给第三方开发者看的说明书（从零到发布，含非 C# 语言实现）：

| 文档 | 内容 |
| --- | --- |
| `docs/PLUGIN-DEVELOPER-MANUAL.md` | 插件开发说明书：快速开始、manifest、两类能力、权限与沙箱、打包签名、升级排空、排错、Python/Node 实现 |
| `docs/MCP-INTEGRATION-MANUAL.md` | MCP 接入说明书：把现成 MCP server 当插件载入，以及把本应用当 MCP server 给外部 Agent 用 |

需要更细的运行时内部行为：`docs/PLUGIN-AUTHOR-GUIDE.md`（字段速查）、`docs/PLUGIN-LIFECYCLE.md`（状态机/隔离/审计）、`docs/MCP-ACCESS.md`、`docs/MCP-INTEGRATION.md`。

### 主要后端文件职责

| 文件/目录 | 职责 |
| --- | --- |
| `Program.cs` | 控制台入口：构建并启动 `ApiHost`，按配置自动打开浏览器。 |
| `ApiHost.cs` | 业务组合根：内容根解析、数据目录重定向、便携数据库启动/停止、构建、初始化与端点映射的顺序；控制台版和桌面版共用。 |
| `Hosting/` | 模块化注册：`HostBootstrap`、`HostOptionsModule`、`PersistenceModule`、`KnowledgeModule`、`DocumentsChatsModule`、`AvatarModule`、`AgentModule`、`McpModule`、`PluginModule`、`WorkerModule`、`HostPipeline`、`DatabaseInitializer`。新增宿主只能复用这些模块，不得复制注册。 |
| `Plugins/PluginManifestLoader.cs` | 扫描 `Plugins:RootPath` 下的插件目录（识别 `manifest.json`/`plugin.json`/`skill.json`，解压多一层时下探），解析 `versions/{v}` + `active.json` 布局（兼容扁平布局），校验 manifest 并解析入口（宿主命令 + 包名不要求文件存在）。 |
| `Plugins/PluginPaths.cs` | 插件目录布局的唯一计算入口：版本目录、激活指针、数据目录、暂存目录；清单文件名别名表（`ManifestFileNames`）。 |
| `Plugins/PluginInstaller.cs` | 两段式安装：`StageAsync` 只做 ZIP 解压、manifest/入口校验与摘要校验（不碰生效目录），`InstallFromDirectoryAsync` 负责落盘切换；另含激活、回滚、卸载、旧版本修剪。 |
| `Plugins/PluginIntegrity.cs` | 规范化包摘要（SHA-256）与 ECDSA/RSA 签名校验，`off/warn/enforce` 三档策略。 |
| `Plugins/PluginAuditWriter.cs` | 插件调用与生命周期审计，入参脱敏、超长截断，写失败不阻断业务。 |
| `Plugins/PluginProcessHost.cs` | 启动插件子进程、校验握手回执、下发配置、健康检查与带超时/取消的能力调用；支持两种方言（自有 `plugin/*` 与标准 MCP `initialize`/`tools/list`/`tools/call`）；维护在途调用计数与排空状态（排空期新调用立即 503）。 |
| `Plugins/Isolation/PluginIsolationOptions.cs` | `Plugins:Isolation` 配置（Mode/额外可读写根/容器名前缀/是否重定向用户目录）与对外回显的 `PluginIsolationReport`。 |
| `Plugins/Isolation/PluginIsolationPlanner.cs` | 纯计算：权限声明 → 能力 SID（有 `network` 才给 `S-1-15-3-1`）+ 授权路径（插件目录只读、数据目录可写、`.runtimes` 只读）+ 环境重定向。不碰祖先目录。 |
| `Plugins/Isolation/WindowsAppContainer.cs` | AppContainer 配置创建/SID 派生、ACL 授权（已等价则跳过，避免重复触发继承重整）、容器可读性判定与可照做的错误说明。 |
| `Plugins/Isolation/AppContainerPluginProcess.cs` | `CreateProcess` + `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` 启动受限进程；三条自建管道；`CREATE_SUSPENDED` → 挂 Job（`KILL_ON_JOB_CLOSE`）→ 恢复，杜绝孙进程逃逸。 |
| `Plugins/Isolation/PluginProcess.cs` | 子进程抽象（PID/stdin/stdout/stderr/退出/杀树），让「未隔离」与「隔离」两条启动路径共用宿主后续逻辑。 |
| `Plugins/Isolation/PluginProcessFactory.cs` | 按 `Isolation.Mode` 选择隔离启动 / 降级启动 / 拒绝启动；环境变量白名单（不复制连接串与 API Key）；`node` 等裸命令优先解析到 `plugins/.runtimes`；为 Node 注入 `--preserve-symlinks-main`。 |
| `Plugins/PluginManager.cs` | 插件发现、四道启动闸门（启用/完整性/配置/权限）、退避重启、启停/配置/授权/升级/回滚/卸载；升级＝排空 → 停旧进程 → 落盘 → 启动，另暴露 `DrainAsync` 与 `IsScheduledSyncEligible`。 |
| `Plugins/IPluginRuntime.cs` | 同步用例需要的最小插件运行时端口（调用能力 + 是否参与定时同步），让 `PluginSyncService` 可脱离进程单测。 |
| `Plugins/PluginSyncService.cs` | 平台同步用例：调插件 → 解析批次 → 上限校验 → 幂等摄取 → 写游标 → 记审计；手动/定时/重试共用这一份逻辑，失败保留旧游标。 |
| `Plugins/PluginSyncJobHandler.cs` | `plugin.sync` 任务处理器（参数解析 + 可重试性映射）与补投器（按 `plugin_sync_states` 的间隔入队，幂等键 `plugin.sync:{scopeKey}`）。 |
| `Plugins/PluginTrustedKeyRegistry.cs` | 受信任发布者公钥的合并视图：配置文件优先、数据库次之，按域名段最长前缀匹配；数据库不可用时降级为配置项。 |
| `Application/Plugins/IPluginTrustedKeyStore.cs` | 受信任公钥持久化端口 + `PluginTrustedKeys` 纯函数（校验/拒绝私钥/指纹/前缀匹配）。 |
| `Infrastructure/Persistence/SqlSugarPluginTrustedKeyStore.cs` | `plugin_trusted_keys` 表的 SqlSugar 实现。 |
| `Persistence/PluginTrustedKeySchemaEntity.cs` | `plugin_trusted_keys` 表结构。 |
| `Plugins/PluginToolSource.cs` | 把 `agent-tool` 能力桥接为 `plugin__{plugin}__{capability}` 工具；需确认的副作用能力不桥接给模型。 |
| `Plugins/PluginIngestionSink.cs` | 平台条目摄取编排：查映射 → 判定动作 → 调内容端口 → 写回映射/墓碑；执行知识库作用域与配额。 |
| `Plugins/PlatformItemDecision.cs` | 幂等判定矩阵（Create/Update/Skip/Retract/Tombstone/Reject）与内容哈希规范化，纯函数。 |
| `Plugins/IPluginContentStore.cs` | 摄取内容端口：知识库存在性、创建/替换/删除资料，插件不直接写表。 |
| `Plugins/KnowledgePluginContentStore.cs` | 把内容端口适配到 `KnowledgeIngestionService` / `KnowledgeBaseStore` / `LocalDocumentStore`。 |
| `Endpoints/PluginEndpoints.cs` | 插件状态、健康、调用、同步（同步/异步）、映射条目、游标、启停、配置、授权、作用域、安装、回滚、卸载、排空、受信任公钥与审计接口。 |
| `Application/Plugins/IPluginRegistry.cs` | 插件安装态与审计的持久化端口（安装态、授权、审计记录）。 |
| `Application/Plugins/IPluginItemMap.cs` | 平台条目映射与同步游标的持久化端口（含条目状态常量与游标作用域键）。 |
| `Application/Plugins/PluginAccessPolicy.cs` | 启动权限闸门、授权请求校验、副作用确认判定（纯函数，穷举测试）。 |
| `Application/Plugins/PluginScopePolicy.cs` | 知识库作用域与配额参数校验：拒绝 `Guid.Empty`、不存在的库与负数配额，自动去重。 |
| `Agents/Tooling/AgentKnowledgeScope.cs` | 对话级知识库作用域：越界时返回可读拒绝说明，避免模型静默拿到空结果。 |
| `Persistence/PluginMappingSchemaEntities.cs` | `plugin_platform_items` / `plugin_sync_states` 表结构。 |
| `Infrastructure/Persistence/SqlSugarPluginRegistry.cs` | `plugin_installations` / `plugin_audit_events` / `plugin_platform_items` / `plugin_sync_states` 四表的 SqlSugar 实现。 |
| `Domain/Agents/AgentClient.cs` | 外部智能体客户端的领域模型（能力、工具白名单、知识库范围）与密钥生成/摘要/解析（纯函数）。 |
| `Domain/Agents/McpToolCatalog.cs` | 能力词表与 MCP 工具清单：每个工具所需能力、是否写操作、是否作用于知识库；清单外的工具默认拒绝。 |
| `Application/Agents/IAgentClientStore.cs` | 客户端与调用审计的持久化端口（含客户端命令与审计记录模型）。 |
| `Application/Agents/AgentClientService.cs` | 客户端用例：创建、轮换密钥、改作用域、启停、删除与接入校验（校验能力/工具名/知识库存在性）。 |
| `Application/Agents/AgentCallGate.cs` | MCP 工具准入矩阵（能力 → 工具白名单 → 知识库范围），纯函数可穷举测试。 |
| `Application/Agents/AgentRateLimiter.cs` | 按客户端的令牌桶限流，额度可运行期调整，能给出准确的 `Retry-After`。 |
| `Persistence/AgentClientSchemaEntities.cs` | `agent_clients` / `agent_client_calls` 表结构。 |
| `Infrastructure/Persistence/SqlSugarAgentClientStore.cs` | 客户端与调用审计的 SqlSugar 实现（损坏 JSON 按最小权限处理）。 |
| `Hosting/AgentClientGate.cs` | `/mcp` 准入中间件：认证 → 启停 → 限流 → 工具准入 → 审计。 |
| `Endpoints/AgentClientEndpoints.cs` | 客户端管理、能力/工具清单、密钥轮换与调用审计接口。 |
| `Domain/Chats/ChatHistory.cs` | 聊天历史读取端口与模型：四个来源名（`knowledge`/`avatar`/`companion`/`companion-import`）、会话摘要与消息命中，只读。 |
| `Application/Chats/ChatHistoryService.cs` | 聊天历史回读用例：来源名规范化（写错报错而非返回空）、条数夹紧、错误说人话。 |
| `Infrastructure/Persistence/SqlSugarChatHistoryStore.cs` | 用一条 `UNION ALL` 合并三张会话表与导入记录表的只读视图；LIKE 元字符转义、命中处截取上下文。 |
| `Mcp/ChatHistoryMcpTools.cs` | MCP 工具 `list_chat_sessions` 与 `chat_search_history`（`chat:read`）。 |
| `Infrastructure/PortablePostgres/PortablePostgresHost.cs` | 便携 PostgreSQL 的完整生命周期：定位 `bin`、`initdb` 初始化、`pg_ctl start` 启动、等就绪、创建数据库并执行 `CREATE EXTENSION IF NOT EXISTS vector`、退出时 `pg_ctl stop`。 |
| `Infrastructure/PortablePostgres/PortablePostgresOptions.cs` | 便携数据库配置：`Enabled`、外部连接串优先策略与预检超时、`bin` 目录、端口、根目录、数据目录、账号、启动/停止超时、退出时是否停止。 |
| `Infrastructure/PortablePostgres/PgProcessRunner.cs` | 运行 `pg_ctl` / `initdb` 并把输出重定向到文件（不能用管道，否则 `postgres.exe` 继承写端、父进程等不到 EOF），读日志尾部拼进异常信息。 |
| `Infrastructure/Persistence/ConnectionPreflight.cs` | 启动期外部数据库预检：TCP + 真实连接探测，失败时输出端点、脱敏连接串、失败分类与三条修法（口令永不进日志）。 |
| `Endpoints/ApiEndpointMappings.cs` | 统一注册业务路由模块。 |
| `Endpoints/KnowledgeEndpoints.cs` | 知识库、资料、分块上传、检索、问答及问答历史接口。 |
| `Endpoints/CompanionEndpoints.cs` | 聊天记录导入、档案、风格画像及伴侣会话接口。 |
| `Endpoints/HealthEndpoints.cs` | API/数据库健康检查。 |
| `Documents/LocalDocumentStore.cs` | 校验文件类型和大小，将原始文件安全保存到本地目录。 |
| `Documents/ResumableUploadService.cs` | 分块上传会话、偏移量校验和断点续传。 |
| `Documents/KnowledgeIngestionService.cs` | 文档解析、分块、Embedding 和视频摘要处理；视频会抽关键帧并调用视觉模型描述，转写与画面按时间轴合并成切片。 |
| `Documents/MediaTimeline.cs` | 时间轴切片的纯函数：`[hh:mm:ss]` 行格式、只在段边界切分、时间戳反解出引用跳转点。 |
| `Documents/DocumentIngestJobHandler.cs` | `document.ingest` 任务处理器与补投器：按资料状态推导任务，抢占资料后解析并回写进度/结果。 |
| `Domain/Jobs/JobModels.cs` | 任务类型、状态、优先级、重试退避与状态迁移规则（纯函数）。 |
| `Domain/Jobs/JobRecord.cs` | 任务记录、计数、查询条件、入队命令、租约、失败信息与 SSE 事件模型。 |
| `Application/Jobs/IJobQueue.cs` | 任务队列端口（入队/领取/心跳/进度/完成/失败/取消/重跑/回收/清理）与补投器端口。 |
| `Application/Jobs/IJobHandler.cs` | 处理器契约、执行上下文与任务类型索引（惰性发现类型，避免把 scoped 处理器锁成单例）。 |
| `Application/Jobs/JobScheduler.cs` | N 个并发槽位的调度器：回收 → 补投 → 清理 → 领取 → 执行，每个槽位独立 ExecutionContext。 |
| `Application/Jobs/JobEventStream.cs` | SSE 广播（每订阅者有界通道，慢客户端只丢自己的旧事件）与槽位唤醒信号。 |
| `Application/Jobs/JobOptions.cs` | 并发度、轮询、租约、重试、保留期、排队上限等配置。 |
| `Persistence/JobSchemaEntities.cs` | `jobs` 表结构与索引（含幂等键唯一索引）。 |
| `Infrastructure/Persistence/SqlSugarJobQueue.cs` | 任务队列的 SqlSugar 实现：乐观 CAS 领取、租约续期、退避重试、僵尸回收。 |
| `Endpoints/JobEndpoints.cs` | 任务中心接口：列表/计数/类型/详情/取消/重跑与 SSE 实时进度。 |
| `Hosting/JobsModule.cs` | 任务系统的依赖注入组合（调度器、队列、处理器索引、补投器）。 |
| `Persistence/KnowledgeBaseStore.cs` | 知识库、资料、上传状态、分块和向量检索数据访问。 |
| `Persistence/KnowledgeChatStore.cs` | 知识库问答会话及消息持久化和历史管理（含引用与答案元数据列）。 |
| `Persistence/KnowledgeChatCitations.cs` | 引用元数据模型（引用 / 检索上下文 / 答案元数据）与 JSON 编解码。 |
| `Domain/Notes/NoteSource.cs` | 笔记引用的一条资料依据（资料 ID / 文件名 / 切片序号 / 相似度 / 片段）。 |
| `Domain/Notes/NoteQuery.cs` | 笔记筛选条件值对象（关键词/分类/资料/专题）+ 规范化属性，让过滤在 SQL 里做。 |
| `Domain/Notes/NoteCitationAnchor.cs` | 正文引用锚点 `[1]` / `[2,3]` 的解析：切分文本与锚点、识别悬空序号、导出时转成 Markdown 脚注。纯函数，只认 1-2 位数字（所以链接 `[2024](url)` 与标签 `[TODO]` 不会被误判）。 |
| `Application/Notes/NoteExportFormatter.cs` | 把多篇笔记渲染成一份 Markdown（纯函数，可单测）。正文写了锚点时输出脚注定义，没写时保持原来的编号列表。 |
| `Application/KnowledgePackages/KnowledgePackageManifest.cs` | 知识库包清单模型（format/version、专题、资料、笔记、warnings）与 JSON 选项。 |
| `Application/KnowledgePackages/KnowledgePackageReader.cs` | 包的读取与校验：格式/版本/JSON，条目名归一化（`files\a.md` → `files/a.md`）与索引，条目 SHA-256，重名让路规则。 |
| `Application/KnowledgePackages/KnowledgePackageExporter.cs` | 把知识库打成 zip（临时文件 + `DeleteOnClose` 流式返回）。 |
| `Application/KnowledgePackages/KnowledgePackageImporter.cs` | 把包导入成新知识库：重建 ID 映射、校验哈希、资料重新排队解析；失败按「笔记→专题→资料→知识库」逆序回滚，零内容导入直接报错。 |
| `Persistence/KnowledgePackageStore.cs` | `IKnowledgePackageStore` 实现：把 `KnowledgeBaseStore` 收窄成包需要的动作，并提供回滚用的 `DeleteKnowledgeBaseAsync`。 |
| `Persistence/CompanionStore.cs` | 伴侣档案、导入消息和会话数据访问，以及 `companion_message_vectors` 的写入与向量检索。 |
| `Companions/CompanionMemoryIndexer.cs` | 把目标人物的历史消息向量化到独立表（`companion_message_vectors`），并提供语义检索入口；模型不可用时返回 null 让调用方退回词面检索。 |
| `Companions/ChatHistoryParser.cs` | 聊天导出解析：QQ 块式导出、微信行内时间戳、通用文本、CSV（表头别名/引号/制表符），并剥离 `昵称(QQ号)` 尾部。 |
| `Persistence/*SchemaEntities.cs` | SqlSugar CodeFirst 表、列和普通索引映射。 |
| `Persistence/PgVectorSql.cs` | 集中封装 pgvector 专属 SQL（扩展 DDL、余弦距离运算符、vector 类型转换），其余数据访问均走 SqlSugar 原生 API。 |
| `Models/LocalOnnxEmbeddingService.cs` | 在 API 进程内加载 BGE ONNX 模型并生成归一化向量。 |
| `Agents/KnowledgeAgent.cs` | 通过 Microsoft Agent Framework 调用聊天模型并组织 RAG 上下文。 |
| `Chats/KnowledgeChatService.cs` | 组合历史消息、检索结果、Agent 回答和引用信息（含检索上下文与工具审计），并写入会话。 |

`MyFirstMafAgent` 是独立的 .NET 控制台示例，演示使用 MAF 调用兼容 OpenAI 的聊天模型进行流式对话；它不属于 Vue/API 主系统的启动依赖。

## 3. 架构与实现方式

```text
浏览器
  │ HTTP/JSON
  ▼
Vue 3 + TypeScript 前端
  │ REST API
  ▼
ASP.NET Core 10 Minimal API
  ├─ Endpoints：请求路由和参数校验
  ├─ Documents / Chats / Companions：业务流程编排
  ├─ Agents / Models：Embedding 与聊天模型调用
  ├─ SqlSugar + Npgsql ───────── PostgreSQL + pgvector
  └─ LocalDocumentStore ───────── 本地文件目录
```

后端采用按业务域拆分的 Minimal API，不使用 MVC Controller。`Program.cs` 只负责 Host、依赖注入、中间件及启动初始化；API 定义位于 `Endpoints/`，路由按知识库、问答、资料上传、AI 伴侣等功能分组，并带 OpenAPI 标签。该组织方式保留轻量端点定义，同时可以从文件结构直接定位接口。

开发环境映射 OpenAPI JSON 与 Scalar 页面：`/openapi/v1.json`、`/scalar/v1`。两者仅在 Development 环境开启。

前端以 `App.vue` 管理当前页面、知识库选择和问答数据；各视图由独立 Vue 组件实现（`components/` 下按页面命名，如 `JobsView.vue`、`PluginsView.vue`、`LibraryView.vue`）；`services/api.ts` 统一封装 API 请求。前后端通过 HTTP/JSON 通信，问答接口当前返回完整响应，不使用 SSE 流式传输；唯一的 SSE 是任务中心的 `/api/jobs/stream`（单向进度推送，断开自动退回轮询）。插件的安装、授权、配置（按 manifest 的 JSON Schema 渲染）、作用域、同步与审计全在 `PluginsView.vue` 里完成，不需要 curl。

## 4. 核心业务流程

### 4.1 资料上传与索引

1. 前端将资料提交到 `POST /api/documents`；大文件通过 `POST /api/uploads` 创建会话，使用 `PUT /api/uploads/{uploadId}/chunks?offset={bytes}` 上传分块，再调用 `POST /api/uploads/{uploadId}/complete` 完成归档。
2. API 校验扩展名、文件大小及知识库是否存在。默认普通文件上限为 50 MiB、视频上限为 5 GiB，分块默认 16 MiB；具体值由 `Storage` 配置控制。
3. 原文件写入 `App_Data/uploads/{knowledgeBaseId}/` 下的随机文件名路径；数据库保存资料元数据、受控存储路径和处理状态。断点续传期间的数据写入暂存文件。
4. 资料记录进入 `Queued` 状态。调度器（`JobScheduler`）的补投器每秒扫一遍排队资料，按幂等键 `document.ingest:{documentId}` 入队一条 `document.ingest` 任务；多个并发槽位并行领取执行，领取时把资料从 `Queued` 改为 `Processing` 并回写进度与阶段。API 重启时会把未完成的 `Processing` 项重新排队，并靠租约过期回收被中断的任务。
5. 文本、Markdown 直接读取；HTML/HTM 使用 `HtmlTextExtractor` 抽取正文（标题层级、列表、表格单元格，不执行脚本、不加载外部资源），非 UTF-8 页面按 BOM 与 `<meta charset>` 解码（含 GBK/GB18030）；DOCX 使用 DocumentFormat.OpenXml；PDF 使用 PdfPig；图片由所选聊天模型生成可检索描述。
6. 视频由 FFmpeg 抽取单声道 16 kHz 音频，Whisper.net 生成带时间段的转写文本。关键帧分析可选，默认关闭；启用后按配置抽帧并调用支持图像输入的模型描述画面。视频摘要与转写内容进入后续索引流程。
7. 文本按约 1,400 字符切片，并保留约 180 字符重叠；切片边界优先选择换行、句末标点或空格。
8. 每个切片由本地 ONNX Embedding 模型生成向量，并写入 `kb_chunks`；全部完成后资料状态变为 `Ready`，失败时记录错误信息并标记为 `Failed`。

后台处理队列保存在 PostgreSQL 中，由 API 进程内的 `JobScheduler`（`BackgroundService`，可配置并发槽位数）消费；不是独立消息队列服务。任务的排队、进度、重试次数、取消请求与租约都写在 `jobs` 表里，可通过 `GET /api/jobs` 与 `GET /api/jobs/stream`（SSE）观察与控制，详见 `docs/JOBS.md`。

### 4.2 知识库问答（RAG）

1. `KnowledgeChatService` 校验会话所属知识库，并读取该会话最近 30 条消息作为对话历史。
2. `RetrievalRouter` 用本地 ONNX 模型对问题生成向量，按当前知识库和 `Ready` 状态过滤资料块，并跳过指纹/维度不一致的索引。
3. 检索是**混合**的：向量（pgvector 余弦距离 `<=>`）与词法（PostgreSQL 全文）两路召回，再用 RRF 融合。词法侧的 token 由 `Domain/Knowledge/LexicalTokens.cs` 在应用侧切成中文二元组（PostgreSQL 默认解析器不认中文，随包的 PostgreSQL 17.9 也没有 `zhparser`），写入 `kb_chunks.lexical_tokens` 并由 GIN 表达式索引支持；历史切片由 `LexicalIndexWorker` 后台补分词。融合之后可选一层**重排**（`Application/Knowledge/CoverageReranker.cs` → `Domain/Knowledge/RerankScoring.cs`）：`Score = (1-w)·(0.5×融合名次 + 0.5×归一余弦) + w×idf 覆盖率`，`w=0.65`。默认开启，`Retrieval:RerankEnabled=false` 可回到纯 RRF。
4. 聊天默认取前 5 个片段（**重排开启时**每路召回 60 条再融合，融合后由 `RerankScoring` 重排再截断；关闭时各召回 15 条直接融合）；显式检索接口的结果数限制为 1–20，并可用 `mode` 选 `hybrid` / `vector` / `lexical` / `rerank`。
5. `ToolCallingAgent` 将资料片段、最近对话和本轮问题组装成上下文交给聊天模型。提示词要求资料不足时明确说明、标记来源，并将检索内容视为不可信引用数据而非系统指令。
6. API 返回回答和引用元数据；本轮用户消息与助手回答保存到 `knowledge_chat_messages`，会话标题初始为首个问题的前 100 个字符。
6. “AI 知识问答”页提供三个单轮开关：工具调用（总开关）、联网搜索、本地文件。它们随请求体（`enableToolCalling` / `enableWebSearch` / `enableFileAccess`）发送，为空时沿用设置页保存的偏好，非空时只对本次提问生效且不落库。关闭总开关后不向模型下发任何 `tools`，模型只能用预检索的资料片段回答；关闭联网或本地文件时由 `AgentToolRegistry` 按工具分类过滤，知识库与分身工具不受影响。

聊天历史表保存角色、文本内容和时间。**引用元数据会随助手消息一起落库**（`citations` / `answer_meta` 两列）：

- `citations` 是数组，每项为 `documentId / fileName / similarity / rank（从 1 开始）/ chunkId / chunkIndex / snippet（≤240 字）`，能定位到“哪份资料的哪一段”。切片标识来自检索 SQL 投影的 `kb_chunks.id` 与 `chunk_index`（旧版直查与新索引两条路径都有）。
- `answer_meta` 保存 `retrieval`（`indexId` / `embeddingSpecName` / `warning` / `legacyIndex` / `strategy` / `abstained` / `topScore`）、`toolCalls`（工具调用审计）与 `sources`（联网来源链接），回答“为什么是这几条资料”和“答案用了哪些工具”。
- 写入集中在 `KnowledgeChatStore.AddSessionExchangeAsync`，读回时经 `KnowledgeChatJson` 反序列化；脏 JSON 一律降级为空引用，不让整个会话打不开。
- 旧库靠 `KnowledgeChatStore.EnsureMessageColumns()` 显式补列（`CodeFirst` 只在建表时生效）。
- Markdown 导出会为每条助手消息追加「引用资料（序号 / 第 N 段 / 相关度 / 片段）」与「网页来源」引用块。

### 4.3 问答历史管理

会话按知识库隔离。历史列表按最近更新时间倒序显示，删除会话会通过数据库级联删除对应消息。管理接口包括重命名、单条删除、一次最多 100 条的批量删除和 Markdown 导出。前端在删除当前会话后自动切换到其他会话；没有剩余会话时清空当前对话视图。

### 4.4 AI 伴侣

1. 用户上传 UTF-8 JSON/TXT 导出文件，并提供档案名、目标人物发言者名称和平台信息。
2. `ChatHistoryParser` 支持顶层 JSON 数组或 `messages`/`records` 数组；TXT 支持 `[时间] 发言者: 消息` 行，未匹配的续行并入上一条消息。不直接读取微信/QQ 私有或加密数据库。
3. 解析后的档案和原始消息在事务中写入 PostgreSQL。导入文件最大 20 MB，最多 100,000 条消息，单条消息不超过 20,000 字符。
4. 风格画像使用所选聊天模型根据历史样本生成；会话消息单独存储。对话时按关键词检索最多 8 条相关原始消息，再结合当前会话历史构造提示词。
5. UI 和提示词均明确该功能是 AI 模拟，不代表现实人物本人；导入前应确认数据处理授权。

### 4.5 向量索引生命周期与模型切换

资料解析、内容版本与向量索引已经解耦：

- `kb_documents` 保存 `file_hash`、`content_hash` 和 `content_version`；`kb_chunks` 按 `document_version` 保存每个内容版本的切片，旧版本保留用于审计和回滚。
- 资料发布在单个数据库事务内完成：写入分块版本 → 更新资料状态 → 写入 `kb_outbox_events`（幂等键形如 `document:{id}:v{version}:published`）。
- `kb_embedding_specs` 注册向量模型规格（provider、模型、维度、序列长度、revision），指纹唯一。
- `kb_indexes` 表示索引实例，状态为 `Building → Ready → Active → Retired`；`kb_index_vectors` 按索引独立存储向量；`kb_active_indexes` 单行保存活动索引及指纹，检索路由只读这一行。
- `IndexLifecycleManager` 负责：创建阴影索引 → 全量重建 → 增量重放 → 一致性检查 → 质量评测 → 原子切换 → 回滚窗口 → 清理。
- `IndexLifecycleWorker` 在进程内后台推进构建与变更追赶；API 重启后从数据库检查点继续。
- `RetrievalRouter` 原子读取活动索引 → 用同指纹模型生成查询向量 → 校验指纹/维度 → 只查询该索引的向量；指纹不一致时阻止跨模型检索。
- `SummaryService` 只读取已发布分块文本，按「内容哈希 + 摘要模型指纹」缓存，与向量索引和摘要模型切换互不影响；摘要失败不会阻断资料发布。
- 每个知识库可通过 `kb_model_bindings` 绑定自己的向量规格与摘要模型，实现不同用户/知识库使用不同模型。

切换向量模型（设置页即可完成）：

1. 进入“设置 → 向量模型（Embedding）”。
2. 本地模型：上传 `.onnx` 与 `tokenizer.json`，服务端自动识别维度、校验词表并做一次真实推理；在线模型：填写 OpenAI 兼容端点、模型名、密钥与维度，注册前会调用一次 `/embeddings` 验证。
3. 在已注册列表中选择目标规格，点击底部“保存设置”。系统会把它持久化为默认向量模型，并为所有知识库创建阴影索引、排队全量重建。
4. 设置页展示每个知识库的重建进度（`Building → Active`）；构建期间检索继续使用旧的活动索引。
5. 构建完成后执行一致性检查与质量评测并原子切换；切换失败或一致性不通过时活动索引不受影响，可在回滚窗口内调用 rollback。

也可以按知识库单独绑定：`PUT /api/knowledge-bases/{id}/model-binding`。

### 4.6 Function Calling（工具调用）

聊天模型不再通过 SDK Agent 抽象调用，而是由 API 自己实现 OpenAI `tools` / `tool_calls` 循环：

- `Agents/ToolCallingAgent.cs`：组装系统提示（含预检索结果）→ 调用 `POST /chat/completions`（带 `tools`）→ 执行模型请求的工具 → 把结果作为 `tool` 消息回填 → 直到模型给出文本回答。达到 `Agent:MaxToolIterations` 后强制收口，模型不支持 `tools` 或首次调用报错时自动退化为普通问答（`CompleteChatAsync`）。
- `Agents/Tooling/AgentWorkspace.cs`：本地沙箱，路径经 `Resolve` 归一化，拒绝 `..`、绝对路径逃逸；读写按字符数分页与截断。
- `Agents/Tooling/WorkspaceToolSource.cs`：`workspace_list_files`、`workspace_read_file`、`workspace_write_file`、`workspace_delete_file`、`workspace_search`（`Agent:AllowWorkspaceWrite=false` 时不注册写/删工具）。
- `Agents/Tooling/AgentFileAccess.cs` + `LocalFileToolSource.cs`：访问 `Agent:FileRoots` 授权的本机目录（如 `D:\学习资料`）。工具为 `list_file_roots`、`local_list_directory`、`local_read_file`、`local_search_files`、`local_grep`、`local_write_file`、`local_delete_file`；路径支持绝对路径与“根名称/子路径”，自动识别 UTF-8/UTF-16/GB18030，未授权目录与 `..` 穿越一律拒绝。
- `Agents/Tooling/KnowledgeToolSource.cs`：`list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`read_document_content`。
- `Agents/Tooling/AvatarToolSource.cs`：`avatar_get_persona`、`avatar_feed`（自动整理 + 向量化）、`avatar_search_memories`、`avatar_digest`、`avatar_ask_as_me`（分类 `avatar`，只读写分身数据，不触碰知识库）。
- `Agents/Tooling/AgentToolRegistry.cs`：聚合多个 `IAgentToolSource`，单个来源失败不影响其他工具，同名工具按注册顺序去重。
- 每次工具调用的名称、参数、结果摘要、耗时、成功与否以 `AgentToolCallTrace` 随 `/api/chat` 与会话问答响应返回，前端在消息下方显示“工具调用”标签。

### 4.7 MCP 双向互操作

- **本机知识库作为 MCP 服务端**：`KnowledgeMcpTools`（`Mcp/KnowledgeMcpTools.cs`）、`LocalFileMcpTools`（`Mcp/LocalFileMcpTools.cs`）、`AvatarMcpTools`（`Mcp/AvatarMcpTools.cs`）与 `ChatHistoryMcpTools`（`Mcp/ChatHistoryMcpTools.cs`）通过 `ModelContextProtocol.AspNetCore` 以 Streamable HTTP 暴露在 `Mcp:ServerPath`（默认 `/mcp`，无状态模式）。其他大模型/智能体可调用 `list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`get_document_content`、`get_document_summary`、`upload_document`、`workspace_*`、`list_file_roots`、`local_*`、`avatar_get_persona`、`avatar_feed`、`avatar_search_memories`、`avatar_digest`、`avatar_ask_as_me`、`avatar_list_fragments`，以及 `list_chat_sessions`、`chat_search_history`（共 **24** 个工具，能力与准入见 `docs/MCP-ACCESS.md`）。
- **本机作为 MCP 客户端**：`Mcp/McpClientRegistry.cs` 按 `Mcp:Servers` 惰性连接外部服务器（`http` Streamable HTTP 或 `stdio`），工具列表缓存 30 秒；`Mcp/McpToolSource.cs` 把远端工具桥接为 `mcp__{server}__{tool}`（名称清洗、去重、≤64 字符）供聊天模型调用。
- 连接失败只影响对应服务器，状态通过 `GET /api/mcp/status` 暴露；设置页“AI 工具与 MCP”卡片展示 MCP 端点、连接数与工具清单。

典型配置（`appsettings.json`）：

```json
"Agent": {
  "EnableToolCalling": true,
  "MaxToolIterations": 6,
  "WorkspaceRootPath": "App_Data/agent-workspace",
  "AllowWorkspaceWrite": true,
  "MaxReadChars": 8000,
  "MaxWriteChars": 200000,
  "MaxToolResultChars": 12000,
  "EnableMcpToolBridge": true,
  "MaxDirectoryEntries": 300,
  "MaxSearchMatches": 60,
  "FileRoots": [
    { "Name": "学习资料", "Path": "D:\\学习资料", "AllowWrite": false, "AllowDelete": false, "Enabled": true }
  ]
},
"Mcp": {
  "EnableServer": true,
  "ServerPath": "/mcp",
  "EnableClient": true,
  "Servers": []
}
```

要让 AI 读取知识库以外的本机文件，只需在 `Agent:FileRoots` 中追加一项：`Path` 写绝对目录，`Name` 用作路径前缀；默认只读，需要模型写文件时把 `AllowWrite` 设为 `true`（删除同理）。修改后重启 API，设置页“AI 工具与 MCP”卡片会显示已授权目录及其权限。

新增接口：`GET /api/agent/tools`、`GET /api/agent/info`、`GET /api/mcp/status`，以及 MCP 端点 `{Mcp:ServerPath}`。

### 4.8 分身（数字孪生）：证据链记忆 + 反馈评测闭环

分身回答的不是“资料里怎么说”，而是“**如果是我，我会怎么做**”。整个模块只有两个界面：**投喂**与**问答**。

> **与知识库隔离**：分身数据只写入 `avatar_*` 表，不进入 `kb_documents` / `kb_chunks` / `kb_index_vectors`，不参与知识库语义检索，也不会被知识库问答引用；反之，分身问答只读分身记忆与洞察，不读知识库文档。

**投喂：异步整理，不阻塞问答**

- 用户只有一个输入框：想到什么写什么（日记、吐槽、聊天记录、观点、自我介绍、和谁关系近、说话习惯……），也可以直接导入文件。
- `POST /api/avatar/feed` / `POST /api/avatar/feed/upload`：原文先落 `avatar_fragments`，同时创建 `avatar_ingestions` 任务并立即返回 `ingestionId`。相同文件用 `source_hash + segment_index + extractor_version` 做幂等，重复导入不会重复建记忆。
- `AvatarIngestionWorker` 后台轮询任务，失败可重试；长内容按段落分批提取，不会因为单次模型输入长度限制而静默丢内容。整个流水线不修改知识库的资料处理链路（资料解析由任务系统的 `document.ingest` 处理器负责）。
- **记忆提取**（`AvatarOrganizer`，契约 `avatar-extractor-v2`）只提取“用户说了什么”：
  - `memory_type`：`fact / value / semantic / episodic / procedural / relationship / expression_style / goal_habit`；
  - `stability`：`stable / phase / situational`；`confidence`：0–100；
  - `evidence_span`：必须逐字摘录原文，保存到 `avatar_memory_evidence`；
  - `supersedesMemoryId`：只在明确矛盾时指向旧记忆；
  - `structured_data`、`privacy_level`、`people`、`tags`、`importance`；
  - 同时输出候选洞察 `insights[]` 与实体 `entities[]`，但候选洞察不会直接覆盖稳定人格。
- **去重 / 冲突 / 版本**：低风险且一致的内容合并证据；推断性内容进入 `candidate / pending_confirmation`；新旧矛盾时旧记忆只改状态为 `historical / superseded / corrected`，并写 `avatar_memory_versions`，绝不删除旧记忆，也不静默合并冲突。
- 模型异常或 JSON 无法解析时自动兜底：原文仍写入一条记忆与证据并标记 `usedFallback`，可稍后重试。
- 旧版碎片可通过 `POST /api/avatar/memories/migrate` 排入同一整理队列，平滑升级。
- 知识库问答与学习打卡仍会经 `AvatarActivityRecorder` 自动写入一条碎片 + 一条记忆（同一来源+分类 30 分钟内只记一次），并补写证据与版本。

**记忆状态与证据**

- `avatar_memories` 新增 `memory_type / stability / confidence / lifecycle_status / valid_from / valid_to / supersedes_id / structured_data / privacy_level / last_confirmed_at / last_used_at / evidence_count`。`vector_state` 只表示向量化状态，与生命周期完全独立。
- 每条记忆至少有一条 `avatar_memory_evidence`（原文摘录、偏移、来源哈希、发生/记录时间）；用户纠正时可以回到最初出处。
- `GET /api/avatar/memories/{id}/evidence` 查看证据，`GET /api/avatar/memories/{id}/versions` 查看修订历史。

**人格归纳：候选版本，不直接覆盖**

- `AvatarInsightService` 在积累到新记忆后运行：输入多条相关记忆及证据，输出“维度、结论、置信度、适用范围、支持证据”。
- 与现有洞察一致且置信度 ≥75、证据 ≥2 条时，才更新 `avatar_insights` 当前快照；否则只写 `avatar_insight_versions` 候选。
- `GET /api/avatar/insights/candidates` 查看候选，`POST /api/avatar/insights/candidates/{id}/confirm` 由用户确认后升级为稳定人格。

**向量化**

- `AvatarMemoryIndexer` 复用知识库已注册的默认向量模型（`EmbeddingSpecService.GetDefaultAsync`），只写 `avatar_memory_vectors`，与知识库索引完全隔离。
- 每条记忆把 `title + summary + content`（截断 4000 字符）生成向量；记录 `embedding_spec_id`、`embedding_fingerprint`、`dimensions`，检索时按指纹过滤。
- 记忆先入库、后向量化：向量模型不可用时记忆保持 `pending`（词面检索仍可用），前端提供“补向量”和“全部重向量化”。

**问答：先理解问题，再混合召回**

- 会话与消息落在 `avatar_chat_sessions` / `avatar_chat_messages`；`POST /api/avatar/chat` 返回 `chatMessageId`，供反馈绑定。
- `AvatarQueryClassifier` 规则优先地把问题分为：自我认知 / 历史原因 / 未来选择 / 关系态度 / 表达模仿 / 事实查询，并识别实体与时间意图（当前 / 历史 / 变化）。
- `AvatarMemoryRetriever` 多路召回并重排：
  - 结构化过滤（生命周期、类型、实体、时间、隐私）；
  - 中文二元组词面检索；
  - `avatar_memory_vectors` 语义检索（按指纹过滤）；
  - 表达风格样本与决策模式分别召回；
  - 综合语义、词面、类型、实体、重要度、置信度、时间、反馈历史与冲突惩罚。
- 矛盾观点默认只展示当前有效状态；只有问“以前为什么/后来怎么变”时才召回历史与已被取代的记忆。
- `AvatarPersonaChatService` 提示词分为 A 稳定人格 / B 当前有效记忆 / C 情境与历史 / D 表达风格样本 / E 决策模式 / F 当前任务；系统提示强制第一人称“我”、禁止编造、禁止“作为 AI/根据资料”、150–400 字，记忆不足时明确说“这点我记得不够清楚”。
- 回答随引用记忆返回（`memoryId / kind / title / score`），前端在气泡下展示“记忆类型 · 标题”。没有任何记忆和洞察时接口返回 400 并提示先投喂。

**反馈闭环**

- `POST /api/avatar/feedback`：
  - `like`：弱正向信号，只用于后续重排与风格加权，不直接改人格；
  - `unlike`：必须带原因（记错事实 / 判断不符 / 过度推断 / 语气不对 / 关系错误 / 时间错误 / 漏掉记忆）；
  - `correction` / `supplement`：创建新的 `avatar_ingestions` 任务，重新走记忆提取；旧记忆标为 `corrected / superseded` 并写版本。
- 模型自己生成的回答永远不会被当作关于用户的新事实。

**固定评测集与回归**

- `avatar_eval_cases` 保存问题、认可答案、必须引用/禁止引用的记忆、评分维度与公开/隐藏集；推荐 30–50 题，覆盖价值观、重要经历、人际关系、决策、表达、拒绝、冲突、记忆不足。
- `POST /api/avatar/eval/run` 逐题调用分身回答并计算：通过率、必须引用记忆命中率（引用准确率）、禁止引用（事实错误）次数、表达评分、记忆不足题是否承认不知道；结果写入 `avatar_eval_runs` / `avatar_eval_results`。
- 每次调整整理器、检索或提示词后跑一遍，以实际表现而非投喂数量衡量成熟度。

**完备度**

`AvatarService.EvaluateReadiness(profile, memoryCount, insightCount, chatCount)`：记忆 ≥10 条得 45 分（否则每条 4 分），洞察 ≥5 条得 25 分（否则每条 5 分），基础画像已配置得 20 分，至少 1 个会话得 10 分；同时返回缺失项提示，分数只用于提示“还缺什么”。

**工具与 MCP**

- 聊天模型工具（`AvatarToolSource`，分类 `avatar`）：`avatar_get_persona`、`avatar_feed`（创建异步整理任务）、`avatar_search_memories`、`avatar_digest`、`avatar_ask_as_me`。
- MCP 工具（`AvatarMcpTools`）：`avatar_get_persona`、`avatar_feed`、`avatar_search_memories`、`avatar_digest`、`avatar_ask_as_me`、`avatar_list_fragments`。
- MCP 工具（`ChatHistoryMcpTools`，能力 `chat:read`）：`list_chat_sessions`、`chat_search_history`——读回对话原话（含导入的 QQ/微信/CSV 记录），与「记忆」分开授权。

**保留能力**：`avatar_profile`（基础画像）、`avatar_experiences`、`avatar_behaviors`、`avatar_relationships`、`avatar_simulations`（未来轨迹推演，`POST /api/avatar/simulations`）仍在后端可用，前端不再作为独立页签展示。

分身数据只保存在本机 PostgreSQL；使用云端聊天模型/向量模型时，投喂文本摘录、记忆与人格洞察会发送给所配置的服务商。若希望个人隐私只留在本地，需要同时把聊天推理、记忆提取与向量生成都切到本地实现，并审查备份、日志与工具访问权限。


## 5. 数据存储

### 5.1 数据库表

| 表 | 用途 | 关系/说明 |
| --- | --- | --- |
| `kb_knowledge_bases` | 知识库元数据。 | 含固定默认知识库和用户创建的知识库。 |
| `kb_topics` | 知识库下的专题名称、颜色和时间。 | 关联知识库；资料通过 `kb_documents.topic_id` 归属专题。 |
| `kb_documents` | 资料文件名、存储路径、状态、进度、摘要、错误信息、文件哈希、内容哈希和当前内容版本。 | 关联知识库；可关联专题，删除专题时置空。 |
| `kb_notes` | 学习笔记标题、正文、分类、颜色、标签、置顶状态、来源资料、专题与引用片段。 | 关联知识库；`tags`、`sources` 以 JSON 文本保存；`topic_id`/`source_document_id`/`source_session_id` 为可空 uuid。旧库启动时由 `SqlSugarNoteRepository.EnsureColumns()` 补列。 |
| `kb_chunks` | 资料切片文本、序号、内容版本和内容哈希；`embedding` 仅保留为历史兼容列。 | 关联资料；`(document_id, document_version, chunk_index)` 唯一。 |
| `kb_document_versions` | 资料每次内容变化产生的分块版本、内容哈希和切片数。 | 关联资料；`(document_id, version)` 唯一。 |
| `kb_embedding_specs` | 向量模型规格、维度、序列长度、revision 和唯一指纹。 | 指纹相同的模型视为同一向量空间。 |
| `kb_model_bindings` | 知识库绑定的向量规格与摘要模型设置。 | 每个知识库一行；为空时回退到全局配置。 |
| `kb_indexes` | 索引实例、所属规格、状态、构建进度、检查点、质量分和回滚时间。 | 状态 `Building/Ready/Active/Retired/Failed`。 |
| `kb_active_indexes` | 每个知识库当前活动索引及指纹、维度。 | 检索路由单行原子读取；切换索引只更新此表。 |
| `kb_index_vectors` | 按索引隔离的切片向量。 | 关联索引与切片；`(index_id, chunk_id)` 唯一。 |
| `kb_outbox_events` | 事务性变更事件、幂等键和发生时间。 | 索引检查点按事件 ID 顺序重放。 |
| `kb_document_summaries` | 按内容哈希与摘要模型指纹缓存的摘要。 | 与向量索引生命周期完全解耦。 |
| `kb_upload_sessions` | 断点续传会话、暂存路径和已上传字节数。 | 关联知识库，可关联完成后的资料记录。 |
| `knowledge_chat_sessions` | 知识库问答会话标题与时间。 | 关联知识库，删除知识库时级联删除会话。 |
| `knowledge_chat_messages` | 问题和回答文本、角色与时间；助手消息额外带 `citations`（引用数组）与 `answer_meta`（检索上下文 / 工具调用 / 网页来源）。 | 关联问答会话，删除会话时级联删除消息。 |
| `companion_profiles` | AI 伴侣档案、目标发言者、平台及风格描述。 | 档案独立于知识库问答。 |
| `companion_messages` | 导入的原始聊天记录。 | 关联档案。 |
| `companion_message_vectors` | 伴侣历史消息的向量：`message_id` 主键、规格 id、指纹、维度与 pgvector 列。 | **独立于 `kb_*`**：私聊不进资料索引，避免「问资料」时把私聊当证据引用；查询按指纹 + `profile_id` + `sender` 过滤。 |
| `companion_sessions` | 伴侣对话会话。 | 关联档案。 |
| `companion_session_messages` | 伴侣会话中的用户和 AI 消息。 | 关联会话。 |
| `app_settings` | 前端设置页保存的 JSON 配置。 | 单机密钥存储，接口只返回掩码提示。 |
| `study_checkins` | 每日学习打卡日期、时长和备注。 | 日期唯一；用于计算连续学习与本周节奏。 |
| `avatar_fragments` | 分身原始投喂底稿：类型、标题、正文、发生时间、来源、标签、是否已整理。 | 与知识库表完全隔离；`tags` 以 JSON 数组文本保存。 |
| `avatar_memories` | 规范化记忆：类型、标题、归纳、原话细节、时间、人物、标签、情绪、重要度、置信度、生命周期、有效期、版本取代关系、结构化数据、隐私级别与向量状态。 | 分身的检索单位；`fragment_id` 关联底稿，删除记忆不删底稿；旧记忆只改状态不删除。 |
| `avatar_memory_vectors` | 记忆向量：`memory_id` 主键、规格 id、指纹、维度与 pgvector 列。 | 只被分身检索使用；读取时必须投影，禁止整行读取 vector 列。 |
| `avatar_feed_entries` | 聊天式投喂流水：用户原文与 AI 整理回执。 | `bigint` 自增主键；`payload` 保存本次新增记忆的摘要 JSON。 |
| `avatar_insights` | 人格洞察：维度、标题、结论、置信度、证据。 | `(dimension, title)` 唯一，重复整理覆盖更新。 |
| `avatar_chat_sessions` | 第一人称问答会话标题与时间。 | 删除会话时级联删除消息。 |
| `avatar_chat_messages` | 会话消息角色、内容与引用记忆。 | `bigint` 自增主键；`citations` 以 JSON 文本保存。 |
| `avatar_profile` | 分身单例画像：定位、性格、价值观、优势/短板、技能、资源、约束、目标与风险偏好。 | 整个部署只有一行；`values` 列名为 `values_text`。 |
| `avatar_experiences` | 用户投喂的经历：时间、分类、结果、情绪、影响强度与标签。 | `tags` 以 JSON 数组文本保存。 |
| `avatar_behaviors` | 日常行为流：分类、描述、时长、情绪、来源（manual/chat/tool/import）。 | 自动记录与手动记录共用。 |
| `avatar_relationships` | 关键关系人：角色、亲近度、影响力与说明。 | 推演时作为支持/阻力输入。 |
| `avatar_simulations` | 轨迹推演：情景、跨度、状态、成功率、置信度、结构化因素/里程碑/风险/行动与完整报告。 | JSON 字段以 `text` 保存；失败也落库便于重试。 |
| `avatar_ingestions` | 异步投喂任务：来源、状态、阶段、错误、统计、幂等键、片段与待取代记忆。 | 由 `AvatarIngestionWorker` 消费；失败可重试。 |
| `avatar_memory_evidence` | 记忆到原文的证据：摘录、字符偏移、页码/消息 id、来源哈希、发生与记录时间。 | 每条记忆至少一条；纠正时可回溯最初出处。 |
| `avatar_memory_versions` | 记忆修订历史：版本号、快照 JSON、变更类型、取代关系与原因。 | 创建/合并/被取代/纠正/确认都留痕。 |
| `avatar_insight_versions` | 人格洞察候选与历史版本：维度、结论、置信度、状态、范围、证据记忆 id。 | 候选只有被确认或证据足够才升级到 `avatar_insights`。 |
| `avatar_feedback` | 回答反馈：聊天消息、引用记忆、类型、原因、评论、纠正文本与关联投喂任务。 | `like` 仅弱信号；`correction/supplement` 创建新投喂任务。 |
| `avatar_entities` | 人物/组织/项目实体：类型、名称、别名与元数据。 | 与记忆多对多关联，用于按“对谁/关于什么”聚拢记忆。 |
| `avatar_memory_entities` | 记忆与实体的关联及角色。 | `(memory_id, entity_id)` 主键。 |
| `avatar_eval_cases` | 固定评测题：分类、问题、认可答案、必须/禁止引用的记忆、评分维度、公开/隐藏集与启用状态。 | 建议维护 30–50 题。 |
| `avatar_eval_runs` | 一次评测运行：总数、通过数、事实错误数、引用准确率、表达评分与摘要。 | 每次调整检索/提示词后回归。 |
| `avatar_eval_results` | 单题评测结果：回答、引用记忆、是否通过、得分与原因。 | 关联运行与评测题。 |

### 5.2 初始化和迁移

表结构由 SqlSugar CodeFirst 实体（`SugarTable`、`SugarColumn`、`SugarIndex`）定义，启动时按顺序调用 `CodeFirst.InitTables`；普通索引也由实体元数据维护。数据库连接串必须指向一个已存在的 PostgreSQL 数据库，API 不负责创建数据库本身。

数据访问已全部改用 SqlSugar 原生 API：Store 使用 `Queryable`、`Insertable`、`Updateable`、`Deleteable`、`Storageable` 和 `SqlFunc` 完成读写与检索，事务通过 `Ado.BeginTranAsync`/`CommitTranAsync`/`RollbackTranAsync` 管理，不再手写查询或更新语句。仅 pgvector 相关能力因 SqlSugar 没有原生 API 而保留 SQL：启用 `vector` 扩展的 DDL、余弦距离运算符 `<=>` 以及 `vector` 列插入时的类型转换，这些统一封装在 `Persistence/PgVectorSql.cs`，并通过 `SqlFunc.MappingColumn`、`OrderBy` 和 `SugarColumn.InsertSql` 扩展点注入。`database/schema.sql` 与 `database/create-database.sql` 作为可选的完整建库脚本保留，运行时不加载。

原始资料位于本地文件系统，数据库仅保存受控文件路径及元数据。备份需要同时覆盖 PostgreSQL 数据库和 API 的 `App_Data` 目录。

## 6. 技术方案

| 层次 | 技术 | 使用方式 |
| --- | --- | --- |
| API 运行时 | .NET 10、ASP.NET Core | Web API 宿主、依赖注入、后台服务、异常处理和 CORS。 |
| API 风格 | Minimal API、`Microsoft.AspNetCore.OpenApi`、Scalar | 按功能路由分组；开发环境提供 OpenAPI JSON 和 Scalar 页面。 |
| 数据访问 | SqlSugar Core、Npgsql | CodeFirst 创建表和普通索引；Store 的数据读写和检索使用 SqlSugar 原生 API；仅 pgvector 专属 SQL 通过 `PgVectorSql` 扩展点保留。 |
| 数据库 | PostgreSQL、pgvector | 保存元数据、状态、聊天历史、文本切片及向量；余弦距离检索。 |
| 前端 | Vue 3、TypeScript、Vite、lucide-vue-next | SPA 视图、交互状态、API 请求和图标组件。 |
| Agent/聊天 | Microsoft Agent Framework、OpenAI SDK、OpenAI 兼容 Chat Completions | 知识库问答 Agent、摘要、图片描述、风格画像和伴侣对话。 |
| Embedding | Microsoft.ML.OnnxRuntime、Microsoft.ML.Tokenizers、BGE-small-zh-v1.5 | API 进程内运行 WordPiece tokenizer 和 ONNX 推理；当前配置 512 维、最多 512 token，输出做均值池化和归一化。 |
| 文档处理 | PdfPig、DocumentFormat.OpenXml | PDF 文本提取和 DOCX 正文提取。 |
| 视频处理 | FFmpeg、Whisper.net、Whisper.net.Runtime | 音轨抽取、语音识别及可选关键帧描述。 |
| 测试 | xUnit、Microsoft.NET.Test.Sdk | 覆盖文件存储、聊天记录解析和 Markdown 导出等单元逻辑。 |

Embedding 与聊天模型采用分离配置：切换本地/云端聊天模型不会改变 Embedding 推理位置。向量模型通过 `kb_embedding_specs` 注册并由索引生命周期管理；切换模型或维度时创建阴影索引重建，完成后原子切换，不能在同一索引内混用不同模型产生的向量。摘要模型可按知识库单独绑定，切换摘要模型只影响后续摘要生成与缓存命中，不触发索引重建。

## 7. API 接口概览

详细路由实现分别位于 `Endpoints/HealthEndpoints.cs`、`Endpoints/KnowledgeEndpoints.cs` 和 `Endpoints/CompanionEndpoints.cs`。

| 方法 | 路由 | 说明 |
| --- | --- | --- |
| `GET` | `/api/health` | 检查 API 与 PostgreSQL 连接。 |
| `GET` / `POST` | `/api/knowledge-bases` | 列出/创建知识库。 |
| `GET` / `POST` | `/api/embedding-specs` | 列出/注册向量模型规格。 |
| `POST` | `/api/embedding-specs/import-local` | multipart 导入本地 ONNX 模型（`modelFile` + `tokenizerFile`），自动识别维度并验证推理。 |
| `POST` | `/api/embedding-specs/register-cloud` | 注册 OpenAI 兼容向量模型，注册前调用一次 `/embeddings` 验证。 |
| `POST` | `/api/embedding-specs/{specId}/probe` | 对指定规格做一次真实向量化探测。 |
| `GET` / `POST` | `/api/knowledge-bases/{id}/indexes` | 列出索引/创建阴影索引。 |
| `POST` | `/api/knowledge-bases/{id}/indexes/{indexId}/rebuild` | 排队全量重建；活动索引会自动创建阴影索引。 |
| `POST` | `/api/knowledge-bases/{id}/indexes/{indexId}/activate` | 一致性检查后原子切换活动索引。 |
| `POST` | `/api/knowledge-bases/{id}/indexes/{indexId}/check` | 执行一致性检查与质量评测。 |
| `POST` | `/api/knowledge-bases/{id}/rollback` | 回滚到回滚窗口内的历史索引。 |
| `GET` | `/api/knowledge-bases/{id}/retrieval-status` | 查看活动索引、模型与检查点状态。 |
| `GET` | `/api/knowledge-bases/{id}/changelog?after=&limit=` | 查看 Outbox 变更事件与重放检查点。 |
| `GET` / `PUT` | `/api/knowledge-bases/{id}/model-binding` | 读取/设置知识库的向量与摘要模型绑定。 |
| `GET` | `/api/documents/{documentId}/summary` | 读取资料最新摘要。 |
| `POST` | `/api/documents/{documentId}/summary/regenerate` | 使用当前摘要模型重新生成摘要。 |
| `GET` | `/api/documents?knowledgeBaseId={id}` | 查询资料列表及处理状态。 |
| `GET` | `/api/documents/{documentId}` | 查询资料详情。 |
| `POST` | `/api/documents` | 普通文件上传。 |
| `POST` / `GET` / `PUT` | `/api/uploads...` | 创建、查询和分块上传续传会话并完成归档。 |
| `GET` | `/api/documents/{documentId}/download` | 下载原始文件（支持 Range 分段）。 |
| `GET` | `/api/documents/{documentId}/content?offset=&length=` | 分页读取已解析原文，用于原文阅读器。 |
| `POST` | `/api/documents/{documentId}/retry` | 重新解析 `Failed` 或 `Ready` 状态的资料。 |
| `DELETE` | `/api/documents/{documentId}` | 删除资料及其分块，并清理本地原始文件。 |
| `POST` | `/api/search` | 显式向量检索。 |
| `POST` | `/api/chat` | 不创建会话的知识库问答。 |
| `GET` / `POST` | `/api/knowledge-bases/{knowledgeBaseId}/sessions` | 列出/创建持久化问答会话。 |
| `GET` / `POST` | `/api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}/messages` / `.../chat` | 读取消息和在会话中继续问答。 |
| `PUT` / `DELETE` | `/api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}` | 重命名/删除会话。 |
| `POST` | `/api/knowledge-bases/{knowledgeBaseId}/sessions/batch-delete` | 批量删除会话，JSON 请求体为 `{"sessionIds":[...]}`，最多 100 条。 |
| `GET` | `/api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}/export` | 下载 Markdown 对话记录。 |
| `GET` / `POST` | `/api/knowledge-bases/{knowledgeBaseId}/topics` | 列出/创建专题。 |
| `PUT` / `DELETE` | `/api/knowledge-bases/{knowledgeBaseId}/topics/{topicId}` | 重命名/删除专题（删除时资料移回未分类）。 |
| `GET` / `POST` | `/api/knowledge-bases/{knowledgeBaseId}/notes` | 列出（`query`/`category`/`documentId`/`topicId` 均下推到 SQL 过滤）/创建笔记。 |
| `GET` / `PUT` / `DELETE` | `/api/knowledge-bases/{knowledgeBaseId}/notes/{noteId}` | 读取/更新/删除笔记。 |
| `POST` | `/api/knowledge-bases/{knowledgeBaseId}/notes/{noteId}/pin` | 设置或取消笔记置顶。 |
| `POST` | `/api/knowledge-bases/{knowledgeBaseId}/notes/export` | 批量导出笔记为一份 Markdown（`noteIds` 为空即全部）。 |
| `GET` | `/api/knowledge-bases/{knowledgeBaseId}/package` | 导出知识库包（zip：`manifest.json` + 原始文件），统计在 `X-Package-*` 响应头。 |
| `POST` | `/api/knowledge-bases/import` | 导入知识库包（multipart，字段名 `file`），一律新建知识库并重新排队解析；失败或零内容会回滚并返回 400。 |
| `PUT` / `GET` | `/api/documents/{documentId}/topic` | 设置/读取资料所属专题。 |
| `GET` / `PUT` | `/api/settings` | 读取/保存模型与学习偏好设置；密钥只返回掩码提示。 |
| `GET` / `POST` | `/api/study/checkins` | 读取学习打卡统计/记录今日打卡。 |
| `DELETE` | `/api/study/checkins/today` | 撤销今日打卡。 |
| `GET` / `PUT` | `/api/avatar` / `/api/avatar/profile` | 读取分身总览（完备度、记忆/洞察/会话计数、最近记忆与投喂流水）与保存基础画像。 |
| `POST` / `GET` | `/api/avatar/feed` | 创建异步投喂任务（返回 `ingestionId`）/ 读取投喂流水。 |
| `POST` | `/api/avatar/feed/upload` | 导入 `.txt/.md/.json/.log/.csv`（≤10 MB），按 `source_hash` 幂等排入整理队列。 |
| `DELETE` | `/api/avatar/feed` | 清空投喂流水（记忆与洞察保留）。 |
| `GET` | `/api/avatar/memories` | 列出当前有效记忆；带 `query` 时走问题分类 + 多路混合检索。 |
| `DELETE` | `/api/avatar/memories/{id}` | 删除单条记忆（同时删除其向量）。 |
| `POST` | `/api/avatar/memories/reindex` | 重新向量化：`{ "all": false }` 只补待处理，`true` 全部重跑。 |
| `POST` | `/api/avatar/memories/migrate` | 把旧版未整理的碎片排入同一异步整理队列。 |
| `GET` | `/api/avatar/memories/{id}/evidence` | 查看某条记忆的原文证据。 |
| `GET` | `/api/avatar/memories/{id}/versions` | 查看某条记忆的修订历史。 |
| `GET` | `/api/avatar/ingestions` / `/api/avatar/ingestions/{id}` | 查看投喂任务列表/单任务状态（`queued/running/completed/partial/failed`）。 |
| `POST` | `/api/avatar/ingestions/{id}/retry` | 重试失败或部分完成的投喂任务。 |
| `POST` / `GET` | `/api/avatar/feedback` | 提交/列出回答反馈（`like/unlike/correction/supplement`）。 |
| `GET` | `/api/avatar/insights/candidates` | 列出待确认的人格候选版本。 |
| `POST` | `/api/avatar/insights/candidates/{id}/confirm` | 确认候选，升级为稳定人格快照。 |
| `GET` / `POST` | `/api/avatar/eval/cases` | 列出/新增固定评测题；`DELETE /api/avatar/eval/cases/{id}` 删除。 |
| `POST` | `/api/avatar/eval/run` | 运行回归评测（`{ includeHidden, limit }`）。 |
| `GET` | `/api/avatar/eval/runs` / `/api/avatar/eval/runs/{id}` | 查看评测批次与逐题结果。 |
| `GET` / `POST` | `/api/avatar/fragments` | 兼容旧版：列出/投喂原始碎片。 |
| `POST` | `/api/avatar/fragments/bulk` | 兼容旧版：批量投喂分段文本。 |
| `POST` | `/api/avatar/fragments/upload` | 兼容旧版：上传文件并按段落切成多段碎片。 |
| `DELETE` | `/api/avatar/fragments/{id}` | 删除单条原始碎片。 |
| `GET` / `DELETE` | `/api/avatar/insights` | 列出或清空人格洞察（清空保留记忆）。 |
| `POST` | `/api/avatar/digest` | 兼容旧版：分析未整理碎片，产出/更新人格洞察。 |
| `GET` / `POST` | `/api/avatar/chat/sessions` | 列出/新建第一人称问答会话；`DELETE /api/avatar/chat/sessions/{id}` 删除会话。 |
| `GET` | `/api/avatar/chat/sessions/{id}/messages` | 读取会话消息与引用片段。 |
| `POST` | `/api/avatar/chat` | 以“我”的身份回答（`{ sessionId, message }`，`sessionId` 为空时自动建会话）。 |
| `GET` / `POST` | `/api/avatar/experiences` | 保留：列出/投喂经历；`DELETE /api/avatar/experiences/{id}` 删除。 |
| `GET` / `POST` | `/api/avatar/behaviors` | 保留：列出（`days` 默认 30）/记录行为；`DELETE /api/avatar/behaviors/{id}` 删除。 |
| `GET` / `POST` | `/api/avatar/relationships` | 保留：列出/新增关系人；`PUT`、`DELETE /api/avatar/relationships/{id}` 修改与删除。 |
| `GET` / `POST` | `/api/avatar/simulations` | 保留：列出/发起未来轨迹推演；`GET`、`DELETE /api/avatar/simulations/{id}` 查看与删除。 |
| `/api/companions/*` | — | 导入历史、查询/删除档案、生成画像、创建会话、读取消息和发送伴侣对话。 |
| `GET` / `POST` | `/api/companions/{id}/index`、`/api/companions/{id}/reindex` | 查询「语义记忆」已索引条数 / 手动触发重建（`?limit=` 控制本批上限，导入后会后台自动跑一次）。 |

知识库问答会话接口的路径均包含 `knowledgeBaseId`，后端据此校验会话归属，避免通过其他知识库 ID 操作会话。

## 8. 配置与启动

### 8.1 必要配置

| 配置项 | 用途 | 当前默认/要求 |
| --- | --- | --- |
| `ConnectionStrings:KnowledgeBase` | PostgreSQL 连接串。 | 可留空：留空时由 API 拉起随包的便携 PostgreSQL 17.9 + pgvector 0.8.7，并自动执行 `CREATE EXTENSION IF NOT EXISTS vector`。填写则优先使用外部数据库。 |
| `PortablePostgres:Enabled` | 是否启用便携 PostgreSQL。 | 默认 `true`；容器部署建议 `false`（改用外部 PG）。 |
| `PortablePostgres:RespectExternalConnectionString` | 有外部连接串时是否跳过便携库。 | 默认 `true`，便于开发机继续使用已有 PostgreSQL。 |
| `PortablePostgres:PreflightTimeoutSeconds` | 启动时对外部连接串做一次可达性预检的超时（秒）。 | 默认 `5`；`0` = 关闭预检。库正常时只多一次 TCP 连接。 |
| `PortablePostgres:BinDirectory` | `bin` 目录（`postgres.exe`/`pg_ctl.exe`/`initdb.exe` 所在处）。 | **默认空 = 自动定位**：`ExternalTools/pgSQL/<rid>/bin` → `ExternalTools/pgSQL/bin`。可以填 `bin` 目录，也可以填 `pgsql` 根目录（自动补 `bin`）；填了但不存在会回落自动定位并记日志，不会启动失败。 |
| `PortablePostgres:Port` | 便携库监听端口。 | 默认 `55432`；被占时向后找空闲端口；若 `pgdata/postmaster.pid` 指向的 `postgres.exe` 还活着，复用它记录的端口，不再起第二个实例。 |
| `PortablePostgres:RootDirectory` | 便携数据库根目录（数据、日志都放在其下）。 | 默认空 = 自动挑选：`%LOCALAPPDATA%\MyFirstMafAgentData` → `%TEMP%\MyFirstMafAgent` → `%ProgramData%\MyFirstMafAgent` → `%USERPROFILE%\.myfirstmafagent` → **`{程序目录}\App_Data\pgdata`（便携兜底，最后才用）**。显式配置时不回落。 |
| `PortablePostgres:DataDirectory` | 数据目录（`initdb` 的 `-D`）。 | 默认空 = `{便携根目录}\pgdata`。 |
| `PortablePostgres:Username` | 便携库超级用户。 | 默认 `postgres`，`initdb -U` 用它。 |
| `PortablePostgres:Password` | 该用户的密码。 | 默认 `postgres`；首次 `initdb` 经 `--pwfile=<临时文件>` 写入，`scram-sha-256` 认证。 |
| `PortablePostgres:Database` | 目标数据库名。 | 默认 `myfirstmafagent`；不存在时自动 `CREATE DATABASE`。 |
| `PortablePostgres:StartupTimeoutSeconds` | 等数据库就绪的上限（秒）。 | 默认 `180`，作为 `pg_ctl start` 的 `-w -t` 超时。 |
| `PortablePostgres:StopOnExit` | 应用退出时是否停止本次拉起的实例。 | 默认 `true`；外部数据库与不是本进程拉起的实例不受影响。 |
| `Desktop:OpenBrowser` | 控制台版启动后是否自动打开浏览器。 | 默认 `true`；开发环境默认不打开。 |
| `Llm:UseCloudApi` | 选择 `Llm:Online` 或 `Llm:Local` 配置。 | `true` 使用在线配置，`false` 使用本地配置。在线密钥不要提交到仓库。 |
| `Embedding:ModelDirectory` | 本地 ONNX 模型与 tokenizer 目录。 | 默认位于 `ExternalTools/Embedding/bge-small-zh-v1.5`，目录应包含 `model.onnx` 与 `tokenizer.json`。 |
| `Embedding:ModelRootPath` | 设置页导入的本地向量模型存放目录。 | 默认 `App_Data/models/embedding`，导入的模型会自动识别维度。 |
| `Embedding:MaxModelFileSizeBytes` | 单个本地模型文件上限。 | 默认 1 GiB；同时决定 API 请求体上限。 |
| `Embedding:DefaultSpecName` | 默认向量规格名称。 | 设置页保存的默认规格优先于该配置。 |
| `Indexing:RollbackWindowMinutes` | 旧索引可回滚的时间窗口。 | 默认 60 分钟，超时后由后台清理。 |
| `Retrieval:HybridEnabled` | 是否启用混合检索（向量 + 词法 RRF 融合）。 | 默认 `true`；设为 `false` 整体退回纯向量检索，行为与 P2-1 之前一致。 |
| `Retrieval:RrfK` | RRF 平滑常数 k。 | 默认 60。越大越看重"两路合并排名"，越小越只看头部命中。 |
| `Retrieval:CandidateMultiplier` / `Retrieval:MaxCandidates` | 每一路的候选倍数与候选上限。 | 默认 3 / 60：融合前多取一些，融合后再截到调用方要的条数。 |
| `Storage:RootPath` | 原始资料和分块暂存文件目录。 | 默认 `App_Data/uploads`。 |
| `Storage:MaxFileSizeBytes` | 普通文件上限。 | 默认 50 MiB。 |
| `Storage:MaxVideoFileSizeBytes` | 视频文件上限。 | 默认 5 GiB。 |
| `Storage:MaxAudioFileSizeBytes` | 音频文件上限（语音消息/录音/播客）。 | 默认 300 MiB。音频单独一档，不跟普通文档共用 50 MiB，否则一条长语音就会被拒。 |
| `Storage:UploadChunkSizeBytes` | 单个上传分块上限/默认分块大小。 | 默认 16 MiB。 |
| `Media:FfmpegPath` | FFmpeg 可执行文件路径。 | **默认空 = 自动定位**：先找 `ExternalTools/ffmpeg/<rid>/`，再找旧版扁平布局，最后回落到 PATH 里的 `ffmpeg`（容器里用 apt 安装即可）。 |
| `Media:WhisperModel` / `Media:WhisperModelPath` | Whisper 模型类型和缓存路径。 | 默认 Base；模型缺失时首次转写会尝试下载，也可在设置页导入本地模型。 |
| `Media:AutoDownloadWhisperModel` | 缺模型时是否允许自动联网下载。 | 默认 `true`；离线部署设 `false`，缺模型会立刻报"去设置页导入"。 |
| `Media:MaxWhisperModelBytes` | 导入/下载模型的体积上限。 | 默认 3 GiB（large-v3 量级），同时计入请求体上限。 |
| `Jobs:MaxConcurrentJobs` | 长任务并发槽位数（每个槽位一条数据库连接）。 | 默认 `4`，合法区间 1–32。 |
| `Jobs:LeaseSeconds` | 任务租约；执行者崩溃后超过它未续租即被回收重跑。 | 默认 `120`，合法区间 30–3600（启动时校验，填小会直接启动失败）。 |
| `Jobs:RetryBaseSeconds` / `RetryMaxSeconds` | 失败退避的基数与上限（指数增长）。 | 默认 `10` / `600`。 |
| `Jobs:MaxAttempts` | 默认最大尝试次数（含首次）。 | 默认 `3`。 |
| `Jobs:Enabled` | 是否运行任务调度器。 | 默认 `true`；关闭后任务只入队不执行，便于排查。 |
| `Jobs:MaxQueuedJobs` | 排队任务上限，达到后补投停止入队。 | 默认 `5000`。 |
| `Jobs:TypeLimits` | 按任务类型的并发上限，给高优先级任务留出保底槽位（`GET /api/jobs/quotas` 可看占用）。 | 默认 `{ "plugin.sync": 2 }`；不配或配 `0` 表示该类型不单独限制；只影响下一次领取，不抢占已运行任务；计数是进程内的，多进程部署时每进程各自限流；改完需重启。 |
| `Plugins:DrainTimeoutSeconds` | 升级/停用前等待在途调用结束的秒数，超时则强停并写审计。 | 默认 `15`，合法区间 0–600。 |
| `Plugins:ScheduledSyncEnabled` | 是否启用定时同步（插件返回 `nextSyncAfterSeconds` 后由补投器按间隔入队）。 | 默认 `true`；关闭后只能手动触发同步。 |
| `Plugins:MinScheduledSyncIntervalSeconds` | 定时同步的最小间隔，插件报的间隔比它小就按它算。 | 默认 `60`，合法区间 30–86400。 |
| `Plugins:TrustedPublisherKeys` | 受信任发布者公钥（按 `PluginId` 域名段前缀匹配）。 | 与库表 `plugin_trusted_keys` 合并，**配置项优先**且不可经由接口删除。 |
| `Plugins:MaxQueuedInvocationsPerPlugin` | 单个插件允许多少次调用**排队**（不含正在运行）。 | 默认 `16`；在途上限 = manifest 的 `maxConcurrentInvocations` + 本值；`0` 表示「没有空闲槽位就立即拒绝」。超额返回 **429**（`code=-32006`），不是无限排队。 |
| `Plugins:StartupConcurrency` | 启动阶段并行拉起的插件数。 | 默认 `4`。**插件数量本身没有上限**（扫描不截断、列表不分页）；这个值只约束「同时 spawn 多少个进程」。 |
| `Plugins:MaxQueuedInvocationsPerCapability` | 单个能力的排队上限。 | 默认 `8`；只对在 `CapabilityConcurrencyLimits` 里配了上限的能力生效。 |
| `Plugins:CapabilityConcurrencyLimits` | 能力级并发上限。键写 `"<pluginId>/<capabilityId>"`（只对某插件）或 `"<capabilityId>"`（对所有同名能力）。 | 默认空（只有插件级闸门）。它是在插件预算里**再切一刀**，不会放大插件上限。改完需重启。 |
| `VITE_API_BASE_URL` | 前端 API 根地址。 | 未设置时使用同源（空字符串）；Vite 开发服务器通过 `/api` 代理转发到 `http://127.0.0.1:5056`。 |

### 8.2 本地启动

默认无需准备数据库：`ConnectionStrings:KnowledgeBase` 留空时，API 会拉起随包的便携 PostgreSQL（按当前 RID 解析 `ExternalTools/pgSQL/<rid>/bin`），首次运行 `initdb` 建库、之后 `pg_ctl start` 启动，并确保 `myfirstmafagent` 数据库存在且 `vector` 扩展已启用。若要使用已有 PostgreSQL，把连接串配置到用户机密或环境变量即可（默认会跳过便携库）：

```powershell
$env:ConnectionStrings__KnowledgeBase = "Host=localhost;Port=5432;Database=knowledge_base;Username=postgres;Password=<password>"
dotnet run --project MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj
```

> **外部连接串配了但库没起来时，启动会直接停下并把原因说清楚**（`Infrastructure/Persistence/ConnectionPreflight.cs`）。因为"有连接串就跳过便携库"是既定规则，所以预检会先探一次那条连接，失败时输出端点、脱敏连接串、失败分类和三条修法（启动那台数据库 / 清空连接串改用便携库 / 设 `PortablePostgres:RespectExternalConnectionString=false`）。
>
> 典型场景：`dotnet user-secrets` 里留着 `Host=localhost;Port=5432;…`，而本机 PostgreSQL 服务是停止状态。此时要么以管理员身份 `Start-Service postgresql-x64-18`（先用 `Get-Service *postgresql*` 确认服务名），要么 `dotnet user-secrets remove "ConnectionStrings:KnowledgeBase" --project MyFirstMafAgent.Api` 改用便携库。预检超时可用 `PortablePostgres:PreflightTimeoutSeconds` 调整，设为 `0` 关闭。

API 默认地址为 `http://127.0.0.1:5056`。API 启动后检查 `GET /api/health`。前端另开终端运行：

```powershell
cd FrontEnd/myfist-agent
npm install
npm run dev
```

前端默认地址为 `http://localhost:5173`，Vite 会把 `/api`、`/mcp` 代理到 `http://127.0.0.1:5056`，因此前端代码统一使用同源相对路径，不需要再配置 `VITE_API_BASE_URL`。

便携数据库的运行文件：

- 便携 PostgreSQL 程序：随包目录 `ExternalTools/pgSQL/<rid>/`（`bin/`、`lib/`、`share/` 三块，`VERSION.txt` 记版本）
- 数据目录：`%LOCALAPPDATA%\MyFirstMafAgentData\pgdata`
- 服务器日志：`%LOCALAPPDATA%\MyFirstMafAgentData\pglogs\postgres.log`（`pg_ctl -l`）；`pg_ctl` 自身的输出在同一个目录的 `pgctl-start.out`
- 应用日志：`%LOCALAPPDATA%\MyFirstMafAgentData\logs`

### 8.3 打包为桌面版 / 控制台便携版

前端已改为同源请求 + 相对资源路径（`base: './'`），可直接由内嵌 Kestrel 托管。项目提供两种分发形态：

| 形态 | 项目 | 说明 |
| --- | --- | --- |
| 桌面版 | `desktop-electron/` + `MyFirstMafAgent.Api` | Electron 窗口 + 作为 sidecar 的 API 子进程（不再有 Photino / Velopack） |
| 控制台版 | `MyFirstMafAgent.Api` | 启动本地 Kestrel，自动用默认浏览器打开界面 |

一键发布（构建前端 → `dotnet publish` → 复制 `wwwroot` → 校验便携 PostgreSQL 目录/模型）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Publish-Portable.ps1 -Target all
```

`-Target desktop` 发布的是 **API 本体**到 `publish/desktop`，Electron 主进程从那里拉起 sidecar：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target desktop -SkipFrontend
```

产物：

| 目录 | 用途 |
| --- | --- |
| `publish/console/` | 控制台便携版（双击 `MyFirstMafAgent.Api.exe`） |
| `publish/desktop/` | Electron sidecar（`MyFirstMafAgent.Api.exe` + `wwwroot` + `ExternalTools`） |
| `publish/releases/` | electron-builder 产出的 NSIS 安装包（`MyFirstMafAgent-<version>-setup.exe`） |

Electron 调试用 `cd desktop-electron; npm install; npm start`（可用 `MAF_API_EXE` 指向已发布的 API）。打安装包：`cd desktop-electron; npm run dist:full`（等价于 `scripts\Build-ElectronInstaller.ps1`，会先发布 sidecar 再跑 electron-builder）。自动更新已接入（`desktop-electron/updater.js` + `electron-updater`），发布用 `npm run dist:publish`，详见 `docs/DEPLOYMENT.md` §7。

### 8.3.1 检索评估（可复跑基线）

检索改动的正确验证方式不是"感觉更准了"，而是同一份问题集在改动前后的 recall / MRR 对比：

```powershell
# 先确认 API 在跑、知识库里有 Ready 资料
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -ListFiles
# 对比纯向量 / 纯词法 / 混合，并把原始报告存档
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -Modes vector,lexical,hybrid -OutFile C:\Temp\eval.json
# 四档阶梯（加上重排）
powershell -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -Modes vector,lexical,hybrid,rerank -OutFile C:\Temp\eval.json
```

问题集写在 `scripts/retrieval-eval/default.json`：每条包含 `query`、可接受的 `expectFiles`，以及可选的 `expectText`（要求命中切片正文出现这段文字）。也可以写 `"expectAbstain": true` + `"expectFiles": []` 表示“这题语料里本来就没有答案，正确行为是拒答”；这类用例不进 recall 分母，脚本会额外打印一张**拒答阈值扫描表**（阈值 → 该拒的拒了几条 / 不该拒却拒了几条）。脚本对每种模式输出逐题排名与 `recall@K` / `MRR`。

> 语料只有几份资料时三种模式都会满分——那是烟雾测试。真正的对比需要几十份同主题资料 + 会互相混淆的问题。详见 `scripts/retrieval-eval/README.md`。

#### 同主题混淆语料（T-3）

```powershell
# 建库 + 上传 27 份资料 + 等 Ready，并把知识库 ID 写进 corpus.kb.txt（可重复执行，按文件名跳过已存在的）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Seed-RetrievalCorpus.ps1

# corpus.json 故意把 knowledgeBaseId 留空，由同目录的 corpus.kb.txt 提供
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -Fixture scripts\retrieval-eval\corpus.json -Modes vector,lexical,hybrid,rerank
```

`corpus/` 下 27 份中文资料分三簇（插件运行时 / 索引 / 任务中心，各 8 份 + 1 份总览），簇内共用大量词汇；`corpus.json` 有 42 个有答案用例（分「字面 / 语义 / 区分 / 总览陷阱」四类）+ 8 个无答案用例，报告按类分别输出 recall 与 MRR。基线（bge-small-zh-v1.5，top-5，连跑两次一致）：vector `0.8571 / 0.7270`、lexical `0.9524 / 0.8460`、hybrid `0.9762 / 0.8492`、rerank `1.0000 / 0.9075`。

> 重排模式下响应里的 `similarity` 是**重排得分**（`RerankScoring`，`(1-w)·先验 + w·覆盖率`），不是余弦；跨模式比大小没有意义。拒答阈值默认关闭（`Retrieval:AbstainEnabled=false`），因为实测这份语料上有答案/无答案的得分完全重叠，详见 `docs/ROADMAP.md` D-4。

> 语料建在哪个知识库上就用哪个向量模型：如果默认规格被切成了桩/云端模型，先用 `POST /api/knowledge-bases/{id}/indexes { embeddingSpecId, activateWhenReady: true }` 把本地 BGE 索引切过去，否则数字描述的是那个模型而不是 BGE。

### 8.4 跨平台与容器

同一份后端代码支持 `win-x64` / `linux-x64` / `linux-arm64` / `osx-arm64` / `osx-x64`；便携 PostgreSQL 目前只随包 `win-x64`，其它平台要先按同构装配好该平台的 `ExternalTools/pgSQL/<rid>/`（`win-arm64` 无便携库）。

```powershell
# 按平台发布（默认会清掉其它平台的原生二进制）
powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime linux-x64
# 补上该平台的 ffmpeg（需能访问 GitHub）
powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target ffmpeg
# 便携 PostgreSQL 不从网络下载：给它一份该平台的现成 pgsql 树（含 bin/lib/share）或 zip
powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target pgsql -PgSource D:\PG\dist\pgsql
```

```bash
# 容器（不含 Electron，便携库关闭，用外部 pgvector）
docker compose up -d --build
```

原生工具查找规则、便携库目录结构、容器环境变量、镜像内含/不含什么、已验证/未验证清单：见 **`docs/DEPLOYMENT.md`**。要点：

- `ExternalTools/pgSQL/<rid>/` 必须**整树**分发（`bin/`、`lib/`、`share/` 的相对位置不能拆，只拷 exe 会 `0xC0000135` 找不到 DLL）；`ExternalTools/ffmpeg/<rid>/` 放上游原始文件名即可。配置项留空自动定位。
- 容器里必须用 `--urls http://0.0.0.0:5056` 覆盖，因为 `appsettings.json` 的 `Urls` 会盖掉 `ASPNETCORE_URLS`。
- 本机尚未装 Docker，也没在 Linux/macOS 真机上跑过；非 Windows 的便携 PostgreSQL/ffmpeg 二进制尚未装配（目录已建好，README 里写了同构装配的做法）。

发布要点：

- 静态文件采用外置 `wwwroot`（发布脚本写入），不使用嵌入资源；不启用 AOT，因此不受嵌入文件提供程序限制。
- 应用图标统一放在仓库根 `assets/app.ico`（ICO 格式，含 16/32/48/64/128/256 六种尺寸）：Electron 主进程用它作为窗口图标，打包器用它生成 exe 与快捷方式图标；前端 `public/favicon.ico` 是同一文件的副本。**替换图标：覆盖 `assets/app.ico` 并同步 `FrontEnd/myfist-agent/public/favicon.ico`，再重新发布即可。**
- 桌面版窗口加载 `http://127.0.0.1:<随机端口>/`，API 是独立子进程：令牌经环境变量传入、由主进程通过 `X-Api-Token` 请求头注入；退出时先 `POST /api/desktop/shutdown`，超时再强杀。
- 便携库随 API 启动，退出时走 `pg_ctl stop -D <pgdata> -m fast -w -t 60` 优雅停止；异常退出后下次启动会读 `pgdata/postmaster.pid`，若那个 `postgres.exe` 还活着就直接复用它的端口，不再起第二个实例。
- 便携库**就地**从发布目录的 `ExternalTools/pgSQL/<rid>/` 运行（不再复制到 `%LOCALAPPDATA%`），数据与日志写在 `%LOCALAPPDATA%\MyFirstMafAgentData` 下的 `pgdata`/`pglogs`：所以升级/卸载必须先把 `postgres.exe` 停掉，否则它占住安装目录——`MyFirstMafAgent.Api.exe --stop-portable-postgres` 就是给安装器用的。
- **文件夹版不要放在桌面、文档或下载目录**：安全软件（火绒/360 的文件防护）与 Windows「受控文件夹访问」会拦截从这些位置启动的未签名程序写入用户目录，便携数据库因此初始化失败（表现为 `Access is denied`）。推荐 `D:\Apps\MyFirstMafAgent`。
- **同一个约束也适用于开发**：仓库在桌面目录下时，VS 里 F5 启动的 `bin\Debug\...\MyFirstMafAgent.Api.exe` 无法写入 `%LOCALAPPDATA%` / `%TEMP%` / `%ProgramData%` / 用户主目录 / `%APPDATA%` 中的任何一个（实测：同一个探针程序只有自己的目录可写）。此时有三条路：**把仓库移到非受保护目录**（如 `C:\Dev\MyFirstMafAgent`）、**在安全软件里放行该 exe/目录**、或**配置 `PortablePostgres:RootDirectory` 指向一个已存在的可写目录**。注意：`PortablePostgres:DataDirectory` 救不了这种情况——它是在选根目录之后才被读取的。
- 自动选目录的**最后一档**是 `{程序目录}\App_Data\pgdata`（便携兜底）：用户目录整片不可写时才用，日志会打一条警告，因为覆盖安装/删除程序目录会连带数据。
- 应用目录**可以**包含中文，但靠的是一层镜像，不是 `initdb` 天生支持：Windows 下 `initdb` 会把程序路径按 ANSI 代码页塞进启动 `postgres.exe` 的命令行，非 ASCII 目录会得到 `FATAL: invalid byte sequence for encoding "UTF8"`（实测：`C:\Word\通用文件服务\...` 下初始化必失败，同一份文件搬到 `C:\pgspike\pgsql` 立即成功）。所以当 `ExternalTools/pgSQL/<rid>/bin` 位于非 ASCII 路径时，`PortablePostgresHost.EnsureRunnableBinDirectoryAsync` 先把整棵树镜像到 `{便携根目录}\pgsql`（首次约 65 MB，之后靠 `VERSION.txt` 指纹判断是否最新，不再复制），再从镜像里跑 `initdb`/`pg_ctl`。**数据目录则必须是纯 ASCII**（`EnsureAsciiDataPath` 强制校验，否则直接报错并给出改法）：这一条同时解决了升级时 `postgres.exe` 锁住中文安装目录的问题。
- 文件夹版应用目录需可写（会在其下创建 `App_Data`），不要放在 `Program Files`。
- 应用未签名，Windows SmartScreen 首次运行会提示“未知发布者”；分发前建议配置代码签名（届时在 electron-builder 里配置）。

## 9. 当前边界与运维注意事项

- 当前无用户认证和授权，不能将默认配置视为多用户或公网部署方案；对外开放前需补充认证、授权、请求限流、HTTPS 和敏感配置管理。
- 原始文件与数据库分开保存，数据库备份不包含上传文件。恢复时需要同时恢复数据库和 `App_Data`，并确保资料记录中的存储路径仍有效。
- 选择在线聊天模型时，问题、检索片段、提示词及伴侣对话会发送给所配置的服务商；选择本地聊天端点时可避免此类云端调用。Embedding 始终在 API 进程本地运行。
- 图片描述要求聊天模型支持视觉输入；**未配置视觉模型时图片依然会保存并直接显示缩略图**，只是内容变成一句兜底文本（`图片资料：xxx.png（未生成图片描述：视觉模型不可用）`）参与检索，不会整篇失败。视频转写依赖 FFmpeg；Whisper 模型（`ggml-base.bin`，约 141 MB）**不随安装包分发**，可在「设置 → 语音转写（Whisper）」里**导入本地 ggml 文件**或点「联网下载」，存在 `%LOCALAPPDATA%\MyFirstMafAgentData\models` 后无需重启即生效。离线部署建议把 `Media:AutoDownloadWhisperModel` 设为 `false`：缺模型时会立刻提示去哪里导入，而不是卡在下载上。转写完成后会尝试用聊天模型生成摘要，**未配置有效聊天端点时只丢摘要，转写内容仍会发布并可检索**。视频关键帧分析默认关闭，启用后还需视觉模型。音频（语音消息/录音）走同一条 FFmpeg + Whisper 链路（单声道 16 kHz），**没有语音内容时不会失败**，而是存成 `音频资料：xxx.mp3（未检测到语音内容）`；纯音乐、音效、表情包配套音频因此都能原样保存。
- AI 伴侣仅支持可读 UTF-8 JSON/TXT，未实现微信/QQ 私有格式解密或官方格式适配。伴侣历史检索基于关键词，不是向量检索。
- 学习笔记已持久化；当前知识库检索以 pgvector 相似度为主，未实现全文与向量混合检索或重排。
- 上述尚未实现的能力（含代码签名、插件隔离、多用户认证等）已整理成待办清单，见 **`docs/ROADMAP.md`**。
- 问答历史导出为 Markdown，助手消息下会附上引用资料（序号 / 段落 / 相关度 / 片段）与网页来源；引用元数据存于 `knowledge_chat_messages.citations` 与 `answer_meta`，历史会话重开时同样呈现。
- 分身是模仿而非本人：记忆不足时问答接口直接返回 400 提示先投喂，不会硬编回答；记忆、向量、洞察与会话均与知识库表完全隔离，不参与知识库检索，也不会被当作事实引用。若使用云端聊天模型或云端向量模型，投喂文本、记忆与人格洞察会发送给服务商；这些数据不属于医疗、法律或投资建议。
- 便携数据库首次启动要 `initdb` 建一个全新的集群（`PostgreSQL 17.9` + `pgvector 0.8.7`），数据写入 `%LOCALAPPDATA%\MyFirstMafAgentData\pgdata`；几十秒量级，之后启动只等 `pg_ctl` 就绪。
- 便携库的平台约束（都已进实现）：`pg_ctl` 的 stdout/stderr 不能重定向成管道（`postgres.exe` 继承写端，父进程等不到 EOF），所以输出落文件、`-l` 日志另用一个文件；不能以管理员权限运行（`initdb`/`postgres` 拒绝提权令牌）；端口被占时日志报 `Permission denied` 而不是 `address already in use`；- `ExternalTools/pgSQL` 必须整树分发（`bin`/`lib`/`share` 三块，只拷 exe 会 `0xC0000135`）。`PortablePostgres:RootDirectory` 可显式指定根目录；**数据目录必须是纯 ASCII**（`initdb` 在非 ASCII 数据目录下必失败），程序目录可以含中文但要靠上面的镜像。
- 桌面版是 Electron 壳 + API 子进程，不再依赖 WebView2/Photino。启动诊断：Electron 侧写入 `%APPDATA%\myfirstmafagent-desktop\logs\electron-shell.log`（含 `starting API sidecar`、`API sidecar ready` 与 API 输出尾巴），后端写入 `%LOCALAPPDATA%\MyFirstMafAgentData\logs`。排查“窗口空白 / 白屏”时先看 sidecar 是否成功拉起、`/api/health` 是否 200。
- 安装包未做代码签名，SmartScreen 会提示未知发布者；正式分发前建议配置签名证书。

## 10. 测试与验证入口

后端测试项目为 `MyFirstMafAgent.Api.Tests`，当前覆盖本地文件安全存储、续传、聊天记录解析、问答 Markdown 导出格式、Agent 文件沙箱与授权目录、分身领域规则（画像校验、经历/行为归一化、推演状态机与完备度）、分身记忆卡的构造/截断与自由类型归一化、整理器 JSON 解析与兜底、文本切段、中文二元组检索排序与洞察 JSON 解析、插件 manifest/JSON Schema/完整性签名/安装回滚/审计脱敏、平台条目判定矩阵/映射幂等/撤回墓碑/同步游标/知识库作用域与配额、外部智能体客户端密钥/能力匹配/工具准入矩阵/令牌桶限流等逻辑。运行命令：

```powershell
dotnet test MyFirstMafAgent.Api.Tests/MyFirstMafAgent.Api.Tests.csproj
```

前端类型检查和生产构建由同一命令执行：

```powershell
cd FrontEnd/myfist-agent
npm run build
```

当前测试以单元测试为主；数据库表创建、PostgreSQL 迁移及 pgvector 查询仍需在具备 PostgreSQL/pgvector 的环境中进行集成验证。
