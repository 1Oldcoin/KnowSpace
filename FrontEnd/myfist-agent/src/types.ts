export type ViewName = 'overview' | 'materials' | 'notes' | 'assistant' | 'companions' | 'avatar' | 'topics' | 'settings'

export type MaterialKind = 'pdf' | 'html' | 'document' | 'image' | 'video' | 'note'

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
}

export interface NoteDraft {
  title: string
  content: string
  category: string
  color: string
  tags: string[]
}

export interface AssistantMessage {
  id: string
  role: 'assistant' | 'user'
  text: string
  time: string
  citations?: Array<{ documentId: string; fileName: string; similarity: number }>
  toolCalls?: Array<{ name: string; arguments: string; resultPreview: string; succeeded: boolean; elapsedMilliseconds: number }>
}
