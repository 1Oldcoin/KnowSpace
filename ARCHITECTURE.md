# 本地学习资料知识库架构

## 1. 目标与边界

构建一个个人使用的本地知识库，用于保存学习资料、生成笔记，并基于资料进行问答。原始文件和索引数据默认保存在本机；聊天模型可以通过配置选择本地模型服务或在线 API。模型服务需要提供 OpenAI 兼容的 Chat Completions 接口；不兼容时再增加专用适配器。

## 2. 总体架构

```text
┌──────────────────────────────┐
│ Vue 3 + TypeScript + Vite     │
│ 上传 / 资料 / 笔记 / 问答 / AI 伴侣 UI │
└──────────────┬───────────────┘
			   │ HTTP / JSON（后续可用 SSE 流式输出）
┌──────────────▼───────────────┐
│ ASP.NET Core Web API (.NET 10)│
│ API / 校验 / MAF Agent / RAG / Companion │
└───────┬──────────┬───────────┘
		│          │ 后台处理任务
		│          ├─ 文档：解析 → 分块 → Embedding
		│          └─ 视频：FFmpeg 抽取音轨 + Whisper.net 转写
		│
		├─ 原始文件：本地文件系统
		├─ 元数据、切片、向量、笔记：PostgreSQL + pgvector
		└─ Chat：可配置的在线或本地 API；Embedding：API 进程内 BGE ONNX 推理
```

### 组件职责

| 组件                    | 职责                                                    |
| --------------------- | ----------------------------------------------------- |
| Vue 3 前端              | 拖拽上传、资料与笔记管理、处理状态展示、知识库问答                             |
| ASP.NET Core API      | 文件接收、参数验证、模型调用、RAG 编排、统一错误响应                          |
| 后台处理服务                | 执行解析、切片、向量化、自动总结；任务状态需要可持久化和可恢复                       |
| 本地文件系统                | 保存用户上传的原始资料；数据库只保存受控文件路径和元数据                          |
| PostgreSQL + pgvector | 保存资料元数据、文本切片、Embedding、笔记及处理状态                        |
| Companion 模块          | 导入可读聊天记录、生成风格画像、检索样本并持久化 AI 对话                        |
| MAF                   | 组织 Agent 与 ContextProvider；检索结果需要包含可回溯的来源信息           |
| 文档解析                  | 使用 PdfPig、DocumentFormat.OpenXml 在 API 进程内处理 PDF、DOCX |
| 视频转写                  | FFmpeg 命令行抽取音轨；Whisper.net 在 API 进程内转写                |

## 3. 数据处理与问答流程

### 文档导入

1. API 验证文件大小、扩展名和请求参数，将文件安全地保存到本地存储目录。
2. 计算文件哈希用于重复检测，并创建处理任务和资料元数据。
3. 文档解析器将文件转换为带有页码/标题等来源信息的文本。
4. 按段落和 token 长度进行语义切片；保存来源文件、页码、章节等元数据。
5. 通过 API 进程内的 BGE ONNX 模型生成 512 维向量，写入 pgvector。
6. 可选地触发摘要/笔记生成，结果作为独立记录保存并关联来源资料。

### 知识库问答

1. 将用户问题向量化，并执行向量相似度检索；可结合 PostgreSQL 全文检索进行混合检索。
2. 使用元数据过滤和可选的重排，选出有限数量的相关切片。
3. 通过 function calling 把带来源的检索结果与工具能力提供给聊天模型（`ToolCallingAgent`）。
4. 返回回答与引用（资料名、页码/章节；视频资料包含时间戳）。证据不足时应明确告知，不应编造来源。

### 当前已实现范围与限制

- 知识库支持 TXT、MD、DOCX、PDF、HTML/HTM；图片通过所选聊天模型生成描述，视频通过 FFmpeg 抽取音轨并由 Whisper.net 在 API 进程内转写，之后走现有文本切片和向量化流程。
    
  HTML/HTM 在 API 进程内抽取正文（标题层级、列表、表格单元格），不执行脚本、不加载外部资源；非 UTF-8 页面按文件 BOM 与 `<meta charset>` 声明解码（含 GBK/GB18030）。
- 图片能力要求所选模型支持视觉输入；视频处理需要随 API 部署的 FFmpeg 可执行文件。Whisper.net 使用 .NET NuGet 包和 CPU 原生运行时，不需要 Python/Whisper CLI；模型文件首次转写时自动下载并缓存。
- Companion 首版支持 UTF-8 通用 JSON/TXT 导出，不直接读取微信/QQ 私有或加密数据库。JSON 接受顶层数组或 `messages` / `records` 数组；TXT 接受 `[时间] 发言者: 消息` 行，未匹配的续行合并到上一条消息。
- Companion 数据写入 PostgreSQL：人物档案、原始消息、画像、会话与消息；聊天会从目标人物的历史消息中做关键词检索。当前不是向量检索，平台官方格式需按脱敏样例开发适配器。
- 知识库支持多库创建与切换；专题按知识库隔离，资料可归入单一专题。笔记仍是前端示例数据。
- 学习打卡与用户设置保存在 PostgreSQL；聊天模型设置可由前端修改并在进程内即时生效。

### 视频处理（当前 MVP）

抽取音轨 → Whisper.net 转写 → 文本切片 → 向量化入库。当前未提取字幕轨道、关键帧或时间戳；这些属于后续增强。

## 4. 模型配置与切换

聊天与 Embedding 使用独立配置。`Llm:UseCloudApi=true` 使用 `Llm:Online`，设为 `false` 使用 `Llm:Local`；聊天服务需提供 OpenAI-compatible Chat Completions 接口。Embedding 在 API 进程内使用 `Microsoft.ML.OnnxRuntime` 加载本地 BGE ONNX 模型，不需要单独的 Embedding HTTP 服务，也不依赖聊天供应商提供 Embedding API。

示例配置（密钥不要提交到版本控制）：

```json
{
	"Llm": {
	"UseCloudApi": false,
	"Online": {
	  "Endpoint": "https://api.example.com/v1",
	  "Model": "chat-model",
	  "ApiKey": ""
	},
	"Local": {
	  "Endpoint": "http://localhost:1234/v1",
	  "Model": "local-chat-model",
	  "ApiKey": "local"
	}
  },
	"Embedding": {
	"ModelDirectory": "ExternalTools/Embedding/bge-small-zh-v1.5",
	"Dimensions": 512,
	"MaxSequenceLength": 512,
	"IntraOpNumThreads": 0
  }
}
```

Embedding 模型目录包含 `model.onnx` 和 `tokenizer.json`；项目会将其复制到构建/发布目录。当前 BGE-small-zh-v1.5 使用 512 维向量和最多 512 个 token。聊天端点与密钥可继续通过环境变量覆盖。不同 Embedding 模型/维度不能混用同一向量索引；切换到该模型后，已有资料需要重新向量化。

## 5. 存储设计

- 原始文档/视频：本地受控目录；禁止直接使用客户端提供的路径或文件名作为磁盘路径。
- PostgreSQL：资料、哈希、媒体类型、大小、导入时间、处理状态、错误信息、笔记等结构化数据。
- pgvector：带资料 ID、页码/时间戳、分块序号、模型版本的切片向量。
- 知识库问答历史与 AI 伴侣的档案、导入消息、会话及消息均保存在 PostgreSQL 中；删除知识库/档案会级联删除对应会话和消息。
- 备份：同时备份 PostgreSQL 数据和原始文件目录，并定期验证恢复流程。

## 6. 后端中间件与服务

建议按职责逐步启用以下 ASP.NET Core 组件：

| 中间件/服务                             | 作用                                     | 阶段          |
| ---------------------------------- | -------------------------------------- | ----------- |
| Exception Handler + ProblemDetails | 将未处理异常转换为统一错误响应，不向客户端泄露堆栈              | MVP         |
| CORS                               | 仅允许本地 Vue 开发/部署来源访问 API                | MVP         |
| HTTPS Redirection                  | 非本地部署时强制 HTTPS；纯 localhost 开发按实际启动配置决定 | 部署时         |
| Request Body Size / Form Limits    | 限制上传请求体大小，避免过量上传占用内存/磁盘                | MVP         |
| 文件扩展名、大小及内容校验                      | 拒绝不支持或明显不匹配的文件；不能只信任客户端 MIME 类型        | MVP         |
| OpenAPI                            | 本地开发时查看和调试 API                         | MVP 开发环境    |
| Health Checks                      | 提供服务存活检查；接入数据库后增加数据库就绪检查               | MVP / 数据库阶段 |
| Rate Limiting                      | 限制昂贵的聊天/上传请求，保护本机服务                    | 暴露到局域网时     |
| Authentication / Authorization     | 多用户或网络暴露时保护资料和 API                     | 需要时         |
| Logging / Correlation ID           | 记录任务和请求关联信息，避免记录密钥和完整敏感内容              | MVP         |

中间件顺序应遵循 ASP.NET Core 约定：错误处理在前，随后路由与 CORS 等策略，最后映射端点。文件解析、向量化和总结属于后台任务/应用服务，不应作为 ASP.NET 中间件实现。

## 7. 后端目录与 API 组织

后端采用按业务拆分的 ASP.NET Core Minimal API，不使用 MVC Controller。`Program.cs` 负责 Host、DI、中间件和启动初始化；HTTP 路由集中在 `Endpoints/`，并按功能在 OpenAPI/Scalar 中分组，避免把所有接口堆在启动文件中。

### 7.1 DDD 分层

代码按领域驱动设计分为四层，依赖方向固定为 `Endpoints → Application → Domain`，`Infrastructure` 只实现 `Domain`/`Application` 定义的端口，由 `Program.cs` 在启动时注入。

```text
MyFirstMafAgent.Api/
├─ Domain/                  # 领域层：实体、值对象、领域规则、仓储端口（不依赖 SqlSugar/ASP.NET）
│  ├─ Notes/                # Note 聚合根、INoteRepository、不变量校验
│  ├─ Topics/               # Topic 值对象、ITopicRepository
│  ├─ Study/                # StudyRecord、StudyStatistics、StudyCalculator（连续天数计算）
│  ├─ Settings/             # UserPreferences 值对象
│  ├─ Avatar/               # 分身：AvatarProfile/Experience/Behavior/Relationship/Simulation + IAvatarRepository
│  └─ Knowledge/            # RetrievedChunk、DocumentText、IEmbeddingGenerator、检索/原文端口
├─ Application/             # 应用层：用例编排、DTO、命令
│  ├─ Notes/                # NoteService
│  ├─ Topics/               # TopicService
│  ├─ Study/                # StudyService
│  ├─ Settings/             # SettingsService（合并密钥、刷新运行时模型配置）
│  ├─ Knowledge/            # KnowledgeSearchService、DocumentContentService（原文分页）
│  ├─ Avatar/               # AvatarService（画像/经历/行为/关系）、AvatarSimulationService（轨迹推演）
│  └─ Common/               # 统一的应用异常（NotFound/Conflict）
├─ Infrastructure/          # 基础设施层：SqlSugar 仓储、ONNX/Http 实现
│  ├─ Persistence/          # SqlSugarNoteRepository / Topic / Study / 检索 / 原文 + 表映射
│  └─ Settings/             # 设置持久化、LlmSettingsProvider、RuntimeSettingsProvider
├─ Endpoints/               # 表现层：路由、请求契约、异常到 HTTP 的映射（ApiResults）
├─ Documents/ Chats/ Companions/ Agents/ Models/   # 迁移中的既有模块
└─ Program.cs               # 组合根：DI、中间件、启动初始化
```

分层规则：

- `Domain` 不引用 `SqlSugar`、`Microsoft.AspNetCore` 或 `Infrastructure`。
- `Application` 只依赖 `Domain` 的端口，不直接使用 `SqlSugarScope`。
- `Infrastructure` 的类型不暴露给端点；端点只注入 `Application` 服务。
- 领域规则集中在聚合根与领域服务中（例如 `StudyCalculator.Summarize` 负责连续打卡计算），应用层只做编排。

### 7.2 迁移状态

`Notes`、`Topics`、`Study`、`Settings`、`Knowledge`（检索与原文阅读）、`Avatar`（分身）已按上述分层实现。`Documents`（上传/解析）、`Chats`（问答历史）、`Companions`（AI 伴侣）仍在使用原有 `Persistence` 命名空间，属于过渡态；它们与分层模块通过 `KnowledgeBaseStore`、`KnowledgeChatStore` 等既有类型协作，后续可沿同一模式迁移到 `Domain/Application/Infrastructure`，不影响现有接口行为。

```text
MyFirstMafAgent.Api/
├─ Endpoints/           # Health、知识库/问答/资料、AI 伴侣路由
├─ Contracts/           # HTTP 请求契约
├─ Options/             # 强类型配置与校验
├─ Agents/              # MAF Agent
├─ Chats/               # 问答与检索编排
├─ Documents/           # 文件存储、解析与处理服务
├─ Companions/          # 聊天导入解析与陪伴对话编排
├─ Persistence/         # Store 与 SqlSugar CodeFirst 实体
└─ Program.cs           # Host、DI、中间件和端点注册
```

开发环境可通过 `/openapi/v1.json` 查看 OpenAPI 文档，或访问 `/scalar/v1` 浏览和调试接口。

## 8. 分阶段实施

1. **后端基础**：ASP.NET Core API、健康检查、模型配置切换、文件安全落盘。
2. **文档 RAG MVP**：PostgreSQL + pgvector、解析、分块、Embedding、向量检索、带来源问答。
3. **学习笔记**：自动摘要、知识点提取、笔记编辑/导出和资料关联。
4. **视频资料**：ffmpeg、字幕/ASR、带时间戳的检索与总结。
5. **检索增强**：全文+向量混合检索、重排、拒答阈值、效果评估。

MVP 不应一开始就承担关键帧 VLM、复杂消息队列或多用户权限等非必要复杂度。先跑通“导入文档 → 检索 → 有来源的回答 → 保存笔记”。

## 9. 当前 MVP 启动配置

API 启动时通过 SqlSugar CodeFirst 实体幂等创建知识库、资料、分块、上传、问答历史和 AI 伴侣表及普通索引；PostgreSQL 的 `vector` 扩展、外键/CHECK 约束、`lower(name)` 表达式唯一索引和旧表数据回填仍使用必要的 PostgreSQL SQL。

默认情况下不需要外部数据库：`ConnectionStrings:KnowledgeBase` 留空时，`PortablePostgresHost` 会启动随应用发布的 `ExternalTools/pgSQL/pg0-windows-x86_64.exe`（内置 PostgreSQL 18 + pgvector），创建目标数据库并执行 `CREATE EXTENSION IF NOT EXISTS vector`。数据目录默认在 `%LOCALAPPDATA%\MyFirstMafAgent\pgsql`，应用退出时自动 `pg0 stop`。

如需使用已有 PostgreSQL，通过环境变量或用户机密提供连接串（默认会跳过 pg0）；运行 API 的用户需有创建扩展/表/约束的权限，不要把真实密码写入配置文件：

```powershell
$env:ConnectionStrings__KnowledgeBase = "Host=localhost;Port=5432;Database=knowledge_base;Username=postgres;Password=<your-password>"
dotnet run --project MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj
```

聊天模型通过 OpenAI-compatible 接口配置。`Llm:UseCloudApi=true` 选择 `Llm:Online`，`false` 选择 `Llm:Local`。Embedding 使用 API 内的 ONNX Runtime；`Embedding:ModelDirectory` 指向包含 `model.onnx` 和 `tokenizer.json` 的目录，当前 BGE 配置为 512 维。更换模型或维度后不要混用旧向量，需重新导入/向量化已有资料。

PDF 和 DOCX 在 API 进程内提取文字；图片描述走当前选择的聊天模型，因此所选模型必须支持图片输入。视频由 `Media:FfmpegPath` 指定 FFmpeg（默认使用随 API 发布的 `ExternalTools/ffmpeg/ffmpeg.exe`），Whisper.net 使用 `Media:WhisperModel` 指定模型类型，并将模型缓存到 `Media:WhisperModelPath`。模型首次使用时需要网络下载；离线部署需提前准备模型文件。API 上传请求会等待音频转写、切片和向量化完成。工具缺失或模型不支持图片时，原文件仍会保留，资料状态显示为 Failed 并附带处理错误。

前端使用 `cd FrontEnd/myfist-agent; npm run dev` 启动；API 默认地址为 `http://127.0.0.1:5056`。前端默认使用同源请求，Vite 通过 `server.proxy` 把 `/api`、`/mcp` 转发到 5056，因此不再需要设置 `VITE_API_BASE_URL`。先启动并确认 `/api/health` 返回数据库已连接，再上传资料。API 路由按领域定义在 `MyFirstMafAgent.Api/Endpoints/`；`/api/search` 提供显式相似度检索，`/api/chat` 会自动检索并返回引用。知识库问答历史支持会话重命名、单条/批量删除和 Markdown 导出，具体路由见前端 README。

发布形态有两种：`MyFirstMafAgent.Api`（控制台 + 自动打开浏览器）与 `MyFirstMafAgent.Desktop`（Photino 原生窗口，进程内 Kestrel + WebView2）。前端 `vite.config.ts` 使用 `base: './'`，构建产物由 `ApiHost` 通过 `wwwroot` 托管；`scripts/Publish-Portable.ps1` 负责构建前端、`dotnet publish`（self-contained win-x64，非 AOT）、复制 `wwwroot` 并校验 pg0/Embedding 模型。`MyFirstMafAgent.Desktop` 另用 Velopack 打成安装包，`VelopackApp.Build().Run()` 必须是 `Main` 的第一句，且 `Main` 必须全同步 + `[STAThread]`，窗口才在 STA 线程上创建（否则 WebView2 静默失败、窗口空白）。便携数据库统一从 `%LOCALAPPDATA%\MyFirstMafAgentData\tools` 运行、工作目录设在 `pgsql` 下，避免 `postgres.exe` 锁住安装目录导致升级失败。

发布目录**不能放在桌面/文档/下载**：Windows「受控文件夹访问」会阻止从这些位置启动的未签名程序写入用户目录，便携数据库会启动失败。应用目录本身可以含中文，`PortablePostgresHost` 会把 pg0 复制到 `%LOCALAPPDATA%\MyFirstMafAgent\tools` 后运行（pg0 要求可执行路径与数据目录均为纯 ASCII）。

AI 伴侣导入端点为 `POST /api/companions/import`（multipart 字段：`name`、`targetSender`、`platform`、`file`），格式为 UTF-8 JSON/TXT，最大 20 MB。`targetSender` 必须匹配导出中的发言者名称。生成画像、开启会话及对话需调用当前选择的聊天模型；云端模式会将提示、相关历史片段及本轮对话发送给配置的 API 服务商。该功能用于生成 AI 模拟，不应被呈现为逝者本人；导入前应确认有权处理聊天记录。

Embedding 使用 `Microsoft.ML.OnnxRuntime` 和 `Microsoft.ML.Tokenizers` NuGet 包在 API 内运行本地 BGE-small-zh-v1.5，不需要独立 Embedding 服务；模型目录随构建和发布输出部署。视频处理使用 `Whisper.net` 与其 CPU runtime NuGet 包，不需要 Python 或 Whisper CLI；Whisper 模型文件首次转写时下载并缓存。FFmpeg 仍是原生可执行工具，项目会将其复制到构建和发布目录；重新分发时需遵守 FFmpeg 对应构建的许可要求。

## 10. Function Calling 与 MCP

聊天模型不再依赖具体 SDK 的 Agent 抽象，而是在 API 内实现标准的 OpenAI `tools` / `tool_calls` 循环（`Agents/ToolCallingAgent.cs` + `Models/OpenAiCompatibleModelClient.cs` 的 `CompleteChatWithToolsAsync`），因此任何兼容 Chat Completions 的本地或在线模型都能使用；模型不支持 `tools` 时自动退化为普通问答。


### 工具来源（`IAgentToolSource`）

| 来源                    | 工具                                                                                                                                  | 说明                                                                                                                                                                                                |
| --------------------- | ----------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WorkspaceToolSource` | `workspace_list_files`、`workspace_read_file`、`workspace_write_file`、`workspace_delete_file`、`workspace_search`                      | 读写 `Agent:WorkspaceRootPath` 下的沙箱目录，`AgentWorkspace.Resolve` 拒绝 `..`、绝对路径与符号链接逃逸                                                                                                                  |
| `LocalFileToolSource` | `list_file_roots`、`local_list_directory`、`local_read_file`、`local_search_files`、`local_grep`、`local_write_file`、`local_delete_file` | 访问 `Agent:FileRoots` 显式授权的本机目录（如 `D:\学习资料`），支持绝对路径与“根名称/子路径”，自动识别 UTF-8/UTF-16/GB18030                                                                                                            |
| `KnowledgeToolSource` | `list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`read_document_content`                                             | 复用 `KnowledgeSearchService` / `DocumentContentService`，检索遵循 Active Index 路由                                                                                                                       |
| `WebSearchToolSource` | `web_search`                                                                                                                        | 联网搜索（分类 `web`）。按 `WebSearch:Provider` 分发到 DuckDuckGo（免密钥 HTML 端点）/ Bing / Tavily；网络异常、超时、非 2xx 都以 JSON 错误返回而不抛异常，交给模型决定下一步。结果结构为 `{ provider, query, count, results: [{ title, url, snippet }] }` |
| `UtilityToolSource`   | `fetch_url`、`current_time`、`calculate`                                                                                              | 抓取网页正文（HTML 经 `HtmlTextExtractor` 抽成纯文本，按 `MaxToolResultChars` 截断）、获取当前时间、四则运算。其中 **`fetch_url` 归类为 `web`**（与 `web_search` 同分类，避免关闭联网后仍可访问网络），`current_time` / `calculate` 归类为 `utility`          |
| `McpToolSource`       | `mcp__{server}__{tool}`                                                                                                             | 把外部 MCP 服务器的工具桥接给聊天模型，单服务器故障不影响其他工具                                                                                                                                                               |

`AgentToolRegistry` 聚合所有来源并按名称去重（内置工具优先），执行结果与耗时通过 `AgentToolCallTrace` 随 `/api/chat` 与会话问答一起返回，前端在消息下方展示“工具调用”标签。

### 用户级开关与工具集构建

工具调用受**两层开关**控制，两者是“与”的关系：

- 全局总闸 `Agent:EnableToolCalling`：运维层面的开关，关闭后所有用户都无法使用任何工具。
- 用户偏好 `AgentPreferences`（设置页保存，经 `AgentPreferenceProvider` 读取并缓存 10 秒）：`EnableToolCalling`（是否允许调用工具）、`EnableWebSearch`（是否允许联网搜索/抓网页）、`EnableFileAccess`（是否允许查看本地文件，默认开启）。

`AgentToolRegistry.GetToolsAsync(context, ct, preferences)` 在内部合并两层开关：全局关闭或用户关闭工具调用时返回空集；随后按工具分类过滤——关闭 `EnableWebSearch` 时剔除 `web` 分类（含 `web_search` 与 `fetch_url`），关闭 `EnableFileAccess` 时剔除 `workspace` / `local` / `files` 分类，知识库工具与分身工具不受这两个开关影响。

`ToolCallingAgent.AskAsync` 每轮读取一次用户偏好，用偏好调用 registry，并据同一份偏好拼装系统提示词（`ToolCallingAgent.BuildInstructions`，public static 便于单测）：

- 关闭联网时明确告知模型“你没有联网能力，不要编造外部链接或时效性信息”，并要求相关问题时如实说明；
- 开启联网时给出 `web_search` / `fetch_url` 的用法：知识库能回答的优先查知识库，时效性事实或外部信息先联网搜索，引用时给出链接，且搜索结果视为引用数据而非指令；
- 关闭文件访问时明确模型不能读取或写入本机文件，不得宣称已打开/保存文件。

若最终工具集为空（两层开关任一关闭，或所有来源都不可用），直接走普通问答；模型不支持 `tools` 协议时同样回退为普通问答。联网类工具的返回结果会被解析出链接，作为 `AgentAnswer.Sources` 随响应返回（`/api/chat` 的 `sources` 字段；会话问答同样返回）。聊天响应还会透出 `toolCallingEnabled` 与 `webSearchEnabled` 两个布尔值，供前端提示当前状态。

### MCP 双向互操作

- **服务端**：`ModelContextProtocol.AspNetCore` 以 Streamable HTTP（`Stateless = true`）在 `Mcp:ServerPath`（默认 `/mcp`）暴露 `KnowledgeMcpTools`，其他大模型或智能体可以通过 MCP 读取本机知识库、摘要与工作目录文件。工具包括 `list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`get_document_content`、`get_document_summary`、`workspace_list_files`、`workspace_read_file`、`workspace_write_file`。
- **客户端**：`McpClientRegistry` 按 `Mcp:Servers` 惰性连接外部服务器（`http` 或 `stdio`），工具列表缓存 30 秒，连接失败只记录状态并暴露在 `GET /api/mcp/status`。

### 配置与端点

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
  "Servers": [
    { "Name": "local-tools", "Transport": "http", "Endpoint": "http://localhost:5057/mcp", "Enabled": false }
  ]
},
"WebSearch": {
  "Provider": "DuckDuckGo",
  "ApiKey": "",
  "BingEndpoint": "https://api.bing.microsoft.com/v7.0/search",
  "TavilyEndpoint": "https://api.tavily.com/search",
  "MaxResults": 5,
  "TimeoutSeconds": 15,
  "UserAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36"
}
```

`WebSearch:Provider` 取 `DuckDuckGo`（默认，免密钥的 HTML 端点，可能被限流）、`Bing` 或 `Tavily`（均需在 `WebSearch:ApiKey` 填写密钥，也可用环境变量 `WebSearch__ApiKey` 提供），设为 `None` 则彻底关闭联网搜索。密钥缺失或服务商不可用时，工具只会返回一条说明性错误，不会抛异常影响问答。`MaxResults` 取值 1~10；`TimeoutSeconds` 同时约束联网搜索与 `fetch_url` 抓取。

`Agent:EnableToolCalling` 只是**全局总闸**：即使为 `true`，用户也必须在设置页自行开启“工具调用/联网搜索”后模型才会真正获得工具（默认全部关闭，文件访问默认开启）。用户开关保存在 `user_settings` 中，修改后立即生效（`AgentPreferenceProvider` 缓存 10 秒，保存设置时主动失效）。

`Agent:FileRoots` 是访问本机其它目录的唯一入口：只有列出的目录可读写，默认只读；`Path` 支持绝对路径或相对内容根目录。模型可以用绝对路径或 `学习资料/子路径` 引用文件。

- `GET /api/agent/tools`：当前可用工具（名称、说明、分类）。
- `GET /api/agent/info`：工具调用开关、工作目录可写状态、MCP 配置概览。
- `GET /api/mcp/status`：外部 MCP 服务器的连接状态、错误与工具数量。

### 安全边界

- 所有文件操作都限制在 `AgentWorkspace` 沙箱或 `Agent:FileRoots` 白名单内，路径经过 `GetFullPath` 归一化并校验包含关系，`..` 穿越、白名单外绝对路径和指向外部的符号链接都会被拒绝。
- `Agent:FileRoots` 默认只读；只有显式设置 `AllowWrite` / `AllowDelete` 才会注册对应的写/删工具。
- 工具返回内容按 `MaxToolResultChars` 截断，避免上下文溢出；检索结果在提示词中明确标注为引用数据而非指令。
- `MaxToolIterations` 限制工具往返次数，达到上限后要求模型直接给出结论，防止死循环。
- 联网能力默认对用户关闭：`web_search` 与 `fetch_url` 同属 `web` 分类，关闭“允许联网搜索”后两者一并剔除，模型无法绕过开关访问网络。
- `fetch_url` 只接受 http/https 绝对地址，抓取前经 `SafeUrlGuard` 拒绝环回、私网、链路本地、云元数据等内部地址（并在 DNS 解析后二次校验），响应体最多读取 2 MB。
- 联网与抓取结果在提示词中被明确标注为引用数据而非指令，页面内的“忽略以上指令”之类文本按普通内容对待。
- MCP 服务端当前没有鉴权，默认仅监听本机；如需对外暴露必须在前置代理上加认证。

## 11. 分身模块（Avatar）

“分身”回答的不是“资料里怎么说”，而是“**如果是我，我会怎么做**”。用户把碎片化材料（日记、聊天记录、性格与形象描述、观点、习惯）持续投喂进来，系统把素材分析成可复用的人格洞察，再用第一人称回答。

**边界：分身与知识库完全隔离。** 分身素材只写入 `avatar_*` 表，不进入 `kb_documents` / `kb_chunks` / `kb_index_vectors`，不参与语义检索，也不会被知识库问答引用；反之，分身问答（`AvatarPersonaChatService`）也只读取分身素材与洞察，不读知识库文档。

### 11.1 数据模型

| 表                                                                                         | 聚合                  | 说明                                                                                                                                                                                        |
| ----------------------------------------------------------------------------------------- | ------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `avatar_fragments`                                                                        | `AvatarFragment`    | 碎片素材：类型（`journal`/`chat`/`personality`/`appearance`/`opinion`/`habit`/`knowledge`/`relationship`/`other`）、标题（留空时取内容首行）、正文（上限 20 万字符）、发生时间、来源（`manual`/`upload`/`chat`/`checkin`）、标签、是否已分析 |
| `avatar_insights`                                                                         | `AvatarInsight`     | 人格洞察：维度（`summary`/`personality`/`values`/`habits`/`language`/`decision`/`knowledge`/`relationship`/`preference`）、标题、结论正文、置信度 0-100、证据条目；`(dimension, title)` 唯一，重复分析走覆盖更新                 |
| `avatar_chat_sessions`                                                                    | `AvatarChatSession` | 第一人称问答会话：标题（首条提问自动改名）、创建/更新时间                                                                                                                                                             |
| `avatar_chat_messages`                                                                    | `AvatarChatMessage` | 会话消息（`bigint` 自增主键）：角色、内容、引用片段（JSON：`fragmentId/kind/title/score`）                                                                                                                        |
| `avatar_profile`                                                                          | `AvatarProfile`     | 单例基础画像：称呼、一句话定位等（v2 只在前端暴露称呼与定位，其余字段保留兼容）                                                                                                                                                 |
| `avatar_experiences` / `avatar_behaviors` / `avatar_relationships` / `avatar_simulations` | 同上节保留               | 经历、行为流、关系人、轨迹推演，作为补充素材与推演能力保留                                                                                                                                                             |

全部通过 SqlSugar CodeFirst 创建；标签、证据、引用等集合字段以 `text` 存 JSON，避免依赖 PostgreSQL 数组映射。注意 `avatar_profile.values` 的列名是 `values_text`（`values` 是 SQL 保留字）。

### 11.2 三条流程

**投喂**

1. 前端“投喂”页支持粘贴长文本（自动识别类型、可选日期/标签），或直接上传 `.txt/.md/.json/.log`（≤10 MB）。上传文件由 `AvatarEndpoints` 解码（UTF-8/BOM/UTF-16/GB18030）后按空行切段、单段超过 3000 字符再硬切，最多 200 段。
2. `AvatarFragment.Create` 负责类型归一化（未知类型落到 `other`）、空标题回退、长度上限校验。
3. 知识库问答与学习打卡会通过 `AvatarActivityRecorder` 自动写入碎片（学习/工作/创作 → `habit`，情绪 → `personality`，社交 → `relationship`，其它 → `journal`），同一来源+分类 30 分钟内只记一次，失败不影响主流程。

**分析**

1. `AvatarDigestService.DigestAsync` 取最近未分析的碎片（单次最多 24 段、每段截断 1500 字符），连同已有洞察一起发给聊天模型，要求输出**严格 JSON**：`{"insights":[{dimension,title,content,confidence,evidence[]}]}`，并强制至少一条 `summary`。
2. 解析是宽容的：整体非 JSON 或缺少 `insights` 时返回空集合，单条不合法则跳过，不影响其余结论；解析成功后按 `(dimension, title)` 覆盖写入，并把本次素材标记为已分析。
3. 用户可“重新分析全部”（`includeDigested: true`，取最近 200 段）或“清空洞察”（素材保留）。

**问答**

1. `AvatarPersonaChatService.AskAsync` 加载最多 300 段素材 + 全部洞察，用 `AvatarFragmentRetriever` 做中文二元组 + 英文词元打分（标题权重 3、正文命中上限 4、90 天内轻微时间加权），取前 10 段作为“我的记忆”；无关键词命中时退回最近 4 段，保证跨话题也能答。
2. 提示词分为「我的人格」「我的记忆」「我们刚才的对话」「现在对方问我」四段，系统提示强制第一人称“我”、禁止编造、禁止出现“作为 AI/根据资料”之类表述、150-400 字。
3. 回答连同引用片段一起落库，会话标题在首次提问后由“新的对话”自动改名。

### 11.3 完备度

`AvatarService.EvaluateReadiness(profile, fragmentCount, insightCount, chatCount)`：

| 维度   | 满分 | 规则                 |
| ---- | -- | ------------------ |
| 素材   | 45 | ≥10 段得 45，否则每段 4 分 |
| 洞察   | 25 | ≥5 条得 25，否则每条 5 分  |
| 基础画像 | 20 | 已配置称呼/定位得 20       |
| 对话   | 10 | 至少 1 个会话得 10       |

分数只用于提示“还缺什么”，不参与任何决策。

### 11.4 工具与端点

- 聊天模型工具（`AvatarToolSource`，分类 `avatar`）：`avatar_get_persona`（素材/洞察/完备度概览）、`avatar_feed`（沉淀一段素材）、`avatar_digest`（触发分析）、`avatar_ask_as_me`（以第一人称代答）。
- MCP 工具（`AvatarMcpTools`）：`avatar_get_persona`、`avatar_feed`、`avatar_digest`、`avatar_ask_as_me`、`avatar_list_fragments`，让其他智能体读取分身或写入素材。
- HTTP 端点：`GET /api/avatar`（总览）、`PUT /api/avatar/profile`、`GET/POST /api/avatar/fragments`、`POST /api/avatar/fragments/bulk`、`POST /api/avatar/fragments/upload`、`DELETE /api/avatar/fragments/{id}`、`GET/DELETE /api/avatar/insights`、`POST /api/avatar/digest`、`GET/POST /api/avatar/chat/sessions`、`GET /api/avatar/chat/sessions/{id}/messages`、`DELETE /api/avatar/chat/sessions/{id}`、`POST /api/avatar/chat`；另有保留的 `/experiences`、`/behaviors`、`/relationships`、`/simulations`。

### 11.5 边界与伦理

- 分身是**模仿**，不是本人：素材不足时 `AvatarPersonaChatService` 直接抛 `AvatarValidationException`（400），提示先投喂素材，而不是硬编一段回答。
- 分身输出不进入知识库，也不会被当作事实引用；知识库助手也不会把分身素材当成资料。
- 数据只保存在本机 PostgreSQL；使用云端聊天模型时，素材摘录与洞察会发送给所配置的服务商。
- 用户可随时删除单条素材、单个会话、清空全部洞察；不提供对外共享或自动决策能力。
