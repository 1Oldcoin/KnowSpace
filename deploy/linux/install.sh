#!/usr/bin/env bash
#
# 知间（MyFirstMafAgent）Linux 裸机安装脚本 —— 一体形态（API + 内置 PostgreSQL 17）
#
# 用法：
#   # 1) 在 Windows 开发机上发布 Linux 版本
#   powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime linux-x64
#
#   # 2) 把 publish\console-linux-x64 整个目录拷到服务器，然后
#   sudo bash deploy/linux/install.sh --source /tmp/console-linux-x64
#
# 做了什么（每一步都可重复执行，已完成的会跳过）：
#   1. 检查发行版与 root
#   2. 装 PostgreSQL 17 + pgvector（走 PGDG 源；Ubuntu/Debian 官方源版本未必是 17）
#   3. 建系统用户 myfirstmafagent（无登录 shell）
#   4. 铺程序到 /opt/myfirstmafagent，数据放 /var/lib/myfirstmafagent
#   5. 写 /etc/myfirstmafagent/myfirstmafagent.env（已存在则不动）
#   6. 装 systemd 单元并启动，最后跑一遍自检
#
# 想改用外部 PostgreSQL：先把 env 里的 PortablePostgres__Enabled 改成 false 并填
# ConnectionStrings__KnowledgeBase，再 systemctl restart。脚本不需要重跑。
#
set -euo pipefail

# apt 在部分发行版上会弹 needrestart / 交互式确认，把安装脚本卡在无人值守的 SSH 里。
export DEBIAN_FRONTEND=noninteractive

APP_DIR="/opt/myfirstmafagent"
DATA_DIR="/var/lib/myfirstmafagent"
CONF_DIR="/etc/myfirstmafagent"
SERVICE="myfirstmafagent"
SERVICE_USER="myfirstmafagent"
PG_BIN="/usr/lib/postgresql/17/bin"
SOURCE=""

say()  { printf '\n\033[36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[33m[warn] %s\033[0m\n' "$*"; }
die()  { printf '\033[31m[error] %s\033[0m\n' "$*" >&2; exit 1; }

usage() {
    sed -n '3,22p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)   SOURCE="${2:-}"; shift 2 ;;
        --app-dir)  APP_DIR="${2:-}"; shift 2 ;;
        --data-dir) DATA_DIR="${2:-}"; shift 2 ;;
        -h|--help)  usage ;;
        *) die "未知参数：$1（用 --help 查看用法）" ;;
    esac
done

[[ $EUID -eq 0 ]] || die "请用 root 运行：sudo bash $0 --source <发布目录>"

# ── 1. 发行版检查 ─────────────────────────────────────────────────────
say "1/6 检查发行版"
[[ -r /etc/os-release ]] || die "找不到 /etc/os-release，本脚本只支持 Debian/Ubuntu 系。"
# shellcheck disable=SC1091
. /etc/os-release
CODENAME="${VERSION_CODENAME:-}"
[[ -n "$CODENAME" ]] || die "无法从 /etc/os-release 取到 VERSION_CODENAME。"
case "${ID:-}${ID_LIKE:-}" in
    *debian*|*ubuntu*) : ;;
    *) die "只支持 Debian/Ubuntu 系（当前 ID=${ID:-?}）。其他发行版请手工装 PostgreSQL 17 + pgvector 后设置 PortablePostgres__BinDirectory。" ;;
esac
echo "发行版：${PRETTY_NAME:-$ID}（$CODENAME）"

# ── 2. PostgreSQL 17 + pgvector ──────────────────────────────────────
say "2/6 安装 PostgreSQL 17 + pgvector"
if [[ -x "$PG_BIN/postgres" ]]; then
    echo "已存在：$PG_BIN/postgres（跳过安装）"
else
    apt-get update
    apt-get install -y --no-install-recommends ca-certificates curl gnupg
    install -d /usr/share/postgresql-common/pgdg
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list
    apt-get update
    # 只装服务端二进制与 pgvector；不装 postgresql-common 的集群模板服务。
    apt-get install -y --no-install-recommends postgresql-17 postgresql-17-pgvector
    systemctl disable --now postgresql 2>/dev/null || true
fi
[[ -x "$PG_BIN/postgres" ]] || die "安装后仍找不到 $PG_BIN/postgres。"
[[ "$("$PG_BIN/postgres" --version)" == *" 17."* ]] || warn "版本不是 17：$("$PG_BIN/postgres" --version)（内置库大版本硬编码为 17）"

if ! ls "$PG_BIN/../lib/vector.so" >/dev/null 2>&1; then
    warn "没找到 $PG_BIN/../lib/vector.so —— CREATE EXTENSION vector 会失败。"
    warn "请安装：apt-get install -y postgresql-17-pgvector"
fi

# ── 3. 服务账号 ──────────────────────────────────────────────────────
say "3/6 创建系统用户 $SERVICE_USER"
if id -u "$SERVICE_USER" >/dev/null 2>&1; then
    echo "已存在（跳过）"
else
    # 无登录 shell、无家目录：这个账号只用来跑服务，不需要交互。
    useradd --system --no-create-home --shell /usr/sbin/nologin --home-dir "$APP_DIR" "$SERVICE_USER"
fi

# ── 4. 程序与数据目录 ────────────────────────────────────────────────
say "4/6 铺程序到 $APP_DIR，数据到 $DATA_DIR"
[[ -n "$SOURCE" ]] || die "缺少 --source <发布目录>（Publish-Portable.ps1 -Runtime linux-x64 的产物）。"
[[ -f "$SOURCE/Zhijian.Api" ]] || die "$SOURCE 下没有 Zhijian.Api，确认这是 console-linux-x64 的发布产物。"
[[ -f "$SOURCE/wwwroot/index.html" ]] || warn "$SOURCE 下没有 wwwroot/index.html，本次只提供 API（前端未发布）。"

install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$APP_DIR"
install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$DATA_DIR"
# uploads 的**上层**是数据根：Storage__RootPath 指到这里，OAuth 签名私钥才能落在
# $DATA_DIR/oauth/ 而不是 root 属主的程序目录里。
for sub in uploads models logs; do
    install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$DATA_DIR/$sub"
done
install -d -m 0750 "$CONF_DIR"

# 服务运行中替换文件会拿到 "Text file busy"，先停。
if systemctl is-active --quiet "$SERVICE"; then
    systemctl stop "$SERVICE"
fi

# 只同步程序文件，不碰 App_Data（那是数据）。
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete \
        --exclude 'App_Data/' \
        "$SOURCE"/ "$APP_DIR"/
else
    warn "没有 rsync，退化为 cp -a（不会删除 $APP_DIR 里的旧文件）。"
    cp -a "$SOURCE"/. "$APP_DIR"/
fi
chown -R root:"$SERVICE_USER" "$APP_DIR"
chmod -R g+rX "$APP_DIR"
# 主程序与原生二进制必须有可执行位（从 Windows 拷过来或从 zip/tar 解出来会丢）。
find "$APP_DIR" -maxdepth 6 -type f \
     \( -name 'Zhijian.Api' -o -name 'ffmpeg' -o -name 'ffprobe' \
        -o -name 'postgres' -o -name 'pg_ctl' -o -name 'initdb' \
        -o -name 'psql' -o -name 'pg_isready' -o -name 'pg_dump' -o -name 'pg_restore' \) \
     -exec chmod 0755 {} +
chown -R "$SERVICE_USER":"$SERVICE_USER" "$DATA_DIR"

# ── 5. 配置文件 ──────────────────────────────────────────────────────
say "5/6 写配置 $CONF_DIR/myfirstmafagent.env"
ENV_FILE="$CONF_DIR/myfirstmafagent.env"
if [[ -f "$ENV_FILE" ]]; then
    echo "已存在，保持不动（升级不会覆盖你的配置）"
else
    SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
    if [[ -f "$SCRIPT_DIR/myfirstmafagent.env.example" ]]; then
        install -m 0640 -o root -g "$SERVICE_USER" \
            "$SCRIPT_DIR/myfirstmafagent.env.example" "$ENV_FILE"
    else
        die "找不到 $SCRIPT_DIR/myfirstmafagent.env.example，无法生成配置。"
    fi

    # 生成强口令并写进 env：内置库的默认口令是 postgres，等于没锁。
    STRONG_PASSWORD="$(openssl rand -hex 24 2>/dev/null || head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    sed -i "s|^PortablePostgres__Password=.*|PortablePostgres__Password=${STRONG_PASSWORD}|" "$ENV_FILE"

    echo "已生成随机数据库口令并写入 $ENV_FILE"
    echo
    warn "还有两件事要你手工决定（都在 $ENV_FILE 里）："
    warn "  · Server__PublicBaseUrl  —— 填公网地址，否则「一键复制」给出的是 127.0.0.1"
    warn "  · MAF_SERVER_TOKEN       —— 对外暴露前必须设（openssl rand -hex 32）"
fi

# ── 6. systemd ───────────────────────────────────────────────────────
say "6/6 安装 systemd 单元并启动"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
[[ -f "$SCRIPT_DIR/myfirstmafagent.service" ]] || die "找不到 $SCRIPT_DIR/myfirstmafagent.service"
install -m 0644 "$SCRIPT_DIR/myfirstmafagent.service" "/etc/systemd/system/${SERVICE}.service"
systemctl daemon-reload
systemctl enable "$SERVICE"
systemctl restart "$SERVICE"

echo
say "等待服务就绪（首次启动要 initdb + 全量建表，最多约 120 秒）"
for i in $(seq 1 60); do
    if curl -fsS --max-time 2 http://127.0.0.1:5056/api/health >/dev/null 2>&1; then
        echo "就绪（约 $((i * 2)) 秒）"
        break
    fi
    if [[ $i -eq 60 ]]; then
        warn "60 次探活都失败。看日志：journalctl -u $SERVICE -n 120 --no-pager"
    fi
    sleep 2
done

echo
say "自检"
curl -fsS http://127.0.0.1:5056/api/health || true
echo
SELF_CHECK="$SCRIPT_DIR/../../scripts/Test-IntegratedStack.sh"
if [[ -f "$SELF_CHECK" ]]; then
    bash "$SELF_CHECK" --base-url http://127.0.0.1:5056 --pg-bin "$PG_BIN" || true
else
    warn "没找到 $SELF_CHECK（只拷了 deploy/linux？）——跳过完整自检。"
    warn "需要时可单独执行：bash scripts/Test-IntegratedStack.sh --base-url http://127.0.0.1:5056"
fi

echo
say "完成"
cat <<EOF
服务：      systemctl status $SERVICE
日志：      journalctl -u $SERVICE -f
配置：      $ENV_FILE   （改完 systemctl restart $SERVICE）
程序：      $APP_DIR
数据：      $DATA_DIR  （pgdata / pglogs / pgsockets，备份就备份这个目录）
浏览器：    http://<服务器地址>:5056  （只监听 127.0.0.1，对外请先上反代 + TLS）

安全提醒：
  1. appsettings.json 里的 Urls 是 127.0.0.1，本脚本靠命令行 --urls 覆盖；
     想换监听地址请改 systemd 单元的 ExecStart，不要只改 appsettings。
  2. 内置数据库只监听容器/主机内的 127.0.0.1:55432，不对外。
  3. 对外暴露前必读 docs/CLOUD-DEPLOYMENT.md。
EOF
