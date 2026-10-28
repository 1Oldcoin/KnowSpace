// 默认同源：发布后前端由内嵌 Kestrel 直接托管；Vite 开发服务器通过 vite.config.ts 的 /api 代理转发。
// 如需连接其他主机，可设置 VITE_API_BASE_URL（例如 http://192.168.1.10:5056）。
const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')
/** 给需要自己拼 URL 的组件（视频播放等）用，避免各自重复读一次环境变量。 */
export { apiBaseUrl }

export interface UploadResult {
  id: string
  fileName: string
  extension: string
  sizeBytes: number
  status: string
  error?: string
}

export interface KnowledgeBase {
  id: string
  name: string
  createdAt: string
  /** 默认知识库不可删除：未指定知识库的上传与检索都以它为兜底。 */
  isDefault?: boolean
}

export interface KnowledgeChatSession {
  id: string
  knowledgeBaseId: string
  title: string
  createdAt: string
  updatedAt: string
}

export interface KnowledgeChatMessage {
  id: number | string
  role: 'user' | 'assistant'
  content: string
  createdAt: string
  citations?: Citation[] | null
  answerMeta?: KnowledgeChatAnswerMeta | null
}

export interface CompanionProfile {
  id: string
  name: string
  targetSender: string
  platform: string
  personaPrompt: string
  messageCount: number
  createdAt: string
}

export interface CompanionSession {
  id: string
  profileId: string
  title: string
  createdAt: string
}

export interface CompanionMessage {
  id: number | string
  role: 'user' | 'assistant'
  content: string
  createdAt: string
}

export interface DocumentResult {
  id: string
  fileName: string
  extension: string
  contentType: string
  sizeBytes: number
  status: string
  error?: string
  createdAt: string
  chunkCount: number
  summary?: string
  progress: number
  processingStage?: string
  topicId?: string | null
}

export interface Topic {
  id: string
  knowledgeBaseId: string
  name: string
  color: string
  createdAt: string
  documentCount: number
}

export interface StudyCheckinDay {
  date: string
  minutes: number
  note?: string | null
}

export interface StudySummary {
  todayCheckedIn: boolean
  currentStreak: number
  longestStreak: number
  totalDays: number
  todayMinutes: number
  thisWeekMinutes: number
  days: StudyCheckinDay[]
  week: StudyCheckinDay[]
}

export interface ModelEndpointSettings {
  endpoint: string
  model: string
  hasApiKey: boolean
  apiKeyHint: string
}

export interface EmbeddingSpec {
  id: string
  name: string
  provider: string
  model: string
  dimensions: number
  maxSequenceLength: number
  revision: string
  fingerprint: string
  endpoint: string | null
  hasApiKey: boolean
  createdAt: string
}

export interface EmbeddingIndexStatus {
  knowledgeBaseId: string
  indexId: string
  specName: string | null
  status: string
  progress: number
  stage: string | null
}

export interface EmbeddingSettings {
  defaultSpecId: string | null
  defaultSpecName: string | null
  specs: EmbeddingSpec[]
  indexes: EmbeddingIndexStatus[]
}

export interface AgentSettings {
  enableToolCalling: boolean
  enableWebSearch: boolean
  enableFileAccess: boolean
}

export const defaultAgentSettings: AgentSettings = {
  enableToolCalling: false,
  enableWebSearch: false,
  enableFileAccess: true,
}

export function normalizeAgentSettings(source?: Partial<AgentSettings> | null): AgentSettings {
  return {
    enableToolCalling: source?.enableToolCalling ?? defaultAgentSettings.enableToolCalling,
    enableWebSearch: source?.enableWebSearch ?? defaultAgentSettings.enableWebSearch,
    enableFileAccess: source?.enableFileAccess ?? defaultAgentSettings.enableFileAccess,
  }
}

export interface AppSettings {
  useCloudApi: boolean
  local: ModelEndpointSettings
  online: ModelEndpointSettings
  preferences: {
    displayName: string
    dailyGoalMinutes: number
  }
  runtime: {
    embeddingModelDirectory: string
    embeddingDimensions: number
    storageRootPath: string
    maxFileSizeBytes: number
    maxVideoFileSizeBytes: number
  }
  embedding: EmbeddingSettings
  /** 旧版本后端不返回该字段，读取时需用 normalizeAgentSettings 兜底。 */
  agent?: AgentSettings
}

export interface SaveSettingsPayload {
  useCloudApi: boolean
  local: { endpoint: string; model: string; apiKey?: string | null }
  online: { endpoint: string; model: string; apiKey?: string | null }
  preferences: { displayName: string; dailyGoalMinutes: number }
  embeddingSpecId?: string | null
  agent?: AgentSettings
}

export interface SemanticSearchResult {
  documentId: string
  fileName: string
  content: string
  similarity: number
}

export interface NoteSourceRecord {
  documentId: string
  fileName: string
  chunkIndex: number
  similarity: number
  snippet?: string | null
}

export interface NoteRecord {
  id: string
  knowledgeBaseId: string
  title: string
  excerpt: string
  content: string
  category: string
  color: string
  tags: string[]
  isPinned: boolean
  sourceDocumentId?: string | null
  createdAt: string
  updatedAt: string
  topicId?: string | null
  sources?: NoteSourceRecord[] | null
  sourceSessionId?: string | null
}

export interface NoteInput {
  title: string
  content: string
  category?: string
  color?: string
  tags?: string[]
  sourceDocumentId?: string | null
  topicId?: string | null
  sources?: NoteSourceRecord[] | null
  sourceSessionId?: string | null
}

export interface DocumentContentPage {
  documentId: string
  fileName: string
  extension: string
  content: string
  totalLength: number
  offset: number
  hasMore: boolean
  chunkCount: number
}

interface UploadSessionResult {
  id: string
  fileName: string
  sizeBytes: number
  uploadedBytes: number
  chunkSizeBytes: number
  status: string
  documentId?: string | null
}

interface UploadChunkResult {
  uploadedBytes: number
}

export async function listKnowledgeBases(): Promise<KnowledgeBase[]> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeBase[]
}

export async function createKnowledgeBase(name: string): Promise<KnowledgeBase> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeBase
}

/** 删除知识库时连带删掉的数量，用来在提示里告诉用户「删了什么」。 */
export interface KnowledgeBaseDeletionResult {
  knowledgeBaseId: string
  name: string
  deletedDocuments: number
  deletedChunks: number
  deletedNotes: number
  deletedTopics: number
  deletedChatSessions: number
}

/**
 * 删除知识库：资料、切片、向量、笔记、专题、问答历史会一起消失，不可恢复。
 * 默认知识库由后端拒绝（409），界面应当先隐藏入口。
 */
export async function deleteKnowledgeBase(knowledgeBaseId: string): Promise<KnowledgeBaseDeletionResult> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeBaseDeletionResult
}

export async function createKnowledgeChatSession(knowledgeBaseId: string, title = '新的对话'): Promise<KnowledgeChatSession> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeChatSession
}

export async function listKnowledgeChatSessions(knowledgeBaseId: string): Promise<KnowledgeChatSession[]> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeChatSession[]
}

export async function renameKnowledgeChatSession(knowledgeBaseId: string, sessionId: string, title: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/${encodeURIComponent(sessionId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function deleteKnowledgeChatSession(knowledgeBaseId: string, sessionId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/${encodeURIComponent(sessionId)}`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function deleteKnowledgeChatSessions(knowledgeBaseId: string, sessionIds: string[]): Promise<number> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/batch-delete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionIds }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const result = await response.json() as { deletedCount?: number }
  return result.deletedCount ?? 0
}

export async function exportKnowledgeChatSession(knowledgeBaseId: string, sessionId: string): Promise<Blob> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/${encodeURIComponent(sessionId)}/export`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.blob()
}

export async function listKnowledgeChatMessages(knowledgeBaseId: string, sessionId: string): Promise<KnowledgeChatMessage[]> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/${encodeURIComponent(sessionId)}/messages`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgeChatMessage[]
}

export async function sendKnowledgeChatMessage(knowledgeBaseId: string, sessionId: string, message: string, toolOptions?: ChatToolOptions): Promise<ChatReply> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/sessions/${encodeURIComponent(sessionId)}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message, ...toolOptions }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const result = await response.json() as { answer?: string; citations?: Citation[]; toolCalls?: ToolCallTrace[]; toolCallingEnabled?: boolean; webSearchEnabled?: boolean; fileAccessEnabled?: boolean; retrieval?: KnowledgeChatRetrieval }
  return {
    answer: result.answer || '模型暂时没有返回文本内容。',
    citations: result.citations || [],
    toolCalls: result.toolCalls || [],
    toolCallingEnabled: result.toolCallingEnabled,
    webSearchEnabled: result.webSearchEnabled,
    fileAccessEnabled: result.fileAccessEnabled,
    retrieval: result.retrieval,
  }
}

export async function listCompanions(): Promise<CompanionProfile[]> {
  const response = await fetch(`${apiBaseUrl}/api/companions`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionProfile[]
}

export async function importChatHistory(file: File, name: string, targetSender: string, platform: string): Promise<CompanionProfile> {
  const body = new FormData()
  body.append('file', file)
  body.append('name', name)
  body.append('targetSender', targetSender)
  body.append('platform', platform)
  const response = await fetch(`${apiBaseUrl}/api/companions/import`, { method: 'POST', body })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionProfile
}

export async function generateCompanionPersona(profileId: string): Promise<CompanionProfile> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/persona`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionProfile
}

export async function deleteCompanion(profileId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function createCompanionSession(profileId: string, title: string): Promise<CompanionSession> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/sessions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionSession
}

export async function listCompanionSessions(profileId: string): Promise<CompanionSession[]> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/sessions`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionSession[]
}

export async function listCompanionMessages(sessionId: string): Promise<CompanionMessage[]> {
  const response = await fetch(`${apiBaseUrl}/api/companions/sessions/${sessionId}/messages`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionMessage[]
}

export async function sendCompanionMessage(profileId: string, sessionId: string, message: string): Promise<{ answer: string; recalledMessageCount: number }> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/sessions/${sessionId}/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as { answer: string; recalledMessageCount: number }
}

/** 伴侣历史的向量索引状态：indexed 是当前向量模型下已建索引的消息数。 */
export interface CompanionIndexStatus {
  profileId: string
  indexed: number
}

export async function getCompanionIndex(profileId: string): Promise<CompanionIndexStatus> {
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/index`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as CompanionIndexStatus
}

/** 手动补齐/重建向量索引（导入时已自动后台建一次，这里用于换模型或失败后重试）。 */
export async function reindexCompanion(profileId: string, limit?: number): Promise<{ profileId: string; indexed: number; failed: number; total: number; warning: string | null }> {
  const query = typeof limit === 'number' ? `?limit=${limit}` : ''
  const response = await fetch(`${apiBaseUrl}/api/companions/${profileId}/reindex${query}`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as { profileId: string; indexed: number; failed: number; total: number; warning: string | null }
}

/**
 * 一条问答引用：rank/chunkIndex/chunkId 可回溯到“哪份资料的哪一段”，
 * snippet 用于悬浮预览，similarity 是余弦相似度（0~1）。
 * 旧数据可能只有前三个字段。
 */
export interface Citation {
  documentId: string
  fileName: string
  similarity: number
  rank?: number
  chunkId?: number
  chunkIndex?: number
  snippet?: string | null
  // 音视频切片在媒体中的起始秒数；普通文档为 null。前端据此提供“跳到这里播放”。
  mediaStartSeconds?: number | null
}

/** 本轮检索的索引上下文，用于解释“为什么是这几条资料”。 */
export interface KnowledgeChatRetrieval {
  indexId?: string | null
  embeddingSpecName?: string | null
  warning?: string | null
  legacyIndex: boolean
}

/** 助手消息落库的答案元数据：检索上下文 + 工具调用审计 + 网页来源。 */
export interface KnowledgeChatAnswerMeta {
  retrieval?: KnowledgeChatRetrieval | null
  toolCalls?: ToolCallTrace[] | null
  sources?: string[] | null
}

export interface ToolCallTrace {
  name: string
  arguments: string
  resultPreview: string
  succeeded: boolean
  elapsedMilliseconds: number
}

export interface ChatReply {
  answer: string
  citations: Citation[]
  toolCalls?: ToolCallTrace[]
  toolCallingEnabled?: boolean
  webSearchEnabled?: boolean
  fileAccessEnabled?: boolean
  retrieval?: KnowledgeChatRetrieval
}

/**
 * 单轮问答的工具开关。传 null/undefined 时沿用设置页保存的偏好；
 * 传布尔值只对本次提问生效。
 */
export interface ChatToolOptions {
  enableToolCalling?: boolean
  enableWebSearch?: boolean
  enableFileAccess?: boolean
}

/** 设置页默认值：不调用工具、不联网，但允许查看本地文件。 */
export const defaultChatToolOptions: Required<ChatToolOptions> = {
  enableToolCalling: false,
  enableWebSearch: false,
  enableFileAccess: true,
}

export interface AgentToolInfo {
  name: string
  description: string
  category: string
}

export interface McpServerStatus {
  name: string
  connected: boolean
  error?: string | null
  toolCount: number
}

export interface McpStatus {
  serverEnabled: boolean
  serverPath: string
  connectedCount: number
  toolCount: number
  servers: McpServerStatus[]
  tools: { server: string; name: string; description: string }[]
}

export interface AgentFileRoot {
  name: string
  path: string
  writable: boolean
  deletable: boolean
  exists: boolean
}

export interface AgentInfo {
  toolCallingEnabled: boolean
  webSearchEnabled?: boolean
  fileAccessEnabled?: boolean
  maxToolIterations: number
  workspaceWritable: boolean
  mcpServerEnabled: boolean
  mcpServerPath: string
  fileRoots: AgentFileRoot[]
}

export async function getAgentInfo(): Promise<AgentInfo> {
  const response = await fetch(`${apiBaseUrl}/api/agent/info`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AgentInfo
}

export async function listAgentTools(knowledgeBaseId?: string): Promise<AgentToolInfo[]> {
  const query = knowledgeBaseId ? `?knowledgeBaseId=${encodeURIComponent(knowledgeBaseId)}` : ''
  const response = await fetch(`${apiBaseUrl}/api/agent/tools${query}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const result = await response.json() as { tools?: AgentToolInfo[] }
  return result.tools || []
}

export async function getMcpStatus(): Promise<McpStatus> {
  const response = await fetch(`${apiBaseUrl}/api/mcp/status`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as McpStatus
}

/** 一键复制的本地 MCP 配置（Cursor / Claude Desktop）。 */
export interface LocalMcpConfig {
  endpoint: string
  baseUrl: string
  /**
   * 基地址是从哪来的：`oauth-issuer` | `public-base-url` | `kestrel` | `request`。
   * 前两种说明地址是「对外可用」的；后两种在本机才是对的。
   */
  baseUrlSource: string
  port: number | null
  fixedLocalPort: number | null
  cursor: string
  claudeDesktop: string
  curl: string
  notes: string[]
}

/**
 * 取当前实例的实际监听地址与客户端配置片段。
 *
 * 端口是随机回环端口，只有后端自己知道，前端不能猜——所以由这个接口给。
 * 不传 clientKey 时生成的配置不含鉴权头，用户可以随后自己补上。
 */
export async function getLocalMcpConfig(clientKey?: string): Promise<LocalMcpConfig> {
  const query = clientKey ? `?clientKey=${encodeURIComponent(clientKey)}` : ''
  const response = await fetch(`${apiBaseUrl}/api/mcp/local-config${query}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as LocalMcpConfig
}

async function getErrorMessage(response: Response) {
  try {
    const body = await response.json() as { error?: string; detail?: string; title?: string }
    return body.error || body.detail || body.title || `请求失败（${response.status}）`
  } catch {
    return `请求失败（${response.status}）`
  }
}

export async function checkApiHealth(): Promise<boolean> {
  try {
    const response = await fetch(`${apiBaseUrl}/api/health`, { signal: AbortSignal.timeout(4000) })
    return response.ok
  } catch {
    return false
  }
}

export async function uploadDocument(
  file: File,
  knowledgeBaseId: string,
  onProgress?: (uploadedBytes: number, totalBytes: number) => void,
): Promise<UploadResult> {
  const uploadKey = `myfirstmafagent.upload.${knowledgeBaseId}.${encodeURIComponent(file.name)}.${file.size}.${file.lastModified}`
  let session: UploadSessionResult | undefined
  const savedSessionId = window.localStorage.getItem(uploadKey)
  if (savedSessionId) {
    try {
      session = await getUploadSession(savedSessionId)
      if (session.fileName !== file.name || session.sizeBytes !== file.size || !['uploading', 'completed'].includes(session.status.toLowerCase()))
        session = undefined
    } catch {
      session = undefined
    }
    if (!session) window.localStorage.removeItem(uploadKey)
  }

  if (!session) {
    const response = await fetch(`${apiBaseUrl}/api/uploads`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ knowledgeBaseId, fileName: file.name, sizeBytes: file.size, contentType: file.type || 'application/octet-stream' }),
    })
    if (!response.ok) throw new Error(await getErrorMessage(response))
    session = await response.json() as UploadSessionResult
    window.localStorage.setItem(uploadKey, session.id)
  }

  if (session.status.toLowerCase() === 'completed') {
    const result = await completeUpload(session.id)
    window.localStorage.removeItem(uploadKey)
    onProgress?.(file.size, file.size)
    return result
  }

  let uploadedBytes = session.uploadedBytes
  const chunkSizeBytes = session.chunkSizeBytes
  if (chunkSizeBytes <= 0) throw new Error('服务器返回了无效的分片大小。')
  onProgress?.(uploadedBytes, file.size)

  while (uploadedBytes < file.size) {
    const offset = uploadedBytes
    const end = Math.min(file.size, offset + chunkSizeBytes)
    let chunkAccepted = false

    for (let attempt = 0; attempt < 3 && !chunkAccepted; attempt++) {
      try {
        const response = await fetch(`${apiBaseUrl}/api/uploads/${encodeURIComponent(session.id)}/chunks?offset=${offset}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/octet-stream' },
          body: file.slice(offset, end),
        })
        if (response.status === 409) {
          session = await getUploadSession(session.id)
          if (session.status.toLowerCase() === 'completed') {
            uploadedBytes = file.size
            chunkAccepted = true
            break
          }
          if (session.uploadedBytes === offset)
            throw new UploadRequestError(await getErrorMessage(response), response.status)
          uploadedBytes = session.uploadedBytes
          onProgress?.(uploadedBytes, file.size)
          chunkAccepted = true
          break
        }
        if (!response.ok) throw new UploadRequestError(await getErrorMessage(response), response.status)
        const result = await response.json() as UploadChunkResult
        if (result.uploadedBytes <= offset || result.uploadedBytes > end)
          throw new Error('服务器返回的分片偏移无效。')
        uploadedBytes = result.uploadedBytes
        onProgress?.(uploadedBytes, file.size)
        chunkAccepted = true
      } catch (error) {
        if (error instanceof UploadRequestError
          && error.status < 500
          && error.status !== 408
          && error.status !== 429)
          throw error
        if (attempt === 2) throw error
        await new Promise(resolve => window.setTimeout(resolve, 500 * (attempt + 1)))
        session = await getUploadSession(session.id)
        uploadedBytes = session.status.toLowerCase() === 'completed' ? file.size : session.uploadedBytes
        onProgress?.(uploadedBytes, file.size)
        if (uploadedBytes > offset) chunkAccepted = true
      }
    }

    if (!chunkAccepted) throw new Error('分片上传未能完成，请重新选择同一文件继续续传。')
  }

  const result = await completeUpload(session.id)
  window.localStorage.removeItem(uploadKey)
  onProgress?.(file.size, file.size)
  return result
}

async function getUploadSession(id: string): Promise<UploadSessionResult> {
  const response = await fetch(`${apiBaseUrl}/api/uploads/${encodeURIComponent(id)}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as UploadSessionResult
}

async function completeUpload(id: string): Promise<UploadResult> {
  const response = await fetch(`${apiBaseUrl}/api/uploads/${encodeURIComponent(id)}/complete`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as UploadResult
}

class UploadRequestError extends Error {
  constructor(message: string, readonly status: number) {
    super(message)
  }
}

export async function retryDocument(documentId: string): Promise<DocumentResult> {
  const response = await fetch(`${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}/retry`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as DocumentResult
}

export async function deleteDocument(documentId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function downloadDocument(documentId: string): Promise<Blob> {
  const response = await fetch(`${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}/download`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.blob()
}

/**
 * 原始文件的可直链地址，用于把图片当缩略图、把音频塞进 `<audio>`。
 *
 * <p>
 * 之所以不复用 `downloadDocument`（它返回 Blob）：`<img src>` / `<audio src>` 不能走 fetch + await，
 * 而在桌面壳里主进程会通过 `webRequest` 注入 `X-Api-Token`，直链同样能拿到令牌。
 * 后端该路由开了 `enableRangeProcessing`，所以音频可以拖动进度条。
 * </p>
 */
export function documentAssetUrl(documentId: string): string {
  return `${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}/download`
}

export async function listDocuments(knowledgeBaseId?: string): Promise<DocumentResult[]> {
  const query = knowledgeBaseId ? `?knowledgeBaseId=${encodeURIComponent(knowledgeBaseId)}` : ''
  const response = await fetch(`${apiBaseUrl}/api/documents${query}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as DocumentResult[]
}

export async function askAssistant(message: string, knowledgeBaseId?: string, toolOptions?: ChatToolOptions): Promise<ChatReply> {
  const response = await fetch(`${apiBaseUrl}/api/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ message, knowledgeBaseId, ...toolOptions }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const result = await response.json() as { answer?: string; citations?: Citation[]; toolCalls?: ToolCallTrace[] }
  return {
    answer: result.answer || '模型暂时没有返回文本内容。',
    citations: result.citations || [],
    toolCalls: result.toolCalls || [],
  }
}

export async function searchKnowledgeBase(query: string, knowledgeBaseId: string, limit = 5): Promise<SemanticSearchResult[]> {
  const response = await fetch(`${apiBaseUrl}/api/search`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ query, knowledgeBaseId, limit }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const result = await response.json() as { results?: SemanticSearchResult[] }
  return result.results ?? []
}

export async function listTopics(knowledgeBaseId: string): Promise<Topic[]> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/topics`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as Topic[]
}

export async function createTopic(knowledgeBaseId: string, name: string, color?: string): Promise<Topic> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/topics`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, color }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as Topic
}

export async function updateTopic(knowledgeBaseId: string, topicId: string, name: string, color?: string): Promise<Topic> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/topics/${encodeURIComponent(topicId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, color }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as Topic
}

export async function deleteTopic(knowledgeBaseId: string, topicId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/topics/${encodeURIComponent(topicId)}`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function assignDocumentTopic(documentId: string, topicId: string | null): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}/topic`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ topicId }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function getAppSettings(): Promise<AppSettings> {
  const response = await fetch(`${apiBaseUrl}/api/settings`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const settings = await response.json() as AppSettings
  settings.agent = normalizeAgentSettings(settings.agent)
  return settings
}

export async function saveAppSettings(payload: SaveSettingsPayload): Promise<AppSettings> {
  const response = await fetch(`${apiBaseUrl}/api/settings`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const settings = await response.json() as AppSettings
  settings.agent = normalizeAgentSettings(settings.agent)
  return settings
}

export interface ImportLocalEmbeddingSpecInput {
  name: string
  model?: string
  dimensions?: number | null
  maxSequenceLength: number
  revision: string
  modelFile: File
  tokenizerFile: File
}

export async function importLocalEmbeddingSpec(input: ImportLocalEmbeddingSpecInput): Promise<EmbeddingSpec> {
  const body = new FormData()
  body.append('name', input.name)
  if (input.model?.trim()) body.append('model', input.model.trim())
  if (input.dimensions) body.append('dimensions', String(input.dimensions))
  body.append('maxSequenceLength', String(input.maxSequenceLength))
  body.append('revision', input.revision)
  body.append('modelFile', input.modelFile)
  body.append('tokenizerFile', input.tokenizerFile)

  const response = await fetch(`${apiBaseUrl}/api/embedding-specs/import-local`, {
    method: 'POST',
    body,
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as EmbeddingSpec
}

export interface RegisterCloudEmbeddingSpecInput {
  name: string
  endpoint: string
  model: string
  apiKey?: string
  dimensions: number
  maxSequenceLength: number
  revision: string
}

export async function registerCloudEmbeddingSpec(input: RegisterCloudEmbeddingSpecInput): Promise<EmbeddingSpec> {
  const response = await fetch(`${apiBaseUrl}/api/embedding-specs/register-cloud`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as EmbeddingSpec
}

export async function probeEmbeddingSpec(specId: string): Promise<{ dimensions: number; elapsedMilliseconds: number }> {
  const response = await fetch(`${apiBaseUrl}/api/embedding-specs/${encodeURIComponent(specId)}/probe`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as { dimensions: number; elapsedMilliseconds: number }
}

export interface WhisperModelStatus {
  model: string
  path: string
  exists: boolean
  sizeBytes: number
  modifiedAt: string | null
  autoDownload: boolean
  downloading: boolean
  downloadedBytes: number
  lastError: string | null
  ready: boolean
}

export async function getWhisperModelStatus(): Promise<WhisperModelStatus> {
  const response = await fetch(`${apiBaseUrl}/api/media/whisper`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as WhisperModelStatus
}

/** 导入本地 ggml 模型文件（离线环境的正路）。走原始字节流，避免大文件被 multipart 缓冲。 */
export async function importWhisperModel(file: File): Promise<WhisperModelStatus> {
  const response = await fetch(`${apiBaseUrl}/api/media/whisper/import-raw?fileName=${encodeURIComponent(file.name)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/octet-stream' },
    body: file,
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as WhisperModelStatus
}

/** 触发后台下载；返回时通常还在下载中，需要轮询 getWhisperModelStatus。 */
export async function downloadWhisperModel(): Promise<WhisperModelStatus> {
  const response = await fetch(`${apiBaseUrl}/api/media/whisper/download`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as WhisperModelStatus
}

export async function deleteWhisperModel(): Promise<WhisperModelStatus> {
  const response = await fetch(`${apiBaseUrl}/api/media/whisper`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as WhisperModelStatus
}

export async function getStudySummary(days = 30): Promise<StudySummary> {
  const response = await fetch(`${apiBaseUrl}/api/study/checkins?days=${days}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as StudySummary
}

export async function checkInStudy(minutes: number, note?: string): Promise<StudySummary> {
  const response = await fetch(`${apiBaseUrl}/api/study/checkins`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ minutes, note }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as StudySummary
}

/**
 * 日历弹窗用：取 [from, to] 内**确实有打卡**的日期（后端不补零）。
 * 与 getStudySummary 的区别是这里给的是显式区间而不是「最近 N 天」，所以可以自由翻月。
 */
export async function getStudyCheckinRange(from: string, to: string): Promise<StudyCheckinDay[]> {
  const params = new URLSearchParams({ from, to })
  const response = await fetch(`${apiBaseUrl}/api/study/checkins/range?${params.toString()}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as StudyCheckinDay[]
}

export interface NoteListFilter {
  query?: string
  category?: string
  documentId?: string | null
  topicId?: string | null
}

export async function listNotes(knowledgeBaseId: string, filter: NoteListFilter = {}): Promise<NoteRecord[]> {
  const params = new URLSearchParams()
  if (filter.query?.trim()) params.set('query', filter.query.trim())
  if (filter.category?.trim()) params.set('category', filter.category.trim())
  if (filter.documentId) params.set('documentId', filter.documentId)
  if (filter.topicId) params.set('topicId', filter.topicId)
  const suffix = params.size ? `?${params.toString()}` : ''
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes${suffix}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as NoteRecord[]
}

/** 批量导出笔记：接口直接回 Markdown 文件，前端只负责把 Blob 落成下载。 */
export async function exportNotes(knowledgeBaseId: string, noteIds?: string[]): Promise<{ blob: Blob; fileName: string }> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes/export`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ noteIds: noteIds ?? null }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))

  const disposition = response.headers.get('Content-Disposition') ?? ''
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
  const rawName = match?.[1]?.trim()
  const fileName = rawName ? decodeURIComponent(rawName) : 'notes.md'
  return { blob: await response.blob(), fileName }
}

export interface KnowledgePackageExportSummary {
  fileName: string
  documents: number
  notes: number
  topics: number
  warnings: number
  /** 用户在“另存为”对话框里点了取消——不是错误，前端不必提示失败。 */
  cancelled?: boolean
  /** 用户选定的文件名；走浏览器兜底下载时为 null。 */
  savedTo?: string | null
}

/** File System Access API：浏览器/Electron 都支持，用来让用户自己挑目录。 */
interface SaveFilePickerOptions {
  suggestedName?: string
  types?: Array<{ description?: string; accept: Record<string, string[]> }>
}

interface SaveFileHandle {
  name: string
  createWritable: () => Promise<WritableStream<Uint8Array>>
}

type SaveFilePicker = (options?: SaveFilePickerOptions) => Promise<SaveFileHandle>

function saveFilePicker(): SaveFilePicker | undefined {
  return (window as unknown as { showSaveFilePicker?: SaveFilePicker }).showSaveFilePicker
}

export interface KnowledgePackageImportResult {
  knowledgeBaseId: string
  knowledgeBaseName: string
  documents: number
  notes: number
  topics: number
  warnings: string[]
}

/**
 * 导出整个知识库为可移植包（zip：manifest.json + 原始文件）。
 *
 * 流程刻意做成「服务端边读边发 → 客户端边收边写」：
 * 1. 先弹“另存为”让用户自己选目录（File System Access API，Electron/Chromium 可用）；
 * 2. 选好之后把响应体直接管道写进那个文件，全程不进内存、服务端也不落临时文件。
 * 浏览器不支持该 API 时，退回普通下载（桌面端由 Electron 的 will-download 弹保存框）。
 */
export async function exportKnowledgeBasePackage(knowledgeBaseId: string): Promise<KnowledgePackageExportSummary> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/package`)
  if (!response.ok) throw new Error(await getErrorMessage(response))

  const disposition = response.headers.get('Content-Disposition') ?? ''
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
  const rawName = match?.[1]?.trim()
  const fileName = rawName ? decodeURIComponent(rawName) : `${knowledgeBaseId}.zjpack.zip`
  const numberHeader = (name: string) => {
    const raw = response.headers.get(name)
    return raw ? Number.parseInt(raw, 10) || 0 : 0
  }

  const summary: KnowledgePackageExportSummary = {
    fileName,
    documents: numberHeader('X-Package-Documents'),
    notes: numberHeader('X-Package-Notes'),
    topics: numberHeader('X-Package-Topics'),
    warnings: numberHeader('X-Package-Warnings'),
  }

  const picker = saveFilePicker()
  if (picker && response.body) {
    let handle: SaveFileHandle | null = null
    try {
      handle = await picker({
        suggestedName: fileName,
        types: [{ description: '知识库包', accept: { 'application/zip': ['.zip'] } }],
      })
    } catch (error) {
      if ((error as DOMException | undefined)?.name === 'AbortError') {
        return { ...summary, cancelled: true }
      }
      // 其它异常（例如浏览器策略禁用）：退回下面的普通下载。
      handle = null
    }

    if (handle) {
      const writable = await handle.createWritable()
      await response.body.pipeTo(writable)
      return { ...summary, savedTo: handle.name }
    }
  }

  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  return { ...summary, savedTo: null }
}

/** 导入知识库包，服务端会新建一个知识库并把资料重新排队解析。 */
export async function importKnowledgeBasePackage(file: File): Promise<KnowledgePackageImportResult> {
  const form = new FormData()
  form.append('file', file, file.name)
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/import`, { method: 'POST', body: form })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as KnowledgePackageImportResult
}

export async function createNote(knowledgeBaseId: string, input: NoteInput): Promise<NoteRecord> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as NoteRecord
}

export async function updateNote(knowledgeBaseId: string, noteId: string, input: NoteInput): Promise<NoteRecord> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes/${encodeURIComponent(noteId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(input),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as NoteRecord
}

export async function setNotePinned(knowledgeBaseId: string, noteId: string, pinned: boolean): Promise<NoteRecord> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes/${encodeURIComponent(noteId)}/pin`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pinned }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as NoteRecord
}

export async function deleteNote(knowledgeBaseId: string, noteId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes/${encodeURIComponent(noteId)}`, {
    method: 'DELETE',
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function getDocumentContent(documentId: string, offset = 0, length = 8000): Promise<DocumentContentPage> {
  const response = await fetch(`${apiBaseUrl}/api/documents/${encodeURIComponent(documentId)}/content?offset=${offset}&length=${length}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as DocumentContentPage
}

// ---------- 分身（Avatar） ----------
// 分身与知识库完全隔离：素材、洞察、对话都只写入 avatar_* 表。

export interface AvatarProfile {
  id: string
  displayName: string
  headline: string
  personality: string
  values: string
  strengths: string
  weaknesses: string
  skills: string
  resources: string
  constraints: string
  longTermGoals: string
  riskPreference: 'conservative' | 'balanced' | 'aggressive'
  riskPreferenceLabel: string
  isConfigured: boolean
  updatedAt: string
}

/** 碎片素材：日记、聊天记录、性格描述、形象描述… */
export interface AvatarFragment {
  id: string
  kind: string
  kindLabel: string
  title: string
  content: string
  occurredAt: string
  source: string
  tags: string[]
  isDigested: boolean
}

/** 人格洞察：由素材分析出的一条结论。 */
export interface AvatarInsight {
  id: string
  dimension: string
  dimensionLabel: string
  title: string
  content: string
  confidence: number
  evidenceCount: number
  evidence: string[]
  updatedAt: string
}

/** AI 整理后的一条记忆卡，是分身问答的检索单位。 */
export interface AvatarMemory {
  id: string
  fragmentId: string | null
  kind: string
  kindLabel: string
  memoryType: string
  memoryTypeLabel: string
  stability: string
  title: string
  summary: string
  content: string
  occurredAt: string
  people: string[]
  tags: string[]
  emotion: string
  importance: number
  confidence: number
  lifecycleStatus: 'active' | 'candidate' | 'pending_confirmation' | 'historical' | 'superseded' | 'corrected' | 'archived' | string
  lifecycleLabel: string
  privacyLevel: string
  validFrom: string | null
  validTo: string | null
  supersedesId: string | null
  evidenceCount: number
  lastUsedAt: string | null
  source: string
  vectorState: 'pending' | 'ready' | 'failed' | string
  createdAt: string
}

/** 聊天式投喂的一条对话记录。 */
export interface AvatarFeedEntry {
  id: number
  role: 'user' | 'assistant' | string
  content: string
  createdAt: string
}

export interface AvatarFeedResult {
  reply: string
  memories: AvatarMemory[]
  insights: AvatarInsight[]
  usedFallback: boolean
  warning: string | null
  indexedCount: number
  pendingVectorCount: number
  ingestionId: string | null
  ingestionStatus: string | null
}

/** 异步投喂任务状态。 */
export interface AvatarIngestion {
  id: string
  sourceType: string
  sourceRef: string | null
  status: 'queued' | 'running' | 'completed' | 'partial' | 'failed' | string
  stage: string
  error: string | null
  memoryCount: number
  insightCount: number
  statsJson: string
  createdAt: string
  startedAt: string | null
  completedAt: string | null
}

export type AvatarFeedbackType = 'like' | 'unlike' | 'correction' | 'supplement'

export interface AvatarFeedbackPayload {
  chatMessageId?: number | null
  memoryIds?: string[]
  feedbackType: AvatarFeedbackType
  reasonCode?: string
  comment?: string
  correctedText?: string
}

export interface AvatarFeedbackResult {
  id: string
  chatMessageId: number | null
  memoryIds: string[]
  feedbackType: string
  reasonCode: string
  comment: string
  correctedText: string
  ingestionId: string | null
  createdAt: string
}

export interface AvatarReindexResult {
  indexed: number
  pending: number
  total: number
  warning: string | null
}

export interface AvatarChatCitation {
  memoryId: string
  kind: string
  title: string
  score: number
}

export interface AvatarChatMessage {
  id: number
  role: 'user' | 'assistant' | string
  content: string
  citations: AvatarChatCitation[]
  createdAt: string
}

export interface AvatarChatSession {
  id: string
  title: string
  createdAt: string
  updatedAt: string
}

export interface AvatarChatReply {
  sessionId: string
  answer: string
  citations: AvatarChatCitation[]
  chatMessageId: number
}

/** 人格洞察候选：证据还不够、等待确认的版本。 */
export interface AvatarInsightCandidate {
  id: string
  insightId: string | null
  dimension: string
  dimensionLabel: string
  title: string
  content: string
  confidence: number
  status: string
  scope: string
  evidenceIds: string[]
  supersedesId: string | null
  createdAt: string
}

export interface AvatarDigestResult {
  insightCount: number
  fragmentCount: number
  pendingFragmentCount: number
  insights: AvatarInsight[]
}

export interface AvatarUploadResult {
  imported: number
  fragments: AvatarFragment[]
}

export interface AvatarBehaviorStat {
  category: string
  minutes: number
  count: number
}

export interface AvatarOverview {
  profile: AvatarProfile
  fragmentCount: number
  undigestedCount: number
  insightCount: number
  chatSessionCount: number
  readinessScore: number
  missingInputs: string[]
  recentFragments: AvatarFragment[]
  insights: AvatarInsight[]
  chatSessions: AvatarChatSession[]
  behaviorStats: AvatarBehaviorStat[]
  memoryCount: number
  pendingVectorCount: number
  recentMemories: AvatarMemory[]
  feedEntries: AvatarFeedEntry[]
}

export interface SaveAvatarProfilePayload {
  displayName: string
  headline: string
  personality: string
  values: string
  strengths: string
  weaknesses: string
  skills: string
  resources: string
  constraints: string
  longTermGoals: string
  riskPreference: string
}

export interface CreateAvatarFragmentPayload {
  kind?: string
  title?: string
  content: string
  occurredAt?: string
  tags?: string[]
}

export const avatarFragmentKinds: { value: string; label: string }[] = [
  { value: 'journal', label: '日记' },
  { value: 'chat', label: '聊天记录' },
  { value: 'personality', label: '性格' },
  { value: 'appearance', label: '形象' },
  { value: 'opinion', label: '观点' },
  { value: 'habit', label: '习惯' },
  { value: 'knowledge', label: '见闻' },
  { value: 'relationship', label: '人际' },
  { value: 'other', label: '其它' },
]

export async function getAvatarOverview(): Promise<AvatarOverview> {
  const response = await fetch(`${apiBaseUrl}/api/avatar`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarOverview
}

export async function saveAvatarProfile(payload: SaveAvatarProfilePayload): Promise<AvatarProfile> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/profile`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarProfile
}

// ---- 投喂：碎片素材 ----

export async function listAvatarFragments(limit = 100): Promise<AvatarFragment[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/fragments?limit=${limit}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFragment[]
}

export async function createAvatarFragment(payload: CreateAvatarFragmentPayload): Promise<AvatarFragment> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/fragments`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFragment
}

export async function uploadAvatarFragmentFile(
  file: File,
  kind: string,
  occurrenceDate?: string,
): Promise<AvatarUploadResult> {
  const form = new FormData()
  form.append('file', file)
  form.append('kind', kind)
  if (occurrenceDate) form.append('occurrenceDate', occurrenceDate)

  const response = await fetch(`${apiBaseUrl}/api/avatar/fragments/upload`, {
    method: 'POST',
    body: form,
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarUploadResult
}

export async function deleteAvatarFragment(fragmentId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/fragments/${encodeURIComponent(fragmentId)}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

// ---- 投喂：自由文本 / 文件（AI 自动整理 + 向量化） ----

export async function feedAvatar(text: string, occurredAt?: string): Promise<AvatarFeedResult> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/feed`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text, occurredAt: occurredAt ?? null }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFeedResult
}

export async function uploadAvatarFeedFile(file: File, occurrenceDate?: string): Promise<AvatarFeedResult> {
  const form = new FormData()
  form.append('file', file)
  if (occurrenceDate) form.append('occurrenceDate', occurrenceDate)

  const response = await fetch(`${apiBaseUrl}/api/avatar/feed/upload`, {
    method: 'POST',
    body: form,
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFeedResult
}

export async function listAvatarFeed(limit = 60): Promise<AvatarFeedEntry[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/feed?limit=${limit}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFeedEntry[]
}

export async function listAvatarMemories(query = '', limit = 100): Promise<AvatarMemory[]> {
  const params = new URLSearchParams({ limit: String(limit) })
  if (query.trim()) params.set('query', query.trim())
  const response = await fetch(`${apiBaseUrl}/api/avatar/memories?${params.toString()}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarMemory[]
}

/** 查询一次异步投喂任务的进度。 */
export async function getAvatarIngestion(ingestionId: string): Promise<AvatarIngestion> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/ingestions/${encodeURIComponent(ingestionId)}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarIngestion
}

/** 人格候选洞察：证据不足时不会直接写进稳定人格。 */
export async function listAvatarInsightCandidates(limit = 50): Promise<AvatarInsightCandidate[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/insights/candidates?limit=${limit}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarInsightCandidate[]
}

export async function confirmAvatarInsight(versionId: string): Promise<AvatarInsightCandidate> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/insights/candidates/${encodeURIComponent(versionId)}/confirm`, {
    method: 'POST',
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarInsightCandidate
}

/** 回答反馈：像我 / 不像我 / 纠正 / 补充。 */
export async function submitAvatarFeedback(payload: AvatarFeedbackPayload): Promise<AvatarFeedbackResult> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/feedback`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFeedbackResult
}

export async function deleteAvatarMemory(memoryId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/memories/${encodeURIComponent(memoryId)}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function reindexAvatarMemories(all = false, limit = 200): Promise<AvatarReindexResult> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/memories/reindex`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ all, limit }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarReindexResult
}

/** 把旧版碎片素材补整理成记忆（迁移用）。 */
export async function migrateAvatarFragments(limit = 12): Promise<AvatarFeedResult> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/memories/migrate?limit=${limit}`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarFeedResult
}

// ---- 分析：人格洞察 ----

export async function listAvatarInsights(): Promise<AvatarInsight[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/insights`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarInsight[]
}

export async function runAvatarDigest(includeDigested = false): Promise<AvatarDigestResult> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/digest`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ includeDigested }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarDigestResult
}

export async function clearAvatarInsights(): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/insights`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

// ---- 问答：第一人称分身 ----

export async function listAvatarChatSessions(): Promise<AvatarChatSession[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/chat/sessions`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarChatSession[]
}

export async function createAvatarChatSession(title?: string): Promise<AvatarChatSession> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/chat/sessions`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: title ?? null }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarChatSession
}

export async function listAvatarChatMessages(sessionId: string): Promise<AvatarChatMessage[]> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/chat/sessions/${encodeURIComponent(sessionId)}/messages`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarChatMessage[]
}

export async function deleteAvatarChatSession(sessionId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/chat/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

export async function sendAvatarChat(sessionId: string | null, message: string): Promise<AvatarChatReply> {
  const response = await fetch(`${apiBaseUrl}/api/avatar/chat`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sessionId, message }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as AvatarChatReply
}

// ---- 任务中心（长任务队列） ----
//
// 契约见 docs/JOBS.md：任务状态只存在库里，前端只是「观察与控制」，
// 真正的执行永远在后台槽位。因此这里没有"启动任务"之类的概念，
// 只有列表 / 详情 / 取消 / 重跑 / 实时流。

export type JobStatus = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled'

export interface JobRecord {
  id: string
  type: string
  status: JobStatus
  priority: number
  attempts: number
  maxAttempts: number
  payload?: string | null
  result?: string | null
  progress: number
  progressMessage?: string | null
  lastError?: string | null
  workerId?: string | null
  idempotencyKey?: string | null
  knowledgeBaseId?: string | null
  subjectId?: string | null
  cancelRequested: boolean
  createdAt: string
  updatedAt: string
  nextRunAt: string
  startedAt?: string | null
  completedAt?: string | null
  leaseUntil?: string | null
}

export interface JobCounts {
  queued: number
  running: number
  succeeded: number
  failed: number
  cancelled: number
  oldestQueuedAt?: string | null
  pending: number
}

/** SSE 推送的一条任务变化（`kind` 目前有 job/progress/completed/failed）。 */
export interface JobStreamEvent {
  kind: string
  jobId: string
  type: string
  status: JobStatus
  progress: number
  message?: string | null
  error?: string | null
  occurredAt: string
}

export interface JobQueryOptions {
  status?: string
  type?: string
  subjectId?: string
  limit?: number
}

export async function listJobs(options: JobQueryOptions = {}): Promise<JobRecord[]> {
  const params = new URLSearchParams()
  if (options.status) params.set('status', options.status)
  if (options.type) params.set('type', options.type)
  if (options.subjectId) params.set('subjectId', options.subjectId)
  params.set('limit', String(options.limit ?? 50))

  const response = await fetch(`${apiBaseUrl}/api/jobs?${params.toString()}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { jobs: JobRecord[] }
  return body.jobs
}

export async function getJobCounts(): Promise<JobCounts> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/counts`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobCounts
}

/** 已注册的任务类型。界面用它填下拉框，不要硬编码类型名。 */
export async function listJobTypes(): Promise<string[]> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/types`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { types: string[] }
  return body.types
}

/**
 * 按类型的并发配额与当前占用。
 *
 * `limit === null` 表示该类任务没有单独限流（只受 `maxConcurrentJobs` 约束）。
 * 计数是「本进程」的，多进程部署时每个进程各自限流。
 */
export interface JobTypeQuota {
  type: string
  limit: number | null
  running: number
}

export interface JobQuotaSnapshot {
  maxConcurrentJobs: number
  quotas: JobTypeQuota[]
}

export async function getJobQuotas(): Promise<JobQuotaSnapshot> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/quotas`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobQuotaSnapshot
}

export async function getJob(jobId: string): Promise<JobRecord> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/${encodeURIComponent(jobId)}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobRecord
}

export async function cancelJob(jobId: string): Promise<JobRecord> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/${encodeURIComponent(jobId)}/cancel`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobRecord
}

export async function retryJob(jobId: string): Promise<JobRecord> {
  const response = await fetch(`${apiBaseUrl}/api/jobs/${encodeURIComponent(jobId)}/retry`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobRecord
}

export type JobStreamState = 'connecting' | 'live' | 'offline'

export interface JobStreamHandlers {
  /** 首帧快照（每次重连都会再给一份）。 */
  onSnapshot?: (snapshot: { counts: JobCounts; jobs: JobRecord[] }) => void
  onJobEvent?: (event: JobStreamEvent) => void
  onStateChange?: (state: JobStreamState) => void
}

/**
 * 订阅任务变化（SSE）。
 *
 * 返回取消函数：组件卸载时必须调用，否则 EventSource 会一直重连。
 * `EventSource` 自带重连，这里不需要写退避；`onStateChange('offline')`
 * 只是让界面把"实时"角标改成"重连中/轮询"。
 */
export function openJobStream(handlers: JobStreamHandlers): () => void {
  let source: EventSource
  try {
    source = new EventSource(`${apiBaseUrl}/api/jobs/stream`)
  } catch {
    handlers.onStateChange?.('offline')
    return () => {}
  }

  handlers.onStateChange?.('connecting')

  source.addEventListener('open', () => handlers.onStateChange?.('live'))

  source.addEventListener('snapshot', (event) => {
    try {
      const payload = JSON.parse((event as MessageEvent<string>).data) as { counts: JobCounts; jobs: JobRecord[] }
      handlers.onStateChange?.('live')
      handlers.onSnapshot?.(payload)
    } catch {
      // 半帧/坏帧直接忽略，下一帧会自愈。
    }
  })

  // 命名事件必须逐个订阅：onmessage 只收「没有 event: 名」的帧。
  for (const name of ['job', 'progress', 'completed', 'failed']) {
    source.addEventListener(name, (event) => {
      try {
        handlers.onJobEvent?.(JSON.parse((event as MessageEvent<string>).data) as JobStreamEvent)
      } catch {
        // 同上。
      }
    })
  }

  source.addEventListener('error', () => {
    // readyState=2（CLOSED）表示浏览器放弃了重连；1（CONNECTING）表示仍在自动重连。
    handlers.onStateChange?.(source.readyState === 2 ? 'offline' : 'connecting')
  })

  return () => source.close()
}

// ---- 插件（安装 / 授权 / 配置 / 同步 / 版本） ----
//
// 契约见 docs/PLUGIN-LIFECYCLE.md。三条约束直接影响界面设计：
//  1. 声明了 network/file-read/file-write/clipboard/process 的插件**不会自动启动**，
//     状态停在 PermissionRequired，必须由用户在这里授权。
//  2. 升级/回滚/卸载会先排空再换文件，因此这几个按钮可能要等十几秒。
//  3. 同步是长任务，正确的入口是 sync-async（进任务队列），而不是阻塞的 /sync。

export type PluginState =
  | 'Discovered' | 'Starting' | 'Running' | 'Degraded' | 'Failed'
  | 'Stopped' | 'NeedsConfiguration' | 'Disabled' | 'PermissionRequired' | 'Draining'

export const pluginPermissionLabels: Record<string, string> = {
  network: '联网',
  'file-read': '读取本地文件',
  'file-write': '写入本地文件',
  clipboard: '剪贴板',
  process: '启动子进程',
}

export const pluginStateLabels: Record<PluginState, string> = {
  Discovered: '已发现',
  Starting: '启动中',
  Running: '运行中',
  Degraded: '降级',
  Failed: '失败',
  Stopped: '已停止',
  NeedsConfiguration: '待配置',
  Disabled: '已停用',
  PermissionRequired: '待授权',
  Draining: '排空中',
}

export interface PluginStatus {
  id: string
  name: string
  version: string
  publisher: string
  state: PluginState
  directory: string
  capabilities: string[]
  health: string | null
  lastError: string | null
  enabled: boolean
  activeVersion: string | null
  installedVersions: string[]
  grantedPermissions: string[]
  missingPermissions: string[]
  integrity: string | null
  configured: boolean
  inFlightInvocations: number
  draining: boolean
}

export interface PluginCapabilityDeclaration {
  id: string
  kind: string
  name: string
  description?: string | null
  parameters?: string | null
  permissions?: string[]
  sideEffect?: string
}

export interface PluginTrustedKeyView {
  keyId: string
  pluginId: string
  algorithm: string
  fingerprint: string
  note?: string | null
  createdAt?: string
  updatedAt?: string
}

export interface PluginListResult {
  enabled: boolean
  rootPath: string
  plugins: PluginStatus[]
}

export interface PluginDetail {
  status: PluginStatus
  // 方言：plugin（自有协议）或 mcp（标准 MCP，能力来自 tools/list）。
  dialect: string
  capabilities: string[]
  declaredCapabilities: PluginCapabilityDeclaration[]
  // MCP 插件清单里通常不写能力，真正生效的是运行期发现的那批。
  discoveredCapabilities: PluginCapabilityDeclaration[]
  health: string | null
  lastError: string | null
  inFlightInvocations: number
  draining: boolean
  trustedKey: PluginTrustedKeyView | null
  config: string | null
  configSchema: string | null
  configRequired: string[]
  declaredPermissions: string[]
  allowedKnowledgeBaseIds: string[]
  maxDocuments: number
  limits: Record<string, number> | null
  // P1-4：并发 + 排队占用。未启动插件时为 null。
  concurrency: PluginConcurrency | null
  // D-1：沙箱实况。applied=false 表示插件没跑进沙箱（被拒或平台不支持）。
  isolation: PluginIsolation | null
}

/** 一个插件的沙箱实况（Windows AppContainer）。 */
export interface PluginIsolation {
  mode: string
  applied: boolean
  kind: string
  network: boolean
  capabilitySids: string[]
  writablePaths: string[]
  readablePaths: string[]
  detail: string | null
  short: string
}

/** 一个能力的并发占用（只有配了能力级上限才会出现在列表里）。 */
export interface PluginCapabilityConcurrency {
  capabilityId: string
  concurrencyLimit: number
  running: number
  queued: number
}

/** 一个插件的并发占用：运行数 / 上限、排队数与排队上限。 */
export interface PluginConcurrency {
  pluginId: string
  concurrencyLimit: number
  running: number
  queued: number
  queueLimit: number
  capabilities: PluginCapabilityConcurrency[]
}

export interface PluginAuditEntry {
  id: number
  occurredAt: string
  pluginId: string
  action: string
  capabilityId?: string | null
  actor: string
  outcome: string
  durationMs: number
  detail?: string | null
}

export interface PluginSyncStateView {
  scopeKey: string
  pluginId: string
  capabilityId: string
  knowledgeBaseId: string
  cursor?: string | null
  nextSyncAfterSeconds?: number | null
  lastSyncedAt?: string | null
  lastStatus?: string | null
  lastError?: string | null
  updatedAt: string
}

export async function listPlugins(): Promise<PluginListResult> {
  const response = await fetch(`${apiBaseUrl}/api/plugins`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as PluginListResult
}

export async function getPlugin(pluginId: string): Promise<PluginDetail> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as PluginDetail
}

/** 所有已登记插件的并发/排队占用（含未启动的，便于排查“谁占满了”）。 */
export async function getPluginConcurrency(): Promise<PluginConcurrency[]> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/concurrency`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { plugins: PluginConcurrency[] }
  return body.plugins
}

/**
 * 重新扫描插件目录：新拷进来的文件夹会被发现并启动，已删除的会被清理。
 * 插件数量没有上限，这个接口就是「把文件夹拷进去 → 它就能用」的那一步。
 */
export async function rescanPlugins(): Promise<{ discovered: string[]; started: string[]; failed: string[]; removed: string[]; plugins: PluginStatus[] }> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/rescan`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as { discovered: string[]; started: string[]; failed: string[]; removed: string[]; plugins: PluginStatus[] }
}

/** 安装/升级插件包（ZIP）。后端按 manifest 版本号判断是首装还是升级。 */
export async function installPlugin(file: File): Promise<{ pluginId: string; version: string; state?: string; warnings?: string[] }> {
  const form = new FormData()
  form.append('file', file)

  const response = await fetch(`${apiBaseUrl}/api/plugins/install`, { method: 'POST', body: form })
  if (!response.ok) {
    // 安装失败会返回 errors 数组，逐条显示比只显示"安装失败"有用得多。
    const body = await response.json().catch(() => null) as { message?: string; errors?: string[] } | null
    const detail = body?.errors?.length ? `${body.message ?? '安装失败'}：${body.errors.join('；')}` : body?.message
    throw new Error(detail || `安装失败（${response.status}）`)
  }
  return await response.json() as { pluginId: string; version: string; state?: string; warnings?: string[] }
}

async function pluginOperation(pluginId: string, path: string, init?: RequestInit): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}${path}`, init)
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string; errors?: string[] } | null
    const detail = body?.errors?.length ? `${body.message ?? '操作失败'}：${body.errors.join('；')}` : body?.message
    throw new Error(detail || `操作失败（${response.status}）`)
  }
}

export function setPluginEnabled(pluginId: string, enabled: boolean): Promise<void> {
  return pluginOperation(pluginId, '/enabled', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled }),
  })
}

/** 保存配置。传 null 表示清空（后端要求显式 config 字段，这里已经包好）。 */
export function savePluginConfig(pluginId: string, config: unknown): Promise<void> {
  return pluginOperation(pluginId, '/config', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ config }),
  })
}

export function savePluginPermissions(pluginId: string, permissions: string[]): Promise<void> {
  return pluginOperation(pluginId, '/permissions', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ permissions }),
  })
}

/** 作用域：空数组表示不限制知识库；maxDocuments=0 表示跟随全局默认。 */
export function savePluginScope(pluginId: string, allowedKnowledgeBaseIds: string[], maxDocuments: number): Promise<void> {
  return pluginOperation(pluginId, '/scope', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ allowedKnowledgeBaseIds, maxDocuments }),
  })
}

export function restartPlugin(pluginId: string): Promise<void> {
  return pluginOperation(pluginId, '/restart', { method: 'POST' })
}

export function checkPluginHealth(pluginId: string): Promise<{ status?: string; message?: string }> {
  return fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}/health`, { method: 'POST' })
    .then(async response => {
      if (!response.ok) throw new Error(await getErrorMessage(response))
      return await response.json() as { status?: string; message?: string }
    })
}

/** 主动排空：不再接受新调用，等在途调用结束（最多 DrainTimeoutSeconds）。 */
export async function drainPlugin(pluginId: string): Promise<{ drained: boolean; inFlight: number }> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}/drain`, { method: 'POST' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as { drained: boolean; inFlight: number }
}

export function rollbackPlugin(pluginId: string): Promise<void> {
  return pluginOperation(pluginId, '/rollback', { method: 'POST' })
}

export async function uninstallPlugin(pluginId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}`, { method: 'DELETE' })
  if (!response.ok) throw new Error(await getErrorMessage(response))
}

/** 把一次同步排进任务队列，返回可跟踪的任务（在任务中心看进度、可取消）。 */
export async function syncPluginAsync(pluginId: string, capabilityId: string, knowledgeBaseId: string): Promise<JobRecord> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}/sync-async`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ capabilityId, knowledgeBaseId }),
  })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as JobRecord
}

export async function listPluginSyncStates(pluginId: string): Promise<PluginSyncStateView[]> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/${encodeURIComponent(pluginId)}/sync-state`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { states: PluginSyncStateView[] }
  return body.states
}

export async function listPluginAudit(pluginId: string | null, limit = 50): Promise<PluginAuditEntry[]> {
  const params = new URLSearchParams({ limit: String(limit) })
  const path = pluginId ? `/api/plugins/${encodeURIComponent(pluginId)}/audit` : '/api/plugins/audit'
  const response = await fetch(`${apiBaseUrl}${path}?${params.toString()}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { entries: PluginAuditEntry[] }
  return body.entries
}

export async function listTrustedKeys(): Promise<PluginTrustedKeyView[]> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/trusted-keys`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = await response.json() as { keys: PluginTrustedKeyView[] }
  return body.keys
}

export async function saveTrustedKey(keyId: string, pluginId: string, publicKeyPem: string, algorithm: string, note?: string): Promise<PluginTrustedKeyView> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/trusted-keys/${encodeURIComponent(keyId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pluginId, publicKeyPem, algorithm, note: note ?? null }),
  })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(body?.message || `保存失败（${response.status}）`)
  }
  return await response.json() as PluginTrustedKeyView
}

export async function deleteTrustedKey(keyId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/plugins/trusted-keys/${encodeURIComponent(keyId)}`, { method: 'DELETE' })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { message?: string } | null
    throw new Error(body?.message || `删除失败（${response.status}）`)
  }
}

