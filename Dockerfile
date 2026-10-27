# syntax=docker/dockerfile:1
#
# 知间（MyFirstMafAgent）后端镜像 —— **一体模式**
#
# 一个容器里跑：ASP.NET Core API + 前端静态文件 + PostgreSQL 17 + pgvector。
# 数据库由 API 进程自己拉起（initdb → pg_ctl start），数据落在 /app/App_Data 卷里。
# 想改回「API + 独立数据库」：docker compose --profile external up，
# 或只改两个环境变量（PortablePostgres__Enabled=false + ConnectionStrings__KnowledgeBase）。
#
# 这里记下几个踩过的坑：
#
# 1. 镜像里只跑 API + 前端，**不含 Electron**。Electron 只负责桌面渲染，在容器里毫无意义。
#
# 2. **PostgreSQL 用 apt 装，不随包分发便携树。** 便携树只有 win-x64 一份（65 MB），
#    Linux 上还得自己编 pgvector；apt 的 postgresql-17-pgvector 把 vector.so 装进
#    /usr/lib/postgresql/17/lib/，正好是那套 bin 的默认 $libdir，CREATE EXTENSION 零配置。
#
# 3. **必须装 17，不是 18。** 便携库的数据目录大版本号在代码里硬编码为 17
#    （PortablePostgresHost.ExpectedMajorVersion）。混用会让已有数据目录被判为不兼容。
#
# 4. **ffmpeg 用 apt 装**，不随包分发 100~300 MB 副本。程序找不到
#    ExternalTools/ffmpeg/<rid>/ffmpeg 时会回落到 PATH 里的 ffmpeg。
#
# 5. **policy-rc.d 挡掉装包时的服务自启。** 容器里没有 systemd，postgresql-common 的
#    postinst 会尝试 start 集群，失败会让 RUN 直接报错。
#
# 6. 监听地址必须用命令行的 `--urls` 覆盖：appsettings.json 里的 `Urls` 是 127.0.0.1，
#    在容器里会把端口映射顶死。（实测：ASPNETCORE_URLS 会被同名的配置项压掉，只有命令行参数生效。）
#
# 7. 数据目录 /app/App_Data 挂卷：上传的资料、日志、OAuth 签名私钥、**pgdata** 都在里面，
#    容器重建不丢。

# ---------- 1. 前端构建 ----------
FROM node:22-alpine AS frontend
WORKDIR /src
COPY FrontEnd/myfist-agent/package.json FrontEnd/myfist-agent/package-lock.json* ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund
COPY FrontEnd/myfist-agent/ ./
RUN npm run build

# ---------- 2. 后端发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只拷 csproj，让 restore 层能被缓存。
COPY MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj MyFirstMafAgent.Api/
COPY MyFirstMafAgent.Plugins.Sdk/MyFirstMafAgent.Plugins.Sdk.csproj MyFirstMafAgent.Plugins.Sdk/
RUN dotnet restore MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj

COPY MyFirstMafAgent.Api/ MyFirstMafAgent.Api/
COPY MyFirstMafAgent.Plugins.Sdk/ MyFirstMafAgent.Plugins.Sdk/
# csproj 里的 <ApplicationIcon>..\assets\app.ico</ApplicationIcon> 需要这个目录。
COPY assets/ assets/

# 框架依赖发布：运行镜像自带 ASP.NET Core 运行时，镜像更小、构建更快。
RUN dotnet publish MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj \
      -c Release \
      -o /app/publish \
      --nologo \
      -p:UseAppHost=false

# 清掉非 Linux 的原生二进制约 370 MB：pgSQL 全平台删（改用 apt）、
# ffmpeg 全平台删（改用 apt）、Embedding 模型**保留**（离线向量化靠它）。
RUN rm -rf /app/publish/ExternalTools/pgSQL \
           /app/publish/ExternalTools/ffmpeg \
    && find /app/publish/ExternalTools -maxdepth 3 -type f \( -name '*.exe' -o -name '*.pdb' \) -delete

# ---------- 3. 运行 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# 装包期间禁止服务自启（容器无 systemd）。
RUN printf '#!/bin/sh\nexit 101\n' > /usr/sbin/policy-rc.d && chmod +x /usr/sbin/policy-rc.d

# PostgreSQL 17 + pgvector 走 PGDG（Debian/Ubuntu 官方源里版本未必是 17）。
# VERSION_CODENAME 从 /etc/os-release 取，所以 bookworm / trixie / noble 都能用同一段。
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg ffmpeg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc \
         -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && . /etc/os-release \
    && test -n "${VERSION_CODENAME}" \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
         > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-17 postgresql-17-pgvector \
    && rm -rf /var/lib/apt/lists/* \
              /var/lib/postgresql/17/main \
              /var/log/postgresql \
    && rm -f /usr/sbin/policy-rc.d

WORKDIR /app

COPY --from=build /app/publish ./
COPY --from=frontend /src/dist ./wwwroot

# 自检脚本随镜像走：docker compose exec app bash /app/scripts/Test-IntegratedStack.sh
# （只依赖 curl，不装 jq。）
COPY scripts/Test-IntegratedStack.sh ./scripts/Test-IntegratedStack.sh
RUN chmod 0755 ./scripts/Test-IntegratedStack.sh

# 数据目录必须由 app 用户拥有：initdb 要在里面建集群，OAuth 私钥也要写在这里。
# 子目录名与 Windows 便携版保持一致（pgdata / pglogs / pgsockets 由程序自己创建）。
RUN mkdir -p /app/App_Data/uploads /app/App_Data/models /app/App_Data/logs \
    && chown -R app:app /app/App_Data

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    Desktop__OpenBrowser=false \
    Storage__RootPath=App_Data/uploads \
    Media__AutoDownloadWhisperModel=true \
    Server__AllowAnonymousHealth=true \
    PortablePostgres__Enabled=true \
    PortablePostgres__BinDirectory=/usr/lib/postgresql/17/bin \
    PortablePostgres__RootDirectory=/app/App_Data \
    PortablePostgres__Port=55432 \
    PortablePostgres__StopOnExit=true

# 非 root 运行：postgres / initdb 明确拒绝 root，aspnet:10.0 内置的 app 用户正好。
USER app

EXPOSE 5056

# 探活走 /api/health（它会真查一次数据库，比只看端口有意义）。
# 上面已开 Server__AllowAnonymousHealth，所以这里不需要令牌。
# start-period 给足：首次启动要 initdb（3~10 秒）再跑完整建表。
HEALTHCHECK --interval=15s --timeout=5s --start-period=90s --retries=10 \
    CMD curl -fsS http://127.0.0.1:5056/api/health || exit 1

ENTRYPOINT ["dotnet", "Zhijian.Api.dll"]
CMD ["--urls", "http://0.0.0.0:5056"]
