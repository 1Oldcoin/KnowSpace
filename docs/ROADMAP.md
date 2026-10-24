# 未完成事项清单（ROADMAP）

> 这份文件**只列还没做的**。已完成的能力见 `ARCHITECTURE.md` §9 与 `PROJECT_DOCUMENTATION.md`。
> 重构前的历史清单（含每个阶段的验证记录与踩坑）归档在
> `docs/archive/ROADMAP-2026-10-before-refactor.md`，只在需要考古时看。
>
> 最后一次按当前架构重写：2026-10-24。当前形态 = Electron sidecar + ASP.NET Core API
> + 便携 PostgreSQL(pg0)/pgvector + 插件子进程（JSON-RPC over stdio）。

---

## 0. 已定的产品边界

> 这一节是**口径**，不是待办。写在这里是为了不必每次讨论都重新决定一遍。
> 想改主意可以，但要先改这张表。

| 能力 | 决定 | 理由 |
| --- | --- | --- |
| 多用户与认证授权 | **不做** | 定位是本机单用户知识库。单机单用户加用户表，只换来登录摩擦，换不到隔离（数据本来就在同一个 OS 用户下） |
| 插件 / 知识库市场 | **不自建平台** | 分发交给插件市场官网与 GitHub Releases；本项目只负责「把包载入、管好、报清楚」 |
| 安装包代码签名 | **不做** | 由发布者自行打包签名；SmartScreen 提示作为已知项写进发布说明，不阻塞功能 |
| 跨平台 | **代码就绪，实测延后** | RID 抽象、Dockerfile、发布脚本都在；由发布者用虚拟机自行验证（见 `docs/DEPLOYMENT.md` §8） |
| 分布式执行 / 多语言 SDK / 任务 DAG | **不计划** | 目标是「本机所有 Agent 共享一个知识库」，没有多机、多团队、跨语言编排的场景 |
| 插件进程隔离 | **已做（Windows）** | 权限不能只靠插件自觉：用户必须能在**不知情即被拒绝**的前提下控制联网与读写。Windows 用 AppContainer 落地；其它平台的等价实现未实测，如实标注未支持（详见 D-1） |
| 插件规范 | **对齐主流** | 开发者不该为每个平台重写一份插件。以 MCP 为骨、兼容生态既有清单词汇（详见 D-2） |

---

## 1. 技术债（先还，别一边修一边加）

| 编号 | 问题 | 影响 | 状态 |
| --- | --- | --- | --- |
| **T-1** | 笔记列表过滤在内存里做 | `GET /notes?query=&category=&documentId=&topicId=` 先把整库笔记（含 50 KB 正文）读进内存再筛。上千条笔记后明显变慢 | ✅ 已还（2026-10-24） |
| **T-2** | 知识库包导入没有补偿回滚 | 导入中途失败会留下"半成品知识库"+ 已落盘文件，用户得自己收拾 | ✅ 已还（2026-10-24） |
| **T-3** | 检索评估语料太小 | 三种模式的 recall@5 / MRR 全是 1.0，等于没有信号；rerank 与拒答阈值没有调参依据 | ✅ 已还（2026-10-24） |
| **T-4** | 端到端冒烟没有脚本 | 每次回归靠手跑发布版 + curl | 降级：发布者手工全量测，暂不做脚本 |
| **T-5** | 笔记正文没有引用锚点 | 引用只出现在卡片 chip，正文里仍是纯文字，长笔记找不到出处 | ✅ 已还（2026-10-24） |

已还清的四条（T-1 / T-2 / T-3 / T-5）的做法与证据见 §5。

---

## 2. 待做功能

### D-1 插件进程真隔离（最高优先）✅ 已完成（2026-10-24，Windows）

- **原现状**：`PluginAccessPolicy` 是**准入控制**——manifest 声明的权限必须由用户授予，能力按副作用等级决定是否需确认。但子进程与核心**同用户同权限**：一个声明「无 network」的插件照样能 `socket()`，一个声明「无 file-write」的插件照样能写用户目录。权限表只是"说了算"，不是"做得到"。
- **已做**：
  1. Windows 用 **AppContainer** 启动插件进程（`CreateAppContainerProfile` + `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`）。`network` 能力（`S-1-15-3-1`）**按授权决定是否附加**——没授就真的没有出站。
  2. 文件系统：只给插件安装目录 `(OI)(CI)(RX)`、`MYFIRSTMAFAGENT_PLUGIN_DATA` `(OI)(CI)(M)`、`plugins/.runtimes` `(OI)(CI)(RX)` 授 ACL；**祖先目录一律不碰**（实测：容器凭叶子目录的继承 ACE 就能穿越整条路径；而给 `C:\` 写 ACE 会让 Windows 在整个驱动器上递归重整继承，卡死数分钟，且普通用户本来也无权改）。
  3. 进程树用 Job 对象兜底：`CREATE_SUSPENDED` → `AssignProcessToJobObject(KILL_ON_JOB_CLOSE)` → `ResumeThread`，核心退出后孙进程不会滞留。
  4. 环境变量改白名单（不再把连接串/API Key 复制给第三方程序），`USERPROFILE`/`TEMP`/`APPDATA`/`LOCALAPPDATA` 重定向到插件数据目录；`node` 等裸命令优先解析到 `plugins/.runtimes`，并为 Node 注入 `--preserve-symlinks-main`（否则 node 解析入口脚本时要 `realpath('C:\')`，容器读不到 → `EPERM`）。
  5. 隔离失败**拒绝启动**（`Mode=require`）并给出可照做的建议，不静默降级；非 Windows 报告 `kind: unsupported / applied:false`，不假装有沙箱。
  6. `GET /api/plugins/{id}` 新增 `isolation`（mode/applied/kind/network/capabilitySids/可读写路径），列表加 `isolation` 短标签，前端详情页新增「沙箱」一行。
- **验收证据**（本机 5056，Production，win-x64 自包含发布，Node 25，真实 HTTP 调用）：
  - 同一份 Node 插件代码、同一个目标 `223.5.5.5:443`：清单不声明权限 → `isolation=windows-appcontainer:offline`，`POST /invoke` 返回 `"CONNECT_ERR EACCES"`；同一插件授予 `network` → `isolation=windows-appcontainer:network`、`capabilitySids=["S-1-15-3-1"]`，`POST /invoke` 返回 `"CONNECT_OK"`。
  - 入口指向 `C:\Program Files\nodejs\node.exe`（容器读不到）→ 状态 `Failed`、`isolation.applied=false`、`lastError` 明确写出「容器没有读取入口程序的权限 + 把运行时放进 `plugins/.runtimes` 或加入 `ReadableRoots`」。
  - `icacls` 可见插件目录 `S-1-15-2-…:(OI)(CI)(RX)`、数据目录 `:(OI)(CI)(M)`；重启后 `ApplyGrants` 不再重复写 ACL（本次新写 0 条）。
  - 真机用例（`PluginIsolationWindowsTests`，仅 Windows 跑）：建配置/同 SID 幂等、授权后可读且二次授权为 0、`Program Files` 运行时被判不可读、容器内 `cmd` 可读插件目录/可写数据目录/读不到未授权目录。
  - 全量 **702 测试通过**；`vue-tsc` + `vite build` 通过。
- **一期不做**：域名白名单、只读路径白名单、CPU/内存限额（先做「有/无」两档，把决定权交回用户）；Linux `bwrap`/`unshare`、macOS `sandbox-exec` 的等价实现（未实测就标未支持）；AppContainer 的 loopback 豁免（`CheckNetIsolation` 需管理员）。

### D-2 插件规范对齐（MCP 兼容 + 生态既有词汇）✅ 已完成（2026-10-24）

- **原问题**：自有协议 `plugin/initialize|configure|health|capabilities/list|capabilities/invoke|shutdown`，清单只认 `manifest.json`，权限只认 `network|file-read|file-write|clipboard|process`。结果是一个现成的 MCP server 或生态里普遍形态的插件包**根本载不进来**，作者必须为这个平台重写一份。
- **已做**：
  1. **MCP 方言**：清单写 `"protocol": "mcp"`（或 `"dialect": "mcp"`）即走 `initialize` → `notifications/initialized` → `tools/list`（跟游标分页）→ `tools/call`；每个 tool 映射成 `agent-tool`，`inputSchema` 原样作为工具参数；`isError` → 调用失败，`content` 里的文本拼成结果；健康检查用 `ping`；退出靠关闭 stdin；取消用 `notifications/cancelled`。副作用等级取自 `annotations`（`destructiveHint` / `readOnlyHint`），清单里同名能力可覆盖。
  2. **清单词汇别名**（`PluginManifestNormalizer`）：认 `plugin.json` / `skill.json`；`net.outbound`→`network`、`fs`→`file-read`+`file-write`、`proc`→`process`；`inputSchema`→`parameters`、`args`→`arguments`、`cwd`→`workingDirectory`、`command` 写成数组也能读、顶层 `requires`/`permissions` 对所有能力生效、`kind`/`sideEffect` 的生态写法都认。**所有改名作为 warning 透出，认不出的权限词原样保留并报错。**
  3. **目录直接扫描**：`App_Data/plugins/{目录}/` 放清单即被发现；解压多一层时下探（只在"恰好一个子目录"时猜）；宿主命令 + 包名（`npx -y @scope/server`）不再被误判成"入口文件不存在"。
  4. **MCP 的配置通道**：`entry.env` + 用户配置标量字段注入为 `MYFIRSTMAFAGENT_CONFIG_<KEY>`（MCP 只认环境变量，没有 `configure` 方法）；改配置会重启进程。
  5. 作者指南补 §3.1「清单文件名与字段别名」与 §3.2「用 MCP 方言写插件」。
- **验收证据**（本机 5057，Production，win-x64 自包含发布，Node 25）：
  - 假 MCP server（`initialize`/`ping`/`tools/list`/`tools/call`），清单是 `plugin.json` + `args` + `env` + `net.outbound` + `protocol` 缺省（用 `dialect`）→ 热扫描发现 → 权限未授予时停在 `PermissionRequired`（`network`，证明别名已展开）→ 授予后 `Running`，日志出现 `MCP server demo-mcp introduced itself as 'demo-mcp-server' 0.4.2 (protocol 2024-11-05)`、`ready with 2 capability(ies): echo, wipe-storage`。
  - `POST /invoke {capabilityId:"echo"}` → `HTTP 200` `"result":"echo: hello from mcp"`；`wipe-storage`（`destructiveHint`）不带 confirm → `HTTP 403`；带 confirm → `HTTP 502` 且错误信息是 server 返回的 `refusing to wipe storage in a demo server`。
  - `GET /api/agent/tools` 里出现 `plugin__demo-mcp__echo`（`wipe-storage` 因需确认而未桥接给模型），共 24 个工具。
  - ZIP 安装：`skill.json` + 包裹目录 + `requires: ["fs","proc"]` → `HTTP 200`，落到 `plugins/skill-demo/versions/0.0.0`，`declaredPermissions = file-read,file-write,process`；授权后 `POST /invoke` → `"echo: from skill zip"`。
  - 停用插件：`mcp: stdin closed, exiting` → 进程自行退出，**没有走 kill**，`node.exe` 残留 0。
  - 单测：新增 `PluginDialectTests`（23 项）、`PluginDiscoveryTests`、`McpCancellationTests`；全量 **670 通过**。
- **一期不做**：SSE / HTTP 传输的 MCP server（只支持 stdio）；`notifications/tools/list_changed` 热重建能力表（只记日志，重启生效）；MCP 的 prompts / resources / sampling / elicitation。

### D-3 知识库包从 URL 安装 + Releases 分发

- **现状**：`.zjpack.zip` 导出/导入已打通（P2-6），但只能本地选文件。
- **要做**：
  1. `POST /api/knowledge-bases/import-from-url`，body `{ url, sha256? }`；服务端下载后走同一套校验。**必须校验**：协议只允许 https、大小上限、可选 `sha256` 比对、不接受重定向到内网地址（复用 `SafeUrlGuard`）。
  2. 包里补 `author` / `license` / `homepage` 字段（可选，缺了也能导入）。
  3. 导入前预览：`POST .../package/inspect` 返回「N 份资料 / N 篇笔记 / N 个专题 + 预估体积」，让用户先看清楚再点。
  4. 设置页在「知识库备份与分享」里加「从链接导入」，支持粘贴 GitHub Releases 直链或市场口令（口令 = 短码，服务端/官网解析成直链）。
- **验收**：粘一个 Releases 直链进界面 → 预览数量正确 → 导入成功且资料可检索。
- **不做**：不自建目录/评分/评论；不做版本升级（同一知识库的增量更新留到有真实需求时再说）。

### D-4 检索质量：重排与拒答 ✅ 已完成（2026-10-24）

- **原问题**：混合检索 + RRF 已完成，但 rerank 与拒答阈值没做——没有模型、也没有语料分布。已知第一个靶子：RRF 会把"单路弱命中"稀释掉——「领取任务的执行者多久必须汇报一次」lexical 排第 5 能召回、vector 零召回，融合后掉出 top-5。
- **已做**：
  1. **重排打分核心 `RerankScoring`**（纯函数，`Domain/Knowledge/RerankScoring.cs`）：`Score = (1-w)·Base + w·Coverage`，其中 `Base = 0.5×融合名次先验 + 0.5×归一余弦`，`Coverage` 是查询词元的 idf 加权覆盖率（idf 用候选集自身的文档频率估，不需要外部语料）。中文分词复用全文检索那套二元组，口径一致。
  2. **重排端口 `IChunkReranker`**（`Application/Knowledge/CoverageReranker.cs`）：默认实现 `CoverageReranker` 不需要任何模型；要接 cross-encoder 时只换注册，路由不用动。
  3. **新检索模式 `mode=rerank`**（另接受 `hybrid+rerank` / `hybrid_rerank` / `hybrid-rerank`），响应新增 `mode` / `abstained` / `topScore` / `abstainThreshold` 四个字段；显式传 `hybrid` 永远不会被自动升级为重排，保证评估基线稳定。
  4. **拒答机制**：最高分低于阈值时返回空结果 + warning「资料里没有找到与该问题足够相关的内容（最高相关度 X，低于阈值 Y）」，并把这句话作为 `referenceNote` 拼进 system prompt，明确要求"不要用常识或推测补全，也不要编造引用"；落库的 `retrieval.abstained/topScore` 供前端 `.citation-warning` 展示。
- **验收证据**（本机 5056，Production，win-x64 自包含发布，语料 KB `96664cb1-…`（27 篇）+ BGE 索引 `749313f5-…`，fixture 42 条有答案 + 8 条无答案，**同一命令连跑两次数字完全一致**）：

  | 模式 | strategy | recall@5 | MRR | 命中/有答案 |
  |---|---|---|---|---|
  | `vector` | vector | 0.8571 | 0.7270 | 36/42 |
  | `lexical` | lexical | 0.9524 | 0.8460 | 40/42 |
  | `hybrid` | hybrid | 0.9762 | 0.8492 | 41/42 |
  | `rerank` | rerank | **1.0000** | **0.9075** | **42/42** |

  复跑命令：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Evaluate-Retrieval.ps1 -Fixture scripts\retrieval-eval\corpus.json -Modes vector,lexical,hybrid,rerank -OutFile C:\Temp\eval-d4.json`

- **权重不是拍的**：`w`（覆盖率权重）在真实语料上扫过一遍，`w=0` 精确复现 hybrid 基线（0.9762 / 0.8492，说明链路没串味）：

  | w | 0.20 | 0.30 | 0.40 | 0.50 | 0.60 | **0.65** | 0.70 | 0.80 | 0.90 | 1.00 |
  |---|---|---|---|---|---|---|---|---|---|---|
  | recall@5 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | **1.0000** | 1.0000 | 1.0000 | 1.0000 | 0.9286 |
  | MRR | 0.8401 | 0.8738 | 0.8857 | 0.8937 | 0.8956 | **0.9075** | 0.9115 | 0.8480 | 0.8480 | 0.7937 |

  `w=0.20~0.90` 的 recall@5 都在 1.0000，取平台区中间偏左的 **0.65**（不是取 MRR 最高点 0.70，避免"挑尖峰"式的过拟合）。
- **本次被真实运行打出来的三个问题**（都已修）：
  1. **纯覆盖率重排比 hybrid 更差**（第一次接上实测 0.9524 / 0.7972，比基线还低）。原因：覆盖率只看"问题的词出现过没有"，沾满虚词就能拿高分，于是把 RRF 本来排对的结果挤下去（16 条用例名次变化里 9 条是变差）。改成"先验 + 证据"的凸组合后反超。
  2. **`RerankCandidateLimit` 是个死配置**。候选池是**每一路**的召回条数（`limit×3`=15），只在 `fuseTake` 上放大没有用——融合只能从已召回的那几条里挑。改成在召回前就把候选池放大到 `RerankCandidateLimit`。20 条候选时 rerank 与 hybrid 打平（0.9762 / 0.8552），60 条候选时才拿到 1.0000 / 0.9075。代价实测 p50 `92ms→97ms`、p90 `144ms→179ms`（30 次查询）。
  3. **候选少于 5 条时"通用词剔除"会把查询词元剔光**，覆盖率恒为 0，等于把重排关掉。加了 `MinCandidatesForUbiquitousFilter = 5` 的门槛。
- **拒答：机制做完了，但默认关闭——这是实测结论，不是没做完**。同一份 fixture 上"有答案但换了说法"与"真的没有答案"的得分**完全重叠**：有答案最高分最低 0.361，无答案最高分最高 0.474。

  | 阈值 | 该拒的拒了几条 | 不该拒却拒了 |
  |---|---|---|
  | 0.30 | 0/8 | 0/42 |
  | 0.35 | 3/8 | 0/42 |
  | 0.40 | 5/8 | 1/42 |
  | 0.45 | 7/8 | 4/42 |
  | 0.50 | 8/8 | 15/42 |

  要让 8 条无答案全部拒掉，必须同时误拒 15/42 条（36%）有答案的问题——这比"让模型自己读资料后说没有"差得多。所以 `Retrieval:AbstainEnabled` 默认 `false`，阈值默认 0.45 并把这个取舍写进注释；真正的"资料里没有"由提示词约束模型回答。
- **拒答链路端到端验证**（临时 `Retrieval__AbstainEnabled=true`、阈值 0.45）：
  - `POST /api/search` `mode=rerank`：「番茄牛腩要炖多久」→ `abstained=true`、`topScore=0.417`、`results=[]`、warning 原文返回；「公司年假有几天，怎么申请」0.376 拒；「金毛老是拆家该怎么训练」0.330 拒；而「插件一共有哪些状态」0.480 放行、「领取任务的执行者多久必须汇报一次」0.527 放行 → **该拒的拒了、不该拒的答了**。
  - 同一批问题 `mode=hybrid` 全部 `abstained=false`、`abstainThreshold=0`（拒答只在重排模式生效，不污染基线）。
  - `POST /api/chat`：「番茄牛腩要炖多久」→ `retrieval.abstained=true`、`topScore=0.4168`、`strategy=rerank`、warning 落在 `retrieval.warning`，前端 `.citation-warning` 直接展示；「插件一共有哪些状态」→ `abstained=false`。
- **单测**：`RerankScoringTests` 29 项（覆盖 idf 排序、平台区、名次/余弦两项先验、通用词剔除门槛、空查询降级、`[0,1]` 边界、模式解析、wire 名）、`KnowledgeAbstainPromptTests` 5 项（拒答提示词的生成、空格不外溢、拼在参考资料**之后**）。全量 **811 通过**，`scripts\Check-Architecture.ps1` 通过。
- **已知限制（诚实说明）**：
  - **没有接 cross-encoder**。仓库里只有 `bge-small-zh-v1.5`（embedding，94 MB `model.onnx`），没有任何 rerank 模型；接一条没验证过的 ONNX 推理路径比不接更糟——它会在用户机器上以未知方式失败。所以这一期的重排是**词面**的，不是**语义**的：它能修"词都出现过但 RRF 排得靠后"，修不了"完全换了说法"。换实现只需替换 `IChunkReranker` 的注册（例如日后接入 `bge-reranker-base`）。
  - **分数阈值拒答在这类单领域语料上不可行**——上面的重叠数据就是证据。跨领域语料（家庭相册 + 工作文档 + 闲聊）可能分得开，但那是另一份语料的事，不能拿这份数字去承诺。
  - 42 + 8 条用例只能说明方向，不足以称"通用最优"；`w=0.65`、`UbiquitousRatio=0.7` 都是这份语料上的平台区中点，换语料应重跑 `Evaluate-Retrieval.ps1` 再定。
- **一期不做**：cross-encoder / 神经重排、LLM 打分重排、查询改写（HyDE / 多查询）、按用户反馈在线学习权重。

### D-5 视频关键帧与视觉检索 ✅ 已完成（2026-10-24）

- **原问题**：视频走 Whisper 转写，画面内容完全没进索引，视频只能"按字幕搜"；而很多视频的关键信息只在画面里（板书、幻灯片、演示界面）。
- **已做**：
  1. **按场景变化抽帧**：`select='gt(scene,阈值)'`（默认 0.25，可配 `Media:FrameSceneThreshold`）；场景帧不足 2 张时退化成 `fps=1/30` 均匀采样（幻灯片、固定机位、纯色背景不会一片空白）。
  2. **时间戳来自 ffmpeg 自己**：`showinfo` 的 `pts_time` 解析回每帧起始时间。按"第几张 × 固定间隔"推算在场景检测下必然错位（大量帧被丢弃，序号与时间不成比例）。
  3. **一帧一描述**：视觉模型逐帧描述（`scale=768:-2` 控成本）；视觉模型不可用时只记一条 warning，视频保持"仅转写"入库——**独立阶段只能降级，不能把整篇资料变 Failed**。
  4. **`MediaTimeline` 统一时间轴分片**：转写与画面描述合并后按时间排序，每行以 `[hh:mm:ss]` 开头，切片只在段边界拆分；时间戳永不与文本分离，单行超长也自成一片。
  5. **引用带时间点**：`KnowledgeChatCitation.MediaStartSeconds` 由切片首行时间戳推导；前端引用芯片显示播放图标 + `mm:ss`，点击打开播放器 `currentTime = 秒数`（`enableRangeProcessing` 已支持定位）。
  6. 音频转写路径也改用同一套时间轴分片（原来按字符数硬切，会把时间戳切成孤行）。
- **验收证据**（本机 5056，Production，win-x64 自包含发布，ffmpeg 9.0.2，Whisper base）：
  - 30 秒三段式测试视频（白 / 黑 / 动态测试图，硬切在 10s、20s）→ 日志 `Scene detection produced 2 frame(s)`，`2` 次视觉请求（不是 60 次），切片内容为：
    `[00:00:00] [BLANK_AUDIO]` / `[00:00:10] 画面描述：…` / `[00:00:20] 画面描述：…`——**时间点与场景切换完全对齐**。
  - `POST /api/chat` → `citations[0].mediaStartSeconds = 0`（时间轴切片），`citations[1].mediaStartSeconds = null`（摘要切片），UI 芯片渲染 `0:00` + 播放图标，tooltip「从 0:00 开始」，点击弹窗 `readyState=4 / duration=30 / currentTime=0`，手动设 `currentTime=10` 立即生效（证明可按引用时间点跳转）。
  - 单测 `MediaTimelineTests`（17 项，含真实 ffmpeg 9 的 `showinfo` stderr 样本）。
- **本次被真实运行打出来的两个 bug**（都已修）：
  - `-vf` 与滤镜表达式被拼进同一个 argv 元素，ffmpeg 报未知选项 → 抽帧全失败且只有一条 warning。外置进程用 `ArgumentList` 必须一个选项一个参数。
  - `Media:EnableVideoFrameAnalysis` 在 `appsettings.json` 里仍是 `false`，代码默认值 `true` 被配置覆盖 → 视觉链路根本没跑（日志里 0 次视觉请求）。
  - 顺带修：`ReadStartSeconds` 曾把 0 秒当"没有时间戳"返回 null，导致**从片头开始的那片引用永远无法跳转**（0 在 JS 里是假值，前端也用真假值判断）。现在 0 是合法时间点，null 只表示"不是媒体切片"。
  - 顺带修：ffmpeg 失败信息原来截取 stderr 前 500 字符，看到的永远是版本横幅；改成取最后 800 字符。
- **已知限制（诚实说明）**：验证用的是一个本地桩视觉服务，证明的是**链路**（抽帧 → 描述 → 时间对齐 → 引用跳转），不是画面描述质量；描述质量取决于接入的真实视觉模型。场景检测是阈值法，对缓慢渐变不敏感（会退化到均匀采样）。每帧一次视觉调用是真实成本，`Media:MaxVideoFrames`（默认 60）是上限。
- **一期不做**：OCR、镜头级分割（Shots）、按人脸/物体建实体索引、视频摘要的分层树。

### D-6 伴侣历史进向量检索 + 常见导出格式 ✅ 已完成（2026-10-24）

- **原问题**：只吃 UTF-8 JSON/TXT，QQ/微信导出的文本会被整段吞成"一条消息"或误判成多条；伴侣检索是关键词匹配，用户记得"聊过什么"但记不住原话时等于搜不到。
- **已做**：
  1. **解析器支持 QQ / 微信 / 通用文本 + CSV**：`ChatHistoryParser` 改成状态机——识别 QQ 块头（`2023-05-12 21:03:11 小明(123456)` 单独一行）、微信行内时间戳、`发送者 时间: 内容`、无时间戳的 `发送者: 内容`、CSV（表头别名识别 + 引号/逗号/制表符 + 无表头按前三列兜底），并剥掉 `小明(123456)` 尾部的 QQ 号。支持 `.json` / `.txt` / `.csv`，其他扩展名直接报错并列出支持的格式。
  2. **独立向量表** `companion_message_vectors`（`message_id` 主键 + `embedding vector` + 指纹 + 维度）：伴侣历史**不进任何 `kb_*` 表**——私聊混进资料索引后，"问我的资料"会把私聊当证据引用出来。
  3. **向量 + 词面 RRF 融合**：`CompanionMemoryIndexer` 复用默认向量模型（跟随设置页选定规格），`CompanionService.RecallAsync` 词面优先召回、向量结果按 RRF（k=60）融合；向量不可用自动退回纯词面，功能不会整体不可用。
  4. **导入即后台建索引** + 手动重建：`POST /api/companions/{id}/reindex?limit=`、`GET /api/companions/{id}/index`；前端伴侣页显示「语义记忆 N 条」和重建按钮。
  5. 只给**目标人物本人**的消息建向量（`sender` 过滤是硬条件），少于 4 个字符的消息跳过。
- **验收证据**（本机 5056，Production，QQ 格式导出文件 9 条消息）：
  - 导入 → `messageCount=9`；2 秒内 `GET /index → indexed=7`（= 目标人物 7 条，另外 2 条是我说的，正确地没进索引）。
  - 提问「后来那次检查还顺利吗」（与任何一条原话**没有**字面重叠）→ 日志 `Companion recall: vector=7 lexical=0 fused=7`，`recalledMessageCount=7`，送进提示词的第一条正是「下周三我要去医院做个胃镜，说实话有点紧张」——**词面检索一条都召不回，语义链路把正确的往事放在了第一位**。
  - 单测：`ChatHistoryParserTests` 扩到 10 项（QQ 块内冒号不拆消息、QQ 号剥离、微信行内格式、发送者在前格式、CSV 表头/无表头、空文件、不支持扩展名）；全量 **725 通过**。
- **本次被真实运行打出来的问题**（都已修）：提示词里对召回结果做了 `.Reverse()`（历史遗留：词面检索按时间倒序时反转能变正序），结果**最相关的那条被压到最后一行**；改为保持相关度顺序。另：谓词索引原来直接取"最早注册的规格"，用户在设置页切换向量模型后伴侣记忆仍用旧模型（"已索引 N 条"与实际检索模型不一致），现在跟随设置页默认规格。
- **已知限制（诚实说明）**：验证用的是本地桩向量服务（手写主题词表），证明的是**链路与排序**，不是真实 embedding 模型的语义质量。切换向量模型后旧向量仍在库里（按指纹过滤，不会被误用），需点一次"重建索引"。
- **一期不做**：语音消息转写进伴侣索引、图片 OCR、跨画像的联合检索、自动增量同步（重新导入即可）。

### D-7 插件包摘要/签名工具（可选）

- **现状**：`IntegrityPolicy=enforce` 要求插件包带摘要/签名，但没有"生成摘要 → 签名 → 写进 manifest"的工具链，实际上没人能走通。
- **要做**：一个小工具（`dotnet` 或脚本）：`sign-plugin <目录> --key <pem>` → 产出可直接安装的签名包。
- **说明**：与安装包代码签名无关（那个不做）。这里签的是**插件包**，目的是防篡改与来源可追溯。
- **优先级**：低。默认 `IntegrityPolicy=warn`，未签名插件照常安装；只有当要分发第三方插件时才值得做。

---

## 3. 明确不做（留档，避免反复讨论）

| 项 | 原编号 | 为什么不做了 |
| --- | --- | --- |
| 多用户、OIDC、知识库 ACL、API Token | 原 P3-1 | 见 §0：定位是单机单用户 |
| 自建插件/知识库市场（上传、评分、评论、一键安装） | 原 P3-2 | 分发交给 GitHub Releases 与市场官网，本项目只负责载入 |
| 安装包代码签名 | 原 P0-5 | 发布者自行处理 |
| 任务队列多机领取 / 插件远程主机 / 跨机租约 | 原 P3-4 | 没有多机场景 |
| TypeScript / Python 插件 SDK | 原 P3-5 | 协议已经语言无关（JSON-RPC over stdio），跨语言作者直接照协议写即可；D-2 的 MCP 兼容更重要 |
| 任务 DAG 与批量编排 | 原 P3-6 | 现有"优先级 + 类型配额"够用 |

---

## 4. 顺手记录的小项（低优先，随时可做）

| 项 | 说明 |
| --- | --- |
| `publish/` 目录 | `console` / `desktop` / `releases` 都是构建产物，可随时删除重建（安装包约 310 MB） |
| 根目录临时文件 | `probe-out.txt`、`probe-out2.txt`、`rowdump.txt`、`api.ts` 是早期排查残留，确认无用可删 |
| `App_Data/probeB` | 旧探针数据库目录，确认无用可删 |
| 在 Desktop 路径跑测试会崩 | `dotnet test` 的 testhost 在受保护目录下崩，必须加 `-p:OutputPath=C:\Temp\maftests\`（已记入仓库记忆） |
| 缺少 `DELETE /api/knowledge-bases/{id}` | 现在只能删资料、笔记、专题，知识库本身删不掉；清理验证用的一次性库只能重建数据库。加接口时要一并处理笔记/专题/资料/向量/落盘文件的级联（`KnowledgePackageStore.DeleteKnowledgeBaseAsync` 已有事务版实现可复用） |
| `docs/PHASE0-BASELINE.md` | 阶段 0 的延迟/索引测量，属于历史基线，不必再更新 |
| **安装包需要重打** | `publish/releases/MyFirstMafAgent-0.1.0-setup.exe` 是 2026-10-23 构建，**不含** D-1/D-5/D-6/T-3/T-5/BUG-1/T-6。对外分发前必须重跑 `scripts/Publish-Portable.ps1` + `scripts/Build-ElectronInstaller.ps1`，否则用户装到的是"已修好的 bug 还在"的旧行为 |

---

## 5. 已还技术债的做法与证据（2026-10-24）

**T-1 笔记过滤下推 SQL**
- 落点：`Domain/Notes/NoteQuery.cs`（过滤条件值对象）、`INoteRepository.ListAsync(NoteQuery, ct)`、`SqlSugarNoteRepository` 用 SqlSugar 表达式翻译成 WHERE，`NoteService` 不再读全库。
- 引用反查需要的"哪篇笔记引用了这份资料"仍要跨 JSON 列，因此新增 `kb_notes.source_document_ids`（空格分隔的 `guid:N`），写入时维护，查询走 `LIKE`；旧行在 `InitializeAsync` 里按 `sources` 回填。
- 为什么不建 GIN 索引：笔记量级（单库千级）下索引收益低于维护成本；真正贵的是把 50 KB 正文拉进内存，而这已经被 WHERE 挡住了。
- 证据（真实 Postgres）：不传筛选 2 条；`query=握手` 1 条、`query=插件` 2 条、`query=zzzznomatch` 0 条；`category=问答笔记` 2 条、`category=读书摘录` 0 条；`documentId=<插件资料>` 2 条、未知 ID 0 条；`topicId` 1 条；组合筛选 2 条；`query=100%` 0 条（`%`/`_` 被转义，没有变成通配）；只传空格 2 条（等价于不过滤）。换成英文笔记后 `plugin` / `PLUGIN` / `Protocol` 各 1 条、`category=engineering` 与 `ENGINEERING` 各 1 条（不区分大小写）。启动日志确认回填：`Note source backfill: 6 note(s) indexed by source document`。

**T-2 知识库包导入补偿回滚**
- 落点：`KnowledgePackageImporter` 用 `try/catch` 包住导入主体，失败时按「笔记 → 专题 → 资料（含落盘文件）→ 知识库」逆序补偿删除，并把已删除/未删除的条目写进异常信息。
- 新增 `IKnowledgePackageStore.DeleteKnowledgeBaseAsync`；资料删除复用既有 `KnowledgeBaseStore.DeleteDocumentAsync`（它负责文件 + 切片 + 版本）。
- 为什么不用数据库事务：导入过程要写文件系统，事务包不住；补偿式回滚能保证"要么全在，要么全清"，代价是极端情况下可能残留孤儿文件（可接受，且会记日志）。
- 顺带修掉的两个真 bug（验证时才暴露）：
  1. **条目名分隔符**：Windows 打出来的 zip 条目名是 `files\0001.md`，清单里是 `files/0001.md`，直接 `TryGetValue` 永远落空 → 导入 0 份资料却返回 200。修法 `KnowledgePackageReader.NormalizeEntryPath` + `BuildEntryIndex`。
  2. **零内容导入留空库**：上面那条的后果是库里多一个空知识库。现在 `createdDocumentCount == 0 && createdNotes == 0` 直接抛异常，让补偿逻辑把壳子删掉，错误信息带第一条 warning。
- 证据（本机 5056，Production，win-x64 自包含发布）：
  - 正常路径：导出 11 份资料 / 3 篇笔记 / 1 个专题 → 导入成功，`documents=11 notes=3 topics=1 warnings=0`。
  - 归一化：1200 份资料的反斜杠条目包 → `HTTP 200 documents=1200 warnings=0`（修复前是 200 但 0 份）。
  - 回滚：清单声明 1 份资料但包里没有该文件 → `HTTP 400`「导入失败，已回滚写入的 0 份资料 / 0 篇笔记 / 1 个专题（原因：导入包没有可导入的内容：跳过《missing.md》：包内缺少文件 files/missing.md。）」，知识库数 10 → 10（零泄漏），日志 `Knowledge package import failed and was rolled back: kb=… notes=0 topics=1 documents=0`。
  - 单测：`KnowledgePackageRollbackTests`（2 项，含零内容回滚）、`KnowledgePackageTests` 新增条目归一化 2 项、`NoteQueryTests` 4 项；全量 648 通过。

**T-3 检索评估语料（同主题混淆集）**
- 落点：`scripts/retrieval-eval/corpus/`（27 份中文资料，插件运行时 / 索引 / 任务中心三簇各 8 份 + 1 份总览）、`scripts/retrieval-eval/corpus.json`（42 个用例，分「字面 / 语义 / 区分 / 总览陷阱」四类）、`scripts/Seed-RetrievalCorpus.ps1`（建库 + 上传 + 等 Ready + 写 `corpus.kb.txt`，可重复执行且按文件名跳过已存在的资料）、`scripts/Evaluate-Retrieval.ps1`（新增分类统计、`corpus.kb.txt` 兜底读 ID、以及把 `-Modes vector,lexical,hybrid` 拆成三个模式——之前会当成一个模式名跑）。
- 为什么语料要"故意混"：三簇内部共用大量词汇（超时、重试、检查、保留期、回收…），并且每簇都有一份总览文只给模糊答案。这样「哪一份答得对」不再是文件名能不能对上，而是细节能不能被正确资料命中。
- 基线（本机 5056，Production，真实 bge-small-zh-v1.5 ONNX 向量 + 真实全文索引，top-5，42 个用例）：

  | 模式 | recall@5 | MRR |
  | --- | --- | --- |
  | vector | 0.8571 | 0.7270 |
  | lexical | 0.9524 | 0.8460 |
  | **hybrid** | **0.9762** | **0.8492** |

  分类别看，两个单模式各有一类彻底塌掉：**字面**（编号/配置名）vector 0.60 / 0.52，lexical 1.00 / 1.00；**语义**（改写问法）vector 1.00 / 0.90，lexical 0.80 / 0.68。hybrid 在两类的 recall 都是 1.00，这就是"必须两路都有"的实测依据，不再是断言。
- 顺带得到一个对 D-4 有用的反例：**RRF 也会把命中挤出去**。「领取任务的执行者多久必须汇报一次」lexical 排第 5 能命中、vector 完全没召回，融合后掉出 top-5（hybrid 全程唯一漏掉的一条）。同理「上传太多文件会不会被拒」从 lexical 第 2 掉到 hybrid 第 4。→ rerank 该优化的正是这种"单路弱命中被新候选稀释"的情况。
- 可复跑：连跑两次三个数字完全一致（向量化是确定性的，语料固定）。命令见 `scripts/retrieval-eval/README.md`。
- 诚实边界：命中判定是**文档级**（`expectFiles` + `expectText`），不解切片级排序质量；语料是我按"真实手册会怎么写"手写的，不是从真实用户库里抽的，所以适合做**回归对比**，不适合当作绝对能力结论。

**T-5 笔记正文引用锚点**
- 落点：`Domain/Notes/NoteCitationAnchor.cs`（纯函数，可单测）、`NoteExportFormatter`（正文锚点导出为 Markdown 脚注）、`NotesView.vue`（编辑器内的锚点面板 + 实时预览 + 悬空提示 + 卡片角标）。
- 语法故意做窄：只认方括号里的 1-2 位数字，可逗号分组（`[1]`、`[2,3]`）。所以 `[2024](url)` 这类链接、`[TODO]` 这类标签不会被误判成引用。
- 三条硬规则：
  1. **悬空锚点不变成死链**。序号大于引用条数时，编辑器里显示成红底序号并提示"第 N 条引用不存在"，导出时原样保留文字（看得见才改得掉），不生成脚注。
  2. **相邻纯文本要合并**。悬空锚点被当文本处理时不能把一句话拆成两段，否则调用方（渲染/导出）得自己拼。
  3. **没有锚点的老笔记导出格式不变**（仍是 `### 引用资料（N 条）` + 编号列表），只在正文确实写了锚点时切换成脚注模式，避免同一份稿子里出现两套编号。
- 证据（本机 5056，Production，真实 Postgres）：
  - 造一篇引用 3 份资料、正文写 `[1]` `[2]` 且故意写一个 `[9]` 的笔记 → `POST /notes` 返回 `sources: 3`；`POST /notes/export` 输出 `握手阶段是 10 秒[^1]…` + `[^1]: 索引-增量重放.md（第 1 段） · 相关度 81% · 资料 ID：… · 摘录…`、`[^2]: …`，`[9]` 原样保留，未被引用的第 3 条单独列在「未被正文引用的资料」下，且不再输出 `### 引用资料`。
  - 浏览器：卡片角标「正文 2 处引用」；打开编辑器出现「正文引用锚点 2 / 3 条已被正文引用」+ 3 个可插入的引用；预览里 `[1]` `[2]` 渲染成可点序号、`[9]` 是纯文本；点「插入」后正文尾部追加 `[3]`、计数变 `3 / 3`、预览多出一个 `3`；点序号 1 直接打开「索引-增量重放」原文阅读器（标题/正文/片段数一致）。
  - 单测：`NoteCitationAnchorTests` 15 项（分组锚点、悬空、链接/标签不误判、去重排序、5 组导出转换、脚注模式与列表模式、无引用笔记导出不变）；全量 740 通过。
- 诚实边界：锚点要**手动写**（或点"插入"），系统不会自动判断"这句话出自第几段"——那需要句子级对齐，属于另一个量级的工作。

**BUG-1 投喂任务永远卡在 running（42804）**（2026-10-24， completeness audit 发现）
- 现象：`avatar_feed` 返回 200 并把任务置 `queued`，Worker 领成 `running/extracting`，然后**永远停在那里**；`memoryCount=0`、`isDigested=false`、`undigested` 只增不减，`avatar_search_memories` 查不到刚投喂的内容。
- 根因：`SqlSugarAvatarRepository.UpdateIngestionAsync` 用 `Updateable.SetColumns(it => new T { CompletedAt = entity.CompletedAt })`。当 `CompletedAt` 是**求值为 `null` 的 `DateTimeOffset?`** 时，SqlSugar 把参数声明成 `DbType.AnsiString`，PostgreSQL 报 `42804: column "completed_at" is of type timestamp with time zone but expression is of type text`。这是投喂流程的**第一次**状态回写，所以它一挂，后面所有整理都不发生。
- 修法：`UpdateIngestionAsync` / `ClaimNextIngestionAsync` / `RequeueStaleIngestionsAsync` 改成显式 SQL + 显式参数，时间列统一 `CAST(@x AS timestamptz)`，参数类型不再影响写入。
- 顺带堵掉"队列会被一条任务堵死"：Worker 加 5 分钟单条超时（超时用独立取消令牌打断，收尾用 `CancellationToken.None` 写 `failed`，否则状态回写会被已取消的令牌带走、行又变回 `running` 坟头），并在空闲时每 60 秒扫一次超过 10 分钟的僵尸 `running` 重新排队（启动时也扫）。
- **修第一处之后又暴露出同类第二处**（这正是"只是把报错从 42804 换成另一个 42804"的典型）：同样的推断也发生在 `UpdateMemoriesAsync`（`valid_from`/`valid_to`/`supersedes_id`/`last_confirmed_at`/`last_used_at`），任务从"卡在 running"变成"明确 failed 且错误里写着 `column "valid_to" is of type text`"——**是新加的超时 + 失败落库把隐藏的错误显形了**。随后把该类语句统一改成实体驱动的 `Updateable(entity).UpdateColumns(...)`（`UpdateMemoriesAsync` / `UpdateSimulationAsync` / `UpdateEvalRunAsync`），并按实测把规律写进架构文档：字面量 `null` 会被正确推断，变量求值为 `null` 才会退化成 `AnsiString`；`Insertable` 与 `UpdateColumns` 一直是安全的。
- 证据（本机 5056，Production，真实 Postgres）：
  - 修复前：`feed -> queued` → 60 秒轮询 `undigested` 恒为 2 → `GET /api/avatar/ingestions` 两条都是 `running/extracting`，日志 `Avatar ingestion worker iteration failed. Npgsql.PostgresException 42804 …`。
  - 修复后（重启触发僵尸回收）：日志 `Requeued 2 stale avatar ingestion tasks.` → 两条立刻跑完，`status=partial / memoryCount=1 / completedAt` 有值。
  - 新投喂走快乐路径：`feed -> queued` → **2 秒内** `memories 4→5、undigested=0`，`avatar_search_memories("科目二补考约在什么时候练车")` 命中「早上六点半」。
  - 改写问法验证：`avatar_search_memories` 用「他十一月考驾照担心哪一项」「阳台装修想装什么」「他最近在研究咖啡怎么冲」分别命中「科目二」「壁灯」「耶加雪菲」。
  - 修复后新内容：`feed -> queued` → 2 秒内 `memories 5→6`，检索「他换了什么电脑，键盘手感如何」命中「雾面」，日志无 42804。
  - 之前 `failed`（`valid_to` 42804）的那条任务 `POST /api/avatar/ingestions/{id}/retry` → `{"retried":true}` → 变成 `partial/completed`、`statsJson {"merged":1}`，证明合并/更新记忆这条路径真的通了。
  - 单测：`AvatarIngestionUpdateCommandTests` 7 项（空时间戳必须是 `DBNull`、有值必须是 `DateTimeOffset`、三列都必须走 `CAST`、覆盖全部可变列、用 `ToSql()` 钉住"SqlSugar 仍会把 null 时间戳推断成 AnsiString"这条事实）、`NullableColumnBindingTests` 5 项（字面量 null / 变量 null / UpdateColumns / Insert / 主键 WHERE 四种绑定对比）。
- 诚实边界：整理质量取决于聊天模型。桩模型返回非 JSON 时会**降级为原文记忆**（`partial` + 「部分内容暂时无法整理，已按原文保存」），链路通但记忆没被提炼——这正是上面证据里 `status=partial` 的原因。

**T-6 聊天历史回读（MCP `chat:read`）**（2026-10-24）
- 动机：审计时发现「聊天历史存了 7 张表，但没有任何 MCP 工具能把原话读回去」。外部 Agent 因此只知道"资料里怎么说"，不知道"用户上次说了什么"——这是"跨 Agent 回忆"里唯一真正缺的一环。
- 落点：`Domain/Chats/ChatHistory.cs`（端口 + 四个来源名）、`Application/Chats/ChatHistoryService.cs`、`Infrastructure/Persistence/SqlSugarChatHistoryStore.cs`（一条 `UNION ALL` 合并三张会话表 + 一张导入记录表）、`Mcp/ChatHistoryMcpTools.cs`。
- 能力刻意单列 `chat:read`：私人对话比资料更敏感，只拿到 `knowledge:read`/`avatar:read` 的客户端不该顺带看见。
- 两个反直觉的取舍：来源名写错**直接报错并列出可用值**（返回空列表会被误读成"用户没聊过"）；LIKE 元字符先转义（不转义时搜 `%` 会捞出全部历史）。
- 证据（本机 5056，Production）：
  - `tools/list` → **24** 个工具，含 `list_chat_sessions`、`chat_search_history`。
  - 未授予 `chat:read`：`chat_search_history` → `-32003`「客户端 'probe-agent' 未获得能力 chat:read」。
  - `PUT /api/agent-clients/ac_…` 加上 `chat:read` → 200 后：`list_chat_sessions` 返回 5 个会话（`source/scopeLabel/messageCount/lastMessagePreview` 齐全）；`chat_search_history("胃镜")` 命中导入记录「下周三我要去医院做个胃镜，说实话有点紧张」；`source=knowledge` + `query=检索` 命中知识库问答；留空 query 返回最近 3 条；`source=memories` 返回可读错误。
  - `query="%"` → 0 条（转义生效，没有变成"全表"）。
  - **顺带修掉一个真实越权面**：`AgentCallGate` 原先对 `UsesKnowledgeBase: false` 的工具直接放行，于是受限客户端只要给 `chat_search_history` 传一个别的 `knowledgeBaseId`，就能读出未授权知识库的问答历史。现在显式传入的知识库 ID 一律对照白名单：把客户端限制在 corpus 库后读默认库历史 → `-32003`（错误信息列出允许的库），读 corpus 库 → 放行，不传库 → 放行（只看分身/伴侣）。
  - 单测：`ChatHistoryTests` 23 项（来源名规范化 9 组、未知名报错、条数夹紧、all 展开、空白 query 退化为最近、trim、转义、片段截取与回退、工具登记与 `chat:read` 不隐含于其它能力）；`AgentClientTests` 新增 2 项（可选知识库参数必须受范围约束、不传则不受限）；全量 **772** 通过。
- 诚实边界：只读、只做词面 `ILIKE`（不走向量），跨"同义改写"的对话检索仍然要靠 `avatar_search_memories` 那条记忆链路。`tools/list` 仍返回全部工具、不按能力裁剪（见 `docs/MCP-ACCESS.md` 的说明）。
