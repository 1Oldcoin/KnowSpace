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

通配：`knowledge:*`（该作用域的读写全开）、`*`（全部）。

**默认只给 `knowledge:read`**。给外部 Agent 开 `files:read` 等于让它读你授权目录里的任意文件，开 `avatar:*` 等于让它读写分身记忆——按需再加。

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
