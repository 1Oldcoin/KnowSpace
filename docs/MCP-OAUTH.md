# MCP 的 OAuth 2.1 接入（实现与验收）

> 相关：`docs/MCP-ACCESS.md`（接入总览）、`docs/CLOUD-DEPLOYMENT.md`（部署与安全清单）、`ARCHITECTURE.md` §9.10（授权模型）。

本文覆盖一条主线的实现与验收：**让 Cursor / Claude Desktop 通过 OAuth 连上知间的 `/mcp`**，同时保证既有的 `maf_` 密钥通道、每次调用的业务闸门、审计与本地配置入口全都不退化。

---

## 0. 四条不可让步的原则

| # | 原则 | 在代码里的落点 |
| --- | --- | --- |
| 1 | **OAuth 只解决「谁被授权」，不替代「每次调用是否允许」** | `scope` 只决定能力上限；知识库范围、工具白名单、限流、审计仍由 `Hosting/AgentClientGate.cs` 逐次判定 |
| 2 | **CIMD 优先，DCR 兜底，预注册保留** | `OAuth/OAuthService.EnsureClientAsync()` 先查库（预注册/缓存的 CIMD），再拉 CIMD；DCR 是独立端点 `POST /oauth/register` |
| 3 | **协议版本兼容是 OAuth 的前置条件** | SDK 2.2.0 已实现 `server/discover` 与双世代（Legacy `initialize` + Modern `_meta`）；网关对 `server/discover` 放行预认证，并在 `/api/mcp/diagnostics` 记录协商结果 |
| 4 | **本地「确定可用入口」永远保留** | `GET /api/mcp/local-config` + 设置页「复制 Cursor / Claude Desktop 配置」，不依赖 OAuth、Server Card 或 mDNS |

---

## 1. 三层授权，界线必须清楚

```
┌──────────────────────────── 协议层（OAuth） ────────────────────────────┐
│ 谁被授权：client_id + PKCE + scope + aud（RFC 8707）                     │
│ 代码：OAuth/（授权服务器、令牌、CIMD）                                    │
└────────────────────────────────┬────────────────────────────────────────┘
                                 │ 校验通过 → 翻译成 OAuthPrincipal
┌────────────────────────────────▼────────────────────────────────────────┐
│ 业务层：oauth_consents（subject + client_id → scope + 知识库列表）        │
│ 撤销立即生效：令牌仍有效，但下一次请求就 401                              │
└────────────────────────────────┬────────────────────────────────────────┘
                                 │
┌────────────────────────────────▼────────────────────────────────────────┐
│ 调用层：AgentClientGate（能力 → 工具白名单 → 知识库范围 → 限流 → 审计）   │
│ 与 maf_ 密钥通道完全共用同一份判定代码                                    │
└─────────────────────────────────────────────────────────────────────────┘
```

**为什么不能合并**：把白名单塞进 `scope` 会逼着每次改权限都重新走一遍浏览器授权；而只靠 OAuth 不做调用层校验，一旦某条路径忘了登记能力就是静默越权。两层各自有明确职责，组合起来才安全。

---

## 2. 协议版本兼容性（先做，先验收）

### 2.1 现状

`ModelContextProtocol.AspNetCore` 2.2.0 同时支持两代协议：

| 世代 | 客户端第一个请求 | 版本标识 | 服务端行为 |
| --- | --- | --- | --- |
| Legacy（2024-11-05 … 2025-11-25） | `initialize` | `params.protocolVersion` | 握手后回 `InitializeResult` |
| Modern（2026-07-28） | `server/discover` | `params._meta["io.modelcontextprotocol/protocolVersion"]` + `Mcp-Protocol-Version` / `Mcp-Method` 头 | 回 `DiscoverResult`（`supportedVersions`、`capabilities`） |

`Mcp-Session-Id` 在 Modern 世代不再使用；`ping`、`logging/setLevel`、`notifications/roots/list_changed` 被移除，`resources/subscribe` 换成 `subscriptions/listen`。本项目的工具实现没有依赖这些方法，因此不需要 Dual-era 改造，只补了两件事：

1. **网关放行 `server/discover`（不要求凭据）**：它只返回静态能力，不碰业务数据；真正的数据面仍然要认证。
2. **诊断采样**：每个 `/mcp` 请求都记录 `firstMethod / protocolVersion / era / authenticated / status`。

### 2.2 验收方法

```powershell
# 一、Modern：server/discover（不需要凭据，但必须带每请求元数据）
curl.exe -s -X POST http://127.0.0.1:5056/mcp `
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" `
  -H "MCP-Protocol-Version: 2026-07-28" -H "Mcp-Method: server/discover" `
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"server/discover\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}}}"
# 期望：200，result.supportedVersions 含 "2026-07-28"

# 二、Legacy：initialize（用 maf_ 密钥）
curl.exe -s -X POST http://127.0.0.1:5056/mcp `
  -H "Authorization: Bearer maf_…" -H "Content-Type: application/json" `
  -H "Accept: application/json, text/event-stream" `
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"smoke\",\"version\":\"1.0\"}}}"
# 期望：200，result.protocolVersion = "2025-11-25"

# 三、看客户端实际协商到了什么
curl.exe -s http://127.0.0.1:5056/api/mcp/diagnostics
```

`/api/mcp/diagnostics` 返回：

```jsonc
{
  "protocol": {
    "supportedProtocolVersions": ["2024-11-05", "2025-03-26", "2025-06-18", "2025-11-25", "2026-07-28"],
    "modernProtocolVersion": "2026-07-28",
    "methodCounts": { "initialize": 3, "server/discover": 2, "tools/list": 12 },
    "recent": [ { "occurredAt": "...", "firstMethod": "server/discover", "protocolVersion": "2026-07-28", "era": "modern", "authenticated": true, "statusCode": 200 } ]
  },
  "channels": { "clientKey": { "enabled": true }, "oauth": { "enabled": true, "issuer": "...", "resource": "..." } },
  "verificationChecklist": [ "…照做就能留下 Legacy / Modern 各一条成功记录…" ]
}
```

> 协议版本清单是**显式常量**（`Hosting/McpProtocolDiagnostics.cs`），因为 SDK 的 `McpProtocolVersions` 是 `internal`。升级 SDK 时同步这张表，诊断页才不会撒谎。

---

## 3. 客户端注册：预注册 → CIMD → DCR → 手动

| 顺序 | 机制 | 何时用 | 代码 |
| --- | --- | --- | --- |
| 1 | 预注册（静态 `client_id`） | 企业内部客户端；不想依赖出站请求 | 直接写 `oauth_clients`（`source=preregistered`） |
| 2 | **CIMD** | 默认路径。`client_id` 就是一个 HTTPS URL，服务端拉它的元数据 | `OAuth/CimdClientMetadataResolver.cs` |
| 3 | DCR | 兜底。不支持 CIMD 的客户端 | `POST /oauth/register` |
| 4 | 手动输入 | 最后兜底 | 设置页填 `maf_` 密钥（走密钥通道，不走 OAuth） |

授权服务器元数据里声明 `client_id_metadata_document_supported: true`，客户端据此选择 CIMD。

### 3.1 CIMD 的 SSRF 防线（逐条对应实现）

| 限制 | 为什么 | 实现 |
| --- | --- | --- |
| 只允许绝对 HTTPS | 明文文档可被中间人替换成攻击者的回调地址 | `IsAllowedClientIdUrl(..., allowInsecureHttp)`, 默认 `false` |
| 拒绝用户信息 / fragment | `https://user@host` 容易被误读为可信域名 | 同上 |
| 拒绝重定向 | 302 到内网是最经典的 SSRF 手法 | `SocketsHttpHandler.AllowAutoRedirect = false` |
| 拒绝私有/回环/链路本地/组播/保留段 | 云元数据 `169.254.169.254`、内网管理面 | `IsAllowedAddress()`（IPv4 + IPv6） |
| **连接已校验的 IP** | 杜绝 DNS 重绑定（解析与连接之间换答案） | `ConnectCallback` 里解析 → 逐个校验 → 直连 IP |
| 响应体积上限 + 超时 | 压缩炸弹 / 慢速攻击 | `MaxResponseBytes`（默认 32 KiB）、`TimeoutSeconds`（默认 5s） |
| 只接受 JSON | 避免把 HTML 当元数据解析 | `Content-Type` 校验 |
| **`client_id` 必须与 URL 逐字符一致** | 否则任何人都能托管一份声明别人 `client_id` 的文档 | `ParseMetadata()` |
| 只允许公开客户端 | CIMD 没有安全通道传 `client_secret` | `token_endpoint_auth_method` 必须是 `none` |

联调时可以用 `Mcp:OAuth:Cimd:AllowInsecureHttp=true` + `AllowPrivateAddresses=true` 指向本机的小 JSON 文件；**这两项在生产上必须是 false**。

---

## 4. 受众绑定（RFC 8707 `aud`）：不能省

- 授权请求**必须**带 `resource`，且必须等于本资源标识；缺失或不等 → `invalid_target`。
- 令牌请求（`authorization_code` 与 `refresh_token`）同样**必须**带 `resource`。
- 访问令牌的 `aud` 由授权服务器在签发时写入；`/mcp` 校验时**必须**等于本资源标识。

结果是：签给 `https://a.example.com/mcp` 的令牌拿到 `https://b.example.com/mcp` 上会被 401，无法「误兑换 / 重放」。

> 资源标识与签发者都从配置推导，**不从请求头推导**。反代没配好时 `Host` 是可控输入，用它拼出来的 `aud` 等于没有受众绑定。

---

## 5. 用户身份与业务层授权

单用户 owner 模型：`Mcp:OAuth:OwnerSubject`（默认 `owner`）作为令牌的 `sub`，同意记录写在同一 subject 下。数据结构（`oauth_consents`：subject → client_id → scopes + knowledge_base_ids）就是未来多用户扩展点，加多用户时只需要把 `sub` 换成登录用户 ID。

同意页上的取舍：

- **写权限默认不勾选**（`knowledge:write`、`files:write` 等），必须用户显式勾上。
- 知识库全选 = 存空数组 = 不限制（以后新建的库也在范围内）；部分选择 = 存具体 ID 列表。
- 重新同意会**覆盖**旧范围（不是取并集）——用户缩小权限时必须真的缩小。
- 撤销同意后：已签发的访问令牌在下一次请求时就被拒（不需要等过期），刷新令牌一并作废。

---

## 6. 端点清单

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/.well-known/oauth-protected-resource` | RFC 9728 资源元数据（也有 `…/mcp` 后缀形式） |
| GET | `/.well-known/oauth-authorization-server` | RFC 8414 授权服务器元数据（含 `client_id_metadata_document_supported`） |
| GET | `/.well-known/mcp/server-card.json` | Server Card（Draft，静态信息，**不承担功能依赖**） |
| GET | `/oauth/authorize` | 同意页（HTML，无缓存，禁 iframe） |
| POST | `/oauth/authorize` | 同意 / 拒绝（`request_id` 同时充当 CSRF 令牌） |
| POST | `/oauth/token` | `authorization_code`（PKCE S256）/ `refresh_token`（轮换） |
| POST | `/oauth/register` | DCR（兜底路径，可关闭） |
| POST | `/oauth/revoke` | RFC 7009（刷新令牌吊销 + 访问令牌 jti 黑名单） |
| GET | `/oauth/jwks` | 公钥 JWKS（只含公钥参数） |
| GET | `/api/mcp/diagnostics` | 协议诊断 + 通道状态 + 验收清单 |
| GET | `/api/mcp/local-config` | 一键复制配置（Cursor / Claude Desktop / curl） |
| GET | `/api/mcp/auth-events` | 认证事件审计（与工具调用审计分开） |
| GET | `/api/mcp/oauth-consents` | 授权同意列表 |
| POST | `/api/mcp/oauth-consents/{id}/revoke` | 撤销同意（立即生效） |

`/api/*` 继承 `/api` 令牌保护；`/.well-known/*` 与 `/oauth/*` 必须匿名可达（客户端在这之前没有凭据）。

**但「匿名可达」不等于「无限制可打」**：`/oauth/token` 与 `/oauth/register` 复用 `/mcp` 的同一个按来源 IP 失败闸门（`AgentAuthThrottle`），口径是 `Server:FailedAuthLimitPerMinute`（默认 30 次/分钟）→ `Server:FailedAuthBlockSeconds`（默认封 300 秒）。只对 4xx 计数、2xx 清零，5xx 与 3xx 不计。被封时返回：

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 285

{"error":"temporarily_unavailable","error_description":"鉴权失败次数过多，请 285 秒后重试。"}
```

注意三个连带事实：

- **三个端点共用同一个 IP 窗口**：在 `/oauth/token` 上撞满 30 次后，同一来源访问 `/mcp` 也会拿到 429（反之亦然）。这是有意的——爆破者不该换个路径就重新获得配额。
- 反向代理之后**必须**配 `Server:TrustForwardedHeaders=true` + `Server:KnownProxies`，否则所有请求共享代理那一个 IP，一个人爆破会把所有人一起封 5 分钟。
- 计数只在内存、重启清零。它是保护手段，不是计费依据。

---

## 7. 配置

```jsonc
"Mcp": {
  "OAuth": {
    "Enabled": true,
    "Issuer": "https://knowspace.example.com",   // 必须是客户端能访问到的绝对地址
    "Resource": "",                               // 留空 = {Issuer}{ServerPath}
    "OwnerSubject": "owner",
    "OwnerDisplayName": "本机用户",
    "AccessTokenLifetimeMinutes": 60,
    "RefreshTokenLifetimeDays": 30,
    "AuthorizationCodeLifetimeSeconds": 300,
    "AllowDynamicRegistration": true,             // DCR 兜底；不需要可关
    "MaxRegisteredClients": 100,
    "RequestsPerMinute": 120,                     // OAuth 通道的兜底额度
    "SigningKeyPath": "",                         // 留空 = {数据根}/oauth/signing-key.pem
    "ClockSkewSeconds": 60,
    "Cimd": {
      "Enabled": true,
      "TimeoutSeconds": 5,
      "MaxResponseBytes": 32768,
      "AllowPrivateAddresses": false,
      "AllowInsecureHttp": false,
      "CacheMinutes": 60
    }
  }
}
```

要点：

- `Enabled=true` 但 `Issuer` 为空/非法 → OAuth 自动禁用并打警告（`aud` 无意义时宁可不开）。
- 签名密钥是 ES256（P-256），落在数据目录的 `oauth/signing-key.pem`（非 Windows 上收敛到 0600）。轮换 = 删文件重启，所有令牌立即失效。
- 令牌是 RFC 9068 `at+jwt`，算法白名单只有 ES256（拒绝 `alg: none` 与算法混淆），`kid` 必须匹配。

---

## 8. 部署要点

1. **同源**：`/mcp` 与 `/oauth/*`、`/.well-known/*` 必须在同一个域名下（Cursor 在资源服务器与授权服务器不同源时有已知问题）。本项目天然满足：授权服务器就是同一个进程。
2. **`Issuer` 必须是公网实际地址**，且与反代上的 `Host`/TLS 一致。用 `X-Forwarded-Proto/Host` 时记得开 `Server:TrustForwardedHeaders`。
3. 反代**不要**给 `/oauth/*` 和 `/.well-known/*` 加 Basic 认证：客户端拿不到。
4. 反向代理要透传 `Mcp-Method`、`MCP-Protocol-Version`、`Authorization`、`Accept: application/json, text/event-stream` 这些头。
5. `/oauth/token` 返回 `Cache-Control: no-store`；同意页同时带 `X-Frame-Options: DENY`。
6. **`Server:PublicBaseUrl` 建议一并填**（例如 `https://knowspace.example.com`）。它只影响 `/api/mcp/local-config` 与 Server Card 里生成的地址，不参与鉴权；但不填的话，OAuth 关闭时「一键复制」会给出 `http://127.0.0.1:5056/mcp`——那个地址在用户本机连不上云端。解析优先级：`Mcp:OAuth:Issuer` > `Server:PublicBaseUrl` > Kestrel 监听地址 > 请求的 scheme/host。

---

## 9. 验收清单（对应任务板 1→7）

用仓库里的脚本一键跑通（需要实例已启用 OAuth）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-McpOAuth.ps1 `
  -BaseUrl https://knowspace.example.com `
  -KnowledgeBaseId 11111111-1111-1111-1111-111111111111 `
  -VerifyRevocation
```

脚本会依次验证：元数据发现 → DCR → GET 同意页 → 同意 → 授权码换令牌 → 用 Bearer 调 `/mcp` → 撤销同意后同一个令牌立即 401。

| # | 验收项 | 通过标准 | 状态 |
| --- | --- | --- | --- |
| 1 | 协议兼容性 | Cursor / Claude Desktop 各一条完整成功（或明确失败）记录，`/api/mcp/diagnostics` 可见 `era` | 脚本已覆盖；真实客户端需手工跑一轮 |
| 2 | `server/discover` 与版本协商 | 无凭据可达，`supportedVersions` 正确；不支持的版本返回明确错误 | ✅ 已验证 |
| 3 | 资源服务器（签名/有效期/`aud`/scope） | 合法令牌 200；错误 `aud` → 401 | ✅ 已验证（`aud` 由单元测试覆盖） |
| 4 | 授权服务器 + CIMD | 浏览器授权全流程；CIMD 拒绝 HTTP/重定向/私有地址 | ✅ 流程已验证；CIMD 策略有单元测试 |
| 5 | DCR 兜底 | `/oauth/register` 可用且可关闭 | ✅ 已验证 |
| 6 | 业务层授权关联 | 越界知识库 → 403；撤销 → 401 | ✅ 已验证 |
| 7 | 认证事件审计 | `/api/mcp/auth-events` 能查到同意、签发、刷新、撤销 | ✅ 已验证 |
| 8 | 一键复制本地配置 | 复制后粘贴可用 | ✅ 接口 + 设置页按钮 |
| 9 | Server Card | 可访问、内容准确、不阻塞功能 | ✅ 已验证 |
| 10 | mDNS 默认关闭 | 默认不广播；开启需显式配置 | ⚠️ 见 §10 |
| 11 | HTTP 桥接插件（A-云端） | 独立需求，B-云端稳定后再做 | ⏳ 未开始 |

---

## 10. 已知风险与兜底

| 风险 | 事实 | 兜底 |
| --- | --- | --- |
| **Cursor 刷新令牌有已知问题** | 2.6.x → 3.x 的刷新支持被标为 Partial：后台刷新成功但活跃传输层不采纳新令牌 | `maf_` 密钥通道长期保留；文档明确「OAuth 刷新失效时切回 `maf_` 配置」 |
| **Cursor 要求 RS/AS 同源** | v2.5.20+ 在不同源时无法完成 OAuth | 默认同源部署（本实现即是） |
| **CIMD 在部分客户端是 SHOULD** | 某些客户端 CIMD 支持不完整会回退 DCR | CIMD 与 DCR 同时可用，不假设客户端一定走 CIMD |
| **协议版本断裂** | Modern 客户端打 Legacy 服务端可能得到实现定义的错误或静默失败 | SDK 双世代 + 诊断端点；`server/discover` 已实现 |
| **内置 MCP 客户端无沙箱** | `Mcp:Servers` 在核心进程内运行 | 文档标注「调试通道，无沙箱」，正式接入走插件路线 |
| **mDNS 不是授权** | 广播只解决「发现」 | 默认关闭；`Server:EnableMdns` 仅为局域网共享预留，开启时仍走 OAuth / 密钥认证 |
| **`EnableMdns` 目前只是开关** | 本阶段未实现实际 DNS-SD 广播器（避免默认打开发现面引入新攻击面） | 开关 + 配置项已就位；置 `true` 时启动会打 `LogWarning` 说「不生效」并提供替代入口，实现留到「局域网共享」真要做时 |

---

## 11. 代码地图

| 关注点 | 文件 |
| --- | --- |
| 配置 | `Options/McpOAuthOptions.cs`、`Options/ServerSecurityOptions.cs` |
| scope ↔ 能力映射 | `OAuth/OAuthScopes.cs` |
| PKCE / 重定向 / 资源标识 | `OAuth/OAuthProtocol.cs` |
| 令牌签发与校验（ES256 / at+jwt / JWKS） | `OAuth/OAuthTokenService.cs` |
| CIMD + SSRF 防护 | `OAuth/CimdClientMetadataResolver.cs` |
| 用例层（注册、授权、换码、刷新、撤销、Bearer 校验） | `OAuth/OAuthService.cs` |
| OAuth 主体 → AgentClient 翻译 | `OAuth/OAuthPrincipal.cs` |
| HTTP 面 | `Hosting/OAuthEndpoints.cs` |
| 表结构 / 存储 | `Persistence/OAuthSchemaEntities.cs`、`Infrastructure/Persistence/SqlSugarOAuthStore.cs` |
| 统一闸门 | `Hosting/AgentClientGate.cs` |
| 协议诊断 | `Hosting/McpProtocolDiagnostics.cs` |
| 本地配置 / Server Card / 审计查询 | `Hosting/LocalMcpConfig.cs`、`Endpoints/McpAccessEndpoints.cs` |
| 端到端验证脚本 | `scripts/Test-McpOAuth.ps1` |
| 单元测试 | `MyFirstMafAgent.Api.Tests/OAuthTests.cs` |
