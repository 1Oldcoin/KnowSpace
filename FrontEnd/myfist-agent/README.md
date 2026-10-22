# 知间 · 本地学习知识库

Vue 3 + TypeScript + Vite 前端工程，提供概览、资料、专题、学习笔记、原文阅读、知识库问答、AI 伴侣和设置视图。

## 启动

1. 启动 `MyFirstMafAgent.Api`，默认地址为 `http://localhost:5056`。
2. 在此目录安装依赖并启动开发服务器：

```sh
npm install
npm run dev
```

3. 浏览器访问 `http://localhost:5173`。

若 API 地址不同，复制 `.env.example` 为 `.env.local` 并设置 `VITE_API_BASE_URL`。本地 CORS 配置需允许前端来源 `http://localhost:5173`。

## API 集成范围

- `GET /api/health`：显示后端连接状态。
- `GET /api/knowledge-bases`、`POST /api/knowledge-bases`：查询和创建知识库。
- `GET /api/documents`、`GET /api/documents/{id}`：查询资料、摘要和处理进度。
- `POST /api/documents`：兼容的小文件上传；大文件使用 `/api/uploads` 创建可续传会话、`PUT /api/uploads/{id}/chunks?offset={bytes}` 上传分块、`POST /api/uploads/{id}/complete` 完成上传。
- 支持格式：PDF、DOCX、TXT、MD、HTML/HTM、PNG/JPG/JPEG/WEBP/GIF 和 MP4/MOV/MKV/WEBM/AVI。HTML/HTM 由 API 抽取正文（标题、列表、表格）后再切分并写入向量索引；前端会把 HTML 资料单独标记为“网页”。
- `POST /api/documents/{id}/retry`：重新解析 `Failed` 或 `Ready` 状态的资料。
- `GET /api/documents/{id}/download`：下载原始文件。
- `DELETE /api/documents/{id}`：删除资料、其分块并清理本地原始文件。
- `POST /api/search`、`POST /api/chat`：pgvector 检索与带引用知识库问答。两个问答接口都接受可选的单轮工具开关 `enableToolCalling` / `enableWebSearch` / `enableFileAccess`（不传则沿用设置页偏好）。
- “AI 知识问答”页底部有工具开关：关闭总开关时只用知识库资料回答；开启后可再单独选择联网搜索与本地文件检索。选择结果保存在浏览器本地。
- `/api/knowledge-bases/{knowledgeBaseId}/sessions`：`GET` 列出历史、`POST` 新建会话；`GET /{sessionId}/messages` 读取消息，`POST /{sessionId}/chat` 继续对话。
- `PUT /api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}`：重命名会话。
- `DELETE /api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}`：删除会话及其消息。
- `POST /api/knowledge-bases/{knowledgeBaseId}/sessions/batch-delete`：批量删除（JSON：`{"sessionIds":["..."]}`，单次最多 100 条）。
- `GET /api/knowledge-bases/{knowledgeBaseId}/sessions/{sessionId}/export`：下载 Markdown 对话记录。
- `/api/knowledge-bases/{knowledgeBaseId}/topics`：`GET` 列出专题（含资料数量）、`POST` 创建专题；`PUT` / `DELETE /{topicId}` 重命名或删除专题。
- `PUT /api/documents/{documentId}/topic`：把资料移入专题或移回未分类。
- `GET /api/documents/{documentId}/content?offset=&length=`：分页读取已解析原文，供原文阅读器使用。
- `/api/knowledge-bases/{knowledgeBaseId}/notes`：`GET` 列出（支持 `query`、`category`）、`POST` 新建笔记；`GET/PUT/DELETE /{noteId}` 读取、更新、删除；`POST /{noteId}/pin` 置顶切换。
- `GET` / `PUT /api/settings`：读取/保存聊天模型、显示名称和每日学习目标；API Key 仅返回掩码提示，保存后立即生效。
- `GET` / `POST /api/study/checkins`：读取连续学习、累计天数和本周节奏；记录/更新今日学习时长与备注。`DELETE /api/study/checkins/today` 可撤销今日打卡。
- `/api/companions/*`：导入聊天记录、生成人物风格画像、保存陪伴会话与消息。

## API 路由查找与调试

路由按功能拆分在 `MyFirstMafAgent.Api/Endpoints/`（Health、Knowledge、Companion），不再集中在 `Program.cs`。开发环境访问 `/scalar/v1` 可浏览和调试接口，OpenAPI JSON 地址为 `/openapi/v1.json`。

## 大视频处理

- 视频最大 `5 GiB`（配置为 `5 × 1024³` 字节），普通文件最大 `50 MiB`；分块默认 `16 MiB`。前端使用分块上传，网络中断后重新选择同一文件可从服务器已接收的位置继续。
- 上传完成后会返回资料记录并进入后台队列；可在资料列表查看排队、处理进度、摘要或失败原因，失败项可重试。视频处理提取音轨、由 Whisper 转写、调用配置的 LLM 汇总，再把摘要和带时间戳的文本切分并写入向量索引供检索/问答使用。
- FFmpeg 使用 `MyFirstMafAgent.Api/ExternalTools/ffmpeg/ffmpeg.exe`；仓库当前已有该文件。Embedding 使用 `MyFirstMafAgent.Api/ExternalTools/Embedding/bge-small-zh-v1.5/model.onnx` 和 `tokenizer.json`；仓库当前已有这两个文件，无需另建 Embedding 服务。
- Whisper 默认使用 `Base` 模型，首次处理视频时尝试自动下载到 `MyFirstMafAgent.Api/App_Data/models/ggml-base.bin`。当前工作区尚无此模型文件；首次下载需要网络。如果 API 所在环境不能访问下载源，请手动获取 Whisper.net 对应的 GGML `base` 模型并保存到该路径，或在 `Media:WhisperModel` 与 `Media:WhisperModelPath` 中改为已准备的模型及路径。不要把 `.dll` 运行时文件当作模型文件。
- 仅转写和摘要无需视觉模型。视频关键帧分析默认关闭（`Media:EnableVideoFrameAnalysis=false`）；启用后还需配置支持图像输入的 OpenAI-compatible 模型端点，当前默认的 `deepseek-chat` 端点不保证支持视觉输入。关键帧采样间隔和上限可用 `Media:FrameSampleIntervalSeconds`、`Media:MaxVideoFrames` 配置。
- 大视频的实际处理耗时取决于视频时长、机器性能和 LLM 服务。API、PostgreSQL 与本地上传目录必须保持可用且有足够磁盘空间；此功能不是 5 GiB 单次 HTTP 请求。

图片处理要求当前聊天模型支持视觉输入。学习笔记已通过 `/api/knowledge-bases/{knowledgeBaseId}/notes` 持久化到 PostgreSQL。

AI 伴侣当前接受 UTF-8 通用 JSON/TXT 导出文件，不直接读取微信/QQ 私有或加密数据库；不同平台的官方导出格式可能需要转换。聊天记录和会话存入 PostgreSQL。设置 `Llm:UseCloudApi=true` 时，画像生成及聊天相关内容会发往配置的云 API；`false` 使用本地模型端点。该功能生成的是 AI 模拟，不是聊天对象本人，请只导入有权处理的数据。

## 构建与类型检查

```sh
npm run build
```
