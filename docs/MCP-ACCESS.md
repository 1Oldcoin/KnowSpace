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

桌面版端口是**随机回环端口**，只有后端知道。设置页「AI 工具与 MCP → MCP 接入配置」里有两个按钮，直接把带真实端口的配置复制到剪贴板：

- **Cursor**：`url` + `headers.Authorization`
- **Claude Desktop**：`npx -y mcp-remote <url> --header "Authorization: Bearer maf_…"`

对应的接口是 `GET /api/mcp/local-config?clientKey=maf_…`（不传 `clientKey` 就生成不含鉴权头的版本，之后自己补）。
需要固定端口时设 `Server:FixedLocalPort`（默认 `0` = 随机）；这只影响端口可预测性，**监听地址仍然只有回环**，文档标注「仅建议单机可信环境」。

### 服务端部署：`Server:PublicBaseUrl`

「一键复制」的地址按以下优先级解析，返回值里的 `baseUrlSource` 会告诉你这次用了哪一个：

| 优先级 | 来源 | `baseUrlSource` | 使用场景 |
| --- | --- | --- | --- |
| 1 | `Mcp:OAuth:Issuer` | `oauth-issuer` | 开了 OAuth：`aud` 按 issuer 签发，配置片段必须与之一致 |
| 2 | `Server:PublicBaseUrl` | `public-base-url` | 服务端部署、未开 OAuth |
| 3 | Kestrel 监听地址 | `kestrel` | 桌面版随机回环端口 |
| 4 | 请求的 scheme/host | `request` | 兜底 |

```jsonc
"Server": {
  "PublicBaseUrl": "https://knowspace.example.com"
}
```

它**只影响生成给人看的配置字符串**，不参与任何鉴权判定，也不影响令牌的 `iss`/`aud`（后者只认 `Mcp:OAuth:Issuer`，且绝不从请求头推导）。
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
- [`MCP-INTEGRATION.md`](MCP-INTEGRATION.md) —— 速查版
- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) §9.10 —— 授权模型的设计取舍
