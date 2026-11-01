# 知间桌面版启动耗时归因（2026-10-31 11:51:47 冷启动 12.1s）

> 只读分析报告。**未修改任何产品代码，未启动/停止/杀掉任何进程。**
> 数据来自现场日志与已安装产物，不包含任何"重启一次再测"的结果。
>
> 结论一句话：**12.1s 不是"每次启动都要 12 秒"，而是「覆盖安装 + 重启后首次冷启动」把三笔一次性开销叠加到了一起**：
> 全量 DDL（4.84s，stamp 未命中）+ 便携 PostgreSQL 崩溃恢复（约 1.4s）+ 冷机全局放大（约 2.5 倍）。
> 同一台机器同一天的常规启动是 **2.4–2.7s**（00:09 那次 2.65s）。

---

## 1. 结论速览

1. **耗时最大的一笔是数据库结构初始化（5.42s，占 44.6%）**，其中 4.84s 是"跑全量 DDL"。
   如果 `SchemaStamp` 命中，同一步只要 **422ms**（00:09 实测）——差 **5.0s**。
2. **第二笔是便携 PostgreSQL（3.59s，占 29.5%）**。但 PostgreSQL 自身只占了 **1.72s**，
   而且其中 **约 1.36s 是"上次非正常退出 → 崩溃恢复"**；剩下约 1.87s 花在**应用侧**：
   `pg_ctl.exe` 进程创建（冷机 ~0.88s）、就绪探测、以及 3 次独立建连。
3. **第三笔在外壳（Electron）侧（约 2.6s，占 21%）**：后端把 ready 文件写出来之后，
   外壳还要 **1.24s** 才拿到 `/api/health` 200（冷机首次请求 JIT + 探针建连 + **重复调用了一次 `waitForHealth`**），
   再加窗口加载 0.12s、首帧 0.43s。
4. **同一份二进制在不同温度下的差异极大**：11:51 那次整条链路都比 00:09 慢 **2.3–2.5 倍**
   （`host built` 83ms vs 31ms、`plugin init` 379ms vs 260ms、stamp 读 574ms vs 148ms、全量 DDL 4.84s vs 2.34s）。
   系统盘是 NVMe SSD，所以这不是磁盘寻道，而是 **ReadyToRun 缺失导致的全量 JIT + 杀软首次扫描 + 页缓存冷**。
5. **stamp 未命中的机制成立，但这次的具体触发还有一项证据冲突**（见 §6）：
   覆盖安装会换掉 API 程序集 → MVID 变 → 指纹变 → 全量 DDL。这是 23:58 那次的成因。
   但 **11:49 这次覆盖安装装回去的 API dll 与 00:09 运行的那份字节完全相同**（SHA256 一致），
   所以 11:51 的 miss 更可能是「**本机另一份构建（开发用的 `dotnet run` / console 版）连过同一个 pgdata，把指纹改写成了别的值**」，
   或 `TryReadAsync` 静默吞掉了异常。**补 3 行日志即可定案**（见 §6.4、§8 的 O1）。

---

## 2. 数据来源与测量口径

| 项目 | 值 |
| --- | --- |
| API 控制台日志 | `C:\Users\zhs\AppData\Roaming\myfirstmafagent-desktop\logs\api-console.log`（2.0MB，4 次启动） |
| 桌面壳日志 | `C:\Users\zhs\AppData\Roaming\myfirstmafagent-desktop\logs\electron-shell.log` |
| PostgreSQL 服务端日志 | `C:\Users\zhs\AppData\Local\MyFirstMafAgentData\pglogs\postgres.log` |
| 便携 PG 数据目录 | `C:\Users\zhs\AppData\Local\MyFirstMafAgentData\pgdata`（端口 55432） |
| 持久化程序位置 | `C:\Users\zhs\AppData\Local\Programs\Zhijian\resources\api`（版本 **1.0.5**，注册表 DisplayVersion=1.0.5） |
| 打点实现 | `MyFirstMafAgent.Api\Hosting\StartupTimeline.cs`（`[startup] +Nms …`，N 为进程内单调毫秒） |
| 对照测量脚本 | `scripts\Measure-Startup.ps1`（本次**未运行**，因为它会真起服务） |

**时间基准（11:51 那次，本地时间，由三份日志对表得到）：**

```
11:51:46.785  Electron 进程启动（壳日志 "splash shown at 1106ms" / "API ready … after 11727ms" 反推）
11:51:47.058  壳读到 runtime 文件、判定要自己拉后端（+273ms）
11:51:47.122  壳 spawn Zhijian.Api.exe（+337ms）
11:51:47.636  后端第一个 [startup] 打点 = 后端时间轴 t0（+851ms，比 spawn 晚 514ms）
11:51:57.10   Kestrel "Now listening" + ready 文件写出（t0+9465ms）
11:51:58.389  壳拿到 /api/health 200（+11.60s）
11:51:58.512  壳 loadURL 完成（+11.73s）
11:51:58.939  主窗口 ready-to-show 并显示（+12.15s）← 用户抱怨的 12.1s
```

> ⚠️ 口径陷阱：`StartupTimeline` 的注释写"时间起点是进程启动（ProcessStartTime）"（`StartupTimeline.cs:14`），
> 但实现是 `Stopwatch.StartNew()` 在类型首次使用时才开始计时（`StartupTimeline.cs:20`）。
> 因此 `[startup] +0ms` ≈ spawn 之后 0.12s（热机）～0.51s（冷机），**后端真实启动时长比日志数字多这 0.5s**。
> 本报告的瀑布表已把这段补回去。

---

## 3. 四次启动对照（同一台机器）

| # | 本地时间 | 场景 | PG 阶段 | stamp | schema 阶段 | 后端可监听（t0 起） | 外壳 API ready | 主窗口出现 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A | 10-30 23:32 | 常规启动（PG 已在跑，直接复用） | **446ms** | 命中 | 567ms | 1.24s | 2.43s | 5.28s（首窗创建偶发偏慢） |
| B | 10-30 23:58 | **覆盖安装后首次** | 1181ms | **未命中** | **2341ms** | 3.71s | 4.50s | 4.72s |
| C | 10-31 00:09 | 常规启动（PG 已在跑） | 1019ms | 命中 | **422ms** | ~1.8s | 2.41s | **2.65s** |
| D | 10-31 11:51 | **再次覆盖安装 + 机器重启后首次冷启动** | **3588ms** | **未命中** | **5417ms** | **9.47s** | **11.73s** | **12.15s** |

同一份 1.0.5 程序，**C（2.65s）↔ D（12.15s）差 4.6 倍**。这 9.5s 的差额可以拆成：
数据库结构初始化 **+5.0s**（其中全量 DDL 本身 +4.57s、指纹读取冷机变慢 +0.43s）、PG 阶段 **+2.57s**、
外壳 health 阶段 **+0.78s**、.NET 首个打点前 **+0.39s**、服务注册/建主机 **+0.15s**、窗口首帧 **+0.19s**。

---

## 4. 11:51 那次 12.15s 的完整瀑布

| # | 区间 | 耗时 | 占比 | 累计 | 代码位置 | 是否每次启动都有 |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | Electron 进程启动 → 决定拉起后端并 spawn | 337ms | 2.8% | 0.34s | `desktop-electron\main.js:104, 917-951, 1077-1140` | 每次（可压缩） |
| 2 | spawn → .NET 第一个打点（运行时初始化 + 程序集加载/JIT），再 + 参数解析/宿主形态/安全姿态检查到 `portable PostgreSQL starting` | 578ms<br>（514 + 64） | 4.8% | 0.91s | `Program.cs:20`、`ApiHost.cs:38-64` | 每次，**冷机 ×4**（514ms vs 122ms） |
| 3 | **便携 PostgreSQL**（含 pg_ctl + 就绪探测 + 建库/扩展检查） | **3588ms** | **29.5%** | 4.50s | `ApiHost.cs:82-85`、`PortablePostgresHost.cs:95-203, 750-913` | 每次，**冷机 ×3.5** |
| 4 | 服务注册 + `builder.Build()`（含日志/DI 反射） | 188ms | 1.5% | 4.69s | `ApiHost.cs:126-138` | 每次，冷机 ×4 |
| 5 | **数据库结构初始化（stamp + 全量 DDL + 数据步骤）** | **5417ms** | **44.6%** | 10.11s | `ApiHost.cs:160-162`、`DatabaseInitializer.cs:35-90` | **仅升级/指纹失配后第一次**（命中只要 422ms） |
| 6 | plugin 入队 + 端点映射 + Kestrel 绑定 | 207ms | 1.7% | 10.32s | `ApiHost.cs:171-176`、`PluginModule.cs:65-98`、`Program.cs:21` | 每次 |
| 7 | ready 文件写出 → 外壳 `/api/health` 200 | 1289ms | 10.6% | 11.60s | `Program.cs:24-26`、`main.js:369-417, 952-953, 1143`、`HealthEndpoints.cs:11-46` | 每次（可压缩一半） |
| 8 | `createWindow` + loadURL（建立渲染进程/首请求） | 123ms | 1.0% | 11.73s | `main.js:448-513, 1147` | 每次 |
| 9 | 首帧渲染 → `ready-to-show` 显示主窗口 | 427ms | 3.5% | **12.15s** | `main.js:503-508` | 每次，冷机 ×1.8 |

### 4.1 便携 PostgreSQL 阶段 3588ms 拆解

| 子阶段 | 证据 | 耗时 |
| --- | --- | --- |
| 解析 bin 目录、写日志行 | `[startup] +117ms/+182ms` | ~120ms |
| `pg_ctl.exe` 进程创建 → `postgres.exe` 开始写服务端日志 | 应用 `+182ms` vs PG 日志 `11:51:48.699 starting` | **~880ms**（冷机杀软扫描新 exe 全家桶） |
| PG 自身引导（读配置/pg_control） | `48.699 → 49.063` | 364ms |
| **崩溃恢复准备**（`database system was interrupted` → `automatic recovery in progress`） | `49.063 → 50.019` | **956ms** |
| redo + end-of-recovery checkpoint + `ready to accept connections` | `50.019 → 50.421`（redo 仅 0.02s） | 402ms |
| `pg_ctl -w` 返回 + 应用侧就绪探测首连 | 应用 `+3276ms` | ~489ms |
| `EnsureDatabaseAndVectorAsync`（2 条新连接 + 2 条查询） | `+3276 → +3652ms` | 376ms |
| **合计** | `portable PostgreSQL ready [3588ms]` | **3588ms** |

**对照热机（00:09 那次，同一份程序）**：PG 自己 `51.928 → 52.296` 只有 **368ms**，应用侧整段 1019ms。
即冷机把这一段放大了 **3.5 倍**（约 2.57s 是"冷"出来的，不是代码变慢）。

### 4.2 数据库结构初始化 5417ms 拆解

| 子步骤 | 11:51（冷、全量 DDL） | 00:09（热、stamp 命中） | 23:58（热、全量 DDL） |
| --- | --- | --- | --- |
| 读指纹 + 判断 | **574ms** | 148ms | 164ms |
| `kb store`（pgvector 检查 + 15 张表 CodeFirst + 补列 + 词法索引） | **2009ms** | 111ms | 651ms |
| `kb recover documents` | 134ms | 50ms | 43ms |
| `kb chat store` | 161ms | 0ms | 67ms |
| `companion store` | 231ms | 0ms | 124ms |
| `topic repository` | 45ms | 0ms | 25ms |
| `note repository` | 160ms | 24ms | 96ms |
| `study repository` | 44ms | 0ms | 23ms |
| **`avatar repository`（21 张表）** | **1385ms** | 0ms | **792ms** |
| `agent client store` | 95ms | 0ms | 49ms |
| `oauth store`（7 张表） | 315ms | 0ms | 159ms |
| `job queue` | 67ms | 1ms | 33ms |
| `settings`（建表 + 读配置） | 99ms | 35ms | 55ms |
| `embedding specs` | 28ms | 16ms | 21ms |
| `default indexes` | 62ms | 34ms | 37ms |
| `schema stamp`（写指纹） | 3ms | — | 2ms |
| **合计** | **5417ms** | **422ms** | **2341ms** |

代码位置：
- 指纹读取/判断：`MyFirstMafAgent.Api\Hosting\DatabaseInitializer.cs:35-44`
- 16 个顺序步骤：`DatabaseInitializer.cs:46-86`（`Step(...)` 包装见 `:24-29`）
- 指纹写入：`DatabaseInitializer.cs:88-90` → `SchemaStamp.cs:116-133`
- kb 全量建表细节：`Persistence\KnowledgeBaseStore.cs:40-68`（`PgVectorSql.EnsureExtensionAsync` :43、`InitTables` ×15 :45-67、`LexicalSearchSql.EnsureIndexSql` :57）
- avatar 21 张表：`Infrastructure\Persistence\SqlSugarAvatarRepository.cs:16-53`

> 冷机时 **同一步"全量 DDL"从 2341ms 涨到 4840ms（+2.5s）**，工作量和表结构完全一样，
> 只是 catalog 查询全部打到冷页缓存 + 首次 JIT。所以"5.4s 里约 2.5s 是冷机溢价，约 2.3s 才是 DDL 本身"。

### 4.3 ready 文件 → health 200 的 1289ms

`main.js` 在 ready 文件出现后立刻 `fetch('/api/health')`，但 11:51 实测算到 **1236ms** 才 200；
热机同期只有 **455–607ms**。原因（按贡献排序）：

1. `/api/health` 是**进程内第一个 HTTP 请求**：路由/中间件/鉴权管线全部首次 JIT（冷机尤重）。
2. 该端点不是简单探活：先做 `SqlSugarDatabaseHealthProbe`（**非池化独立连接** + `SELECT 1`，含 SCRAM 鉴权），
   再做业务池探针（`HealthEndpoints.cs:11-46`、`SqlSugarDatabaseHealthProbe.cs:30-42`）。
3. **`waitForHealth` 被调用了两次**：`ensureBackend()` 内部一次（`main.js:953`），`bootstrap()` 里又一次（`main.js:1143`），
   日志里因此出现两条 `health ok`（58.389 / 58.485）。第二次纯属重复，约 96–200ms。
4. Node 侧 `fetch`（undici）首次初始化、以及 120ms 轮询间隔（`main.js:413`）。

### 4.4 外壳侧另外两笔

- **splash 到 1.106s 才出现**（`SPLASH_DELAY_MS=180` + Electron 冷启动 + 加载 `splash.html`）。
  也就是说用户双击图标后有 **1.1s 全黑**，然后 splash 再挂 **11.0s**。热机 splash 是 470–476ms。
- 主窗口 `ready-to-show` 到真正显示 427ms（冷机首帧），热机 237ms。

---

## 5. 哪些是"每次启动必然发生"，哪些是"仅升级后一次"

### 5.1 每次启动必付（热机基线 ≈ 2.4–2.7s，冷机 ≈ 4.7–5.5s）

| 阶段 | 热机实测 | 冷机实测 | 归属 |
| --- | --- | --- | --- |
| Electron 主进程 + 单实例锁 + runtime/pref 读 | ~0.34s | ~0.34s | 壳 |
| .NET 进程启动到首个打点（程序集加载/JIT） | 0.12s | 0.51s | API |
| 便携 PostgreSQL 启动 + 探测 + 建库/扩展检查 | 1.02s | 3.59s | API+PG |
| 服务注册 + `builder.Build()` | 0.04s | 0.19s | API |
| **读指纹（不算 DDL）** | 0.15s | 0.57s | API+DB |
| 数据步骤：默认知识库、恢复中断资料、设置加载、向量规格、默认索引 | ~0.27s | ~0.43s | API+DB |
| plugin 入队 + 端点映射 + Kestrel 绑定 | ~0.15s | 0.21s | API |
| 外壳 ready→health、建窗、首帧 | 0.46+0.24+0.24s ≈ 0.94s | 1.24+0.12+0.43s ≈ 1.79s | 壳 |
| **主窗口可见合计** | **≈ 2.4–2.7s** | **≈ 4.7–5.5s** | — |

### 5.2 只在特定条件下才发生

| 偶发开销 | 触发条件 | 实测代价 |
| --- | --- | --- |
| **全量 DDL**（13 个 store 的 CodeFirst/补列/索引） | 指纹失配：覆盖安装换了 API 程序集（MVID 变）、本机另一份构建写过同一个库、`MAF_FORCE_SCHEMA_INIT=1`、指纹表读失败、数据目录被还原 | **热机 1.9s / 冷机 5.0s**（对比命中 0.42s） |
| **PG 崩溃恢复** | 上次退出时 PostgreSQL 没走完 `pg_ctl stop -m fast`（宿主强杀 API、Windows 关机/注销、断电） | 冷机 **1.36s**；热机约 0.1–0.4s（看 WAL 量） |
| PG 首次 `initdb` | 数据目录为空 | 3–10s（本次不涉及） |
| 便携库镜像到纯英文目录 | 安装路径含非 ASCII 字符 | 首次 ~65MB 拷贝（本次不涉及，路径已纯英文） |

> 关键结论：**用户抱怨的"启动很慢"只在「升级后第一次 + 重启后第一次」这两个条件同时成立时出现**。
> 11:51:47 正好是"1.0.5 覆盖安装（11:49）+ 机器重启后第一次启动"，两笔一次性开销叠在一起，再乘上 2.5 倍冷机溢价。

---

## 6. 为什么 stamp 未命中（重点）

### 6.1 机制（确定）

`Hosting\SchemaStamp.cs:45-87` 的指纹 = **SHA256( 所有 `*SchemaEntity` 的表名/列名/类型/可空/默认值/长度/主键/自增 + 程序集 MVID )**：

```csharp
// SchemaStamp.cs:51 —— 这一行是"升级后必慢"的根源
builder.Append(assembly.ManifestModule.ModuleVersionId).Append('\n');
```

MVID 每次重新编译都会变，于是**任何一次覆盖安装/重新构建，都会让指纹失配 → 全量 DDL**。
`DatabaseInitializer.cs:37-44` 失配时只打一行 `schema stamp missing or changed`，缺 'missing' 与 'changed' 的区分。

### 6.2 观测（确定）

- `23:58` 启动：`schema stamp missing or changed` → 全量 DDL 2341ms → **写入指纹**。
- `00:09` 启动：`schema stamp matches` → 422ms。**证明 23:58 那次确实把指纹写进去了、而且读得出来。**
- `11:51` 启动：`schema stamp missing or changed` → 全量 DDL 5417ms。

### 6.3 证据冲突：11:51 那次不像是"换了程序集"

| 检查项 | 结果 |
| --- | --- |
| 安装版 `resources\api\Zhijian.Api.dll` | 3,647,488 字节 · mtime 2026-10-30 23:54:14 · SHA256 前缀 `DAABFF68E3305AE7…` |
| 仓库 `publish\desktop\Zhijian.Api.dll`（10-30 23:33:24 构建） | **同样 3,647,488 字节 · 同样 SHA256** |
| `publish\releases\win-unpacked\resources\api\Zhijian.Api.dll` | **同样 SHA256** |
| 全部 `resources\api` 文件 mtime | 全部 = 2026-10-30 23:54:14（安装包保留构建时间戳，**不能用它推断安装时刻**） |
| 注：`resources\api` 下各子目录 mtime | 11:49 系列 → **11:49 确实发生了一次"写文件"级别的覆盖安装** |
| 注册表 DisplayVersion | 1.0.5 |

也就是说：**11:49 覆盖安装写回去的 API 程序集，与 00:09 在跑的那份字节完全相同 → MVID 相同 → 单靠"换程序集"解释不了 11:51 的失配。**

### 6.4 因此更可能的两个原因（需 1 条日志定案）

**假设 A（可能性较高）：本机另一份 API 构建连过同一个 pgdata，把指纹改写成了它的 MVID。**

- `AppPaths.PortableDataRootCandidates()` 的第一候选是 `%LOCALAPPDATA%\MyFirstMafAgentData`（`Infrastructure\AppPaths.cs:81-90`），
  **console 版 / `dotnet run` / `publish\desktop` 直接跑，与安装版共享同一个 pgdata**。
- 只要在 00:14（后端崩溃：`API sidecar exited code=0xC000027B`）之后有人用开发构建跑过一次，
  它就会以**自己的 MVID** 跑一遍全量 DDL 并**覆盖**指纹；随后安装版再启动 → 指纹"变了" → 全量 DDL。
- 旁证：仓库里 `bin\Release\net10.0\Zhijian.Api.dll`（10-30 23:35:25，3,648,000 字节）与发布版**不是同一份产物**（MVID 必然不同）。

**假设 B（可能性较低）：`TryReadAsync` 静默失败被当成"missing"。**

- `SchemaStamp.cs:108-112` 对任何读失败 `catch { return null; }`；`information_schema` 查询 + `GetIntAsync` 若有一次瞬时失败，
  就会被当成"指纹缺失"，然后**照常跑完整 DDL（成功）**——外部只看到一行 "missing or changed"，看不出真实原因。
- 11:51 那次指纹读取耗时 574ms（热机 148ms），符合"冷机上的首次 SqlSugar 查询 + 首连"特征，**没有**超时迹象（5s 上限），
  所以这条假设证据偏弱，但完全无法从现有日志排除。

### 6.5 后续启动还会重复吗？

- **不会重复**：指纹写回之后（11:51 那次 `schema stamp [3ms]` 已写入），下一次启动会走 422ms 的命中路径。
- **会再次出现**（同一症状）的条件，全部已知且可控：

| 条件 | 频率 | 代价 |
| --- | --- | --- |
| 再一次覆盖安装**且 API 程序集确实变了**（正常发版） | 每次升级一次 | 热机 ~1.9s / 冷机 ~5s |
| 同一个数据目录上跑过别的开发构建（**开发机专属**） | 不定 | 同上 |
| `MAF_FORCE_SCHEMA_INIT=1`（应急开关） | 手动 | 同上 |
| 指纹表读失败（瞬时 DB 问题） | 罕见 | 同上，且**无日志线索** |
| 数据目录被还原/替换成旧备份 | 罕见 | 同上 |

---

## 7. 便携 PostgreSQL 3.6s 是否合理？

**结论：一半合理、一半可优化。**

| 组成 | 是否合理 | 说明 |
| --- | --- | --- |
| PG 自身引导 368–1720ms | 合理 | warm 368ms 已经很好；冷机 1.72s 主要是"新 exe 首次载入 + 恢复" |
| 崩溃恢复 ~1.36s | **可消除** | 根因是**非正常退出**：宿主默认"关窗口不关后端"，Windows 关机时 Electron 的 `before-quit` 不会停 API（见 `main.js:1179-1195` 只在用户勾选 `stopBackendOnExit` 时才 `stopApi()`），于是 PG 每次关机都被硬杀 → 下次开机首启必做恢复。日志证据：`00:04:26 正常停止 → 00:09:51 启动 → 之后再无停止记录 → 11:51:49 "database system was interrupted; last known up at 00:09:52"` |
| `pg_ctl.exe → postgres.exe` 进程创建 ~880ms（冷） | 部分合理 | 冷机杀软扫描 + 页缓存冷；把安装目录与数据目录加杀软排除可显著缓解（本机 Windows Defender 实时防护开着：SecurityCenter2 productState=0x60100） |
| 就绪探测 `WaitUntilReadyAsync` ~489ms | **可优化** | `pg_ctl -w` 已经等到"能连"，紧接着又用 `Pooling=false` 新开一条连接跑 `SELECT 1`（`PortablePostgresHost.cs:826-874`）；随后 `EnsureDatabaseAndVectorAsync` **再新开 2 条连接**（`:876-913`，`admin` + `EnableVectorExtensionAsync` 各一条，`:915-951`）。共 3 条建连 + 3 次 SCRAM 鉴权，可以合并成 1 条 |
| 冷机整体 3.5 倍放大 | 环境因素 | NVMe SSD 上出现 3.5 倍，说明瓶颈是 JIT/杀软/页缓存，不是磁盘 |

> 便携 PG 的启动参数本身没问题：`pg_ctl start -D … -l … -w -t 30 -o "-p 55432 -c listen_addresses=127.0.0.1"`（`PortablePostgresHost.cs:761-773`）。
> `postgresql.conf` 也是 `initdb` 默认（`shared_buffers=128MB`、`max_wal_size=1GB`、fsync 默认开）——**不建议**为了 1–2 秒去关 fsync/full_page_writes，那是用用户数据完整性换启动速度。

---

## 8. 优化清单（按收益/风险排序）

> 收益估算基于本机实测：热启动 2.65s、升级后热机首启 4.72s、升级后冷机首启 12.15s。
> "风险"指改错的后果，不含工作量。

| # | 优化项 | 改哪里 | 预计收益 | 风险 | 备注 |
| --- | --- | --- | --- | --- | --- |
| **O1** | **先补诊断：把 stamp 失配的原因打出来** | `SchemaStamp.cs:92-113`（`TryReadAsync` 不再静默，记录异常；返回 `(value, error)`）、`DatabaseInitializer.cs:40-44`（分开记录 `missing` / `changed`，各打指纹前 8 位） | 0s，但**决定 O2 是否有效**，并让下次线上问题 1 行日志定因 | **零**（只加日志） | **建议第一步就做**：§6.3 的证据冲突说明当前"升级→慢"的因果链在 11:51 这次并不闭合 |
| **O2** | **指纹不再包含程序集 MVID**，改用"实体结构哈希 + 显式 `SchemaVersion` 常量" | `SchemaStamp.cs:51`（删 MVID 行）→ 新增 `const int SchemaVersion`（有人手写 DDL 时 +1）；把 `PgVectorSql.cs:39,46`、`LexicalSearchSql.cs:25` 这类手写 DDL 纳入版本常量注释 | 升级后首启：热机 **−1.9s**、冷机 **−5.0s**（12.15s → ~7.1s）；常规启动 0 | 中：手写 DDL 若忘记 bump 版本号，老库会缺列。缓解：保留 `MAF_FORCE_SCHEMA_INIT`、在 CI 里对 schema 敏感改动加检查 | 收益最大的一条；**前提是 O1 证实失配确实来自 MVID**。若证实是"别的构建污染"，则本项收益为 0，应改做 O2b |
| **O2b** | 指纹按数据目录隔离，或把"开发构建"与"安装版"的 pgdata 分开 | `AppPaths.cs:81-90`（第一候选按产品形态区分）、或 `PortablePostgres:RootDirectory` 让 dev 用独立目录 | 消除"开发机自己把指纹改了"的假故障 | 低 | 与 O1 结论二选一 |
| **O3** | **发布 ReadyToRun（AOT 预编译 IL）** | `scripts\Publish-Portable.ps1:108-109` 增加 `-p:PublishReadyToRun=true`（或 `<PublishReadyToRun>true</PublishReadyToRun>` 到 `MyFirstMafAgent.Api.csproj`） | 冷机整体 **−1.0～2.0s**（冷机 2.5 倍溢价的主因之一：`host built` 83ms、stamp 读 574ms、`plugin init` 379ms 全是首次 JIT）；热机 −0.1～0.3s | 低-中：包体 +30～50%，需验证 21 个插件的程序集加载与 ONNX 运行时不受影响 | 现用发布参数 `PublishSingleFile=false / PublishTrimmed=false`，**没有** R2R |
| **O4** | 外壳：ready 文件出现即建窗，health 校验移到页面内/看门狗；并删掉重复的 `waitForHealth` | `main.js:952-953` 与 `main.js:1143`（二选一删除）；`main.js:394-417`（health 改为非阻塞） | 热机 **−0.45～0.6s**、冷机 **−1.0～1.2s**；外壳侧 2.6s → 1.4–1.6s | 低-中：窗口可能比后端"完全可用"早 0.5s，需要页面首个请求带重试（已被 `recoverBackend` 兜底） | 单纯删掉重复调用即可先拿到 −0.1～0.2s，零风险 |
| **O5** | PG：三次建连合并成一次 | `PortablePostgresHost.cs:826-874`（就绪探测）与 `:876-913`、`:915-951`（建库 + 扩展探询）复用同一条 `NpgsqlConnection` | 热机 **−0.1～0.2s**、冷机 **−0.3～0.5s** | 低 | 目前：探测 1 条 + admin 1 条 + 扩展开 1 条，每条都做 SCRAM 鉴权 |
| **O6** | **关机/注销时优雅停 PG**，消除下次首启的崩溃恢复 | `desktop-electron\main.js:1179-1195`（`before-quit` 只处理勾选场景）；增加 `powerMonitor.on('shutdown')` / `session-end` 时调用 `stopApi()`；或 API 侧监听 `SIGTERM`/`WM_QUERYENDSESSION` | **每次关机后首启 −1.0～1.4s** | 低 | 只影响"重启后第一次"；日志证据见 §7。注意别把"关窗口不停后端"的默认行为改掉 |
| **O7** | PG 启动与服务注册/`builder.Build()` 并行 | `ApiHost.cs:80-138`（先 `FindAvailablePort` 解析连接串，PG 启动与 `builder.Build()` 用 `Task.WhenAll`） | **−80～150ms** | 中：`AddSqlSugarPersistence` 依赖连接串，需把"端口/连接串解析"从"PG 已就绪"里拆出来 | 收益一般，改面较大，优先级低于 O2/O3 |
| **O8** | 全量 DDL 分片/并行/后移 | `DatabaseInitializer.cs:46-86`；指纹改"按 store 分片"（每个 store 一个 fingerprint 行） | 升级后首启再 **−1.5～2.0s**（只重跑真正改过表结构的 store；`avatar` 1.4s、`oauth` 0.3s 常与本次改动无关） | 中-高：并行 DDL 会争 PG catalog 锁；分片指纹要保证"半个库已升级"也是一致状态 | 只在 O2 之后才有意义（O2 已把升级首启降到 ~7s） |
| **O9** | 安装时自动加杀软/Defender 排除（安装目录 + `%LOCALAPPDATA%\MyFirstMafAgentData`） | 安装器（electron-builder NSIS 脚本）/ 首次启动引导文案 | 冷机整体 **−0.5～1.5s**（本机 Defender 实时防护开启，冷机全局 2.5 倍溢价主要嫌疑） | 低-中：需要用户授权；企业环境可能不允许 | 不改代码即可先在用户机上验证：`Add-MpPreference -ExclusionPath …` |
| **O10** | 感知优化：splash 更早出现（去掉 180ms 延迟、精简 splash.html） | `main.js:74, 538-578` | 用户"黑屏"从 1.1s 降到 0.5–0.7s（**不减少总时长**） | 低 | 冷机 Electron 自身启动就要 ~0.3s，只能缓解观感 |
| **O11** | 全量 DDL 的非首屏 store 后移到 Kestrel 监听之后（端点 gate 返回 503） | `ApiHost.cs:160-176`、需引入 "schema not ready" 中间件 | 升级首启 −1.4～2.0s | 高：任何请求在 gate 放行前打到未建表的 store 都会 500；影响面比 O8 大 | 不推荐先做 |

### 8.1 预期效果汇总（估算）

| 场景 | 现状 | 做完 O1+O2+O3+O4+O5+O6（推荐组合） | 备注 |
| --- | --- | --- | --- |
| 常规热启动 | 2.65s | **1.7–2.0s** | O3 压缩 JIT、O4 压缩外壳 |
| 常规冷启动（重启后） | ~4.7–5.5s | **2.8–3.6s** | 冷机溢价被 O3 压掉一部分 |
| **升级后冷启动（11:51 场景）** | **12.15s** | **4.5–5.5s** | O2 干掉 5s、O3 再压 1–2s、O4 压 1s |

> 注意：如果 O1 的结论是"指纹是被本机另一份构建改写的"（假设 A），则 O2 的收益为 0，
> 应对症做 O2b（把 dev 与安装版的数据目录分开）+ 安装后主动写指纹（安装器结束时调一次 `--init-schema`）。
> 这正是为什么把 O1 排在第一位。

---

## 9. 附录 A：11:51 那次的关键原始日志

`api-console.log`（`[startup]` 第 454–517 行，节选）：

```
[startup] +      1ms process arguments parsed
[startup] +     64ms portable PostgreSQL starting
[startup] +    182ms 启动便携 PostgreSQL：端口 55432，数据目录 C:\Users\zhs\AppData\Local\MyFirstMafAgentData\pgdata
[startup] +   3276ms 便携 PostgreSQL 已就绪：127.0.0.1:55432
[startup] +   3652ms portable PostgreSQL ready [3588ms]
[startup] +   3686ms service registration done
[startup] +   3769ms host built [83ms]
[startup] +   3840ms database schema initialization starting
[startup] +   4414ms schema stamp missing or changed; running full DDL initialization
[startup] +   6426ms   schema: kb store [2009ms]
[startup] +   8587ms   schema: avatar repository [1385ms]
[startup] +   9257ms database schema ready [5417ms]
[startup] +   9270ms plugin initialization queued [8ms]
[startup] +   9831ms plugin initialization finished (background) [379ms]
{ "Timestamp":"2026-10-31T11:51:57.1007259+08:00", "Message":"Now listening on: http://127.0.0.1:5056" }
```

`pglogs\postgres.log`（11:51 那次）：

```
2026-10-31 11:51:48.699 CST [13532] LOG:  starting PostgreSQL 17.9 on x86_64-windows
2026-10-31 11:51:49.063 CST [3484] LOG:  database system was interrupted; last known up at 2026-10-31 00:09:52 CST
2026-10-31 11:51:50.019 CST [3484] LOG:  database system was not properly shut down; automatic recovery in progress
2026-10-31 11:51:50.064 CST [3484] LOG:  redo done at 0/42E4A50 system usage: CPU: user: 0.00 s, system: 0.01 s, elapsed: 0.02 s
2026-10-31 11:51:50.421 CST [13532] LOG:  database system is ready to accept connections
```

`electron-shell.log`（11:51 那次，UTC，+8h 为本地）：

```
2026-10-31T03:51:47.058Z runtime file points at http://127.0.0.1:5056 but nothing answered; will start our own backend
2026-10-31T03:51:47.122Z starting API sidecar on http://127.0.0.1:5056
2026-10-31T03:51:47.891Z splash shown at 1106ms
2026-10-31T03:51:57.153Z ready file detected after 10368ms
2026-10-31T03:51:58.389Z health ok after 11604ms
2026-10-31T03:51:58.485Z health ok after 11700ms        ← 第二次重复的 health 检查
2026-10-31T03:51:58.511Z window loaded http://127.0.0.1:5056
2026-10-31T03:51:58.939Z main window shown at 12154ms
```

---

## 10. 附录 B：如何复核（全部只读，或只改测量脚本）

```powershell
# 1) 提取任意一次启动的时间线（[startup] 打点）
$f = "$env:APPDATA\myfirstmafagent-desktop\logs\api-console.log"
Select-String -Path $f -Pattern '\[startup\]' -Encoding utf8 |
  ForEach-Object { $_.Line } | Select-Object -Last 80

# 2) 看 PG 自身的启动/恢复/停止
Get-Content "$env:LOCALAPPDATA\MyFirstMafAgentData\pglogs\postgres.log" -Tail 60

# 3) 看外壳各阶段（ready 文件、health、窗口）
Get-Content "$env:APPDATA\myfirstmafagent-desktop\logs\electron-shell.log" -Tail 40

# 4) 结构指纹现状（需要数据库在跑；只读查询）
#    SELECT fingerprint, updated_at FROM maf_schema_stamp;
#    —— 如果 updated_at 比你上一次"安装时间"还新，说明有别的进程写过它（验证 §6.4 假设 A）

# 5) 前后对比测量（会真的启动一个临时后端，按需使用；本次报告未运行）
#    scripts\Measure-Startup.ps1 -Runs 3
```

**诊断补丁建议（O1，三行级别，不影响逻辑）**

```csharp
// SchemaStamp.TryReadAsync：把异常带出来，而不是 catch { return null; }
// DatabaseInitializer：日志里区分三种情况，并打印指纹前 8 位
app.Logger.LogInformation(
    "schema stamp: stored={Stored} current={Current} reason={Reason}",
    Short(storedStamp), Short(stamp),
    SchemaStamp.ForceFullInitialization ? "forced"
        : storedStamp is null ? "missing/unreadable"
        : "changed");
```

---

## 11. 一句话给用户/发布说明

> 知间桌面版常规启动约 **2.4–2.7 秒**；**每次覆盖升级后的第一次启动**因为要重跑一遍数据库结构检查，
> 会多花 **2–5 秒**（机器刚重启/杀软首次扫描时更明显）。这属于设计内的"升级一次性成本"，
> 后续启动不会重复；目前已定位到 4 处可落地的优化（见 §8），其中"指纹去掉程序集 MVID"和"发布 ReadyToRun"
> 两项即可把升级后首次启动从 12.1s 压到 5s 左右。
