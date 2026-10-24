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

- 知识库支持 TXT、MD、DOCX、PDF、HTML/HTM；图片通过所选聊天模型生成描述，视频通过 FFmpeg 抽取音轨并由 Whisper.net 在 API 进程内转写，**并抽关键画面生成描述**，两者统一按时间轴切片后入库检索（详见下一节）。
    
  HTML/HTM 在 API 进程内抽取正文（标题层级、列表、表格单元格），不执行脚本、不加载外部资源；非 UTF-8 页面按文件 BOM 与 `<meta charset>` 声明解码（含 GBK/GB18030）。
- 图片能力要求所选模型支持视觉输入；视频处理需要随 API 部署的 FFmpeg 可执行文件。Whisper.net 使用 .NET NuGet 包和 CPU 原生运行时，不需要 Python/Whisper CLI；模型文件首次转写时自动下载并缓存，也可从设置页导入本地 ggml 模型（离线部署）。
- Companion 解析通用 JSON/TXT/CSV，以及 QQ 消息管理器导出的文本（块头 + 块内多行）与行内时间戳的通用格式；不读取微信/QQ 私有或加密数据库。JSON 接受顶层数组或 `messages` / `records` 数组。
- Companion 数据写入 PostgreSQL：人物档案、原始消息、画像、会话与消息；历史消息建立**独立向量索引**（`companion_message_vectors`），检索为「向量 + 词面」RRF 融合。向量模型不可用时自动退回纯词面。
- 知识库支持多库创建与切换；专题按知识库隔离，资料可归入单一专题。笔记仍是前端示例数据。
- 学习打卡与用户设置保存在 PostgreSQL；聊天模型设置可由前端修改并在进程内即时生效。

### 视频处理（时间轴对齐 + 视觉检索）

抽取音轨 → Whisper.net 转写 → **抽关键画面 + 视觉模型描述** → 两路结果按时间戳合并成时间轴切片 → 向量化入库。

- **抽帧策略**：先按场景切换抽（`select='gt(scene,0.25)'`，`Media:FrameSceneThreshold` 可调）；场景帧少于 2 张（幻灯片、固定机位、纯色背景）时退化为 `fps=1/30` 均匀采样（`Media:FrameSampleIntervalSeconds`）。单次上限 `Media:MaxVideoFrames`（默认 60），因为每帧都是一次真实的视觉模型调用。
- **时间戳来自工具本身**：从 `showinfo` 的 `pts_time` 反解每帧起始秒数。用「第几张 × 固定间隔」推算在场景检测下必然错位（大量帧被丢弃，序号与时间不成比例）。
- **切片规则（`MediaTimeline`）**：转写段与画面描述合并后按时间排序，每行形如 `[hh:mm:ss] 文本`，只在段边界切分；单行超长时仍自成一片，**时间戳永不与文本分离**。音频转写同样走这套规则（此前按字符数硬切，会把时间戳切成孤行）。
- **引用可定位**：`KnowledgeChatCitation.MediaStartSeconds` 从切片首行时间戳推导，`0` 是合法值；前端引用芯片据此显示播放图标与 `mm:ss`，点击后播放器 `currentTime` 跳到该秒数（下载端点启用 range，支持定位）。
- **降级边界**：视觉模型不可用时只记一条 warning，视频保持「仅转写」入库；抽帧失败同样只降级不失败——独立阶段不能把整篇资料变 `Failed`。当前未提取内嵌字幕轨道，也未做 OCR 与镜头级分割。

### 伴侣历史检索（独立向量空间）

`CompanionMemoryIndexer` 给导入记录中**目标人物本人**的消息建向量，写在 `companion_message_vectors`（`message_id` 主键 + `embedding` + 模型指纹 + 维度），**不进任何 `kb_*` 表**：私聊一旦混进资料索引，「问我的资料」就会把私聊当证据引用出来。

- 检索时词面与向量两路各取候选，按 RRF（k=60）融合；`sender` 与 `profile_id` 都是硬过滤条件，否则两个人的聊天会互相串。
- 少于 4 个字符的消息不建向量；单条超过 1000 字符只取前 1000 字再向量化。
- 向量模型跟随设置页选定的默认规格；切换模型后旧向量按指纹隔离，不会被新查询误用，需要点一次「重建索引」。
- 模型不可用时 `TrySearchAsync` 返回 `null`，调用方自动退回纯词面检索。
- 提示词里的召回片段**按相关度排序**（最相关在前），不做时间反转。

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
- 向量表按用途分开：知识库切片进 `kb_*` 索引表，伴侣历史进 `companion_message_vectors`。两边共用同一套默认向量模型，但**互不写入**，避免「问资料」时把私聊当证据、或「问往事」时把资料当聊天。
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

> 阶段 0–8 已落地；当前仍未完成的事项（Electron 打包/自动更新、插件与任务中心界面、混合检索、多用户认证、插件隔离等）统一收集在 **`docs/ROADMAP.md`**，按 P0–P3 排序，每项附现状与验收标准。

## 9. 当前 MVP 启动配置

API 启动时通过 SqlSugar CodeFirst 实体幂等创建知识库、资料、分块、上传、问答历史和 AI 伴侣表及普通索引；PostgreSQL 的 `vector` 扩展、外键/CHECK 约束、`lower(name)` 表达式唯一索引和旧表数据回填仍使用必要的 PostgreSQL SQL。

> **SqlSugar 的一个类型推断陷阱（已踩过，务必绕开）**：`Updateable<T>().SetColumns(it => new T { TimeCol = entity.TimeCol })` 在右值是**求值为 `null` 的可空列**时，会把参数声明成 `DbType.AnsiString`（`Guid?` 同样退化成字符串）。参数值本身确实是空，但 PostgreSQL 类型检查看的是**参数的声明类型**，于是报
> `42804: column "completed_at" is of type timestamp with time zone but expression is of type text`。
> 症状极具迷惑性：`avatar_feed` 返回 200、任务被领成 `running`，然后**第一次状态回写就炸**，投喂的内容永远进不了记忆库。
>
> 实测规律（`Updateable().ToSql()` 拿到的参数类型）：
>
> | 写法 | 空值时的参数类型 | 可用 |
> | --- | --- | --- |
> | `SetColumns(it => new T { Col = null })` | `DateTimeOffset` / `Guid`（按列类型） | ✅ |
> | `SetColumns(it => new T { Col = someVar })`（`someVar` 为 null） | `AnsiString` | ❌ 42804 |
> | `Updateable(entity).UpdateColumns(it => new { it.Col })` | 按实体属性类型 | ✅ |
> | `Insertable(entity)` | 按实体属性类型 | ✅ |
>
> 因此规则是：**可空列的值来自变量时，不用 `SetColumns`**——要么写实体驱动的 `UpdateColumns`，要么写显式 SQL 加 `CAST(... AS timestamptz)`。已按此修正 `avatar_ingestions` 的三条状态语句（显式 SQL）与 `UpdateMemoriesAsync` / `UpdateSimulationAsync` / `UpdateEvalRunAsync`（`UpdateColumns`）。回归测试：`AvatarIngestionUpdateCommandTests`、`NullableColumnBindingTests`。


默认情况下不需要外部数据库：`ConnectionStrings:KnowledgeBase` 留空时，`PortablePostgresHost` 会启动随应用发布的 `ExternalTools/pgSQL/pg0-windows-x86_64.exe`（内置 PostgreSQL 18 + pgvector），创建目标数据库并执行 `CREATE EXTENSION IF NOT EXISTS vector`。数据目录默认在 `%LOCALAPPDATA%\MyFirstMafAgent\pgsql`，应用退出时自动 `pg0 stop`。

如需使用已有 PostgreSQL，通过环境变量或用户机密提供连接串（默认会跳过 pg0）；运行 API 的用户需有创建扩展/表/约束的权限，不要把真实密码写入配置文件：

```powershell
$env:ConnectionStrings__KnowledgeBase = "Host=localhost;Port=5432;Database=knowledge_base;Username=postgres;Password=<your-password>"
dotnet run --project MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj
```

聊天模型通过 OpenAI-compatible 接口配置。`Llm:UseCloudApi=true` 选择 `Llm:Online`，`false` 选择 `Llm:Local`。Embedding 使用 API 内的 ONNX Runtime；`Embedding:ModelDirectory` 指向包含 `model.onnx` 和 `tokenizer.json` 的目录，当前 BGE 配置为 512 维。更换模型或维度后不要混用旧向量，需重新导入/向量化已有资料。

PDF 和 DOCX 在 API 进程内提取文字；图片描述走当前选择的聊天模型，因此所选模型必须支持图片输入。视频由 `Media:FfmpegPath` 指定 FFmpeg（默认使用随 API 发布的 `ExternalTools/ffmpeg/ffmpeg.exe`），Whisper.net 使用 `Media:WhisperModel` 指定模型类型，并将模型缓存到 `Media:WhisperModelPath`。模型文件由 `WhisperModelManager` 单独管理：设置页可看状态、导入本地 ggml 文件（`/api/media/whisper/import-raw`）或按需后台下载（`/api/media/whisper/download`）；`Media:AutoDownloadWhisperModel=false` 时缺模型会直接报"去设置页导入"，适合离线部署。导入/替换后推理对象按文件指纹自动重建，不需要重启。工具缺失或模型不支持图片时，原文件仍会保留，资料状态显示为 Failed 并附带处理错误；视频摘要需要聊天模型，失败时只丢摘要、转写内容照常发布。

前端使用 `cd FrontEnd/myfist-agent; npm run dev` 启动；API 默认地址为 `http://127.0.0.1:5056`。前端默认使用同源请求，Vite 通过 `server.proxy` 把 `/api`、`/mcp` 转发到 5056，因此不再需要设置 `VITE_API_BASE_URL`。先启动并确认 `/api/health` 返回数据库已连接，再上传资料。API 路由按领域定义在 `MyFirstMafAgent.Api/Endpoints/`；`/api/search` 提供显式相似度检索，`/api/chat` 会自动检索并返回引用。知识库问答历史支持会话重命名、单条/批量删除和 Markdown 导出，具体路由见前端 README。

发布形态有两种：`MyFirstMafAgent.Api`（控制台 + 自动打开浏览器）与 Electron 桌面壳（`desktop-electron/`，把同一份 API 当作 sidecar 子进程拉起）。前端 `vite.config.ts` 使用 `base: './'`，构建产物由 `ApiHost` 通过 `wwwroot` 托管；`scripts/Publish-Portable.ps1` 负责构建前端、`dotnet publish`（self-contained win-x64，非 AOT）、复制 `wwwroot` 并校验 pg0/Embedding 模型——`-Target desktop` 发布的就是 **API 本体**，放到 `publish/desktop`，Electron 从那里拉起 sidecar。安装包由 electron-builder（NSIS）生成：壳只打 `main.js`/`preload.js`，后端通过 `extraResources` 整体放到 `resources/api`；`scripts/Build-ElectronInstaller.ps1` 一把梭（发布 + 打包），产物在 `publish/releases`。便携数据库统一从 `%LOCALAPPDATA%\MyFirstMafAgentData\tools` 运行、工作目录设在 `pgsql` 下，避免 `postgres.exe` 锁住安装目录导致升级失败。

发布目录**不能放在桌面/文档/下载**：Windows「受控文件夹访问」会阻止从这些位置启动的未签名程序写入用户目录，便携数据库会启动失败。应用目录本身可以含中文，`PortablePostgresHost` 会把 pg0 复制到 `%LOCALAPPDATA%\MyFirstMafAgent\tools` 后运行（pg0 要求可执行路径与数据目录均为纯 ASCII）。

### 9.1 宿主分层：模块化组合根

`ApiHost.cs` 只保留启动顺序与生命周期（内容根解析、数据目录重定向、便携数据库、构建、初始化、映射、退出）。具体注册项拆到 `MyFirstMafAgent.Api/Hosting/`：

| 文件 | 职责 |
| --- | --- |
| `HostBootstrap.cs` | 数据目录重定向、JSON/文件日志、Kestrel 请求体上限、ProblemDetails、OpenAPI、CORS |
| `HostOptionsModule.cs` | 所有配置项的绑定与启动校验（Llm/Storage/Embedding/Media/Indexing/Agent/Mcp/WebSearch/PortablePostgres） |
| `PersistenceModule.cs` | `SqlSugarScope` 与全部仓储；领域端口在这里绑定到 Infrastructure 实现 |
| `KnowledgeModule.cs` | 向量规格、索引生命周期、检索路由、摘要、摄取与检索应用服务 |
| `DocumentsChatsModule.cs` | 资料处理、上传、转写、聊天、伴侣，以及笔记/主题/学习服务 |
| `AvatarModule.cs` | 分身记忆、摄取、证据链、人格候选、反馈与评测 |
| `AgentModule.cs` | 模型客户端、运行期设置/偏好、工具来源与 `ToolCallingAgent` |
| `McpModule.cs` | MCP 服务端（Streamable HTTP）与外部 MCP 客户端注册表 |
| `WorkerModule.cs` | 资料处理、索引生命周期、分身摄取三个后台 Worker |
| `PluginModule.cs` | 插件 manifest 加载、进程宿主与生命周期、工具桥接、平台摄取下沉 |
| `HostPipeline.cs` | 请求日志、异常处理、静态前端、REST/MCP/OpenAPI 映射与 SPA 回退 |
| `DatabaseInitializer.cs` | 建表/补列、恢复中断资料、加载设置、注册向量规格、准备默认索引 |

新增宿主（Electron sidecar 或新的渲染壳）**必须复用 `ApiHost.BuildAsync`**，不得复制服务注册或中间件；宿主差异只体现在监听地址、窗口生命周期和用户数据目录参数上。桌面壳现在只有 Electron 一种实现；进程内嵌 Kestrel 的 `MyFirstMafAgent.Desktop`（Photino + Velopack）已整工程删除，不要再把它当作可用入口。

依赖方向为 `Endpoints/Adapters → Application → Domain`，`Infrastructure` 实现应用与领域端口。可用以下命令持续检查（Domain 不得引用 SqlSugar/ASP.NET Core/Application，Application 不得引用 SqlSugar/Infrastructure，Endpoints 不得直接使用 SqlSugar）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Check-Architecture.ps1
```

阶段 0 的性能与向量索引基线采集方法见 `docs/PHASE0-BASELINE.md`；在该基线完成前，不得以“加线程”“换向量库”作为性能结论。

### 9.2 桌面宿主：Electron sidecar（阶段 2）

Electron.NET 现阶段不能作为基线：`ElectronNET.Core` 最新为 `0.6.0`（预览重写版），`ElectronNET.API` 停留在 Electron 23。按方案启用既定备选方案——**原生 Electron 主进程 + .NET API sidecar**，REST/MCP 契约完全不变。控制台入口保留为无界面/调试入口，桌面端只保留这一条路径（旧的 Photino 壳与 Velopack 打包已删除）。

| 组件 | 位置 | 说明 |
| --- | --- | --- |
| Electron 主进程 | `desktop-electron/main.js` | 单实例锁、随机端口、一次性令牌、子进程生命周期、外链拦截、菜单与退出 |
| Preload | `desktop-electron/preload.js` | 白名单：`desktopHost.platform`、`desktopHost.openExternal`；**不**下发令牌 |
| 本机鉴权 | `Hosting/DesktopSecurity.cs` | `X-Api-Token` 仅覆盖 `/api` 与 `/mcp`；固定时间比较；未配置令牌时整体禁用 |
| 启动契约 | `Hosting/DesktopHostArguments.cs` | `--desktop-ready-file`、`--desktop-data-root`；ready 文件只含 `url`/`pid`，不含令牌 |
| 控制面 | `Endpoints/DesktopEndpoints.cs` | `GET /api/desktop/info`、`POST /api/desktop/shutdown`（仅桌面模式注册） |
| 渲染安全 | `ApiHost` + `DESKTOP_STRICT_CSP=1` | 严格 CSP 响应头；`contextIsolation`/`sandbox` 开启、`nodeIntegration` 关闭 |

启动顺序：pg0 → API（`http://127.0.0.1:0`）→ 轮询 ready 文件 → 轮询 `/api/health` → 创建窗口。退出顺序：`POST /api/desktop/shutdown` → 等待 API 退出（≤20s）→ 强制结束；`ApplicationStopping` 只停止本次启动的 pg0。

令牌不入 URL、日志、ready 文件或渲染进程；主进程用 `webRequest.onBeforeSendHeaders` 注入请求头，因此前端 `api.ts` 无需改动。

契约验证（不需要 Electron）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-DesktopContract.ps1 -ApiExe <published>\MyFirstMafAgent.Api.exe
```

AI 伴侣导入端点为 `POST /api/companions/import`（multipart 字段：`name`、`targetSender`、`platform`、`file`），格式为 UTF-8 JSON/TXT，最大 20 MB。`targetSender` 必须匹配导出中的发言者名称。生成画像、开启会话及对话需调用当前选择的聊天模型；云端模式会将提示、相关历史片段及本轮对话发送给配置的 API 服务商。该功能用于生成 AI 模拟，不应被呈现为逝者本人；导入前应确认有权处理聊天记录。

### 9.3 插件运行时（阶段 3）

插件以**独立子进程**运行，通过版本化 JSON-RPC 2.0 over stdio 与核心通信。不使用 `AssemblyLoadContext` 做第三方隔离——它的卸载是协作式的，无法提供强制卸载或安全边界。

| 组件 | 位置 | 职责 |
| --- | --- | --- |
| 协议与 SDK | `MyFirstMafAgent.Plugins.Sdk` | manifest 模型与校验、SemVer、JSON-RPC 分帧、`PluginServer` 插件侧宿主。**不引用 ASP.NET/SqlSugar** |
| manifest 加载 | `Plugins/PluginManifestLoader.cs` | 扫描插件目录、schema 校验、入口可执行文件解析 |
| 进程宿主 | `Plugins/PluginProcessHost.cs` | 启动子进程、握手校验、健康检查、带超时/取消的调用、崩溃标记 |
| 隔离计划 | `Plugins/Isolation/PluginIsolationPlanner.cs` | 权限声明 → 能力 SID + 授权路径 + 环境重定向（纯计算，可单测） |
| 隔离执行 | `Plugins/Isolation/WindowsAppContainer.cs`、`AppContainerPluginProcess.cs` | 建配置、写 ACL、`CreateProcess` + `SECURITY_CAPABILITIES` + Job 对象 |
| 启动选择 | `Plugins/Isolation/PluginProcessFactory.cs` | 按 `Isolation.Mode` 决定「隔离启动 / 降级启动 / 拒绝启动」，并收窄环境变量 |
| 生命周期 | `Plugins/PluginManager.cs` | 发现→启动→退避重启→停止；单个插件失败不影响核心启动 |
| 工具桥接 | `Plugins/PluginToolSource.cs` | `agent-tool` → `IAgentToolSource`，命名 `plugin__{plugin}__{capability}` |
| 摄取下沉 | `Plugins/PluginIngestionSink.cs` | `platform-connector` 结果包装成一次普通上传，复用版本/Outbox/索引生命周期 |
| 端点 | `Endpoints/PluginEndpoints.cs` | `/api/plugins` 状态、健康、调用、平台同步 |

**协议**：`plugin/initialize`、`plugin/configure`、`plugin/health`、`plugin/capabilities/list`、`plugin/capabilities/invoke`、`plugin/shutdown`，通知 `plugin/cancel`、`plugin/log`。一行一个 JSON（NDJSON），单行 ≤ 8 MiB；握手时校验 `pluginId`/`pluginVersion` 与 manifest 一致，且不允许插件报告未声明的能力。

**两种方言（D-2）**：同一个进程宿主、同一套并发/审计/排空逻辑，只换方法名与结果形状。

| 维度 | `dialect: plugin`（自有） | `dialect: mcp`（标准 MCP） |
| --- | --- | --- |
| 握手 | `plugin/initialize` → 校验 id/version 与清单一致 | `initialize`（`protocolVersion: 2024-11-05`）→ `notifications/initialized`；`serverInfo` 只记录不对账 |
| 能力来源 | 清单 `capabilities`（插件不允许发明） | `tools/list`（跟 `nextCursor` 分页，≤20 页），每个 tool → 一个 `agent-tool` |
| 调用 | `plugin/capabilities/invoke` | `tools/call`，`content` 数组压平成文本；`isError: true` → 调用失败 |
| 健康 | `plugin/health` | `ping`（对端回 `-32601` 视为健康） |
| 退出 | `plugin/shutdown` | 关闭 stdin（MCP 约定的退出信号） |
| 取消 | `plugin/cancel` + `targetId` | `notifications/cancelled` + `requestId` |
| 配置 | `plugin/configure` 下发 JSON | 启动时环境变量：`entry.env` + 用户配置的标量字段注入为 `MYFIRSTMAFAGENT_CONFIG_<KEY>` |

为什么值得做成两种方言而不是"只吃自己的清单"：MCP 与本项目的传输层完全同构（JSON-RPC 2.0 over stdio、一行一条、stdout 只放协议消息），差异全在方法名和结果形状这一层。代价是一个分支，收益是**任何现成的 stdio MCP server 写 5 行清单就能用**——插件生态的规模效应比自定义协议的整洁更重要。

**清单宽容度（同一件事的两面）**：认三种清单文件名（`manifest.json` / `plugin.json` / `skill.json`）、认生态词汇别名（`inputSchema`→`parameters`、`net.outbound`→`network`、`fs`→`file-read`+`file-write`、`proc`→`process`、`args`→`arguments`、`command` 写成数组、顶层 `requires`）。所有改名都由 `PluginManifestNormalizer` 在读入时完成，并作为 **warning 透出**——不静默生效；认不出来的权限词原样保留并报错，作者必须能看见自己写错了什么。

**目录即安装**：`App_Data/plugins/{任意目录}/` 下放清单即可被发现；解压多套了一层（`plugins/demo/demo/manifest.json`）时会下探，但只在"恰好一个子目录"时下探——多于一个候选说明这更像一个杂项目录，硬猜会选错。入口是宿主命令且没有文件参数时（`npx -y @scope/server`）不判"文件不存在"：路径由包管理器在运行期决定，安装期检查不出来。

**两类能力**：`agent-tool` 暴露给模型；`platform-connector` 只能经同步端点使用，返回统一摄取结构（`source`/`externalId`/`title`/`type`/`content`/`updatedAt`/`contentHash`/`retracted`/`metadata`），由核心调用既有摄取用例，插件无法直接写表。MCP 方言下所有 tool 都是 `agent-tool`，副作用等级取自 MCP 的 `annotations`（`destructiveHint`→`destructive`、`readOnlyHint:false`→`write-local`、否则 `read`），清单里同名能力可覆盖。

**安全边界（D-1：真隔离，不是「同一个用户下的子进程」）**：

Windows 上用 **AppContainer** 启动插件子进程——不是受限令牌，因为受限令牌只能削特权，**削不掉网络**；AppContainer 是低完整性 + 能力模型，令牌里没有 `internetClient`（`S-1-15-3-1`）时，进程连出站 socket 都建不出来（实测 `EACCES`，不是"口头禁止"）。

| 关注点 | 实现 | 实测证据 |
| --- | --- | --- |
| 建容器 | `CreateAppContainerProfile`（已存在则 `DeriveAppContainerSidFromAppContainerName`） | SID 形如 `S-1-15-2-…` |
| 能力 | 有 `network` 才附加 `S-1-15-3-1` | 无 → `CONNECT_ERR EACCES`；有 → `CONNECT_OK` |
| 文件 | 插件目录 `(OI)(CI)(RX)`、数据目录 `(OI)(CI)(M)`、`plugins/.runtimes` 只读 | `icacls` 可见容器 SID 的 ACE |
| 进程树 | `CREATE_SUSPENDED` → `AssignProcessToJobObject(KILL_ON_JOB_CLOSE)` → `ResumeThread` | 核心退出后孙进程不会滞留 |
| 环境 | 变量白名单（不复制连接串/API Key）+ `USERPROFILE`/`TEMP`/`APPDATA` 重定向到插件数据目录 | 容器写不了用户目录，不重定向运行时会在启动阶段就失败 |
| 拒绝 | 入口程序对容器不可读时**拒绝启动**并给出可照做的建议 | `C:\Program Files\nodejs\node.exe` 只授了 `Users`，读不到 |

三条踩出来的硬约束，改代码前务必先读：

1. **只授叶子目录，绝不碰祖先**。容器凭插件目录上的继承 ACE 就能穿越整条路径（已在 `C:\Temp` 与「用户目录 + 中文路径」上实测）。给 `C:\` 加一条 ACE 会让 Windows 在整个驱动器上递归重整继承——实测卡死数分钟；何况普通用户本来也无权改 `C:\` 的 ACL（`拒绝访问`）。
2. **已经授过的目录不要重复写 ACL**。`SetAccessControl` 每次都会触发继承重整，插件重启时等于把成本乘上启动次数。`ApplyGrants` 因此先查是否已有等价 Allow 规则。
3. **判断"容器能不能读"只看对象自己的有效 DACL**（显式 + 已继承）。沿祖先找可继承 ACE 会被 `C:\Program Files`（有 `ALL APPLICATION PACKAGES:(OI)(CI)(RX)`，但 `nodejs` 这一层断了继承）骗过。

`Plugins:Isolation:Mode`：`require`（默认，Windows）、`warn`、`off`；非 Windows 平台默认 `warn` 且报告 `kind: unsupported`——没有沙箱时**不假装有**，接口里 `applied=false` 会如实回显。运行时统一放 `App_Data/plugins/.runtimes/{命令名}/`，清单里继续写 `"command": "node"` 即可（解析顺序：`.runtimes` 优先于 `PATH`）。Node 插件额外需要跳过主模块 realpath，核心自动注入 `NODE_OPTIONS=--preserve-symlinks-main`（否则 node 会 `EPERM: lstat 'C:\'`）。

**仍然不做的**：AppContainer 不是恶意代码保险箱——它能限制网络与文件可见范围，但同机内核提权、侧信道、DoS 仍然无解。签名与摘要校验见 §9.4（`IntegrityPolicy=enforce` 可强制）。插件写目录被限制在 `plugins/.data/{pluginId}`；跨进程不继承连接串等机密。

**调用准入（阶段 11）**：`Application/Plugins/PluginInvocationLimiter.cs` 是唯一的准入点，取代了 `PluginProcessHost` 里那个**排队无上限**的 `SemaphoreSlim`。四层限制（插件队列 → 能力队列 → 能力并发 → 插件并发）中任何一层不满足都立刻返回 `-32006` / HTTP 429，而不是让调用方挂到自己的超时。三个必须保持的约束：**在途上限 = 并发 + 队列**（因此 `queue=0` 是「没有空位就拒绝」而不是「一律拒绝」）；**先拿能力闸门再拿插件闸门**（反了会让调用一边占插件容量一边空等，且插件级 `running` 虚高）；**任何失败路径都要把计数与信号量还回去**。计数在进程内，多进程部署时是「每进程上限」。

**验证**：`scripts/Test-PluginContract.ps1` 以真实子进程跑完握手、能力发现、健康、两类调用、结构化错误与关闭（18 项检查）。插件作者文档：`docs/PLUGIN-AUTHOR-GUIDE.md`；准入控制细节与实测数据见 `docs/PLUGIN-LIFECYCLE.md` §14。

**插件数量**：扫描不截断、列表不分页、注册表不设配额——**没有数量上限**。唯一收敛的是 `Plugins:StartupConcurrency`（默认 4）控制的“同时 spawn 多少进程”，因为每个插件就是一个子进程。`POST /api/plugins/rescan` 支持热发现（把文件夹拷进去即可，不必重启 API），且**扫描不覆盖用户的停用意图**。

### 9.3.1 媒体资产：图片与音频（阶段 11）

存储层把“资料”和“资产”统一了：同一份原文件既参与检索，也可以直接展示/播放。

| 类型 | 解析 | 原文件用途 |
| --- | --- | --- |
| 图片（png/jpg/jpeg/webp/gif） | 视觉模型描述 → 可检索文本 | 缩略图、表情包、截图 |
| 音频（mp3/wav/m4a/aac/ogg/opus/flac/weba/amr） | FFmpeg 归一化 16 kHz 单声道 → Whisper 转写 → 可选摘要 | 语音消息、录音、音效 |
| 视频（mp4/mov/mkv/webm/avi） | 抽音轨转写 + 可选关键帧描述 | 原片下载 |

两个设计判断，都来自“存不下来”的真实失败：

- **解析失败不能连原文件一起丢掉。** 图片没有视觉模型、音频没有语音内容，都是常态；此时发布一句兜底文本（`图片资料：…（未生成图片描述：视觉模型不可用）`）而不是整篇 `Failed`——用户要的是「能存、能看」，检索是加分项。
- **Content-Type 按扩展名归一化，不信客户端。** 浏览器与脚本经常上报 `application/octet-stream`；直接采信会让 `/download` 把图片当二进制下载、让 `<audio>` 播不出来。`GET /api/documents/{id}/download` 开了 `enableRangeProcessing`，所以音频可以拖动进度条（实测 `206 Partial Content`）。

体积上限分三档而非一刀切：`Storage:MaxFileSizeBytes`（50 MiB 文档）、`MaxAudioFileSizeBytes`（300 MiB）、`MaxVideoFileSizeBytes`（5 GiB），三者都会参与 Kestrel 请求体上限的取最大值计算。

### 9.4 插件生命周期与安全（阶段 4）

状态分两层：**用户意图**（是否启用、配置、已授予权限）落库到 `plugin_installations`；**文件布局**（已安装版本、当前生效版本）落在 `plugins/{id}/versions/*` + `active.json`；进程与健康只在内存，重启后重新发现。

| 组件 | 位置 | 职责 |
| --- | --- | --- |
| 注册表端口 | `Application/Plugins/IPluginRegistry.cs` | 安装态与审计的持久化抽象，含 `PluginRegistration` / `PluginAuditRecord` |
| 注册表实现 | `Infrastructure/Persistence/SqlSugarPluginRegistry.cs` | `plugin_installations`、`plugin_audit_events` 两张表 |
| 权限策略 | `Application/Plugins/PluginAccessPolicy.cs` | 启动闸门（声明∩授予）、授权校验、副作用确认判定，纯函数 |
| 完整性 | `Plugins/PluginIntegrity.cs` | 规范化包摘要（SHA-256）+ ECDSA/RSA 签名校验，`off/warn/enforce` 三档策略 |
| 目录布局 | `Plugins/PluginPaths.cs` | `versions/{v}` + `active.json` + `.data` + `.staging` 的唯一计算入口 |
| 安装器 | `Plugins/PluginInstaller.cs` | ZIP 安装（拒绝路径穿越）、激活、回滚、卸载、旧版本修剪 |
| 审计 | `Plugins/PluginAuditWriter.cs` | 写入审计，入参自动脱敏与截断，失败不阻断业务 |
| 配置校验 | `MyFirstMafAgent.Plugins.Sdk/JsonSchemaLite.cs` | JSON Schema 子集校验（type/properties/required/enum/pattern/范围/组合） |

**启动四道闸门**（顺序固定，任一不满足不拉起进程）：启用 → 完整性 → 必填配置 → 权限。对应状态分别为 `Disabled`、`Failed`、`NeedsConfiguration`、`PermissionRequired`。

**升级与回滚**：每个版本是完整目录，升级写 `active.json` 并重启，`previous` 字段记住上一版；回滚是反向换指针，回滚后启动失败会自动切回原版本。不原地覆盖文件——Windows 上运行中的 exe 无法替换。

**副作用闸门**：`write-external` / `destructive` 能力在 `RequireConfirmationForSideEffects=true` 时**不桥接给模型**（模型无法提供确认），直接调用必须带 `confirm=true`，否则 403。

**审计**：`install/uninstall/rollback/enable/disable/start/configure/grant/invoke/sync/health/integrity-rejected`，记录发起者（api/agent/system）、结果、耗时与脱敏后的入参；卸载不删除历史审计。

**边界澄清**：权限闸门是准入控制，不是内核级强制；真正的隔离（受限令牌、AppContainer、容器）与在途请求排空式升级仍未实现。详见 `docs/PLUGIN-LIFECYCLE.md`。

### 9.5 平台条目映射与作用域（阶段 5）

插件把外部平台内容送进知识库时，真正难的不是"写进去"，而是**不重复写、能撤回、不越界**。这三件事由一张映射表 + 一张游标表 + 两个策略类兜住，插件本身不参与去重决策。

| 组件 | 位置 | 职责 |
| --- | --- | --- |
| 判定矩阵 | `Plugins/PlatformItemDecision.cs` | 纯函数：`(本次条目, 既有映射) → Create/Update/Skip/Retract/Tombstone/Reject`，并做哈希规范化 |
| 摄取端口 | `Application`→`Plugins/IPluginContentStore.cs` + `KnowledgePluginContentStore.cs` | 适配核心摄取用例（`KnowledgeIngestionService` / `KnowledgeBaseStore` / `LocalDocumentStore`），插件永不直接写表 |
| 映射与游标端口 | `Application/Plugins/IPluginItemMap.cs` | `IPluginItemMap` + `IPluginSyncStateStore`，含 `PlatformItemLink`、`PluginSyncState` |
| 映射与游标实现 | `Infrastructure/Persistence/SqlSugarPluginRegistry.cs` | `plugin_platform_items`、`plugin_sync_states` |
| 摄取编排 | `Plugins/PluginIngestionSink.cs` | 查映射 → 判定 → 调内容端口 → 写回映射/墓碑；配额与作用域在这里执行 |
| 作用域策略 | `Application/Plugins/PluginScopePolicy.cs` | 校验 `allowedKnowledgeBaseIds` / `maxDocuments`（拒绝 `Guid.Empty`、不存在的库、负数配额，自动去重） |
| 对话作用域 | `Agents/Tooling/AgentKnowledgeScope.cs` | 把同一个限制下推到 Agent 工具层，越界返回可读拒绝而非空结果 |

**映射键带知识库**：`plugin_id + source + external_id + knowledge_base_id`。同一条平台内容镜像到两个知识库时，两边的映射、配额、撤回互不影响；如果键里不带知识库，B 库的撤回会把 A 库的资料一并删掉。

**撤回即删除 + 墓碑**：撤回时删掉资料并把映射置为 `retracted`、`document_id` 置空，映射行保留。这样迟到的旧批次无法让已删除内容复活（墓碑 + 撤回 → `Skip`），既保证"删干净"又保证"不复活"。`document_id` 为空还顺带覆盖了"资料被用户手工删除"的场景：下一次同步退化为重新落库，且同样受配额约束。

**游标作用域**：`scope_key = pluginId|capabilityId|knowledgeBaseId`。请求不传 `cursor` 就用持久化游标；能力调用失败保留旧游标，避免一次瞬时故障导致整库重放。

**配额按知识库计**：插件级 `maxDocuments` 优先，0 表示跟随全局 `DefaultMaxDocumentsPerPlugin`（0 = 不限制）。超额条目计入 `quotaRejected` 并给诊断，同批次其他条目照常处理，同步状态降级为 `partial`。

**Write-ahead 顺序**：先落资料、再写映射。反过来的话，映射指向不存在的资料会让条目永久卡住；现在这条路径会退化为重新落库。审计与 `sync-state` 记录每次同步的计数与失败原因，便于对账。

Embedding 使用 `Microsoft.ML.OnnxRuntime` 和 `Microsoft.ML.Tokenizers` NuGet 包在 API 内运行本地 BGE-small-zh-v1.5，不需要独立 Embedding 服务；模型目录随构建和发布输出部署。视频处理使用 `Whisper.net` 与其 CPU runtime NuGet 包，不需要 Python 或 Whisper CLI；Whisper 模型文件首次转写时下载并缓存。FFmpeg 仍是原生可执行工具，项目会将其复制到构建和发布目录；重新分发时需遵守 FFmpeg 对应构建的许可要求。

### 9.6 长任务并发与任务中心（阶段 7）

阶段 0–6 的所有长任务都跑在"一个后台循环里排队做"的模式上：`DocumentProcessingWorker` 每 1.5 秒捞一篇 `Queued` 资料解析，做完再捞下一篇。这在单用户演示里看不出问题，但它是**并发度为 1**：一次导入 20 篇资料，第 20 篇要等前 19 篇全部解析完；期间没有进度、不能取消、进程一崩就停在"处理中"。

阶段 7 把这套机制换成"库里的任务 + N 个并发槽位"，并顺带交付了任务中心（查看 / 取消 / 重跑 / 实时进度）。

| 组件 | 位置 | 职责 |
| --- | --- | --- |
| 任务模型 | `Domain/Jobs/JobModels.cs`、`JobRecord.cs` | 状态机（`Queued/Running/Succeeded/Failed/Cancelled`）、优先级、重试策略、租约判定 |
| 队列端口 | `Application/Jobs/IJobQueue.cs`、`IJobHandler.cs` | 入队/领取/心跳/进度/完成/失败/取消/重跑/回收/清理；处理器契约与类型索引 |
| 调度器 | `Application/Jobs/JobScheduler.cs` | N 个并发槽位，各自循环"回收 → 补投 → 清理 → 领取 → 执行" |
| 队列实现 | `Infrastructure/Persistence/SqlSugarJobQueue.cs` | `jobs` 表 + 乐观 CAS 领取 + 租约续期 + 失败退避 |
| 资料解析任务 | `Documents/DocumentIngestJobHandler.cs` | 处理器（`document.ingest`）+ 补投器（从 `kb_documents.status` 推导任务） |
| 任务中心 API | `Endpoints/JobEndpoints.cs` | 列表/计数/类型/详情/取消/重跑 + SSE 实时流 |
| 配置 | `Application/Jobs/JobOptions.cs` | 并发度、轮询、租约、重试、保留期、排队上限、补投间隔 |

**为什么是固定槽位而不是"来一个任务起一个线程"**：并发度直接等于同时占用的数据库连接数与 CPU，无限并发会把库和内存一起打满，最后连健康检查都超时。`MaxConcurrentJobs` 是运维按机器能力定的上限（默认 4）。

**每个槽位必须独立 ExecutionContext**：槽位通过 `Application/BackgroundLoop.RunIsolatedAsync` 启动。SqlSugar 把 `SqlSugarClient`/连接缓存在 `AsyncLocal` 里，而 hostsed service 与请求处理共享同一个 ExecutionContext——不隔离就会在并发时抛 `A command is already in progress`。这是阶段 0 起就存在的硬约束，任务系统只是把它从"一个循环"扩到"N 个循环"。

**领取用乐观 CAS 而不是 `SKIP LOCKED`**：项目约定业务 SQL 只写在 `PgVectorSql.cs`（向量相关），其余一律用 SqlSugar 表达式，所以领取动作是"先按优先级取一批候选，再逐条 `UPDATE jobs SET status='Running', worker_id=?, lease_until=? WHERE id=? AND status='Queued'`"。抢到（影响行数=1）才算领取成功，抢不到换下一条。多进程并存时同样不会重复执行。

**租约 + 心跳 + 僵尸回收**：领取时写入 `lease_until = now + LeaseSeconds`，执行期间续租（处理器上报进度时会顺带续租，所以长文档不需要额外心跳）。进程被杀后没人续租，租约到期即被 `RecoverStaleAsync` 放回队列——这就是"崩溃不丢任务"的实现方式，重启后任务自动重跑，不需要人工干预。心跳间隔取 `clamp(LeaseSeconds/3, 1s, 5s)`：上限 5 秒是刻意的，因为**取消请求是靠心跳读回来的**，如果按租约/3 算，默认 120 秒租约要等 40 秒才响应取消。

**重试与退避**：可重试失败回到 `Queued` 并设置 `next_run_at = now + base * 2^(attempts-1)`（有上限），超过 `MaxAttempts` 才真正 `Failed`。`JobExecutionException(fatal: true)`（如"资料不存在"）不重试。**停机不算失败**：`Cancelled` 只由用户取消产生，进程退出时任务保持 `Running`，等租约过期回收。

**任务由业务状态推导，而不是接口里顺手入队**：`document.ingest` 任务的唯一来源是补投器——它扫 `kb_documents.status = 'Queued'`，为每篇资料入队一条任务，幂等键 `document.ingest:{documentId}`。好处是双写不可能不一致：上传接口漏了、进程崩了、插件直接写库了，下一轮补投都会补齐；重复投递被唯一索引挡住。补投器已结束的任务是否重新武装，用"资料 `updated_at` 是否晚于任务 `completed_at`"判定：晚于说明是用户主动"重新解析"，重新武装；否则若是 `Cancelled`，就让资料进入 `Failed` 而不是复活任务（否则取消等于没取消）。

**取消的语义**：`Queued` 任务取消直接终态；`Running` 任务取消通过心跳回传 `CancelRequested`，调度器取消处理器的 `CancellationToken`，处理器负责把资料落到一个**不会自动复活**的状态（`Failed`，可手动重试）。重复点击取消是幂等的（已结束返回 409）。取消是**协作式**的：请求最多 5 秒经心跳抵达，之后还要处理器真的检查令牌；如果工作恰好在取消抵达前完成，任务会是 `Succeeded`——这也算诚实的结论，而不是骗一个 `Cancelled`。

**SSE 而不是轮询**：`GET /api/jobs/stream` 先推一帧 `snapshot`（当前计数），之后每次状态变化推 `job` 帧，15 秒一次 `: ping` 保活。每个订阅者一个容量 64 的有界通道（`DropOldest`），慢客户端只丢自己的旧事件，既不阻塞任务也不影响其他订阅者。响应显式 `DisableBuffering` 并带 `X-Accel-Buffering: no`，否则反向代理会把 SSE 攒成"一次性响应"。

**配置**（`Jobs` 节）：`Enabled`、`MaxConcurrentJobs`、`PollIntervalMs`、`LeaseSeconds`、`MaxAttempts`、`RetryBaseSeconds`、`RetryMaxSeconds`、`RetentionHours`、`MaxQueuedJobs`、`FeederIntervalSeconds`、`PurgeIntervalMinutes`。`MaxQueuedJobs` 是补投的硬闸门：积压超过它就不再入队，避免异常循环把库撑爆。

**这一步删掉了什么**：`Documents/DocumentProcessingWorker.cs`（单线程循环）已删除。资料解析现在完全由任务系统驱动，`WorkerModule` 只保留索引生命周期与分身两条后台循环。

### 9.7 插件打磨：排空升级、定时同步与受信任公钥（阶段 8）

阶段 7 解决了"谁来做长任务"，但插件的三个现实问题还没答案：**升级时正在跑的调用怎么办**、**插件说"过一会儿再来同步"由谁兑现**、**谁的签名算数**。阶段 8 把这三件事补齐，并顺手把插件的安装改成两段式。

| 组件 | 位置 | 职责 |
| --- | --- | --- |
| 排空与在途计数 | `Plugins/PluginProcessHost.cs` | `BeginDrain/EndDrain/DrainAsync`、`InFlightInvocations`、排空期拒绝新调用 |
| 两段式安装 | `Plugins/PluginInstaller.cs` | `StageAsync`（只读校验）→ `InstallFromDirectoryAsync`（落盘切换） |
| 生命周期编排 | `Plugins/PluginManager.cs` | 升级＝排空 → 停旧进程 → 落盘 → 启动；`DrainAsync` 对外暴露 |
| 同步用例 | `Plugins/PluginSyncService.cs` | 调插件 → 校验批次 → 幂等摄取 → 写游标 → 记审计（三个入口共用一份逻辑） |
| 同步任务 | `Plugins/PluginSyncJobHandler.cs` | `plugin.sync` 处理器 + 补投器（读 `plugin_sync_states`） |
| 受信任公钥 | `Application/Plugins/IPluginTrustedKeyStore.cs`、`Infrastructure/Persistence/SqlSugarPluginTrustedKeyStore.cs`、`Plugins/PluginTrustedKeyRegistry.cs` | 公钥持久化端口、SqlSugar 实现、配置+库合并视图 |
| 公钥校验 | `PluginTrustedKeys`（同端口文件） | `TryNormalize`（拒绝私钥/坏算法/坏 PEM）、指纹、前缀匹配 |

**升级必然是"换进程"，所以必须先排空**。Windows 上运行中的 exe 删不掉，插件升级只能停旧进程再换文件。但如果直接 `Kill`，正在跑的那次同步/工具调用对调用方就是一次无解释的"连接已断开"。排空的语义是：**不再接受新调用（立即 503），已经在跑的调用等它自己做完**。因此 `PluginProcessHost` 里维护 `_inFlight` 计数器，`InvokeAsync` 只在拿到并发令牌后自增、`finally` 自减；排空态下新调用直接抛 `ErrorUnhealthy`——**快速失败而不是排队**，否则调用方会先卡住、再收到断开。

**超时不允许把升级永久堵死**。`DrainAsync` 最多等 `Plugins:DrainTimeoutSeconds`（默认 15 秒），超时就记警告并按原计划强停：一次卡死的调用不应该让插件永远升不了级。返回值 `false` 被写入审计，运维能看到"这次升级是强停完成的"。

**排空状态必须能挡住"顺手重启"**。管理器的 `EnsureStartedAsync` 在调用前会把"没在跑"的插件拉起来（崩溃自愈），而 `Draining` 恰好属于"没在跑"——如果不特判，一次排空期间到达的调用会触发重启，把刚腾出来的进程又拉回来：文件重新被占用，升级必然失败，排空也就名存实亡。因此 `Draining` 在这里直接返回 503。

**文件占用要容忍**。子进程刚退出时 Windows 仍会短暂占用版本目录里的 dll，直接 `Directory.Move` 会 `Access denied`、直接 `File.Copy` 会 `being used by another process`。安装器对移动重试 4 次、对复制重试 3 次；仍挪不动就退化为"原地覆盖"并记一条警告——宁可多留几个旧文件，也不要让一次版本切换永久失败。

**安装改成两段式，是为了"坏包不能把好插件停掉"**。`StageAsync` 只做解压 + manifest/入口校验，落在 `.staging/` 下，不碰任何生效目录；校验通过后才知道要不要走升级路径。顺序因此变成：`Stage`（可能失败，此时插件仍在正常服务）→ 若同 ID 已加载 → `StopHostAsync`（排空 + 停进程）→ `InstallFromDirectoryAsync`（落盘 + 切 `active.json` 指针）→ `ReloadAndStartAsync`。审计里这条 install 记录带 `(drain upgrade)` 后缀，便于区分首装与升级。

**定时同步的唯一来源是游标表**。插件在批次里返回 `nextSyncAfterSeconds` 表示"过一会儿再来"。核心把这个值写进 `plugin_sync_states.next_sync_after_seconds`，`PluginSyncJobFeeder` 每轮扫描"有自动同步间隔"的行，到点了才入队，幂等键 `plugin.sync:{pluginId}|{capabilityId}|{knowledgeBaseId}`。三条规则让它不会失控：

- **间隔夹最小值**：插件写错单位（返回 1）会被夹到 `max(30, Plugins:MinScheduledSyncIntervalSeconds)`，否则队列会被瞬间刷爆。
- **终态任务按间隔重新武装**：任务结束后必须再过了一个间隔才会被 `RetryAsync` 复活；一直失败的插件只按它自己声明的节奏重试。
- **停用的插件不参与**：`IPluginRuntime.IsScheduledSyncEligible` 只对"已启动且未被用户停用"的插件返回 true。

**为什么抽了 `IPluginRuntime`**：同步用例需要"调插件"与"这插件该不该定时同步"两件事，而 `PluginManager` 带着进程、生命周期门、安装器一堆依赖。抽一个两方法的端口出来，同步用例就能在单测里用假实现跑完所有分支（批次超限、结果不是批次、权限拒绝 vs 进程不健康），这也是本阶段新增 38 个单测几乎不需要真实进程的原因。

**同步失败不动游标**。游标只有在"批次被接受并写入"后才前进；失败时写 `last_status = failed` 并保留旧游标。否则一次瞬时故障会让整库重放。反过来，**部分条目失败仍然推进游标**：失败项的内容哈希没被更新，下次同步会重新判定；卡住不动的话，一片坏数据会让整条同步永久停摆。

**受信任公钥放数据库，配置文件优先**。`appsettings` 在桌面应用里改不了（要重启且用户看不到），所以"运行期信任一个新发布者"必须落库：`plugin_trusted_keys` 表 + `PUT /api/plugins/trusted-keys/{keyId}`。两边在 `PluginTrustedKeyRegistry` 里合并，**配置项优先**（运维用配置文件可以压过库里的临时授权），库里这一行用 `config:` 前缀的 KeyId 表示不可删除。写入时严格校验：只接受 `ecdsa`/`rsa`，必须能被 `ImportFromPem` 解析，**含 `PRIVATE KEY` 的 PEM 直接拒绝**——谁能写库谁就能自签，签名就失去意义了。落库后立刻 `ReloadAsync`，新信任的发布者马上生效，不需要重启。

**指纹不是安全边界**：`指纹 = SHA-256(规范化 PEM) 前 16 位`，只是给人核对"库里这条和发布者公布的是不是同一把钥匙"用的。

**新增端点**：`POST /api/plugins/{id}/drain`、`POST /api/plugins/{id}/sync-async`、`GET|PUT|DELETE /api/plugins/trusted-keys[/{keyId}]`；`GET /api/plugins/{id}` 增加 `inFlightInvocations`、`draining`、`trustedKey`。

**新增配置**（`Plugins` 节）：`DrainTimeoutSeconds`（0–600）、`ScheduledSyncEnabled`、`MinScheduledSyncIntervalSeconds`（30–86400）。

### 9.8 桌面交付：Electron 安装包（阶段 10）

阶段 9 删掉了 Photino 壳与 Velopack。桌面端从"能跑"到"能交付"只差打包，本阶段补上：

**打包只做一件事：把已发布的 API 原样塞进安装包**。`electron-builder.yml` 里 `files` 只含 `main.js`/`preload.js`/`package.json`，后端通过 `extraResources` 从 `../publish/desktop` 映射到 `resources\api`——**不能打进 asar**，asar 内的 exe 无法作为子进程启动。`main.js` 本来就按 `process.resourcesPath/api` 找 exe，所以代码零改动。产物 `MyFirstMafAgent-<version>-setup.exe` 约 310 MB，大头是自包含 .NET 运行时与 pg0；NSIS 再用 LZMA 压到同一体积量级，因此首次安装需要十几秒。

**卸载钩子曾经是"有实现、没调用方"的死代码**。`PortablePostgresHost.TryStopForUninstall` 从 Velopack 时代就存在，删工程时唯一的调用点（`OnBeforeUninstallFastCallback`）一起没了，于是变成"文档说要接、代码没人调"。本阶段的接法刻意做得极薄：给 API 加一个一次性参数 `--stop-portable-postgres`，在 `Program.cs` **构建 Host 之前**处理（只调 `TryStopForUninstall(AppContext.BaseDirectory)` 然后 `return 0`），NSIS 的 `customUnInstall` 用 `nsExec::ExecToLog` 调它。这样安装器不需要知道 pg0 的实例名/数据目录/连接串，**这些全部仍由 appsettings 决定**，避免两处配置漂移；用 `nsExec` 而不是 `ExecWait`，是为了卸载向导里不闪控制台窗口。

**顺序很重要**：`customUnInstall` 在 electron-builder 的 `un.install` 段里排在"删除已安装文件"之前（`uninstaller.nsh` 的 `!insertmacro customUnInstall` 紧跟 `setLinkVars`）。electron-updater 的升级也是先静默跑旧版卸载器，所以升级路径自动覆盖。实测：强杀 API 与 Electron 留下 10 个 `postgres.exe`，静默卸载后归零、安装目录删干净、`%LOCALAPPDATA%\MyFirstMafAgentData` 完好。

**"装不上"的两个环境坑（都不是安装包的问题）**：

- Windows「受控文件夹访问」会把 `桌面`/`文档`/`下载` 里的未签名程序拦下。NSIS 只回一个退出码 2、没有任何输出，`electron-builder` 的报错也只是 "process failed 2"。现象很迷惑：同样的安装包放到 `C:\Temp` 就能装。
- NSIS 阶段要**执行**一个临时安装器来生成卸载器；所以构建目录同样不能在受保护目录。`scripts\Build-ElectronInstaller.ps1` 检测到这种情况就改在 `%TEMP%\maf-electron-build` 构建，再把 `*-setup.exe` 复制回 `publish\releases`（复制不受限制）。

**用户数据路径刻意保持不变**：`productName` 只写在 `electron-builder.yml` 里，没有写进 `package.json`，因此渲染进程的 `app.getPath('userData')` 仍是 `%APPDATA%\myfirstmafagent-desktop`，日志与 `api-ready.json` 的位置不会因为打包而漂移。真正的业务数据本来就在 `%LOCALAPPDATA%\MyFirstMafAgentData`，安装器用 `deleteAppDataOnUninstall: false` 显式固定不删。

### 9.9 平台抽象与多形态交付（阶段 11）

后端真正与平台绑定的只有**三处原生依赖**，其余（Kestrel、SqlSugar/Npgsql、ONNX、Whisper 托管层）本来就是跨平台的（.NET 原生库在 `runtimes/` 下按 RID 分发）：

| 绑定 | 旧写法 | 现写法 |
| --- | --- | --- |
| 便携 PostgreSQL | `ExternalTools/pgSQL/pg0-windows-x86_64.exe` 写死 | `PlatformRuntime.ResolveTool` 按 RID 解析，`Pg0VendorFileName` 直接映射上游文件名 |
| FFmpeg | `Media:FfmpegPath` 默认 `ffmpeg.exe` | 默认空 → 按 RID 找，最后回落 PATH |
| 插件入口 | 以 `.exe` 后缀判定"原生程序 vs 宿主命令" | `PluginEntryCommand.IsHostCommand`：不含路径分隔符、不是已知脚本扩展名、包内无同名文件 → 宿主命令 |

`Infrastructure/PlatformRuntime.cs` 是这套抽象的**唯一入口**，只做四件事：算 RID、列出候选路径、解析、补可执行位。

- **查找顺序（先命中先用）**：`<tool>/<rid>/<上游原名>` → `<tool>/<rid>/<工具名><后缀>` → `<tool>/<上游原名>` → `<tool>/<工具名>-<rid><后缀>` → `<tool>/<工具名><后缀>`。前两级是"多平台共存"，后三级是**向历史布局兼容**（老用户把 `ffmpeg.exe` 直接放根下，不能因为升级就找不到）。
- **"配置即覆盖，而不是配置即唯一"**：`PortablePostgres:ExecutablePath` / `Media:FfmpegPath` 显式填写且文件存在 → 用它；填写但不存在 → **回落自动定位并记日志**，不抛异常。这条规则的动机来自一个很具体的场景：把 Windows 的 appsettings 同步到 Linux 服务器后，写死的 `pg0-windows-x86_64.exe` 会让服务直接起不来，而实际上 `linux-x64/` 下就有可用的二进制。
- **可执行位**：`cp`/`unzip` 会丢 `+x`，而 `Process.Start` 在 Linux 上会直接 `Permission denied`。`EnsureExecutable` 在 Unix 上 `chmod 0755`，且**永不抛**（只读挂载时不至于让启动崩掉）。
- **失败要可操作**：找不到 pg0 时，错误信息列出平台、**全部 5 条候选路径**、上游文件该放哪里、下载地址，以及"改用外部数据库"的替代方案——而不是一句"找不到文件"。

**容器口径**：Electron 不进镜像（容器里没有渲染需求），API 以框架依赖方式跑（运行镜像已有 .NET 运行时），ffmpeg 用 `apt` 装而**不随镜像塞副本**——正好由"回落 PATH"这条规则支撑。容器里必须用命令行 `--urls http://0.0.0.0:5056`，因为 `appsettings.json` 的 `Urls` 优先级高于 `ASPNETCORE_URLS`，环境变量压不住。

**发布脚本的两个默认行为**（都被真实需求逼出来的）：默认 `-SelfContained $true`（目标机不装 .NET）；默认删掉**其它平台**的原生二进制（`-KeepForeignRids` 可关闭），否则一个 Linux 包会白白带着 Windows 的 exe。

### 9.10 自动更新（阶段 11）

`electron-updater` + `desktop-electron/updater.js`，其中 `createUpdater` 是一个**可注入依赖**的工厂（`app` / `autoUpdater` 都可从参数传入），这样才能在没有 Electron 的 Node 环境里断言安装顺序。

三处设计是刻意的：

1. **关掉 `autoInstallOnAppQuit`**。本应用的 `before-quit` 会 `preventDefault()` 再 `app.exit(0)`，那条退出路径**不会**执行安装器；留着自动安装只会造成"下载好了但永远不生效"。
2. **安装顺序固定为 `stopApi → markQuitting → quitAndInstall`**。安装器要覆盖 `resources/api`，而便携 `postgres.exe` 会锁住目录（§9.8 的卸载钩子解决的是同一个问题的另一面）。顺序在测试里被显式断言，且 `beforeInstall` 抛错时**绝不会**调到安装器。
3. **更新源三级覆盖**：`MAF_UPDATE_URL` → 用户数据目录 `update-feed.json` → 包内 `app-update.yml`。同一份安装包因此能服务 GitHub Releases、自建静态目录和离线内网发布点。

### 9.11 问答引用元数据（阶段 12）

引用要能**回答"这句话是从哪一段来的"**，而不只是"用了哪个文件"。整条链路有四个点必须对齐，缺一个引用就退化成装饰：

| 环节 | 位置 | 要点 |
| --- | --- | --- |
| 检索带出切片标识 | `PgVectorSql.BuildSearchSql` / `BuildIndexSearchSql` → `RetrievedChunk(ChunkId, ChunkIndex)` | 两条 SQL（新版索引 `kb_index_vectors`、旧版直查 `kb_chunks`）都投影 `c.id` / `c.chunk_index`；只给文件名等于放弃段落级定位 |
| 组装引用 | `KnowledgeChatJson.FromChunks` | `rank` 用检索顺序（1 起），相似度保留 4 位，`snippet` 压平换行并截断 240 字；片段是给前端悬浮预览用的，不落全文 |
| 落库 | `knowledge_chat_messages.citations` / `answer_meta`（text/JSON） | 引用与「检索上下文（indexId / 向量模型 / 降级提示）+ 工具调用审计 + 网页来源」一起写；旧库靠 `EnsureMessageColumns()` 显式补列，因为 `CodeFirst` 只在建表时生效 |
| 读回与导出 | `KnowledgeChatStore.ToMessage` → `GET /messages`、`KnowledgeChatExportFormatter` | 反序列化失败一律降级为空引用：一条脏 JSON 不能让整个会话打不开；导出件脱离应用后仍然能核对答案依据 |

**为什么用 JSON 文本列而不是独立表**：引用只被"随消息整体读取"这一种方式访问，没有按引用反查消息的需求；独立表会引入 join 与级联成本，而 JSON 列让一次消息查询就能渲染完整气泡。等到需要"某段资料被引用过多少次"这类统计时再拆表。

**当前边界**：`legacyIndex=true`（索引不可用回退旧版直查）只是一条提示，不会阻止回答——检索降级时仍然给用户答案和引用，同时把降级事实写进 `answer_meta` 并在界面上以提示条暴露。

### 9.12 混合检索与评估集（阶段 12）

向量检索擅长"意思相近"，但对**字面必须出现**的东西（编号、URL、报错码、罕见术语）并不可靠；全文检索正好相反。两条路都要，剩下的问题是**怎么融合**和**怎么证明融合有效**。

| 决策 | 选择 | 为什么 |
| --- | --- | --- |
| 中文怎么分词 | 应用侧二元组（`Domain/Knowledge/LexicalTokens.cs`），写进 `kb_chunks.lexical_tokens` | PostgreSQL 的 `simple` 解析器会把整段中文当一个词；pg0 的 PG 不带 `zhparser`/`pg_jieba`，装扩展就得要求用户装数据库插件 |
| 索引怎么建 | `CREATE INDEX … USING GIN (to_tsvector('simple', coalesce(lexical_tokens, '')))` 表达式索引 | 表达式索引让写入保持"普通的 SqlSugar 批量插入"，不需要为 `tsvector` 手拼 SQL；查询必须用完全相同的表达式才吃得到索引（有单测盯着） |
| 老数据怎么办 | `LexicalIndexWorker` 分批补 `lexical_tokens` | 分词只能在应用侧做，塞进启动流程会让大库启动卡几分钟；空转时只发一条 `LIMIT 1` |
| 怎么融合 | RRF，`Σ 1/(k+rank)`，k=60 | 余弦在 0–1、`ts_rank` 常见 0.0x，线性加权必须按语料重新调参；RRF 只看排名，免疫量纲 |
| 两路怎么跑 | **串行** | 一个请求上下文里 `SqlSugarScope` 共用一条连接，并发查询直接抛 `NpgsqlOperationInProgressException`（同 §9.6 的 Worker 隔离问题） |
| 怎么证明有效 | `scripts/Evaluate-Retrieval.ps1` + JSON fixture，输出 recall@K / MRR | 没有可复跑的基线，"优化"就只是感觉 |

**降级策略**：词法检索抛错（索引缺失、语法异常）时返回空结果 + `Warning`，回答继续走纯向量——检索的一路坏掉不该让问答整体失败。`Retrieval:HybridEnabled=false` 则是彻底回到 P2-1 之前的行为。

**基线怎么来的（T-3）**：`scripts/retrieval-eval/corpus/` 有 27 份刻意互相混淆的中文资料（插件运行时 / 索引 / 任务中心三簇，各 8 份 + 1 份只给模糊答案的总览），`corpus.json` 有 42 个用例分四类。用真实 bge-small-zh-v1.5、top-5 跑出来：vector `0.8571 / 0.7270`、lexical `0.9524 / 0.8460`、hybrid `0.9762 / 0.8492`。分类别看，vector 在「字面」类掉到 0.60、lexical 在「语义」类掉到 0.80——**两路各有各的塌陷面，这才是必须融合的实测依据**。同一份语料连跑两次数字一致。

同时暴露了 RRF 的一个真实短板：单路弱命中会被融合稀释。「领取任务的执行者多久必须汇报一次」lexical 排第 5 能召回、vector 零召回，融合后掉出 top-5。这类"只被一路低排名召回"的样本正是 reranker 该修的形状——D-4 就是冲它去的。

**重排（D-4）怎么落地**：`mode=rerank` 在融合之后加一层 `RerankScoring`（`Domain/Knowledge/RerankScoring.cs`，纯函数，无模型）。

| 决策 | 选择 | 为什么 |
| --- | --- | --- |
| 用什么信号重排 | `Score = (1-w)·Base + w·Coverage`，`w=0.65` | 实测**纯覆盖率排序比 hybrid 还差**（0.9524 / 0.7972）：覆盖率只看"问题的词出现过没有"，沾满虚词就能拿高分，会把 RRF 本来排对的结果挤下去。加回先验后反超 |
| 先验怎么算 | `Base = 0.5×融合名次 + 0.5×归一余弦` | 名次和余弦各有各的盲区；余弦的绝对值跨查询不可比（bge-small-zh 上相关/不相关往往只差 0.05），所以只在**候选窗口内**做 min-max 归一 |
| idf 从哪来 | 候选集自身的文档频率，`ln(1+N/(1+df))` | 不需要外部语料统计；「任务」「索引」这类到处都是的词自动降权，「汇报」「保留期」自动升权 |
| 通用词怎么办 | 文档频率 > 70% 候选的词元直接剔出分子分母 | 留着只会把分数压扁。但**候选少于 5 条时不剔**——那时任何词元的文档频率都高于 70%，会剔光全部词元、覆盖率恒为 0 |
| 候选池多深 | `RerankCandidateLimit`（默认 60）= **每一路**的召回条数 | 只在融合后放大没有用，融合只能从已召回的那几条里挑。20 条时 rerank 与 hybrid 打平，60 条才有 1.0000 / 0.9075；代价 p50 +5ms、p90 +35ms |
| 要不要 cross-encoder | **不接** | 仓库里只有 `bge-small-zh-v1.5`（embedding），没有任何 rerank 模型；没验证过的 ONNX 推理路径会在用户机器上以未知方式失败。算法抽在 `IChunkReranker` 后面，日后换注册即可 |
| 拒答怎么判 | 机制提供，**默认关闭** | 实测这份语料上"有答案但换了说法"与"真没答案"的得分完全重叠（有答案最低 0.361，无答案最高 0.474）；要拒掉 8/8 无答案必须误拒 15/42 有答案 |

**四档实测（同一 fixture，42 有答案 + 8 无答案，连跑两次一致）**：vector `0.8571 / 0.7270` → lexical `0.9524 / 0.8460` → hybrid `0.9762 / 0.8492` → rerank `1.0000 / 0.9075`。完整曲线与失败复盘见 `docs/ROADMAP.md` D-4。

**拒答为什么不靠分数阈值**：`Retrieval:AbstainEnabled` 默认 `false`。真正的"资料里没有"由提示词约束模型回答——`KnowledgeChatService.BuildReferenceNote` 在拒答时生成一句"不要用常识或推测补全，也不要编造引用"的提示，`ToolCallingAgent.BuildSystemPrompt` 把它拼在参考资料**之后**（放在前面会被资料淹没）。拒答判定本身仍然落库（`retrieval.abstained/topScore`），前端 `.citation-warning` 直接展示。

状态机（`idle/checking/available/downloading/downloaded/up-to-date/error/disabled`）通过 `desktop:update-status` 广播到渲染进程；`autoDownload=false` + 手动 `downloadUpdate()`，让"下载"和"安装"分成两个用户可见的动作。

### 9.13 笔记引用与知识库包（阶段 13）

笔记与资料的关系有两种，必须分开建模：

| 关系 | 存什么 | 为什么 |
| --- | --- | --- |
| 归属 | `kb_notes.topic_id` | 一篇笔记归一个专题，够用且好筛 |
| 依据 | `kb_notes.sources`（JSON 数组，`NoteSource`） | 一条结论可能同时依据多份资料的不同段落；只留一个 `source_document_id` 会把"从问答里长出来的笔记"压成"关于某个文件" |

**为什么引用要连切片序号一起存**：笔记是半年后读的东西。那时需要的不是"这篇笔记关于某个文件"，而是"这句话出自第 3 段"。因此 `sources` 里带 `chunkIndex`、当时的 `similarity` 与 `snippet`——即便资料被重新解析、切片序号变了，笔记里仍保留当时的依据，作者能自己判断要不要更新。

**为什么正文还要能写锚点（T-5）**：只有卡片上的引用 chip，说明的是"这篇笔记用过哪几段资料"，说明不了"这句话出自哪一段"。长笔记里要核对某一句的出处，只能自己从头找。所以正文支持 `[1]` / `[2,3]` 形式的锚点，序号就是 `sources` 的下标 +1：

| 决策 | 选择 | 为什么 |
| --- | --- | --- |
| 语法 | 只认方括号里的 1-2 位数字（可逗号分组） | `[2024](url)` 是链接、`[TODO]` 是标签，宽一点的语法一定会误判 |
| 悬空序号 | 渲染成红底提示，导出原样保留文字 | 静默丢掉等于让用户以为自己写对了；变成链接又点了没去处 |
| 导出 | 正文有锚点时输出 Markdown 脚注（`[^1]` + `[^1]: 文件（第 N 段）· 相关度 · 摘录`） | 阅读器里能点回定义；没有锚点的老笔记仍用编号列表，避免同一份稿子里两套编号 |
| 谁写锚点 | 用户手动写，或点面板上的"插入"在光标处补 `[n]` | 自动判断"这句话出自第几段"需要句子级对齐，是另一个量级的工作；先把手动的路铺好 |

**为什么 `Update` 的专题/引用参数用 `null` 表示"保持不变"**：清空与"更新时没传"是两件事。只有 `null` 表示后者，显式清空要传空集合 / `Guid.Empty`，否则界面上的"取消归类"无法表达（这条被单测钉住）。

**笔记筛选为什么必须下推到 SQL**：最初 `NoteService.ListAsync` 是"取出整个知识库的笔记 → 内存里 `Where`"。资料几百份、笔记几千篇时，每次打开专题面板都要把整库实体读进来再丢掉绝大部分，内存与耗时都随库大小线性增长。现在过滤条件被收进值对象 `Domain/Notes/NoteQuery`，`SqlSugarNoteRepository.ListAsync(NoteQuery, ct)` 直接翻成 `WHERE`：分类与关键词用 `ILIKE`（`%` / `_` / `\` 由 `EscapeLikePattern` 转义，避免用户输入的 `100%` 变成通配），资料过滤走冗余列 `source_document_ids`（`LIKE '%guid:N%'`），专题走强类型条件。

| 决策 | 选择 | 为什么 |
| --- | --- | --- |
| 资料过滤存冗余列 | `kb_notes.source_document_ids`（空格分隔的 `guid:N`），启动时按 `sources` 回填 | `sources` 是 JSON，`LIKE` 匹配 JSON 文本既慢又容易误中；冗余一列索引友好，回填只做一次（只更新空串行） |
| 关键词大小写 | `ILIKE`，不做 `lower()` 包装 | PG 上 `ILIKE` 能吃到 `pg_trgm` 索引（一旦加上），且不用改列排序规则 |
| 值对象而散参数 | `NoteQuery` 记录 + `Normalized*` / `Effective*` 属性 | 空白字符串、`Guid.Empty` 这类"等于没传"的边界在一处收口，仓库层不必重复判断；单测能直接钉住规范化行为 |

**知识库包：为什么不导出向量与切片**

| 决策 | 选择 | 为什么 |
| --- | --- | --- |
| 包里放什么 | `manifest.json` + 原始文件（`files/NNNN.ext`） | 向量绑定具体模型与版本（还有芯片差异），换机器几乎必然对不上；原始文件、笔记、专题才是用户真正拥有的东西 |
| 导入怎么做 | 新建知识库 + 资料重新入队解析 | 导入慢一点，但索引是按本机模型重新算的，检索结果才对得上；顺带复用既有的解析/索引链路，不写第二套 |
| ID 怎么办 | 全部重新生成，按包内映射重写笔记引用 | 避免与已有数据撞 ID；映射表只在一次导入的内存里存在 |
| 重名怎么办 | 自动让路：`名称（导入）`、`名称（导入 2）`… | 不让用户为了导入先改名或删库 |
| 完整性怎么保证 | 每个文件记 SHA-256，导入时逐条比对 | 包会被网盘、聊天软件转发，"文件坏了"必须能被发现并跳过，而不是塞进库里变成解析失败 |
| 为什么不合并进已有库 | 一期不做 | 合并要处理同名资料 / 专题 / 笔记的冲突策略，每一种都需要用户决策；先把备份迁移与分享打通 |

**分层落地**：包功能是应用服务（`Application/KnowledgePackages/`），但它需要"列资料、建资料、写摘要"这些数据动作。为此新增领域端口 `IKnowledgePackageStore`，由 `Persistence/KnowledgePackageStore` 收窄 `KnowledgeBaseStore` 实现——应用层不认识持久化类型，分层检查保持 0 违规。

**导入失败怎么办：补偿回滚而不是事务**

文件系统写入无法进数据库事务，所以导入走"记日志 + 逆序补偿"：`KnowledgePackageImporter` 在 `try` 里维护三本账（新建专题 ID、新建笔记 ID、新建资料 ID + 落盘路径），任何异常都进同一个 `catch`，按 **笔记 → 专题 → 资料（含落盘文件）→ 知识库** 逆序删除，然后把"已回滚写入的 N 份资料 / N 篇笔记 / N 个专题（原因：…）"塞进 `KnowledgePackageException` 返回给前端。补偿本身用 `CancellationToken.None`——请求已经取消了，但清理必须做完；单条补偿失败只记 `LogError`，不影响其余补偿。

两个从这条链路上长出来的真实修复：

| 问题 | 现象 | 修复 |
| --- | --- | --- |
| 包条目名分隔符不一致 | Windows 自带压缩 / `ZipFile.CreateFromDirectory` 写的条目名是 `files\0001.md`，清单里写的是 `files/0001.md`，`TryGetValue` 永远落空 → 导入 0 份资料却返回成功 | `KnowledgePackageReader.NormalizeEntryPath`（反斜杠→斜杠、去 `./` 与前导 `/`）+ `BuildEntryIndex` 归一化建索引，查找一律走索引 |
| 零内容导入成功 | 上一条的后果：库里多出一个空知识库，用户以为导入成功 | 导入结束时 `createdDocumentCount == 0 && createdNotes == 0` 直接抛 `KnowledgePackageException`，让补偿逻辑把壳子删掉；错误信息带上第一条 warning（通常是"包内缺少文件 …"） |

**为什么不做成真正的两阶段提交**：导入的产物分散在 PG（元数据）、向量表（后续异步写入）和 `App_Data/uploads`（文件）三处，任何一处都可能因为磁盘满、进程被杀而停在中途——那已经不是代码能补偿的范畴（下次启动的清理任务才管得了）。补偿回滚的目标是"用户看到明确的失败，且库里不留半成品"，不是"任何时刻断电都一致"。

**诚实的边界**：不导出对话历史（`sourceSessionId` 导入后清空）；"分享到市场"目前只是"把 zip 发出去"，托管与一键安装属于 `docs/ROADMAP.md` 的 D-3。另外 `DELETE /api/knowledge-bases/{id}` 目前不存在（只能删资料与笔记），清理验证用的一次性库只能重建数据库。

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

- **服务端**：`ModelContextProtocol.AspNetCore` 以 Streamable HTTP（`Stateless = true`）在 `Mcp:ServerPath`（默认 `/mcp`）暴露 `KnowledgeMcpTools`，其他大模型或智能体可以通过 MCP 读取本机知识库、摘要与工作目录文件。工具包括 `list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`get_document_content`、`get_document_summary`、`get_document_status`、`upload_document`、`workspace_list_files`、`workspace_read_file`、`workspace_write_file`。
- **客户端**：`McpClientRegistry` 按 `Mcp:Servers` 惰性连接外部服务器（`http` 或 `stdio`），工具列表缓存 30 秒，连接失败只记录状态并暴露在 `GET /api/mcp/status`。

### 对外开放：客户端密钥、能力与作用域（阶段 6）

把本项目当作"所有 Agent 工具的核心存储层"之后，`/mcp` 就是对外数据面。这里有一条必须澄清的边界：**监听回环地址不等于鉴权**，同机任何进程都能访问回环端口。因此原始设计（`/mcp` 与 `/api` 共用桌面一次性令牌）既挡不住本机进程，又因为令牌按设计不下发、不落盘，导致外部 Agent **永远无法接入**。

阶段 6 把数据面拆成两条独立的信任路径：

| 面 | 使用者 | 凭据 | 生命周期 |
| --- | --- | --- | --- |
| `/api` | 桌面窗口内的前端 | 桌面一次性令牌（`X-Api-Token`） | 每次启动重新生成，进程退出即失效 |
| `/mcp` | 外部智能体（Cursor、Claude Desktop、自研 Agent） | 客户端密钥（`Authorization: Bearer` 或 `X-Api-Key`） | 长期有效，可轮换、可停用、可删除 |

**闸门顺序固定**：认证（401）→ 启停（403）→ 限流（429 + `Retry-After`）→ 工具准入（200 + JSON-RPC 错误）→ 审计（旁路写入）。

- **能力词表**：`knowledge:read`、`knowledge:write`、`workspace:read|write`、`files:read|write`、`avatar:read|write`、`chat:read`，外加通配 `*` 与按作用域通配（`knowledge:*`）。用「能力:读/写」而不是单一"是否只读"布尔：外部 Agent 常常只需要知识库，没有任何理由读本机任意授权目录。其中 `chat:read` 刻意不复用 `knowledge:read`——私人对话比资料更敏感，只拿到知识库权限的客户端不应顺带看见。
- **工具清单即权限边界**：`McpToolCatalog` 把每个工具映射到「所需能力 + 是否写操作 + 是否作用于知识库」，**清单之外的工具默认拒绝**。新增工具忘记登记时是"不可用"，而不是"对所有人开放"。
- **作用域三级收窄**：能力（能做什么）→ 工具白名单（能做哪些具体工具）→ 知识库白名单（能动哪些数据）。省略 `knowledgeBaseId` 时按默认知识库判定，因此"不传参数"不会绕过范围；`get_document_content` 只带资料 ID，必须回查资料所属知识库再判定。
- **拒绝用 200 + JSON-RPC 错误**（`-32003`）：工具级拒绝要让模型看到可读原因并自行改道，而不是变成传输层错误。
- **密钥只存摘要**：256 位随机值的 SHA-256 摘要 + 12 字符展示前缀；明文只在创建/轮换的响应里出现一次。轮换立即让旧密钥失效，更新作用域不会让已接入的客户端掉线。
- **限流是令牌桶而非固定窗口**：额度存在数据库、可随时改，桶在额度变化时重新装满；能给出准确的 `Retry-After`。计数只放内存——限流是保护手段，不是计费依据。
- **审计表 `agent_client_calls`**：记录"谁、调了什么工具、打到哪个知识库、结果、耗时"，**不含请求正文**（避免把用户资料复制进审计表）。删除客户端不删历史。
- **写能力只有一条落地路径**：`upload_document` 把文本包成一次普通上传，复用解析/分块/内容版本/Outbox/索引生命周期，与插件摄取完全同源。

**仍未实现**：OAuth 授权码流（当前是长期密钥）、按客户端的内容级 ACL（只有知识库级）、MCP 删除工具、调用配额与计费。

**明确没做的一件事**：`tools/list` 不按客户端能力裁剪，受限客户端会「看得见但调不通」。裁剪必须在响应体重写上做（MCP SDK 的 `ToolCollection` 是单例，不支持按请求过滤），而这会动到 MCP 传输层，风险高于收益。因此权限判定的唯一入口仍然是 `tools/call` 的闸门，接第三方 Agent 时 `-32003` 应当作「换个工具再做」的正常分支处理。

### 聊天历史回读（`chat:read`）

外部 Agent 要接上「上次聊到哪」，光有检索是不够的——检索读的是资料与记忆，读不到**原话**。因此单独开一条只读路径：

| 层 | 类型 | 职责 |
| --- | --- | --- |
| Domain | `Domain/Chats/ChatHistory.cs`（`ChatSessionSummary` / `ChatMessageHit` / `IChatHistoryStore`） | 定义读端口与来源名（`knowledge` / `avatar` / `companion` / `companion-import`） |
| Application | `Application/Chats/ChatHistoryService.cs` | 来源名规范化、条数夹紧、把「参数写错」变成可读错误 |
| Infrastructure | `Infrastructure/Persistence/SqlSugarChatHistoryStore.cs` | 一条 `UNION ALL` 合并三张会话表与一张导入记录表 |
| MCP | `Mcp/ChatHistoryMcpTools.cs` | `list_chat_sessions`、`chat_search_history` |

两个刻意的取舍：

1. **来源名写错直接报错**，不静默返回空列表——空列表会被调用方理解成「用户没聊过」，而真实原因只是参数拼错。
2. **LIKE 元字符先转义**：不转义时搜 `%` 会把全部历史捞出来、搜 `_` 会变成单字符通配，两种都是「看起来能用、结果莫名其妙」的坑。

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

**异步整理（`avatar_ingestions` 队列）**

1. 投喂只做「落碎片 + 建任务（`queued`）」就返回，长文本不会阻塞问答。`AvatarIngestionWorker` 用 `ClaimNextIngestionAsync` 原子领取（`UPDATE ... WHERE id=@id AND status='queued'`，抢不到就说明别人领走了）。
2. 单条任务有 **5 分钟上限**：超时由独立取消令牌打断，然后用 `CancellationToken.None` 把状态写成 `failed`。这一点很关键——用已取消的令牌写状态，行会重新变回 `running` 坟头，队列就此堵死。
3. 空闲时每 60 秒扫一次「僵尸 `running`」（超过 10 分钟）重新排队，覆盖进程崩溃、状态回写前被打断等场景。启动时也扫一次。
4. 模型返回的内容解析不出记忆时**降级为原文记忆**（`partial` + 提示可重试），而不是丢掉用户投喂的原文——原文已经在 `avatar_fragments` 里，重试不会重复。


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
