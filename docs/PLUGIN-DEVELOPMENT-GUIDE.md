# 插件开发教程：从零到发布

面向**第一次**为本项目写插件的开发者。跟着 §1 走，十分钟内你能得到一个能在应用里跑起来的插件。

> 📘 **对外整合版**：`docs/PLUGIN-DEVELOPER-MANUAL.md`（含非 C# 语言实现、发布检查清单、故障对照表）。本文保留为仓库内的分阶段教程。

配套文档：

| 文档 | 什么时候看 |
| --- | --- |
| 本文 | 你想**动手写一个插件** |
| `docs/PLUGIN-AUTHOR-GUIDE.md` | 需要**速查**清单字段、协议方法、错误码 |
| `docs/PLUGIN-LIFECYCLE.md` | 想知道**运行时到底怎么管你**（状态机、闸门、沙箱、审计） |
| `docs/MCP-INTEGRATION.md` | 你手里已经有一个 **MCP server**，想直接接进来 |
| `samples/MyFirstMafAgent.Plugin.SamplePlatform/` | 想要一份**能编译能跑**的完整参考实现 |

---

## 1. 十分钟跑通第一个插件

### 1.1 环境

- .NET 10 SDK（写 C# 插件时）
- 应用已在跑（`dotnet run` 或安装版），能打开 **插件** 页面

> 不是只能写 C#。协议是 JSON-RPC over stdio，Node、Python、Go、Rust 都能写，C# 只是有现成 SDK 最省事。见 §10。

### 1.2 建项目

```bash
dotnet new console -n MyFirstPlugin
cd MyFirstPlugin
dotnet add reference ..\..\MyFirstMafAgent.Plugins.Sdk\MyFirstMafAgent.Plugins.Sdk.csproj
```

`MyFirstPlugin.csproj` 关键部分：

```xml
<PropertyGroup>
  <OutputType>Exe</OutputType>
  <TargetFramework>net10.0</TargetFramework>
</PropertyGroup>

<ItemGroup>
  <!-- manifest.json 复制到输出目录，让 bin 输出本身就是一个合法插件目录 -->
  <None Update="manifest.json" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

外部开发者用 NuGet 包时改成 `<PackageReference Include="MyFirstMafAgent.Plugins.Sdk" Version="1.0.0" />`。

### 1.3 写代码

`Program.cs` —— 只有一行真正重要：

```csharp
using MyFirstMafAgent.Plugins.Sdk;

return await PluginServer.RunAsync(new MyPlugin(), args);
```

`MyPlugin.cs` —— 你只需要实现四个成员：

```csharp
using System.Text.Json.Nodes;
using MyFirstMafAgent.Plugins.Sdk;
using MyFirstMafAgent.Plugins.Sdk.Rpc;   // PluginRpcException 在这里

internal sealed class MyPlugin : PluginBase
{
    // 必须与 manifest.id 一致，握手时校验
    public override string Id => "com.example.my";

    // 必须与 manifest.version 一致，握手时校验
    public override string Version => "1.0.0";

    // 必须与 manifest.capabilities 一致，运行期自检
    public override IReadOnlyList<PluginCapability> Capabilities { get; } =
    [
        new PluginCapability
        {
            Id = "my.greet",
            Kind = PluginCapabilityKinds.AgentTool,
            Name = "打招呼",
            Description = "按名字返回一句问候。",
            Parameters = """{"type":"object","properties":{"name":{"type":"string","description":"名字"}},"required":["name"],"additionalProperties":false}""",
            Permissions = [],
            SideEffect = PluginSideEffects.Read
        }
    ];

    public override Task<JsonNode?> InvokeAsync(string capabilityId, JsonNode? arguments, CancellationToken ct) =>
        capabilityId switch
        {
            "my.greet" => Task.FromResult<JsonNode?>(new JsonObject
            {
                ["message"] = $"你好，{arguments?["name"]?.GetValue<string>() ?? "陌生人"}"
            }),
            _ => throw new PluginRpcException(PluginProtocol.ErrorUnknownCapability, capabilityId)
        };
}
```

**就这些。** 协议握手、分帧、错误码、取消、日志转发都由 `PluginServer` / `PluginBase` 处理。

### 1.4 写清单

`manifest.json`，和可执行文件同级：

```json
{
  "schemaVersion": "1.0",
  "id": "com.example.my",
  "name": "我的第一个插件",
  "version": "1.0.0",
  "publisher": "Example",
  "entry": { "command": "MyFirstPlugin.exe", "arguments": [] },
  "runtime": "dotnet",
  "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" },
  "capabilities": [
    {
      "id": "my.greet",
      "kind": "agent-tool",
      "name": "打招呼",
      "description": "按名字返回一句问候。",
      "parameters": "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"],\"additionalProperties\":false}",
      "permissions": [],
      "sideEffect": "read"
    }
  ]
}
```

### 1.5 先自测，再装进应用

**协议级自测**（不用启动应用）：

```powershell
dotnet build samples\MyFirstMafAgent.Plugin.SamplePlatform
powershell -File scripts\Test-PluginContract.ps1
```

`Test-PluginContract.ps1` 直接起子进程跑一遍握手 → 能力列表 → 调用 → 关闭，能在你碰到 UI 之前就发现"stdout 里混了日志""能力 id 不匹配"这类问题。

**装进应用**，两条路：

| 方式 | 做法 | 适合 |
| --- | --- | --- |
| 目录 + 热重扫 | 把整个输出目录拷到 `App_Data/plugins/com.example.my/`，然后点插件页的 **重新扫描** | 开发迭代（不用打包） |
| ZIP 上传 | 把目录打成 ZIP → `POST /api/plugins/install` 或界面 **安装** | 发布、给别人 |

装完状态应该是 `Running`；若停在 `PermissionRequired` 说明你声明了权限（见 §5）。

---

## 2. 心智模型

```
核心进程 (ASP.NET Core)                    你的插件进程
├── PluginManager  发现/启动/健康/状态  ←→  PluginServer（SDK 提供）
├── PluginProcessHost  管子进程          ←→  stdin/stdout: JSON-RPC 2.0 (NDJSON)
├── PluginToolSource  把能力桥接给模型
└── PluginIngestionSink  平台内容走核心摄取用例
```

三条**硬约束**，违反了就是"跑不起来"而不是"跑得不好"：

1. **插件是独立进程。** 核心不会把第三方程序集加载进自己的进程，也没有 `AssemblyLoadContext` 那种假隔离。
2. **stdout 只承载协议消息。** 你的日志必须写 stderr。用 `PluginBase.Log()` 最省事（它写 stderr，还顺带推给核心日志窗口）。`Console.WriteLine` 会直接破坏协议帧。
3. **插件不写数据库。** 平台内容通过 `platform-connector` 的返回值交给核心，由核心调用既有摄取用例（版本、Outbox、向量索引全部复用）。你连数据库连接串也拿不到——沙箱环境变量里根本没有。

---

## 3. manifest 参考

### 3.1 完整示例

```json
{
  "schemaVersion": "1.0",
  "id": "com.example.notes",
  "name": "示例笔记",
  "version": "1.0.0",
  "publisher": "Example",
  "description": "把示例平台的笔记同步进知识库。",
  "entry": {
    "command": "MyPlugin.exe",
    "arguments": ["--stdio"],
    "workingDirectory": null
  },
  "runtime": "dotnet",
  "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" },
  "capabilities": [
    {
      "id": "notes.search",
      "kind": "agent-tool",
      "name": "搜索笔记",
      "description": "按关键词检索笔记。",
      "parameters": "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"],\"additionalProperties\":false}",
      "permissions": ["network"],
      "sideEffect": "read"
    },
    {
      "id": "platform.notes.sync",
      "kind": "platform-connector",
      "name": "同步笔记",
      "description": "拉取平台侧变更。",
      "permissions": ["network"],
      "sideEffect": "read"
    }
  ],
  "config": {
    "schema": "{\"type\":\"object\",\"properties\":{\"workspace\":{\"type\":\"string\"}},\"required\":[\"workspace\"],\"additionalProperties\":false}",
    "required": ["workspace"]
  },
  "healthCheck": { "intervalSeconds": 30, "timeoutMs": 5000 },
  "limits": {
    "maxMemoryMb": 256,
    "maxConcurrentInvocations": 2,
    "invocationTimeoutMs": 15000,
    "maxRequestBytes": 262144,
    "maxRetries": 0
  },
  "integrity": { "sha256": null, "signature": null }
}
```

### 3.2 字段表

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `schemaVersion` | 否 | 必须为 `1.0`（缺省即 `1.0`） |
| `id` | 是 | 小写反向域名，段间 `.`，段内 `a-z0-9-`，≤128 字符。`dialect: mcp` 时放宽为单段 |
| `version` | 是 | SemVer。`1.10.0 > 1.9.0`，`1.0.0-rc1 < 1.0.0` |
| `entry.command` | 是 | 可执行文件名或绝对路径。**裸命令优先在 `<plugins>/.runtimes` 里找**，再查 PATH |
| `entry.arguments` | 否 | 参数数组；写成单个字符串也接受 |
| `entry.workingDirectory` | 否 | 缺省为插件目录 |
| `runtime` | 否 | 仅作文档用途（`dotnet` / `node` / `python` …） |
| `protocol` | 是 | 闭区间 `[minVersion, maxVersion]` 必须包含核心协议 `1.0.0` |
| `capabilities` | 是 | 自有协议至少 1 项；`dialect: mcp` 可为空（能力来自 `tools/list`） |
| `capabilities[].id` | 是 | 小写，唯一。建议 `<域>.<动作>` |
| `capabilities[].kind` | 是 | `agent-tool` 或 `platform-connector` |
| `capabilities[].parameters` | 否 | JSON Schema **字符串**（不是对象！） |
| `capabilities[].permissions` | 否 | `network` / `file-read` / `file-write` / `clipboard` / `process` |
| `capabilities[].sideEffect` | 否 | `read`（默认）/ `write-local` / `write-external` / `destructive` |
| `config.schema` | 否 | JSON Schema 字符串，界面据此**自动生成配置表单** |
| `config.required` | 否 | 未填则插件停在 `NeedsConfiguration` |
| `limits.*` | 否 | 并发、超时、请求体上限 |
| `integrity` | 否 | §6 |

### 3.3 校验规则速记

不通过的插件**不会启动**，状态 `Failed`，原因写在 `lastError` 里。最常见的四条：

- `id` 用了大写或下划线
- `version` 写的 `1.0` 而不是 `1.0.0`
- `parameters` / `config.schema` 写成了对象而不是**转义后的字符串**
- manifest 里的 `id`/`version`/能力 与代码里 `PluginBase` 声明的不一致（握手失败）

---

## 4. 写能力

### 4.1 `agent-tool`：给模型用的工具

- `parameters` 是给模型看的输入 schema，**写清楚 `description`**，模型的填空质量直接取决于它。
- 返回值任意 JSON：对象、数组、字符串都行，核心序列化后作为工具结果回给模型。
- 只有 `read` / `write-local` 的 `agent-tool` 会出现在 `/api/agent/tools` 里。`write-external` / `destructive` **永远不会**暴露给模型（模型无法提供人工确认），只能通过管理端点带 `confirm: true` 调用。

### 4.2 `platform-connector`：把外部内容同步进知识库

返回值必须是一批"平台条目"，核心会拿它走既有的摄取流程：

```csharp
public override Task<JsonNode?> InvokeAsync(string capabilityId, JsonNode? arguments, CancellationToken ct) =>
    capabilityId switch
    {
        "platform.notes.sync" => Task.FromResult<JsonNode?>(new JsonArray
        {
            new JsonObject
            {
                ["source"] = "com.example.notes",
                ["externalId"] = "note-1001",      // 平台侧稳定 ID，去重靠它
                ["title"] = "笔记标题",
                ["type"] = "note",
                ["content"] = "正文……",
                ["updatedAt"] = "2026-10-20T09:00:00+08:00",
                ["metadata"] = new JsonObject { ["url"] = "https://example/notes/1001" }
            }
        }),
        _ => throw new PluginRpcException(PluginProtocol.ErrorUnknownCapability, capabilityId)
    };
```

核心替你处理的事（**不要自己实现**）：

| 核心负责 | 说明 |
| --- | --- |
| 幂等 | 同 `externalId` + `source` + 知识库只对应一条资料 |
| 变更检测 | `content` 哈希不变就不重写，避免无意义的新版本 |
| 撤回 | 返回值里标记删除 → 核心真删资料，但**留墓碑**，迟到的旧批次无法复活它 |
| 游标 | 你的 `cursor` 由核心持久化，下次同步自动带上 |
| 配额 | 插件作用域/文档数上限由核心判定，超额条目会被拒 |

**不要**在插件里自己存 `externalId → 资料ID` 映射，那是核心的表。你只需要保证 `externalId` 稳定。

### 4.3 配置

```csharp
public override Task ConfigureAsync(JsonNode? config, CancellationToken ct)
{
    _workspace = config?["workspace"]?.GetValue<string>()
        ?? throw new InvalidOperationException("workspace 未配置");
    return Task.CompletedTask;
}
```

- `ConfigureAsync` 里**抛异常 = 启动失败**。这是刻意的：半个配置的插件比不启动更危险。
- 用户在界面改配置后，核心会重启你的进程并重新下发 `plugin/configure`。
- 需要持久化的东西写 `PluginInitializeContext.DataDirectory`，**不要**写自己目录（升级会换目录）。

### 4.4 健康检查

```csharp
public override async Task<PluginHealth> CheckHealthAsync(CancellationToken ct)
{
    try
    {
        await _client.PingAsync(ct);
        return PluginHealth.Healthy();
    }
    catch (Exception ex)
    {
        return PluginHealth.Degraded($"平台暂不可达：{ex.Message}");
    }
}
```

`Unhealthy` 会让核心停止把调用路由给你；`Degraded` 仍然可用（适合"部分降级"）。

### 4.5 超时与取消

- 每次调用都带 `CancellationToken`。**请真的把它传下去**，否则核心超时后你还在跑，会拖住后续调用。
- 超时值由 `limits.invocationTimeoutMs` 决定（默认 15s），排队等待另算。
- 长任务不要指望一次调用跑完——拆成多次，或用 `platform-connector` 的游标分批。

---

## 5. 权限与沙箱（Windows）

### 5.1 权限是"声明 ∩ 授权"

- 你在 manifest 里**声明**权限；用户必须**授予**，两者相交才是实际可用集合。
- 默认不自动授权。声明了 `network`/`file-read`/`file-write`/`clipboard`/`process` 的插件会停在 `PermissionRequired`，直到用户在插件页点授权。
- **不要随手声明 `network`**：不需要联网的工具声明了它，只会让用户多一步操作，还可能让人怀疑你的插件。

### 5.2 你真的会被关进沙箱

Windows 上插件跑在 **AppContainer** 里（详见 `PLUGIN-LIFECYCLE.md` §8.1）。对你的实际影响：

| 约束 | 应对 |
| --- | --- |
| 没授 `network` 就**真的没有出站**（`socket()` 直接 `EACCES`） | 需要联网就老实声明 `network` |
| 只能读自己的插件目录、`plugins/.runtimes`、系统目录 | 额外目录请用户加 `Plugins:Isolation:ReadableRoots` |
| 只能写 `MYFIRSTMAFAGENT_PLUGIN_DATA` | 所有状态、缓存、临时文件都放这里 |
| 环境变量是白名单，父进程的变量**不会**给你 | 别指望 `DATABASE_URL`、API Key 之类能"顺便"读到 |
| `USERPROFILE`/`TEMP`/`APPDATA` 已重定向到你的数据目录 | 正常用 `Path.GetTempPath()` 即可 |
| **运行时装在 `Program Files` 可能读不到**（如 `C:\Program Files\nodejs`） | 把运行时放到 `<plugins>/.runtimes/node/`，清单里继续写 `"command": "node"` |

最后一条是踩过坑的：容器读不到 `Program Files` 下的 node，直接表现为 `Failed`。核心会优先在 `.runtimes` 里解析裸命令，所以放进去就自动生效。

### 5.3 Node 插件的一个坑

核心会为 Node 注入 `NODE_OPTIONS=--preserve-symlinks-main`。原因是 node 解析入口脚本时要 `realpath('C:\')`，容器读不到根目录 → `EPERM`。**不要覆盖 `NODE_OPTIONS`**。

---

## 6. 打包、摘要与签名

### 6.1 目录结构

```
com.example.my/
  manifest.json
  MyPlugin.exe
  MyPlugin.dll
  <其它依赖>
```

打成 ZIP 时**把这一层作为根**（不要多包一层文件夹；多一层时核心只在"恰好一个子目录"的情况下猜一层）。

发布版建议 `dotnet publish -c Release -r win-x64 --self-contained true`：容器里读不到系统 .NET 安装目录的概率很高，自包含最省事。

### 6.2 摘要

摘要算法（自己算的时候照做）：

1. 取包内所有普通文件，**排除** `manifest.json`、`active.json`、任何以 `.` 开头的目录（`.data`、`.staging`）
2. 按相对路径（`/` 分隔、大小写敏感、Ordinal 排序）排序
3. 依次写入：UTF-8 相对路径、`\0`、UTF-8 十进制字节数、`\0`、文件内容
4. SHA-256，小写十六进制

把结果填进 `integrity.sha256`。`manifest.json` 自己不参与摘要（会自引用）。

### 6.3 签名

对**上面那串十六进制字符串的 UTF-8 字节**做 SHA-256 签名，Base64 填入 `integrity.signature`：

```bash
# ECDSA P-256（openssl 默认 DER，核心也接受 P1363）
openssl dgst -sha256 -sign publisher.key -out sig.bin <(printf '%s' "$DIGEST")
base64 -w0 sig.bin

# RSA（PKCS#1 v1.5）
openssl dgst -sha256 -sign rsa.key -out sig.bin <(printf '%s' "$DIGEST")
```

用户把你的公钥贴进插件页的**受信任公钥**（或管理员配 `Plugins:TrustedPublisherKeys`），并把 `Plugins:IntegrityPolicy` 设为 `enforce` 后，篡改过的包会被拒绝启动。

> 当前版本没有"一键生成密钥 → 算摘要 → 签名"的脚本（ROADMAP P1-6），上面就是完整流程。

---

## 7. 调试与排错

### 7.1 看状态

`GET /api/plugins/{id}` 里有：

| 字段 | 用途 |
| --- | --- |
| `state` | 10 个状态之一（见 §7.2） |
| `lastError` | **先看这个**，多数问题直接写着原因 |
| `isolation` | `appcontainer:network` / `appcontainer:offline` / `none: 原因` |
| `discoveredCapabilities` | 运行期真正生效的能力（MCP 方言下看这个） |
| `declaredCapabilities` | 清单里声明的 |
| `inFlightInvocations` / `draining` | 排空进度 |

### 7.2 状态速查

| 状态 | 含义 | 怎么办 |
| --- | --- | --- |
| `Discovered` | 发现但没启用 | 启用 |
| `Starting` | 正在握手 | 卡住看 `lastError` |
| `Running` | 正常 | — |
| `Degraded` | 能调用但部分功能降级 | 看健康检查返回的 `detail` |
| `Failed` | 启动失败 | `lastError`（清单/入口/隔离大多在这里） |
| `NeedsConfiguration` | `config.required` 没填 | 填配置 |
| `PermissionRequired` | 权限没授 | 授权 |
| `Disabled` | 用户停用 | 启用 |
| `Draining` | 正在排空（准备升级） | 等 |
| `Stopped` | 已停止 | 重启 |

### 7.3 错误码

| 码 | 含义 | 通常是 |
| --- | --- | --- |
| `-32602` | 参数不合法 | 你的 schema 与实际入参不符 |
| `-32001` | 协议不兼容 | `protocol` 区间写错 |
| `-32002` | 未配置 | 先 `ConfigureAsync` |
| `-32003` | 权限不足 | 声明缺失或未授权 |
| `-32005` | 未知能力 | `capabilityId` 拼错 / 清单与代码不一致 |
| `-32006` | 过载 | 队列满，客户端收到 429 |
| `-32800` | 已取消 | 正常现象 |

### 7.4 常见故障

| 现象 | 原因 |
| --- | --- |
| 握手超时 | 你在 stdout 打了日志，或入口进程根本没起来 |
| `Failed` + "入口文件不存在" | `entry.command` 写的是路径但目录结构不对；宿主命令 + 包名形式是允许的 |
| `Failed` + 隔离报错 | 见 §5.2；`.runtimes` 或 `ReadableRoots` |
| 安装 400 | 清单校验失败，返回体里有具体字段 |
| 改了文件没生效 | 目录安装需要**重新扫描**（没有文件监听，见 §8.2） |

---

## 8. 加载、升级与回滚

### 8.1 安装即启动

`POST /api/plugins/install` 是两段式：先 `Stage`（只读校验清单/入口/摘要/签名），再落盘切 `active.json`，最后启动。**坏包在 Stage 就失败，此时旧版本仍在正常服务**——不会为了装一个坏包把线上插件停掉。

### 8.2 热加载的边界（重要）

| 操作 | 需要重启应用吗 |
| --- | --- |
| 安装 ZIP | ❌ 装完立刻启动 |
| 目录放进 `App_Data/plugins/` | ❌ 但要**点一下"重新扫描"**（`POST /api/plugins/rescan`） |
| 改插件配置 / 授权 | ❌ 自动重启该插件进程 |
| 改 `appsettings.json` 的 `Plugins` 段 | ✅ 需要重启 API |
| 升级/回滚 | ❌ 自动排空后重启进程 |

**没有 `FileSystemWatcher`**。把目录丢进去不会自动生效——这是刻意的：自动触发等于让"复制文件"变成"执行任意代码"的入口。

### 8.3 升级与排空

- 升级 = 换 `active.json` 指针 + 重启进程，**不原地覆盖文件**（Windows 上运行中的 exe 删不掉）。
- 升级前核心会先**排空**：停止接受新调用，等已发出的调用结束（默认最多 15 秒），再停进程。
- 想手动控制可以先 `POST /api/plugins/{id}/drain` 再上传。
- 想回滚：`POST /api/plugins/{id}/rollback`，回到上一个版本。
- 因此：**不要依赖超长单次调用**（超过排空超时可能被强停）；也不要依赖自己目录的绝对路径（版本目录会变）。

---

## 9. 发布检查清单

- [ ] 清单 `id`/`version`/`capabilities` 与代码里的 `PluginBase` 声明**完全一致**
- [ ] `version` 是合法 SemVer（三段）
- [ ] `parameters` / `config.schema` 是**字符串**，且是合法 JSON Schema
- [ ] 每个参数都有 `description`（模型靠它填对）
- [ ] 只声明真正需要的权限
- [ ] `sideEffect` 如实填写（写操作别标 `read`）
- [ ] 日志全部走 `PluginBase.Log()` / stderr，stdout 干净
- [ ] 持久化只用 `DataDirectory`
- [ ] 所有 `CancellationToken` 都传到了底层调用
- [ ] `scripts\Test-PluginContract.ps1` 通过
- [ ] 在**干净机器**上装一次（别只在自己开发机试）
- [ ] 发布包填了 `integrity.sha256`（有条件就签名）

---

## 10. 非 C# 插件

协议是语言无关的。要点：

1. 读 stdin 的**一行一个 JSON**，写 stdout 的**一行一个 JSON**（NDJSON）。
2. 实现 `plugin/initialize` → `plugin/configure` → `plugin/capabilities/list` → `plugin/capabilities/invoke` → `plugin/shutdown`，外加能处理 `plugin/health`、`plugin/cancel`。
3. 通知类消息（无 `id`）不需要响应。
4. 日志一律 stderr。
5. 清单照样写 `manifest.json`，`runtime` 填你的语言。

方法名、错误码、握手字段见 `docs/PLUGIN-AUTHOR-GUIDE.md` §4。目前没有 TypeScript/Python 官方 SDK（ROADMAP 后续），但协议本身很薄，照着 `MyFirstMafAgent.Plugins.Sdk/Rpc/JsonRpcPeer.cs` 抄一遍即可。

---

## 11. FAQ

**Q：我能把插件写成一个 DLL 让核心加载吗？**
不能，也不打算支持。安全边界依赖独立进程 + AppContainer。

**Q：插件能直接读写知识库数据库吗？**
不能。内容通过 `platform-connector` 返回值交给核心，由核心走摄取用例（版本、索引、Outbox 一致）。

**Q：为什么我的插件装完是 `PermissionRequired`？**
你在清单里声明了权限但用户没授权。这是默认行为，不是 bug。

**Q：为什么界面上看不到我的工具？**
`write-external` / `destructive` 的能力不会暴露给模型；另外顺序是"插件先 `Running`"，工具才会出现（`/api/agent/tools` 每次实时聚合，不需要重启）。

**Q：插件崩溃了会怎样？**
核心按退避重启，连续失败进 `Failed` 并记录 `lastError`，不影响其它插件。

**Q：能装多少个插件？**
没有数量上限；启动并发默认 4，可配 `Plugins:StartupConcurrency`。

**Q：Linux / macOS 上有沙箱吗？**
没有。会如实报 `unsupported`，`require` 模式直接拒绝启动。别指望在那边得到同等隔离。
