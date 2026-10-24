# syntax=docker/dockerfile:1
#
# 知间（MyFirstMafAgent）后端镜像
#
# 设计要点（踩过的坑，改之前先看）：
#
# 1. 镜像里只跑 API + 前端静态文件，**不含 Electron**。Electron 只负责桌面渲染，
#    在容器里没有任何意义。
# 2. **不打包便携数据库（pg0）**。容器用外部 PostgreSQL（见 docker-compose.yml 的
#    pgvector 服务），通过 ConnectionStrings__KnowledgeBase 接入；
#    PortablePostgres__Enabled=false 让便携数据库彻底不启动。
# 3. **ffmpeg 用 apt 装**，不随包分发 100 MB 副本。程序找不到
#    ExternalTools/ffmpeg/<rid>/ffmpeg 时会回落到 PATH 里的 ffmpeg。
# 4. 监听地址必须用命令行 `--urls` 覆盖：appsettings.json 里的 `Urls`
#    是 127.0.0.1（本机桌面用），在容器里会把端口映射挡死。
#    （实测：ASPNETCORE_URLS 会被 appsettings 里同名的 Urls 覆盖，只有 --urls 生效。）
# 5. 数据目录 /app/App_Data 挂卷：上传的资料、日志、Whisper 模型都在里面，
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

# 框架依赖发布：运行镜像自带 ASP.NET Core 运行时，镜像更小、构建更快。
RUN dotnet publish MyFirstMafAgent.Api/MyFirstMafAgent.Api.csproj \
      -c Release \
      -o /app/publish \
      --nologo \
      -p:UseAppHost=false

# 清掉 Windows/macOS 的原生二进制：容器里用不到，白白多出 200~300 MB。
RUN rm -rf /app/publish/ExternalTools/pgSQL/win-* \
           /app/publish/ExternalTools/pgSQL/osx-* \
           /app/publish/ExternalTools/ffmpeg/win-* \
           /app/publish/ExternalTools/ffmpeg/osx-* \
    && find /app/publish/ExternalTools -maxdepth 2 -type f -name '*.exe' -delete \
    && find /app/publish/ExternalTools -maxdepth 2 -type f -name '*.pdb' -delete

# ---------- 3. 运行时 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg：视频转写/关键帧用；curl：给 HEALTHCHECK 用。
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/publish ./
COPY --from=frontend /src/dist ./wwwroot

# 数据目录（上传文件、日志、Whisper 模型）。首次启动会自动创建子目录。
RUN mkdir -p /app/App_Data/uploads /app/App_Data/models /app/App_Data/logs \
    && chown -R app:app /app/App_Data

ENV ASPNETCORE_ENVIRONMENT=Production \
    Desktop__OpenBrowser=false \
    PortablePostgres__Enabled=false \
    Media__AutoDownloadWhisperModel=true \
    Storage__RootPath=App_Data/uploads \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# 非 root 运行（aspnet:10.0 内置 app 用户）。
USER app

EXPOSE 5056

# 探活走 /api/health（它会真的查一次数据库，比只看端口有意义）。
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
    CMD curl -fsS http://127.0.0.1:5056/api/health || exit 1

# --urls 覆盖 appsettings.json 里的 127.0.0.1，否则容器外访问不到。
ENTRYPOINT ["dotnet", "MyFirstMafAgent.Api.dll"]
CMD ["--urls", "http://0.0.0.0:5056"]
