# 跨平台部署（Windows / Linux / macOS / Docker）

> 这份文档是**部署口径的唯一来源**。改打包方式、加平台、动原生工具目录，先读这里。

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
| 便携 PostgreSQL（pg0，内置 PG 18 + pgvector） | `ExternalTools/pgSQL/<rid>/` | **按平台**，可留空（容器里用外部 PG） |
| Embedding 模型（bge-small-zh-v1.5, 97 MB） | `ExternalTools/Embedding/bge-small-zh-v1.5/` | 是（与平台无关，ONNX） |
| ffmpeg | `ExternalTools/ffmpeg/<rid>/` 或 PATH | 可选（只有视频需要） |

Whisper 模型（141 MB 起）**不随包分发**，运行时可下载或从设置页导入。

## 3. 支持的平台矩阵

| RID | 便携数据库 | ffmpeg | 现状 |
| --- | --- | --- | --- |
| `win-x64` | ✅ `pg0-windows-x86_64.exe` | ✅ 随包 | **已验证**（安装包 + 真实上传链路） |
| `linux-x64` | 需补 `pg0-linux-x86_64-gnu` | 建议 apt 安装 | **已验证可发布**（`dotnet publish -r linux-x64` 产出正常）；容器镜像尚未在本机 `docker build` 验证 |
| `linux-arm64` | 需补 `pg0-linux-aarch64-gnu` | 建议包管理器 | 同上 |
| `osx-arm64` | 需补 `pg0-darwin-aarch64` | 建议 `brew` | 未验证（无 macOS 机器） |
| `osx-x64` | 需补 `pg0-darwin-x86_64` | 建议 `brew` | 未验证 |
| `win-arm64` | ❌ 上游无发行包 | — | 仅框架可跑，便携库不可用 |

## 4. 原生工具怎么放（自动查找规则）

`PlatformRuntime.ResolveTool` 按当前 RID 依次尝试（**先命中先用**）：

```
ExternalTools/<tool>/<rid>/<上游原始文件名>     ← 推荐
ExternalTools/<tool>/<rid>/<工具名>[.exe]
ExternalTools/<tool>/<上游原始文件名>
ExternalTools/<tool>/<工具名>-<rid>[.exe]
ExternalTools/<tool>/<工具名>[.exe]             ← 旧版单平台布局
```

要点：

- **不需要改名**。pg0 官方文件名直接丢进 `<rid>/` 就能被识别。
- **配置写死也不怕**。`PortablePostgres:ExecutablePath` / `Media:FfmpegPath` 留空是推荐做法；
  即使填了一个在当前平台不存在的路径（典型：把 Windows 的 appsettings 带到 Linux），
  也会回落到自动定位，而不是启动失败。
- 两者都找不到时才报错，且错误信息会**列出全部候选路径 + 下载地址 + 应放的文件名**。
- Linux/macOS 会**自动补 `+x` 位**（复制文件会丢权限）；但只读挂载或特殊文件系统仍需手动 `chmod`。

下载（需要能访问 GitHub）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Runtime linux-x64 -Target all
```

> ⚠️ 本机实测：从 `github.com` 的 release 下载会超时（`curl: (28)`），
> 所以 `ExternalTools/pgSQL/{linux,osx}-*/` 下的 README 里放了**逐条下载命令**，
> 你可以在能联网的机器上下载后放回来。目录留空不影响构建，只影响「自带数据库」这条。

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
| 不打包 pg0 | 用外部 PostgreSQL，`PortablePostgres__Enabled=false` |
| ffmpeg 用 `apt-get install` | 不随镜像塞 100 MB 副本；程序找不到随包副本时会回落到 PATH |
| `CMD ["--urls","http://0.0.0.0:5056"]` | **appsettings.json 里的 `Urls` 是 127.0.0.1，会盖掉 `ASPNETCORE_URLS`**，只有命令行 `--urls` 能覆盖 |
| `VOLUME /app/App_Data` | 上传资料、日志、Whisper 模型、插件目录都在里面 |
| 非 root（`app` 用户） | 最小权限 |
| `HEALTHCHECK` 打 `/api/health` | 它会真的查一次数据库，比只看端口有意义 |

镜像里**没有**的东西（按需自行挂载或加卷）：Whisper 模型、插件 ZIP、用户数据。

## 7. 桌面版自动更新（P0-3）

| 项 | 说明 |
| --- | --- |
| 组件 | `electron-updater` + `desktop-electron/updater.js` |
| 检查时机 | 启动后 20 秒检查一次，之后每 6 小时一次；也可在「帮助 → 检查更新」或「设置 → 桌面版更新」手动触发 |
| 发现新版本 | 自动后台下载（`autoDownload=false` + 手动 `downloadUpdate`），下载完提示「重启并安装」 |
| 安装顺序 | **先停 API（连带 pg0）→ 再 `quitAndInstall`**，否则 `postgres.exe` 会锁住安装目录 |
| 更新源优先级 | `MAF_UPDATE_URL` 环境变量 → 用户数据目录 `update-feed.json` 的 `{ "url": ... }` → 安装包内 `app-update.yml` |
| 发布 | `cd desktop-electron; npm run dist:publish`（`--publish always`），或手工把 `publish/releases` 里的 `latest.yml` + 安装包 + `.blockmap` 放到静态目录 |
| 测试 | `cd desktop-electron; npm run test:updater`（8 个用例，验证「先停 API 再装」的顺序） |

**为什么不能用 `autoInstallOnAppQuit`**：本应用的 `before-quit` 会 `preventDefault()` 后走 `app.exit(0)`，那条路径根本不会执行安装器。所以只在用户确认后显式安装。

## 8. 已验证 / 未验证（诚实清单）

**已验证**

- `win-x64` 控制台包启动 → `/api/health` 200，pg0 从旧版扁平路径被自动识别并缓存为 `pg0-win-x64.exe`
- 删掉 pg0 后启动 → 报错列出 5 条候选路径 + 下载地址 + 目标文件名（可操作）
- `linux-x64` 发布成功，产出 ELF 启动器 + 清理掉 12 项其它平台文件；缺 pg0/ffmpeg 只警告
- Dockerfile 里的发布命令（`-p:UseAppHost=false`）与入口 `dotnet MyFirstMafAgent.Api.dll --urls ...` 在 Windows 上等价跑通
- `electron-builder.yml` 通过 schema 校验，`publish` 配置可解析
- 更新逻辑 8 个用例全绿

**未验证（不要当成已完成）**

- `docker build` / `docker compose up` 未在本机执行（本机没有 Docker）
- Linux / macOS 上的真实运行（没有对应机器；pg0 二进制也因网络受限未下载）
- Electron 安装包重新构建（本机访问 GitHub 拉 `winCodeSign` 超时；上一阶段的 0.1.0 安装包是当时构建的）
- 自动更新的端到端升级（需要两个版本的安装包 + 可达的更新源）
