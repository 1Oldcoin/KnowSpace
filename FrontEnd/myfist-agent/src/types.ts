export type ViewName = 'overview' | 'materials' | 'notes' | 'assistant' | 'companions' | 'avatar' | 'topics' | 'jobs' | 'plugins' | 'mcp' | 'settings'

export type MaterialKind = 'pdf' | 'html' | 'document' | 'image' | 'video' | 'audio' | 'note'

export interface MaterialItem {
  id: string
  title: string
  fileName?: string
  kind: MaterialKind
  category: string
  detail: string
  updated: string
  status: 'ready' | 'processing' | 'failed' | 'demo'
  color: string
  iconColor: string
  progress?: number
  processingStage?: string
  summary?: string
  error?: string
  topicId?: string
}

export interface NoteSourceItem {
  documentId: string
  fileName: string
  chunkIndex: number
  similarity: number
  snippet?: string | null
}

export interface NoteItem {
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
  date: string
  topicId?: string | null
  sources?: NoteSourceItem[] | null
  sourceSessionId?: string | null
}

export interface NoteDraft {
  title: string
  content: string
  category: string
  color: string
  tags: string[]
  topicId?: string | null
}

export interface AssistantMessage {
  id: string
  role: 'assistant' | 'user'
  text: string
  time: string
  citations?: Array<{
    documentId: string
    fileName: string
    similarity: number
    rank?: number
    chunkId?: number
    chunkIndex?: number
    snippet?: string | null
    /** 音视频切片在媒体中的起始秒数；普通文档为 null。点击引用可从这里开始播放。 */
    mediaStartSeconds?: number | null
  }>
  toolCalls?: Array<{ name: string; arguments: string; resultPreview: string; succeeded: boolean; elapsedMilliseconds: number }>
  /** 本轮回答的检索上下文（索引 / 向量模型 / 降级提示）；历史会话重开时也会恢复。 */
  retrieval?: {
    indexId?: string | null
    embeddingSpecName?: string | null
    warning?: string | null
    legacyIndex: boolean
  }
}
