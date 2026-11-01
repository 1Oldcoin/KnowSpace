# 项目开发规范（所有贡献者必须遵守）

本文件适用于整个仓库，包括人工开发者、AI 编程助手和自动化代理。修改前必须阅读；新代码及本次修改涉及的代码必须符合本规范。不得以历史代码存在例外为理由新增违规实现，也不得借遵守规范之名无关地重构整个仓库。

## 1. 规范效力与工作流程

- 本文件是仓库级开发约定的统一入口。子目录规范只能细化职责和要求，不得自行放宽安全、数据保护与兼容性约束。
- 需求与本规范冲突时，先说明冲突并取得维护者确认；架构、协议或安全边界调整必须同时更新实现、测试和相关文档，不能静默绕过规范。
- 修改前检查工作区已有改动，定位实际控制行为的代码和邻近测试。不得覆盖、回退或删除他人的未提交工作。
- 每次改动应范围明确、能够验证；禁止顺手批量改名、格式化无关文件、升级无关依赖或迁移历史模块。
- 未经明确要求，不创建提交、不推送、不强制改写 Git 历史，不执行破坏性清理。
- 完成时必须说明改了什么、执行了哪些验证、验证结果及未验证项。无法运行测试必须说明阻碍，不能把“编译通过”写成“测试通过”。
- 本文件定义必须遵守的交付标准，不代表所有规则都已有自动检查。代码审查必须核对第 10 节；自动化脚本只覆盖其实际检查范围，不得宣称 CI 已强制执行全部规则。

## 2. 当前架构与目录归属

产品为“知间-本地知识库”。当前采用 **ASP.NET Core Minimal API 模块化单体 + Vue 前端 + Electron 桌面壳 + 独立进程插件**，不是 MVC 管理后台或独立 Hub/Agent 服务集群。

| 路径 | 职责与放置规则 |
| --- | --- |
| [MyFirstMafAgent.Api](MyFirstMafAgent.Api/) | 后端宿主与业务模块；程序集产物名为 `Zhijian.Api`，项目目录和命名空间保持现有名称 |
| [Program.cs](MyFirstMafAgent.Api/Program.cs)、[ApiHost.cs](MyFirstMafAgent.Api/ApiHost.cs)、[Hosting](MyFirstMafAgent.Api/Hosting/) | 程序入口、组合根、DI 注册、启动初始化、中间件及协议接入；不得堆积业务计算和数据库查询 |
| [Endpoints](MyFirstMafAgent.Api/Endpoints/) | 按业务组织 Minimal API，处理 HTTP 参数、校验、状态码及应用服务调用 |
| [Application](MyFirstMafAgent.Api/Application/) | 用例编排、事务边界、跨领域流程及后台任务协调 |
| [Domain](MyFirstMafAgent.Api/Domain/) | 领域实体、值对象、业务规则与仓储等端口；不依赖 Web 或数据库框架 |
| [Infrastructure](MyFirstMafAgent.Api/Infrastructure/) | 仓储、数据库、文件存储、外部进程等基础设施实现 |
| [Persistence](MyFirstMafAgent.Api/Persistence/) | 现有数据库映射实体及兼容存储实现；不等同于纯领域模型 |
| [Contracts](MyFirstMafAgent.Api/Contracts/)、[Options](MyFirstMafAgent.Api/Options/) | 请求／响应契约及强类型配置 |
| [Agents](MyFirstMafAgent.Api/Agents/)、[Models](MyFirstMafAgent.Api/Models/) | AI Agent、模型访问及相关能力；具体归属沿用邻近实现 |
| [Documents](MyFirstMafAgent.Api/Documents/)、[Chats](MyFirstMafAgent.Api/Chats/)、[Companions](MyFirstMafAgent.Api/Companions/) | 现有文档、问答和伴侣业务；保留兼容入口，不借小需求强行搬迁 |
| [Mcp](MyFirstMafAgent.Api/Mcp/)、[OAuth](MyFirstMafAgent.Api/OAuth/) | MCP 工具与 OAuth 协议实现；宿主接入仍由 Hosting 负责 |
| [Plugins](MyFirstMafAgent.Api/Plugins/) | 插件发现、子进程通信、权限和生命周期管理 |
| [MyFirstMafAgent.Plugins.Sdk](MyFirstMafAgent.Plugins.Sdk/) | 独立插件协议与开发 SDK；不得引用 API 项目或引入 ASP.NET、ORM 等宿主依赖 |
| [MyFirstMafAgent.Api.Tests](MyFirstMafAgent.Api.Tests/) | xUnit 后端回归测试，优先扩展已有对应测试文件 |
| [FrontEnd/myfist-agent](FrontEnd/myfist-agent/) | Vue 3 + TypeScript + Vite 前端源码；保留现有目录拼写 |
| [desktop-electron](desktop-electron/) | Electron 主进程、preload、更新器、安装包配置与桌面测试 |
| [scripts](scripts/)、[deploy/linux](deploy/linux/) | 构建、验证、发布脚本与 Linux 部署配置 |
| [docs](docs/)、[samples](samples/) | 专项设计、操作手册与插件示例 |

`bin/`、`obj/`、`dist/`、`publish/` 和后端 `wwwroot` 中的前端构建产物不是业务源码入口。`App_Data/` 可能包含真实数据库、上传文件和 OAuth 私钥，绝不是可随意清理的构建缓存。

### 分层依赖

```text
Endpoints -> Application -> Domain
Infrastructure -> Domain / Application 中定义的端口
Hosting / ApiHost -> 注册并组装上述实现
API -> Plugins.Sdk（SDK 不得反向引用 API）
```

- 新增业务规则放 Domain，用例编排放 Application，持久化实现放 Infrastructure。端点不得直接使用 SqlSugar 或拼 SQL。
- Domain 不得依赖 Application、Infrastructure、Persistence、Endpoints、ASP.NET 或 SqlSugar；Application 不得直接依赖 Infrastructure、Persistence、Endpoints 或 SqlSugar。
- 当前仍有旧业务模块直接使用兼容存储，不能据此宣称全仓已经完成严格分层。修改旧入口时保持契约，跨层迁移必须单独说明范围并补回归测试。
- 参考现有路径：[StudyEndpoints.cs](MyFirstMafAgent.Api/Endpoints/StudyEndpoints.cs) → [StudyService.cs](MyFirstMafAgent.Api/Application/Study/StudyService.cs) → [StudyRecord.cs](MyFirstMafAgent.Api/Domain/Study/StudyRecord.cs)，仓储实现见 [SqlSugarStudyRepository.cs](MyFirstMafAgent.Api/Infrastructure/Persistence/SqlSugarStudyRepository.cs)。

## 3. 技术栈与依赖管理

- 所有 .NET 项目保持 `net10.0`，启用 Nullable 与 ImplicitUsings，不降级框架。不把某台机器的 SDK 补丁版本写成全员前置条件。
- 数据库为 **PostgreSQL + pgvector**，通过 SqlSugar／Npgsql 访问；便携模式当前要求 PostgreSQL 17。不得宣称默认 SQLite 或只改连接串即可任意切换数据库。
- AI 能力沿用 Microsoft Agents AI、Microsoft.Extensions.AI、OpenAI 兼容访问及现有 ONNX／文档／媒体处理组件；不得另起一套平行调用链。
- 前端及桌面依赖以各自项目清单为准。前端当前 Node 要求为 `^22.18.0 || >=24.12.0`；更改工具链时同步配置、锁文件和说明。
- NuGet 版本以 [API 项目文件](MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj)、[SDK 项目文件](MyFirstMafAgent.Plugins.Sdk/MyFirstMafAgent.Plugins.Sdk.csproj)、[测试项目文件](MyFirstMafAgent.Api.Tests/MyFirstMafAgent.Api.Tests.csproj) 为准，使用精确版本，不使用浮动版本或范围。本文不维护另一张会漂移的版本表。
- 新增依赖前先确认现有实现不能满足需求，再检查维护状态、许可证、平台兼容性和传递漏洞；优先官方或已有依赖，不为轻量封装引入整套框架。
- npm 依赖变更必须同步已有锁文件；NuGet 依赖变更必须运行依赖检查。新增技术栈或改变模块边界时同步本规范与架构文档。
- 使用机器／仓库实际配置的包源。代理仅是个人环境设置，不写入业务代码或强制所有贡献者使用某个本地端口；不得为下载关闭 TLS 证书校验。

## 4. C# 编码与注释

- 延续邻近代码的文件作用域命名空间、缩进和命名；类型及公共成员用 PascalCase，参数和局部变量用 camelCase，私有字段沿用当前类型风格。
- 使用项目已有 ImplicitUsings 和文件顶部显式 using，不为遵守规范批量创建 `GlobalUsings.cs`。确需全局 using 时只收纳稳定公共命名空间，不用它隐藏跨层依赖，不使用 `global using static`。
- 异步方法以 `Async` 结尾，沿调用链传递 `CancellationToken`，取消时及时释放资源。优先沿用 `Task`／`Task<T>`；已有协议要求的签名与 `ValueTask` 不强改。
- 禁止在请求和任务路径中用 `.Result`、`.Wait()` 阻塞异步操作；禁止无归属的 fire-and-forget。长任务接入现有任务系统并明确失败、取消、重试和幂等语义。
- 不用 `null!`、禁用 Nullable 或大范围压制诊断掩盖空值问题；外部输入必须在边界验证。异常不能空吞，业务错误、取消和基础设施故障应分别处理。
- 新增业务日志使用 `ILogger<T>` 和结构化参数，不在业务代码里使用 `Console.WriteLine`。当前日志并非全仓 Serilog；保留启动诊断及插件协议必要的独立输出通道。
- 新增业务配置优先使用 Options 强类型；组合根的配置读取和已有动态设置服务保留各自职责，不强行改成启动时快照而破坏热更新行为。

### 注释要求

- 新增或修改的 `public` 类型、接口、方法、属性和字段必须有 `///` XML 文档；实现接口时可用 `/// <inheritdoc/>`。
- `summary` 说明用途，每个参数写 `param`，有返回值时写 `returns`；`exception` 只列主动抛出且调用方需要处理的异常。不写空标签或创建人／日期等 Git 已记录的信息。
- 新增或修改的 `private` 类型、方法、字段（含常量、静态只读字段）必须有简短用途注释；字段说明存什么及重要生命周期，方法说明做什么，不逐句翻译实现。
- 实现内注释只解释约束、边界条件或非显然的取舍，不写“调用方法”“变量加一”等流水账。历史缺失注释按本次涉及范围补齐，不批量污染无关文件。
- 待办统一为 `// TODO(<标签>): <具体问题及完成条件>`，标签使用 `安全`、`性能`、`重构`、`?` 或当前路线图已有阶段名。不得用未实现的 TODO 代替已要求交付的功能；安全缺口不能仅留 TODO 后交付。

## 5. API、MCP 与安全边界

- 新增 HTTP 接口沿用 Minimal API 和对应 Endpoints 模块，不引入 MVC Controller 平行组织接口。参数、错误响应和 OpenAPI 分组沿用邻近端点约定，不泄露内部堆栈。
- `/api` 与 `/mcp` 是不同认证边界：API 使用现有 `X-Api-Token` 闸门，MCP 接受客户端密钥或 OAuth。不得写成“所有入口统一 Bearer 登录”，更不得在新增路由绕过已有闸门。
- 保留 [HostPipeline.cs](MyFirstMafAgent.Api/Hosting/HostPipeline.cs)、[DesktopSecurity.cs](MyFirstMafAgent.Api/Hosting/DesktopSecurity.cs)、[SecurityPostureGuard.cs](MyFirstMafAgent.Api/Hosting/SecurityPostureGuard.cs) 的安全职责；Server 模式不得放宽启动安全校验、可信代理或 OAuth/CIMD 约束。
- 当前 [McpModule.cs](MyFirstMafAgent.Api/Hosting/McpModule.cs) 使用 Streamable HTTP 且 `Stateless = true`。不得仅因外部示例改为 stateful；需要改变时必须先确认会话、部署和 SDK 兼容性并补协议回归。
- MCP 调用必须经过 [AgentClientGate.cs](MyFirstMafAgent.Api/Hosting/AgentClientGate.cs) 的能力、工具白名单、知识库范围、限流与审计检查，授权失败默认拒绝。现有发现请求豁免不代表工具调用可匿名。
- Server 模式不得注册本地文件工具。数据读取和写入均需校验资源归属，不能只依靠前端隐藏按钮；修改授权边界必须覆盖跨知识库、私人来源和禁用客户端等拒绝路径。
- 访问令牌、API Key、OAuth 私钥、原始私人聊天内容不得进入日志、异常响应、提交、截图或测试快照。安全审计使用现有持久化审计通道，普通诊断日志不能替代审计。
- 新增对外 URL 访问、上传、下载和解压必须处理 SSRF、大小限制、路径穿越及超时；不得以“本地应用”为由跳过安全验证。

## 6. 数据、文件与后台任务

- 数据库查询参数化；动态列名、排序等使用固定白名单。查询必须限制范围、分页或批量大小，避免一次读取完整正文／向量大表及 N+1 查询。
- 数据结构变更同时检查 SqlSugar 映射、手写 DDL、初始化流程及现有数据升级。不得通过删除数据库让测试通过；保留现有 schema 初始化与指纹机制。
- 知识库切片、伴侣私人历史及不同 Embedding 模型／维度必须遵守现有索引隔离，不能混入同一检索空间。问答来源要可追溯，缺少证据不得伪造引用。
- 新任务复用现有持久化调度、取消、配额与恢复机制，见 [docs/JOBS.md](docs/JOBS.md)。新增持续增长的任务／审计／事件数据需明确保留与清理策略。
- 文件操作复用现有存储接口和受控路径逻辑；基础设施层可按现状使用 `System.IO`，不得假定仓库已采用 `IFileSystem` 或为此强制引入新依赖。
- 不直接把用户文件名、相对路径或压缩包条目拼成落盘路径；验证规范化后的目标仍在授权根目录内，并考虑符号链接／重解析点逃逸。
- 临时产物使用当前模块已有的临时目录／数据根策略，按任务隔离并清理自己创建的内容；可验证完整性的下载物必须核验哈希或签名。不再使用无关项目的固定目录名。
- 外部进程复用已有封装，使用参数列表而非拼接不可信 shell 字符串；处理超时、取消、退出码、输出及进程回收。不能把“所有命令已有 PowerShell AST 检查”当作事实。
- `App_Data`、`pgdata`、`uploads`、OAuth 密钥和用户设置必须保留；涉及迁移、卸载、发布或清理时先确认数据范围，禁止对这些目录执行无差别 `git clean` 或递归删除。

## 7. 插件协议与生命周期

- 插件采用 **独立子进程 + JSON-RPC over stdio**，不是程序集热加载。沿用 [插件 SDK](MyFirstMafAgent.Plugins.Sdk/) 和现有清单契约；禁止照搬其他项目的插件接口或 `AssemblyLoadContext` 架构。
- stdout 保持协议专用，普通日志不得污染协议流；新增消息和能力必须兼顾旧插件版本及错误处理。
- 生命周期统一经 [PluginManager.cs](MyFirstMafAgent.Api/Plugins/PluginManager.cs) 协调，禁止直接替换运行中的文件或旁路启动进程。
- 当前支持运行中重扫、启停、配置／权限变更、安装升级、排空、回滚和卸载，但不保证零中断；重扫不等于自动重启已运行插件，配置／权限更新可能需要重启插件。
- 保留已有 SHA-256 和发布者签名策略，不能宣称“不支持签名”或“始终强制签名”，不能为调试静默降低策略。
- 工具权限、知识库范围、网络／文件访问限制和宿主能力调用必须沿用既有校验。破坏性协议变更须明确版本兼容策略，并同步 SDK、宿主、示例和契约测试。
- 实施前按需阅读 [插件开发手册](docs/PLUGIN-DEVELOPER-MANUAL.md) 和 [插件生命周期](docs/PLUGIN-LIFECYCLE.md)。

## 8. 前端与桌面

- 前端使用 Vue 3、TypeScript、Vite 和现有组件／样式体系；不得引入 React、Bootstrap 管理后台或另一套状态／路由方案替代现状。
- HTTP 访问复用 [services/api.ts](FrontEnd/myfist-agent/src/services/api.ts)，保持同源、可配置地址及桌面运行兼容；禁止在组件散落固定主机和端口。
- 类型明确，不用随意的 `any` 或类型断言掩盖接口不一致。API 契约变更同步前端类型、调用方、加载／空／错误状态。
- 对话框复用 [services/dialog.ts](FrontEnd/myfist-agent/src/services/dialog.ts) 与现有 `AppDialogHost`，禁止新增 `window.prompt`／`window.confirm`。
- 图标沿用 Lucide；交互必须在模板中实际呈现，不能只写脚本和 CSS。新增事件监听、定时器、流式连接在卸载／切换时释放，处理过期响应和取消。
- UI 变更需检查桌面与窄屏下布局、键盘访问、焦点和真实操作；不能把 TypeScript 编译成功视为界面验收。前端源码构建后才同步产物，禁止只改 `wwwroot` 生成文件。
- Electron 保持 `contextIsolation`、sandbox 和禁用 Node 集成；preload 只暴露白名单 IPC，不向渲染进程暴露 API 令牌或任意文件／进程调用。
- 产品显示名、可执行文件名和持久化身份是不同概念。不得顺手修改 `appId`、用户数据目录、命名空间或安装升级标识；变更需有明确迁移方案，安装产物文件名保持更新链路兼容。

## 9. 部署与脚本

- 桌面形态是 Electron 启动本地 API sidecar；Linux／Docker 运行同一 API 并托管前端，不能照抄独立 Windows Agent 服务的拓扑。
- 便携 PostgreSQL 与外部数据库通过现有配置区分。容器不能默认访问宿主文件；挂载高权限 socket 或宿主目录必须作为显式安全决策，不作为常规“启用主机能力”步骤。
- 运行路径、外部工具名称、权限和进程启动必须兼容实际目标平台，不硬编码开发机盘符。未实测 Linux／Docker 时必须说明，不用 Windows 构建代替跨平台运行验收。
- PowerShell 脚本保持 Windows PowerShell 5.1 兼容，命令示例不使用 `&&`；包含中文的脚本需采用可由 5.1 正确解析的编码，保留已有 BOM。Linux shell 脚本保持 LF 和可执行位。
- 发布走既有脚本并保留用户数据；`-SkipFrontend` 只可在确认前端产物已更新时使用。验证用隔离测试数据，不擅自启动／停止用户数据库或占用生产端口。
- 部署细节见 [部署说明](docs/DEPLOYMENT.md)、[云部署说明](docs/CLOUD-DEPLOYMENT.md) 和 [桌面说明](desktop-electron/README.md)。

## 10. 必须执行的验证与交付标准

先运行最能验证本次行为的定向测试，再执行对应范围的检查。下面命令从仓库根目录运行；未涉及的模块不要求无差别打包或全量端到端测试。

| 变更范围 | 最低验证要求 |
| --- | --- |
| 后端代码 | API Release 构建 + 对应 xUnit 回归；修改分层依赖时另跑架构检查 |
| 数据库、权限、MCP、插件、任务等共享行为 | 定向回归之外增加受影响调用方／拒绝路径／契约检查；涉及运行流程时在隔离环境验收 |
| 前端代码 | `npm run build`（包含类型检查）+ 受影响界面实际操作验证 |
| Electron／更新器 | 对应桌面测试；修改启动、IPC、打包或升级链路时增加相应契约／安装回归 |
| 依赖 | 漏洞检查、相关项目构建及兼容性回归；不能隐藏扫描失败或新增高危漏洞 |
| 文档 | 检查引用路径、命令参数及内容与实现是否一致；纯文档变更不要求启动业务服务 |

```powershell
dotnet build MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj -c Release
dotnet test MyFirstMafAgent.Api.Tests/MyFirstMafAgent.Api.Tests.csproj
# 定向测试示例；其他改动替换为实际测试类或过滤条件。
dotnet test MyFirstMafAgent.Api.Tests/MyFirstMafAgent.Api.Tests.csproj --filter "FullyQualifiedName~HostProfileResolverTests"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Check-Architecture.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Check-Dependencies.ps1
npm --prefix FrontEnd/myfist-agent run build
npm --prefix desktop-electron run test:updater
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Check-DocLinks.ps1
```

- [解决方案](MyFirstMafAgent.slnx) 当前包含工作区外测试项目引用，默认优先构建上述实际项目；不要为了构建擅自创建外部目录或删除引用。
- [Check-DocLinks.ps1](scripts/Check-DocLinks.ps1) 当前只覆盖其列出的文档，不包含本文件且不校验标题锚点。改本文时必须额外检查链接，不能以该脚本通过代替本文件验收。
- MCP／插件／桌面端到端脚本在 [scripts](scripts/) 中；先读参数和前置条件，再针对已准备的测试实例执行，不依赖脚本中的本机默认路径。
- 修复缺陷应优先在现有测试文件补失败场景；新增功能至少覆盖成功和重要失败路径。测试保持确定性，不依赖个人密钥、真实私人数据或未声明的在线服务。
- 交付前核对：目录归属正确；未扩大依赖／权限；公共契约与数据兼容；未带入秘密和生成垃圾；所需测试通过；相关文档同步；未验证风险明确列出。

## 11. 文档维护

- 当前实现细节参考 [ARCHITECTURE.md](ARCHITECTURE.md) 和 [PROJECT_DOCUMENTATION.md](PROJECT_DOCUMENTATION.md)，使用入口见 [README.md](README.md)。文档与代码不一致时先核对实际实现，再修正文档；不能把未来方案写成已经实现。
- 后续新增模块、调整依赖方向、认证／插件协议、数据路径或验证入口时，必须同步本文件相关条目，不复制出第二份相互冲突的全局规范。
- 历史计划仅作背景，实际优先级见 [路线图](docs/ROADMAP.md)；本文的强制规则不能由历史草案默默替换。
