#!/usr/bin/env bash
#
# 一体部署自检（Linux 容器内 / 裸机 / WSL 都能跑）
#
# 用法：
#   bash scripts/Test-IntegratedStack.sh                       # 默认 http://127.0.0.1:5056
#   bash scripts/Test-IntegratedStack.sh --base-url http://127.0.0.1:5057
#   bash scripts/Test-IntegratedStack.sh --token "$MAF_SERVER_TOKEN"
#   bash scripts/Test-IntegratedStack.sh --pg-password postgres     # 额外验证 pgvector 版本
#
# 只依赖 curl；没有 jq 也能跑（用 grep 取字段）。
# 退出码：0 = 全部通过；1 = 有硬失败项。
#
set -uo pipefail

BASE_URL="http://127.0.0.1:5056"
TOKEN=""
PG_BIN="/usr/lib/postgresql/17/bin"
PG_PASSWORD=""
PG_PORT="55432"

PASS=0
FAIL=0
SKIP=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        --base-url)    BASE_URL="${2:-}"; shift 2 ;;
        --token)       TOKEN="${2:-}"; shift 2 ;;
        --pg-bin)      PG_BIN="${2:-}"; shift 2 ;;
        --pg-password) PG_PASSWORD="${2:-}"; shift 2 ;;
        --pg-port)     PG_PORT="${2:-}"; shift 2 ;;
        -h|--help)     sed -n '3,13p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "未知参数：$1" >&2; exit 1 ;;
    esac
done

BASE_URL="${BASE_URL%/}"

ok()      { PASS=$((PASS+1)); printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()     { FAIL=$((FAIL+1)); printf '  \033[31m✗\033[0m %s\n' "$*"; }
skip()    { SKIP=$((SKIP+1)); printf '  \033[33m-\033[0m %s\n' "$*"; }
section() { printf '\n\033[36m%s\033[0m\n' "$*"; }

AUTH=()
[[ -n "$TOKEN" ]] && AUTH=(-H "X-Api-Token: ${TOKEN}")

# 发一个请求，把结果写进三个全局变量。
# 刻意不用命令替换：$(...) 是子 shell，里面赋的 BODY/HDRS 传不回来。
#   STATUS = "200" / "401" / ... ；BODY = 响应体；HDRS = 原始响应头
STATUS=""
BODY=""
HDRS=""
req() {
    local path="$1"; shift
    local tmp; tmp="$(mktemp)"
    HDRS="$(curl -sS -D - -o "$tmp" --max-time 20 "$@" "${BASE_URL}${path}" 2>&1)"
    BODY="$(cat "$tmp" 2>/dev/null)"
    rm -f "$tmp"
    STATUS="$(printf '%s\n' "$HDRS" | awk 'NR==1{print $2}' | tr -d '\r')"
    [[ "$STATUS" =~ ^[0-9]{3}$ ]] || STATUS="000"
}

hdr_value() {
    printf '%s\n' "$HDRS" | tr -d '\r' | grep -i "^$1:" | head -n1 | cut -d' ' -f2-
}

# ── 1. 健康与数据库 ──────────────────────────────────────────────────
section "1. 健康检查 /api/health"
req /api/health
if [[ "$STATUS" == "200" ]]; then
    ok "HTTP 200"
else
    bad "HTTP ${STATUS}（服务没起来？裸机看 journalctl -u myfirstmafagent，容器看 docker logs）"
    printf '     curl 原始输出：%s\n' "${HDRS:0:200}"
fi

if [[ "$BODY" == *'"status":"ok"'* ]]; then
    ok "status=ok"
else
    bad "status 字段异常：${BODY:0:200}"
fi

# 关键项：/api/health 会真查一次数据库，这条过了才算内置库真起来。
if [[ "$BODY" == *'"database":"connected"'* ]]; then
    ok "database=connected（内置 PostgreSQL 已就绪）"
else
    bad "database 未连接：${BODY:0:200}"
fi

# ── 2. 内置 PostgreSQL ──────────────────────────────────────────────
section "2. 内置 PostgreSQL"
PG_ISREADY=""
if command -v pg_isready >/dev/null 2>&1; then PG_ISREADY="pg_isready"
elif [[ -x "$PG_BIN/pg_isready" ]]; then PG_ISREADY="$PG_BIN/pg_isready"; fi

if [[ -n "$PG_ISREADY" ]]; then
    if "$PG_ISREADY" -h 127.0.0.1 -p "$PG_PORT" >/dev/null 2>&1; then
        ok "127.0.0.1:${PG_PORT} 可接受连接"
    else
        bad "127.0.0.1:${PG_PORT} 连不上（内置库没起来？）"
    fi
else
    skip "找不到 pg_isready（跳过端口探测）"
fi

# postgres 是多进程架构：1 主进程 + checkpointer/bgwriter/wal_writer/autovacuum/bgworker，
# 空闲时固定 6 个；每来一个客户端连接再加 1 个。这里只提示，不作为失败项。
PG_COUNT="$(pgrep -c postgres 2>/dev/null || true)"
PG_COUNT="${PG_COUNT:-0}"
if [[ "$PG_COUNT" -ge 6 ]]; then
    ok "postgres 进程 ${PG_COUNT} 个（≥6 属正常：1 主进程 + 5 后台 + 每连接 1 个）"
elif [[ "$PG_COUNT" -ge 1 ]]; then
    skip "postgres 进程 ${PG_COUNT} 个（进程命名空间隔离时看不全，不影响判定）"
else
    bad "没有 postgres 进程"
fi

# ── 3. pgvector ─────────────────────────────────────────────────────
section "3. pgvector 扩展"
PSQL=""
if command -v psql >/dev/null 2>&1; then PSQL="psql"
elif [[ -x "$PG_BIN/psql" ]]; then PSQL="$PG_BIN/psql"; fi

if [[ -n "$PSQL" && -n "$PG_PASSWORD" ]]; then
    EXT="$(PGPASSWORD="$PG_PASSWORD" "$PSQL" -h 127.0.0.1 -p "$PG_PORT" -U postgres \
            -d myfirstmafagent -tAc "select extversion from pg_extension where extname='vector'" 2>&1)"
    if [[ -n "$EXT" && "$EXT" != *ERROR* && "$EXT" != *error* && "$EXT" != *FATAL* ]]; then
        ok "vector ${EXT} 已安装"
    else
        bad "vector 未安装或查询失败：${EXT:0:200}"
        echo "     修法：apt-get install -y postgresql-17-pgvector 后重启服务"
    fi
else
    skip "未提供 --pg-password，跳过版本查询（服务能启动本身就说明 CREATE EXTENSION 过了）"
fi

# ── 4. 数据落地位置 ─────────────────────────────────────────────────
section "4. 数据落地位置"
FOUND_PGDATA=""
for candidate in /app/App_Data/pgdata /var/lib/myfirstmafagent/pgdata \
                 "${HOME:-/nonexistent}/.local/share/MyFirstMafAgentData/pgdata"; do
    if [[ -f "$candidate/PG_VERSION" ]]; then FOUND_PGDATA="$candidate"; break; fi
done

if [[ -n "$FOUND_PGDATA" ]]; then
    ok "pgdata = $FOUND_PGDATA（PG_VERSION=$(cat "$FOUND_PGDATA/PG_VERSION" 2>/dev/null)）"
    case "$FOUND_PGDATA" in
        /app/App_Data/*|/var/lib/*|/opt/*/App_Data/*) ok "位于预期的持久化位置" ;;
        *) bad "数据目录不在挂卷/数据根下 —— 容器重建会丢数据，检查 PortablePostgres:RootDirectory" ;;
    esac
else
    bad "找不到 pgdata（数据没落地？）"
fi

# ── 5. MCP 认证挑战 ─────────────────────────────────────────────────
section "5. /mcp 未认证挑战"
req /mcp -X POST -H 'Content-Type: application/json' \
    -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
if [[ "$STATUS" == "401" ]]; then
    ok "未带凭据返回 401"
elif [[ "$STATUS" == "429" ]]; then
    bad "429：来源 IP 已被失败闸门封禁（Server:FailedAuthLimitPerMinute），等几分钟或重启服务"
else
    bad "期望 401，实际 ${STATUS}（Mcp:RequireClientKey 被关了？）"
fi

CHALLENGE="$(hdr_value WWW-Authenticate)"
if [[ -n "$CHALLENGE" ]]; then
    ok "WWW-Authenticate：${CHALLENGE}"
    case "$CHALLENGE" in
        *resource_metadata*) ok "OAuth 已启用（挑战头指向 RFC 9728 元数据）" ;;
        *X-Api-Key*)         ok "仅 maf_ 密钥通道（OAuth 未启用）" ;;
    esac
else
    bad "缺少 WWW-Authenticate（客户端无法自动发现认证方式）"
fi

# ── 6. 管理端点 ─────────────────────────────────────────────────────
section "6. 管理端点（设了 MAF_SERVER_TOKEN 时要加 --token）"
req /api/mcp/local-config "${AUTH[@]}"
if [[ "$STATUS" == "200" ]]; then
    URL="$(printf '%s' "$BODY" | grep -o '"baseUrl":"[^"]*"' | head -n1 | cut -d'"' -f4)"
    SRC="$(printf '%s' "$BODY" | grep -o '"baseUrlSource":"[^"]*"' | head -n1 | cut -d'"' -f4)"
    ok "local-config 200 → ${URL}（来源 ${SRC}）"
    case "${SRC:-}" in
        public-base-url|oauth-issuer) ok "对外地址来自配置，一键复制给别人能直接用" ;;
        kestrel|request) skip "地址只在本机可用；对外部署请设 Server:PublicBaseUrl" ;;
    esac
else
    if [[ "$STATUS" == "401" ]]; then
        bad "local-config 返回 401（需要 --token）"
    else
        bad "local-config 返回 ${STATUS}"
    fi
fi

req /api/mcp/diagnostics "${AUTH[@]}"
if [[ "$STATUS" == "200" ]]; then
    VERSIONS="$(printf '%s' "$BODY" | grep -o '"supportedProtocolVersions":\[[^]]*\]' | head -n1)"
    ok "diagnostics 200 ${VERSIONS:+→ $VERSIONS}"
else
    bad "diagnostics 返回 ${STATUS}"
fi

# ── 7. 公开发现端点 ─────────────────────────────────────────────────
section "7. 公开发现端点"
req /.well-known/mcp/server-card.json
if [[ "$STATUS" == "200" ]]; then ok "server-card 200"; else bad "server-card 返回 ${STATUS}"; fi

req /.well-known/oauth-protected-resource
if [[ "$STATUS" == "200" ]]; then
    ok "受保护资源元数据 200（OAuth 通道已启用）"
    req /.well-known/oauth-authorization-server
    if [[ "$STATUS" == "200" ]]; then ok "授权服务器元数据 200"; else bad "授权服务器元数据返回 ${STATUS}"; fi
else
    skip "OAuth 元数据 ${STATUS}（OAuth 关闭时属正常：Mcp:OAuth:Enabled=false）"
fi

# ── 汇总 ────────────────────────────────────────────────────────────
section "汇总"
printf '  通过 %d，失败 %d，跳过 %d\n' "$PASS" "$FAIL" "$SKIP"
if [[ "$FAIL" -gt 0 ]]; then
    printf '\n\033[31m有一体化链路没打通，按上面的 ✗ 逐条排查。\033[0m\n'
    exit 1
fi
printf '\n\033[32m一体部署自检通过。\033[0m\n'
