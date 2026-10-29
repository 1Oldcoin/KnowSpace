# 把知识库接给外部智能体（MCP 接入指南）

> 📘 **对外整合版**：`docs/MCP-INTEGRATION-MANUAL.md` §2（含 Cursor / Claude Desktop 配置与排查顺序）。
> ☁️ **云端部署**：`docs/CLOUD-DEPLOYMENT.md`（把知识库放云服务器、对外暴露前的安全清单、Docker/systemd/Windows 三种形态）。

本文只讲一件事：**怎么让 Cursor、Claude Desktop 或自研 Agent 通过 MCP 使用知识库**，以及它们被允许做什么。

---

## 1. 三条信任路径，别混用

| 面 | 谁在用 | 凭据 | 说明 |
| --- | --- | --- | --- |
| `/api` | 界面（浏览器 / 桌面窗口） | `X-Api-Token` | 桌面：一次性令牌，每次启动重生成、不落盘、不下发给渲染进程<br>服务端：长期令牌 `Server:ApiToken` / 环境变量 `MAF_SERVER_TOKEN`，或反向代理认证后注入 |
| `/mcp` | 外部智能体 | 客户端密钥 `Authorization: Bearer maf_…` 或 `X-Api-Key: maf_…`<br>**或** OAuth 访问令牌 `Authorization: Bearer eyJ…` | 两条通道并存，共用同一个准入闸门 |
| `/oauth/*`、`/.well-known/*` | OAuth 流程本身 | 各自的参数 + PKCE | 必须匿名可达：客户端在这之前还没有凭据 |
| 静态资源 | 浏览器 | 无 | 首页与 `assets/*` 不需要凭据 |

**为什么不是同一个令牌**：桌面令牌按设计不落盘，外部 Agent 拿不到；而如果为了对外把令牌固定下来，等于让本机上任何进程都能读走整个知识库。两件事必须分开。

> ⚠️ **云端部署必读**：`/api` 默认只靠 Electron 宿主注入令牌。直接把容器端口映射到公网，`/api` 是**不鉴权**的——扫到端口就能读写全部资料、问答记录，甚至读改 LLM 密钥。处理方式见 `docs/CLOUD-DEPLOYMENT.md` §3.1。

---

## 2. 三步接入

### 第一步：建一个客户端，拿到密钥

最省事的方式是界面：**设置 → AI 工具与 MCP → 管理接入客户端**（左侧导航「接入的 AI 工具」）。
那里能新建客户端、勾选能力与工具白名单、限制可见知识库、轮换密钥、看调用统计与审计明细，全部不用敲命令。

单页一次拿全量统计：

```powershell
curl.exe -s "http://127.0.0.1:5056/api/agent-clients/overview?windowHours=24"
```

返回每个客户端的 `online`（**近似值**：`enabled` 且最近 5 分钟内有调用）、`usage.total/success/denied/failed/lastCallAt`，
以及 `totals`。MCP 服务端是无状态的（`Stateless = true`），没有长连接可查，所以界面上写的是「最近活跃」而不是「已连接」。

脚本化建客户端：

```powershell
curl.exe -s -X POST http://127.0.0.1:5056/api/agent-clients `
  -H "Content-Type: application/json" `
  -d '{\"name\":\"Cursor\",\"capabilities\":[\"knowledge:read\"]}'
```

返回里的 `key` 形如 `maf_xxxxxxxx...`，**只出现这一次**。丢了就轮换：

```powershell
curl.exe -s -X POST http://127.0.0.1:5056/api/agent-clients/<clientId>/rotate
```

### 第二步：在客户端里配置服务器

Cursor（`~/.cursor/mcp.json`）或 Claude Desktop（`claude_desktop_config.json`）：

```json
{
  "mcpServers": {
    "myfirstmafagent": {
      "url": "http://127.0.0.1:5056/mcp",
      "headers": {
        "Authorization": "Bearer maf_这里换成你的密钥"
      }
    }
  }
}
```

### 第三步：验证

```powershell
# 看服务器是否连得上、工具是否齐全（用的是同一个 MCP 客户端 SDK）
curl.exe -s http://127.0.0.1:5056/api/mcp/status
```

---

## 3. 能授予什么能力

| 能力 | 覆盖的工具 |
| --- | --- |
| `knowledge:read` | `list_knowledge_bases`、`list_documents`、`search_knowledge_base`、`get_document_content`、`get_document_summary`、`get_document_status` |
| `knowledge:write` | `upload_document` |
| `workspace:read` | `workspace_list_files`、`workspace_read_file` |
| `workspace:write` | `workspace_write_file` |
| `files:read` | `list_file_roots`、`local_list_directory`、`local_read_file`、`local_search_files`、`local_grep` |
| `files:write` | `local_write_file` |
| `avatar:read` | `avatar_get_persona`、`avatar_search_memories`、`avatar_list_fragments`、`avatar_ask_as_me` |
| `avatar:write` | `avatar_feed`、`avatar_digest` |
| `chat:read` | `list_chat_sessions`、`chat_search_history` |

通配：`knowledge:*`（该作用域的读写全开）、`*`（全部）。

**默认只给 `knowledge:read`**。给外部 Agent 开 `files:read` 等于让它读你授权目录里的任意文件，开 `avatar:*` 等于让它读写分身记忆，开 `chat:read` 等于让它读你和助手、伴侣的私人对话——按需再加。

### `chat:read` 为什么单独一项

聊天历史比资料更敏感，因此**不复用** `knowledge:read` 或 `avatar:read`：只拿到知识库权限的客户端不应该顺带看见你聊过什么。两个工具的分工也说清楚——

| 工具 | 读的是什么 | 典型用法 |
| --- | --- | --- |
| `avatar_search_memories` | 已被 AI 整理成记忆卡的结论 | 「上次说他要考驾照」 |
| `list_chat_sessions` | 对话列表（知识库问答 / 分身对话 / 伴侣对话） | 「上次那个对话在哪」 |
| `chat_search_history` | 对话里的原话（另含导入的 QQ/微信/CSV 记录） | 「他原话是怎么说的」 |

来源过滤用 `source`：`knowledge` / `avatar` / `companion` / `companion-import` / `all`（默认）。名字写错会直接报错并列出可用值，不会静默返回空列表——空列表会被误读成「用户没聊过」。

`knowledgeBaseId` 与 `companionProfileId` 是**分支过滤器**，不是全局过滤：前者只作用于 `knowledge` 来源，后者只作用于 `companion` / `companion-import`。要「只看某个知识库的问答」请同时传 `source=knowledge`。

完整清单随时可查：

```powershell
curl.exe -s http://127.0.0.1:5056/api/agent-clients/catalog
```

---

## 4. 三级收窄：能力 → 工具白名单 → 知识库白名单

在动手之前，先把两个容易卡住的前提说清楚。

### 4.1 `X-Api-Token` 是什么，从哪来

它是**管理面 `/api` 的令牌**，跟给外部 Agent 用的 `maf_` 密钥完全是两回事：

| 令牌 | 谁的凭据 | 放在哪 | 能做什么 |
| --- | --- | --- | --- |
| `X-Api-Token` | **你（界面/管理员）** | 请求头 | 建库、传资料、建 MCP 客户端、改白名单 |
| `X-Api-Key: maf_…` | **外部 Agent** | 请求头 | 只能调工具，不能碰 `/api` |

它由谁产生，取决于你怎么跑起来的：

| 启动方式 | 令牌来源 | 怎么拿到 |
| --- | --- | --- |
| 桌面版（安装包 / `npm start`） | Electron 每次启动随机生成 32 字节，注入 `DESKTOP_API_TOKEN` | 应用运行期间读 `%LOCALAPPDATA%\MyFirstMafAgentData\runtime\token`（退出时自动删除） |
| 自己 `dotnet run` | 环境变量 `DESKTOP_API_TOKEN`（或 `MAF_SERVER_TOKEN`） | 你启动时自己设的，自己知道 |
| 裸机 / Docker 服务端 | 环境变量 `MAF_SERVER_TOKEN` | `deploy/linux/myfirstmafagent.env` 或 `.env` |

三种都读不到时（`Hosting:Profile=Desktop` 且没设任何令牌），管理面**不鉴权**，也不需要这个头——这正是桌面调试态。

桌面版的一行取法：

```powershell
$root  = Join-Path $env:LOCALAPPDATA 'MyFirstMafAgentData'
$info  = Get-Content (Join-Path $root 'runtime\desktop.json') -Raw | ConvertFrom-Json   # 含随机端口
$token = (Get-Content (Join-Path $root 'runtime\token') -Raw).Trim()
$base  = "$($info.url)/api"
```

服务端 / 自己跑的实例则是固定地址 + 环境变量里的令牌：

```powershell
$base  = 'http://127.0.0.1:5056/api'
$token = $env:MAF_SERVER_TOKEN
```

后面所有 `curl` 示例都假设你已经有了这两个变量。

### 4.2 建库不需要 `curl`（桌面版）

上一节那句 `POST /api/knowledge-bases` 只是接口事实，**日常不需要手敲**：界面侧栏工作区那一行的 **「+」** 就是"新建知识库"，**垃圾桶图标**是"删除当前知识库"（默认库置灰）。curl 只在写脚本或排查时用。

> **无界面部署（Linux / Docker）不适用**：那种环境下没有可用的浏览器界面
> （`services/api.ts` 里全是裸 `fetch`，没有任何注入 `X-Api-Token` 的代码），
> 建库、建客户端、改白名单全部要走 `curl` 并带 `X-Api-Token`。见 §4.4 开头的一键脚本。

### 4.3 客户端配置长什么样

```jsonc
{
  "name": "Claude Desktop",
  "capabilities": ["knowledge:read"],
  "allowedKnowledgeBaseIds": ["<某个知识库的 GUID>"],   // 空数组 = 不限
  "allowedTools": ["search_knowledge_base"],            // 空数组 = 本能力内全放开
  "requestsPerMinute": 60                               // 0 = 不限
}
```

规则：

- 知识库白名单**也管默认知识库**：不传 `knowledgeBaseId` 时按默认库判定，所以"省略参数"绕不过范围。
- **可选参数也一样受校验**：`chat_search_history` / `list_chat_sessions` 不强制要知识库，但只要显式传了 `knowledgeBaseId`，网关就会拿它对照白名单——否则「可选」就成了绕过范围的后门（真实修过：`AgentCallGate` 曾经对 `UsesKnowledgeBase: false` 的工具直接放行）。
- `get_document_content` 只带资料 ID，网关会回查资料属于哪个知识库再判定。
- `list_knowledge_bases` 只返回范围内的库（连名字也不外泄）。
- **知识库被删除后，客户端范围不会自动放宽**：白名单里那个 GUID 仍然存在，只是再也匹配不到任何知识库。所以「删掉库」不等于「回收授权」——受限客户端会退化成什么都访问不了，需要手工改它的白名单。这是刻意的：把空列表当作「不限」会让一次删除变成一次提权。删除知识库不会走 MCP，只有界面与 `DELETE /api/knowledge-bases/{id}` 两条路（都需要界面令牌），MCP 侧不提供建库/删库工具。

### 4.4 三条路具体怎么操作

先说清现状：**桌面版有完整的管理界面**——设置页「AI 工具与 MCP」卡片里的
「管理接入客户端」（左侧导航「接入的 AI 工具」）能新建客户端、勾选能力与工具白名单、
限定可见知识库、轮换密钥、看调用统计与审计明细，`allowedKnowledgeBaseIds` 也在那里编辑。
所以桌面用户不需要下面任何一条路。

**Linux / Docker 没有可用界面**（`/api` 全部 401，前端没有令牌输入口），
只能走路 A（curl + `X-Api-Token`）；路 B 需要能打开同意页；路 C 是应急手段。

#### 路 A 的前置：先把下面两个变量准备好

```bash
BASE=http://127.0.0.1:5056
TOKEN=<MAF_SERVER_TOKEN 的值>
```

#### 路 A 一键脚本（无界面部署推荐）

```bash
# 1) 拿知识库 GUID（建客户端时要填；空数组 = 不限全部库，别留空）
curl -s "$BASE/api/knowledge-bases" -H "X-Api-Token: $TOKEN" \
  | jq -r '.[] | "\(.id)\t\(.name)"'

# 2) 看能力名与工具名（拼错会被创建接口直接拒掉）
curl -s "$BASE/api/agent-clients/catalog" -H "X-Api-Token: $TOKEN" \
  | jq '{capabilities, tools: [.tools[].name]}'

# 3) 建客户端 —— 明文密钥只在这一步出现一次
curl -s -X POST "$BASE/api/agent-clients" \
  -H "X-Api-Token: $TOKEN" -H 'Content-Type: application/json' \
  -d '{
        "name": "本地Agent",
        "capabilities": ["knowledge:read"],
        "allowedKnowledgeBaseIds": ["<步骤1的GUID>"],
        "allowedTools": ["list_knowledge_bases","search_knowledge_base","get_document_content"],
        "requestsPerMinute": 60
      }' | jq -r '.key'
```

> 请求体与响应体用的是**同一个**字段名 `allowedKnowledgeBaseIds`（不是 `knowledgeBaseIds`）。
> 拼错不会报错，会被当成没传 → 变成"不限全部知识库"，这比报错更危险。
> 建完用 `GET /api/agent-clients/{id}` 复核一眼 `allowedKnowledgeBaseIds`。

| 日常操作 | 命令 |
| --- | --- |
| 改能力 / 库 / 工具 | `PUT /api/agent-clients/{id}`，未给出的字段保持原值 |
| 密钥丢了 | `POST /api/agent-clients/{id}/rotate` → 返回新 `key`，旧的立即失效 |
| 看调用统计 | `GET /api/agent-clients/overview?windowHours=24` |
| 看调用明细 | `GET /api/agent-clients/calls?clientId={id}&limit=50` |
| 临时停用 | `PUT` 带 `{"enabled": false}`，不用删记录 |

Server 形态下 `files:*` / `files:write` **不要给**：`McpModule.cs` 只在 `!profile.IsServer` 时注册
`LocalFileMcpTools`，这两个能力在云端根本没有对应工具，给了是空权限，但会让人误以为存在本机文件访问能力。

下面三条路里的 PowerShell 版本适用于桌面版（本机 `127.0.0.1:5056`）。

| 路 | 需要什么 | 适合 |
| --- | --- | --- |
| A `PUT /api/agent-clients/{id}` | 一个终端 + `X-Api-Token` | 任何情况；精确、可脚本化 |
| B OAuth 同意页勾选 | 客户端支持 OAuth；能打开同意页 | 一次性授权，不用记 GUID |
| C 宽松客户端自举 | 无 | 只想"先能连上"的临时验证 |

#### 路 A：`PUT /api/agent-clients/{id}` —— 精确指定，脚本化

```powershell
# 1) 看现有客户端（列表里没有明文密钥，只有前缀）
$clients = Invoke-RestMethod "$base/agent-clients" -Headers @{ 'X-Api-Token' = $token }
$clients.clients | Format-Table id, name, keyPrefix, capabilities, allowedKnowledgeBaseIds, requestsPerMinute

# 2) 看现有知识库，拿到 GUID
$kbs = Invoke-RestMethod "$base/knowledge-bases" -Headers @{ 'X-Api-Token' = $token }
$kbs | Format-Table id, name, isDefault
$keep = ($kbs | Where-Object { $_.name -eq '工作笔记' }).id

# 3) 改白名单（没写的字段保持原值；allowedKnowledgeBaseIds 传了就整体覆盖）
$body = @{
    capabilities            = @('knowledge:read')
    allowedKnowledgeBaseIds = @($keep)      # 空数组 @() = 不限制
    allowedTools            = @()
    requestsPerMinute       = 60
} | ConvertTo-Json
Invoke-RestMethod "$base/agent-clients/$($clients.clients[0].id)" -Method Put `
    -Headers @{ 'X-Api-Token' = $token } -ContentType 'application/json' -Body $body
```

`PUT` 的语义是"部分更新"：`name`、`description`、`capabilities`、`allowedTools`、`requestsPerMinute` 没写的字段保持原值（`null` = 不修改），
但 `allowedKnowledgeBaseIds` 只要传了就整体覆盖——传 `@()` 是"清空 = 不限制"，不是"不变"。想把受限改回不限制，必须显式传空数组。

> **PowerShell 的坑**：`@($keep)` 序列化成 `["guid"]`（数组），而 `$keep` 直接赋值会序列化成 `"guid"`（字符串），后者接口会 400。
> 只有一个知识库时也必须写 `@($keep)`。

如果连客户端都还没有，先建一个（响应里的 `key` 只出现这一次）：

```powershell
$created = Invoke-RestMethod "$base/agent-clients" -Method Post `
    -Headers @{ 'X-Api-Token' = $token } -ContentType 'application/json' -Body (@{
        name                    = 'Claude Code'
        capabilities            = @('knowledge:read')
        allowedKnowledgeBaseIds = @($keep)
        allowedTools            = @()
        requestsPerMinute       = 60
    } | ConvertTo-Json)
"密钥（只显示一次）： $($created.key)"
```

#### 路 B：OAuth 同意页勾选

```text
客户端发起 OAuth 授权 → 浏览器打开 https://<你的域名>/oauth/authorize?…
  → 页面「可访问的知识库」区勾选需要的库（默认全勾）
  → 「同意授权」
```

勾选结果会直接写进该客户端的 `allowedKnowledgeBaseIds`，不需要任何 curl。
前提是能打开同意页：Desktop Profile 下从本机浏览器打开即可；Server Profile 走 `ConsentAccessGuard`（回环来源，或带 `X-Api-Token` 的请求），
公网直接访问会 403，详见 `docs/MCP-REMOTE-DESIGN.md` §3.2。

**注意**：同意页的勾选框默认是**全选**，顺手点「同意」会把新加的库一并授权。只想给一部分时，先取消全选再逐个勾。

#### 路 C：先用宽松客户端自举 —— 应急

1. 建一个 `allowedKnowledgeBaseIds` 为**空**的客户端（空 = 不限制），能力只给 `knowledge:read`。
2. 用它调 `list_knowledge_bases`，拿到全部 GUID。
3. 再按路 A 把这个客户端收窄。

**别把这个宽松客户端留在生产上**——它拥有完整的知识库读权限。

#### 改完怎么验证

```powershell
# 受限客户端应当只看到自己范围内的库
# 桌面版：$mcp = "$($info.url)/mcp"；服务端：$mcp = 'http://127.0.0.1:5056/mcp'
$mcp = 'http://127.0.0.1:5056/mcp'
curl.exe -s $mcp -H "Content-Type: application/json" -H "X-Api-Key: maf_xxx" `
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_knowledge_bases","arguments":{}}}'
```

返回的数组条数应当等于你配的 GUID 个数。再传一个范围外的 `knowledgeBaseId` 去调 `search_knowledge_base`，
应当收到 `-32003` 与一句列明允许范围的错误，而不是空结果——空结果会被误读成"库里没这篇"。


---

## 5. 拒绝是什么样子

| 场景 | HTTP | 响应 |
| --- | --- | --- |
| 没带密钥 / 密钥错 | 401 | `{"jsonrpc":"2.0","id":null,"error":{"code":-32001,...}}` + `WWW-Authenticate` |
| 客户端被停用 | 403 | `-32003 客户端已停用` |
| 来源 IP 鉴权失败过多 | 429 | `Retry-After: <秒>` + `-32006` |
| 超过每分钟上限 | 429 | `Retry-After: <秒>` + `-32004` |
| 没这项能力 / 不在白名单 / 越界知识库 | 200 | `-32003` + 可读中文原因 |
| 工具不存在 | 200 | `-32601` |
| 请求体超过上限 | 413 | `-32600` |

关于 `-32006`：密钥是 256 位随机值猜不中，这个闸门防的是**无上限的失败重试**打满日志/磁盘。
按**来源 IP** 计数（不是客户端 ID——失败请求根本没有客户端 ID），默认 30 次/分钟触发，封 300 秒，
成功一次即清零。阈值由 `Server:FailedAuthLimitPerMinute` / `FailedAuthBlockSeconds` 控制，`0` 表示不限。

工具级拒绝故意用 HTTP 200 + JSON-RPC 错误：模型能读到原因并改道（比如改用 `list_documents`），而不是拿到一个无法理解的传输错误。

> **`tools/list` 的现实行为**：它返回的是**全部**已登记工具，不按客户端能力裁剪。
> 也就是说受限客户端会「看得见但调不通」。当前的取舍是把权限判定集中在 `tools/call` 的
> 闸门上，而不是在响应体重写上做过滤——后者要改 MCP 传输层，风险远高于收益。
> 因此接第三方 Agent 时，`-32003` 必须当作「换个工具再做」的正常分支来处理。

---

## 6. 管理端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/agent-clients` | 列表（只给前缀，不含密钥） |
| GET | `/api/agent-clients/overview?windowHours=` | 列表 + 在线近似值 + 窗口内调用统计（界面用的单请求聚合） |
| GET | `/api/agent-clients/catalog` | 能力与工具清单 |
| GET | `/api/agent-clients/{id}` | 单个客户端 |
| GET | `/api/agent-clients/calls?clientId=&limit=` | 调用审计（时间倒序） |
| POST | `/api/agent-clients` | 新建，返回明文密钥**一次** |
| PUT | `/api/agent-clients/{id}` | 改能力/白名单/额度/启停 |
| POST | `/api/agent-clients/{id}/rotate` | 轮换密钥（旧密钥立即失效） |
| DELETE | `/api/agent-clients/{id}` | 删除（审计历史保留） |
| GET | `/api/mcp/diagnostics` | 协议诊断（Legacy/Modern、版本、方法计数）+ 通道状态 |
| GET | `/api/mcp/local-config` | 一键复制的 Cursor / Claude Desktop / curl 配置（含真实端口） |
| GET | `/api/mcp/auth-events?limit=` | **认证事件**审计（与工具调用审计分开） |
| GET | `/api/mcp/oauth-consents` | OAuth 授权同意列表 |
| POST | `/api/mcp/oauth-consents/{id}/revoke` | 撤销同意（已签发令牌立即失效） |

---

## 7. 配置

```json
"Mcp": {
  "EnableServer": true,
  "ServerPath": "/mcp",
  "RequireClientKey": true,
  "DefaultRequestsPerMinute": 120,
  "MaxAgentClients": 50,
  "EnableCallAudit": true,
  "MaxRequestBodyBytes": 8388608
}
```

- `RequireClientKey=false` 只留给本机调试：关掉之后 `/mcp` 不鉴权、不限流、不审计，启动日志会打警告。
- `MaxRequestBodyBytes` 必须够大以容纳 `upload_document` 的正文，网关需要读参数才能判定权限。

服务端部署（云主机 / 容器 / 内网）另需：

```json
"Server": {
  "ApiToken": "",                    // 留空则不启用；生产用环境变量 MAF_SERVER_TOKEN
  "AllowAnonymousHealth": false,      // 云端设 true，让 LB/HEALTHCHECK 探活
  "TrustForwardedHeaders": false,     // 放在反向代理后面时必开
  "KnownProxies": [],                 // 代理不同机时填它的 IP
  "FailedAuthLimitPerMinute": 30,
  "FailedAuthBlockSeconds": 300,
  "HideServerHeader": true
}
```

> `TrustForwardedHeaders` 不开是一个**真实漏洞**：`RemoteIpAddress` 恒为代理地址，于是审计来源 IP 全变
> `127.0.0.1`（无法溯源），且 `/api/desktop/shutdown` 的回环校验会把经代理进来的请求当成本机请求——
> 公网任何人都能关掉服务。详见 `docs/CLOUD-DEPLOYMENT.md` §3.2。

---

## 8. 验证与排错

```powershell
# 协议层端到端（需要已发布的 API 在 5056 端口）
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-McpE2E.ps1
```

排错顺序：

1. `GET /api/health` 通不通（数据库/端口）。
2. 不带密钥打 `/mcp` 是否 401（应该是）；如果 200，说明 `RequireClientKey` 被关掉了。
3. 带密钥失败 → 看 `GET /api/agent-clients/calls?limit=20` 的 `outcome` 与 `error`，拒绝原因写在里面。
4. 客户端显示"连上了但没有工具" → 检查能力是否为空（能力为空 = 什么都调不了）。
5. 云上出现 `429 -32006` → 触发失败闸门；等 `Retry-After` 秒，或确认密钥是否正确。
6. 审计里来源 IP 全是 `127.0.0.1` → 没开 `Server:TrustForwardedHeaders`。

---

## 9. 边界

- 客户端密钥是**长期凭据**，不是 OAuth：不要在公共机器上长期保存，怀疑泄露就轮换。当前**没有有效期字段**，建议每季度主动轮换一次。
- 子进程/工具仍以当前用户身份运行，访问 `/mcp` 的 Agent 能读到的内容 = 该客户端被授予的范围，不是操作系统级隔离。
- 审计不含请求正文，因此"谁搜了什么词"不会入库；如果需要，请在网关层另行加字段前先评估隐私影响。
- 应用本身不终止 TLS。密钥是 Bearer 型凭据，**明文 HTTP 上等于公开**——云端必须由反向代理提供 HTTPS。
- **请求必须是单条 JSON-RPC 消息**（`POST` + `application/json`）。批量数组、重复属性名、非 JSON 的 Content-Type 一律被网关拒绝（400 / 415）——这些在旧实现里会被当成"无需判定"而放行，属于越权面。
- `/mcp` 校验 `Host` 与 `Origin`：反代后若对外域名与 `Mcp:OAuth:Issuer` / `Server:PublicBaseUrl` 不一致，需在 `Mcp:AllowedHosts` 里登记；浏览器类工具（如 MCP Inspector）的源要登记在 `Mcp:AllowedOrigins`，否则 403。
- **受限客户端（配了知识库白名单）读不到私人对话**：`chat:read` 只覆盖知识库问答，分身 / 伴侣 / 导入来源需要"不限制知识库"的客户端才能看到。
- 服务端形态（`Hosting:Profile=Server`）**不暴露** `local_*` 文件工具 —— 云上的"本机目录"是服务器的文件系统。

## 8. OAuth 2.1（可选主路径）

从「复制密钥贴进配置」升级到「浏览器点一下同意」时启用。完整实现与验收见 [`MCP-OAUTH.md`](MCP-OAUTH.md)，这里只列使用者需要知道的四件事。

```jsonc
"Mcp": {
  "OAuth": {
    "Enabled": true,
    "Issuer": "https://knowspace.example.com"   // 必须与公网访问地址一致
  }
}
```

1. **OAuth 与 `maf_` 密钥并存**。OAuth 是普通用户的主路径；密钥通道留给 CI、脚本和不支持 OAuth 的客户端。Cursor 的刷新令牌有已知问题，这时切回密钥配置即可。
2. **客户端注册顺序是「预注册 → CIMD → DCR → 手动」**。授权服务器在元数据里声明 `client_id_metadata_document_supported: true`，支持 CIMD 的客户端会把 `client_id` 设成 HTTPS URL，由服务端拉取（只允许 HTTPS、拒绝重定向、拒绝私有地址、连接已校验 IP）。
3. **`aud` 强制绑定**：授权请求与令牌请求都必须带 `resource`，`/mcp` 校验 `aud` 必须等于本资源标识。签给别的资源的令牌拿过来是 401。
4. **scope 不等于数据库权限**。同意页选中的知识库会写进 `oauth_consents`，每次 `tools/call` 仍然逐次判定；撤销同意后旧令牌下一次请求就失效。
5. **`/oauth/token` 与 `/oauth/register` 有按来源 IP 的失败闸门**（与 `/mcp` 共用同一个 30 次/分钟 → 封 300 秒的窗口）。它们可以在无任何凭据的情况下被调用，所以必须限速；被封时返回 `429` + `error: temporarily_unavailable` + `Retry-After`。

401 响应会带 `WWW-Authenticate: Bearer realm=…, resource_metadata="…/.well-known/oauth-protected-resource"`，支持 OAuth 的客户端据此自动走授权流程。

6. **同意页的访问闸门（当前版本）**：`/oauth/authorize` 只接受两类来源 —— 回环地址（本机浏览器），或携带有效 `X-Api-Token`（`Server:ApiToken` / `MAF_SERVER_TOKEN`）的请求。浏览器本身加不了请求头，所以公网部署要由反向代理在完成自己的认证后注入该头；不注入就只能从服务器本机完成授权，或改用 `maf_` 密钥。账号登录 + MFA 见 [`MCP-REMOTE-DESIGN.md`](MCP-REMOTE-DESIGN.md) §3.2（Phase 1）。

---

## 9. 协议版本与客户端兼容

服务端由 MCP SDK 2.2.0 提供双世代支持：

| 客户端世代 | 第一个请求 | 服务端行为 |
| --- | --- | --- |
| Legacy（…2025-11-25） | `initialize` | 正常握手，回 `InitializeResult` |
| Modern（2026-07-28） | `server/discover` | 回 `supportedVersions` + `capabilities`；不再有 `Mcp-Session-Id` |

`server/discover` **不需要凭据**（只返回静态能力，不碰业务数据）；数据面仍然要认证。想看「我的客户端到底协商到了哪个版本」，看：

```powershell
curl.exe -s http://127.0.0.1:5056/api/mcp/diagnostics
```

返回里有 `recent[]`（每个 `/mcp` 请求的 `firstMethod` / `protocolVersion` / `era` / 状态）和 `methodCounts`。这是「连接不通」时最有用的第一现场证据。

---

## 10. 本地配置：一键复制

桌面版默认使用**专属端口 5056**（`Server:FixedLocalPort`）。它被占用时会自动回退到随机回环端口，
所以别把地址写死——设置页「设置 → AI 工具与 MCP → MCP 接入配置」里有两个按钮，
直接把带**真实当前端口**的配置复制到剪贴板：

- **Cursor**：`url` + `headers.Authorization`
- **Claude Desktop**：`npx -y mcp-remote <url> --header "Authorization: Bearer maf_…"`

对应的接口是 `GET /api/mcp/local-config?clientKey=maf_…`（不传 `clientKey` 就生成不含鉴权头的本，之后自己补）。

| `Server:FixedLocalPort` | 行为 |
| --- | --- |
| `5056`（默认） | 每次启动都用 5056；**被占用时自动改用随机端口** |
| `0` | 每次启动由系统分配随机端口 |

两种都只绑 `127.0.0.1`。想临时改成随机而不改文件：设环境变量 `MAF_FIXED_LOCAL_PORT=0`（它优先于配置文件）。
注意安装目录里的 `appsettings.json` 会在升级时被替换，想长期生效请用环境变量。

### 服务端部署：`Server:PublicBaseUrl`

「一键复制」的地址按以下优先级解析，返回值里的 `baseUrlSource` 会告诉你这次用了哪一个：

| 优先级 | 来源 | `baseUrlSource` | 使用场景 |
| --- | --- | --- | --- |
| 1 | `Mcp:OAuth:Issuer` | `oauth-issuer` | 开了 OAuth：`aud` 按 issuer 签发，配置片段必须与之一致 |
| 2 | `Server:PublicBaseUrl` | `public-base-url` | 服务端部署、未开 OAuth |
| 3 | Kestrel 监听地址 | `kestrel` | 桌面版：默认 5056，被占用时是随机回环端口 |
| 4 | 请求的 scheme/host | `request` | 兜底 |

```jsonc
"Server": {
  "PublicBaseUrl": "https://knowspace.example.com"
}
```

它同时影响两件事：①生成给人看的配置字符串；②`/mcp` 的 `Host`/`Origin` 允许列表
（`McpTransportGuard` 会把它和 `Mcp:OAuth:Issuer` 的主机一起加进白名单）。
它**不影响令牌的 `iss`/`aud`**（后者只认 `Mcp:OAuth:Issuer`，且绝不从请求头推导）。
反代后不填它，`/mcp` 会因为 `Host` 不在白名单返回 403。
如果既没开 OAuth 又没填它，而你又从别的机器打开管理页，`notes[]` 里会出现一条「回环地址只在本机有效」的提醒，前端会把它显示成黄色警告。

---

## 11. Server Card 与 mDNS

- `GET /.well-known/mcp/server-card.json`（云端与本地都有）：描述端点、支持的协议版本、认证方式、工具分类。**当前是 Draft，本项目不围绕它做任何功能依赖**——客户端支持了是锦上添花，不支持也一切照常。
- mDNS **默认关闭**（`Server:EnableMdns=false`）。它是局域网「发现」协议，不是授权协议：不能证明客户端可信，也不能替代认证。只有用户显式开启局域网共享时才应打开。当前版本提供开关与配置项，实际 DNS-SD 广播留到「局域网共享」真正要做时再实现，避免默认打开一个新的暴露面。
- **`EnableMdns=true` 目前不生效，但会吵一句**：启动时打一条 `LogWarning`，明确说「服务名不会出现在局域网上、本次启动没有任何发现行为、请改用 `/api/mcp/local-config`」。这样用户不会误以为这是客户端的问题（详见 `ServerSecurity.WarnUnimplementedMdns`）。
- **没有 mDNS 也不影响接入**：Cursor / Claude Desktop 都不做 mDNS 发现，它们只接受一条写死的 URL 或 stdio 命令。真正可用的入口是 `GET /api/mcp/local-config`（可粘贴的完整配置块）与 `GET /.well-known/mcp/server-card.json`（机器可读描述），两者都是**拉取式**、地址来源确定。

## 相关文档

- [`CLOUD-DEPLOYMENT.md`](CLOUD-DEPLOYMENT.md) —— 云服务器部署与 MCP 接入（安全清单、三种部署形态、密钥生命周期）
- [`MCP-OAUTH.md`](MCP-OAUTH.md) —— OAuth 2.1 实现、CIMD/SSRF 防线、验收清单与风险
- [`MCP-INTEGRATION-MANUAL.md`](MCP-INTEGRATION-MANUAL.md) —— MCP 两个方向的完整说明书
- [`MCP-INTEGRATION-MANUAL.md`](MCP-INTEGRATION-MANUAL.md) —— 反方向（把现成 MCP server 接进来 / 做成插件）的完整说明书
- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) §9.10 —— 授权模型的设计取舍
