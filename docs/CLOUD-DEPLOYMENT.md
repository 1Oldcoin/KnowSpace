# 云服务器部署与 MCP 接入

> 面向：把「知间」部署到云主机（Docker / Linux / Windows），本机用 AI 工具通过 MCP 读写云端知识库。
> 读完这份文档，你应该能回答三个问题：**现在有哪些防护**、**暴露到公网前必须做什么**、**本地工具怎么连上来**。

---

## 0. 一句话结论

MCP 数据面（`/mcp`）的授权模型是完整的：密钥 + 能力 + 工具白名单 + 知识库范围 + 限流 + 审计，
默认拒绝清单外的工具。**但 REST 数据面（`/api`）在云端默认是不鉴权的** ——
它原本只为「Electron 窗口访问自己拉起的后端」设计。

2026-10-28 起加了**启动守卫**：只要进程按服务端形态运行（`Hosting:Profile=Server`，
或 `Auto` 推断出"监听了非回环地址"），缺 `MAF_SERVER_TOKEN`、`RequireClientKey=false`、
CIMD 的调试开关、OAuth Issuer 非 https 等组合会让进程**拒绝启动**，而不是带着漏洞跑起来。
所以现在的最低要求从"记得配令牌"变成了"不配就起不来"。

即便如此，**令牌长度、反向代理、TLS 仍然必须自己配**。三件事的具体做法见 §3。

> 完整设计与后续阶段（账号登录 + TOTP、PAT 过期、审计保留期与分区、备份恢复、日志脱敏、
> 多副本限流、依赖扫描）见 [`MCP-REMOTE-DESIGN.md`](MCP-REMOTE-DESIGN.md)。

---

## 1. 两个数据面，两套授权

| | `/api`（REST） | `/mcp`（MCP） |
|---|---|---|
| 谁用 | 浏览器里的前端界面 | Cursor / Claude / 自研 Agent |
| 鉴权方式 | `X-Api-Token` 请求头 | `X-Api-Key` 或 `Authorization: Bearer` |
| 凭据形态 | 桌面：一次性随机令牌（每次启动重生成）<br>服务端：长期令牌 `Server:ApiToken` | 长期客户端密钥 `maf_<base64url>`（256 位随机） |
| 凭据存储 | 只在内存/环境变量，**不落盘、不进前端** | 库里只存 SHA-256 哈希 + 12 位前缀，明文只在创建时出现一次 |
| 授权粒度 | 全有或全无 | 能力 + 工具白名单 + 知识库白名单 + 每分钟额度 |
| 审计 | 无（HTTP 访问日志） | 每次调用落库：客户端、工具、知识库、结果、耗时、错误 |

为什么 MCP 不用桌面令牌：桌面令牌按设计不下发给渲染进程、不落盘、进程退出即失效，
外部 Agent 根本拿不到。所以 `/mcp` 用一套独立的长期密钥体系。

---

## 2. 现有防护清单（开箱即用，无需配置）

### 2.1 MCP 数据面

| 防护 | 实现位置 | 说明 |
|---|---|---|
| 客户端密钥认证 | `Hosting/AgentClientGate.cs` | 固定时间比较，无计时侧信道 |
| 前缀可识别 | `Domain/Agents/AgentClient.cs` | `maf_` 前缀便于在泄露扫描/日志里定位 |
| 快速短路 | 同上 `LooksLikeKey` | 形状不对直接拒，不打数据库 |
| 逐客户端启停 | `AgentClient.Enabled` | 停用后立刻 403，无需删记录 |
| 令牌桶限流 | `Application/Agents/AgentRateLimiter.cs` | 额度存库、可随时改，返回准确 `Retry-After` |
| 鉴权失败闸门 | `Application/Agents/AgentAuthThrottle.cs` | **按来源 IP** 计失败，超限封禁（默认 30 次/分 → 封 5 分钟） |
| 能力模型 | `Domain/Agents/McpToolCatalog.cs` | `knowledge:read/write`、`workspace:*`、`files:*`、`avatar:*`、`chat:read`、`*` |
| 工具白名单 | `AgentClient.AllowedTools` | 空白名单 = 本能力内全放开 |
| 知识库范围 | `AgentClient.AllowedKnowledgeBaseIds` | 空白名单 = 不限制 |
| **默认拒绝** | `McpToolCatalog` | 未登记的工具一律拒绝 —— 新增工具忘了登记是「不可用」而非「对所有人开放」 |
| 越权读资料 | `AgentClientGate` + `AgentCallGate.DecideDocumentAccess` | `get_document_content` 只带 documentId，网关回查所属知识库再判 |
| 可选参数也校验 | `AgentCallGate.Decide` | `chat_search_history` 的显式 knowledgeBaseId 同样受白名单约束（可选 ≠ 免检） |
| 请求体上限 | `Mcp:MaxRequestBodyBytes`（8 MB） | 网关要读参数才能判权，必须封顶 |
| 客户端数量上限 | `Mcp:MaxAgentClients`（50） | 防无限注册 |
| 调用审计 | `IAgentCallLogStore` | 审计写失败只丢该条，不阻断调用 |
| 可读错误码 | JSON-RPC `-32001/-32003/-32004/-32006` | 让模型看到可读原因而不是传输错误 |
| **OAuth 端点抽水防护** | `OAuthEndpoints.MapAbuseGuard` | `/oauth/token`、`/oauth/register` 与 `/mcp` 共用按 IP 的失败闸门（30 次/分钟 → 封 300 秒），封禁时返 `429` + `Retry-After` |

### 2.2 REST 数据面

| 防护 | 说明 |
|---|---|
| 桌面令牌 | Electron 主进程经 `webRequest.onBeforeSendHeaders` 注入，渲染进程拿不到 |
| 严格 CSP | 桌面模式给 HTML 响应加 `default-src 'self'` 等 |
| 控制面回环限制 | `/api/desktop/info`、`/shutdown` 只接受回环来源 |
| 安全响应头 | `X-Content-Type-Options`、`X-Frame-Options: DENY`、`Referrer-Policy` |
| 指纹隐藏 | 默认移除 `Server: Kestrel` |
| 请求体上限 | 按 `Storage:MaxFileSizeBytes` / Whisper 模型大小动态计算 |

### 2.3 顺手做过的其它加固

- 插件运行时：AppContainer / bubblewrap 隔离、清单签名校验、权限授予、调用配额、审计。
- Web 搜索：`SafeUrlGuard` 拦 SSRF（含云元数据地址段）。
- 便携 PostgreSQL：只监听回环、随机端口、`--stop-portable-postgres` 由安装器在卸载前调用。

---

## 3. 暴露到公网前必须做的四件事

### 3.1 给 `/api` 配令牌（**必做**）

两种用法，任选其一。

**A. 反向代理注入**（推荐，浏览器端零改动）

前端不需要任何修改：nginx / Caddy 完成人类认证（Basic / OAuth / mTLS），
再把 `X-Api-Token` 加进转发请求里。

```nginx
location / {
    auth_basic           "KnowSpace";
    auth_basic_user_file /etc/nginx/.htpasswd;

    proxy_pass         http://127.0.0.1:5056;
    proxy_set_header   Host              $host;
    proxy_set_header   X-Real-IP         $remote_addr;
    proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;

    # 关键：认证通过后由代理由注入后端令牌，浏览器拿不到它
    proxy_set_header   X-Api-Token       "<用 openssl rand -hex 32 生成>";
}
```

**B. 客户端自带令牌**

```bash
export MAF_SERVER_TOKEN="$(openssl rand -hex 32)"
```

之后所有 `/api` 请求都要带 `X-Api-Token: $MAF_SERVER_TOKEN`。
适合脚本、CI、自研前端；原生 Web 界面需要自己补一个「输入令牌」的门。

> 令牌可用 `openssl rand -hex 32` 生成；配置校验要求非空时至少 16 字符。

### 3.2 放行健康检查 + 开启转发头

```yaml
environment:
  Server__AllowAnonymousHealth: "true"   # 让 LB / HEALTHCHECK / uptime 探活不用带令牌
  Server__TrustForwardedHeaders: "true"  # 放在反向代理后面时必开
```

**不开启转发头是一个真实漏洞**：`RemoteIpAddress` 永远是代理地址，于是

- MCP 审计里的来源 IP 全是 `127.0.0.1`，出事无法溯源；
- `/api/desktop/*` 用回环判定「是不是本机」，经代理进来的
  `POST /api/desktop/shutdown` 会被当成本机请求 —— **公网任何人都能关掉你的服务**。

代理与 API 不同机时，还要显式声明代理地址：

```yaml
  Server__KnownProxies__0: "10.0.0.5"
```

> `AllowAnonymousHealth=true` 只放行 `/api/health`（返回 `{"status":"ok","database":"connected"}`），
> `/api/healthz`、`/api/settings` 等仍然需要令牌。

### 3.3 上 TLS

客户端密钥是 Bearer 型凭据，**明文 HTTP 上等于公开**。用 Caddy 自动证书最省事：

```
knowspace.example.com {
    reverse_proxy 127.0.0.1:5056
}
```

不要直接把 5056 映射到公网：

```yaml
ports:
  - "127.0.0.1:5056:5056"   # 只绑回环，由反代对外
```

### 3.4 换掉默认口令

`appsettings.json` 里的 `PortablePostgres:Password` 是 `postgres`，
`docker-compose.yml` 里的 `POSTGRES_PASSWORD` 也是 `postgres`。
云端务必改成强口令，并只在容器网络内可达（compose 默认已不映射 5432）。

### 3.5 启用 OAuth（可选，面向普通用户）

想让用户“浏览器点一下同意”而不是复制粘贴密钥时：

```jsonc
"Mcp": {
  "OAuth": {
    "Enabled": true,
    "Issuer": "https://knowspace.example.com"   // 必须与公网地址完全一致
  }
},
"Server": {
  // 只影响「一键复制」生成的地址（不参与鉴权）。开了 OAuth 时可以省略（Issuer 优先）。
  "PublicBaseUrl": "https://knowspace.example.com"
}
```

五个硬要求：

1. **同源**：`/mcp` 与 `/oauth/*`、`/.well-known/*` 必须在同一域名下（Cursor 在跨源时无法完成 OAuth）。本实现天然同源。
2. **`Issuer` 不能猜**：它写进令牌的 `iss`/`aud`。反代后面要么直接配公网地址，要么开 `Server:TrustForwardedHeaders` 并确保 `X-Forwarded-Proto/Host` 可信。
3. **反代不要给 `/oauth/*`、`/.well-known/*` 加 Basic 认证**：客户端在这之前没有凭据。同时要透传 `Authorization`、`Accept: application/json, text/event-stream`、`Mcp-Method`、`MCP-Protocol-Version` 四个头。
4. **`maf_` 密钥通道继续保留**：Cursor 的刷新令牌有已知问题，OAuth 失效时切回密钥配置。
5. **确认 `/oauth/token` 与 `/oauth/register` 的 IP 闸门看到的是真实客户端 IP**。这两个端点默认 30 次失败/分钟 → 封 300 秒（`Server:FailedAuthLimitPerMinute` / `FailedAuthBlockSeconds`）。在反代后面如果 `RemoteIpAddress` 永远是代理地址，一个人爆破会把所有用户一起封——所以 `Server:TrustForwardedHeaders=true` 与 `Server:KnownProxies` 是**必配项**，不是可选项。

Caddy 示例（不需要额外规则，但要确保不拦截 `/.well-known`）：

```
knowspace.example.com {
    reverse_proxy 127.0.0.1:5056
}
```

部署后自检：

```powershell
# 元数据是否可匿名访问
curl.exe -s https://knowspace.example.com/.well-known/oauth-authorization-server
# 全流程（DCR → 授权 → 换令牌 → 调 /mcp → 撤销）
powershell -NoProfile -File scripts\Test-McpOAuth.ps1 -BaseUrl https://knowspace.example.com -VerifyRevocation
```

细节与风险清单见 [`MCP-OAUTH.md`](MCP-OAUTH.md)。

---

## 4. 三种部署形态

### 4.1 Docker Compose（Linux 云主机，推荐）

默认是**一体形态**：一个容器里跑 API + 前端 + PostgreSQL 17 + pgvector。

```bash
git clone <repo> && cd MyFirstMafAgent
cat > .env <<'EOF'
LLM_API_KEY=sk-xxxx
MAF_SERVER_TOKEN=<openssl rand -hex 32 的输出>
PG_PASSWORD=<强口令>
PUBLIC_BASE_URL=https://knowspace.example.com
EOF
docker compose up -d --build
docker compose exec app bash /app/scripts/Test-IntegratedStack.sh --pg-password "$PG_PASSWORD"
```

`docker-compose.yml` 里两个已经接好的环境变量：

| 变量 | 作用 |
| --- | --- |
| `Server__PublicBaseUrl` | 只影响「一键复制」生成的 MCP 地址（不参与鉴权）；不填会生成 `http://127.0.0.1:5056/mcp`，别人本机连不上 |
| `Server__TrustForwardedHeaders` | 反代后面**必开**，否则审计 IP 全变 `127.0.0.1`，且按 IP 封禁会把代理当成客户端（一个人爆破全体连坐） |

反代与 API **不同机**时还要加 `Server__KnownProxies__0=<代理真实 IP>`（多个用 `__1` 递增）。

要改成「API + 独立数据库」（适合独立备份/扩容/多副本）：

```bash
docker compose --profile external up -d --build
```

它用 `pgvector/pgvector:pg17` 另起一个 `db` 容器，并把
`PortablePostgres__Enabled=false` + `ConnectionStrings__KnowledgeBase` 注入给 `api`。
两个形态的数据卷不同（`maf-app-data` vs `maf-api-data`+`maf-pgdata`），**不能直接互换**。

`ffmpeg` 由 apt 安装，不随包分发 100 MB 副本。

> 一体部署的代价：数据库随 API 进程生命周期（`StopOnExit=true`），不能跑多副本，
> 升级 PostgreSQL = 重建镜像。要这些能力就切 `external` profile，切换只改两个环境变量。

### 4.2 Linux 裸机 / systemd

最省事的方法：发布 `-Target console -Runtime linux-x64`，拷到服务器后一条命令：

```bash
sudo bash deploy/linux/install.sh --source /tmp/console-linux-x64
```

它做五件事：装 PostgreSQL 17 + pgvector（PGDG 源）、建系统账号 `myfirstmafagent`、
铺程序到 `/opt/myfirstmafagent` 与数据到 `/var/lib/myfirstmafagent`、
写 `/etc/myfirstmafagent/myfirstmafagent.env`（带随机数据库口令）、装并启动 systemd 单元。

手写单元时的关键行（完整模板见 `deploy/linux/myfirstmafagent.service`）：

```ini
[Service]
Type=simple
User=myfirstmafagent
WorkingDirectory=/opt/myfirstmafagent
EnvironmentFile=/etc/myfirstmafagent/myfirstmafagent.env
ExecStart=/opt/myfirstmafagent/Zhijian.Api --urls http://127.0.0.1:5056 --no-browser
Restart=always
RestartSec=5
TimeoutStopSec=120
ReadWritePaths=/var/lib/myfirstmafagent
```

env 里最少这几项：

```ini
MAF_SERVER_TOKEN=<令牌>
Server__AllowAnonymousHealth=true
Server__TrustForwardedHeaders=true
Server__PublicBaseUrl=https://knowspace.example.com
# 内置库：用 apt 装的 PG 17，不随包分发便携树（便携树只有 win-x64）
PortablePostgres__Enabled=true
PortablePostgres__BinDirectory=/usr/lib/postgresql/17/bin
PortablePostgres__RootDirectory=/var/lib/myfirstmafagent
PortablePostgres__Password=<强口令>
# 签名私钥会落在它的上层目录（/var/lib/myfirstmafagent/oauth/），所以不能用程序目录
Storage__RootPath=/var/lib/myfirstmafagent/uploads
```

> 必须加 `--urls`：`appsettings.json` 里的 `Urls` 是 `127.0.0.1`，
> 绑不到对外地址。`ASPNETCORE_URLS` 会被配置文件同名项压掉，只有命令行参数生效。

想改用本机已有的 PostgreSQL：把 `PortablePostgres__Enabled` 改 `false`、加上
`ConnectionStrings__KnowledgeBase`，`systemctl restart` 即可，不用重跑安装脚本；
但 **pgvector 扩展要自己装**（`postgresql-17-pgvector`）。

### 4.3 Windows Server

```powershell
# 作为服务安装（需 NSSM 或 sc.exe）
$env:MAF_SERVER_TOKEN = "<令牌>"
$env:Server__AllowAnonymousHealth = "true"
$env:Server__TrustForwardedHeaders = "true"
.\Zhijian.Api.exe --urls http://127.0.0.1:5056 --no-browser
```

便携 PostgreSQL 在 Windows 上开箱可用（`ExternalTools\pgSQL\win-x64`）；
纯 ASCII 安装路径是硬要求（`initdb` 会按 ANSI 代码页拼路径，含中文直接失败）。

---

## 5. 本地工具通过 MCP 连云端

### 5.1 方案一：在客户端里配 MCP 服务器（推荐）

先在云端「设置 → MCP 客户端」创建一个密钥：选能力（只读就选 `knowledge:read`）、
限定知识库、设每分钟额度。**明文只显示一次**，立刻存进客户端配置。

**Cursor**（`~/.cursor/mcp.json`）：

```json
{
  "mcpServers": {
    "knowspace": {
      "url": "https://knowspace.example.com/mcp",
      "headers": {
        "X-Api-Key": "maf_xxxxxxxxxxxxxxxxxxxxxxxx"
      }
    }
  }
}
```

**Claude Desktop**（`claude_desktop_config.json`）—— 需要把 HTTP 端点包一层 stdio 桥：

```json
{
  "mcpServers": {
    "knowspace": {
      "command": "npx",
      "args": [
        "-y", "mcp-remote",
        "https://knowspace.example.com/mcp",
        "--header", "X-Api-Key:maf_xxxxxxxxxxxxxxxxxxxxxxxx"
      ]
    }
  }
}
```

**自研 Agent（.NET / Python）** 直接发 JSON-RPC：

```bash
curl -X POST https://knowspace.example.com/mcp \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: maf_xxxxxxxxxxxxxxxxxxxxxxxx" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

### 5.2 方案二：反过来，本地跑 MCP 连本地库

本机装了桌面版时，本地 API 自己就是 MCP 服务端：
`http://127.0.0.1:<端口>/mcp`（端口随机，见 `%LOCALAPPDATA%\MyFirstMafAgentData\runtime`）。
适合「资料在本地、不想上云」的场景，授权模型完全一样。

### 5.3 两种方案的取舍

| | 方案一：客户端配 MCP | 方案二：装插件/本地桥 |
|---|---|---|
| 改造成本 | 改一个 JSON 配置 | 要为每个工具写适配层 |
| 凭据管理 | 每个客户端一把密钥，可独立吊销 | 密钥散落在各插件里，难统一吊销 |
| 权限收窄 | 每把密钥可单独限定能力/知识库/额度 | 通常一把通吃 |
| 审计 | 全量落库，可按客户端查 | 只能看到桥接层的日志 |
| 适用 | **推荐**，Cursor/Claude/自研 Agent 都支持 | 老工具不支持 HTTP MCP 时才需要 |

**结论：优先方案一。** 你不需要「一开始就生成一堆密钥」——
需要时在界面里点一下创建，选好能力范围，明文拷进客户端即可。
密钥可以随时停用或删除，删掉即刻失效。

---

## 6. 密钥全生命周期

| 动作 | 位置 | 效果 |
|---|---|---|
| 创建 | 设置 → MCP 客户端 → 新建 | 明文**只出现一次**；库里存 SHA-256 + 前缀 |
| 限定能力 | 同上，勾选 | 决定能调哪些工具类别 |
| 限定工具 | 同上 | 进一步收窄到具体工具名 |
| 限定知识库 | 同上 | 该密钥只能看到/操作指定知识库 |
| 限流 | 同上，每分钟次数 | 令牌桶，超限 429 + `Retry-After` |
| 停用 | 同上，取消「启用」 | 立刻 403，保留记录与审计 |
| 轮换 | 新建一把 → 客户端改配置 → 停用旧的 | 有重叠窗口，不中断服务 |
| 吊销 | 删除 | 立刻失效 |

**泄露处置**：`maf_` 前缀让它在代码仓库/日志扫描里能被直接定位。发现泄露就删掉该客户端 ——
哈希设计意味着库里没有可恢复的明文，不需要改密码，也无法被反推。

---

## 7. 排障

| 现象 | 原因 | 处理 |
|---|---|---|
| `401 A valid local API token is required` | `/api` 需要令牌 | 带 `X-Api-Token`，或让反代注入 |
| `401 缺少或无效的客户端密钥` | `/mcp` 密钥不对 | 检查 `X-Api-Key` / `Bearer` |
| `429 鉴权失败次数过多` | 触发失败闸门 | 等 `Retry-After` 秒；确认密钥；必要时重启清空内存计数 |
| `429 请求过于频繁` | 超出该客户端的每分钟额度 | 调高额度或放慢调用 |
| `403 客户端已停用` | 被停用 | 重新启用 |
| `403 未被授权访问知识库` | 不在该密钥的知识库白名单里 | 补白名单 |
| `-32601 未知工具` | 工具名不在 `McpToolCatalog` | 查 `tools/list` |
| 审计里 IP 全是 `127.0.0.1` | 没开转发头 | 设 `Server__TrustForwardedHeaders=true` |
| `/api/desktop/shutdown` 被外部调通 | 同上（回环判定被代理骗过） | 同上，**优先修** |

---

## 8. 还没做的（如实说明）

| 项 | 现状 | 影响 |
|---|---|---|
| 密钥有效期 | 无 `ExpiresAt` 字段 | 只能手动轮换；建议每季度换一次 |
| 用户体系 | 无多用户/登录 | 单租户；多人共用需靠反代做身份区分 |
| HTTPS | 应用本身不终止 TLS | 必须由反代/负载均衡提供 |
| 审计保留 | 跟随数据库，无自动归档 | 长期运行需自行定期清理 |
| 端口扫描防护 | 无 fail2ban / WAF 集成 | 建议在反代或云防火墙层加限速与封禁 |
