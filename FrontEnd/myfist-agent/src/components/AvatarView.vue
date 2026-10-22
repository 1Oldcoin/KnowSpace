<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import type { Component } from 'vue'
import {
  AlertTriangle,
  Check,
  Fingerprint,
  Inbox,
  LoaderCircle,
  MessageCircle,
  Paperclip,
  Plus,
  RefreshCw,
  Search,
  Send,
  Sparkles,
  Trash2,
} from 'lucide-vue-next'
import {
  clearAvatarInsights,
  confirmAvatarInsight,
  createAvatarChatSession,
  deleteAvatarChatSession,
  deleteAvatarMemory,
  feedAvatar,
  getAvatarIngestion,
  getAvatarOverview,
  listAvatarChatMessages,
  listAvatarInsightCandidates,
  listAvatarMemories,
  migrateAvatarFragments,
  reindexAvatarMemories,
  saveAvatarProfile,
  sendAvatarChat,
  submitAvatarFeedback,
  uploadAvatarFeedFile,
  type AvatarChatMessage,
  type AvatarChatSession,
  type AvatarFeedbackType,
  type AvatarInsight,
  type AvatarInsightCandidate,
  type AvatarMemory,
  type AvatarOverview,
} from '../services/api'

const props = defineProps<{ apiOnline: boolean }>()
const emit = defineEmits<{ notice: [message: string] }>()

type TabKey = 'feed' | 'ask'

const tabs: { key: TabKey; label: string; icon: Component; hint: string }[] = [
  { key: 'feed', label: '投喂', icon: Inbox, hint: '随手写，AI 帮我整理' },
  { key: 'ask', label: '问答', icon: MessageCircle, hint: '如果是我，我会怎么做' },
]

const overview = ref<AvatarOverview | null>(null)
const activeTab = ref<TabKey>('feed')
const loading = ref(false)
const feeding = ref(false)
const asking = ref(false)
const errorMessage = ref('')
const noticeMessage = ref('')
const fileInput = ref<HTMLInputElement | null>(null)

const feedText = ref('')
const feedDate = ref(today())
const showDate = ref(false)
const feedPane = ref<HTMLElement | null>(null)
const memories = ref<AvatarMemory[]>([])
const memoryQuery = ref('')
const memorySearching = ref(false)
const reindexing = ref(false)

const profileForm = reactive({
  displayName: '',
  headline: '',
})

const sessions = ref<AvatarChatSession[]>([])
const activeSessionId = ref<string | null>(null)
const messages = ref<AvatarChatMessage[]>([])
const question = ref('')
const messagePane = ref<HTMLElement | null>(null)
const feedbackTarget = ref<number | null>(null)
const feedbackSending = ref<number | null>(null)
const feedbackDone = ref<Record<number, string>>({})

const feedbackReasons = [
  { value: 'fact_error', label: '记错事实' },
  { value: 'judgment_mismatch', label: '判断不符' },
  { value: 'over_inference', label: '过度推断' },
  { value: 'tone_mismatch', label: '语气不对' },
  { value: 'relationship_error', label: '关系理解错误' },
  { value: 'time_error', label: '时间错误' },
  { value: 'missing_memory', label: '漏掉重要记忆' },
  { value: 'other', label: '其它' },
]

const canEdit = computed(() => props.apiOnline && !feeding.value && !reindexing.value)
const readiness = computed(() => overview.value?.readinessScore ?? 0)
const undigested = computed(() => overview.value?.undigestedCount ?? 0)
const memoryCount = computed(() => overview.value?.memoryCount ?? 0)
const pendingVectors = computed(() => overview.value?.pendingVectorCount ?? 0)
const insightCount = computed(() => overview.value?.insightCount ?? 0)
const chatCount = computed(() => overview.value?.chatSessionCount ?? 0)
const feedEntries = computed(() => overview.value?.feedEntries ?? [])
const insights = computed(() => overview.value?.insights ?? [])
const candidates = ref<AvatarInsightCandidate[]>([])
const insightGroups = computed(() => {
  const groups: { dimension: string; items: AvatarInsight[] }[] = []
  for (const insight of insights.value) {
    const existing = groups.find(group => group.dimension === insight.dimensionLabel)
    if (existing) existing.items.push(insight)
    else groups.push({ dimension: insight.dimensionLabel, items: [insight] })
  }
  return groups
})
const activeSession = computed(() => sessions.value.find(item => item.id === activeSessionId.value) ?? null)
const hasMaterial = computed(() => memoryCount.value > 0 || insightCount.value > 0)
const canAsk = computed(() => props.apiOnline && !asking.value && hasMaterial.value && question.value.trim().length > 0)
const visibleMemories = computed(() => memories.value)

onMounted(() => { void load() })

function today() {
  const now = new Date()
  const offset = now.getTimezoneOffset()
  return new Date(now.getTime() - offset * 60_000).toISOString().slice(0, 10)
}

function toIsoDate(value: string) {
  if (!value) return undefined
  const parsed = new Date(`${value}T09:00:00`)
  return Number.isNaN(parsed.getTime()) ? undefined : parsed.toISOString()
}

function formatDate(value: string) {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleDateString('zh-CN')
}

function formatDateTime(value: string) {
  const parsed = new Date(value)
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '操作失败，请稍后重试。'
}

function flash(message: string) {
  noticeMessage.value = message
  emit('notice', message)
  window.setTimeout(() => { if (noticeMessage.value === message) noticeMessage.value = '' }, 4000)
}

async function load() {
  loading.value = true
  errorMessage.value = ''
  try {
    const data = await getAvatarOverview()
    overview.value = data
    sessions.value = data.chatSessions
    memories.value = data.recentMemories
    candidates.value = await listAvatarInsightCandidates().catch(() => [])
    profileForm.displayName = data.profile.displayName
    profileForm.headline = data.profile.headline
    if (!activeSessionId.value && data.chatSessions.length) {
      activeSessionId.value = data.chatSessions[0]?.id ?? null
      if (activeSessionId.value) await loadMessages(activeSessionId.value)
    }
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    loading.value = false
  }
}

// ---- 投喂 ----

async function submitFeed() {
  if (!canEdit.value) return
  const text = feedText.value.trim()
  if (!text) {
    errorMessage.value = '先写一点内容，再喂给我。'
    return
  }

  feeding.value = true
  errorMessage.value = ''
  try {
    const result = await feedAvatar(text, showDate.value ? toIsoDate(feedDate.value) : undefined)
    feedText.value = ''
    if (result.warning) errorMessage.value = result.warning
    if (result.ingestionId) {
      await waitForIngestion(result.ingestionId)
    } else {
      await load()
    }
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    feeding.value = false
  }
}

/** 后台整理是异步的：轮询任务状态，完成后再刷新记忆列表。 */
async function waitForIngestion(ingestionId: string) {
  for (let attempt = 0; attempt < 80; attempt += 1) {
    try {
      const task = await getAvatarIngestion(ingestionId)
      if (['completed', 'partial', 'failed'].includes(task.status)) {
        await load()
        if (task.status === 'failed') {
          errorMessage.value = task.error || '这次整理没有完成，原文已经保存。'
        } else {
          flash(task.status === 'partial'
            ? `整理完成（部分兜底）：新增 ${task.memoryCount} 条记忆，${task.insightCount} 条候选洞察。`
            : `整理完成：新增 ${task.memoryCount} 条记忆，${task.insightCount} 条候选洞察。`)
        }
        return
      }
    } catch (error) {
      errorMessage.value = errorText(error)
      return
    }
    await new Promise(resolve => window.setTimeout(resolve, 1500))
  }
  flash('还在后台整理，稍后点“刷新”就能看到结果。')
}

function pickFile() {
  fileInput.value?.click()
}

async function onFilePicked(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !canEdit.value) return
  if (file.size > 10 * 1024 * 1024) {
    errorMessage.value = '文件不能超过 10 MB。'
    return
  }

  feeding.value = true
  errorMessage.value = ''
  try {
    const result = await uploadAvatarFeedFile(file, showDate.value ? feedDate.value : undefined)
    if (result.warning) errorMessage.value = result.warning
    if (result.ingestionId) {
      await waitForIngestion(result.ingestionId)
    } else {
      await load()
    }
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    feeding.value = false
  }
}

async function searchMemories() {
  if (memorySearching.value) return
  memorySearching.value = true
  errorMessage.value = ''
  try {
    memories.value = await listAvatarMemories(memoryQuery.value)
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    memorySearching.value = false
  }
}

function resetMemorySearch() {
  memoryQuery.value = ''
  memories.value = overview.value?.recentMemories ?? []
}

async function removeMemory(memory: AvatarMemory) {
  if (!canEdit.value) return
  try {
    await deleteAvatarMemory(memory.id)
    memories.value = memories.value.filter(item => item.id !== memory.id)
    await load()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function reindex(all: boolean) {
  if (!canEdit.value) return
  reindexing.value = true
  errorMessage.value = ''
  try {
    const result = await reindexAvatarMemories(all)
    flash(result.warning
      ? `向量化暂时不可用：${result.warning}`
      : `向量化完成：${result.indexed} 条，仍有 ${result.pending} 条待处理。`)
    await load()
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    reindexing.value = false
  }
}

async function migrateLegacy() {
  if (!canEdit.value) return
  feeding.value = true
  errorMessage.value = ''
  try {
    const result = await migrateAvatarFragments()
    if (result.warning) errorMessage.value = result.warning
    if (result.ingestionId) {
      await waitForIngestion(result.ingestionId)
    } else {
      await load()
    }
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    feeding.value = false
  }
}

async function wipeInsights() {
  if (!canEdit.value) return
  try {
    await clearAvatarInsights()
    flash('人格洞察已清空，记忆仍然保留。')
    await load()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function confirmCandidate(candidate: AvatarInsightCandidate) {
  if (!canEdit.value) return
  try {
    await confirmAvatarInsight(candidate.id)
    flash('已确认，这条会进入稳定人格。')
    await load()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

function vectorLabel(state: string) {
  if (state === 'ready') return '已向量化'
  if (state === 'failed') return '向量化失败'
  return '待向量化'
}

async function scrollFeed() {
  await nextTick()
  if (feedPane.value) feedPane.value.scrollTop = feedPane.value.scrollHeight
}

function handleFeedKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) {
    event.preventDefault()
    void submitFeed()
  }
}

async function submitIdentity() {
  if (!canEdit.value) return
  const current = overview.value?.profile
  try {
    await saveAvatarProfile({
      displayName: profileForm.displayName.trim(),
      headline: profileForm.headline.trim(),
      personality: current?.personality ?? '',
      values: current?.values ?? '',
      strengths: current?.strengths ?? '',
      weaknesses: current?.weaknesses ?? '',
      skills: current?.skills ?? '',
      resources: current?.resources ?? '',
      constraints: current?.constraints ?? '',
      longTermGoals: current?.longTermGoals ?? '',
      riskPreference: current?.riskPreference ?? 'balanced',
    })
    flash('称呼已保存。')
    await load()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

// ---- 问答 ----

async function loadMessages(sessionId: string) {
  try {
    messages.value = await listAvatarChatMessages(sessionId)
    await scrollToBottom()
  } catch (error) {
    messages.value = []
    errorMessage.value = errorText(error)
  }
}

async function selectSession(session: AvatarChatSession) {
  activeSessionId.value = session.id
  await loadMessages(session.id)
}

async function newSession() {
  if (!props.apiOnline) return
  try {
    const session = await createAvatarChatSession()
    sessions.value = [session, ...sessions.value]
    activeSessionId.value = session.id
    messages.value = []
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function removeSession(session: AvatarChatSession) {
  try {
    await deleteAvatarChatSession(session.id)
    sessions.value = sessions.value.filter(item => item.id !== session.id)
    if (activeSessionId.value === session.id) {
      activeSessionId.value = sessions.value[0]?.id ?? null
      messages.value = []
      if (activeSessionId.value) await loadMessages(activeSessionId.value)
    }
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function scrollToBottom() {
  await nextTick()
  if (messagePane.value) messagePane.value.scrollTop = messagePane.value.scrollHeight
}

async function ask() {
  if (!canAsk.value) return
  const text = question.value.trim()
  question.value = ''
  asking.value = true
  errorMessage.value = ''

  const pendingId = -Date.now()
  messages.value = [
    ...messages.value,
    { id: pendingId, role: 'user', content: text, citations: [], createdAt: new Date().toISOString() },
  ]
  await scrollToBottom()

  try {
    const reply = await sendAvatarChat(activeSessionId.value, text)
    activeSessionId.value = reply.sessionId
    messages.value = [
      ...messages.value,
      {
        id: reply.chatMessageId || pendingId + 1,
        role: 'assistant',
        content: reply.answer,
        citations: reply.citations,
        createdAt: new Date().toISOString(),
      },
    ]
    sessions.value = await refreshSessions(reply.sessionId)
    await scrollToBottom()
  } catch (error) {
    messages.value = messages.value.filter(item => item.id !== pendingId)
    errorMessage.value = errorText(error)
  } finally {
    asking.value = false
  }
}

// ---- 回答反馈 ----

async function sendFeedback(message: AvatarChatMessage, type: AvatarFeedbackType, reason?: string) {
  if (message.id <= 0 || feedbackSending.value === message.id) return
  feedbackSending.value = message.id
  errorMessage.value = ''
  try {
    await submitAvatarFeedback({
      chatMessageId: message.id,
      memoryIds: message.citations.map(citation => citation.memoryId),
      feedbackType: type,
      reasonCode: reason,
    })
    feedbackDone.value = {
      ...feedbackDone.value,
      [message.id]: type === 'like' ? '已记为“像我”' : '已记录，我会据此修正',
    }
    feedbackTarget.value = null
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    feedbackSending.value = null
  }
}

async function correctMessage(message: AvatarChatMessage) {
  const text = window.prompt('正确的内容是什么？我会把它当成新的投喂，修正相关记忆。')
  if (!text?.trim()) return
  if (message.id <= 0 || feedbackSending.value === message.id) return

  feedbackSending.value = message.id
  errorMessage.value = ''
  try {
    await submitAvatarFeedback({
      chatMessageId: message.id,
      memoryIds: message.citations.map(citation => citation.memoryId),
      feedbackType: 'correction',
      reasonCode: 'fact_error',
      correctedText: text.trim(),
    })
    feedbackDone.value = { ...feedbackDone.value, [message.id]: '已提交纠正，正在后台修订记忆' }
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    feedbackSending.value = null
  }
}

async function refreshSessions(keepId: string) {
  try {
    const data = await getAvatarOverview()
    overview.value = data
    return data.chatSessions
  } catch {
    return sessions.value.map(item => (item.id === keepId ? { ...item, updatedAt: new Date().toISOString() } : item))
  }
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void ask()
  }
}
</script>

<template>
  <section class="avatar-page">
    <header class="avatar-heading">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> YOUR DIGITAL TWIN</div>
        <h1>我的分身</h1>
        <p>
          随手写，或者丢一个文件进来。AI 会自己整理成记忆、归纳人格并向量化；不用选类型、不用写标题。
          这部分数据与知识库完全隔离，不会被知识库检索，也不会进入文档索引。
        </p>
      </div>
      <button class="button button-soft" :disabled="loading || feeding" @click="load">
        <LoaderCircle v-if="loading" :size="15" class="spin" /><RefreshCw v-else :size="15" /> 刷新
      </button>
    </header>

    <div class="avatar-readiness">
      <div class="avatar-readiness-head">
        <div class="avatar-readiness-icon"><Fingerprint :size="20" /></div>
        <div>
          <strong>分身完备度 {{ readiness }}%</strong>
          <small>
            {{ overview?.profile.displayName || '还没有称呼' }}
            · {{ overview?.profile.headline || '还没有一句话定位' }}
          </small>
        </div>
        <div class="avatar-counts">
          <span><b>{{ memoryCount }}</b>记忆</span>
          <span><b>{{ pendingVectors }}</b>待向量化</span>
          <span><b>{{ insightCount }}</b>洞察</span>
          <span><b>{{ chatCount }}</b>对话</span>
        </div>
      </div>
      <div class="avatar-progress"><span :style="{ width: `${Math.max(2, readiness)}%` }"></span></div>
      <ul v-if="overview?.missingInputs.length" class="avatar-missing">
        <li v-for="item in overview.missingInputs" :key="item"><AlertTriangle :size="11" /> {{ item }}</li>
      </ul>
    </div>

    <div v-if="errorMessage" class="avatar-alert avatar-alert-error" role="alert">
      {{ errorMessage }}<button aria-label="关闭" @click="errorMessage = ''">×</button>
    </div>
    <div v-if="noticeMessage" class="avatar-alert avatar-alert-success" role="status">{{ noticeMessage }}</div>

    <nav class="avatar-tabs" aria-label="分身模块">
      <button v-for="tab in tabs" :key="tab.key" :class="{ active: activeTab === tab.key }" @click="activeTab = tab.key">
        <component :is="tab.icon" :size="14" /> {{ tab.label }}
      </button>
    </nav>

    <!-- ============ 投喂 ============ -->
    <div v-if="activeTab === 'feed'" class="avatar-panel">
      <div class="avatar-feed-grid">
        <div class="avatar-feed-chat">
          <div class="avatar-panel-head">
            <h2>和分身聊聊你自己</h2>
            <p>想到什么就写什么：今天发生了什么、你怎么想、喜欢什么、和谁关系近。不用选类型、不用写标题，AI 会自动整理成记忆并向量化。</p>
          </div>

          <div ref="feedPane" class="avatar-feed-stream">
            <div v-if="!feedEntries.length" class="avatar-feed-empty">
              <Sparkles :size="18" />
              <p>还没有投喂记录。试着写一句：<br />“我写小说时习惯先想结尾，再倒推大纲。”</p>
            </div>
            <div v-for="entry in feedEntries" :key="entry.id" :class="['avatar-feed-entry', entry.role]">
              <div class="avatar-feed-bubble">{{ entry.content }}</div>
              <small>{{ formatDateTime(entry.createdAt) }}</small>
            </div>
            <div v-if="feeding" class="avatar-feed-entry assistant">
              <div class="avatar-feed-bubble pending"><LoaderCircle :size="13" class="spin" /> 正在读，也在记…</div>
            </div>
          </div>

          <div class="avatar-feed-composer">
            <textarea
              v-model="feedText"
              rows="3"
              maxlength="200000"
              placeholder="想到什么就写什么，比如：我遇到争执时一般先沉默几秒，再挑最关键的一句说。"
              @keydown="handleFeedKeydown"
            ></textarea>
            <div class="avatar-feed-tools">
              <button class="button button-soft" :disabled="!canEdit" @click="pickFile">
                <Paperclip :size="14" /> 导入文件
              </button>
              <input ref="fileInput" class="avatar-file-input" type="file" accept=".txt,.md,.json,.log,.csv" @change="onFilePicked" />
              <button :class="['avatar-date-toggle', { active: showDate }]" type="button" @click="showDate = !showDate">
                记录时间
              </button>
              <input v-if="showDate" v-model="feedDate" class="avatar-date-input" type="date" />
              <button class="button button-primary" :disabled="!canEdit || !feedText.trim()" @click="submitFeed">
                <LoaderCircle v-if="feeding" :size="15" class="spin" /><Send v-else :size="15" /> 投喂
              </button>
            </div>
          </div>
          <p class="avatar-hint">支持 .txt / .md / .json / .log / .csv，单文件 10 MB 内。长文件会自动分段整理，超出部分按原文保存。</p>
        </div>

        <div class="avatar-memory-panel">
          <div class="avatar-list-head avatar-memory-head">
            <h2>它记住的我</h2>
            <span>{{ memoryCount }} 条记忆 · {{ pendingVectors }} 条待向量化</span>
          </div>

          <div class="avatar-memory-search">
            <input v-model="memoryQuery" placeholder="搜一条记忆，例如：写作习惯" @keydown.enter.prevent="searchMemories" />
            <button class="avatar-icon-button" aria-label="搜索记忆" :disabled="memorySearching" @click="searchMemories">
              <LoaderCircle v-if="memorySearching" :size="13" class="spin" /><Search v-else :size="13" />
            </button>
            <button v-if="memoryQuery" class="avatar-icon-button" aria-label="清除搜索" @click="resetMemorySearch">×</button>
          </div>

          <div class="avatar-actions avatar-memory-actions">
            <button class="button button-soft" :disabled="!canEdit || !memoryCount" @click="reindex(false)">
              <RefreshCw :size="14" /> 补向量
            </button>
            <button class="button button-soft" :disabled="!canEdit || !memoryCount" @click="reindex(true)">
              <RefreshCw :size="14" /> 全部重向量化
            </button>
            <button v-if="undigested" class="button button-soft" :disabled="!canEdit" @click="migrateLegacy">
              <Sparkles :size="14" /> 整理旧素材（{{ undigested }}）
            </button>
          </div>

          <div v-if="!visibleMemories.length" class="avatar-empty">
            {{ memoryQuery
              ? '没有搜到相关记忆。'
              : (undigested ? `你还有 ${undigested} 段旧素材没整理，点上面的“整理旧素材”补成记忆。` : '还没有记忆。左边随手写一句就行，不用整理。') }}
          </div>
          <div v-else class="avatar-card-list avatar-memory-list">
            <article v-for="memory in visibleMemories" :key="memory.id" class="avatar-card">
              <div class="avatar-card-head">
                <span class="avatar-chip">{{ memory.memoryTypeLabel || memory.kindLabel }}</span>
                <span v-if="memory.lifecycleStatus !== 'active'" class="avatar-chip avatar-chip-muted">
                  {{ memory.lifecycleLabel }}
                </span>
                <span class="avatar-card-date">{{ formatDate(memory.occurredAt) }}</span>
                <span :class="['avatar-status', memory.vectorState === 'ready' ? 'done' : 'pending']">
                  {{ vectorLabel(memory.vectorState) }}
                </span>
                <button class="avatar-icon-button" aria-label="删除记忆" :disabled="!canEdit" @click="removeMemory(memory)">
                  <Trash2 :size="13" />
                </button>
              </div>
              <strong>{{ memory.title }}</strong>
              <p>{{ memory.summary }}</p>
              <div class="avatar-card-meta">
                <span>置信度 {{ memory.confidence }}%</span>
                <span v-if="memory.evidenceCount">原文证据 {{ memory.evidenceCount }} 条</span>
                <span v-if="memory.lastUsedAt">最近使用 {{ formatDate(memory.lastUsedAt) }}</span>
              </div>
              <div v-if="memory.tags.length || memory.people.length" class="avatar-card-foot">
                <span v-for="tag in memory.tags" :key="`tag-${tag}`" class="avatar-tag">{{ tag }}</span>
                <span v-for="person in memory.people" :key="`person-${person}`" class="avatar-tag avatar-tag-person">{{ person }}</span>
              </div>
            </article>
          </div>
        </div>
      </div>

      <div class="avatar-identity">
        <div class="avatar-list-head"><h2>我的称呼</h2><span>分身回答时会用第一人称，这里只影响称呼</span></div>
        <div class="avatar-form-grid">
          <label class="avatar-field"><span>称呼</span><input v-model="profileForm.displayName" maxlength="60" placeholder="例如：小林" /></label>
          <label class="avatar-field"><span>一句话定位</span><input v-model="profileForm.headline" maxlength="200" placeholder="例如：白天上班、晚上写网文的兼职作者" /></label>
        </div>
        <div class="avatar-actions">
          <button class="button button-soft" :disabled="!canEdit" @click="submitIdentity"><Check :size="15" /> 保存称呼</button>
        </div>
      </div>

      <div class="avatar-insights">
        <div class="avatar-stats-head">
          <h2>我对自己的人格总结</h2>
          <span>{{ insightCount }} 条洞察 · 投喂后自动更新</span>
        </div>
        <div class="avatar-actions">
          <button class="button button-soft" :disabled="!canEdit || !insightCount" @click="wipeInsights">
            <Trash2 :size="15" /> 清空洞察
          </button>
          <span class="avatar-hint">洞察由投喂内容自动归纳，只读取分身记忆，不会读取知识库文档。</span>
        </div>

        <div v-if="!insights.length" class="avatar-empty">
          还没有洞察。多喂几段内容，我会自动总结你的性格、价值观、习惯、语言风格与决策方式。
        </div>
        <div v-else class="avatar-insight-groups">
          <div v-for="group in insightGroups" :key="group.dimension" class="avatar-insight-group">
            <h3>{{ group.dimension }}</h3>
            <article v-for="insight in group.items" :key="insight.id" class="avatar-insight">
              <div class="avatar-insight-head">
                <strong>{{ insight.title }}</strong>
                <span class="avatar-confidence">
                  <span class="avatar-confidence-bar"><i :style="{ width: `${Math.min(100, insight.confidence)}%` }"></i></span>
                  {{ insight.confidence }}%
                </span>
              </div>
              <p>{{ insight.content }}</p>
              <div v-if="insight.evidence.length" class="avatar-card-foot">
                <span v-for="item in insight.evidence" :key="item" class="avatar-tag">{{ item }}</span>
              </div>
            </article>
          </div>
        </div>

        <div v-if="candidates.length" class="avatar-candidates">
          <div class="avatar-list-head">
            <h3>待确认的人格候选</h3>
            <span>证据还不够稳定，确认后才会进入上面的人格总结</span>
          </div>
          <article v-for="candidate in candidates" :key="candidate.id" class="avatar-insight avatar-candidate">
            <div class="avatar-insight-head">
              <strong>{{ candidate.title }}</strong>
              <span class="avatar-chip">{{ candidate.dimensionLabel }}</span>
              <span class="avatar-confidence">{{ candidate.confidence }}%</span>
            </div>
            <p>{{ candidate.content }}</p>
            <div class="avatar-actions">
              <span v-if="candidate.scope" class="avatar-hint">适用范围：{{ candidate.scope }}</span>
              <button class="button button-soft" :disabled="!canEdit" @click="confirmCandidate(candidate)">
                <Check :size="14" /> 确认这是我的稳定特征
              </button>
            </div>
          </article>
        </div>
      </div>
    </div>

    <!-- ============ 问答 ============ -->
    <div v-else class="avatar-panel avatar-chat">
      <aside class="avatar-chat-sessions">
        <div class="avatar-session-head">
          <h2>对话</h2>
          <button class="button button-soft" :disabled="!apiOnline" @click="newSession"><Plus :size="14" /> 新对话</button>
        </div>
        <div v-if="!sessions.length" class="avatar-empty">还没有对话。</div>
        <div v-else class="avatar-session-list">
          <div
            v-for="session in sessions"
            :key="session.id"
            :class="['avatar-session', { active: session.id === activeSessionId }]"
          >
            <button class="avatar-session-main" @click="selectSession(session)">
              <strong>{{ session.title }}</strong>
              <small>{{ formatDateTime(session.updatedAt) }}</small>
            </button>
            <button class="avatar-icon-button" aria-label="删除对话" @click="removeSession(session)"><Trash2 :size="12" /></button>
          </div>
        </div>
      </aside>

      <div class="avatar-chat-main">
        <div class="avatar-panel-head">
          <h2>{{ activeSession?.title || '问我一个问题' }}</h2>
          <p>分身会站在你的立场，用“我”来回答。它只依据你的记忆与人格洞察，记忆不足时会直说。</p>
        </div>

        <div v-if="!hasMaterial" class="avatar-empty">
          还没有素材，分身没有可模仿的东西。先到“投喂”里写几段日记或聊天记录，再回来问我。
        </div>

        <div v-else ref="messagePane" class="avatar-chat-messages">
          <div v-if="!messages.length" class="avatar-chat-hint">
            试试这样问：“这周又想断更了，我该怎么办？”“有人当面否定我，我会怎么反应？”
          </div>
          <div v-for="message in messages" :key="message.id" :class="['avatar-bubble', message.role]">
            <div class="avatar-bubble-body">{{ message.content }}</div>
            <div v-if="message.citations.length" class="avatar-bubble-citations">
              <span v-for="citation in message.citations" :key="`${citation.memoryId}-${citation.title}`" class="avatar-tag">
                {{ citation.kind ? citation.kind : '记忆' }} · {{ citation.title }}
              </span>
            </div>
            <div v-if="message.role === 'assistant' && message.id > 0" class="avatar-feedback">
              <template v-if="feedbackDone[message.id]">
                <span class="avatar-feedback-done">{{ feedbackDone[message.id] }}</span>
              </template>
              <template v-else>
                <button
                  class="avatar-feedback-button"
                  :disabled="feedbackSending === message.id"
                  @click="sendFeedback(message, 'like')"
                >像我</button>
                <button
                  class="avatar-feedback-button"
                  :disabled="feedbackSending === message.id"
                  @click="feedbackTarget = feedbackTarget === message.id ? null : message.id"
                >不像我</button>
                <button
                  class="avatar-feedback-button"
                  :disabled="feedbackSending === message.id"
                  @click="correctMessage(message)"
                >纠正</button>
              </template>
              <div v-if="feedbackTarget === message.id" class="avatar-feedback-reasons">
                <button
                  v-for="reason in feedbackReasons"
                  :key="reason.value"
                  class="avatar-feedback-reason"
                  :disabled="feedbackSending === message.id"
                  @click="sendFeedback(message, 'unlike', reason.value)"
                >{{ reason.label }}</button>
              </div>
            </div>
          </div>
          <div v-if="asking" class="avatar-bubble assistant pending">
            <div class="avatar-bubble-body"><LoaderCircle :size="13" class="spin" /> 正在以我的方式想这件事…</div>
          </div>
        </div>

        <div class="avatar-composer">
          <textarea
            v-model="question"
            rows="2"
            :disabled="!hasMaterial || asking"
            placeholder="问我：如果是你，你会怎么处理？"
            @keydown="handleKeydown"
          ></textarea>
          <button class="button button-primary" :disabled="!canAsk" @click="ask">
            <LoaderCircle v-if="asking" :size="15" class="spin" /><Send v-else :size="15" /> 提问
          </button>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.avatar-page { display: flex; flex-direction: column; gap: 16px; }
.avatar-heading { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; }
.avatar-heading h1 { margin: 0; color: #37453d; font-size: 26px; letter-spacing: -.4px; }
.avatar-heading p { max-width: 680px; margin: 8px 0 0; color: #94a098; font-size: 11px; line-height: 1.75; }

.avatar-readiness { padding: 16px 18px; border: 1px solid #e7ece7; border-radius: 13px; background: #fff; }
.avatar-readiness-head { display: flex; align-items: center; gap: 12px; }
.avatar-readiness-icon { display: grid; place-items: center; width: 38px; height: 38px; border-radius: 10px; color: #397c65; background: #eaf4ec; }
.avatar-readiness-head strong { display: block; color: #42544a; font-size: 13px; }
.avatar-readiness-head small { display: block; margin-top: 3px; color: #9aa69f; font-size: 10px; }
.avatar-counts { display: flex; gap: 14px; margin-left: auto; color: #8b988f; font-size: 9px; }
.avatar-counts b { display: block; color: #46584d; font-size: 15px; font-weight: 600; }
.avatar-progress { height: 6px; margin-top: 12px; overflow: hidden; border-radius: 4px; background: #eef2ee; }
.avatar-progress span { display: block; height: 100%; border-radius: 4px; background: linear-gradient(90deg, #7fb894, #397c65); transition: width .4s; }
.avatar-missing { display: grid; gap: 4px; margin: 10px 0 0; padding: 0; list-style: none; color: #a88a6f; font-size: 9px; }
.avatar-missing li { display: flex; align-items: flex-start; gap: 5px; }
.avatar-missing svg { flex: 0 0 auto; margin-top: 1px; }

.avatar-alert { padding: 10px 13px; border-radius: 9px; font-size: 10px; }
.avatar-alert-error { display: flex; justify-content: space-between; color: #a95f4f; border: 1px solid #f0ddd8; background: #fdf5f3; }
.avatar-alert-success { color: #40805f; border: 1px solid #d9ebdf; background: #f2f9f4; }
.avatar-alert button { border: 0; color: inherit; background: transparent; font-size: 14px; }

.avatar-tabs { display: inline-flex; gap: 4px; align-self: flex-start; padding: 4px; border: 1px solid #e8ede8; border-radius: 10px; background: #f7faf7; }
.avatar-tabs button { display: inline-flex; align-items: center; gap: 6px; height: 32px; padding: 0 18px; border: 0; border-radius: 7px; color: #83908a; background: transparent; font-size: 11px; }
.avatar-tabs button.active { color: #fff; background: #397c65; box-shadow: 0 3px 8px #397c6524; }

.avatar-panel { padding: 18px; border: 1px solid #e9ede8; border-radius: 13px; background: #fff; }
.avatar-panel-head h2 { margin: 0; color: #3f4d45; font-size: 13px; font-weight: 600; }
.avatar-panel-head p { margin: 5px 0 0; color: #9aa49e; font-size: 9px; line-height: 1.6; }

.avatar-feed-grid { display: grid; grid-template-columns: minmax(0, 1.02fr) minmax(0, .98fr); gap: 24px; }
.avatar-feed-form { min-width: 0; }
.avatar-feed-list { min-width: 0; }
.avatar-feed-chat, .avatar-memory-panel { min-width: 0; }

.avatar-feed-stream { display: flex; flex-direction: column; gap: 10px; margin-top: 14px; min-height: 240px; max-height: 440px; overflow-y: auto; padding-right: 4px; }
.avatar-feed-empty { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; min-height: 210px; border: 1px dashed #e6ebe5; border-radius: 10px; color: #a7b1aa; font-size: 10px; line-height: 1.8; text-align: center; }
.avatar-feed-empty svg { color: #9dc4aa; }
.avatar-feed-empty p { margin: 0; }
.avatar-feed-entry { display: flex; flex-direction: column; gap: 4px; max-width: 88%; }
.avatar-feed-entry.user { align-self: flex-end; align-items: flex-end; }
.avatar-feed-entry.assistant { align-self: flex-start; }
.avatar-feed-bubble { padding: 10px 13px; border-radius: 11px; font-size: 11px; line-height: 1.8; white-space: pre-wrap; word-break: break-word; }
.avatar-feed-entry.user .avatar-feed-bubble { color: #fff; background: #397c65; border-bottom-right-radius: 4px; }
.avatar-feed-entry.assistant .avatar-feed-bubble { color: #45564c; background: #f6f9f6; border: 1px solid #eaf0ea; border-bottom-left-radius: 4px; }
.avatar-feed-bubble.pending { display: flex; align-items: center; gap: 6px; color: #8b978f; font-size: 10px; }
.avatar-feed-entry small { color: #a6b0a9; font-size: 8px; }
.avatar-feed-composer { margin-top: 14px; padding: 10px; border: 1px solid #e5ebe4; border-radius: 11px; background: #fbfcfa; }
.avatar-feed-composer textarea { width: 100%; padding: 0; border: 0; outline: 0; resize: none; color: #4d5d54; background: transparent; font-size: 11px; font-family: inherit; line-height: 1.75; }
.avatar-feed-tools { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 8px; }
.avatar-feed-tools .button { height: 30px; font-size: 10px; }
.avatar-date-toggle { height: 30px; padding: 0 10px; border: 1px solid #e5ebe4; border-radius: 8px; color: #8b978f; background: #fff; font-size: 9px; }
.avatar-date-toggle.active { color: #397c65; border-color: #b9d8c3; background: #f2f9f4; }
.avatar-date-input { height: 30px; padding: 0 8px; border: 1px solid #e5ebe4; border-radius: 8px; color: #4d5d54; background: #fff; font-size: 10px; }
.avatar-memory-head { margin-top: 0; }
.avatar-memory-search { display: flex; align-items: center; gap: 4px; margin-top: 12px; padding: 0 6px 0 10px; border: 1px solid #e5ebe4; border-radius: 9px; background: #fbfcfa; }
.avatar-memory-search input { flex: 1; min-width: 0; padding: 9px 0; border: 0; outline: 0; color: #4d5d54; background: transparent; font-size: 10px; }
.avatar-memory-search .avatar-icon-button { margin-left: 0; }
.avatar-memory-actions { margin-top: 10px; }
.avatar-memory-list { max-height: 520px; }
.avatar-tag-person { color: #5f7f8f; background: #eef5f7; }

.avatar-form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 15px; }
.avatar-form-single { grid-template-columns: 1fr; }
.avatar-field { display: flex; flex-direction: column; gap: 5px; }
.avatar-field > span { color: #88958b; font-size: 9px; font-weight: 600; }
.avatar-field input, .avatar-field textarea, .avatar-field select {
  width: 100%;
  padding: 8px 9px;
  border: 1px solid #e5ebe4;
  border-radius: 8px;
  outline: 0;
  color: #4d5d54;
  background: #fbfcfa;
  font-size: 10px;
  font-family: inherit;
  resize: vertical;
}
.avatar-field input:focus, .avatar-field textarea:focus, .avatar-field select:focus { border-color: #a9c9b2; background: #fff; }
.avatar-field textarea { line-height: 1.75; }
.avatar-field-row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }

.avatar-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-top: 14px; }
.avatar-hint { margin: 8px 0 0; color: #a4afa8; font-size: 9px; line-height: 1.6; }
.avatar-file-input { display: none; }

.avatar-identity { margin-top: 22px; padding-top: 14px; border-top: 1px solid #f1f3f0; }

.avatar-list-head { display: flex; align-items: baseline; justify-content: space-between; margin-top: 18px; }
.avatar-list-head h2 { margin: 0; color: #46554c; font-size: 12px; font-weight: 600; }
.avatar-list-head span { color: #a4afa8; font-size: 9px; }
.avatar-card-list { display: flex; flex-direction: column; gap: 9px; margin-top: 10px; max-height: 620px; overflow-y: auto; padding-right: 2px; }
.avatar-card { padding: 11px 13px; border: 1px solid #edf0ec; border-radius: 10px; background: #fcfdfb; }
.avatar-card strong { display: block; margin-top: 6px; color: #45564c; font-size: 11px; }
.avatar-card p { margin: 5px 0 0; color: #8b978f; font-size: 9px; line-height: 1.7; display: -webkit-box; -webkit-line-clamp: 5; -webkit-box-orient: vertical; overflow: hidden; white-space: pre-wrap; }
.avatar-card-head { display: flex; align-items: center; gap: 7px; }
.avatar-card-date { color: #a6b0a9; font-size: 8px; }
.avatar-chip { padding: 2px 6px; border-radius: 5px; color: #4c8567; background: #eaf4ec; font-size: 8px; font-weight: 600; }
.avatar-status { padding: 2px 6px; border-radius: 5px; font-size: 8px; font-weight: 600; }
.avatar-status.done { color: #40805f; background: #eaf4ec; }
.avatar-status.pending { color: #a58a4f; background: #f8f3e9; }
.avatar-card-foot { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 8px; color: #a0aba4; font-size: 8px; }
.avatar-tag { padding: 2px 6px; border-radius: 5px; color: #7d8c83; background: #f1f4f1; font-size: 8px; }
.avatar-icon-button { margin-left: auto; display: grid; place-items: center; width: 22px; height: 22px; border: 0; border-radius: 6px; color: #adb7b1; background: transparent; }
.avatar-icon-button:hover { color: #a95f4f; background: #fdf1ee; }
.avatar-empty { margin-top: 10px; padding: 22px; border: 1px dashed #e6ebe5; border-radius: 10px; color: #a7b1aa; font-size: 9px; line-height: 1.7; text-align: center; }

.avatar-insights { margin-top: 22px; padding-top: 16px; border-top: 1px solid #f1f3f0; }
.avatar-stats-head { display: flex; align-items: baseline; justify-content: space-between; }
.avatar-stats-head h2 { margin: 0; color: #46554c; font-size: 12px; font-weight: 600; }
.avatar-stats-head span { color: #a4afa8; font-size: 9px; }
.avatar-insight-groups { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 14px; margin-top: 14px; }
.avatar-insight-group h3 { margin: 0 0 8px; color: #6f9a7f; font-size: 10px; font-weight: 600; }
.avatar-insight { padding: 10px 12px; margin-bottom: 8px; border: 1px solid #edf0ec; border-radius: 9px; background: #fcfdfb; }
.avatar-insight-head { display: flex; align-items: center; gap: 8px; }
.avatar-insight-head strong { flex: 1; min-width: 0; color: #45564c; font-size: 10px; }
.avatar-insight p { margin: 5px 0 0; color: #8b978f; font-size: 9px; line-height: 1.7; }
.avatar-confidence { display: inline-flex; align-items: center; gap: 5px; color: #9aa69f; font-size: 8px; }
.avatar-confidence-bar { display: block; width: 42px; height: 4px; overflow: hidden; border-radius: 3px; background: #eef2ee; }
.avatar-confidence-bar i { display: block; height: 100%; border-radius: 3px; background: #8dbf9f; }

.avatar-chat { display: grid; grid-template-columns: 216px minmax(0, 1fr); gap: 20px; }
.avatar-chat-sessions { min-width: 0; border-right: 1px solid #f1f3f0; padding-right: 16px; }
.avatar-session-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.avatar-session-head h2 { margin: 0; color: #46554c; font-size: 12px; font-weight: 600; }
.avatar-session-list { display: flex; flex-direction: column; gap: 6px; margin-top: 10px; max-height: 560px; overflow-y: auto; }
.avatar-session { display: flex; align-items: center; gap: 4px; border: 1px solid transparent; border-radius: 9px; background: #fcfdfb; }
.avatar-session.active { border-color: #b9d8c3; background: #f6fbf7; }
.avatar-session-main { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 3px; padding: 8px 10px; border: 0; background: transparent; text-align: left; }
.avatar-session-main strong { color: #46554c; font-size: 10px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.avatar-session-main small { color: #a4afa8; font-size: 8px; }

.avatar-chat-main { display: flex; flex-direction: column; min-width: 0; min-height: 460px; }
.avatar-chat-messages { flex: 1; display: flex; flex-direction: column; gap: 12px; margin-top: 14px; max-height: 520px; overflow-y: auto; padding-right: 4px; }
.avatar-chat-hint { padding: 16px; border-radius: 10px; color: #9aa69f; background: #f8faf8; font-size: 10px; line-height: 1.8; }
.avatar-bubble { display: flex; flex-direction: column; gap: 6px; max-width: 84%; }
.avatar-bubble.user { align-self: flex-end; }
.avatar-bubble.assistant { align-self: flex-start; }
.avatar-bubble-body { padding: 10px 13px; border-radius: 11px; font-size: 11px; line-height: 1.85; white-space: pre-wrap; word-break: break-word; }
.avatar-bubble.user .avatar-bubble-body { color: #fff; background: #397c65; border-bottom-right-radius: 4px; }
.avatar-bubble.assistant .avatar-bubble-body { color: #45564c; background: #f6f9f6; border: 1px solid #eaf0ea; border-bottom-left-radius: 4px; }
.avatar-bubble.pending .avatar-bubble-body { display: flex; align-items: center; gap: 6px; color: #8b978f; font-size: 10px; }
.avatar-bubble-citations { display: flex; flex-wrap: wrap; gap: 5px; }

.avatar-composer { display: flex; align-items: flex-end; gap: 10px; margin-top: 14px; }
.avatar-composer textarea {
  flex: 1;
  padding: 10px 12px;
  border: 1px solid #e5ebe4;
  border-radius: 10px;
  outline: 0;
  color: #4d5d54;
  background: #fbfcfa;
  font-size: 11px;
  font-family: inherit;
  line-height: 1.7;
  resize: none;
}
.avatar-composer textarea:focus { border-color: #a9c9b2; background: #fff; }

.avatar-chip-muted { color: #9a7b46 !important; background: #fbf3e3 !important; border-color: #f0e2c6 !important; }
.avatar-card-meta { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 6px; color: #a6b0a9; font-size: 9px; }
.avatar-feedback { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 8px; }
.avatar-feedback-button,
.avatar-feedback-reason {
  padding: 3px 9px;
  border: 1px solid #e3eae4;
  border-radius: 999px;
  color: #7f8e84;
  background: #fff;
  font-size: 10px;
  cursor: pointer;
  transition: all .15s ease;
}
.avatar-feedback-button:hover,
.avatar-feedback-reason:hover { border-color: #a9c9b2; color: #397c65; background: #f3f9f4; }
.avatar-feedback-button:disabled,
.avatar-feedback-reason:disabled { opacity: .5; cursor: default; }
.avatar-feedback-done { color: #6c8f78; font-size: 10px; }
.avatar-feedback-reasons { display: flex; flex-wrap: wrap; gap: 5px; width: 100%; margin-top: 3px; }

.avatar-candidates { display: flex; flex-direction: column; gap: 10px; margin-top: 16px; padding-top: 14px; border-top: 1px dashed #e7ece7; }
.avatar-candidates h3 { margin: 0; color: #4d5d54; font-size: 12px; }
.avatar-candidate { border-color: #f0e2c6; background: #fffdf8; }
.avatar-candidate .avatar-actions { margin-top: 8px; justify-content: space-between; }

.spin { animation: avatar-spin 1s linear infinite; }
@keyframes avatar-spin { to { transform: rotate(360deg); } }

@media (max-width: 1080px) {
  .avatar-feed-grid, .avatar-chat { grid-template-columns: 1fr; }
  .avatar-chat-sessions { border-right: 0; padding-right: 0; border-bottom: 1px solid #f1f3f0; padding-bottom: 12px; }
}
@media (max-width: 760px) {
  .avatar-form-grid, .avatar-field-row { grid-template-columns: 1fr; }
  .avatar-heading { flex-direction: column; align-items: flex-start; }
  .avatar-counts { margin-left: 0; }
  .avatar-bubble { max-width: 100%; }
}
</style>
