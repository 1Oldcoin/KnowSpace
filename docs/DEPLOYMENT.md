# 跨平台部署（Windows / Linux / macOS / Docker）

> 这份文档是**部署口径的唯一来源**。改打包方式、加平台、动原生工具目录，先读这里。
>
> ☁️ **对外暴露到公网（云主机 / 容器）时，先读 [`CLOUD-DEPLOYMENT.md`](CLOUD-DEPLOYMENT.md)** ——
> 那里有 `/api` 令牌、可信代理头、失败闸门三项必做配置；本文只讲“怎么产出产物”。

## 1. 三种交付形态

| 形态 | 产物 | 说明 |
| --- | --- | --- |
| **控制台版** | `publish/console`（win-x64）或 `publish/console-<rid>` | 只跑 API + `wwwroot`，启动后自动开浏览器。**不依赖 Electron**。 |
| **桌面版** | NSIS 安装包（Windows）/ 各平台 electron-builder 产物 | Electron 壳把同一份 API 当 sidecar 拉起。壳只负责窗口与令牌注入，不含业务逻辑。 |
| **容器版** | OCI 镜像 | 只跑 API + `wwwroot`。**不含 Electron**，通常连外部 PostgreSQL。 |

Electron 自身已经跨平台，所以「桌面版」不需要后端做额外适配；**需要适配的是后端里那些与平台绑定的原生二进制**（便携数据库、ffmpeg）。

## 2. 后端是一体的：API + 数据库 + 模型

一个可用的后端包 = 这四块：

| 组成 | 位置 | 是否随包分发 |
| --- | --- | --- |
| API + 前端静态文件 | `MyFirstMafAgent.Api[.exe]`、`wwwroot/` | 是（`dotnet publish` + `npm run build`） |
| 便携 PostgreSQL（PostgreSQL 17.9 + pgvector 0.8.7，EDB Windows x64） | `ExternalTools/pgSQL/<rid>/`（`bin/`、`lib/`、`share/`） | **按平台**，可留空（容器里用外部 PG） |
| Embedding 模型（bge-small-zh-v1.5, 97 MB） | `ExternalTools/Embedding/bge-small-zh-v1.5/` | 是（与平台无关，ONNX） |
| ffmpeg | `ExternalTools/ffmpeg/<rid>/` 或 PATH | 可选（只有视频需要） |

Whisper 模型（141 MB 起）**不随包分发**，运行时可下载或从设置页导入。

## 3. 支持的平台矩阵

| RID | 便携数据库 | ffmpeg | 现状 |
| --- | --- | --- | --- |
| `win-x64` | ✅ 随包 `ExternalTools/pgSQL/win-x64/`（PostgreSQL 17.9 + pgvector 0.8.7） | ✅ 随包 | **已实测**：`initdb` → `pg_ctl start` → `CREATE DATABASE` → `CREATE EXTENSION vector` → `/api/health` 200；复用已在跑的实例、`--stop-portable-postgres` 停库均已验证。安装包形态待复测 |
| `linux-x64` | 未装配：需该平台 PostgreSQL 17.x + pgvector 同构装配到 `ExternalTools/pgSQL/linux-x64/{bin,lib,share}` | 建议 apt 安装 | **已验证可发布**（`dotnet publish -r linux-x64` 产出正常）；容器镜像尚未在本机 `docker build` 验证 |
| `linux-arm64` | 未装配：同上（aarch64 版 PG 17.x） | 建议包管理器 | 同上 |
| `osx-arm64` | 未装配：同上（macOS aarch64 版 PG 17.x） | 建议 `brew` | 未验证（无 macOS 机器） |
| `osx-x64` | 未装配：同上 | 建议 `brew` | 未验证 |
| `win-arm64` | ❌ 未装配 | — | 仅框架可跑，便携库不可用 |

**同构装配**的意思是：目录里必须是**同一个 PostgreSQL 主版本的整套发行件**——`bin/`（`postgres`、`pg_ctl`、`initdb`、`pg_isready`、`psql` 及运行时库）、`lib/`（含 `vector` 扩展的共享库）、`share/extension/`（`vector.control`、`vector--<版本>.sql`）。三块拆开或版本错配都起不来。`bin` 与数据目录的主版本必须一致（当前随包是 17），换版本要连数据目录一起换。

## 4. 原生工具怎么放（自动查找规则）

便携 PostgreSQL 与 ffmpeg 走两套解析：

**便携 PostgreSQL 按目录解析**（`PlatformRuntime.ResolvePostgresBinDirectory`）：

```
ExternalTools/pgSQL/<rid>/bin/     ← 推荐（当前随包只有 win-x64）
ExternalTools/pgSQL/bin/           ← 扁平布局兼容
```

`PortablePostgres:BinDirectory` 可以填 `bin` 目录，也可以填 `pgsql` 根目录（自动补 `bin`）；填了但目录不存在 → 回落自动定位并记日志，不启动失败。

**其它工具按文件解析**（`PlatformRuntime.ResolveTool`，依次尝试，**先命中先用**）：

```
ExternalTools/<tool>/<rid>/<上游原始文件名>     ← 推荐
ExternalTools/<tool>/<rid>/<工具名>[.exe]
ExternalTools/<tool>/<上游原始文件名>
ExternalTools/<tool>/<工具名>-<rid>[.exe]
ExternalTools/<tool>/<工具名>[.exe]             ← 旧版单平台布局
```

要点：

- **便携 PostgreSQL 必须整树分发**。`bin/`、`lib/`、`share/` 的相对位置不能拆，只拷 exe 会 `0xC0000135`（找不到 DLL）；`share/extension/` 里少了 `vector.control` 与 `vector--0.8.7.sql`，`CREATE EXTENSION vector` 就会失败。
- **安装路径含中文是可以的，但数据目录不行**。Windows 下 `initdb` 按 ANSI 代码页拼命令行，非 ASCII 程序路径会直接 `FATAL: invalid byte sequence for encoding "UTF8"`；所以程序目录含中文时，API 会先把 `ExternalTools/pgSQL/<rid>/` 镜像到 `{便携根目录}\pgsql`（首次约 65 MB，之后按 `VERSION.txt` 跳过）再启动。数据目录（`pgdata`）本身仍是**纯 ASCII 硬要求**，选根目录时会校验并给出改法。落点默认 `%LOCALAPPDATA%\MyFirstMafAgentData`，可用 `PortablePostgres:RootDirectory` 指定。
- **ffmpeg 不需要改名**，上游原始文件名丢进 `<rid>/` 就能被识别。
- **配置写死也不怕**。`PortablePostgres:BinDirectory` / `Media:FfmpegPath` 留空是推荐做法；
  即使填了一个在当前平台不存在的路径（典型：把 Windows 的 appsettings 带到 Linux），
  也会回落到自动定位，而不是启动失败。
- 都找不到时才报错，错误信息给出配置值、两条自动定位路径以及应放的文件名。
- Linux/macOS 会**自动补 `+x` 位**（复制文件会丢权限）；但只读挂载或特殊文件系统仍需手动 `chmod`。

`ExternalTools/pgSQL/<rid>/` 下另有 `VERSION.txt`（记录 PostgreSQL / pgvector 版本与构建时间）与 `THIRD-PARTY-NOTICES.txt`（发行件许可）。

> ⚠️ 非 Windows 平台的便携库尚未装配：需要那个平台的 PostgreSQL 17.x 发行件 + 从源码编译的 pgvector，按 `bin`/`lib`/`share` 同构摆进 `ExternalTools/pgSQL/<rid>/`。用 `scripts\Download-ExternalTools.ps1 -Target pgsql -PgSource <含 bin/lib/share 的目录>`（或 `-PgZip <zip>`）可一次性摆好；不给源目录时脚本只跳过并提示，不报错。目录留空不影响构建，只影响「自带数据库」这条。

## 5. 发布命令

```powershell
# Windows 控制台 + Electron sidecar（历史行为，输出 publish/console 与 publish/desktop）
powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target all -Runtime win-x64

# Linux 控制台包（输出 publish/console-linux-x64）
powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime linux-x64

# macOS Apple Silicon（输出 publish/console-osx-arm64）
powershell -ExecutionPolicy Bypass -File scripts\Publish-Portable.ps1 -Target console -Runtime osx-arm64

# 想连其它平台的二进制一起带走（体积 +100~300 MB）
... -KeepForeignRids
```

发布时会**自动删掉其它平台的原生二进制**（默认），避免一个 Linux 包里带着 Windows 的 exe。

自包含 vs 框架依赖：脚本默认 `-SelfContained $true`（目标机器不需要装 .NET）。
容器里用框架依赖（运行镜像已有运行时），见下一节。

## 6. Docker

```bash
docker compose up -d --build
# 浏览器打开 http://localhost:5056
```

`docker-compose.yml` 组成：

- `db`：`pgvector/pgvector:pg18`（若该 tag 不存在，换 `pg17`/`pg16`，只依赖 `vector` 扩展）
- `api`：本仓库 `Dockerfile` 构建，`ConnectionStrings__KnowledgeBase` 指向 `db`

镜像刻意做了这几件事：

| 决定 | 原因 |
| --- | --- |
| 不含 Electron | 容器里没有渲染需求 |
| 不打包便携 PostgreSQL | 用外部 PostgreSQL，`PortablePostgres__Enabled=false` |
| ffmpeg 用 `apt-get install` | 不随镜像塞 100 MB 副本；程序找不到随包副本时会回落到 PATH |
| `CMD ["--urls","http://0.0.0.0:5056"]` | **appsettings.json 里的 `Urls` 是 127.0.0.1，会盖掉 `ASPNETCORE_URLS`**，只有命令行 `--urls` 能覆盖 |
| `VOLUME /app/App_Data` | 上传资料、日志、Whisper 模型、插件目录都在里面 |
| 非 root（`app` 用户） | 最小权限 |
| `HEALTHCHECK` 打 `/api/health` | 它会真的查一次数据库，比只看端口有意义 |

镜像里**没有**的东西（按需自行挂载或加卷）：Whisper 模型、插件 ZIP、用户数据。

### 6.1 对外暴露前必须配的三项

`docker-compose.yml` 默认把端口绑在 `127.0.0.1`，因为容器里的 `/api` **默认不鉴权**（它原本只服务于 Electron 窗口）。
要让局域网/公网访问，三件事缺一不可：

| 项 | 不配的后果 |
| --- | --- |
| `MAF_SERVER_TOKEN`（或反代注入 `X-Api-Token`） | 扫到端口即可读写全部资料、问答记录，甚至读改 LLM 密钥 |
| `Server__TrustForwardedHeaders=true` | 审计来源 IP 全变 `127.0.0.1`（无法溯源）；且 `/api/desktop/shutdown` 的回环校验被代理骗过，**公网可关机** |
| 反向代理上 TLS | MCP 客户端密钥是 Bearer 型凭据，明文 HTTP 上等于公开 |

完整步骤（含 nginx / Caddy 配置示例、Docker / systemd / Windows 三种形态）见 [`CLOUD-DEPLOYMENT.md`](CLOUD-DEPLOYMENT.md)。

## 7. 桌面版自动更新（P0-3）

| 项 | 说明 |
| --- | --- |
| 组件 | `electron-updater` + `desktop-electron/updater.js` |
| 检查时机 | 启动后 20 秒检查一次，之后每 6 小时一次；也可在「帮助 → 检查更新」或「设置 → 桌面版更新」手动触发 |
| 发现新版本 | 自动后台下载（`autoDownload=false` + 手动 `downloadUpdate`），下载完提示「重启并安装」 |
| 安装顺序 | **先停 API（连带停便携库）→ 再 `quitAndInstall`**，否则 `postgres.exe` 会锁住安装目录 |
| 更新源优先级 | `MAF_UPDATE_URL` 环境变量 → 用户数据目录 `update-feed.json` 的 `{ "url": ... }` → 安装包内 `app-update.yml` |
| 发布 | `cd desktop-electron; npm run dist:publish`（`--publish always`），或手工把 `publish/releases` 里的 `latest.yml` + 安装包 + `.blockmap` 放到静态目录 |
| 测试 | `cd desktop-electron; npm run test:updater`（8 个用例，验证「先停 API 再装」的顺序） |

**为什么不能用 `autoInstallOnAppQuit`**：本应用的 `before-quit` 会 `preventDefault()` 后走 `app.exit(0)`，那条路径根本不会执行安装器。所以只在用户确认后显式安装。

## 8. 已验证 / 未验证（诚实清单）

**已验证**

- `ExternalTools/pgSQL/win-x64/` 整树落地：`bin`（`postgres`/`pg_ctl`/`initdb`/`pg_isready`/`psql` 及运行时 DLL）、`lib`（含 `vector.dll`）、`share/extension`（`vector.control` 等）齐备，`VERSION.txt` 记 PostgreSQL 17.9 + pgvector 0.8.7
- **新便携库形态的端到端启动（真机、非模拟）**：首次运行 `initdb` → `pg_ctl start` → 建库 → `CREATE EXTENSION vector` → 退出码 0，`GET /api/health` 返回 `{"status":"ok","database":"connected"}`；`SchemaInitializer` 正常建表、索引生命周期管理器完成 embedding spec 校验
- **复用已在跑的实例**：第二个 API 进程读到 `postmaster.pid` 后直接复用 55432，日志 `检测到已在运行的 PostgreSQL（端口 55432，数据目录 …），直接复用。`，且它退出时**不会**停掉别人启动的库
- **卸载钩子**：`MyFirstMafAgent.Api.exe --stop-portable-postgres` 把运行中的便携库停干净（12 个 `postgres.exe` → 0、55432 释放），日志 `[installer] 便携 PostgreSQL 已停止（数据目录 …）。`
- **非 ASCII 安装路径**：安装目录在 `C:\Word\通用文件服务\...` 下时镜像到纯 ASCII 根目录后成功启动（未镜像时 `initdb` 报 `invalid byte sequence for encoding "UTF8"`）
- 全量单元测试 844 项通过（含新增 `PgProcessRunnerTests` / `PlatformRuntimeTests` 的目录解析用例 / `PortablePostgresRootTests`）
- `linux-x64` 发布成功，产出 ELF 启动器 + 清理掉 12 项其它平台文件；缺 ffmpeg 只警告
- Dockerfile 里的发布命令（`-p:UseAppHost=false`）与入口 `dotnet MyFirstMafAgent.Api.dll --urls ...` 在 Windows 上等价跑通
- `electron-builder.yml` 通过 schema 校验，`publish` 配置可解析
- 更新逻辑 8 个用例全绿

**未验证（不要当成已完成）**

- `docker build` / `docker compose up` 未在本机执行（本机没有 Docker）
- Linux / macOS 上的真实运行（没有对应机器；那些平台的便携库发行件尚未装配）
- Electron 安装包重新构建（本机访问 GitHub 拉 `winCodeSign` 超时；上一阶段的 0.1.0 安装包是当时构建的）
- 自动更新的端到端升级（需要两个版本的安装包 + 可达的更新源）
