# 阶段 0：基线与技术验证

本文件记录「Electron.NET、插件平台与知识核心执行方案」阶段 0 的采集口径与验收标准。
阶段 0 的目标不是优化，而是**先有可复测的数字**：没有基线，后续任何“提速”都无法证明有效。

## 1. 必须先确认的现状事实

以下结论来自代码核对，执行时应先用实际数据库与真实运行结果验证，而不是直接采信文档：

| 项目 | 事实 | 验证方式 |
| --- | --- | --- |
| 组合根 | `ApiHost.BuildAsync` 统一承载 API、静态文件、后台服务、MCP 与便携 PostgreSQL；控制台与 Photino 桌面共用 | 阅读 `MyFirstMafAgent.Api/ApiHost.cs` 与 `Hosting/` |
| Worker 隔离 | `BackgroundLoop.RunIsolatedAsync` 是 SqlSugar AsyncLocal 连接不共享的前提 | 真实宿主运行，检索日志中 `NpgsqlOperationInProgressException` 为 0 |
| 向量检索 | 查询为 `ORDER BY embedding <=> ...`（`PgVectorSql.BuildIndexSearchSql`），仓库代码**没有** HNSW/IVFFlat 索引声明 | `scripts/Measure-VectorIndex.ps1` |
| 向量维度 | `kb_index_vectors.embedding` 是无维度 `vector`，允许不同规格共存；ANN 索引要求固定维度 | 探针中的 `vector_dims` 分组结果 |
| Embedding | `LocalOnnxEmbeddingService` 对推理加锁，`IndexLifecycleManager` 逐块生成向量 | 阅读代码 + 索引重建计时 |
| 分层 | Domain/Application 当前无违规引用 | `scripts/Check-Architecture.ps1` |

## 2. 采集矩阵

每个场景记录：**耗时 p50/p95、吞吐、CPU、峰值内存、错误率**。检索类场景额外记录 **Recall@K**。

| 场景 | 采集方式 |
| --- | --- |
| 资料解析（PDF/DOCX/HTML/图片/视频） | 固定样本集，记录上传到 `Ready` 的端到端时间与耗时占比 |
| Embedding（单条 / 批量） | 固定 200 条切片，分别用单条与批量推理计时，记录峰值内存 |
| 向量写入 | 单个索引 1k / 10k / 50k 切片的写入耗时与每秒行数 |
| 索引构建 | `IndexLifecycleManager` 从 `Building` 到 `Ready` 的总时长（按 chunk 批次记录进度） |
| 向量查询 | `scripts/Measure-ApiLatency.ps1` 对检索端点重复调用 |
| 数据库查询计划 | `scripts/Measure-VectorIndex.ps1` 的 `EXPLAIN (ANALYZE, BUFFERS)` |
| 摘要 / 聊天 | 固定提示词，记录首字节与总时长（含模型服务影响） |

### 2.1 API 延迟

```powershell
# 健康检查（基线最低开销）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-ApiLatency.ps1 `
    -BaseUrl http://127.0.0.1:5056 -Path /api/health -Iterations 30

# 检索（把真实请求体存成 JSON）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-ApiLatency.ps1 `
    -BaseUrl http://127.0.0.1:5056 -Path /api/knowledge-bases/search `
    -Method POST -BodyPath .\search-body.json -Iterations 30
```

脚本只做**顺序**测量，用于延迟基线；并发压力测试需要另建脚本，且必须先通过真实宿主验证没有 SqlSugar 连接共享问题。

### 2.2 向量索引体检

```powershell
$env:MYFIRSTMAFAGENT_DB = 'Host=127.0.0.1;Port=5432;Database=knowledge;Username=postgres;Password=***'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Measure-VectorIndex.ps1 `
    -PsqlPath 'D:\PgSQL\bin\psql.exe'
```

输出文件 `vector-index-probe.txt` 需要归档到下面的记录表。判定规则：

- 计划中出现 `Seq Scan on kb_index_vectors` + `Sort`：**精确扫描**，数据量增长后必然线性变慢。
- 计划中出现 `Index Scan using <hnsw|ivfflat>`：ANN 已生效，需要继续对照 Recall@K。
- `vector_dims` 出现多个不同维度：说明存在多个向量规格，ANN 索引必须按维度/规格隔离（部分索引或分区）。

### 2.3 Recall@K 口径

1. 准备 30–50 条真实查询与人工标注的相关切片（或先用精确扫描结果作为 ground truth）。
2. 记录 `exact` 与 `ANN` 两套结果的前 K 个文档 ID。
3. `Recall@K = |ANN ∩ exact| / |exact|`，同时记录 `p50/p95` 延迟。
4. 任何 ANN 索引（HNSW/IVFFlat）都必须**同时**报告速度与召回；只报速度视为无效结论。

## 3. 记录表（执行时填写）

| 场景 | 数据规模 | p50 (ms) | p95 (ms) | 吞吐 | CPU | 峰值内存 | Recall@10 | 备注 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 健康检查 | - | | | | | | - | |
| 向量检索（精确） | | | | | | | 1.00（基准） | |
| 向量检索（ANN） | | | | | | | | 未实现时为 - |
| 索引构建 | | | | chunks/s | | | - | |
| Embedding 单条 | | | | | | | - | |
| Embedding 批量 | | | | | | | - | |
| 资料解析 | | | | | | | - | |

## 4. Electron.NET 技术验证清单

在改动任何发布脚本之前，先用最小样例验证下列项；任一阻断项不通过，就启用“原生 Electron 主进程 + .NET API sidecar”备选方案，保持 REST 契约不变。

### 4.1 已完成的可行性判定（2026-10-22）

| 候选 | 最新版本 | 结论 |
| --- | --- | --- |
| `ElectronNET.Core` | `0.6.0`（预览重写版） | 不作为基线：版本过早期，缺少可验证的 .NET 10 打包路径 |
| `ElectronNET.API` | `23.6.2` | 不作为基线：绑定 Electron 23，与本机 Node/Electron 基线差距过大 |

**决定：启用 sidecar 备选方案**（`desktop-electron/`）。API 保持进程内 Kestrel 随机回环端口，Electron 主进程只负责窗口与子进程生命周期；控制台与 Photino 入口继续可启动，作为回滚路径。

### 4.2 验证清单

- [x] 技术选型结论有公开版本证据（见 4.1）。
- [x] API 仅监听 `127.0.0.1` 的随机端口（`--urls http://127.0.0.1:0`），窗口在 API 可用后创建。
- [x] 本机 API 鉴权：一次性随机令牌经环境变量传入，只走 `X-Api-Token` 请求头；不写入 URL、日志、ready 文件与渲染进程。
- [x] ready 文件契约只含 `url`/`pid`，且为原子替换写入。
- [x] 退出顺序：`POST /api/desktop/shutdown` → 等待 API 退出 → 由 `ApplicationStopping` 停止本次启动的 pg0；外部 PostgreSQL 不受影响。
- [x] Renderer：`contextIsolation=true`、`nodeIntegration=false`、`sandbox=true`、严格 CSP 响应头；外链与 `window.open` 受限。
- [x] 回滚路径保留：控制台入口与 Photino 桌面仍可启动。
- [ ] 真实 Windows 安装环境下的窗口显示、覆盖升级、卸载保留数据（需要安装包验证）。
- [ ] ONNX / Whisper / FFmpeg 原生依赖在 Electron 打包产物中加载（依赖 4.3 的打包验证）。

### 4.3 仍需在真实安装环境验证

1. `npm install && npm start`（`desktop-electron/`）后窗口正常渲染、无控制台 CSP 报错。
2. 安装 → 覆盖升级 → 卸载：`%LOCALAPPDATA%\MyFirstMafAgentData` 中的数据、模型、上传文件保持不变。
3. 升级期间旧版本 API 进程能被正确排空（当前实现为 shutdown + 20s 超时后强制结束）。

自动化契约检查（不需要 Electron）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-DesktopContract.ps1 -ApiExe <published>\MyFirstMafAgent.Api.exe
```

它验证：随机端口、ready 文件不含令牌、无令牌/错误令牌 401、正确令牌 200、`/mcp` 401、静态首页 200、严格 CSP 头、shutdown 后进程自行退出。


## 5. 阶段 0 验收

- [ ] 上述记录表至少有 1 次完整采集，注明硬件、数据规模、模型指纹与时间。
- [ ] 有 `EXPLAIN (ANALYZE, BUFFERS)` 证据说明当前是否走 ANN 索引。
- [ ] 有 Electron.NET 最小闭环截图/日志，或明确记录阻断原因并启用 sidecar 备选方案。
- [ ] `scripts/Check-Architecture.ps1` 通过。

## 6. 何时才考虑更换向量数据库

只有同时满足以下条件，才以 `IIndexVectorStore` 为基础验证 Qdrant 适配器（双写/回放、shadow collection、召回对照、原子切换、回滚）：

1. 已完成批量推理、批量写入与索引构建优化；
2. pgvector 已建 ANN 索引并在目标过滤条件下达到 Recall@K 目标；
3. 查询 p95 仍不满足交互目标，且 `EXPLAIN` 证明瓶颈在向量检索而非 Embedding 或 I/O；
4. 有前后对比数据（吞吐、延迟、Recall、CPU、内存）。

未满足以上条件时，直接迁移向量库属于**没有测量依据的架构变更**。
