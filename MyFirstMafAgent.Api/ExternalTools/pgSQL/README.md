# 便携 PostgreSQL（随包分发）

> **Linux / macOS 交付包里只有这个文件，没有任何二进制。**
> 不是打包时漏了：Linux 上 PostgreSQL 由系统包提供（`install.sh` 会 apt 装
> `postgresql-17` + `postgresql-17-pgvector`，并把 `PortablePostgres__BinDirectory` 指到
> `/usr/lib/postgresql/17/bin`）；Docker 镜像同理。
> 原因有两个，都是硬的：
> 1. **ABI 绑定发行版**。同一份 `x86_64-unknown-linux-gnu` 构建在 Debian 12 能跑，到
>    Ubuntu 24.04 会因为缺 `libicu72.so.72` 直接加载失败（它是 `libicu74`）。
> 2. **官方二进制不含 pgvector**。缺 `lib/vector.so` 与 `share/extension/vector.control` 时
>    `CREATE EXTENSION vector` 会抛异常，而 `PortablePostgresHost.EnableVectorExtensionAsync`
>    把它当成致命错误（知识库检索强依赖向量索引）—— 启动直接失败，而不是降级。
>
> 所以 Linux 上如果**不经 `install.sh` 直接跑** `./Zhijian.Api`，必须手工给
> `PortablePostgres__BinDirectory` 与 `PortablePostgres__RootDirectory`，
> 或者 `PortablePostgres__Enabled=false` + `ConnectionStrings__KnowledgeBase` 改用外部库。

这个目录**不是**源码，是随应用分发的 PostgreSQL 二进制。`PortablePostgresHost` 在
`ConnectionStrings:KnowledgeBase` 留空时用它启动本地数据库：`initdb` 建集群 → `pg_ctl start`
→ 建库 → `CREATE EXTENSION vector`；应用退出时 `pg_ctl stop -m fast`。**数据库的整个生命周期
由 .NET API 负责**，Electron 壳只拉起 API，不知道数据库存在。

## 目录结构

```
pgSQL/
└─ win-x64/                      ← 按 RID 分平台，可以多个并存
   ├─ bin/                       ← postgres.exe / pg_ctl.exe / initdb.exe / pg_isready.exe / psql.exe + 运行时 DLL
   ├─ lib/                       ← PostgreSQL 库与扩展共享库（含 vector.dll）
   ├─ share/                     ← 时区、字符集、share/extension/（含 vector.control、vector--0.8.7.sql）
   ├─ VERSION.txt                ← PostgreSQL / pgvector 版本与构建时间（镜像流程用它判断是否需要刷新）
   └─ THIRD-PARTY-NOTICES.txt    ← 发行件许可声明
```

当前装配：**PostgreSQL 17.9（Windows x64，EDB 发行件）+ pgvector 0.8.7（MSVC 从源码编译）**。

扁平布局 `pgSQL/bin/`（把 `dist/pgsql` 整个拷进来）也被识别，只是多个平台并存时必须用
`<rid>/`。解析顺序：`PortablePostgres:BinDirectory` → `pgSQL/<rid>/bin` → `pgSQL/bin`。

## 三块不能拆

`bin`、`lib`、`share` 之间的相对位置是编译进 `postgres.exe` 的。只拷 `bin` 会得到
`0xC0000135`（找不到 DLL）；少了 `share/extension/vector.control` 与
`vector--0.8.7.sql`，`CREATE EXTENSION vector` 会报 `extension "vector" is not available`。
也就是说：**不要手工挑文件，整棵树一起搬**。

## 非 ASCII 安装路径会被镜像

Windows 下 `initdb` 把程序路径按 ANSI 代码页拼进启动 `postgres.exe` 的命令行，程序路径含中文时
直接失败：

```
FATAL:  invalid byte sequence for encoding "UTF8": 0xd3 0xc3
```

（就是 `C:\Word\通用文件服务\...` 里的“用”，实测必挂；同一份文件搬到 `C:\pgspike\pgsql` 立刻成功。）
所以 `PortablePostgresHost.EnsureRunnableBinDirectoryAsync` 会在**程序路径含非 ASCII 字符**时，
先把本目录镜像到 `{便携根目录}\pgsql`（首次约 65 MB，之后按 `VERSION.txt` 指纹跳过），再从镜像里
跑 `initdb`/`pg_ctl`。顺带的好处是：非 ASCII 安装目录下 `postgres.exe` 不会锁住安装目录。

数据目录（`pgdata`）反过来是**纯 ASCII 硬要求**，`EnsureAsciiDataPath` 会直接校验并报错——
这条规则另有用途：数据不跟着程序目录走，卸载/覆盖安装才不会连带删数据。

## 换成别的版本（例如把 17.9 升到 17.10，或补其它平台）

1. 拿到目标平台的**整套**发行件（PostgreSQL 主版本必须与数据目录一致；跨主版本要连数据目录一起换）。
2. 放到 `pgSQL/<rid>/`，保持 `bin`/`lib`/`share` 结构；pgvector 用同一套工具链编译后把
   `vector.dll` 放进 `lib/`、`vector.control` 与 `vector--*.sql` 放进 `share/extension/`。
3. 更新 `VERSION.txt`：镜像流程靠它判断要不要重新复制，内容变了才会刷新副本。
4. 删掉旧的 `pgdata`（或另建数据目录）：`initdb` 生成的集群版本与 `bin` 不匹配时，
   `pg_ctl start` 会拒绝启动。
5. 跑一遍 `GET /api/health` 确认 `{"status":"ok","database":"connected"}`。

也可以用脚本摆放：`powershell -ExecutionPolicy Bypass -File scripts\Download-ExternalTools.ps1 -Target pgsql -PgSource <含 bin/lib/share 的目录> -Runtime win-x64`；
或者 `-PgZip <zip>` 直接给压缩包。脚本会用 PE/ELF/Mach-O 魔数确认放进去的是真二进制，
避免误删同名文本文件。

## 启动 PostgreSQL 时踩过的坑（改代码前先读）

| 现象 | 原因 | 做法 |
| --- | --- | --- |
| `pg_ctl start` 迟迟不返回（>90 秒），但日志已写 `database system is ready to accept connections` | `postgres.exe` 继承了 `pg_ctl` 的 stdout/stderr，父进程等不到管道 EOF | **绝不要**把 `pg_ctl` 输出接到管道：`PgProcessRunner` 用 `cmd /d /s /c`（Unix 用 `/bin/sh -c`）把输出重定向到**文件** |
| 日志里 `could not open file "./log/ctl.out": sharing violation` | `pg_ctl -l` 的日志写进了 `PGDATA`，崩溃恢复时的 fsync 撞上这个句柄 | `-l` 指向 `{便携根目录}\pglogs\postgres.log`，与 `pgdata` 平级；`pg_ctl` 自己的输出另用 `pgctl-start.out` |
| 端口被占时报 `Permission denied` 而不是 `address already in use` | Windows 上 PostgreSQL 用 `SO_EXCLUSIVEADDRUSE`，冲突表现为拒绝访问 | 先按 `postmaster.pid` 复用已有实例，否则 `FindAvailablePort` 逐个试；不要靠错误文案判断端口冲突 |
| `initdb` 抱怨 `invalid byte sequence` | 程序路径非 ASCII（见上一节） | 镜像到纯英文目录后再执行 |
| 以管理员身份运行时 `initdb`/`postgres` 直接拒绝 | PostgreSQL 不允许提权令牌 | `EnsureNotElevated` 在启动前拦下并给出提示 |
| 升级/卸载时文件删不掉 | `postgres.exe` 占住安装目录（ASCII 安装路径下） | `Zhijian.Api.exe --stop-portable-postgres` 由安装器在删文件前调用 |

## 和旧方案的区别

这里以前放的是 **pg0**（vectorize-io/pg0 的单文件 exe，靠自解压把 PostgreSQL 18 释放到
`%USERPROFILE%\.pg0`）。单文件的好处只是一个文件，代价是版本不透明、扩展要联网下载、每次运行都要
解包。现在改为直接分发 PostgreSQL 发行件：版本写在 `VERSION.txt`、pgvector 随包、启动不再解压。
遗留的 `%USERPROFILE%\.pg0` 目录可以手工删除（应用不再读它；旧集群版本是 18，17.9 既读不了也不需要）。

## 许可证

PostgreSQL 采用 PostgreSQL License（类 BSD），pgvector 采用 PostgreSQL License。
随包分发时保留 `win-x64/THIRD-PARTY-NOTICES.txt`。