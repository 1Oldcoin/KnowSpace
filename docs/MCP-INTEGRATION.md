# MCP 接入教程

> 📘 **对外整合版**：`docs/MCP-INTEGRATION-MANUAL.md`（两个方向 + 真实配置示例 + 排查表）。本文保留为速查。

本项目与 MCP 有两个方向，**别搞混**：

| 方向 | 你是谁 | 要看的文档 |
| --- | --- | --- |
| **A. 把现成 MCP server 当插件载入** | 插件作者 / 用户，想用别人的 MCP server | **本文 §2–§7** |
| **B. 把本应用当 MCP server 给别的客户端用** | 想让 Claude Desktop / Cursor / 其它 Agent 调用本应用的知识库、文件、聊天记录 | `docs/MCP-ACCESS.md` |

本文只讲 A。B 是"本应用对外提供工具"，和插件系统无关。

---

## 1. 一句话结论

**已经写好的 stdio MCP server，加一份十几行的清单就能载入**，不用改它的代码：

```bash
# 本来你会这么跑它
npx -y @modelcontextprotocol/server-filesystem /data
```

把它变成插件，就是写一个 `plugin.json`：

```json
{
  "schemaVersion": "1.0",
  "id": "filesystem",
  "name": "文件系统",
  "version": "1.0.0",
  "dialect": "mcp",
  "entry": {
    "command": "npx",
    "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"]
  },
  "protocol": { "minVersion": "1.0.0", "maxVersion": "1.0.999" }
}
```

放进 `App_Data/plugins/filesystem/`，点 **重新扫描**，它就 `Running` 了。能力（工具）由核心在握手后通过 `tools/list` 自动发现，**清单里不用写 `capabilities`**。

---

## 2. 前提：传输方式必须是 stdio

| MCP 传输 | 支持 | 说明 |
| --- | --- | --- |
| **stdio**（子进程 + stdin/stdout） | ✅ | 本文的全部内容 |
| HTTP / SSE（远程 URL） | ❌ | 插件是子进程，核心不会连远程 MCP 端点 |

如果你的 MCP server 只提供 HTTP/SSE，两个办法：用官方 `mcp-remote` 之类的 stdio↔HTTP 桥接包成子进程，或者等后续版本支持远程源。

---

## 3. 清单怎么写

### 3.1 最小清单

```json
{
  "schemaVersion": "1.0",
  "id": "demo-mcp",
  "name": "演示 MCP",
  "version": "0.4.2",
  "dialect": "mcp",
  "entry": { "command": "node", "args": ["server.js"] }
}
```

注意与实际 plugins 的区别：

| 字段 | 自有协议 | MCP 方言 |
| --- | --- | --- |
| `capabilities` | **至少 1 项**，必须写全 | 可为空 / 省略（来自 `tools/list`） |
| `protocol` | 闭区间必须包含 `1.0.0` | **不做区间校验**（版本由 MCP 自己协商） |
| `id` | 小写反向域名，段内 `a-z0-9-` | 放宽为单段，允许 `a-z0-9._-`（如 `demo-mcp`） |
| `parameters` | 你写 JSON Schema | 来自工具自身的 `inputSchema` |

### 3.2 `dialect` 的三种写法

```json
{ "dialect": "mcp" }
{ "protocol": "mcp" }
{ "protocol": { "dialect": "mcp" } }
```

三种都认。第二种最容易踩坑：`"protocol": "mcp"` 是**字符串**，声明的是方言而不是版本区间，核心会自动补默认协议区间，并在校验时给一条 warning 提示。

### 3.3 entry 的写法

```json
{ "entry": { "command": "npx", "args": ["-y", "@scope/server"] } }
{ "entry": { "command": ["npx", "-y", "@scope/server"] } }
{ "entry": { "command": "node", "args": ["C:/plugins/mine/server.js"], "workingDirectory": "C:/plugins/mine" } }
```

`command` 写成数组（Claude Desktop 的 `mcpServers` 风格）也认。`args` 是 `arguments` 的别名。

---

## 4. 权限声明（关键一步）

MCP 自己的清单里**没有**权限概念。你得在本项目的清单里补上，否则：

- 插件跑起来但**没有网络**（Windows AppContainer 默认 offline）
- 需要写文件的能力会失败

```json
{
  "id": "demo-mcp",
  "dialect": "mcp",
  "permissions": ["network", "file-read"],
  "entry": { "command": "npx", "args": ["-y", "@scope/server"] }
}
```

顶层 `permissions` 对**所有**工具生效。也可以给单个工具单独声明：

```json
{
  "dialect": "mcp",
  "capabilities": [
    { "id": "echo", "kind": "agent-tool", "sideEffect": "read" },
    { "id": "wipe-storage", "kind": "agent-tool", "sideEffect": "destructive", "permissions": ["file-write"] }
  ]
}
```

原则：**首跑先不写权限，看它要什么，再按需补**。声明了 `network` 的插件会停在 `PermissionRequired` 等用户授权。

---

## 5. 装进去

### 5.1 目录方式（最快）

```
App_Data/plugins/demo-mcp/
  plugin.json            ← 或者 manifest.json / skill.json
  server.js              ← 你的 MCP server
  node_modules/…
```

然后 `POST /api/plugins/rescan`，或界面点 **重新扫描**。

### 5.2 ZIP 方式

打成一个 ZIP（叶子是 plugin.json）→ 界面 **安装** 或：

```bash
curl -X POST http://127.0.0.1:5000/api/plugins/install \
  -H "X-Api-Token: <桌面令牌>" \
  -F "package=@demo-mcp.zip"
```

### 5.3 验证

```bash
curl -s http://127.0.0.1:5000/api/plugins/demo-mcp -H "X-Api-Token: <token>"
```

成功的标志：

```json
{
  "state": "Running",
  "dialect": "mcp",
  "isolation": "appcontainer:network"   // 或 offline
  "discoveredCapabilities": ["echo", "wipe-storage"]
}
```

日志里应该出现类似：

```
MCP server demo-mcp introduced itself as 'demo-mcp-server' 0.4.2 (protocol 2024-11-05)
ready with 2 capability(ies): echo, wipe-storage
```

`discoveredCapabilities` 是**运行期发现**的那批；`declaredCapabilities` 是清单里写的。MCP 方言下前者才是真的。

---

## 6. 工具怎么暴露给模型

- 每个 MCP 工具会被桥接成一个 Agent 工具，名字形如 `plugin__demo-mcp__echo`（`插件__插件id__工具名`）。
- 只有 `read` / `write-local` 的工具会出现在 `/api/agent/tools`。
- 被判定为 `destructive` / `write-external` 的工具**不会**给模型——模型没法提供人工确认。
- 工具列表**实时聚合**，插件启动后不用重启应用。

调一次看看：

```bash
curl -s -X POST http://127.0.0.1:5000/api/plugins/demo-mcp/invoke \
  -H "X-Api-Token: <token>" -H "Content-Type: application/json" \
  -d '{"capabilityId":"echo","arguments":{"text":"hello"}}'
```

需要确认的工具必须带 `"confirm": true`，否则 403。

---

## 7. 常见问题

**Q：握手成功但工具列表是空的。**
少数 server 只在收到 `notifications/initialized` 后才响应 `tools/list`。核心会发这个通知；如果是自己写的 server，确认它的 `initialize` 返回里有 `capabilities.tools`。

**Q：`Failed`，`lastError` 说入口读不到。**
AppContainer 读不到 `Program Files` 下的 `node.exe`。把 node 放进 `<plugins>/.runtimes/node/`，清单里继续写 `"command": "node"`（核心优先在 `.runtimes` 里解析裸命令）。

**Q：`npx -y` 卡在下载。**
容器里可能没有网络权限，或者 `npx` 缓存目录不可写。建议**预装依赖**再放进插件目录，用 `"command": "node", "args": ["server.js"]` 启动，避免运行时下载。

**Q：工具名里带 `__`，模型会不会用错？**
桥接名是稳定契约，别手动改名。界面上显示的是工具的 `description`（来自 `tools/list`），写清楚描述比纠结名字更重要。

**Q：能同时装多个 MCP server 吗？**
能，每个插件一个进程、一份清单，互不影响。

**Q：MCP 的 resources / prompts 支持吗？**
目前只桥接 `tools`。`resources`、`prompts`、`sampling` 未实现。

---

## 8. 从 MCP 到原生插件，什么时候该换

| 情况 | 建议 |
| --- | --- |
| 已有 MCP server，只想用 | **别改**，直接按本文接入 |
| 需要 `platform-connector`（把外部内容同步进知识库） | 写原生插件——MCP 没有这个概念 |
| 需要配置表单（`config.schema` 自动渲染） | 写原生插件，MCP 清单没有配置项 |
| 需要精细的副作用分级与确认流 | 写原生插件（MCP 工具默认按 `read`/`destructive` 粗分） |
| 只是想让模型多几个工具 | MCP 就够 |

两种可以共存：同一个能力集，MCP 版给别人复用，原生版做知识库同步。
