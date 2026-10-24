# 任务中心（长任务并发）

> 阶段 7 交付。面向两类读者：想排查"资料为什么没解析"的运维，和要接任务中心的**前端/插件开发者**。

## 1. 一句话模型

长任务不是"接口里顺手开个线程"，而是**库里的一行数据 + 一个并发槽位 + 一个处理器**：

```
上传资料（写 kb_documents, status=Queued）
        │
        ▼
补投器（每秒扫 Queued）───幂等键───► jobs 表（status=Queued）
                                          │
                        调度器 N 个槽位按优先级 CAS 领取
                                          │
                                          ▼
                             处理器执行（document.ingest …）
                         进度/心跳写回 jobs，状态写回业务表
```

三条不变量：

1. **任务状态只在库里**。进程重启、任务不会凭空消失，进度条也不会跳回 0。
2. **重复投递安全**。幂等键 + 唯一索引，同一件事不会被做两次。
3. **崩溃可恢复**。租约过期即被回收重跑，不需要人工清理"卡住"的任务。

## 2. 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/jobs?status=&type=&subjectId=&limit=` | 任务列表（默认 50，按优先级/创建时间） |
| GET | `/api/jobs/counts` | 各状态计数（`queued/running/succeeded/failed/cancelled/oldestQueuedAt`） |
| GET | `/api/jobs/types` | 已注册的任务类型（前端下拉框用它，别硬编码） |
| GET | `/api/jobs/quotas` | 按类型的并发上限与当前占用（`limit=null` 表示不限）+ `maxConcurrentJobs` |
| GET | `/api/jobs/{id}` | 单个任务详情 |
| POST | `/api/jobs` | 手动入队（`type` 必填，未注册类型返回 400） |
| POST | `/api/jobs/{id}/cancel` | 取消；已结束返回 409 |
| POST | `/api/jobs/{id}/retry` | 重跑；未结束返回 409 |
| GET | `/api/jobs/stream` | SSE：`snapshot` 首帧 + `job` 变化帧 + `: ping` 保活 |

### 任务对象

```jsonc
{
  "id": "b1f4…",                       // 字符串主键
  "type": "document.ingest",
  "status": "Running",                 // Queued | Running | Succeeded | Failed | Cancelled
  "priority": 0,                       // 0 高 / 10 普通 / 20 低（数字越小越先跑）
  "attempts": 1,                       // 已尝试次数（含本次）
  "maxAttempts": 3,
  "payload": "{\"documentId\":\"…\"}",  // 任务参数（JSON 字符串）
  "result": null,                      // 成功后的结果摘要（JSON 字符串）
  "progress": 42,                      // 0..100
  "progressMessage": "生成向量 42/100",
  "lastError": null,
  "workerId": "w_1234_ab12cd_l0",     // 哪个槽位在做
  "idempotencyKey": "document.ingest:…",
  "knowledgeBaseId": "…",
  "subjectId": "…",                    // 业务对象 ID（资料任务里是 documentId）
  "cancelRequested": false,
  "createdAt": "…", "updatedAt": "…", "nextRunAt": "…",
  "startedAt": "…", "completedAt": null, "leaseUntil": "…"
}
```

### SSE 帧

```
event: snapshot
data: {"counts":{"queued":3,"running":2,…},"jobs":[…]}

event: job
data: {"kind":"progress","jobId":"…","type":"document.ingest","status":"Running","progress":55,"message":"生成向量 55/100","error":null,"occurredAt":"…"}

: ping
```

前端用法：`EventSource('/api/jobs/stream')`，收到 `snapshot` 做全量刷新，收到 `job` 按 `jobId` 局部更新；`EventSource` 自带重连，不需要自己写退避。`kind` 目前有 `job`（新建）/`progress`/`completed`/`failed`。

## 3. 处理器（新增任务类型）

补一个任务类型的完整步骤：

1. **类型常量**：在 `Domain/Jobs/JobModels.cs` 的 `JobTypes` 里加常量（`JobEndpoints` 与前端都靠它）。
2. **处理器**：实现 `Application/Jobs/IJobHandler`，注册为 **scoped**。

   ```csharp
   public sealed class MyJobHandler : IJobHandler
   {
       public string Type => JobTypes.MyJob;

       public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken ct)
       {
           await context.ReportProgressAsync(10, "开始", ct);   // 顺带续租
           // …真正的工作，长任务请把一个 ct 传到底层，取消才有效…
           if (badInput) throw JobExecutionException.NotRetryable("参数错误");  // 不重试
           if (transient) throw new JobExecutionException("下游不可用");        // 可重试
           return "结果摘要";   // 写进 jobs.result
       }
   }
   ```

3. **注册**：`Hosting/JobsModule.cs` 里 `services.AddScoped<IJobHandler, MyJobHandler>();`
4. **可选补投器**：如果任务应由业务状态推导（而不是接口里入队），实现 `IJobFeeder` 并注册为 **singleton**，在 `FeedAsync` 里用幂等键入队。
5. **验证**：`GET /api/jobs/types` 能看到新类型；`POST /api/jobs {"type":"…"}` 能入队并被执行。

### 处理器写法红线

| 必须 | 原因 |
| --- | --- |
| 尊重传入的 `CancellationToken`，一路传到底 | 否则"取消"只是把任务标成取消，实际工作还在跑 |
| 用 `context.ReportProgressAsync` 报真实进度 | 它同时是心跳；不报进度又不做长 IO 的任务可能被误判失联 |
| 幂等 | 崩溃恢复/手动重跑会让同一个任务再跑一次 |
| 失败用 `JobExecutionException` | 其他异常一律按"可重试"处理，可能掩盖真正的 bug |
| 不要自己 `Task.Run` 就返回 | 任务会在工作实际完成前被判成功 |

## 4. 状态机

```
Queued ──claim──► Running ──ok──► Succeeded
  │                  │
  │                  ├──可重试失败──► Queued（退避后重跑，直到 MaxAttempts）
  │                  ├──致命失败────► Failed
  │                  ├──取消────────► Cancelled
  │                  └──停机/崩溃────► 保持 Running，租约过期后被回收
  │
  └──取消──► Cancelled
```

- `Failed`/`Cancelled`/`Succeeded` 都是终态，只能通过 `retry` 回到 `Queued`（`attempts` 归零）。
- 停机**不写失败**：这是刻意的，否则每次重启都会把正在跑的任务变成失败，而它其实只是被中断了。

### 取消到底能停下什么

- **排队中的任务**：立即变为 `Cancelled`，处理器根本不会被调用。这是唯一“确定停住”的情况。
- **运行中的任务**：请求写进 `cancel_requested`，调度器在**下一次心跳（≤ 5 秒）**读到它，取消处理器的 `CancellationToken`。能不能真的停下来取决于处理器：
  - 把令牌一路传到 IO（文件、Http、FFmpeg、Whisper）的处理器会很快中断；
  - 纯 CPU 循环（例如逐块做向量）必须在循环里自己 `ThrowIfCancellationRequested`；
  - 如果工作恰好在取消抵达前做完了，任务会变成 `Succeeded`——这是诚实的结论，不是 bug。
- 被取消的资料会被标为 `Failed`（原因“任务已被取消”），且补投器**不会**自动重跑它；想重跑就显式点“重跑任务”或“重新解析资料”。

## 5. 资料解析任务（`document.ingest`）

- 任务来源：补投器扫 `kb_documents.status = 'Queued'`，幂等键 `document.ingest:{documentId}`。
- 处理器先 `Queued → Processing` 抢占资料；抢不到就判断资料现状并结束（"已在别处处理/已完成"不是失败）。
- 解析进度由既有摄取链路写入资料，处理器每 1.2 秒读一次并回写到任务，所以任务进度与资料进度是同一份数据。
- 失败：还有重试次数就把资料放回 `Queued` 再抛（下一次尝试才有东西可抢）；次数用尽则资料留在 `Failed`。
- 取消：资料标为 `Failed`（原因"任务已被取消"），补投器**不会**自动复活被取消的任务——否则取消等于没取消。
- 在任务中心对 `document.ingest` 点"重跑"，等价于"重新解析这篇资料"：处理器发现资料是 `Failed` 会先放回队列再抢占。

## 6. 配置（`appsettings.json` → `Jobs`）

| 键 | 默认 | 合法区间 | 说明 |
| --- | --- | --- | --- |
| `Enabled` | `true` | bool | 关掉后任务只入队不执行（排查/维护用） |
| `MaxConcurrentJobs` | `4` | 1–32 | 并发度；每个槽位一条数据库连接，别超过连接池 |
| `PollIntervalMs` | `1000` | 200–60000 | 空闲轮询；有任务完成/入队会被立即唤醒，不必等满 |
| `LeaseSeconds` | `120` | 30–3600 | 租约；崩溃后超过它未续租即被回收（另加 `lease/2` 宽限） |
| `MaxAttempts` | `3` | 1–10 | 默认最大尝试次数（含首次） |
| `RetryBaseSeconds` | `10` | 1–3600 | 退避基数（指数增长） |
| `RetryMaxSeconds` | `600` | ≥ `RetryBaseSeconds` | 退避上限 |
| `RetentionHours` | `168` | 1–8760 | 终态任务保留时长，超过即被清理 |
| `MaxQueuedJobs` | `5000` | 100–1000000 | 排队上限；达到后补投停止入队 |
| `FeederIntervalSeconds` | `2` | 0–600 | 补投最小间隔（也是僵尸回收的间隔） |
| `PurgeIntervalMinutes` | `60` | 1–1440 | 清理终态任务的间隔 |
| `TypeLimits` | `{ "plugin.sync": 2 }` | 类型名 → 正整数 | 按类型的并发上限；不配或配 `0` 表示不单独限制。见 §6.1 |

这些区间在启动时由 `ValidateOnStart` 校验，**填错区间直接启动失败**（而不是运行期间才出怪事）。调小 `LeaseSeconds` 做实验时尤其容易踩到 30 秒下限。

调参经验：并发度先按 `min(CPU 核数, 数据库连接池/2)` 给；`LeaseSeconds` 要大于“一次进度上报的最长间隔”，否则健康任务会被误回收。**取消的响应时间与 `LeaseSeconds` 无关**：心跳间隔固定封顶 5 秒（见下），所以租约调到 3600 也不会让取消变慢。

### 心跳与取消延迟

执行期间调度器每 `clamp(LeaseSeconds/3, 1s, 5s)` 续一次租约，并顺便读回取消请求——取消就是靠这条心跳传递的。封顶 5 秒是有意的：如果按租约/3 算，默认 120 秒租约要等 40 秒才响应取消，用户会以为点了没反应。

### 6.1 按类型配额（`Jobs:TypeLimits`）

**为什么需要**：槽位是全局的。10 个 `plugin.sync` 排在最前面会把 4 个槽位占满，之后到达的 `document.ingest`（哪怕 `priority=High`）只能干等——优先级只决定「选谁先跑」，管不了「谁已经占着槽」。给类型设上限，等于给每类任务留出保底槽位。

```jsonc
"Jobs": {
  "MaxConcurrentJobs": 4,
  "TypeLimits": { "plugin.sync": 2, "document.ingest": 4 }
}
```

环境变量写法：`Jobs__TypeLimits__plugin.sync=2`（注意键里的点属于类型名，不是层级分隔符；层级分隔符是双下划线）。

**语义（写清楚，避免误用）**：

| 场景 | 行为 |
| --- | --- |
| 未配 `TypeLimits` | 一次领取所有类型，按 `Priority, NextRunAt, CreatedAt` 排序 → **全局优先级**（与旧版完全一致） |
| 配了 `TypeLimits` | **先占坑再领取**：原子占位成功才去领该类型；占不到就跳过这个类型 → 上限是**硬上限** |
| 已运行的任务 | **不会被抢占**，配额只影响「下一次领取」 |
| 类型内的优先级 | 始终生效（`High` 先于 `Low` 被领走） |
| 跨类型的优先级 | 配了配额时退化为「按类型依次尝试」；要纯全局优先级就别配配额 |
| 计数范围 | **进程内**。多进程部署是「每进程上限」，不是集群上限 |
| 生效时机 | 启动时读取，改完要重启 API |

**怎么验证**：

```powershell
# 当前占用 / 上限（limit=null 表示该类型没有单独限制）
curl.exe -s http://127.0.0.1:5056/api/jobs/quotas
# {"maxConcurrentJobs":4,"quotas":[{"type":"document.ingest","limit":1,"running":0},{"type":"plugin.sync","limit":2,"running":0}]}
```

任务中心页面顶部也会显示「按类型限流：插件同步 0/2 · 资料解析 1/1」，峰值占用一眼可见。

**实现要点（改动前必读）**：占位必须与容量判断**在同一次原子操作里**完成（`ConcurrentDictionary.TryAdd/TryUpdate`）。第一版写成「领取前先看一眼计数」，4 个槽位会同时通过检查后一起去领取，`limit=1` 直接被跑成 4 并发。另外，`finally` 里**只在真正跑完任务时**才 `wake.Notify()`：无条件唤醒会让两个空闲槽位互相唤醒成热循环（实测队列空时每秒上千次领取查询）。

## 7. 排查手册

| 现象 | 先看什么 | 常见原因 |
| --- | --- | --- |
| 任务一直 `Queued` | `GET /api/jobs/counts` 的 `queued`、日志里 `Job scheduler starting with N lane(s)` | `Jobs:Enabled=false`；`MaxConcurrentJobs` 被设为 0（会按 1 处理）；没有为类型注册处理器（启动时会把这些任务判 `Failed` 并写明原因） |
| 任务一直 `Running` 不动 | `worker_id`、`lease_until` | 处理器不报进度又卡在无超时的 IO；或进程已被杀但租约还没到期（要等 `LeaseSeconds + LeaseSeconds/2` 才会被回收重跑） |
| 取消后很快又跑起来了 | `cancelRequested`、资料的 `updated_at` 与任务的 `completed_at` | 只有“任务结束**之后**资料又被重新排队”才会重新武装；取消不会 |
| 取消没有立刻生效 | 处理器是否把 `CancellationToken` 传到底 | 取消请求最多 5 秒才经心跳抵达，之后还要处理器真的检查令牌 |
| 进度不刷新 | 处理器是否调用 `ReportProgressAsync`；业务表是否真的在更新进度 | 处理器把工作丢给了别的线程，自己立刻返回 |
| 取消没反应 | 处理器是否把 `CancellationToken` 传到底 | 只 `await Task.Delay(…, ct)` 而底层 Http/文件 IO 没传 ct |
| 资料一直"排队中"却没人处理 | 该资料的 `jobs` 行状态 | 任务被取消了（资料会被标 `Failed`）；或补投达到 `MaxQueuedJobs` |
| 资料 `Failed` 后重跑没效果 | `POST /api/jobs/{id}/retry` 的返回 | 旧版本：只重跑了任务没重排资料；现已合并为同一动作 |
| 同一篇资料出现两个任务 | `jobs.idempotency_key` 唯一索引是否存在 | 手工写库绕过了 `EnqueueAsync` |
| 重启后任务重复执行 | 处理器是否幂等 | 中断前的工作其实已经做完了一部分，重跑必须能容忍 |

**日志关键字**：`Job scheduler starting`、`Job {id} cancellation requested`、`Job {id} interrupted by shutdown`、`Job {id} has unregistered type`、`Enqueued {n} document ingestion job(s)`、`Purged {n} finished job(s)`。

## 8. 明确没做

- **跨机器分布式调度**：CAS 领取本身支持多进程共用一个库，但没有节点注册/心跳表、没有按机器负载分配。
- **按类型的并发配额**：目前所有类型共享 `MaxConcurrentJobs`；"资料解析最多占 2 个槽位"这类策略还没有。
- **优先级抢占**：高优先级任务只能先被领，不能把正在跑的低优先级任务顶掉。
- **任务依赖 / DAG**：没有 `dependsOn`，需要编排请在上层拆成多个任务自行串联。
- **手动重跑的批量操作**：一次只能重跑一个任务（界面上没有多选）。
- **任务 payload 编辑器**：`POST /api/jobs` 能入队，但界面不提供"手工造任务"表单（避免把内部 payload 格式暴露给普通用户）。
- **插件同步任务化**：插件同步目前仍在插件管理器的循环里跑，还没有走任务队列（因此不能在任务中心取消）。

这些项的排期、依赖与验收标准见 **`docs/ROADMAP.md`**（P1-4 插件级配额、P1-5 按类型配额与抢占、P3-4 分布式执行、P3-6 任务 DAG）。

## 9. 前端任务中心（`JobsView.vue`）

侧栏「任务中心」页。设计原则：**界面只做观察与控制**，不重新实现状态机。

| 区域 | 内容 |
| --- | --- |
| 顶部卡片 | 排队中 / 运行中 / 失败 / 已完成（保留期内）四个计数，来自 `/api/jobs/counts` |
| 筛选 | 状态药丸（带数量）+ 类型下拉（选项来自 `/api/jobs/types`，不硬编码） |
| 列表 | 每行：类型 + 业务对象 ID、状态徽标、进度条、`尝试 n/max` + 执行槽位、创建时间与耗时 |
| 行操作 | 排队/运行中 → 取消；终态 → 重跑（与状态机 `canCancel`/`canRetry` 同一套规则） |
| 详情弹窗 | 点任意行打开：优先级、耗时、七个时间戳、worker、幂等键、payload/result 的格式化 JSON、失败原因 |
| 右下角标 | 侧栏「任务中心」显示 `queued + running`，在本页时由 SSE 驱动实时更新，其它页靠 15 秒轮询 |

**实时更新**：`services/api.ts` 的 `openJobStream()` 封装 `EventSource`。

- `snapshot` 帧 → 覆盖计数，并在**没有筛选**时刷新列表（快照只含最新 20 条，带筛选时会污染结果，所以宁可不动）；
- `job`/`progress` 帧 → 按 `jobId` 就地打补丁（进度条平滑推进，不整表重绘）；
- `completed`/`failed` 帧 → 就地打补丁后再 `GET /api/jobs/{id}` 补一次权威值（`completedAt`/`result`/`attempts` 只有库里有）；
- 连不上时 `jobs-live` 角标变成「轮询中」，改为 5 秒轮询一次；`EventSource` 自己负责重连，这里不写退避。

**筛选与快照的坑**：`snapshot` 不带查询参数，所以「状态=失败」时收到快照只更新计数、不覆盖列表——否则用户会看到列表跳回全部任务。

**取消是异步的**：点击后按钮变灰并显示「取消中…」，最终状态等调度器心跳（默认 ≤5 秒）。界面不会假装任务已经停了——这与后端「取消请求写库、由执行者中断」的设计一致。
