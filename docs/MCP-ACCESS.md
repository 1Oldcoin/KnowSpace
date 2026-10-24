# 把本机知识库接给外部智能体（MCP 接入指南）

本文只讲一件事：**怎么让 Cursor、Claude Desktop 或自研 Agent 通过 MCP 使用本机的知识库**，以及它们被允许做什么。

---

## 1. 三条信任路径，别混用

| 面 | 谁在用 | 凭据 | 说明 |
| --- | --- | --- | --- |
| `/api` | 桌面窗口里的前端 | 桌面一次性令牌 `X-Api-Token` | 每次启动重新生成，不落盘、不下发给渲染进程 |
| `/mcp` | 外部智能体 | 客户端密钥 `Authorization: Bearer ...` 或 `X-Api-Key: ...` | 长期有效，可轮换/停用/删除 |
| 静态资源 | 浏览器 | 无 | 首页与 `assets/*` 不需要凭据 |

**为什么不是同一个令牌**：桌面令牌按设计不落盘，外部 Agent 拿不到；而如果为了对外把令牌固定下来，等于让本机上任何进程都能读走整个知识库。两件事必须分开。

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

---

## 5. 拒绝是什么样子

| 场景 | HTTP | 响应 |
| --- | --- | --- |
| 没带密钥 / 密钥错 | 401 | `{"jsonrpc":"2.0","id":null,"error":{"code":-32001,...}}` + `WWW-Authenticate` |
| 客户端被停用 | 403 | `-32003 客户端已停用` |
| 超过每分钟上限 | 429 | `Retry-After: <秒>` + `-32004` |
| 没这项能力 / 不在白名单 / 越界知识库 | 200 | `-32003` + 可读中文原因 |
| 工具不存在 | 200 | `-32601` |
| 请求体超过上限 | 413 | `-32600` |

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

---

## 9. 边界

- 客户端密钥是**长期凭据**，不是 OAuth：不要在公共机器上长期保存，怀疑泄露就轮换。
- 子进程/工具仍以当前用户身份运行，访问 `/mcp` 的 Agent 能读到的内容 = 该客户端被授予的范围，不是操作系统级隔离。
- 审计不含请求正文，因此"谁搜了什么词"不会入库；如果需要，请在网关层另行加字段前先评估隐私影响。
