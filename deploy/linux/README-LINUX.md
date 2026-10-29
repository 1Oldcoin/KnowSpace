# 知间（Zhijian）— Linux 交付包

> **只有一份 Linux 产物。** 前端是 Vue 构建出的静态文件（`wwwroot/`），由 API 进程自己托管，
> 用**浏览器**访问 `http://<主机>:5056`。所以不存在"有 UI 版 / 无 UI 版"的区分 ——
> 装了就有界面，不装就什么都没有。Electron 桌面壳只服务 Windows 安装包（见文末）。

---

## 包里有什么

```
Zhijian-linux-x64/
├── app/                                  ← 后端 + 前端静态文件（310 MB）
│   ├── Zhijian.Api                       ← ELF 可执行（**自包含**：目标机不需要装 .NET 10）
│   ├── Zhijian.Api.dll
│   ├── appsettings.json
│   ├── wwwroot/                          ← 界面就在这儿（index.html + assets）
│   └── ExternalTools/Embedding/          ← 离线向量化模型（bge-small-zh-v1.5，97 MB）
├── deploy/linux/
│   ├── install.sh                        ← 裸机一键安装（apt PG17 + systemd）
│   ├── myfirstmafagent.service
│   └── myfirstmafagent.env.example
├── scripts/Test-IntegratedStack.sh       ← 装完自检（7 段）
└── README-LINUX.md                       ← 本文件
```

注意：`ExternalTools/pgSQL` 与 `ExternalTools/ffmpeg` **不在包里**（只有 README 留着）。
Linux 上这两个都由 apt 提供，包里那份是 Windows 专用的 370 MB 死重量。

> 这不是为了省体积才砍的，是**砍不掉的理由**：Linux 的 PostgreSQL 二进制与发行版 ABI 绑定
> （同一份 gnu 构建在 Debian 12 能跑、Ubuntu 24.04 因缺 `libicu72.so.72` 直接加载失败），
> 而且官方二进制**不含 pgvector** —— 缺 `lib/vector.so` 时 `CREATE EXTENSION vector` 会抛异常，
> `PortablePostgresHost` 把它当致命错误，启动直接失败而不是降级。所以 `install.sh` 走 apt，
> 并把 `PortablePostgres__BinDirectory` 指到 `/usr/lib/postgresql/17/bin`。

> ⚠️ **不要绕过 `install.sh` 直接 `./app/Zhijian.Api`**：包里没有 pgSQL 二进制，
> `PortablePostgresHost` 找不到 `postgres` 会抛 `FileNotFoundException`，进程起不来。
> 确实要手工跑，至少给这两个环境变量：
>
> ```bash
> export PortablePostgres__BinDirectory=/usr/lib/postgresql/17/bin
> export PortablePostgres__RootDirectory=/var/lib/myfirstmafagent
> ./app/Zhijian.Api --urls http://127.0.0.1:5056
> ```
>
> 或者关掉内置库改用外部 PostgreSQL：`PortablePostgres__Enabled=false` +
> `ConnectionStrings__KnowledgeBase=Host=...;Database=...;Username=...;Password=...`
> （外部库需自行装好 pgvector）。

> ⚠️ **解压后先补可执行位**：这个包是在 Windows 上打的，`Zhijian.Api`、`install.sh`、
> `Test-IntegratedStack.sh` 都会是 `0644`。`install.sh` 自己会 `chmod 0755`，所以走路线 B 不用管；
> 但如果你想不经安装脚本直接跑 `./app/Zhijian.Api`，先执行：
>
> ```bash
> chmod +x app/Zhijian.Api deploy/linux/install.sh scripts/Test-IntegratedStack.sh
> ```

---

## 路线 A：Docker（推荐先试这条）

在**仓库根目录**（不是这个包）执行：

```bash
cp .env.example .env        # 填 MAF_SERVER_TOKEN / PG_PASSWORD / PUBLIC_BASE_URL / LLM_API_KEY
docker compose up -d --build
docker compose ps           # 等到 app 变成 healthy（首次约 1~2 分钟，含 initdb）
```

容器起来后 `curl -s http://127.0.0.1:5056/api/health` 应返回 `{"status":"ok",...}`。

> ⚠️ **不要期待浏览器界面**：`/api` 需要 `X-Api-Token`，浏览器里没人注入它，打开页面会看到
> 一片 401。容器版的实际用途是跑 MCP 服务端 + curl 管理；要界面请看
> `docs/CLOUD-DEPLOYMENT.md` §3.1（反代注入令牌）或 §3.7.1（SSH 隧道 + 桌面形态）。

自检：

```bash
docker compose exec app bash /app/scripts/Test-IntegratedStack.sh \
  --pg-password "$(grep ^PG_PASSWORD .env | cut -d= -f2)"
```

**默认是一体模式**：一个容器里跑 API + 前端 + PostgreSQL 17 + pgvector，数据库由 API 进程
自己 `initdb` / `pg_ctl start`，数据落在 `maf-app-data` 卷。

想拆成"API + 独立数据库"两个容器：

```bash
docker compose --profile external up -d --build
```

数据卷：`maf-app-data`（一体）/ `maf-pgdata` + `maf-api-data`（external）。
**备份就备份卷**；`docker compose down -v` 会连数据一起删，别乱敲。

---

## 路线 B：裸机 Linux（systemd）

需要：Ubuntu 22.04+/Debian 12+、root、外网能装 apt 包。**不需要装 .NET**（产物是自包含发布，`libcoreclr.so` 等运行时全在包里）。

```bash
# 1) 把包传上去并解开
scp Zhijian-linux-x64-1.0.1.tar.gz user@host:/tmp/
ssh user@host
tar xzf /tmp/Zhijian-linux-x64-1.0.1.tar.gz -C /tmp/

# 2) 装
sudo bash /tmp/Zhijian-linux-x64/deploy/linux/install.sh \
  --source /tmp/Zhijian-linux-x64/app

# 3) 看状态
systemctl status myfirstmafagent
journalctl -u myfirstmafagent -f
```

安装脚本会做六件事：apt 装 `postgresql-17` + `postgresql-17-pgvector` → 建 `myfirstmafagent`
系统用户 → 铺程序到 `/opt/myfirstmafagent`、数据到 `/var/lib/myfirstmafagent`、配置到
`/etc/myfirstmafagent` → 装 systemd 单元并启动 → 轮询 `/api/health` → 跑自检。

配置文件 `/etc/myfirstmafagent/myfirstmafagent.env` 里**有两件事必须手工改**（脚本会提示）：

| 变量 | 为什么必须改 |
| --- | --- |
| `MAF_SERVER_TOKEN` | 不设则 `/api` 不鉴权。`openssl rand -hex 32` |
| `Server__PublicBaseUrl` | 不设则「一键复制 MCP 配置」给的是 `127.0.0.1`，别人连不上 |

数据库口令脚本会自动生成随机值并写进 env。

改完 `sudo systemctl restart myfirstmafagent`。

**服务只监听 `127.0.0.1:5056`**。要让外面能访问，前面挂 nginx/Caddy 上 TLS，
并把 `Server__TrustForwardedHeaders=true` + `Server__KnownProxies__0=<代理IP>` 打开 ——
否则审计日志里所有 IP 都是 127.0.0.1，而且 `/api/desktop/shutdown` 的回环校验会被骗过。

---

## 想换成外部数据库

两条路都只改两个值，代码路径不变：

```bash
PortablePostgres__Enabled=false
ConnectionStrings__KnowledgeBase=Host=pg.example.com;Port=5432;Database=zhijian;Username=zhijian;Password=***
```

外部库需要 `pgvector` 扩展可用（`CREATE EXTENSION vector` 会执行）。
大版本**必须是 17** —— `PortablePostgresHost.ExpectedMajorVersion` 里硬编码了，
用 18 打开旧数据目录会被判为不兼容。

---

## 首次启动慢是正常的

`initdb`（3~10 秒）+ 全量建表（首次几十秒，之后靠 schema 指纹跳过）。所以：

- 容器 `start_period` 给了 90 秒
- systemd 脚本轮询 120 秒
- 之后每次启动约 1~2 秒

---

## 这台机器上**没有验证过**的部分

诚实说明：打包这台机器没有 Docker、没有 WSL、没有 bash，所以以下都是**在 Linux 上第一次跑**，
不可能已经验证：

| 项目 | 状态 |
| --- | --- |
| `dotnet publish -r linux-x64` | ✅ 验证过（ELF 头 `7F 45 4C 46`、310 MB、wwwroot 与 ONNX 模型齐全） |
| `docker-compose.yml` 语法 | ✅ 解析通过（pyyaml），services/profiles 正确 |
| `docker build` | ❌ 没跑过 |
| `docker compose up`（两种形态） | ❌ 没跑过 |
| `deploy/linux/install.sh` | ❌ 没跑过 |
| `scripts/Test-IntegratedStack.sh` | ❌ 没在真发行版上跑过（只做了关键字/括号配平检查） |

所以你第一轮测试请按这个顺序，出错时能立刻定位是哪一层：

1. `docker build` 能不能过（PGDG 源、`VERSION_CODENAME`、pgvector 包名）
2. `docker compose up` 后 `/api/health` 是否 `{"status":"ok","database":"connected"}`
3. `pgdata` 是否真的落在卷里（`docker compose exec app ls /app/App_Data/pgdata/PG_VERSION` → `17`）
4. 前端是否打得开（`wwwroot` 有没有被正确 COPY）
5. 上传一个 PDF，看切分与向量化是否跑通（ONNX 模型 + pgvector 扩展）

`Test-IntegratedStack.sh` 覆盖了 1~4 和 `/mcp` 的 401、local-config、server-card。

---

## 关于 Electron（Windows 桌面版）

`desktop-electron/` 那套**只在 Windows 上有意义**，且配的是 NSIS 安装包（`win: target: nsis`）。
Linux 上没有对应产物。（注意：这不代表 Linux 上浏览器就能直接用界面 —— 令牌仍需要一个注入点，
见前面的警告。）

如果以后确实想要"Linux 桌面应用"（双击图标、独立窗口、托盘），那要另做
`electron-builder` 的 `linux:` target（AppImage/deb），需要处理：

- Electron 的 Linux 依赖（libnss3、libgbm 等）
- 沙箱（`chrome-sandbox` 需要 `setuid root` 或 `--no-sandbox`）
- 把后端 sidecar 从 `win-x64` 换成 `linux-x64` 并塞进 `extraResources`
- 自动更新的 Linux 形态（AppImage 的 `latest-linux.yml`）

这是独立的一件事，不是"打另一个包"。
