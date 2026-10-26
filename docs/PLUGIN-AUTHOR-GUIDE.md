# 插件作者指南（速查）

> 第一次写插件？先看 **`docs/PLUGIN-DEVELOPER-MANUAL.md`**（对外整合版：从零到发布，含非 C# 语言实现与故障对照表）。
> 仓库内的分阶段教程是 `docs/PLUGIN-DEVELOPMENT-GUIDE.md`。
> 本文是写代码时要摊在手边的**速查**：清单字段、协议方法、错误码、沙箱边界。
> 想知道运行时怎么管你（状态机、四道闸门、审计、隔离），看 `docs/PLUGIN-LIFECYCLE.md`。

本文描述**已经实现**的插件契约。安装/升级/回滚/卸载、配置、授权、审计、摘要/签名校验、
平台条目映射与去重、撤回删除、同步游标、知识库作用域与配额均已落地；
两种对话方言（自有协议 `plugin/*` 与标准 **MCP**）都可用，见 §3.2；
**内核级隔离已在 Windows 落地**（AppContainer，D-1 2026-10-24，见 §7.1），其它平台尚未实现、如实标 `unsupported`。

## 1. 心智模型

```
核心进程 (ASP.NET Core)                    插件进程（你的程序）
├── PluginManager 发现/启动/健康检查   ←→   PluginServer（SDK 提供）
├── PluginProcessHost 管子进程          ←→   stdin/stdout: JSON-RPC 2.0 (NDJSON)
├── PluginToolSource 桥接 agent-tool
└── PluginIngestionSink 走核心摄取用例
```

三条硬约束：

1. **插件是独立进程。** 不把第三方程序集加载进核心；不用 `AssemblyLoadContext` 做隔离（其卸载是协作式的，无法提供安全边界）。
2. **stdout 只承载协议消息。** 任何日志写 stderr（`PluginBase.Log` 已代为处理）。
3. **插件不写数据库。** 平台内容通过 `platform-connector` 返回值提交，由核心调用既有摄取用例（版本、Outbox、索引生命周期全部复用）。

> 已经写好的 stdio MCP server 不用改造：清单里写一行 `"protocol": "mcp"` 就能载入，详见 §3.2。

## 2. 最小插件

```csharp
// Program.cs
using MyFirstMafAgent.Plugins.Sdk;
return await PluginServer.RunAsync(new MyPlugin(), args);
```

```csharp
using System.Text.Json.Nodes;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;   // PluginRpcException 在这里

internal sealed class MyPlugin : PluginBase
{
    public override string Id => "com.example.my";       // 必须与 manifest.id 一致
    public override string Version => "1.0.0";            // 必须与 manifest.version 一致
    public override IReadOnlyList<PluginCapability> Capabilities => [ /* 与 manifest 一致 */ ];

    public override Task<PluginHealth> CheckHealthAsync(CancellationToken ct) =>
        Task.FromResult(PluginHealth.Healthy());

    public override Task<JsonNode?> InvokeAsync(string capabilityId, JsonNode? arguments, CancellationToken ct) =>
        capabilityId switch
        {
            "my.search" => Task.FromResult<JsonNode?>(Search(arguments, ct)),
            _ => throw new PluginRpcException(PluginProtocol.ErrorUnknownCapability, capabilityId)
        };
}
```

项目文件：

```xml
<ItemGroup>
  <PackageReference Include="MyFirstMafAgent.Plugins.Sdk" Version="1.0.0" />
</ItemGroup>
<ItemGroup>
  <None Update="manifest.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

完整可运行示例：`samples/MyFirstMafAgent.Plugin.SamplePlatform/`。

## 3. manifest

`manifest.json` 放在插件目录根部，与可执行文件同级。

```json
{
  "schemaVersion": "1.0",
  "id": "com.example.notes",
  "name": "示例笔记",
  "version": "1.0.0",
  "publisher": "Example",
  "entry": { "command": "MyPlugin.exe", "arguments": [], "workingDirectory": null },
  "runtime": "dotnet",
  "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" },
  "capabilities": [
    {
      "id": "notes.search",
      "kind": "agent-tool",
      "name": "搜索笔记",
      "description": "按关键词检索示例平台笔记",
      "parameters": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}",
      "permissions": ["network"],
      "sideEffect": "read"
    }
  ],
  "config": { "schema": "{\"type\":\"object\",\"properties\":{}}", "required": [] },
  "healthCheck": { "intervalSeconds": 30, "timeoutMs": 5000 },
  "limits": { "maxMemoryMb": 256, "maxConcurrentInvocations": 2, "invocationTimeoutMs": 15000, "maxRequestBytes": 262144, "maxRetries": 0 },
  "integrity": { "sha256": "<64 hex>", "signature": null }
}
```

校验规则（`PluginManifestValidator`，未通过则插件不可用）：

| 字段 | 规则 |
| --- | --- |
| `schemaVersion` | 必须等于 `1.0`（缺省即 `1.0`） |
| `id` | 小写反向域名，段间用 `.`，段内 `a-z0-9-`，总长 ≤ 128；`dialect: mcp` 时放宽为单段 `a-z0-9._-` |
| `version` | SemVer（`1.10.0 > 1.9.0`；`1.0.0-rc1 < 1.0.0`） |
| `protocol` | 闭区间必须包含核心协议 `1.0.0`；`dialect: mcp` 时不做区间判定（版本由 MCP 自己协商） |
| `capabilities` | 自有协议至少 1 项；`dialect: mcp` 可为空（能力来自 `tools/list`）。`id` 唯一且小写；`kind` ∈ `agent-tool` / `platform-connector`；`sideEffect` ∈ `read` / `write-local` / `write-external` / `destructive`；`permissions` ∈ `network` / `file-read` / `file-write` / `clipboard` / `process`；`parameters` 必须是 JSON Schema 对象 |
| `limits` | `maxMemoryMb` 64–4096；并发 1–16；超时 1s–600s；请求 ≤ 16 MiB；`maxRetries` 0–5 |
| `integrity.sha256` | 64 位十六进制；缺失只报警告（阶段 4 会改为强制） |
| `entry` | 相对路径必须真实存在；宿主命令（如 `dotnet`、`npx`）取 `arguments` 里第一个 `.dll`/`.js`/`.py`，没有文件参数时视为「运行期由 PATH/包管理器决定」，不判失败 |

### 3.1 清单文件名与字段别名（D-2）

清单文件名认 `manifest.json`、`plugin.json`、`skill.json` 三种，内容格式相同。
下面这些**生态常见写法**在读取时会被改名成上面的契约词汇，并作为 warning 出现在插件状态里（不静默生效）：

| 你写的 | 等价于 | 说明 |
| --- | --- | --- |
| `"protocol": "mcp"` 或 `"dialect": "mcp"` | 方言声明 | 见 §3.2 |
| `entry.args` | `entry.arguments` | Claude Desktop / MCP 配置的写法 |
| `entry.cwd` | `entry.workingDirectory` | 同上 |
| `entry.command: ["npx","-y","pkg"]` | `command` + `arguments` | 命令与参数写成一个数组 |
| `entry.env: { "K": 3 }` | `{ "K": "3" }` | 环境变量的值必须是字符串 |
| 能力的 `inputSchema` / `input_schema` / `parametersSchema` | `parameters` | MCP 的 schema 是对象，内部统一成字符串 |
| 能力的 `requires`（顶层也可） | `permissions` | 顶层 `requires`/`permissions` 对所有能力生效 |
| `net.outbound` / `net` / `internet` | `network` | 权限词汇别名 |
| `fs` / `filesystem` / `file` | `file-read` + `file-write` | 一个词展开成两个权限 |
| `fs.read` / `fs.write` | `file-read` / `file-write` | |
| `proc` / `exec` / `shell` | `process` | |
| `kind: tool` / `function` | `agent-tool` | |
| `kind: connector` / `platform` | `platform-connector` | |
| `sideEffect: none` / `readonly` | `read` | |
| `sideEffect: write` | `write-local` | |
| `capabilities: ["search","Fetch Page"]` | 两个 `agent-tool` | 只写工具名也认 |
| 能力缺 `kind` | `agent-tool` | 平台连接器必须显式声明 |
| 顶层 `configSchema` / `config` 为字符串 | `config.schema` | |

认不出来的权限词**不会被丢掉**，而是原样保留并报错——你必须能看见自己写错了什么。

### 3.2 用 MCP 方言写插件（现成 MCP server 直接载入）

如果插件本来就是一个 stdio MCP server，清单里写 `"protocol": "mcp"`（或 `"dialect": "mcp"`）即可，**不必实现本项目的 `plugin/*` 方法**：

```json
{
  "id": "filesystem",
  "protocol": "mcp",
  "entry": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."] }
}
```

核心会：

1. 发 `initialize`（`protocolVersion: 2024-11-05`）→ 读 `serverInfo`（只记录，不与清单对账）；
2. 发 `notifications/initialized`；
3. 发 `tools/list`（跟 `nextCursor` 翻页，最多 20 页）→ 每个 tool 变成一个 `agent-tool` 能力，`inputSchema` 原样作为工具参数；
4. 调用时发 `tools/call`，把 `content` 里的文本拼成一段结果返回给模型；`isError: true` 转成调用失败并把文本作为错误信息；
5. 健康检查用 `ping`（对端回 `-32601` 视为健康）；停止时**关闭 stdin**（MCP 约定的退出信号），不做 `shutdown` 请求；
6. 取消用 `notifications/cancelled`（`{ requestId, reason }`）。

副作用等级取自 MCP 的 `annotations`：`destructiveHint` → `destructive`，`readOnlyHint: false` → `write-local`，否则 `read`。你也可以在清单里写同名能力来覆盖（例如把某个工具标成 `write-external` 以强制确认）。

配置通过**环境变量**下发：清单 `entry.env` 里的键值原样注入；用户配置里的每个标量字段注入为 `MYFIRSTMAFAGENT_CONFIG_<KEY大写>`。改配置会重启插件进程。

限制（当前实现）：`notifications/tools/list_changed` 只记日志，不热重建能力表（重启插件生效）；SSE / HTTP 传输的 MCP server 不支持，只支持 stdio。


## 4. 协议

JSON-RPC 2.0，**一行一个 JSON**（NDJSON），UTF-8，`\n` 结尾，单行 ≤ 8 MiB。

### 方法

| 方法 | 方向 | params | result |
| --- | --- | --- | --- |
| `plugin/initialize` | 核心 → 插件 | `{protocolVersion, coreVersion?, dataDirectory?, config?}` | `{protocolVersion, pluginId, pluginVersion, runtime, capabilities[]}` |
| `plugin/configure` | 核心 → 插件 | `{config}` | `{ok:true}` |
| `plugin/health` | 核心 → 插件 | – | `{status:"healthy"\|"degraded"\|"unhealthy", detail?}` |
| `plugin/capabilities/list` | 核心 → 插件 | – | `{capabilities[]}` |
| `plugin/capabilities/invoke` | 核心 → 插件 | `{capabilityId, arguments?}` | `{capabilityId, result}` |
| `plugin/shutdown` | 核心 → 插件 | – | `{ok:true}`（响应写出后插件退出） |
| `plugin/cancel` | 双向通知 | `{targetId}` | 无响应 |
| `plugin/log` | 插件 → 核心通知 | `{level, message}` | 无响应 |

握手校验：`pluginId` / `pluginVersion` 必须与 manifest 完全一致；插件不得报告 manifest 未声明的能力，否则拒绝启动。

### 错误码

| 码 | 含义 |
| --- | --- |
| `-32700` | JSON 解析失败 |
| `-32601` | 方法不存在 |
| `-32602` | 参数非法 |
| `-32603` | 内部错误 |
| `-32001` | 协议不兼容 |
| `-32002` | 未初始化/未配置 |
| `-32003` | 权限不足 |
| `-32004` | 不健康 |
| `-32005` | 未声明的能力 |
| `-32800` | 已取消/超时 |

HTTP 映射：`-32005`→404、`-32602`→400、`-32003`→403、`-32002`/`-32001`→409、`-32800`→408、`-32004`→503，其余 502。

### 取消与超时

核心对每次调用设置超时（manifest `limits.invocationTimeoutMs`，上限受 `Plugins:MaxInvocationTimeoutMs` 约束）。
超时或调用方取消时，核心发送 `plugin/cancel {targetId}`，插件应尽快中止并把 `CancellationToken` 传到下游。
插件应以 `-32800` 结束被取消的请求。

## 5. 两类能力

### agent-tool

暴露给聊天模型，命名规则：`plugin__{pluginId}__{capabilityId}`（非 ASCII 与 `.` 转为 `_`，最长 64，重名加 `_2`）。
`description` 会附加副作用提示与所需权限。模型收到的是文本，JSON 结果会被序列化返回。

### platform-connector

只能通过同步端点触发，模型不能直接调用。返回值必须是：

```json
{
  "items": [
    {
      "source": "com.example.notes",
      "externalId": "note-1001",
      "title": "标题",
      "type": "note",
      "content": "正文（Markdown 或纯文本）",
      "updatedAt": "2026-10-20T09:00:00+08:00",
      "contentHash": "<64 hex>",
      "retracted": false,
      "metadata": { "url": "https://..." }
    }
  ],
  "nextCursor": "page-2",
  "nextSyncAfterSeconds": 3600
}
```

- 单批上限 `Plugins:MaxIngestionItemsPerBatch`（默认 200），超出整批拒绝。
- `type` ∈ `note` / `article` / `message` / `file`。
- `contentHash` 缺失时核心会计算；建议插件自己也提供，便于后续去重。
- **`retracted = true` 会真的删掉对应资料**，并留下墓碑映射，旧批次无法复活；平台从未给过的条目只会写墓碑。
- 去重由核心完成：同一条目（`source + externalId` + 目标知识库）重复出现在不同批次里不会重复建资料；内容哈希没变直接跳过。
- `nextCursor` 会按 `插件 × 能力 × 知识库` 持久化，下次不传 `cursor` 就接着上次同步。

**`nextSyncAfterSeconds` 契约（阶段 8）**：返回它表示“请核心过这么久再同步一次”。核心把值写进游标表，由补投器到点入队 `plugin.sync` 任务，**不需要任何外部定时器**。

| 你返回 | 核心行为 |
| --- | --- |
| `3600` | 一小时后自动同步；若 `Plugins:MinScheduledSyncIntervalSeconds` 比它大，按配置的最小间隔算 |
| `0` / `null` / 不返回 | 不自动同步（只手动触发） |
| `1`（写错单位） | 被夹到最小间隔（默认 60 秒），不会把任务队列刷爆 |

注意：**间隔只在“批次被接受”后才写**；同步失败时游标不动、间隔不更新，下一次仍按旧节奏走。要停止自动同步，返回 `null` 并把配置的 `syncIntervalSeconds` 置 0（示例插件就是这么做的）。

## 6. 调试

```powershell
# 1. 构建示例插件
dotnet build samples\MyFirstMafAgent.Plugin.SamplePlatform\MyFirstMafAgent.Plugin.SamplePlatform.csproj

# 2. 协议级契约测试（不需要启动 API）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-PluginContract.ps1

# 3. 打成 ZIP 并通过端点安装（会自动校验 manifest、入口、摘要）
$src = 'samples\MyFirstMafAgent.Plugin.SamplePlatform\bin\Debug\net10.0'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($src, 'C:\Temp\sample.zip', 'Optimal', $false)
curl.exe -s -F "file=@C:\Temp\sample.zip" http://127.0.0.1:5056/api/plugins/install

# 4. 声明了权限的插件会停在 PermissionRequired，需要先授权
curl.exe -s -X PUT -H "Content-Type: application/json" -d '{"permissions":["network"]}' `
  http://127.0.0.1:5056/api/plugins/com.example.sample-platform/permissions

# 5. 查看状态
curl.exe -s http://127.0.0.1:5056/api/plugins
```

开发期也可以直接把构建输出拷到 `App_Data/plugins/{id}/`（阶段 3 的扁平布局仍然可读，生效版本记为“本地版本”）。

端点：

| 端点 | 用途 |
| --- | --- |
| `GET /api/plugins` | 全部插件：状态、版本、健康、权限、完整性、是否已配置 |
| `GET /api/plugins/{id}` | 单个插件（含 manifest 声明的能力详情） |
| `POST /api/plugins/{id}/health` | 主动健康检查 |
| `POST /api/plugins/{id}/invoke` | `{capabilityId, arguments, confirm?}` |
| `POST /api/plugins/{id}/sync` | `{capabilityId, knowledgeBaseId?, cursor?}` |
| `POST /api/plugins/{id}/sync-async` | 同上，但把同步排进任务中心（可看进度、可取消） |
| `POST /api/plugins/{id}/drain` | 软下线：不再接新调用，等已发出的调用结束，返回 `{drained, inFlight}` |
| `GET /api/plugins/trusted-keys` | 当前生效的受信任公钥（配置项 + 库） |
| `PUT /api/plugins/trusted-keys/{keyId}` | `{pluginId, publicKeyPem, algorithm?, note?}`，立即生效 |
| `DELETE /api/plugins/trusted-keys/{keyId}` | 删除库里的公钥（`config:` 前缀返回 400） |
| `POST /api/plugins/{id}/enabled` | `{enabled}` 启用/停用 |
| `PUT /api/plugins/{id}/config` | 写入配置（按 manifest 的 JSON Schema 校验后重启） |
| `PUT /api/plugins/{id}/permissions` | 授予/收回权限（完整集合） |
| `POST /api/plugins/{id}/restart` | 手动重启 |
| `POST /api/plugins/install` | 上传 ZIP 安装/升级 |
| `POST /api/plugins/{id}/rollback` | 回滚到上一版本 |
| `DELETE /api/plugins/{id}` | 卸载 |
| `GET /api/plugins/{id}/audit` | 审计记录 |
| `GET /api/agent/tools` | 确认 `plugin__*` 工具已桥接 |

插件日志会带前缀转发到核心日志（`Plugin {PluginId}: ...`）；`plugin/log` 通知显示为 `Plugin {PluginId} [level] message`。

## 7. 权限与安全边界（必须遵守）

- **默认不信任。** 只申请实际需要的权限；`sideEffect` 如实标注，`write-external`/`destructive` 会被标记为需要用户确认，且**不会暴露给聊天模型**（模型无法提供确认信息）。
- `platform-connector` 返回的网页/平台内容属**不可信数据**，不得当作指令执行。
- 凭证不要写进 manifest；配置由用户填写，核心通过 `plugin/configure` 下发。
- 插件能访问的写目录只有 `<plugins>/.data/{pluginId}`（`MYFIRSTMAFAGENT_PLUGIN_DATA`）。

### 7.1 你真的会被关进沙箱（Windows，D-1 起）

Windows 上插件进程跑在 **AppContainer** 里，权限表不再只是"说了算"，而是内核强制执行：

| 你声明了什么 | 实际效果 |
| --- | --- |
| 没声明 `network` | 令牌里没有 `internetClient`（`S-1-15-3-1`），`socket()` 直接失败。实测 Node `net.connect` → `EACCES`，不是超时、不是"连不上" |
| 声明 `network` 且用户已授予 | 出站网络正常（实测同一段代码 → `CONNECT_OK`） |
| 任何情况 | 只能读**自己的插件目录**、只能写 `.data/{pluginId}`；读 `C:\...` 其它位置会被拒绝 |
| 任何情况 | 拿不到核心的环境变量（连接串、API Key 不在子进程环境里） |

因此写插件时请注意：

- **运行时（node/python/dotnet…）不能假设装在 `Program Files` 就能用**。容器读不到那里（`C:\Program Files\nodejs` 只授了 `Users` 组）。把运行时放到 `<plugins>/.runtimes/{命令名}/`，清单里继续写 `"command": "node"` 即可——核心解析命令时**优先 `.runtimes`**，再查 `PATH`。
- 需要写入的东西一律写到 `MYFIRSTMAFAGENT_PLUGIN_DATA`；`USERPROFILE`/`TEMP`/`APPDATA`/`LOCALAPPDATA` 已被核心重定向到该目录下，所以 `fs.mkdtemp()` 这类调用能正常工作。
- Node 插件：核心会自动注入 `NODE_OPTIONS=--preserve-symlinks-main`。原因是 node 解析入口脚本时要 `realpath('C:\')`，而容器读不到驱动器根（那需要管理员改 `C:\` 的 ACL）。如果你的插件对这个语义敏感（比如依赖符号链接指向真实路径），请自己在 `entry.env` 里覆盖 `NODE_OPTIONS`。
- **不要依赖 `plugins/.runtimes` 之外的绝对路径**。你如果确实需要某个额外的只读目录，请让用户在 `Plugins:Isolation:ReadableRoots` 里加，而不是假设它能读。
- 隔离失败时核心会**拒绝启动**并把原因写进 `GET /api/plugins/{id}` 的 `isolation.detail`（例如"容器没有读取入口程序的权限"）。`isolation.applied=false` 表示**没有**沙箱在保护用户，用户应当看到这条信息。

非 Windows 平台目前没有等价隔离：`isolation.kind = unsupported`、`applied = false`，权限表只作准入控制。**不要把"只在 Windows 上被限制"当作可以依赖的行为差异**——两边都应该按最小权限写。

## 8. 完整性与签名（阶段 4 已实现）

核心可以要求插件包带摘要与发布者签名（`Plugins:IntegrityPolicy` 为 `warn` 或 `enforce`）。

**摘要算法（必须逐字照做，否则任何重新打包都会校验失败）**：

1. 取包目录下所有普通文件，排除 `manifest.json`、`active.json` 以及任何以 `.` 开头的目录（`.data`、`.staging`）。
2. 按相对路径（`/` 分隔、大小写敏感、Ordinal 排序）排序。
3. 逐个写入：UTF-8 相对路径、`\0`、UTF-8 十进制字节数、`\0`、文件内容。
4. 取 SHA-256，小写十六进制 → 填到 `integrity.sha256`。

`manifest.json` 不参与摘要（摘要写在它里面），其身份由签名与运行期握手校验（id/version/能力）共同保证。

**签名**：对上述十六进制**字符串的 UTF-8 字节**做 SHA-256 签名，Base64 编码后填到 `integrity.signature`。

```bash
# ECDSA P-256（openssl 默认输出 DER，核心也接受 P1363）
openssl dgst -sha256 -sign publisher.key -out sig.bin <(printf '%s' "$DIGEST")
base64 -w0 sig.bin
```

支持 `ecdsa-p256` 与 `rsa`（PKCS#1 v1.5）。公钥有两个来源，**配置优先**：

- `Plugins:TrustedPublisherKeys`（KeyId 记为 `config:{pluginId}`，不可经接口删除）；
- `plugin_trusted_keys` 表（`PUT /api/plugins/trusted-keys/{keyId}`，写完立即生效）。

`PluginId` 按域名段前缀匹配（`com.example` 覆盖 `com.example.notes`，不覆盖 `com.exampleevil.notes`），多个匹配取最长前缀。
接口只接受公钥：`algorithm` 只能是 `ecdsa`/`rsa`，PEM 必须可解析，**含 `PRIVATE KEY` 的请求直接 400**。

`enforce` 策略下缺失摘要或签名会被直接拒绝，且会写入一条 `integrity-rejected` 审计。

## 9. 版本与兼容

| 变更 | 需要 |
| --- | --- |
| 新增可选字段、新增能力 | 提升插件次版本 |
| 删除字段、改变既有字段语义 | 提升 manifest schema 或协议主版本 |
| 协议主版本变化 | 核心与插件都不再兼容，必须同步升级 |

`protocol.minVersion/maxVersion` 声明插件可用的协议区间；核心只在区间包含当前协议版本时启动插件。

多版本共存已实现：每个版本是 `plugins/{id}/versions/{v}` 一个完整目录，`active.json` 指向生效版本，
升级与回滚都是「换指针 + 重启」。因此：

- 插件不得依赖自己目录的绝对路径（版本目录会变），只能依赖 `MYFIRSTMAFAGENT_PLUGIN_DATA`。
- **升级会先排空（阶段 8）**：核心停止接受新调用，等已发出的调用结束（最多 `Plugins:DrainTimeoutSeconds`，默认 15 秒），再停进程替换文件。因此长调用不会被硬断，但**不要依赖超长单次调用**（超过排空超时就可能被强停）。
- 想让核心“主动排空再升级”，可先 `POST /api/plugins/{id}/drain`，再上传新版本。
- 版本号取自 manifest，运行期自述版本必须与之一致，否则拒绝握手。

## 10. 尚未实现（不要在插件中依赖）

| 能力 | 计划阶段 |
| --- | --- |
| Linux `bwrap`/`unshare -n`、macOS `sandbox-exec` 的等价隔离 | 后续（未实测，当前标 `unsupported`） |
| 域名白名单 / 只读路径白名单 / CPU·内存限额 | 后续（当前只做「有/无」两档） |
| 插件市场 / 远程源（目前只支持上传本地 ZIP） | 后续 |
| 插件市场 / 远程安装源 | 后续 |
| TypeScript SDK | 后续（协议已跨语言） |

排期与验收标准见 **`docs/ROADMAP.md`**（P1-3、P1-4、P3-2、P3-3）。

已实现但值得记住的硬约束：**声明了 `network` / `file-read` / `file-write` / `clipboard` / `process`
的插件，默认不会自动启动**——用户必须在界面上授权，否则状态停在 `PermissionRequired`。
只想被调用、不需要网络的工具请不要随手声明 `network`。
