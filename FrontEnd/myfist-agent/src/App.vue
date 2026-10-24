<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Bell, CircleHelp, Search, Sparkles, X } from 'lucide-vue-next'
import { assignDocumentTopic, checkApiHealth, checkInStudy, createKnowledgeBase as createKnowledgeBaseApi, createKnowledgeChatSession, createNote as createNoteApi, createTopic as createTopicApi, deleteDocument as deleteDocumentApi, deleteKnowledgeChatSession as deleteKnowledgeChatSessionApi, deleteKnowledgeChatSessions, deleteNote as deleteNoteApi, deleteTopic as deleteTopicApi, downloadDocument, exportKnowledgeChatSession, getAgentInfo, getAppSettings, getJobCounts, getStudySummary, listDocuments, listKnowledgeBases, listKnowledgeChatMessages, listKnowledgeChatSessions, listNotes, listTopics, renameKnowledgeChatSession as renameKnowledgeChatSessionApi, retryDocument as retryDocumentApi, saveAppSettings, searchKnowledgeBase, sendKnowledgeChatMessage, setNotePinned as setNotePinnedApi, updateNote as updateNoteApi, updateTopic as updateTopicApi, uploadDocument } from './services/api'
import type { AgentSettings, AppSettings, ChatToolOptions, KnowledgeBase, KnowledgeChatSession, NoteInput, NoteRecord, SaveSettingsPayload, SemanticSearchResult, StudySummary, Topic } from './services/api'
import { defaultChatToolOptions } from './services/api'
import AssistantView from './components/AssistantView.vue'
import AvatarView from './components/AvatarView.vue'
import CompanionView from './components/CompanionView.vue'
import JobsView from './components/JobsView.vue'
import LibraryView from './components/LibraryView.vue'
import NotesView from './components/NotesView.vue'
import OverviewView from './components/OverviewView.vue'
import PluginsView from './components/PluginsView.vue'
import SearchOverlay from './components/SearchOverlay.vue'
import SettingsView from './components/SettingsView.vue'
import SidebarNav from './components/SidebarNav.vue'
import TopicsView from './components/TopicsView.vue'
import DocumentReaderModal from './components/DocumentReaderModal.vue'
import type { AssistantMessage, MaterialItem, NoteDraft, NoteItem, ViewName } from './types'
import { fileExtension, isHtmlExtension, isImageExtension, isVideoExtension, maxSizeForExtension, SUPPORTED_EXTENSIONS } from './fileTypes'

const currentView = ref<ViewName>('overview')
const searchQuery = ref('')
const sidebarOpen = ref(false)
const apiOnline = ref(false)
const isChatBusy = ref(false)
const toastMessage = ref('')
let toastTimer: ReturnType<typeof setTimeout> | undefined
let healthTimer: ReturnType<typeof setInterval> | undefined
let materialsPollTimer: ReturnType<typeof setTimeout> | undefined
let searchTimer: ReturnType<typeof setTimeout> | undefined

const materials = ref<MaterialItem[]>([])
const knowledgeBases = ref<KnowledgeBase[]>([])
const activeKnowledgeBaseId = ref('')
const topics = ref<Topic[]>([])
const activeTopicId = ref('')
const appSettings = ref<AppSettings | null>(null)
const isSettingsLoading = ref(false)
const isSettingsSaving = ref(false)
const studySummary = ref<StudySummary | null>(null)
const isSearchOpen = ref(false)
const semanticResults = ref<SemanticSearchResult[]>([])
const isSemanticLoading = ref(false)
let semanticRequestVersion = 0

const knowledgeChatSessions = ref<KnowledgeChatSession[]>([])
const activeKnowledgeChatSessionId = ref('')
const isChatHistoryLoading = ref(false)
const isChatHistoryMutating = ref(false)
let chatRequestVersion = 0

// AI 知识问答页的单轮工具开关：与设置页的全局偏好分离，只影响本页提问。
// 未手动调整过时沿用设置页保存的偏好；手动调整后写入 localStorage 并优先采用。
const CHAT_TOOL_OPTIONS_KEY = 'myfirstmafagent.chatToolOptions'
const storedChatToolOptions = readStoredChatToolOptions()
const chatToolOptions = ref<Required<ChatToolOptions>>(storedChatToolOptions ?? { ...defaultChatToolOptions })
const chatToolOptionsCustomized = ref(storedChatToolOptions !== null)
const chatToolsAvailable = ref(true)

function readStoredChatToolOptions(): Required<ChatToolOptions> | null {
  try {
    const raw = window.localStorage.getItem(CHAT_TOOL_OPTIONS_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<ChatToolOptions>
    return {
      enableToolCalling: typeof parsed.enableToolCalling === 'boolean' ? parsed.enableToolCalling : defaultChatToolOptions.enableToolCalling,
      enableWebSearch: typeof parsed.enableWebSearch === 'boolean' ? parsed.enableWebSearch : defaultChatToolOptions.enableWebSearch,
      enableFileAccess: typeof parsed.enableFileAccess === 'boolean' ? parsed.enableFileAccess : defaultChatToolOptions.enableFileAccess,
    }
  } catch {
    return null
  }
}

function updateChatToolOptions(options: Required<ChatToolOptions>) {
  chatToolOptions.value = options
  chatToolOptionsCustomized.value = true
  try {
    window.localStorage.setItem(CHAT_TOOL_OPTIONS_KEY, JSON.stringify(options))
  } catch {
    // 隐私模式下 localStorage 不可写，开关仍在本页生效。
  }
}

/** 设置页保存过偏好时，让问答页的初始开关与之保持一致。 */
function inheritChatToolOptions(agent: AgentSettings | undefined) {
  if (chatToolOptionsCustomized.value || !agent) return
  chatToolOptions.value = {
    enableToolCalling: agent.enableToolCalling,
    enableWebSearch: agent.enableWebSearch,
    enableFileAccess: agent.enableFileAccess,
  }
}

async function loadChatToolAvailability() {
  try {
    const info = await getAgentInfo()
    // 全局总闸关闭时 registry 不提供任何工具，前端对应置灰。
    chatToolsAvailable.value = info.toolCallingEnabled
  } catch {
    chatToolsAvailable.value = true
  }
}

const notes = ref<NoteItem[]>([])
const notesLoading = ref(false)
const notesSaving = ref(false)
const readerDocument = ref<{ id: string; title: string; fileName?: string; extension?: string } | null>(null)

const chatMessages = ref<AssistantMessage[]>([])
const pendingJobCount = ref(0)
const pageTitle = computed(() => ({ overview: '总览', materials: '我的资料', notes: '学习笔记', assistant: 'AI 知识问答', companions: 'AI 伴侣', avatar: '我的分身', topics: '我的专题', jobs: '任务中心', plugins: '插件', settings: '设置' })[currentView.value])
const displayName = computed(() => appSettings.value?.preferences.displayName || 'Lin')
const dailyGoalMinutes = computed(() => appSettings.value?.preferences.dailyGoalMinutes ?? 60)
const knowledgeBaseNameMap = computed<Record<string, string>>(() =>
  Object.fromEntries(knowledgeBases.value.map(item => [item.id, item.name])))

onMounted(() => {
  const refreshHealth = async () => {
    apiOnline.value = await checkApiHealth()
    void refreshPendingJobs()
  }
  void refreshHealth()
  void loadKnowledgeBases()
  void loadAppSettings()
  void loadStudySummary()
  void loadChatToolAvailability()
  window.addEventListener('keydown', handleGlobalKeydown)
  healthTimer = window.setInterval(refreshHealth, 15000)
})

onBeforeUnmount(() => {
  if (healthTimer) clearInterval(healthTimer)
  if (toastTimer) clearTimeout(toastTimer)
  if (materialsPollTimer) clearTimeout(materialsPollTimer)
  if (searchTimer) clearTimeout(searchTimer)
  window.removeEventListener('keydown', handleGlobalKeydown)
})

function handleGlobalKeydown(event: KeyboardEvent) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    openSearch()
  }
}

function navigate(view: ViewName) {
  currentView.value = view
  sidebarOpen.value = false
  searchQuery.value = ''
}

/** 侧栏的任务角标：只关心“还在跑/在排队”的数量，失败与完成不叫人。 */
async function refreshPendingJobs() {
  if (!apiOnline.value) {
    pendingJobCount.value = 0
    return
  }

  try {
    const counts = await getJobCounts()
    pendingJobCount.value = counts.queued + counts.running
  } catch {
    // 角标是辅助信息，失败就保持原值，不弹提示。
  }
}

function showToast(message: string) {
  toastMessage.value = message
  if (toastTimer) clearTimeout(toastTimer)
  toastTimer = setTimeout(() => { toastMessage.value = '' }, 3400)
}

async function loadMaterials() {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return

  try {
    const documents = await listDocuments(knowledgeBaseId)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    const colors = ['lilac', 'mint', 'peach', 'blue', 'rose', 'sage']
    materials.value = documents.map((document, index) => {
      const extension = document.extension.toLowerCase()
      const kind: MaterialItem['kind'] = extension === '.pdf' ? 'pdf'
        : isVideoExtension(extension) ? 'video'
          : isImageExtension(extension) ? 'image'
            : isHtmlExtension(extension) ? 'html' : 'document'
      const status = document.status.toLowerCase()
      return {
        id: document.id,
        title: document.fileName.replace(/\.[^.]+$/, ''),
        fileName: document.fileName,
        kind,
        category: document.topicId
          ? topics.value.find(topic => topic.id === document.topicId)?.name ?? '本地知识库'
          : '本地知识库',
        detail: `${extension.replace('.', '').toUpperCase()} · ${formatSize(document.sizeBytes)}`,
        updated: new Date(document.createdAt).toLocaleString('zh-CN', { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }),
        status: status === 'ready' ? 'ready' : status === 'failed' ? 'failed' : 'processing',
        color: colors[index % colors.length] ?? 'lilac',
        iconColor: 'purple',
        summary: document.summary,
        progress: document.progress,
        processingStage: document.processingStage,
        error: document.error,
        topicId: document.topicId ?? undefined,
      }
    })
    const hasProcessingDocuments = documents.some(document => ['queued', 'processing'].includes(document.status.toLowerCase()))
    if (hasProcessingDocuments && !materialsPollTimer) {
      materialsPollTimer = window.setTimeout(() => {
        materialsPollTimer = undefined
        void loadMaterials()
      }, 2500)
    } else if (!hasProcessingDocuments && materialsPollTimer) {
      clearTimeout(materialsPollTimer)
      materialsPollTimer = undefined
    }
  } catch {
    if (knowledgeBaseId === activeKnowledgeBaseId.value) apiOnline.value = false
  }
}

async function loadKnowledgeBases() {
  try {
    knowledgeBases.value = await listKnowledgeBases()
    const savedId = window.localStorage.getItem('myfirstmafagent.activeKnowledgeBaseId')
    const selected = knowledgeBases.value.find(item => item.id === savedId) ?? knowledgeBases.value[0]
    if (selected) await changeKnowledgeBase(selected.id)
  } catch (error) {
    apiOnline.value = false
    showToast(error instanceof Error ? error.message : '无法读取知识库。')
  }
}

async function changeKnowledgeBase(id: string) {
  if (!knowledgeBases.value.some(item => item.id === id) || activeKnowledgeBaseId.value === id) return
  if (materialsPollTimer) clearTimeout(materialsPollTimer)
  materialsPollTimer = undefined
  activeKnowledgeBaseId.value = id
  chatRequestVersion++
  window.localStorage.setItem('myfirstmafagent.activeKnowledgeBaseId', id)
  materials.value = []
  chatMessages.value = []
  knowledgeChatSessions.value = []
  activeKnowledgeChatSessionId.value = ''
  isChatBusy.value = false
  topics.value = []
  activeTopicId.value = ''
  semanticResults.value = []
  notes.value = []
  await loadTopics(id)
  await Promise.all([loadMaterials(), loadKnowledgeChatSessions(id), loadNotes(id)])
}

async function loadTopics(knowledgeBaseId = activeKnowledgeBaseId.value) {
  if (!knowledgeBaseId) {
    topics.value = []
    return
  }
  try {
    const list = await listTopics(knowledgeBaseId)
    if (knowledgeBaseId === activeKnowledgeBaseId.value) topics.value = list
  } catch {
    if (knowledgeBaseId === activeKnowledgeBaseId.value) topics.value = []
  }
}

async function requestCreateTopic() {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) {
    showToast('请先选择一个知识库。')
    return
  }
  const name = window.prompt('请输入新专题名称')?.trim()
  if (!name) return
  if (name.length > 50) {
    showToast('专题名称不能超过 50 个字符。')
    return
  }
  const colors = ['lavender', 'mint', 'peach', 'blue', 'rose', 'sage']
  try {
    const topic = await createTopicApi(knowledgeBaseId, name, colors[topics.value.length % colors.length])
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    topics.value = [...topics.value, topic]
    showToast(`已创建专题“${topic.name}”。`)
  } catch (error) {
    showToast(error instanceof Error ? error.message : '创建专题失败。')
  }
}

async function renameTopic(topic: Topic) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  const name = window.prompt('请输入新的专题名称', topic.name)?.trim()
  if (name === undefined) return
  if (!name || name.length > 50) {
    showToast('专题名称必填且不能超过 50 个字符。')
    return
  }
  try {
    const updated = await updateTopicApi(knowledgeBaseId, topic.id, name, topic.color)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    topics.value = topics.value.map(item => item.id === topic.id ? updated : item)
    showToast('专题名称已更新。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '重命名专题失败。')
  }
}

async function removeTopic(topic: Topic) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  if (!window.confirm(`确定删除专题“${topic.name}”吗？其中的资料会移回“未分类”，不会被删除。`)) return
  try {
    await deleteTopicApi(knowledgeBaseId, topic.id)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    topics.value = topics.value.filter(item => item.id !== topic.id)
    materials.value = materials.value.map(item => item.topicId === topic.id ? { ...item, topicId: undefined, category: '本地知识库' } : item)
    if (activeTopicId.value === topic.id) activeTopicId.value = ''
    showToast('专题已删除，资料已移回未分类。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '删除专题失败。')
  }
}

function selectTopic(topicId: string) {
  activeTopicId.value = topicId
  navigate('materials')
}

async function changeDocumentTopic(documentId: string, topicId: string | null) {
  try {
    await assignDocumentTopic(documentId, topicId)
    const topicName = topicId ? topics.value.find(topic => topic.id === topicId)?.name ?? '本地知识库' : '本地知识库'
    materials.value = materials.value.map(item => item.id === documentId
      ? { ...item, topicId: topicId ?? undefined, category: topicName }
      : item)
    void loadTopics()
    showToast(topicId ? '资料已移动到专题。' : '资料已移回未分类。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '移动资料失败。')
  }
}

async function loadAppSettings() {
  isSettingsLoading.value = true
  try {
    appSettings.value = await getAppSettings()
    inheritChatToolOptions(appSettings.value?.agent)
  } catch {
    // API 未连接时保持空状态，由设置页面提示。
  } finally {
    isSettingsLoading.value = false
  }
}

async function saveSettings(payload: SaveSettingsPayload) {
  if (isSettingsSaving.value) return
  isSettingsSaving.value = true
  try {
    appSettings.value = await saveAppSettings(payload)
    showToast('设置已保存，新的模型配置立即生效。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '保存设置失败。')
  } finally {
    isSettingsSaving.value = false
  }
}

async function loadStudySummary() {
  try {
    studySummary.value = await getStudySummary()
  } catch {
    // 打卡数据不可用时保持空状态，不影响其他功能。
  }
}

function noteDate(value: string) {
  const date = new Date(value)
  const now = new Date()
  if (date.toDateString() === now.toDateString())
    return `今天 ${date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}`
  return date.toLocaleDateString('zh-CN', { month: 'numeric', day: 'numeric' })
}

function toNoteItem(note: NoteRecord): NoteItem {
  return {
    id: note.id,
    knowledgeBaseId: note.knowledgeBaseId,
    title: note.title,
    excerpt: note.excerpt,
    content: note.content,
    category: note.category,
    color: note.color,
    tags: note.tags ?? [],
    isPinned: note.isPinned,
    sourceDocumentId: note.sourceDocumentId,
    createdAt: note.createdAt,
    updatedAt: note.updatedAt,
    date: noteDate(note.updatedAt),
  }
}

async function loadNotes(knowledgeBaseId = activeKnowledgeBaseId.value) {
  if (!knowledgeBaseId) {
    notes.value = []
    return
  }
  notesLoading.value = true
  try {
    const list = await listNotes(knowledgeBaseId)
    if (knowledgeBaseId === activeKnowledgeBaseId.value) notes.value = list.map(toNoteItem)
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value) {
      notes.value = []
      showToast(error instanceof Error ? error.message : '无法读取笔记。')
    }
  } finally {
    if (knowledgeBaseId === activeKnowledgeBaseId.value) notesLoading.value = false
  }
}

function toNoteInput(draft: NoteDraft, sourceDocumentId?: string | null): NoteInput {
  return {
    title: draft.title,
    content: draft.content,
    category: draft.category,
    color: draft.color,
    tags: draft.tags,
    sourceDocumentId,
  }
}

async function createNoteHandler(draft: NoteDraft) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) {
    showToast('请先选择一个知识库。')
    return
  }
  notesSaving.value = true
  try {
    const note = await createNoteApi(knowledgeBaseId, toNoteInput(draft))
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    notes.value = [toNoteItem(note), ...notes.value]
    showToast('笔记已保存。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '保存笔记失败。')
  } finally {
    notesSaving.value = false
  }
}

async function updateNoteHandler(noteId: string, draft: NoteDraft) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  notesSaving.value = true
  try {
    const updated = await updateNoteApi(knowledgeBaseId, noteId, toNoteInput(draft))
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    notes.value = notes.value
      .map(note => note.id === noteId ? toNoteItem(updated) : note)
      .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || b.updatedAt.localeCompare(a.updatedAt))
    showToast('笔记已更新。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '更新笔记失败。')
  } finally {
    notesSaving.value = false
  }
}

async function deleteNoteHandler(note: NoteItem) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  if (!window.confirm(`确定删除笔记“${note.title}”吗？此操作不可恢复。`)) return
  notesSaving.value = true
  try {
    await deleteNoteApi(knowledgeBaseId, note.id)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    notes.value = notes.value.filter(item => item.id !== note.id)
    showToast('笔记已删除。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '删除笔记失败。')
  } finally {
    notesSaving.value = false
  }
}

async function pinNoteHandler(note: NoteItem) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  try {
    const updated = await setNotePinnedApi(knowledgeBaseId, note.id, !note.isPinned)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    notes.value = notes.value
      .map(item => item.id === note.id ? toNoteItem(updated) : item)
      .sort((a, b) => Number(b.isPinned) - Number(a.isPinned) || b.updatedAt.localeCompare(a.updatedAt))
    showToast(updated.isPinned ? '笔记已置顶。' : '已取消置顶。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '更新置顶状态失败。')
  }
}

function openReaderById(documentId: string, fileName?: string) {
  readerDocument.value = {
    id: documentId,
    title: fileName?.replace(/\.[^.]+$/, '') ?? '原文阅读',
    fileName,
  }
}

function openReaderFromMaterial(item: MaterialItem) {
  if (item.id.startsWith('upload-')) {
    showToast('资料上传完成后才能阅读原文。')
    return
  }
  readerDocument.value = { id: item.id, title: item.title, fileName: item.fileName }
}

async function saveReaderNote(payload: { title: string; content: string }) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId || !payload.content.trim()) return
  try {
    const note = await createNoteApi(knowledgeBaseId, {
      title: payload.title,
      content: payload.content,
      category: '资料摘录',
      color: 'mint',
      tags: ['原文摘录'],
      sourceDocumentId: readerDocument.value?.id ?? null,
    })
    if (knowledgeBaseId === activeKnowledgeBaseId.value) notes.value = [toNoteItem(note), ...notes.value]
    showToast('已保存为笔记。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '保存笔记失败。')
  }
}

async function recordCheckin(minutes: number, note?: string) {
  try {
    studySummary.value = await checkInStudy(minutes, note)
    showToast(minutes > 0 ? `今日学习 ${minutes} 分钟，已打卡。` : '今日已打卡。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '打卡失败。')
  }
}

function openSearch() {
  if (sidebarOpen.value) return
  if (isSearchOpen.value) return
  isSearchOpen.value = true
  if (searchQuery.value.trim().length >= 2) void runSemanticSearch(searchQuery.value)
}

function closeSearch() {
  isSearchOpen.value = false
  if (searchTimer) clearTimeout(searchTimer)
}

function submitSearch() {
  const query = searchQuery.value
  if (query.trim()) {
    currentView.value = 'materials'
    sidebarOpen.value = false
    searchQuery.value = query
  }
  closeSearch()
}

async function runSemanticSearch(query: string) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  const version = ++semanticRequestVersion
  if (!apiOnline.value || !knowledgeBaseId || query.trim().length < 2) {
    semanticResults.value = []
    isSemanticLoading.value = false
    return
  }
  isSemanticLoading.value = true
  try {
    const results = await searchKnowledgeBase(query.trim(), knowledgeBaseId, 5)
    if (version === semanticRequestVersion) semanticResults.value = results
  } catch {
    if (version === semanticRequestVersion) semanticResults.value = []
  } finally {
    if (version === semanticRequestVersion) isSemanticLoading.value = false
  }
}

watch(searchQuery, (value) => {
  if (searchTimer) clearTimeout(searchTimer)
  if (!isSearchOpen.value) return
  if (value.trim().length < 2) {
    semanticRequestVersion++
    semanticResults.value = []
    isSemanticLoading.value = false
    return
  }
  searchTimer = setTimeout(() => void runSemanticSearch(value), 450)
})

watch(activeKnowledgeBaseId, () => {
  semanticRequestVersion++
  semanticResults.value = []
})

watch(apiOnline, (online) => {
  if (!online) return
  if (!appSettings.value) void loadAppSettings()
  if (!studySummary.value) void loadStudySummary()
  if (notes.value.length === 0) void loadNotes()
})

async function loadKnowledgeChatSessions(knowledgeBaseId: string) {
  isChatHistoryLoading.value = true
  try {
    const sessions = await listKnowledgeChatSessions(knowledgeBaseId)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    knowledgeChatSessions.value = sessions
    const latest = sessions[0]
    if (latest) await openKnowledgeChatSession(latest.id)
    else {
      activeKnowledgeChatSessionId.value = ''
      chatMessages.value = []
    }
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value)
      showToast(error instanceof Error ? error.message : '无法读取问答历史。')
  } finally {
    if (knowledgeBaseId === activeKnowledgeBaseId.value) isChatHistoryLoading.value = false
  }
}

async function openKnowledgeChatSession(sessionId: string) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  activeKnowledgeChatSessionId.value = sessionId
  isChatHistoryLoading.value = true
  chatMessages.value = []
  try {
    const history = await listKnowledgeChatMessages(knowledgeBaseId, sessionId)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value || sessionId !== activeKnowledgeChatSessionId.value) return
    chatMessages.value = history.map(message => ({
      id: String(message.id),
      role: message.role,
      text: message.content,
      time: chatTime(message.createdAt),
    }))
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value && sessionId === activeKnowledgeChatSessionId.value)
      showToast(error instanceof Error ? error.message : '无法恢复这段对话。')
  } finally {
    if (knowledgeBaseId === activeKnowledgeBaseId.value && sessionId === activeKnowledgeChatSessionId.value)
      isChatHistoryLoading.value = false
  }
}

async function requestNewKnowledgeBase() {
  const name = window.prompt('请输入新知识库名称')?.trim()
  if (!name) return
  if (name.length > 100) {
    showToast('知识库名称不能超过 100 个字符。')
    return
  }

  try {
    const knowledgeBase = await createKnowledgeBaseApi(name)
    knowledgeBases.value = [...knowledgeBases.value, knowledgeBase]
    await changeKnowledgeBase(knowledgeBase.id)
    showToast(`已创建并切换到“${knowledgeBase.name}”。`)
  } catch (error) {
    showToast(error instanceof Error ? error.message : '创建知识库失败。')
  }
}

function receiveFiles(files: FileList | null) {
  if (!files?.length) return
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) {
    showToast('请先选择一个知识库。')
    return
  }
  const uploadTopicId = activeTopicId.value && activeTopicId.value !== 'none' ? activeTopicId.value : null
  const accepted = Array.from(files).filter((file) => {
    const extension = fileExtension(file.name)
    return SUPPORTED_EXTENSIONS.includes(extension) && file.size > 0 && file.size <= maxSizeForExtension(extension)
  })
  const rejected = files.length - accepted.length
  if (rejected) showToast(`${rejected} 个文件格式不支持、为空或超过上限（视频 5 GiB，其他文件 50 MiB）。`)
  if (!accepted.length) return

  const uploadQueue = accepted.map((file) => {
    const extension = fileExtension(file.name)
    const kind: MaterialItem['kind'] = extension === '.pdf' ? 'pdf'
      : isVideoExtension(extension) ? 'video'
        : isImageExtension(extension) ? 'image'
          : isHtmlExtension(extension) ? 'html' : 'document'
    const item: MaterialItem = {
      id: `upload-${crypto.randomUUID()}`,
      title: file.name.replace(/\.[^.]+$/, ''),
      fileName: file.name,
      kind,
      category: '本次上传',
      detail: `${extension.replace('.', '').toUpperCase()} · ${formatSize(file.size)}`,
      updated: '刚刚',
      status: 'processing',
      color: kind === 'video' ? 'peach' : kind === 'image' ? 'blue' : kind === 'pdf' ? 'lilac' : kind === 'html' ? 'sage' : 'mint',
      iconColor: kind === 'video' ? 'orange' : kind === 'html' ? 'green' : 'purple',
      progress: 0,
      processingStage: '准备上传',
    }
    materials.value.unshift(item)
    return { file, item }
  })

  showToast(`正在上传 ${accepted.length} 个文件…`)
  void (async () => {
    for (const { file, item } of uploadQueue) {
      try {
        const result = await uploadDocument(file, knowledgeBaseId, (uploadedBytes, totalBytes) => {
          item.progress = totalBytes === 0 ? 0 : Math.floor(uploadedBytes / totalBytes * 100)
          item.processingStage = `上传中 ${formatSize(uploadedBytes)} / ${formatSize(totalBytes)}`
        })
        item.id = result.id
        item.status = result.status.toLowerCase() === 'failed' ? 'failed' : result.status.toLowerCase() === 'ready' ? 'ready' : 'processing'
        item.progress = result.status.toLowerCase() === 'ready' ? 100 : 0
        item.processingStage = result.status.toLowerCase() === 'ready' ? '已完成' : '排队中'
        item.category = '本机文件'
        item.detail = `${result.extension.replace('.', '').toUpperCase()} · ${formatSize(result.sizeBytes)}`
        if (item.status === 'failed') showToast(`${file.name} 处理失败：${result.error || '请检查模型/媒体配置'}`)
        if (uploadTopicId) {
          try {
            await assignDocumentTopic(result.id, uploadTopicId)
            item.topicId = uploadTopicId
            item.category = topics.value.find(topic => topic.id === uploadTopicId)?.name ?? item.category
          } catch {
            // 专题归档失败不影响上传本身，可在资料菜单中重新指定。
          }
        }
        void loadMaterials()
        void loadTopics()
      } catch (error) {
        item.status = 'failed'
        item.error = error instanceof Error ? error.message : '请检查 API 服务'
        item.processingStage = '上传失败；重新选择同一文件可续传'
        showToast(`${file.name} 上传失败：${item.error}`)
      }
    }
  })()
}

async function retryProcessing(documentId: string) {
  try {
    await retryDocumentApi(documentId)
    showToast('已重新排队解析。')
    void loadMaterials()
  } catch (error) {
    showToast(error instanceof Error ? error.message : '重新解析失败。')
  }
}

async function deleteMaterial(item: MaterialItem) {
  if (!window.confirm(`确定删除“${item.title}”吗？该资料及其解析内容将被移除，此操作不可恢复。`)) return
  try {
    await deleteDocumentApi(item.id)
    materials.value = materials.value.filter(material => material.id !== item.id)
    void loadTopics()
    showToast('资料已删除。')
  } catch (error) {
    showToast(error instanceof Error ? error.message : '删除资料失败。')
  }
}

async function downloadMaterial(item: MaterialItem) {
  try {
    const blob = await downloadDocument(item.id)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = item.fileName || item.title
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (error) {
    showToast(error instanceof Error ? error.message : '下载资料失败。')
  }
}

function formatSize(size: number) {
  if (size <= 0) return '0 B'
  if (size < 1024 * 1024) return `${Math.max(1, Math.round(size / 1024))} KB`
  if (size < 1024 ** 3) return `${(size / 1024 / 1024).toFixed(1)} MB`
  return `${(size / 1024 ** 3).toFixed(2)} GB`
}

async function sendMessage(message: string) {
  if (isChatBusy.value || isChatHistoryMutating.value) return
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) {
    showToast('请先选择一个知识库。')
    return
  }

  const requestVersion = ++chatRequestVersion
  isChatBusy.value = true
  let messageAdded = false
  let sessionId = activeKnowledgeChatSessionId.value
  try {
    if (!sessionId) {
      const session = await createKnowledgeChatSession(knowledgeBaseId)
      if (requestVersion !== chatRequestVersion || knowledgeBaseId !== activeKnowledgeBaseId.value) return
      knowledgeChatSessions.value.unshift(session)
      activeKnowledgeChatSessionId.value = session.id
      sessionId = session.id
    }

    chatMessages.value.push({ id: crypto.randomUUID(), role: 'user', text: message, time: chatTime() })
    messageAdded = true
    const result = await sendKnowledgeChatMessage(knowledgeBaseId, sessionId, message, chatToolOptions.value)
    if (requestVersion !== chatRequestVersion || knowledgeBaseId !== activeKnowledgeBaseId.value || sessionId !== activeKnowledgeChatSessionId.value) return
    chatMessages.value.push({ id: crypto.randomUUID(), role: 'assistant', text: result.answer, citations: result.citations, toolCalls: result.toolCalls, time: chatTime() })
    const currentSession = knowledgeChatSessions.value.find(session => session.id === sessionId)
    if (currentSession) {
      const updatedSession = {
        ...currentSession,
        title: currentSession.title === '新的对话' ? message.slice(0, 100) : currentSession.title,
        updatedAt: new Date().toISOString(),
      }
      knowledgeChatSessions.value = [updatedSession, ...knowledgeChatSessions.value.filter(session => session.id !== sessionId)]
    }
  } catch (error) {
    if (requestVersion !== chatRequestVersion || knowledgeBaseId !== activeKnowledgeBaseId.value || sessionId !== activeKnowledgeChatSessionId.value) return
    if (messageAdded) {
      chatMessages.value.push({
        id: crypto.randomUUID(),
        role: 'assistant',
        text: error instanceof Error ? `暂时无法连接模型服务：${error.message}` : '暂时无法连接模型服务，请检查 API 状态和模型配置。',
        time: chatTime(),
      })
    } else {
      showToast(error instanceof Error ? error.message : '无法创建新对话。')
    }
  } finally {
    if (requestVersion === chatRequestVersion) isChatBusy.value = false
  }
}

function chatTime(value?: string) {
  return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit' }).format(value ? new Date(value) : new Date())
}

async function startNewChat() {
  if (isChatBusy.value || isChatHistoryLoading.value || isChatHistoryMutating.value) return
  const knowledgeBaseId = activeKnowledgeBaseId.value
  if (!knowledgeBaseId) return
  const requestVersion = ++chatRequestVersion
  isChatBusy.value = true
  try {
    const session = await createKnowledgeChatSession(knowledgeBaseId)
    if (requestVersion !== chatRequestVersion || knowledgeBaseId !== activeKnowledgeBaseId.value) return
    knowledgeChatSessions.value.unshift(session)
    activeKnowledgeChatSessionId.value = session.id
    chatMessages.value = []
  } catch (error) {
    if (requestVersion === chatRequestVersion) showToast(error instanceof Error ? error.message : '无法创建新对话。')
  } finally {
    if (requestVersion === chatRequestVersion) isChatBusy.value = false
  }
}

function selectKnowledgeChatSession(sessionId: string) {
  if (isChatBusy.value || isChatHistoryLoading.value || isChatHistoryMutating.value) return
  void openKnowledgeChatSession(sessionId)
}

async function renameHistorySession(sessionId: string) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  const session = knowledgeChatSessions.value.find(item => item.id === sessionId)
  if (!knowledgeBaseId || !session || isChatHistoryMutating.value) return

  const title = window.prompt('请输入新的对话名称', session.title)
  if (title === null) return
  const normalizedTitle = title.trim()
  if (!normalizedTitle || normalizedTitle.length > 100) {
    showToast('对话名称必填且不能超过 100 个字符。')
    return
  }

  isChatHistoryMutating.value = true
  try {
    await renameKnowledgeChatSessionApi(knowledgeBaseId, sessionId, normalizedTitle)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    const renamedSession = { ...session, title: normalizedTitle, updatedAt: new Date().toISOString() }
    knowledgeChatSessions.value = [renamedSession, ...knowledgeChatSessions.value.filter(item => item.id !== sessionId)]
    showToast('对话名称已更新。')
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value)
      showToast(error instanceof Error ? error.message : '重命名对话失败。')
  } finally {
    isChatHistoryMutating.value = false
  }
}

async function deleteHistorySession(sessionId: string) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  const session = knowledgeChatSessions.value.find(item => item.id === sessionId)
  if (!knowledgeBaseId || !session || isChatHistoryMutating.value) return
  if (!window.confirm(`确定删除“${session.title}”及其全部消息吗？此操作不可恢复。`)) return

  isChatHistoryMutating.value = true
  try {
    await deleteKnowledgeChatSessionApi(knowledgeBaseId, sessionId)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    knowledgeChatSessions.value = knowledgeChatSessions.value.filter(item => item.id !== sessionId)
    await selectAvailableSessionAfterDelete([sessionId])
    showToast('对话已删除。')
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value)
      showToast(error instanceof Error ? error.message : '删除对话失败。')
  } finally {
    isChatHistoryMutating.value = false
  }
}

async function deleteSelectedHistorySessions(sessionIds: string[]) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  const selectedIds = [...new Set(sessionIds)].filter(id => knowledgeChatSessions.value.some(session => session.id === id))
  if (!knowledgeBaseId || selectedIds.length === 0 || isChatHistoryMutating.value) return
  if (!window.confirm(`确定删除选中的 ${selectedIds.length} 条对话及其全部消息吗？此操作不可恢复。`)) return

  isChatHistoryMutating.value = true
  try {
    const deletedCount = await deleteKnowledgeChatSessions(knowledgeBaseId, selectedIds)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    knowledgeChatSessions.value = knowledgeChatSessions.value.filter(session => !selectedIds.includes(session.id))
    await selectAvailableSessionAfterDelete(selectedIds)
    showToast(`已删除 ${deletedCount} 条对话。`)
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value)
      showToast(error instanceof Error ? error.message : '批量删除对话失败。')
  } finally {
    isChatHistoryMutating.value = false
  }
}

async function selectAvailableSessionAfterDelete(deletedIds: string[]) {
  if (!deletedIds.includes(activeKnowledgeChatSessionId.value)) return
  chatRequestVersion++
  activeKnowledgeChatSessionId.value = ''
  chatMessages.value = []
  const nextSession = knowledgeChatSessions.value[0]
  if (nextSession) await openKnowledgeChatSession(nextSession.id)
}

async function exportHistorySession(sessionId: string) {
  const knowledgeBaseId = activeKnowledgeBaseId.value
  const session = knowledgeChatSessions.value.find(item => item.id === sessionId)
  if (!knowledgeBaseId || !session || isChatHistoryMutating.value) return

  isChatHistoryMutating.value = true
  try {
    const blob = await exportKnowledgeChatSession(knowledgeBaseId, sessionId)
    if (knowledgeBaseId !== activeKnowledgeBaseId.value) return
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    const fileName = session.title
      .replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_')
      .trim()
      .replace(/[. ]+$/g, '')
      .slice(0, 80) || '问答记录'
    link.href = url
    link.download = `${fileName}.md`
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    showToast('对话记录已导出为 Markdown。')
  } catch (error) {
    if (knowledgeBaseId === activeKnowledgeBaseId.value)
      showToast(error instanceof Error ? error.message : '导出对话失败。')
  } finally {
    isChatHistoryMutating.value = false
  }
}
</script>

<template>
  <div class="app-shell" :class="{ 'sidebar-open': sidebarOpen }">
    <button v-if="sidebarOpen" class="mobile-scrim" aria-label="关闭菜单" @click="sidebarOpen = false"></button>
     <SidebarNav
       :active-view="currentView"
       :knowledge-bases="knowledgeBases"
       :active-knowledge-base-id="activeKnowledgeBaseId"
       :topics="topics"
       :active-topic-id="activeTopicId"
       :material-count="materials.length"
       :note-count="notes.length"
       :pending-job-count="pendingJobCount"
       :display-name="displayName"
       @navigate="navigate"
       @select-knowledge-base="changeKnowledgeBase"
       @create-knowledge-base="requestNewKnowledgeBase"
       @create-topic="requestCreateTopic"
       @select-topic="selectTopic"
       @edit-topics="navigate('topics')"
     />
    <main class="main-shell">
      <header class="topbar">
        <button class="mobile-menu icon-button" aria-label="打开菜单" @click="sidebarOpen = true"><span class="menu-lines"></span></button>
        <div class="breadcrumbs"><span>我的空间</span><span class="crumb-slash">/</span><strong>{{ pageTitle }}</strong></div>
        <div class="topbar-right">
          <label class="global-search" @click="openSearch"><Search :size="16" /><input v-model="searchQuery" placeholder="搜索资料、笔记..." @focus="openSearch" @keydown.enter="submitSearch" /><kbd>Ctrl K</kbd></label>
          <button class="icon-button help-button" aria-label="帮助"><CircleHelp :size="18" /></button>
          <button class="icon-button notification-button" aria-label="通知"><Bell :size="18" /><i></i></button>
          <button class="topbar-avatar" aria-label="用户菜单">{{ displayName.slice(0, 1) }}</button>
        </div>
      </header>

      <div class="page-content" :class="{ 'page-content-assistant': currentView === 'assistant' || currentView === 'companions' }">
        <OverviewView v-if="currentView === 'overview'" :materials="materials" :notes="notes" :is-api-online="apiOnline" :study="studySummary" :display-name="displayName" :daily-goal-minutes="dailyGoalMinutes" @upload="receiveFiles" @navigate="navigate" @search="openSearch" @checkin="recordCheckin" @read="openReaderFromMaterial" />
        <LibraryView v-else-if="currentView === 'materials'" :materials="materials" :query="searchQuery" :topics="topics" :active-topic-id="activeTopicId" @upload="receiveFiles" @retry="retryProcessing" @delete="deleteMaterial" @download="downloadMaterial" @notice="showToast" @select-topic="selectTopic" @assign-topic="changeDocumentTopic" @read="openReaderFromMaterial" />
        <NotesView v-else-if="currentView === 'notes'" :notes="notes" :query="searchQuery" :api-online="apiOnline" :busy="notesSaving" :loading="notesLoading" :active-knowledge-base-id="activeKnowledgeBaseId" @create="createNoteHandler" @update="updateNoteHandler" @delete="deleteNoteHandler" @pin="pinNoteHandler" @notice="showToast" />
        <TopicsView v-else-if="currentView === 'topics'" :topics="topics" :materials="materials" :api-online="apiOnline" :active-topic-id="activeTopicId" @create="requestCreateTopic" @rename="renameTopic" @delete="removeTopic" @open="selectTopic" />
        <SettingsView v-else-if="currentView === 'settings'" :settings="appSettings" :api-online="apiOnline" :busy="isSettingsSaving" :loading="isSettingsLoading" :knowledge-base-names="knowledgeBaseNameMap" @save="saveSettings" @reload="loadAppSettings" />
        <AssistantView v-else-if="currentView === 'assistant'" :messages="chatMessages" :busy="isChatBusy || isChatHistoryMutating" :api-online="apiOnline" :sessions="knowledgeChatSessions" :active-session-id="activeKnowledgeChatSessionId" :sessions-loading="isChatHistoryLoading" :tool-options="chatToolOptions" :tools-available="chatToolsAvailable" @send="sendMessage" @new-chat="startNewChat" @select-session="selectKnowledgeChatSession" @rename-session="renameHistorySession" @delete-session="deleteHistorySession" @delete-sessions="deleteSelectedHistorySessions" @export-session="exportHistorySession" @update:tool-options="updateChatToolOptions" />
        <AvatarView v-else-if="currentView === 'avatar'" :api-online="apiOnline" @notice="showToast" />
        <JobsView v-else-if="currentView === 'jobs'" :api-online="apiOnline" @notice="showToast" @counts="pendingJobCount = $event" />
        <PluginsView v-else-if="currentView === 'plugins'" :api-online="apiOnline" :knowledge-bases="knowledgeBases" :active-knowledge-base-id="activeKnowledgeBaseId" @notice="showToast" />
        <CompanionView v-else />
      </div>
      <footer v-if="currentView !== 'assistant'" class="page-footer"><span>知间 <span>·</span> 让每一份学习都有迹可循</span><span>本地优先，安心记录 <span class="footer-leaf">✳</span></span></footer>
    </main>
    <Transition name="toast"><div v-if="toastMessage" class="toast-message"><Sparkles :size="16" />{{ toastMessage }}<button aria-label="关闭" @click="toastMessage = ''"><X :size="15" /></button></div></Transition>
    <SearchOverlay
      v-if="isSearchOpen"
      :query="searchQuery"
      :materials="materials"
      :notes="notes"
      :semantic-results="semanticResults"
      :semantic-loading="isSemanticLoading"
      :api-online="apiOnline"
      @update:query="searchQuery = $event"
      @close="closeSearch"
      @submit="submitSearch"
      @open-material="navigate('materials'); closeSearch()"
      @open-note="navigate('notes'); closeSearch()"
      @open-document="openReaderById($event); closeSearch()"
    />
    <DocumentReaderModal
      v-if="readerDocument"
      :document-id="readerDocument.id"
      :title="readerDocument.title"
      :file-name="readerDocument.fileName"
      :api-online="apiOnline"
      @close="readerDocument = null"
      @save-note="saveReaderNote"
    />
  </div>
</template>
