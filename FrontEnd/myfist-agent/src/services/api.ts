// 默认同源：发布后前端由内嵌 Kestrel 直接托管；Vite 开发服务器通过 vite.config.ts 的 /api 代理转发。
// 如需连接其他主机，可设置 VITE_API_BASE_URL（例如 http://192.168.1.10:5056）。
const apiBaseUrl = (import.meta.env.VITE_API_BASE_URL || '').replace(/\/+$/, '')

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
}

export interface NoteInput {
  title: string
  content: string
  category?: string
  color?: string
  tags?: string[]
  sourceDocumentId?: string | null
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
  const result = await response.json() as { answer?: string; citations?: Citation[]; toolCalls?: ToolCallTrace[]; toolCallingEnabled?: boolean; webSearchEnabled?: boolean; fileAccessEnabled?: boolean }
  return {
    answer: result.answer || '模型暂时没有返回文本内容。',
    citations: result.citations || [],
    toolCalls: result.toolCalls || [],
    toolCallingEnabled: result.toolCallingEnabled,
    webSearchEnabled: result.webSearchEnabled,
    fileAccessEnabled: result.fileAccessEnabled,
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

export interface Citation {
  documentId: string
  fileName: string
  similarity: number
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

export async function listNotes(knowledgeBaseId: string, query?: string, category?: string): Promise<NoteRecord[]> {
  const params = new URLSearchParams()
  if (query?.trim()) params.set('query', query.trim())
  if (category?.trim()) params.set('category', category.trim())
  const suffix = params.size ? `?${params.toString()}` : ''
  const response = await fetch(`${apiBaseUrl}/api/knowledge-bases/${encodeURIComponent(knowledgeBaseId)}/notes${suffix}`)
  if (!response.ok) throw new Error(await getErrorMessage(response))
  return await response.json() as NoteRecord[]
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
