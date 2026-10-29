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

即便如此，**令牌长度**与**传输加密**仍然必须自己配。
反向代理只是拿到这两样东西的一种方式，不是唯一方式——没有域名、不需要网页界面时，
SSH 隧道或直接开端口同样可行，见 §3.7；TLS 的三条实现路径见 §3.3。

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

## 3. 暴露到公网前必须做的事项

先明确一件事：**反向代理不是必须的**。它只解决一个问题——让浏览器里的界面能带上 `X-Api-Token`。
如果你不需要网页界面，另外几条路同样成立，见 §3.7。

### 3.1 给 `/api` 配令牌（**必做**）

服务端形态下缺令牌会**拒绝启动**（`SecurityPostureGuard`），所以这一项不是「可选加固」。

**A. 反向代理注入**（需要网页界面时用这个）

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

    # 关键：认证通过后由代理注入后端令牌，浏览器拿不到它
    proxy_set_header   X-Api-Token       "<用 openssl rand -hex 32 生成>";
}
```

**B. 客户端自带令牌**（脚本 / CI / 只用 MCP 的场景）

```bash
export MAF_SERVER_TOKEN="$(openssl rand -hex 32)"
```

之后所有 `/api` 请求都要带 `X-Api-Token: $MAF_SERVER_TOKEN`。

> **注意**：当前前端**没有**任何注入令牌的代码（`services/api.ts` 里全部是裸 `fetch`），
> 也没有登录页或令牌输入框。所以选 B 意味着**网页界面不可用**（`/api` 全部 401），
> 只能用 curl 管理。要恢复界面，要么用 A，要么等前端补上令牌输入（见 §3.7 结尾）。

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

客户端密钥是 Bearer 型凭据，**明文 HTTP 上等于公开**。

**应用代码里没有 TLS**：`Hosting/` 下没有 `UseHttpsRedirection` / `UseHsts`，
也没有 Kestrel 证书配置，`appsettings.json` 里只有 `"Urls": "http://127.0.0.1:5056"`。
但这不等于「只能靠反代」——三条路都行，按场景选：

| 做法 | 适用 | 代价 |
| --- | --- | --- |
| 反向代理终结 TLS（本节） | 有域名、要给浏览器界面用 | 多一个进程 |
| Kestrel 自己配证书 | 只想让 `/mcp` 走 HTTPS | 无域名只能自签；Node 客户端要设 `NODE_EXTRA_CA_CERTS` |
| 根本不暴露端口（SSH 隧道 / Tailscale / Cloudflare Tunnel） | 个人使用 | 每个客户端要配隧道 |

> 顺带一提：**Let's Encrypt 不给 IP 签发证书**。没有域名时，反代也只能用自签证书，
> 所以「上反代才有 TLS」这个理由在无域名场景下并不成立——真要可信证书，
> 得用 `sslip.io` / `nip.io` 这类把 IP 编码进域名的 DNS，或 Cloudflare Tunnel。

Kestrel 直出 HTTPS 时，用配置即可，不用改代码（注意 `Kestrel:Endpoints` 一旦配置会覆盖 `Urls`，
此时不要再传 `--urls`）：

```yaml
Kestrel__Endpoints__Https__Url: "https://0.0.0.0:5057"
Kestrel__Endpoints__Https__Certificate__Path: "/data/cert.pfx"
Kestrel__Endpoints__Https__Certificate__Password: "<口令>"
```

用 Caddy 自动证书最省事（默认透传 `Host` / `X-Forwarded-*`，且不对 SSE 做缓冲）：

```
knowspace.example.com {
    reverse_proxy 127.0.0.1:5056
}
```

nginx 需要显式写出下面这些（`/mcp` 的 `proxy_buffering off` 与超时**不能省**，理由见 §3.6）：

```nginx
server {
    listen 443 ssl http2;
    server_name knowspace.example.com;

    ssl_certificate     /etc/letsencrypt/live/knowspace.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/knowspace.example.com/privkey.pem;

    # 视频上限 5 GB（Storage:MaxVideoFileSizeBytes），regular 请求 50 MB
    client_max_body_size 6g;

    # 通用转发头：每一段都必须有，缺一个都会让审计/限流看到错误的 IP 或协议
    proxy_http_version 1.1;
    proxy_set_header Connection          "";
    proxy_set_header Host                $host;
    proxy_set_header X-Real-IP           $remote_addr;
    proxy_set_header X-Forwarded-For     $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto   $scheme;

    # ① 浏览器界面：外面必须有人类认证，再由代理注入后端令牌
    location / {
        auth_basic           "KnowSpace";
        auth_basic_user_file /etc/nginx/.htpasswd;

        proxy_pass http://127.0.0.1:5056;
        proxy_set_header X-Api-Token "<MAF_SERVER_TOKEN 的值>";

        proxy_buffering    off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # ② MCP 数据面：不要加 auth_basic（客户端带的是 Authorization: Bearer）
    location /mcp {
        proxy_pass http://127.0.0.1:5056;

        # 必须透传，否则 OAuth 与工具调用都会失败
        proxy_set_header Authorization     $http_authorization;
        proxy_set_header Accept            $http_accept;

        # 关键：EventStream 不能被缓存/缓冲，否则响应被攒住不出流
        proxy_buffering    off;
        proxy_cache        off;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }

    # ③ OAuth 元数据：客户端在此之前没有任何凭据，必须匿名可达
    location ~ ^/(oauth|\.well-known)/ {
        proxy_pass http://127.0.0.1:5056;
    }
}
```

> `/` 那段注入了 `X-Api-Token`，所以**必须**配人类认证（`auth_basic` / OAuth 门户 / mTLS），
> 否则等于把管理面令牌送给任何扫到端口的人。反过来，`/mcp` 与 `/oauth` 不能配 `auth_basic`：
> 它们自带 Bearer 认证，Basic 弹窗会和 `Authorization` 头互相覆盖。

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
  // 同时影响两件事：①「一键复制」生成的 MCP 地址；② /mcp 的 Host/Origin 允许列表（见 §3.6）。
  // 开了 OAuth 时 Issuer 已经提供同一来源，可以省略。
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

### 3.6 反向代理下 `/mcp` 的两个硬性约束

这一节是踩过的坑，两个都不报错、只「不工作」，排查起来很费时间。

#### 3.6.1 `Host` 不在白名单 → 403，连不上

`Hosting/McpTransportGuard.cs` 把请求的 `Host` 与 `Origin` 跟一张白名单比对，不在表里直接 403
（MCP 规范要求防 DNS rebinding）。白名单由这几处拼成：

```csharp
"localhost", "127.0.0.1", "::1", "[::1]"   // 永远在
Mcp:AllowedHosts                            // 显式配置
Mcp:OAuth:Issuer 的主机 + origin            // 开了 OAuth 时
Server:PublicBaseUrl 的主机 + origin        // ← 容易忽略
```

因此：

- **直接用 `http://<公网IP>:5056/mcp`** 一定失败，返回
  `Host '1.2.3.4' 不在允许列表内（Mcp:AllowedHosts）`。这不是 bug，是设计如此。
- **反代改了 `Host` 头也一样失败**。nginx 默认 `proxy_set_header Host $host`，
  转发过去是 `knowspace.example.com`，所以 `Server__PublicBaseUrl`
  （或 `Mcp__AllowedHosts__0`）必须配成同一个值。
- `Origin` 只有**出现时**才校验。浏览器和部分客户端会带，命令行 `curl` 不带，
  所以「curl 能通、客户端不能通」这种情况通常就是 `Origin` 被拒。

报错信息会直接说明该配哪个键，照抄即可。

#### 3.6.2 `text/event-stream` 被缓冲 → `tools/list` 通、`tools/call` 卡住

`/mcp` 的响应是 Server-Sent Events 流。nginx 默认开启 `proxy_buffering`，会把流攒在缓冲区里，
典型症状是**握手成功、`tools/list` 有返回，但 `tools/call` 一直挂着直到超时**。
`proxy_read_timeout` 默认 60 秒，长一点的检索或上传也会被切断。

必须显式关掉：

```nginx
proxy_buffering    off;
proxy_cache        off;
proxy_read_timeout 3600s;
proxy_send_timeout 3600s;
```

Caddy 不需要配这些（默认不缓冲流式响应）。

#### 3.6.3 还要确认的几项

| 项 | 要求 | 不满足的后果 |
| --- | --- | --- |
| `proxy_http_version 1.1` + `Connection ""` | 必须 | SSE 走不了分块传输，退化成缓冲 |
| `Authorization` 透传 | 默认透传，但配了 `auth_basic` 会覆盖 | 客户端拿到 401 |
| `client_max_body_size` | ≥ 视频上限（默认 5 GB） | `413 Request Entity Too Large`（nginx 先于应用拒绝） |
| `/mcp` 请求体上限 | 应用侧 `Mcp:MaxRequestBodyBytes` 只有 8 MB | 大文件请走 `/api/uploads`，不要塞进 MCP 工具参数 |
| `X-Forwarded-Proto` | 必须 | OAuth `Issuer` 拼出 `http://`，令牌 `aud` 对不上 |
| `/oauth/*`、`/.well-known/*` | 不能加 `auth_basic` | 客户端拿不到元数据，OAuth 流程起不来 |

#### 3.6.4 上线自检

```bash
BASE=https://knowspace.example.com

# 1. 健康检查应匿名可访问
curl -s "$BASE/api/health"

# 2. /api 不带令牌应为 401（不是 200）—— 返回 200 说明反代没注入令牌、应用也没开鉴权
curl -s -o /dev/null -w '%{http_code}\n' "$BASE/api/settings"

# 3. /mcp 用密钥能列出工具（同时验证 Host 校验与 SSE 不缓冲）
curl -s "$BASE/mcp" \
  -H "Authorization: Bearer maf_你的密钥" \
  -H 'Accept: application/json, text/event-stream' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# 4. 调一次真实工具，确认不会卡住
curl -s "$BASE/mcp" \
  -H "Authorization: Bearer maf_你的密钥" \
  -H 'Accept: application/json, text/event-stream' \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_knowledge_bases","arguments":{}}}'
```

跑完第 3、4 条后，回到「设置 → 管理接入客户端」，该客户端应出现 `lastCallAt`
与来源 IP。**如果 IP 显示 `127.0.0.1`**，说明 `Server:TrustForwardedHeaders`
或 `Server:KnownProxies` 没配对（见 §3.2）。

### 3.7 不用反向代理的部署方式

没有域名、不要 WAF、只有自己用的时候，反代是多余一层。下面三条同样成立。

#### 3.7.1 SSH 隧道 + Desktop 形态（个人使用最省事）

服务器上不配任何东西，本机执行：

```bash
ssh -N -L 5056:127.0.0.1:5056 user@server
```

本机浏览器打开 `http://127.0.0.1:5056` 即可。为什么不用改配置：

- 请求的 `Host` 是 `127.0.0.1`，**本来就在** `McpTransportGuard` 的固定白名单里，
  不需要配 `Mcp:AllowedHosts`；
- 链路已被 SSH 加密，不需要证书；
- 容器里把 `Hosting__Profile` 设成 `Desktop`（配合 `ports: "127.0.0.1:5056:5056"` 绑回环），
  就跳过「缺令牌拒绝启动」，`/api` 不设令牌也能用界面。

> ⚠️ `Desktop` 形态**会**注册 `local_*` 文件工具（`McpModule.cs` 只在 `profile.IsServer` 时跳过）。
> 服务器上的「本机目录」是服务器的文件系统，所以**不要**给客户端 `files:*` / `files:write` 能力。

#### 3.7.2 Server 形态 + 直接开端口（只跑 MCP）

```yaml
environment:
  MAF_SERVER_TOKEN: "<openssl rand -hex 32>"
  Hosting__Profile: "Server"
  Mcp__AllowedHosts__0: "1.2.3.4"     # 公网 IP，否则 /mcp 因 Host 校验 403
ports:
  - "0.0.0.0:5056:5056"
```

客户端配 `http://1.2.3.4:5056/mcp` + `Authorization: Bearer maf_...` 即可。
管理端点用 curl：

```bash
curl -H "X-Api-Token: $MAF_SERVER_TOKEN" -X POST http://1.2.3.4:5056/api/agent-clients \
  -H 'Content-Type: application/json' \
  -d '{"name":"Cursor","capabilities":["knowledge:read"]}'
```

代价两条：**网页界面不可用**；`maf_` 密钥走明文 HTTP，同网段可被嗅探。
只建议在内网 / VPN 内这么干。

#### 3.7.3 Server 形态 + Kestrel 自签证书

配置见 §3.3 的 `Kestrel__Endpoints__Https__*`。可行，但两点要注意：

- Cursor / Claude 等 Node 客户端默认**拒绝**自签证书，需要给客户端进程设
  `NODE_EXTRA_CA_CERTS=/path/to/ca.crt`；
- 这只解决 `/mcp` 的加密，**网页界面仍然 401**（令牌问题与 TLS 是两件事）。

#### 3.7.4 三者对比

| 场景 | 推荐 | 网页界面 | MCP |
| --- | --- | --- | --- |
| 只有自己用，能开 SSH | §3.7.1 隧道 | ✅ | ✅ |
| 只给 AI 工具连，命令行管理 | §3.7.2 直开端口 | ❌ | ✅（明文传输） |
| 有域名，要公网访问界面 | §3.3 反代 | ✅ | ✅ |
| 无域名但想让界面走公网 | 需要先补前端令牌输入 | ❌ | ✅ |

最后一行是当前的**产品缺口**，不是配置问题：`services/api.ts` 里全是裸 `fetch`，
令牌只能靠 Electron 主进程或反代注入。补法是加一个「服务地址 + 令牌」入口，
把所有请求收口到一个带 `X-Api-Token` 的 `fetch` 包装里。

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
| `Server__PublicBaseUrl` | 影响两件事：①「一键复制」生成的 MCP 地址；② `/mcp` 的 `Host`/`Origin` 允许列表（`McpTransportGuard` 会把它加进去，见 §3.6）。不填会生成 `http://127.0.0.1:5056/mcp`，且反代后 `/mcp` 会因 `Host` 不在白名单返回 403 |
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
