<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { ArrowDown, BookOpen, Check, CornerDownLeft, Download, FileText, FolderOpen, Globe, MessageCircle, MoreHorizontal, NotebookPen, Pencil, Play, Plus, Send, Sparkles, Trash2, UserRound, Wrench } from 'lucide-vue-next'
import type { ChatToolOptions, KnowledgeChatSession } from '../services/api'
import type { AssistantMessage } from '../types'

const props = defineProps<{
  messages: AssistantMessage[]
  busy: boolean
  apiOnline: boolean
  sessions: KnowledgeChatSession[]
  activeSessionId: string
  sessionsLoading: boolean
  /** 单轮工具开关，由 App 统一持久化。 */
  toolOptions: Required<ChatToolOptions>
  /** 全局总闸（Agent:EnableToolCalling）关闭时，工具开关不可用。 */
  toolsAvailable: boolean
}>()
const emit = defineEmits<{
  send: [message: string]
  newChat: []
  selectSession: [sessionId: string]
  renameSession: [sessionId: string]
  deleteSession: [sessionId: string]
  deleteSessions: [sessionIds: string[]]
  exportSession: [sessionId: string]
  saveAsNote: [message: AssistantMessage]
  playMedia: [payload: { documentId: string; fileName: string; seconds: number }]
  'update:toolOptions': [options: Required<ChatToolOptions>]
}>()
const draft = ref('')
const scrollAnchor = ref<HTMLElement | null>(null)
const isManagingSessions = ref(false)
const selectedSessionIds = ref<string[]>([])
const openSessionMenuId = ref('')
const allSessionsSelected = computed(() => props.sessions.length > 0 && selectedSessionIds.value.length === props.sessions.length)

function submit() {
  const value = draft.value.trim()
  if (!value || props.busy || props.sessionsLoading) return
  emit('send', value)
  draft.value = ''
}

function usePrompt(prompt: string) {
  draft.value = prompt
}

watch(() => [props.messages.length, props.activeSessionId], async () => {
  await nextTick()
  scrollAnchor.value?.scrollIntoView({ behavior: 'smooth' })
})

watch(() => props.sessions.map(session => session.id), sessionIds => {
  selectedSessionIds.value = selectedSessionIds.value.filter(sessionId => sessionIds.includes(sessionId))
  if (!sessionIds.includes(openSessionMenuId.value)) openSessionMenuId.value = ''
})

function sessionTime(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}

function toggleSessionManagement() {
  isManagingSessions.value = !isManagingSessions.value
  selectedSessionIds.value = []
  openSessionMenuId.value = ''
}

function toggleSessionSelection(sessionId: string) {
  selectedSessionIds.value = selectedSessionIds.value.includes(sessionId)
    ? selectedSessionIds.value.filter(id => id !== sessionId)
    : [...selectedSessionIds.value, sessionId]
}

function toggleAllSessions() {
  selectedSessionIds.value = allSessionsSelected.value ? [] : props.sessions.map(session => session.id)
}

function selectSession(sessionId: string) {
  if (isManagingSessions.value) {
    toggleSessionSelection(sessionId)
    return
  }
  emit('selectSession', sessionId)
}

function toggleSessionMenu(sessionId: string) {
  openSessionMenuId.value = openSessionMenuId.value === sessionId ? '' : sessionId
}

function requestSessionAction(action: 'rename' | 'delete' | 'export', sessionId: string) {
  openSessionMenuId.value = ''
  if (action === 'rename') emit('renameSession', sessionId)
  else if (action === 'delete') emit('deleteSession', sessionId)
  else emit('exportSession', sessionId)
}

function deleteSelectedSessions() {
  emit('deleteSessions', [...selectedSessionIds.value])
}

// 联网搜索工具名通常包含 web/search，用于在工具调用链上高亮。
function isWebSearchTool(name: string) {
  const normalized = name.toLowerCase()
  return normalized.includes('web') || normalized.includes('internet')
}

function hasWebSearchCall(calls: AssistantMessage['toolCalls']) {
  return (calls ?? []).some(call => isWebSearchTool(call.name))
}

// ---- 引用元数据 ----

type ChatCitation = NonNullable<AssistantMessage['citations']>[number]

/** 相似度按百分比展示；旧数据或异常值直接不显示，避免出现 NaN%。 */
function citationScore(similarity?: number) {
  if (typeof similarity !== 'number' || !Number.isFinite(similarity)) return ''
  return `${Math.round(Math.max(0, Math.min(1, similarity)) * 100)}%`
}

/** 排名优先用后端写入的 rank，旧数据缺失时退化为列表顺序。 */
function citationRank(citation: ChatCitation, index: number) {
  return citation.rank && citation.rank > 0 ? citation.rank : index + 1
}

/** 悬浮提示：文件名 + 切片序号 + 相关度 + 文本片段，用于快速核对答案依据。 */
function citationTooltip(citation: ChatCitation) {
  const parts = [citation.fileName]
  if (typeof citation.chunkIndex === 'number' && citation.chunkIndex >= 0) parts.push(`第 ${citation.chunkIndex + 1} 段`)
  if (hasMediaStart(citation)) parts.push(`从 ${mediaTimeLabel(citation.mediaStartSeconds)} 开始`)
  const score = citationScore(citation.similarity)
  if (score) parts.push(`相似度 ${score}`)
  return citation.snippet ? `${parts.join(' · ')}\n${citation.snippet}` : parts.join(' · ')
}

/**
 * 有没有可跳转的时间点。
 *
 * 必须判 null/undefined 而不是真假值：第一片切片从 `[00:00:00]` 开始，0 是合法时间点，
 * 用 `if (x)` 会把“从片头开始”当成“没有时间点”，整片引用就失去了跳转能力。
 */
function hasMediaStart(citation: ChatCitation): citation is ChatCitation & { mediaStartSeconds: number } {
  return typeof citation.mediaStartSeconds === 'number' && Number.isFinite(citation.mediaStartSeconds)
}

/** 秒数 → mm:ss / h:mm:ss。引用里的时间点是给人看的，不给小数。 */
function mediaTimeLabel(seconds?: number | null) {
  if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 0) return ''
  const total = Math.floor(seconds)
  const pad = (value: number) => value.toString().padStart(2, '0')
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(total % 60)}` : `${minutes}:${pad(total % 60)}`
}

/** 只有音视频切片才带时间点；普通文档的引用保持纯展示。 */
function openCitation(citation: ChatCitation) {
  if (!hasMediaStart(citation)) return
  emit('playMedia', {
    documentId: citation.documentId,
    fileName: citation.fileName,
    seconds: citation.mediaStartSeconds,
  })
}

// ---- 单轮工具开关 ----

const canUseTools = computed(() => props.toolsAvailable && props.apiOnline)

/** 父组件持有开关状态；这里只负责计算下一状态并通知更新。 */
function patchToolOptions(patch: Partial<ChatToolOptions>) {
  emit('update:toolOptions', { ...props.toolOptions, ...patch })
}

function toggleMaster() {
  if (!canUseTools.value) return
  const next = !props.toolOptions.enableToolCalling
  // 关闭工具时子开关一并置灰；重新开启时恢复为“只查知识库”的保守状态。
  patchToolOptions({ enableToolCalling: next, enableWebSearch: next ? props.toolOptions.enableWebSearch : false })
}

function toggleWebSearch() {
  if (!canUseTools.value || !props.toolOptions.enableToolCalling) return
  patchToolOptions({ enableWebSearch: !props.toolOptions.enableWebSearch })
}

function toggleFileAccess() {
  if (!canUseTools.value || !props.toolOptions.enableToolCalling) return
  patchToolOptions({ enableFileAccess: !props.toolOptions.enableFileAccess })
}

const toolHint = computed(() => {
  if (!props.toolsAvailable) return '服务端已关闭工具调用'
  if (!props.toolOptions.enableToolCalling) return '仅使用知识库资料回答'
  const enabled = [
    props.toolOptions.enableWebSearch ? '联网搜索' : '',
    props.toolOptions.enableFileAccess ? '本地文件' : '',
  ].filter(Boolean)
  return enabled.length ? `可调用：知识库 + ${enabled.join(' + ')}` : '可调用：知识库检索'
})

// ---- 存为笔记 ----

/** 只有已给出回答的消息才能存为笔记；空回答或还在流式输出时按钮不可用。 */
function canSaveAsNote(message: AssistantMessage) {
  return message.role === 'assistant' && !!message.text.trim() && !props.busy
}
</script>

<template>
  <section class="assistant-page">
    <header class="assistant-header"><div><div class="eyebrow"><span class="eyebrow-line"></span> YOUR LEARNING COPILOT</div><h1>AI 知识问答</h1><p class="welcome-copy">向你的学习搭子提问，探索资料里的新连接。</p></div><button class="button button-soft" :disabled="busy || sessionsLoading" @click="emit('newChat')"><Plus :size="16" /> 新对话</button></header>
    <div class="assistant-status"><span class="connection-dot" :class="{ online: apiOnline }"></span>{{ apiOnline ? '知识库与模型服务已连接' : '等待连接 API / PostgreSQL' }}<span class="status-divider"></span><BookOpen :size="14" /> 基于资料检索并附带引用</div>
    <div class="assistant-workspace">
      <aside class="assistant-session-list">
        <div class="assistant-session-heading"><strong>问答历史</strong><button class="tiny-icon-button" aria-label="新建对话" :disabled="busy || sessionsLoading" @click="emit('newChat')"><Plus :size="16" /></button></div>
        <div v-if="sessionsLoading" class="assistant-session-empty">正在读取对话…</div>
        <button v-for="session in sessions" :key="session.id" class="assistant-session-item" :class="{ selected: activeSessionId === session.id }" :disabled="busy || sessionsLoading" @click="emit('selectSession', session.id)">
          <span><MessageCircle :size="14" /><strong>{{ session.title }}</strong></span><small>{{ sessionTime(session.updatedAt) }}</small>
        </button>
        <div v-if="!sessionsLoading && sessions.length === 0" class="assistant-session-empty">还没有历史对话，点击“新对话”开始。</div>
      </aside>
      <div class="assistant-chat-column">
        <div class="chat-stage">
          <div v-if="messages.length === 0" class="chat-welcome">
            <div class="chat-orb"><Sparkles :size="24" /></div><div class="chat-welcome-kicker">LEARN SOMETHING NEW</div><h2>今天想弄懂什么？</h2><p>我可以陪你梳理概念、总结思路，或把一个问题拆开来看。</p>
            <div class="prompt-grid"><button @click="usePrompt('帮我用简单的例子解释一个复杂概念')"><span class="prompt-icon prompt-lilac"><Sparkles :size="16" /></span><span><strong>拆解复杂概念</strong><small>用例子讲清一个难点</small></span><CornerDownLeft :size="14" /></button><button @click="usePrompt('帮我设计一个本周的学习计划')"><span class="prompt-icon prompt-mint"><Check :size="16" /></span><span><strong>制定学习计划</strong><small>让目标变成可执行步骤</small></span><CornerDownLeft :size="14" /></button><button @click="usePrompt('帮我总结这段时间学习的重点')"><span class="prompt-icon prompt-peach"><FileText :size="16" /></span><span><strong>回顾学习重点</strong><small>把零散收获串联起来</small></span><CornerDownLeft :size="14" /></button></div>
          </div>
          <div v-else class="chat-messages">
            <div v-for="message in messages" :key="message.id" class="chat-message" :class="`message-${message.role}`"><div class="message-avatar"><Sparkles v-if="message.role === 'assistant'" :size="16" /><UserRound v-else :size="16" /></div><div class="message-content"><div class="message-meta"><strong>{{ message.role === 'assistant' ? '知间 AI' : '我' }}</strong><span>{{ message.time }}</span></div><p>{{ message.text }}</p><div v-if="message.toolCalls?.length" class="tool-trace-list"><span class="tool-trace-title"><Wrench :size="12" /> 工具调用</span><span v-if="hasWebSearchCall(message.toolCalls) && message.toolCalls?.some(call => isWebSearchTool(call.name) && call.succeeded)" class="tool-trace-web"><Globe :size="11" /> 已联网搜索</span><span v-for="(call, index) in message.toolCalls" :key="index" class="tool-trace-chip" :class="{ 'tool-trace-failed': !call.succeeded, 'tool-trace-web-chip': isWebSearchTool(call.name) }" :title="call.resultPreview"><Globe v-if="isWebSearchTool(call.name)" :size="10" />{{ call.name }} · {{ call.elapsedMilliseconds }}ms</span></div><div v-if="message.citations?.length" class="citation-list"><BookOpen :size="13" /><span class="citation-title">引用资料</span><span v-for="(citation, index) in message.citations" :key="`${citation.documentId}-${citation.chunkId ?? index}`" class="citation-chip" :class="{ 'citation-chip-playable': hasMediaStart(citation) }" :title="citationTooltip(citation)" @click="openCitation(citation)"><span class="citation-rank">{{ citationRank(citation, index) }}</span><span class="citation-name">{{ citation.fileName }}</span><span v-if="hasMediaStart(citation)" class="citation-time"><Play :size="8" />{{ mediaTimeLabel(citation.mediaStartSeconds) }}</span><span v-if="citationScore(citation.similarity)" class="citation-score">{{ citationScore(citation.similarity) }}</span></span></div><div v-if="message.retrieval?.warning || message.retrieval?.legacyIndex" class="citation-warning"><BookOpen :size="12" />{{ message.retrieval.warning || '本次检索使用旧版索引，结果可能不稳定。' }}</div><div v-if="message.role === 'assistant'" class="message-actions"><button type="button" class="message-action" :disabled="!canSaveAsNote(message)" title="把这一轮问答（含引用资料）存成学习笔记" @click="emit('saveAsNote', message)"><NotebookPen :size="12" /> 存为笔记</button></div></div></div>
            <div v-if="busy" class="chat-message message-assistant"><div class="message-avatar"><Sparkles :size="16" /></div><div class="typing-indicator"><i></i><i></i><i></i></div></div>
            <div ref="scrollAnchor"></div>
          </div>
        </div>
        <div class="chat-composer-wrap"><div class="chat-composer"><div class="composer-tools"><button type="button" class="composer-tool" :class="{ on: toolOptions.enableToolCalling && toolsAvailable, muted: !toolsAvailable }" :disabled="!canUseTools" :title="toolsAvailable ? '允许模型调用工具；关闭后只使用知识库资料回答' : '服务端已关闭工具调用'" @click="toggleMaster"><Wrench :size="12" /> 工具</button><button type="button" class="composer-tool" :class="{ on: toolOptions.enableToolCalling && toolOptions.enableWebSearch, muted: !toolOptions.enableToolCalling || !toolsAvailable }" :disabled="!canUseTools || !toolOptions.enableToolCalling" title="允许模型联网搜索知识库以外的实时信息" @click="toggleWebSearch"><Globe :size="12" /> 联网搜索</button><button type="button" class="composer-tool" :class="{ on: toolOptions.enableToolCalling && toolOptions.enableFileAccess, muted: !toolOptions.enableToolCalling || !toolsAvailable }" :disabled="!canUseTools || !toolOptions.enableToolCalling" title="允许模型查看授权目录、工作目录与资料原文" @click="toggleFileAccess"><FolderOpen :size="12" /> 本地文件</button><span class="composer-tool-hint">{{ toolHint }}</span></div><textarea v-model="draft" rows="1" placeholder="输入你的问题，按 Enter 发送" @keydown.enter.exact.prevent="submit"></textarea><div class="composer-bottom"><span class="composer-hint"><span><BookOpen :size="13" /> {{ toolOptions.enableToolCalling ? '工具增强问答' : '知识库问答' }}</span><span><ArrowDown :size="13" /> Enter 发送</span></span><button class="send-button" :disabled="!draft.trim() || busy || sessionsLoading" aria-label="发送" @click="submit"><Send :size="17" /></button></div></div><p class="assistant-disclaimer">回答优先基于已处理资料；开启工具后模型可检索知识库以外的信息，AI 生成内容可能不准确，请核对引用来源。</p></div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.assistant-workspace { min-height: 0; flex: 1; display: grid; grid-template-columns: 205px minmax(0, 1fr); gap: 16px; margin-top: 12px; }
.assistant-session-list { min-width: 0; max-height: calc(100vh - 250px); display: flex; flex-direction: column; gap: 5px; padding: 12px; overflow-y: auto; border: 1px solid #e8ede8; border-radius: 12px; background: #fff; }
.assistant-session-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 3px; color: #536258; font-size: 10px; }
.assistant-session-controls { display: flex; align-items: center; gap: 4px; }
.session-manage-button { padding: 4px 6px; border: 0; border-radius: 5px; color: #718078; background: transparent; font-size: 9px; }
.session-manage-button:hover:not(:disabled) { color: #397c65; background: #edf5ee; }
.assistant-session-bulk-toolbar { display: flex; align-items: center; gap: 5px; padding: 5px 2px; color: #89958d; font-size: 8px; }
.assistant-session-bulk-toolbar button { padding: 3px 5px; border: 0; border-radius: 5px; color: #397c65; background: #edf5ee; font-size: inherit; }
.assistant-session-bulk-toolbar .session-delete-selected { margin-left: auto; color: #a54848; background: #f9eded; }
.assistant-session-bulk-toolbar button:disabled { opacity: .45; }
.assistant-session-row { position: relative; min-width: 0; display: flex; align-items: center; gap: 2px; border-radius: 8px; }
.assistant-session-row.selected { color: #397c65; background: #edf5ee; }
.assistant-session-select { flex: none; display: flex; align-items: center; padding-left: 7px; }
.assistant-session-select input { width: 13px; height: 13px; accent-color: #397c65; }
.assistant-session-item { min-width: 0; flex: none; display: flex; flex-direction: column; gap: 5px; padding: 8px; border: 0; border-radius: 8px; color: #75817b; background: transparent; text-align: left; }
.assistant-session-item:hover:not(:disabled), .assistant-session-row.selected .assistant-session-item { color: #397c65; }
.assistant-session-item > span { min-width: 0; display: flex; align-items: center; gap: 7px; }
.assistant-session-item strong { overflow: hidden; color: inherit; font-size: 9px; font-weight: 500; text-overflow: ellipsis; white-space: nowrap; }
.assistant-session-item small { padding-left: 21px; color: #a4aea6; font-size: 8px; }
.assistant-session-actions { position: relative; flex: none; }
.assistant-session-menu { position: absolute; z-index: 5; top: calc(100% - 3px); right: 2px; min-width: 126px; display: flex; flex-direction: column; gap: 2px; padding: 5px; border: 1px solid #e8ede8; border-radius: 8px; background: #fff; box-shadow: 0 8px 24px #304a3a1a; }
.assistant-session-menu button { display: flex; align-items: center; gap: 7px; padding: 7px; border: 0; border-radius: 5px; color: #536258; background: transparent; text-align: left; white-space: nowrap; font-size: 9px; }
.assistant-session-menu button:hover { color: #397c65; background: #edf5ee; }
.assistant-session-menu .session-menu-danger { color: #a54848; }
.assistant-session-menu .session-menu-danger:hover { background: #f9eded; }
.assistant-session-empty { padding: 8px 3px; color: #9aa59e; font-size: 9px; line-height: 1.6; }
.assistant-chat-column { min-width: 0; min-height: 0; display: flex; flex-direction: column; }
.assistant-chat-column .chat-stage { min-height: 0; flex: 1; }
.assistant-chat-column .chat-messages { width: 100%; max-height: 55vh; }
.composer-tools { display: flex; align-items: center; flex-wrap: wrap; gap: 5px; margin-bottom: 7px; }
.composer-tool { display: inline-flex; align-items: center; gap: 4px; padding: 3px 7px; border: 1px solid #e3eae4; border-radius: 999px; color: #85938b; background: #fbfcfb; font-size: 9px; line-height: 1.4; cursor: pointer; transition: color .15s, border-color .15s, background .15s; }
.composer-tool:hover:not(:disabled) { color: #397c65; border-color: #bcd6c4; background: #f2f8f3; }
.composer-tool.on { color: #2f7357; border-color: #a8cdb5; background: #e9f4ec; font-weight: 600; }
.composer-tool.muted { opacity: .5; }
.composer-tool:disabled { cursor: not-allowed; }
.composer-tool-hint { margin-left: auto; color: #9aa59e; font-size: 8px; }
.message-actions { display: flex; align-items: center; gap: 6px; margin-top: 7px; }
.message-action {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 8px;
  border: 1px solid #e3eae4;
  border-radius: 999px;
  color: #85938b;
  background: #fbfcfb;
  font-size: 9px;
  line-height: 1.4;
  cursor: pointer;
  transition: color .15s, border-color .15s, background .15s;
}
.message-action:hover:not(:disabled) { color: #397c65; border-color: #bcd6c4; background: #f2f8f3; }
.message-action:disabled { opacity: .45; cursor: not-allowed; }

@media (max-width: 700px) {
  .assistant-workspace { grid-template-columns: minmax(0, 1fr); gap: 10px; }
  .assistant-session-list { max-height: 150px; }
  .assistant-chat-column { min-height: 48vh; }
}
</style>
