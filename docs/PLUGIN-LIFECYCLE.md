# 插件生命周期与安全（阶段 4–5 口径）

本文只写**已经实现并验证**的行为。未实现的项集中在最后一节，避免按未实现的行为开发。

## 1. 两层状态

| 层 | 内容 | 存放位置 | 重启后 |
| --- | --- | --- | --- |
| 用户意图 | 是否启用、配置、已授予权限 | 数据库 `plugin_installations` | 保留 |
| 文件布局 | 已安装哪些版本、当前生效版本 | `plugins/{id}/versions/*` + `active.json` | 保留 |
| 运行时 | 进程、健康、最后错误 | 内存 | 重新发现 |

"装了什么"由目录决定，"希望它怎样"由数据库决定，二者不再混在一起。

## 2. 目录布局

```
plugins/
  com.example.notes/
    active.json                 {"version":"1.0.1","previous":"1.0.0"}
    versions/
      1.0.1/                    manifest.json + 入口文件（一个完整包）
      1.0.0/
  .data/com.example.notes/      插件唯一可写目录（MYFIRSTMAFAGENT_PLUGIN_DATA）
  .staging/                     安装临时目录，成功/失败都会清理
```

- 升级与回滚都是"改 `active.json` + 重启进程"，不原地覆盖文件——Windows 上运行中的 exe 删不掉。
- 阶段 3 的扁平布局（`plugins/{id}/manifest.json` 直接放包内容）仍然可读，`ActiveVersion` 为 `null`，`InstalledVersions` 为空。
- `Plugins:KeepVersions`（默认 3）控制保留的历史版本数，当前生效版本永不删除。

## 3. 启动四道闸门

`PluginManager.StartSafelyAsync` 顺序固定，任一不满足都不会拉起进程：

| 顺序 | 闸门 | 不满足时的状态 |
| --- | --- | --- |
| 1 | 用户启用（`enabled` 且不在 `Plugins:EnabledPluginIds` 之外） | `Disabled` |
| 2 | 完整性：摘要/签名符合 `Plugins:IntegrityPolicy` | `Failed`（并记 `integrity-rejected` 审计） |
| 3 | manifest `config.required` 中的字段都已填写 | `NeedsConfiguration` |
| 4 | manifest 声明的权限都已授予（或 `AutoGrantPermissions=true`） | `PermissionRequired` |

启动成功后握手并下发 `plugin/configure`；插件拒绝配置视为启动失败——半个配置的插件比不启动更危险。

## 4. 完整性校验

策略 `Plugins:IntegrityPolicy`：

| 值 | 行为 |
| --- | --- |
| `off` | 完全不校验（不计算摘要） |
| `warn`（默认） | 有问题只写警告，仍允许启动/安装 |
| `enforce` | 必须提供 `sha256` 与 `signature`，任一不符即拒绝 |

**摘要算法（插件作者必须照做）**：

1. 取包目录下所有普通文件，**排除** `manifest.json`、`active.json` 与任何以 `.` 开头的目录（`.data`、`.staging`）。
2. 按相对路径（`/` 分隔、大小写敏感、Ordinal 排序）排序。
3. 依次写入：UTF-8 相对路径、`\0`、UTF-8 十进制字节数、`\0`、文件内容。
4. 取 SHA-256，小写十六进制。

`manifest.json` 不参与摘要（摘要写在它里面，包含它会自引用）；manifest 本身的身份由签名与运行期握手校验（id/version/能力）共同保证。

**签名**：`integrity.signature` 是对上述十六进制**字符串的 UTF-8 字节**做 SHA-256 签名，Base64 编码。支持：

- `ecdsa-p256`（P1363 与 DER 两种编码都接受，openssl 默认 DER）
- `rsa`（PKCS#1 v1.5）

受信任公钥来自配置：

```json
"Plugins": {
  "IntegrityPolicy": "enforce",
  "TrustedPublisherKeys": [
    { "PluginId": "com.example", "Algorithm": "ecdsa-p256", "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n..." }
  ]
}
```

`PluginId` 按域名段匹配：`com.example` 覆盖 `com.example.notes`，但不覆盖 `com.exampleevil.notes`；多个匹配取最长前缀。

## 5. 权限

- manifest 的 `capabilities[].permissions` 是**声明**；实际可用集合是"声明 ∩ 已授予"。
- 默认 `AutoGrantPermissions=false`：声明了 `network`/`file-read`/`file-write`/`clipboard`/`process` 的插件会停在 `PermissionRequired`，必须显式授权。
- 授权只能授予插件声明过的权限；授予未声明的权限会被拒绝（防止后续升级偷偷提权）。
- 权限闸门本身是**准入控制**（决定给不给你启动），真正的强制来自**内核级隔离**：Windows 上插件跑在 AppContainer 里，没授 `network` 就没有出站（详见 §8.1）。两者是两层，缺一不可。

## 6. 副作用与确认

| sideEffect | 需要确认 | 是否桥接给模型 |
| --- | --- | --- |
| `read` | 否 | 是 |
| `write-local` | 否 | 是 |
| `write-external` | 是 | **否** |
| `destructive` | 是 | **否** |

`Plugins:RequireConfirmationForSideEffects=true`（默认）时：
- 需要确认的能力不会出现在 `/api/agent/tools` 里——模型无法提供确认信息，暴露它等于绕过确认。
- 直接调用必须带 `"confirm": true`，否则返回 403。

`platform-connector` 永远不桥接给模型，只能经 `/api/plugins/{id}/sync` 使用；`/sync` 是用户主动触发的管理动作，因此按「已确认」处理，但摄取结果仍然必须走核心摄取用例。

## 7. 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/plugins` | 全部插件：状态、版本、健康、已授予/缺失权限、完整性、是否已配置 |
| GET | `/api/plugins/{id}` | 单个插件（含 manifest 声明的能力详情） |
| POST | `/api/plugins/{id}/health` | 主动健康检查 |
| POST | `/api/plugins/{id}/invoke` | `{ capabilityId, arguments, confirm? }` |
| POST | `/api/plugins/{id}/sync` | `{ capabilityId, knowledgeBaseId?, cursor? }`；未传 `cursor` 时用持久化游标 |
| GET | `/api/plugins/{id}/items?knowledgeBaseId=&limit=` | 平台条目映射（含墓碑状态与对应资料 ID） |
| GET | `/api/plugins/{id}/sync-state` | 每个 `插件×能力×知识库` 的游标与上次同步结果 |
| POST | `/api/plugins/{id}/enabled` | `{ enabled }`，停用会停进程，启用会重走四道闸门 |
| PUT | `/api/plugins/{id}/config` | `{ config: {...} }`，按 manifest 的 JSON Schema 校验后重启；缺 `config` 字段返回 400 |
| PUT | `/api/plugins/{id}/permissions` | `{ permissions: ["network"] }`，完整集合而非增量 |
| PUT | `/api/plugins/{id}/scope` | `{ allowedKnowledgeBaseIds: [], maxDocuments: 0 }`，空数组=不限制，0=跟随全局默认 |
| POST | `/api/plugins/{id}/restart` | 手动重启 |
| POST | `/api/plugins/install` | `multipart/form-data` 上传 ZIP，安装后立刻尝试启动 |
| POST | `/api/plugins/{id}/rollback` | 切回 `active.json` 的 `previous`，启动失败自动切回 |
| DELETE | `/api/plugins/{id}` | 卸载：停进程、删版本与数据目录、删安装记录 |
| GET | `/api/plugins/{id}/audit?limit=` | 单个插件审计（时间倒序） |
| GET | `/api/plugins/audit?pluginId=&limit=` | 全局审计 |
| GET | `/api/plugins/concurrency` | 每个插件的并发占用：`concurrencyLimit` / `running` / `queued` / `queueLimit` + 能力级明细 |
| POST | `/api/plugins/rescan` | 重新扫描插件目录：新拷进来的目录被发现并启动，已删除的目录被清理。返回 `discovered` / `started` / `failed` / `removed` |

## 8. 审计

表 `plugin_audit_events`，动作：`install`、`uninstall`、`rollback`、`enable`、`disable`、`start`、`stop`、`configure`、`grant`、`scope`、`invoke`、`sync`、`health`、`integrity-rejected`。

- 记录：时间、插件、动作、能力、发起者（`api` / `agent` / `system`）、结果（`success` / `failure` / `denied` / `cancelled`）、耗时、说明。
- 入参摘要会自动**脱敏**（键名含 key/token/secret/password/credential/authorization/cookie 一律替换为 `***`），并截断到 2000 字符。
- 审计写入失败只写日志，**不**阻断业务调用；卸载不会删除历史审计（审计是追加式记录）。

## 9. 配置项

```json
"Plugins": {
  "Enabled": true,
  "RootPath": "App_Data/plugins",
  "EnabledPluginIds": [],
  "StartupTimeoutSeconds": 30,
  "MaxInvocationTimeoutMs": 120000,
  "HealthIntervalSeconds": 30,
  "EnableToolBridge": true,
  "RestartOnCrash": true,
  "MaxRestartBackoffSeconds": 60,
  "MaxIngestionItemsPerBatch": 200,
  "DefaultMaxDocumentsPerPlugin": 5000,
  "IntegrityPolicy": "warn",
  "TrustedPublisherKeys": [],
  "AutoGrantPermissions": false,
  "RequireConfirmationForSideEffects": true,
  "KeepVersions": 3,
  "MaxPackageBytes": 268435456,
  "DrainTimeoutSeconds": 15,
  "ScheduledSyncEnabled": true,
  "MinScheduledSyncIntervalSeconds": 60,
  "MaxQueuedInvocationsPerPlugin": 16,
  "MaxQueuedInvocationsPerCapability": 8,
  "CapabilityConcurrencyLimits": {},
  "StartupConcurrency": 4,
  "Isolation": {
    "Mode": "require",
    "ReadableRoots": [],
    "WritableRoots": [],
    "ContainerNamePrefix": "MyFirstMafAgent.Plugin",
    "DeleteProfileOnUninstall": false,
    "RedirectUserDirectories": true
  }
}
```

### 8.1 隔离配置（D-1）

| 键 | 含义 | 默认 |
| --- | --- | --- |
| `Mode` | `require` 隔离失败就拒绍启动；`warn` 降级为普通子进程并记日志；`off` 不隔离 | Windows 上缺省 `require`，其它平台缺省 `warn` |
| `ReadableRoots` | 额外只读目录（比如共享的运行时）；该目录会对容器 SID 授 `(OI)(CI)(RX)` | `[]` |
| `WritableRoots` | 额外可写目录；授 `(OI)(CI)(M)` | `[]` |
| `ContainerNamePrefix` | AppContainer 配置名前缀，同一插件的容器名在重启后保持不变（SID 不变，ACL 不用重写） | `MyFirstMafAgent.Plugin` |
| `DeleteProfileOnUninstall` | 卸载插件时是否删掉 AppContainer 配置 | `false` |
| `RedirectUserDirectories` | 是否把 `USERPROFILE`/`TEMP`/`APPDATA`/`LOCALAPPDATA` 指向插件数据目录 | `true` |

两个只靠读代码看不出来的行为：

- **不会给祖先目录写 ACL**。容器凭插件目录上的继承 ACE 就能穿越整条路径；给 `C:\` 写 ACE 会让 Windows 在整个驱动器上递归重整继承（实测卡死数分钟），且普通用户本来也无权改 `C:\` 的 ACL。
- **已授过的目录不会重复写**。`SetAccessControl` 每次都会触发继承重整，插件重启时等于把成本乘上启动次数。

运行时固定放 `App_Data/plugins/.runtimes/{命令名}/`：解析 `"command": "node"` 这类裸命令时**优先 `.runtimes`、其次 `PATH`**，因为 `PATH` 上的 `C:\Program Files\nodejs\node.exe` 对容器是拒绝访问的。

## 9.1 插件数量：没有上限，但启动要限流

**扫描不截断、列表不分页、注册表不设配额**，所以插件数量本身没有上限。唯一被收敛的是「同时 spawn 多少个进程」（`Plugins:StartupConcurrency`，默认 4）：

- 启动阶段改成 `Parallel.ForEachAsync` + `MaxDegreeOfParallelism`。原来是 `foreach ... await` 串行，每插件握手最长 30s，50 个插件最坏就是 25 分钟启动时间。
- 每个插件都是独立子进程，进程数是真正的成本，所以是“并发拉起数”而不是“数量上限”。

**热发现（`POST /api/plugins/rescan`）**：把插件文件夹拷进 `Plugins:RootPath` 后点一下「重新扫描」就能用，不必重启 API（重启会中断正在跑的解析任务与对话）。行为：

| 情况 | 结果 |
| --- | --- |
| 新目录 | 校验（四道闸门）后启动，进 `started` |
| 已在运行且目录未变 | 不动它，避免打断在途调用 |
| 校验/授权/配置未过 | 进 `failed`，`lastError` 写明原因 |
| 目录已删（且进程已停） | 停进程、清计数、从列表移除，进 `removed` |

两个实测得到的约束：

- **用户意图优先于扫描**：上一次停用过的插件，即使目录仍在，rescan 也不会把它拉起来（走闸门 1 变 `Disabled`）。这是刻意的。
- **Windows 上正在运行的插件目录删不掉**（exe/dll 被锁）。要删先停用，让进程退出；这不是 bug，是文件锁的必然结果。

`CapabilityConcurrencyLimits` 的键两种写法：`"<pluginId>/<capabilityId>"`（只对某个插件）或 `"<capabilityId>"`（对所有同名能力生效）。
能力级上限只是**再切一刀**，不会把插件级上限放大。配了 `<pluginId>/<capabilityId>` 就不再匹配同名能力的通配项。

## 10. 验证

```powershell
# 单元测试（391 项）
dotnet test MyFirstMafAgent.Api.Tests\MyFirstMafAgent.Api.Tests.csproj -p:UseAppHost=false -p:OutputPath=C:\Temp\maftests\

# 协议契约（真实子进程，18 项）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-PluginContract.ps1
```

手工端到端（已实测通过）：

```powershell
$b='http://127.0.0.1:5056'
# 1. 安装 → PermissionRequired（示例插件声明了 network）
curl.exe -s -F "file=@C:\Temp\pluginpkg\sample-1.0.0.zip" "$b/api/plugins/install"
# 2. 授权 → Running
curl.exe -s -X PUT -H "Content-Type: application/json" -d '{\"permissions\":[\"network\"]}' "$b/api/plugins/com.example.sample-platform/permissions"
# 3. 配置校验：类型错误 / 未知字段都会被 400 拒绝
curl.exe -s -X PUT -H "Content-Type: application/json" -d '{\"config\":{\"workspace\":123}}' "$b/api/plugins/com.example.sample-platform/config"
# 4. 升级 → active 切换，previous 保留
curl.exe -s -F "file=@C:\Temp\pluginpkg\sample-1.0.1.zip" "$b/api/plugins/install"
# 5. 回滚 → 1.0.1 → 1.0.0
curl.exe -s -X POST "$b/api/plugins/com.example.sample-platform/rollback"
# 6. 停用 → invoke 返回 403；启用 → 配置自动重放
curl.exe -s -X POST -H "Content-Type: application/json" -d '{\"enabled\":false}' "$b/api/plugins/com.example.sample-platform/enabled"
# 7. 审计
curl.exe -s "$b/api/plugins/com.example.sample-platform/audit?limit=10"
# 8. 卸载
curl.exe -s -X DELETE "$b/api/plugins/com.example.sample-platform"
```

阶段 5 手工端到端（已实测通过，数据库真实落盘）：

```powershell
$b='http://127.0.0.1:5056'
$kbA='<知识库 A 的 GUID>'; $kbB='<知识库 B 的 GUID>'
# 1. 首次同步：2 条平台笔记 → 2 篇资料（created=2，nextCursor=done）
curl.exe -s -X POST -H "Content-Type: application/json" -d "{\"capabilityId\":\"platform.notes.sync\",\"knowledgeBaseId\":\"$kbA\"}" "$b/api/plugins/com.example.sample-platform/sync"
# 2. 再同步一次：游标复用，0 条（cursorApplied=done，nextCursor=null）
curl.exe -s -X POST -H "Content-Type: application/json" -d "{\"capabilityId\":\"platform.notes.sync\",\"knowledgeBaseId\":\"$kbA\",\"cursor\":\"restart\"}" "$b/api/plugins/com.example.sample-platform/sync"
# 3. 全量重放：内容未变 → skipped=2，资料数仍是 2（幂等）
# 4. 改配置 contentVersion=2 → 插件重启 → 全量重放 updated=2，资料 ID 不变
curl.exe -s -X PUT -H "Content-Type: application/json" -d "{\"config\":{\"workspace\":\"team-alpha\",\"contentVersion\":\"2\"}}" "$b/api/plugins/com.example.sample-platform/config"
# 5. 撤回 note-1002 → retracted=1、skipped=1，资料被真删，映射留墓碑
curl.exe -s -X PUT -H "Content-Type: application/json" -d "{\"config\":{\"workspace\":\"team-alpha\",\"contentVersion\":\"2\",\"retractNote\":\"note-1002\"}}" "$b/api/plugins/com.example.sample-platform/config"
curl.exe -s "$b/api/plugins/com.example.sample-platform/items"
curl.exe -s "$b/api/plugins/com.example.sample-platform/sync-state"
# 6. 越界写入：作用域只允许 B，却同步到 A → 403 且 code=-32003
curl.exe -s -X PUT -H "Content-Type: application/json" -d "{\"allowedKnowledgeBaseIds\":[\"$kbB\"],\"maxDocuments\":0}" "$b/api/plugins/com.example.sample-platform/scope"
# 7. 配额：maxDocuments=1 → created=1、quotaRejected=1、status=partial
curl.exe -s -X PUT -H "Content-Type: application/json" -d "{\"allowedKnowledgeBaseIds\":[\"$kbB\"],\"maxDocuments\":1}" "$b/api/plugins/com.example.sample-platform/scope"
```

## 11. 平台条目映射与作用域（阶段 5）

### 11.1 幂等：靠映射表，不靠插件自觉

表 `plugin_platform_items`，键 = **`plugin_id + source + external_id + knowledge_base_id`**。

- 键里带知识库：同一条平台内容镜像到两个知识库时，两边的映射、配额与撤回互不影响。若键不带知识库，B 库的撤回会把 A 库的资料一起删掉。
- 判定表（纯函数 `PlatformItemDecision.Decide`，可脱离数据库穷举）：

| 本地映射 | 本次条目 | 动作 |
| --- | --- | --- |
| 无 | 正常 | `Create` |
| 无 | 已撤回 | `Tombstone`（只记墓碑） |
| active + 同哈希 | 正常 | `Skip` |
| active + 异哈希 | 正常 | `Update`（替换源文件，资料 ID 不变） |
| active | 已撤回 | `Retract`（删资料 + 置墓碑） |
| 墓碑 | 已撤回 | `Skip` |
| 墓碑 | 正常 | `Create`（视为平台重新发布） |

- 哈希：平台给了就用平台值（统一小写、去首尾空白），没给就按内容算 SHA-256。
- 资料被用户手工删除时，`Update` 会退化为「重新落库」，仍记为 `updated`，且**同样受配额约束**。

### 11.2 撤回是删除，墓碑防复活

撤回不删除映射行，而是 `state=retracted` + `document_id=null`：

- 资料是真删（`DeleteAsync`），不是软删，索引与切片由核心摄取用例清理。
- 墓碑让**迟到的旧批次**无法把已删除内容复活：旧批次里的同一条目会命中 `墓碑 + 正常 → Create` 才会复活，若旧批次本身仍是撤回态则 `Skip`。
- 平台从未给过、却收到撤回的条目记 `Tombstone`，只写一行墓碑，不产生资料。

### 11.3 同步游标：增量真的增量

表 `plugin_sync_states`，主键 `scope_key = pluginId|capabilityId|knowledgeBaseId`。

- 请求不传 `cursor` → 用持久化游标；传了就以此为准（`cursorApplied` 回显）。
- `nextCursor` 落库；`nextSyncAfterSeconds` 供调度方退避。
- 结果状态：`success`（无失败）、`partial`（有拒绝/无效）、`failed`（能力调用本身失败，**保留旧游标**）。
- 批量上限 `MaxIngestionItemsPerBatch`，防止一次同步把库撑爆。

### 11.4 知识库作用域（双端）

- **插件端**：`PluginRegistration.AllowedKnowledgeBaseIds`，空数组 = 不限制。`PluginScopePolicy` 校验：拒绝 `Guid.Empty`、拒绝不存在的知识库、自动去重、拒绝负数配额。
- 越界写入返回 403 + `-32003`，文案带「允许的知识库」清单；失败也会记入 `sync-state`（`lastStatus=failed`），旧游标不丢。
- **Agent 端**：`AgentKnowledgeScope` 让被限定在某个知识库的对话不能读写其他库——`list_knowledge_bases` 只列可见库，`list_documents` / `search_knowledge_base` / `read_document_content` 越界时返回可读的拒绝说明而不是静默空结果。

### 11.5 配额

- 每条映射占 1 个名额，**按知识库**统计（`CountActiveAsync`）。
- 插件级 `maxDocuments` 优先；为 0 时跟随全局 `DefaultMaxDocumentsPerPlugin`（0 = 不限制）。
- 超额条目计入 `quotaRejected` 并给诊断文案，不影响同批次其他条目；有条目被拒时同步状态为 `partial`。

### 11.6 新增文件

| 文件 | 职责 |
| --- | --- |
| `Plugins/PlatformItemDecision.cs` | 判定矩阵 + 哈希规范化（纯函数） |
| `Plugins/IPluginContentStore.cs`、`Plugins/KnowledgePluginContentStore.cs` | 摄取端口与其核心适配器（创建/替换/删除） |
| `Application/Plugins/IPluginItemMap.cs` | 映射、游标端口与状态常量 |
| `Application/Plugins/PluginScopePolicy.cs` | 作用域参数校验 |
| `Agents/Tooling/AgentKnowledgeScope.cs` | 对话级知识库作用域 |
| `Persistence/PluginMappingSchemaEntities.cs` | 映射表与游标表实体 |

## 12. 排空升级、定时同步与受信任公钥（阶段 8）

### 12.1 升级：先排空，再换进程

插件是独立 exe，Windows 上运行中的文件删不掉，所以升级必然要停旧进程。直接杀进程会让正在跑的同步/工具调用对调用方变成一次无解释的"连接已断开"，因此升级顺序固定为：

```
Stage（只读校验，失败则插件继续正常服务）
  → 同 ID 已加载？→ StopHostAsync（排空 + 停进程）
  → InstallFromDirectoryAsync（落盘 + 切 active.json）
  → ReloadAndStartAsync（起新进程）
```

- **排空语义**：`PluginProcessHost.BeginDrain()` 把状态置为 `Draining`，此后 `InvokeAsync` 立即抛 `ErrorUnhealthy`（HTTP 503）而不是排队——排队会让调用方先卡住、再收到断开。
- **排空态不允许被“随手重启”**：`PluginManager.EnsureStartedAsync` 对 `Draining` 直接报 503。否则一次排空中的调用会谎报“重启”，把刚腾出来的进程又拉回来（文件被占用，升级必失败），排空也就变成了“排空了但还能调用”。
- **目录被占用要容忍**：刚被杀掉的子进程在 Windows 上会短暂占着版本目录里的 dll（Move 报 Access denied、Copy 报 being used by another process）。安装器对移动重试 4 次（≈840ms）、对复制重试 3 次（≈1.2s），仍挪不动则退化为“原地覆盖 + 警告”，宁可留下多余文件，也不要让“从 v1.0.1 装回 v1.0.0”永远失败。
- **在途调用等它做完**：`DrainAsync(timeout)` 轮询 `InFlightInvocations`，最多等 `Plugins:DrainTimeoutSeconds`（默认 15 秒）。超时打印警告并继续强停：一次卡死的调用不该让插件永远升不了级，结果写进审计（`outcome=failure`）。
- **两段式安装的收益**：坏包（manifest 错、入口缺失、摘要不符）在 `Stage` 阶段就失败，此时**插件仍在正常服务**；只有校验通过才可能走到"停进程"。
- **手工软下线**：`POST /api/plugins/{id}/drain` 返回 `{ drained, inFlight }`，可用于运维脚本"先摘流量、等调用结束、再替换"。
- 审计：升级那条 install 记录带 `(drain upgrade)` 后缀，便于与首装区分。

### 12.2 定时同步：插件说"过一会儿再来"，核心负责兑现

插件在批次里返回 `nextSyncAfterSeconds` 表示希望下次同步的时间。核心把它写进 `plugin_sync_states.next_sync_after_seconds`，由 `PluginSyncJobFeeder` 按间隔入队 `plugin.sync` 任务（不阻塞任何请求）。

| 规则 | 行为 |
| --- | --- |
| 幂等键 | `plugin.sync:{pluginId}\|{capabilityId}\|{knowledgeBaseId}`，同一作用域同时只有一个任务 |
| 间隔夹最小值 | `max(30, Plugins:MinScheduledSyncIntervalSeconds)`，插件写错单位（返回 1）不会把队列刷爆 |
| 终态重新武装 | 任务结束后**再过了一个间隔**才 `RetryAsync`，失败的插件只按自己声明的节奏重试 |
| 停用即跳过 | `IsScheduledSyncEligible` 只对"已启动且未被用户停用"的插件返回 true |
| 总开关 | `Plugins:ScheduledSyncEnabled=false` 时既不写间隔也不入队，同步只能手动触发 |

三个入口共用 `PluginSyncService` 一份逻辑：`POST /{id}/sync`（阻塞返回结果）、`POST /{id}/sync-async`（进任务中心，可看进度/取消）、补投器（定时）。**失败不动游标**：一次瞬时故障不能让整库重放；**部分条目失败仍推进游标**：失败项的内容哈希没变，下次同步会重新判定，否则一片坏数据会让同步永久停摆。

### 12.3 受信任公钥：谁的签名算数

签名只有在"公钥可信"时才有意义，而公钥的来源必须能在运行期变更（桌面应用的 `appsettings` 改不了就要重启）。

| 来源 | KeyId | 优先级 | 可否经接口删除 |
| --- | --- | --- | --- |
| `Plugins:TrustedPublisherKeys` | `config:{pluginId}` | **高**（运维可压过临时授权） | 否（400，请改配置文件） |
| `plugin_trusted_keys` 表 | 任意字符串 | 低 | 是（`DELETE` → 204/404） |

- `PUT /api/plugins/trusted-keys/{keyId}` 写入后**立即 `ReloadAsync`**，新发布者的签名马上生效，无需重启。
- 写入校验：`algorithm` 只能是 `ecdsa`/`rsa`；PEM 必须能被 `ImportFromPem` 解析；**含 `PRIVATE KEY` 的一律拒绝**——谁能写库谁就能自签，签名就失去意义。
- 匹配规则：按域名段前缀，取最长匹配（`com.example` 覆盖 `com.example.notes`，不覆盖 `com.example2`）；大小写不敏感。
- 指纹 = `SHA-256(规范化 PEM)` 前 16 位十六进制，用于人工核对，**不是**安全边界。
- 数据库不可用时降级为"只用配置项"，不会把已经信任的发布者一起作废。

### 12.4 新增文件

| 文件 | 职责 |
| --- | --- |
| `Plugins/IPluginRuntime.cs` | 同步用例需要的最小运行时端口（调用能力 + 是否参与定时同步） |
| `Plugins/PluginSyncService.cs` | 同步用例（三个入口共用） |
| `Plugins/PluginSyncJobHandler.cs` | `plugin.sync` 处理器 + 补投器 |
| `Plugins/PluginTrustedKeyRegistry.cs` | 配置 + 库合并视图，最长前缀匹配 |
| `Application/Plugins/IPluginTrustedKeyStore.cs` | 公钥持久化端口 + `PluginTrustedKeys` 纯函数 |
| `Infrastructure/Persistence/SqlSugarPluginTrustedKeyStore.cs` | 表实现 |
| `Persistence/PluginTrustedKeySchemaEntity.cs` | `plugin_trusted_keys` 表结构 |

## 13. 界面（阶段 10 末补齐）

`FrontEnd/myfist-agent/src/components/PluginsView.vue` 是插件的唯一运维入口，覆盖了 §7 里除 `POST /{id}/invoke` 之外的全部端点。

| 页签 | 对应端点 | 说明 |
| --- | --- | --- |
| 卡片列表 | `GET /api/plugins` | 状态、版本、发布者、能力数、`missingPermissions`、`lastError`、`enabled` |
| 安装 | `POST /api/plugins/install`（multipart） | 只接受 `.zip`；`warnings` 会单独弹一条提示（未签名的包会在这里告知） |
| 概览 | `GET /api/plugins/{id}` | 健康、在途调用、排空、版本、目录、完整性、并发上限、能力清单；操作按钮：`restart`/`health`/`drain`/`rollback`/`DELETE` |
| 配置 | `PUT /{id}/config` | 按 manifest 的 `config.schema` 渲染表单（string/integer/number/boolean/enum/string-array），schema 不可解析时退回原始 JSON；「清空配置」发 `{"config": null}` |
| 授权 | `PUT /{id}/permissions` | 五个权限逐条勾选；未授权时插件停在 `PermissionRequired`，不会启动 |
| 作用域 | `PUT /{id}/scope` | 知识库多选（全不选 = 不限制）+ `maxDocuments`（0 = 跟随全局） |
| 同步 | `POST /{id}/sync-async` + `GET /{id}/sync-state` | 选 `platform-connector` 能力与目标知识库；同步进任务队列，进度去「任务中心」看；下方表格展示每个 `(能力, 知识库)` 的游标与上次结果 |
| 审计 | `GET /{id}/audit` | 时间线（安装/授权/配置/启动/同步/调用/回滚/卸载） |
| 受信任公钥 | `GET/PUT/DELETE /api/plugins/trusted-keys` | 粘贴 PEM 新增（后端拒绝私钥）；`config:` 前缀的行不给删，提示去改 appsettings |

**`GET /api/plugins/{id}` 为界面补的字段**（原来只有 `status`，不足以渲染配置/作用域）：

| 字段 | 来源 | 为什么需要 |
| --- | --- | --- |
| `config` | 注册表 `ConfigJson` | 不给当前值，用户每次改配置都要从零重填 |
| `configSchema` / `configRequired` | `manifest.config` | 前端只做渲染，不猜字段 |
| `declaredPermissions` | 能力 `permissions` 并集 | 授权页需要完整可勾选集合，而不只是 `missingPermissions` |
| `allowedKnowledgeBaseIds` / `maxDocuments` | 注册表 | 作用域页的回显 |
| `limits` | `manifest.limits` | 只读展示并发/超时/内存上限 |

**踩过的坑**：

- 同步下拉必须提交能力的 `id`（`platform.notes.sync`），不是 `name`——两者都在 `declaredCapabilities` 里，用错会 404/400。
- `<input type="number">` 上的 `v-model` 会把值转成 `number`，任何 `(value ?? '').trim()` 都会抛 `trim is not a function`。
- `integrity` 是**状态字符串**（`verified`/`not-provided`/`skipped`/`digest-mismatch`/`signature-invalid`/`untrusted-publisher`），不是布尔；`not-provided` 不能显示成绿色「已校验签名」。
- 卸载后详情面板要立刻关掉：10 秒定时刷新会拿已删除的 id 去 `GET`，否则每 10 秒弹一次 404。

## 14. 调用准入：并发上限与有界队列（阶段 11）

### 14.1 为什么要有这一层

原本只有 `PluginProcessHost` 里的一个 `SemaphoreSlim`（容量取 manifest 的 `limits.maxConcurrentInvocations`）。
`WaitAsync` 的排队**没有上限**，所以「插件只允许 2 并发」在真实使用中没有可感知的表现：连续发 10 个请求，
结果是 2 个跑完、8 个一直挂着直到调用方自己的 HTTP 超时。使用者看到的是**卡死**，而不是**太忙**。

现在的模型（`Application/Plugins/PluginInvocationLimiter.cs`，单例）：

| 层 | 上限来源 | 超限行为 |
| --- | --- | --- |
| 插件队列 | `Plugins:MaxQueuedInvocationsPerPlugin`（默认 16） | 立刻 `429` |
| 能力队列 | `Plugins:MaxQueuedInvocationsPerCapability`（默认 8，仅对配了能力级上限的能力生效） | 立刻 `429` |
| 插件并发 | manifest `limits.maxConcurrentInvocations` | 排队；超过 `waitTimeout` 后 `429` |
| 能力并发 | `Plugins:CapabilityConcurrencyLimits` | 排队；超过 `waitTimeout` 后 `429` |

关键规则：

- **在途上限 = 并发 + 队列**。`MaxQueuedInvocationsPerPlugin = 0` 的含义是「没有空闲槽位就拒绝」，
  而不是「一律拒绝」——两者差一个把系统用死的 bug。
- **先拿能力闸门，再拿插件闸门**。反过来会让一次调用一边占着插件容量一边空等能力槽位，
  插件级 `running` 也会虚高（看着在跑，其实在排队），并且会挡住同插件的其它能力。
- **拒绝不吃名额**：占队位失败、排队超时、调用方取消，三条路径都会把已占的计数/信号量还回去。
  这是最容易写错的地方，`PluginInvocationLimiterTests` 专门盯着它。
- **计数是进程内的**：多进程部署时每进程各自限流（与 `JobTypeQuota` 同一口径）。
- 错误码 `-32006`（`PluginProtocol.ErrorOverloaded`）→ HTTP **429**（不是 503：这是「太忙，退避重试」）。
  新增了 `GET /api/plugins/concurrency` 与详情里的 `concurrency` 字段，界面在「概览」显示「并发占用 1/2 · 排队 3」。

### 14.2 实测证据（Windows，release 版 API + 真实插件子进程）

示例插件的 `notes.slow-echo` 能力（每次 1500 ms），manifest 声明 `maxConcurrentInvocations: 2`。

| 场景 | 配置 | 结果 |
| --- | --- | --- |
| 拒绝而非排队 | `Plugins__MaxQueuedInvocationsPerPlugin=0` | 10 个并发 → `200×2 + 429×8`，消息「并发已满（2），且排队已达上限 0」；采样 `running` 峰值 2；结束后 `running=0 queued=0` |
| 有界队列 | `MaxQueuedInvocationsPerPlugin=2` + `CapabilityConcurrencyLimits["notes.slow-echo"]=1` | 10 个并发 → `200×4 + 429×6`（在途上限 2+2），插件级 `running` 峰值 **1**、能力级 `notes.slow-echo 1/1`，结束后全部归零 |

启动日志会打印生效值（`Plugin invocation limits: queue per plugin=2, queue per capability=8, capability limits=notes.slow-echo=1`）——
限流是最容易「配了没生效」的地方，没有这行只能靠压测反推。

### 14.3 还得注意

- 改配置需要**重启 API**（没有热更新）。
- `waitTimeout` 复用的是能力的 `invocationTimeoutMs`：一次调用最坏是 `invocationTimeoutMs`（排队）+ 调用超时，不是无限。
- 队列里的等待**不计入**审计里的 `elapsedMilliseconds`（计时从拿到执行机会开始），所以那个数字仍是「插件自己跑了多久」。

## 15. 仍未实现

> **已实现（不要再看旧结论）**：进程隔离在 **D-1（2026-10-24）已落地**——Windows 上插件运行于
> AppContainer，网络能力按授权附加，文件系统按 ACL 授予，详见 §8.1 与 `docs/ROADMAP.md` D-1。
> 下面的表格只列**当前仍然没有**的东西。

| 能力 | 说明 |
| --- | --- |
| 非 Windows 平台的等价隔离 | Linux 需 `bwrap`/`unshare -n`，macOS 需 `sandbox-exec`，均未实现；当前这些平台如实报 `unsupported`，`Mode=require` 会拒绝启动 |
| 跨机器/分布式执行 | 一个 API 进程内的子进程，没有远程插件主机 |
| 插件市场 / 远程源 | 只支持上传本地 ZIP，没有远程仓库或自动更新 |
| 跨进程/集群配额 | 并发与队列计数都在进程内；多进程部署时是「每进程上限」 |
| 手动作 invoke | 界面不提供裸调用调试入口；工具桥（`PluginToolBridge`）按 `agent-tool` 能力自动暴露给 Agent |
| 签名工作流工具链 | 界面能粘公钥，但没有「生成密钥 → 算摘要 → 签包」的脚本（见 ROADMAP P1-6） |
| 域名白名单 / CPU·内存配额 | 当前只做「有/无」两档；`limits.maxMemoryMb` 尚未按进程强制 |

这些项的排期、依赖与验收标准见 **`docs/ROADMAP.md`**（P1-6、P3 各节）。
