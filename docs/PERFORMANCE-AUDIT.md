# 性能与体积审计（2026-11-01）

本文只记录**实测结论**。凡是"看起来能优化"但缺少证据的，一律不写进"已做"，也不假装做了。

---

## 一、已落地

### 1. 请求级连接兜底中间件

`Hosting/ConnectionReleaseMiddleware.cs`，在 `HostPipeline` 中注册于所有会查库的中间件之前。

**效果实测（同一个生产库，`application_name` 隔离计数）：**

| 场景 | 请求数 | 成功率 | 后端连接数 |
|---|---|---|---|
| 顺序请求 | 150 | 150/150 | 8 → 8 |
| 并发请求（8 并发） | 320 | 320/320 | 8 → 8 |

**结论：请求路径本来就不泄漏。** 这个中间件在这两种压测下**没有触发过任何一次**，所以它的价值不在"修复了一个已观测到的请求级泄漏"，而在于：

1. 兜住**半开事务**这一类泄漏。这一类是**实测复现过的**：连做 10 次「BeginTran 之后既不 Commit 也不 Rollback」，后端连接从 3 条涨到 13 条，一条不落。
2. 一旦真的发生，会留下一条 `请求结束时仍有未提交的事务，已回滚并归还连接：{Method} {Path}` 警告——这是唯一能定位到具体端点的线索。

**有效边界（必须说明）：** `SqlSugarScope` 把客户端缓存在 `AsyncLocal` 中，而 `AsyncLocal` 的值只向下传给子上下文、不向上回传。若端点在自建的子上下文（例如 `Task.Run` 隔离）里新建了连接，本中间件在当前上下文里拿不到同一个对象。实验已确认这一点。所以它是**低成本保险 + 泄漏探针**，不是万灵药；顽固场景仍由 `DatabasePoolWatchdog` 重启兜底。

### 2. 依赖瘦身

| 改动 | 改动量 |
|---|---|
| 移除 `ElBruno.QwenTTS.VoiceCloning`（全仓库零引用） | 3 个 DLL，约 0.17 MB |
| 发布时不再携带 `ffplay.exe` / `ffprobe.exe` | **202.3 MB** |

**发布体积：652.5 MB → 449.5 MB（-203 MB，-31%）。**

依据：`grep` 全仓库确认代码只调用 `ffmpeg`。抽帧用的是 ffmpeg 的 `showinfo` 滤镜取 `pts_time`，因此不需要 `ffprobe`；`ffplay` 是交互式播放器，服务端毫无用处。仓库里仍保留完整三件套，需要时用 `-KeepExtraFfmpegTools` 打回。

冒烟验证：瘦身后的构建启动正常，`/api/health`、`/api/knowledge-bases`、`/api/jobs` 均 200。

### 3. 异步化：消除线程池阻塞

`Plugins/PluginInstaller.cs` 的 `TryMoveDirectory` / `CopyDirectoryWithRetry` 原本用 `Thread.Sleep`（最长 480 ms / 900 ms），而调用它们的方法本身是 `async`。已改为 `await Task.Delay(..., cancellationToken)`，并把取消令牌传进去。

审计范围：全仓库 grep `.Result;` / `.Wait()` / `Thread.Sleep`，共 7 处命中，其中 5 处是业务字段名 `result`（非阻塞调用），真正的阻塞调用只有上述 2 处，均已修复。

---

## 二、评估后判定"空间很小"的项

### 数据库索引

热点表索引现状（`pg_indexes` 实查）：

```
jobs            ix_jobs_status_next_run     btree (status, next_run_at)      ← 队列领取主路径
jobs            ix_jobs_type_status         btree (type, status)
jobs            ix_jobs_created_at          btree (created_at DESC)
jobs            ux_jobs_idempotency_key     btree (idempotency_key)
kb_documents    ix_kb_documents_status_created_at   btree (status, created_at)
kb_documents    ix_kb_documents_knowledge_base_id
kb_chunks       ix_kb_chunks_document_id
kb_chunks       ux_kb_chunks_document_version_chunk_index
kb_chunks       ix_kb_chunks_lexical        gin (to_tsvector(...))          ← 中文词法检索
avatar_ingestions ix_avatar_ingestions_status btree (status, created_at)
```

队列领取（`ClaimAsync`）走 `status + next_run_at + type`，与 `ix_jobs_status_next_run` / `ix_jobs_type_status` 匹配。**没有发现缺失的索引。**

**结论：第 3 项没有可量化收益，不建议为了"做了优化"而加索引。** 真要提速，方向是减少查询次数（N+1）而不是加索引，但这需要先有慢查询日志——目前没有，属于"没有证据就先不动"。

### 日志与监控

已有：每请求一条带 `TraceId` / 方法 / 路径 / 状态码 / 耗时的结构化日志；启动阶段分步打点（`[startup] +Nms ...`）。

本次新增：
- `/api/health` 拆成"数据库活着吗"与"连接池还能用吗"两个判据（`pool: ok | exhausted`）
- `DatabasePoolWatchdog`：池耗尽时记 Critical 并自愈
- 残留事务警告（见上）

**结论：够用。** 再上 OpenTelemetry / Prometheus 属于新增基础设施，不是"优化"，需要单独决策。

---

## 三、没有做的项，以及为什么

### 代码重构（第 6 项）

"模块化设计、函数拆分、减少重复代码"没有可验证的完成标准，也无法在不引入回归风险的前提下批量执行。当前代码已经有明确分层（`Hosting/` 组合根、`Application/` 用例、`Domain/` 领域、`Infrastructure/` 实现、`Endpoints/` 传输），继续重构的边际收益低于风险。

**建议改为具体条目**，一次一个、可验证、可回滚。例如：

1. `HostPipeline.cs` 的中间件顺序目前只能靠注释保证，可以加一条断言测试，防止将来有人插错位置。
2. `PortablePostgresHost.cs` 已超 1000 行，可拆成"生命周期 / 健康检查 / 集群初始化"三块。
3. 17 处 `BeginTranAsync` 可以统一成一个 `UseTransactionAsync` 辅助方法，从结构上消灭"漏 Commit/Rollback"的可能——这一条与连接泄漏直接相关，优先级最高。

### 启动时间

未做优化，也未测量基线。要动它，先要在冷启动路径上打点区分"进程启动 / 依赖注入 / 建表 / 索引加载 / 模型加载"，否则任何改动都是盲改。

---

## 四、下次动手的顺序建议

1. **统一事务辅助方法**（消灭半开事务的来源，与连接泄漏直接相关）
2. **冷启动打点**，拿到基线再谈优化
3. `PortablePostgresHost` 拆分
4. 中间件顺序断言测试
