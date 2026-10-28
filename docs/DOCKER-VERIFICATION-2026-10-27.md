# 容器化验证记录 · 2026-10-27

> 来源：外部同事在 Windows 11 + Docker Desktop 4.41.2（WSL2 后端）上的实测交接说明。
> 本文是**脱敏后的版本**：原始记录里的 `MAF_SERVER_TOKEN`、`PG_PASSWORD`、MCP 客户端密钥
> 全部替换为 `<redacted>`，并补记了随后修复的问题。
>
> 时间：2026-10-27 21:42 ~ 23:50 · 仓库：`1Oldcoin/KnowSpace`（main）

---

## 一、结论

镜像构建成功、容器正常运行、MCP 服务端可用，**从入库到向量检索的完整链路已在 Linux 容器内跑通**。

| 项 | 结果 |
| --- | --- |
| 镜像 | `zhijian-api:integrated`，1.35 GB，多阶段构建（node:22-alpine → dotnet/sdk:10.0 → dotnet/aspnet:10.0） |
| 容器 | `knowspace-app-1`，`127.0.0.1:5056 → 5056`，`Up (healthy)`，RestartCount=0 |
| 数据库 | 容器内 PostgreSQL 17 + pgvector（端口 55432，仅容器内网），`/api/health` 返回 `{"status":"ok","database":"connected"}`，61 张表 |
| 前端 | `/` 返回 200，`assets/index-*.js` 334 KB 正常 |
| MCP 鉴权 | 无密钥 POST `/mcp` → 401（符合预期） |
| MCP 协议 | `initialize` → `zhijian-knowledge 1.0.0`（协议 2025-06-18）；`tools/list` → **24 个工具**；`tools/call` 正常 |
| 业务链路 | MCP `upload_document` 上传 → 后台切块+向量化 → `status: Ready`（chunkCount=1）→ `search_knowledge_base` 命中，相似度 1.0（精确匹配），中文无乱码 |
| 审计 | `/api/agent-clients/calls` 有完整调用记录（工具、耗时、outcome、来源 IP 172.18.0.1） |

> 验证时用的是当时的镜像名 `myfirstmafagent:integrated`。仓库已在同日把镜像名改为
> `zhijian-api:integrated`、compose 项目名固定为 `zhijian`（卷名因此变成 `zhijian_maf-app-data`）。

---

## 二、测试机缺什么，补了什么

### 1. GitHub 直连不通 → 用镜像站克隆

`github.com:443` 从该机不可达（`Failed to connect ... port 443`），`ghfast.top` / `kkgithub` 等镜像可达。

> ⚠️ **这是应急手段，不是推荐流程。** 第三方镜像 + `http.sslVerify=false` 等于放弃对
> 传输内容的一切身份校验，供应链上任何人都能替换代码。真的要这么干，至少：
>
> - 克隆后核对 `git log -1` 的提交 SHA 与 GitHub 上的一致；
> - 只用 `--depth 1 --single-branch` 拉当前分支，别把整段历史一起信任过来；
> - 尽快换成能直连的网络或配好代理。
>
> 另外 Docker Hub 与 `github.com` 的可达性是两回事：`mcr.microsoft.com`（.NET SDK/运行时）、
> `apt.postgresql.org`、`api.nuget.org` 通常都能通，只有 `github.com` 主站与 raw 域名被拦。

### 2. Docker Hub 不可达 → 给 daemon 配镜像加速

首次 `docker compose build` 报 `failed to resolve ... docker.io/docker/dockerfile:1: EOF`。
在 `%USERPROFILE%\.docker\daemon.json` 里加 `registry-mirrors`（保留原有 builder 配置）：

```json
"registry-mirrors": [
  "https://docker.m.daocloud.io",
  "https://docker.1ms.run",
  "https://docker.xuanyuan.me",
  "https://hub.rat.dev"
]
```

重启 Docker Desktop 后 `docker info` 可见 Registry Mirrors，拉取正常。

> 前端 `package-lock.json` 里的 `resolved` 指向 `registry.npmmirror.com`，所以 `npm ci` 不用额外配源。

### 3. Docker 引擎被冻结的 WSL 虚拟机锁死 → 重启电脑

现象：所有 `docker` 命令返回 500 / 管道不存在，`wsl -l -v` 直接挂起。
`logs\vm\init.log` 显示有一次 `Docker Desktop is pausing...`（VM 被 pause 后再没醒过来），
`vmmemWSL` 进程僵住。非管理员 `wsl --shutdown` 无效，最终重启电脑解决。

**建议**：如果经常闲置后被 pause，在 Docker Desktop → Settings → Resources 关掉 Resource Saver。

### 4. 宿主不需要装 .NET / Node / PostgreSQL

全部在镜像构建阶段完成（`dotnet publish` + `npm run build` + apt 装 `postgresql-17` 与
`postgresql-17-pgvector`）。宿主只需要 git + Docker。

### 5. 其他小坑（已规避，写下来避免重复踩）

- 本机只有 Windows PowerShell 5.1；PS 5.1 读 **UTF-8 无 BOM** 的 `.ps1` 会按 GBK 解析导致中文乱码报语法错 → 验证脚本带 BOM 保存。
- PowerShell 把字符串直接当 `-Body` 发时会按 Latin-1 编码，中文变 `?` → 必须传 UTF-8 字节数组 + `Content-Type: application/json; charset=utf-8`。
- `/mcp` 回包是 **SSE**（`event: message` / `data: {...}`），直接 `ConvertFrom-Json` 会失败，要先取 `data:` 行。

---

## 三、怎么用

```powershell
docker compose up -d          # 启动（一体模式：API + 前端 + PostgreSQL + pgvector 单容器）
docker compose ps             # 看状态
docker compose logs -f        # 跟日志
docker compose down           # 停止（数据卷保留）
docker compose build          # 改了代码后重建
```

浏览器打开 <http://127.0.0.1:5056>

### 配置（`.env`）

```
APP_PORT=5056
PUBLIC_BASE_URL=http://127.0.0.1:5056
MAF_SERVER_TOKEN=<redacted>        # /api 令牌，openssl rand -hex 32
PG_PASSWORD=<redacted>             # 内置 PG 口令
TRUST_FORWARDED_HEADERS=false
LLM_ENDPOINT=https://api.deepseek.com/v1
LLM_MODEL=deepseek-chat
LLM_API_KEY=<redacted>             # ← 测试时留空，导致摘要 401（见四.2）
AUTO_DOWNLOAD_WHISPER=false        # 关掉 Whisper 模型自动下载，避免首启动拉 1~3 GB
```

### 数据持久化

运行期数据全在命名卷里（`/app/App_Data`：`pgdata`、`uploads`、`logs`、`models`、OAuth 私钥）。
`docker compose down` 不会删；`docker compose down -v` 会连数据一起删。

---

## 四、当时发现的两个问题（均已修复）

### 1. 启动瞬间 4 条 `plugin_sync_states does not exist`

```
Npgsql.PostgresException 42P01: relation "plugin_sync_states" does not exist
  at SqlSugarPluginRegistry.ListScheduledAsync(...)
  at PluginSyncJobFeeder.FeedAsync(...)
```

**不是偶发，是确定性竞态**：`plugin_sync_states` 由 `PluginModule.QueueInitialization` 在
「主机已监听之后」的后台初始化里建表，而 `JobScheduler` 的第一轮补投几乎同时发生
（`lastFedAt = DateTimeOffset.MinValue`），4 条 lane 各撞一次。表建好后自愈。

**修复**：`PluginSyncJobFeeder.FeedAsync` 把 `42P01` 当作「表还没建好」，返回 0 并只记一次
`Information`，不再刷 4 条 `ERROR`。

### 2. `LLM_API_KEY` 为空 → 摘要生成 401

```
Summary generation failed: Chat API returned 401: Authentication Fails, Your api key: ****ocal is invalid
```

**真实原因不是「密钥填错」，而是根本没填**：两个模型客户端都有
`key = string.IsNullOrWhiteSpace(apiKey) ? "local" : apiKey` 的兜底 —— 本意是照顾 LM Studio
这类不校验密钥的本机服务，但对公网端点一视同仁，于是发出 `Authorization: Bearer local`，
远端回显时截断成 `****ocal`，看起来像密钥泄漏或者填错。

**修复**（两处）：

- 新增 `ModelEndpointAuthorization`：公网端点 + 缺密钥 → 直接抛中文异常，写清缺哪个配置项、三种填法；本机/内网端点维持占位值不变（LM Studio / Ollama 照旧可用）。
- 新增启动期警告 `WarnMissingLlmApiKey`：公网端点缺密钥时在日志里点名，不必等第一次提问才发现。

---

## 五、当时记下的口径不一致（已对齐）

| 项 | 当时 | 现在 |
| --- | --- | --- |
| `list_documents` 的 `knowledgeBaseId` | 描述写「可留空」，实际无默认值 → 不传就 `ArgumentException` | 已加 `= null`，与 `search_knowledge_base` / `upload_document` 一致 |
| compose 项目名 | 未固定，卷名跟着目录名走（`knowspace_maf-app-data`） | 固定 `name: zhijian`，卷名与目录无关 |
| 镜像名 | `myfirstmafagent:integrated` | `zhijian-api:integrated` |
| 仓库里的 `App_Data` | `pgdata` + `uploads` 一并提交（约 75 MB / 1609 个文件） | 已移出版本库，首次启动自动 `initdb` |

---

## 六、MCP 接入参考

### 客户端密钥

在应用内「设置 → MCP 客户端」创建，明文只显示一次。测试期间创建过的那一个已在交接后作废，
请重新生成，**不要**把明文写进任何会提交的文件。

| 项 | 说明 |
| --- | --- |
| 客户端 ID | `ac_<redacted>` |
| 密钥 | `maf_<redacted>` |
| 能力 | `knowledge:read`, `knowledge:write`（可列库/检索/读正文/上传） |
| 限流 | 0 = 不限 |

> `.env` 里的 `MAF_SERVER_TOKEN` 是 `/api` 的管理令牌，与 MCP 密钥是两套凭据，别混用。

### Cursor / Claude Desktop 配置

```json
{
  "mcpServers": {
    "knowspace": {
      "url": "http://127.0.0.1:5056/mcp",
      "headers": { "Authorization": "Bearer maf_xxxxxxxxxxxxxxxx" }
    }
  }
}
```

（`X-Api-Key: maf_...` 头等价于 `Authorization: Bearer`。Claude Desktop 走 stdio 时用
`npx -y mcp-remote http://127.0.0.1:5056/mcp --header "Authorization: Bearer maf_..."`。）

### 裸 JSON-RPC 验证

```powershell
curl.exe -s -X POST http://127.0.0.1:5056/mcp `
  -H "Content-Type: application/json" `
  -H "X-Api-Key: maf_xxxxxxxxxxxxxxxx" `
  -d '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}'
```

### 24 个 MCP 工具（按能力分组）

- `knowledge:*` → `list_knowledge_bases` `list_documents` `search_knowledge_base` `get_document_content` `get_document_summary` `get_document_status` `upload_document`
- `workspace:*` → `workspace_list_files` `workspace_read_file` `workspace_write_file`
- `files:*` → `list_file_roots` `local_list_directory` `local_read_file` `local_search_files` `local_grep` `local_write_file`
- `avatar:*` → 6 个分身记忆工具
- `chat:*` → `list_chat_sessions` `chat_search_history`

> `tools/list` 返回**全部**工具，不按客户端能力裁剪（官方设计取舍）。受限客户端会「看得见但调不通」，
> 拒绝时返回 HTTP 200 + JSON-RPC `-32003`。

---

## 七、环境改动清单（测试机）

| 文件/设置 | 改动 | 原因 |
| --- | --- | --- |
| `%USERPROFILE%\.docker\daemon.json` | 增加 4 个 `registry-mirrors` | Docker Hub 拉取 EOF |
| `<repo>\.env` | 新建（含 token / PG 口令 / 关闭 Whisper 自动下载） | compose 需要 |
| `knowspace-mcp-smoke.ps1` | 新建 | MCP 一键冒烟 |

---

## 八、复现这份验证的推荐顺序

1. `docker build` 能不能过（PGDG 源、`VERSION_CODENAME`、pgvector 包名）
2. `docker compose up` 后 `/api/health` 是否 `{"status":"ok","database":"connected"}`
3. `pgdata` 是否落在卷里（`docker compose exec app ls /app/App_Data/pgdata/PG_VERSION` → `17`）
4. 前端是否打得开（`wwwroot` 有没有被正确 COPY）
5. 上传一个 PDF，看切分与向量化是否跑通（ONNX 模型 + pgvector 扩展）
6. 建一个 MCP 客户端密钥，`initialize` / `tools/list` / `tools/call` 走通

`scripts/Test-IntegratedStack.sh` 覆盖了 1~4 与 `/mcp` 的 401、local-config、server-card。
