<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Archive, ArrowDownToLine, ArrowUpFromLine, Boxes, Check, Cloud, Copy, Cpu, Database, Download, KeyRound, LoaderCircle, Mic, Plug, Power, RefreshCw, RotateCcw, Save, Server, SlidersHorizontal, Sparkles, Trash2, Upload, UserRound } from 'lucide-vue-next'
import { deleteWhisperModel, downloadWhisperModel, exportKnowledgeBasePackage, getWhisperModelStatus, importWhisperModel, importKnowledgeBasePackage } from '../services/api'
import { getAgentInfo, getMcpStatus, importLocalEmbeddingSpec, listAgentTools, probeEmbeddingSpec, registerCloudEmbeddingSpec } from '../services/api'
import { getLocalMcpConfig } from '../services/api'
import { normalizeAgentSettings } from '../services/api'
import type { AgentInfo, AgentSettings, AgentToolInfo, AppSettings, EmbeddingSpec, KnowledgeBase, KnowledgePackageImportResult, McpStatus, SaveSettingsPayload, WhisperModelStatus } from '../services/api'

const props = defineProps<{
  settings: AppSettings | null
  apiOnline: boolean
  busy: boolean
  loading: boolean
  knowledgeBaseNames: Record<string, string>
  knowledgeBases?: KnowledgeBase[]
  activeKnowledgeBaseId?: string
}>()
const emit = defineEmits<{
  save: [payload: SaveSettingsPayload]
  reload: []
  imported: [knowledgeBaseId: string]
}>()

const useCloudApi = ref(true)
const localEndpoint = ref('')
const localModel = ref('')
const onlineEndpoint = ref('')
const onlineModel = ref('')
const localApiKey = ref('')
const onlineApiKey = ref('')
const clearLocalKey = ref(false)
const clearOnlineKey = ref(false)
const displayName = ref('Lin')
const dailyGoalMinutes = ref(60)

const embeddingSpecId = ref<string | null>(null)
const embeddingSource = ref<'local' | 'cloud'>('local')
const embeddingBusy = ref(false)
const embeddingDirty = ref(false)
const embeddingMessage = ref('')
const embeddingError = ref('')
const importedSpecs = ref<EmbeddingSpec[]>([])
const pickerKey = ref(0)

const importName = ref('')
const importModel = ref('')
const importDimensions = ref<number | null>(null)
const importMaxSeq = ref(512)
const importRevision = ref('1')
const importModelFile = ref<File | null>(null)
const importTokenizerFile = ref<File | null>(null)

const cloudName = ref('')
const cloudEndpoint = ref('')
const cloudModel = ref('')
const cloudApiKey = ref('')
const cloudDimensions = ref(1024)
const cloudMaxSeq = ref(512)
const cloudRevision = ref('1')

const mcpStatus = ref<McpStatus | null>(null)
const agentInfo = ref<AgentInfo | null>(null)
const agentTools = ref<AgentToolInfo[]>([])
const agentBusy = ref(false)
const agentError = ref('')

// 一键复制 MCP 配置：端口是后端随机分配的，只有它能告诉我真实入口。
const mcpClientKey = ref('')
const mcpCopyMessage = ref('')
const mcpCopyError = ref('')
// 复制出来的地址「只在本机可用」时给一条警告，不报错——配置本身是能用的。
const mcpBaseUrlWarning = ref('')

const whisperStatus = ref<WhisperModelStatus | null>(null)
const whisperBusy = ref(false)
const whisperMessage = ref('')
const whisperError = ref('')
const whisperFile = ref<File | null>(null)
const whisperPickerKey = ref(0)

const agentSettings = ref<AgentSettings>(normalizeAgentSettings(null))

const toolGroups = computed(() => {
  const labels: Record<string, string> = { knowledge: '知识库工具', workspace: '本地文件工具', mcp: 'MCP 工具' }
  const groups = new Map<string, AgentToolInfo[]>()
  for (const tool of agentTools.value) {
    const key = labels[tool.category] ?? '其他工具'
    groups.set(key, [...(groups.get(key) ?? []), tool])
  }
  return [...groups.entries()].map(([label, tools]) => ({ label, tools }))
})

async function loadAgentInfo() {
  if (!props.apiOnline) return
  agentBusy.value = true
  agentError.value = ''
  try {
    const [status, tools, info] = await Promise.all([getMcpStatus(), listAgentTools(), getAgentInfo()])
    mcpStatus.value = status
    agentTools.value = tools
    agentInfo.value = info
  } catch (error) {
    agentError.value = errorText(error)
  } finally {
    agentBusy.value = false
  }
}

/**
 * 复制本地 MCP 配置。
 *
 * 这是不依赖 OAuth、不依赖 Server Card、不依赖 mDNS 的「确定可用入口」：
 * 拿到真实端口 + 用户填的 maf_ 密钥，粘贴就能用。
 */
async function copyMcpConfig(target: 'cursor' | 'claude') {
  mcpCopyMessage.value = ''
  mcpCopyError.value = ''
  mcpBaseUrlWarning.value = ''
  if (!props.apiOnline) {
    mcpCopyError.value = 'API 未连接，暂时拿不到真实端口。'
    return
  }

  try {
    const config = await getLocalMcpConfig(mcpClientKey.value.trim() || undefined)
    const text = target === 'cursor' ? config.cursor : config.claudeDesktop
    await navigator.clipboard.writeText(text)
    mcpCopyMessage.value = target === 'cursor'
      ? `已复制 Cursor 配置（${config.endpoint}），粘贴到 ~/.cursor/mcp.json 即可。`
      : `已复制 Claude Desktop 配置（${config.endpoint}），粘贴到 claude_desktop_config.json 即可。`
    // 服务端部署时后端会返回一条「回环地址只在本机有效」的提示，原样转述。
    const deploymentNote = config.notes.find(note => note.includes('PublicBaseUrl'))
    if (deploymentNote) mcpBaseUrlWarning.value = deploymentNote
  } catch (error) {
    mcpCopyError.value = errorText(error)
  }
}

// ── Whisper 模型：下载是后台任务，靠轮询把进度拉回来 ──────────────────────
let whisperTimer: number | undefined

async function loadWhisperStatus(silent = false) {
  if (!props.apiOnline) return
  if (!silent) whisperMessage.value = ''
  try {
    whisperStatus.value = await getWhisperModelStatus()
    if (whisperStatus.value.lastError) whisperError.value = whisperStatus.value.lastError
    ensureWhisperPolling()
  } catch (error) {
    if (!silent) whisperError.value = errorText(error)
  }
}

function ensureWhisperPolling() {
  const downloading = whisperStatus.value?.downloading === true
  if (downloading && whisperTimer === undefined) {
    // 下载还在跑：1.5 秒问一次，顺便把已下载字节数当进度条用。
    whisperTimer = window.setInterval(() => { void loadWhisperStatus(true) }, 1500)
  } else if (!downloading && whisperTimer !== undefined) {
    window.clearInterval(whisperTimer)
    whisperTimer = undefined
  }
}

function onWhisperFileChange(event: Event) {
  whisperFile.value = (event.target as HTMLInputElement).files?.[0] ?? null
  whisperError.value = ''
  whisperMessage.value = ''
}

async function importWhisper() {
  if (whisperBusy.value || !whisperFile.value) {
    if (!whisperFile.value) whisperError.value = '请选择要导入的 .bin 模型文件。'
    return
  }
  whisperBusy.value = true
  whisperError.value = ''
  whisperMessage.value = ''
  try {
    whisperStatus.value = await importWhisperModel(whisperFile.value)
    whisperFile.value = null
    whisperPickerKey.value += 1
    whisperMessage.value = '模型已导入，下一段视频会直接使用它转写（不需要重启）。'
  } catch (error) {
    whisperError.value = errorText(error)
  } finally {
    whisperBusy.value = false
  }
}

async function downloadWhisper() {
  if (whisperBusy.value) return
  whisperBusy.value = true
  whisperError.value = ''
  whisperMessage.value = ''
  try {
    whisperStatus.value = await downloadWhisperModel()
    whisperMessage.value = '已开始后台下载，进度会自动刷新。'
    ensureWhisperPolling()
  } catch (error) {
    whisperError.value = errorText(error)
  } finally {
    whisperBusy.value = false
  }
}

async function removeWhisper() {
  if (whisperBusy.value) return
  whisperBusy.value = true
  whisperError.value = ''
  whisperMessage.value = ''
  try {
    whisperStatus.value = await deleteWhisperModel()
    whisperMessage.value = '模型已删除。下次导入视频前需要重新导入或下载。'
  } catch (error) {
    whisperError.value = errorText(error)
  } finally {
    whisperBusy.value = false
  }
}

// ── 桌面版自动更新（P0-3）────────────────────────────────────────────────
// 只有 Electron 壳里才注入 window.desktopHost；在浏览器里打开时整块不渲染。
// 状态由主进程推送（检查中 / 发现新版本 / 下载中 / 可安装 / 已是最新 / 出错）。
interface DesktopUpdateState {
  phase: 'idle' | 'checking' | 'available' | 'downloading' | 'downloaded' | 'up-to-date' | 'error' | 'disabled'
  currentVersion: string
  version: string | null
  percent: number
  error: string | null
}

interface DesktopHostBridge {
  getAppInfo: () => Promise<{ version: string; electron: string; platform: string; packaged: boolean }>
  getUpdateState: () => Promise<DesktopUpdateState | null>
  checkForUpdates: () => Promise<DesktopUpdateState | null>
  downloadUpdate: () => Promise<{ ok: boolean; error?: string } | null>
  installUpdate: () => Promise<{ ok: boolean; error?: string }>
  onUpdateStatus: (callback: (state: DesktopUpdateState) => void) => () => void
  getBackendState: () => Promise<{ mode: 'spawned' | 'attached'; processId: number | null; stopBackendOnExit: boolean }>
  getPrefs: () => Promise<{ stopBackendOnExit: boolean }>
  setPrefs: (patch: { stopBackendOnExit?: boolean }) => Promise<{ stopBackendOnExit: boolean }>
}

const desktopHost = (window as unknown as { desktopHost?: DesktopHostBridge }).desktopHost
const isDesktop = Boolean(desktopHost)
const appVersion = ref('')
const electronVersion = ref('')
const updateState = ref<DesktopUpdateState | null>(null)
const updateBusy = ref(false)
const updateError = ref('')
let unsubscribeUpdate: (() => void) | null = null

const updateLabel = computed(() => {
  const state = updateState.value
  if (!state) return '未检查'
  switch (state.phase) {
    case 'checking': return '正在检查…'
    case 'available': return `发现新版本 ${state.version ?? ''}`.trim()
    case 'downloading': return `下载中 ${state.percent}%`
    case 'downloaded': return `已下载 ${state.version ?? ''}，可重启安装`
    case 'up-to-date': return '已是最新版本'
    case 'disabled': return '不可用'
    case 'error': return '检查失败'
    default: return '未检查'
  }
})
const canInstallUpdate = computed(() => updateState.value?.phase === 'downloaded')

async function initDesktopUpdate() {
  if (!desktopHost) return
  try {
    const [info, state] = await Promise.all([desktopHost.getAppInfo(), desktopHost.getUpdateState()])
    appVersion.value = info.version
    electronVersion.value = info.electron
    updateState.value = state
    // 订阅推送；组件卸载时必须退订，否则每次进设置页都会多一个监听器。
    unsubscribeUpdate = desktopHost.onUpdateStatus((next) => {
      updateState.value = next
      if (next.phase === 'downloaded') updateError.value = ''
    })
  } catch (error) {
    updateError.value = errorText(error)
  }
}

async function checkDesktopUpdate() {
  if (!desktopHost || updateBusy.value) return
  updateBusy.value = true
  updateError.value = ''
  try {
    const state = await desktopHost.checkForUpdates()
    if (state) updateState.value = state
    // 手动检查必须给结论，否则用户会以为按钮没生效。
    if (state?.phase === 'error') updateError.value = state.error ?? '检查更新失败。'
  } catch (error) {
    updateError.value = errorText(error)
  } finally {
    updateBusy.value = false
  }
}

async function downloadDesktopUpdate() {
  if (!desktopHost || updateBusy.value) return
  updateBusy.value = true
  updateError.value = ''
  try {
    const result = await desktopHost.downloadUpdate()
    if (result && !result.ok) updateError.value = result.error ?? '下载失败。'
  } catch (error) {
    updateError.value = errorText(error)
  } finally {
    updateBusy.value = false
  }
}

async function installDesktopUpdate() {
  if (!desktopHost || updateBusy.value) return
  updateBusy.value = true
  updateError.value = ''
  try {
    // 主进程会先停 API（连同便携数据库），再交给安装器；成功后进程会退出，走不到下一行。
    const result = await desktopHost.installUpdate()
    if (!result.ok) updateError.value = result.error ?? '安装失败。'
  } catch (error) {
    updateError.value = errorText(error)
  } finally {
    updateBusy.value = false
  }
}

// ── 桌面版后台服务 ───────────────────────────────────────────────────────
// 关窗口默认不动后端：它可以被当成常驻服务用。勾上「关闭所有服务」后，
// 退出时主进程会先给 /api/desktop/shutdown 发一条命令，让后端连同便携数据库优雅退出。
const backendState = ref<{ mode: 'spawned' | 'attached'; processId: number | null } | null>(null)
const stopBackendOnExit = ref(false)
const backendPrefBusy = ref(false)
const backendError = ref('')

const backendModeLabel = computed(() => {
  if (!backendState.value) return '—'
  return backendState.value.mode === 'attached' ? '接管已在运行的后台服务' : '由本窗口启动'
})

async function initDesktopBackend() {
  if (!desktopHost) return
  try {
    const state = await desktopHost.getBackendState()
    backendState.value = { mode: state.mode, processId: state.processId }
    stopBackendOnExit.value = state.stopBackendOnExit
  } catch (error) {
    backendError.value = errorText(error)
  }
}

async function setStopBackendOnExit(next: boolean) {
  if (!desktopHost || backendPrefBusy.value) return
  backendPrefBusy.value = true
  backendError.value = ''
  try {
    const prefs = await desktopHost.setPrefs({ stopBackendOnExit: next })
    stopBackendOnExit.value = prefs.stopBackendOnExit
  } catch (error) {
    backendError.value = errorText(error)
  } finally {
    backendPrefBusy.value = false
  }
}

onMounted(() => { void loadAgentInfo(); void loadWhisperStatus(); void initDesktopUpdate(); void initDesktopBackend() })
watch(() => props.apiOnline, online => { if (online) { void loadAgentInfo(); void loadWhisperStatus() } })

watch(() => props.settings, (settings) => {
  if (!settings) return
  useCloudApi.value = settings.useCloudApi
  localEndpoint.value = settings.local.endpoint
  localModel.value = settings.local.model
  onlineEndpoint.value = settings.online.endpoint
  onlineModel.value = settings.online.model
  displayName.value = settings.preferences.displayName
  dailyGoalMinutes.value = settings.preferences.dailyGoalMinutes
  if (!embeddingDirty.value) embeddingSpecId.value = settings.embedding.defaultSpecId
  // 旧后端不返回 agent 字段时回落到默认值（工具调用/联网关闭、文件访问开启）。
  agentSettings.value = normalizeAgentSettings(settings.agent)
  localApiKey.value = ''
  onlineApiKey.value = ''
  clearLocalKey.value = false
  clearOnlineKey.value = false
}, { immediate: true })

// 后台索引构建期间自动刷新状态。
let pollTimer: number | undefined
watch(() => props.settings?.embedding.indexes.some(index => index.status === 'Building') ?? false, (building) => {
  if (building && pollTimer === undefined) {
    pollTimer = window.setInterval(() => emit('reload'), 4000)
  } else if (!building && pollTimer !== undefined) {
    window.clearInterval(pollTimer)
    pollTimer = undefined
  }
}, { immediate: true })
onBeforeUnmount(() => {
  if (pollTimer !== undefined) window.clearInterval(pollTimer)
  if (whisperTimer !== undefined) window.clearInterval(whisperTimer)
  unsubscribeUpdate?.()
  unsubscribeUpdate = null
})

const mergedSpecs = computed(() => {
  const base = props.settings?.embedding.specs ?? []
  const extras = importedSpecs.value.filter(spec => !base.some(item => item.id === spec.id))
  return [...base, ...extras]
})
const canSave = computed(() => props.apiOnline && !props.busy && props.settings !== null)

function submit() {
  if (!canSave.value) return
  // 数据卫生：联网搜索必须依附于工具调用，绝不上报 enableToolCalling=false + enableWebSearch=true 的矛盾组合。
  const agentPayload: AgentSettings = {
    ...agentSettings.value,
    enableWebSearch: agentSettings.value.enableWebSearch && agentSettings.value.enableToolCalling,
  }
  emit('save', {
    useCloudApi: useCloudApi.value,
    local: {
      endpoint: localEndpoint.value.trim(),
      model: localModel.value.trim(),
      apiKey: clearLocalKey.value ? '' : localApiKey.value.trim() || undefined,
    },
    online: {
      endpoint: onlineEndpoint.value.trim(),
      model: onlineModel.value.trim(),
      apiKey: clearOnlineKey.value ? '' : onlineApiKey.value.trim() || undefined,
    },
    preferences: {
      displayName: displayName.value.trim() || 'Lin',
      dailyGoalMinutes: Math.max(0, Math.min(1440, Math.round(dailyGoalMinutes.value || 0))),
    },
    embeddingSpecId: embeddingSpecId.value ?? undefined,
    agent: agentPayload,
  })
  agentSettings.value = agentPayload
  embeddingDirty.value = false
  // 设置保存后重新读取智能体信息与工具列表，让下方状态与新开关保持一致。
  window.setTimeout(() => { if (props.apiOnline) void loadAgentInfo() }, 600)
}

function setToolCalling(enabled: boolean) {
  // 关闭工具调用时同步关闭联网搜索，避免内存态与最终提交值长期不一致。
  agentSettings.value = {
    ...agentSettings.value,
    enableToolCalling: enabled,
    enableWebSearch: enabled ? agentSettings.value.enableWebSearch : false,
  }
}

function setWebSearch(enabled: boolean) {
  // 工具调用关闭时联网搜索不可单独开启，避免出现“开关打开但不会被执行”的错觉。
  agentSettings.value = { ...agentSettings.value, enableWebSearch: enabled && agentSettings.value.enableToolCalling }
}

function setFileAccess(enabled: boolean) {
  agentSettings.value = { ...agentSettings.value, enableFileAccess: enabled }
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '操作失败，请稍后重试。'
}

function onModelFileChange(event: Event) {
  importModelFile.value = (event.target as HTMLInputElement).files?.[0] ?? null
}
function onTokenizerFileChange(event: Event) {
  importTokenizerFile.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function importLocalModel() {
  if (embeddingBusy.value) return
  embeddingMessage.value = ''
  embeddingError.value = ''
  if (!importModelFile.value || !importTokenizerFile.value) {
    embeddingError.value = '请同时选择 model.onnx 与 tokenizer.json。'
    return
  }

  embeddingBusy.value = true
  try {
    const spec = await importLocalEmbeddingSpec({
      name: importName.value.trim() || importModelFile.value.name.replace(/\.onnx$/i, ''),
      model: importModel.value.trim() || undefined,
      dimensions: importDimensions.value || null,
      maxSequenceLength: Math.max(2, Math.min(8192, Math.round(importMaxSeq.value || 512))),
      revision: importRevision.value.trim() || '1',
      modelFile: importModelFile.value,
      tokenizerFile: importTokenizerFile.value,
    })
    importedSpecs.value = [spec, ...importedSpecs.value.filter(item => item.id !== spec.id)]
    embeddingSpecId.value = spec.id
    embeddingDirty.value = true
    importName.value = ''
    importModel.value = ''
    importModelFile.value = null
    importTokenizerFile.value = null
    importDimensions.value = null
    pickerKey.value += 1
    embeddingMessage.value = `已导入“${spec.name}”（${spec.dimensions} 维）。点击底部“保存设置”后开始后台重建索引。`
  } catch (error) {
    embeddingError.value = errorText(error)
  } finally {
    embeddingBusy.value = false
  }
}

async function registerCloudModel() {
  if (embeddingBusy.value) return
  embeddingMessage.value = ''
  embeddingError.value = ''
  if (!cloudEndpoint.value.trim() || !cloudModel.value.trim()) {
    embeddingError.value = '请填写云端接口地址与模型名称。'
    return
  }

  embeddingBusy.value = true
  try {
    const spec = await registerCloudEmbeddingSpec({
      name: cloudName.value.trim() || cloudModel.value.trim(),
      endpoint: cloudEndpoint.value.trim(),
      model: cloudModel.value.trim(),
      apiKey: cloudApiKey.value.trim() || undefined,
      dimensions: Math.max(1, Math.min(8192, Math.round(cloudDimensions.value || 1024))),
      maxSequenceLength: Math.max(2, Math.min(8192, Math.round(cloudMaxSeq.value || 512))),
      revision: cloudRevision.value.trim() || '1',
    })
    importedSpecs.value = [spec, ...importedSpecs.value.filter(item => item.id !== spec.id)]
    embeddingSpecId.value = spec.id
    embeddingDirty.value = true
    cloudName.value = ''
    cloudApiKey.value = ''
    embeddingMessage.value = `已注册“${spec.name}”（${spec.dimensions} 维）。点击底部“保存设置”后开始后台重建索引。`
  } catch (error) {
    embeddingError.value = errorText(error)
  } finally {
    embeddingBusy.value = false
  }
}

async function probeSpec(specId: string) {
  if (embeddingBusy.value) return
  embeddingMessage.value = ''
  embeddingError.value = ''
  embeddingBusy.value = true
  try {
    const result = await probeEmbeddingSpec(specId)
    embeddingMessage.value = `连接正常：返回 ${result.dimensions} 维向量，耗时 ${result.elapsedMilliseconds} ms。`
  } catch (error) {
    embeddingError.value = errorText(error)
  } finally {
    embeddingBusy.value = false
  }
}

function indexStatusLabel(status: string) {
  return ({ Building: '构建中', Ready: '待切换', Active: '使用中', Retired: '已下线', Failed: '失败' } as Record<string, string>)[status] ?? status
}

function knowledgeBaseName(id: string) {
  return props.knowledgeBaseNames[id] || '知识库'
}

// ---- 知识库包（导出 / 导入） ----

const packageKnowledgeBaseId = ref('')
const packageExporting = ref(false)
const packageImporting = ref(false)
const packageMessage = ref('')
const packageError = ref('')
const packageWarnings = ref<string[]>([])
const packageFileInput = ref<HTMLInputElement>()

const packageKnowledgeBases = computed(() => props.knowledgeBases ?? [])
const packageKnowledgeBaseName = computed(() => knowledgeBaseName(packageKnowledgeBaseId.value))

// 跟着当前知识库走，但允许用户在这里临时换成别的库做备份。
watch(() => [props.activeKnowledgeBaseId, packageKnowledgeBases.value.length], () => {
  if (!packageKnowledgeBaseId.value && props.activeKnowledgeBaseId) packageKnowledgeBaseId.value = props.activeKnowledgeBaseId
}, { immediate: true })

function resetPackageFeedback() {
  packageMessage.value = ''
  packageError.value = ''
  packageWarnings.value = []
}

async function exportPackage() {
  if (!packageKnowledgeBaseId.value || packageExporting.value) return
  resetPackageFeedback()
  packageExporting.value = true
  try {
    const result = await exportKnowledgeBasePackage(packageKnowledgeBaseId.value)
    // 用户在“另存为”里点了取消：安静退出，不报错也不提示成功。
    if (result.cancelled) return

    const target = result.savedTo ? `到「${result.savedTo}」` : ''
    packageMessage.value = `已导出${target}「${packageKnowledgeBaseName.value}」：资料 ${result.documents} 份、笔记 ${result.notes} 篇、专题 ${result.topics} 个。`
    if (result.warnings) packageWarnings.value = [`导出过程中有 ${result.warnings} 条内容被跳过（详见包内 manifest.json 的 warnings）。`]
  } catch (error) {
    packageError.value = errorText(error)
  } finally {
    packageExporting.value = false
  }
}

function pickPackageFile() {
  packageFileInput.value?.click()
}

async function onPackageFileSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return

  resetPackageFeedback()
  packageImporting.value = true
  try {
    const result = await importKnowledgeBasePackage(file)
    packageMessage.value = `已导入为「${result.knowledgeBaseName}」：资料 ${result.documents} 份、笔记 ${result.notes} 篇、专题 ${result.topics} 个。资料正在后台解析，稍等片刻即可检索。`
    packageWarnings.value = result.warnings
    emit('imported', result.knowledgeBaseId)
  } catch (error) {
    packageError.value = errorText(error)
  } finally {
    packageImporting.value = false
  }
}

function providerLabel(provider: string) {
  return provider === 'onnx-local' ? '本地' : '云端'
}

function formatSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  if (bytes < 1024 ** 3) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`
}

const whisperStateLabel = computed(() => {
  const status = whisperStatus.value
  if (!status) return '读取中…'
  if (status.downloading) return `下载中 · 已获取 ${formatSize(status.downloadedBytes)}`
  return status.exists ? '已就绪' : '未安装'
})

const whisperReady = computed(() => whisperStatus.value?.exists === true)
</script>

<template>
  <section class="page-heading-row">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> PREFERENCES &amp; MODELS</div>
      <h1>设置</h1>
      <p class="welcome-copy">模型与学习偏好都保存在本机数据库中，修改后立即生效。</p>
    </div>
    <button class="button button-soft" :disabled="!apiOnline || loading" @click="emit('reload')">
      <RefreshCw :size="15" :class="{ spinning: loading }" /> 重新读取
    </button>
  </section>

  <div v-if="!settings" class="empty-state">
    <LoaderCircle :size="22" :class="{ spinning: loading }" />
    <strong>{{ apiOnline ? (loading ? '正在读取设置…' : '暂时无法读取设置') : '无法连接本地服务' }}</strong>
    <span>{{ apiOnline ? (loading ? '请稍候' : '请点击右上角“重新读取”再试一次。') : '请先启动 API 服务后再修改设置。' }}</span>
  </div>

  <template v-else>
    <section class="settings-grid">
      <article class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon"><Cpu :size="18" /></span>
          <div><h2>AI 聊天模型</h2><p>问答、摘要、图片描述和 AI 伴侣都会使用当前选择的模型。</p></div>
        </header>

        <div class="settings-segmented" role="group" aria-label="模型来源">
          <button :class="{ selected: !useCloudApi }" @click="useCloudApi = false">本地模型</button>
          <button :class="{ selected: useCloudApi }" @click="useCloudApi = true">在线 API</button>
        </div>

        <div class="settings-endpoint-grid">
          <div class="settings-endpoint" :class="{ 'settings-endpoint-active': !useCloudApi }">
            <div class="settings-endpoint-title"><span>本地模型</span><span v-if="!useCloudApi" class="settings-active-tag"><Check :size="11" /> 使用中</span></div>
            <label class="settings-field"><span>接口地址</span><input v-model="localEndpoint" placeholder="http://localhost:1234/v1" /></label>
            <label class="settings-field"><span>模型名称</span><input v-model="localModel" placeholder="local-model" /></label>
            <label class="settings-field">
              <span>API Key（可选）</span>
              <input v-model="localApiKey" type="password" autocomplete="off" :placeholder="settings.local.hasApiKey ? `已保存 ${settings.local.apiKeyHint}，留空保持不变` : '未设置，通常本地服务无需填写'" />
            </label>
            <button v-if="settings.local.hasApiKey && !clearLocalKey" class="settings-clear-key" @click="clearLocalKey = true">清除已保存的本地密钥</button>
            <button v-else-if="clearLocalKey" class="settings-clear-key" @click="clearLocalKey = false">撤销清除</button>
          </div>

          <div class="settings-endpoint" :class="{ 'settings-endpoint-active': useCloudApi }">
            <div class="settings-endpoint-title"><span>在线 API</span><span v-if="useCloudApi" class="settings-active-tag"><Check :size="11" /> 使用中</span></div>
            <label class="settings-field"><span>接口地址</span><input v-model="onlineEndpoint" placeholder="https://api.example.com/v1" /></label>
            <label class="settings-field"><span>模型名称</span><input v-model="onlineModel" placeholder="chat-model" /></label>
            <label class="settings-field">
              <span>API Key</span>
              <input v-model="onlineApiKey" type="password" autocomplete="off" :placeholder="settings.online.hasApiKey ? `已保存 ${settings.online.apiKeyHint}，留空保持不变` : '请输入服务商提供的密钥'" />
            </label>
            <button v-if="settings.online.hasApiKey && !clearOnlineKey" class="settings-clear-key" @click="clearOnlineKey = true">清除已保存的在线密钥</button>
            <button v-else-if="clearOnlineKey" class="settings-clear-key" @click="clearOnlineKey = false">撤销清除</button>
          </div>
        </div>

        <p class="settings-hint"><KeyRound :size="13" /> 密钥只保存在本机数据库，接口返回时仅显示末四位；云端模式下，提问内容与检索片段会发送给你配置的服务商。</p>
      </article>

      <article class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Boxes :size="18" /></span>
          <div><h2>向量模型（Embedding）</h2><p>决定资料如何向量化与检索。切换后会为每个知识库构建阴影索引，校验通过后原子切换，构建期间旧索引继续服务。</p></div>
        </header>

        <div class="settings-segmented" role="group" aria-label="向量模型来源">
          <button :class="{ selected: embeddingSource === 'local' }" @click="embeddingSource = 'local'">本地导入</button>
          <button :class="{ selected: embeddingSource === 'cloud' }" @click="embeddingSource = 'cloud'">在线 API</button>
        </div>

        <div v-if="embeddingSource === 'local'" class="settings-endpoint settings-endpoint-active">
          <div class="settings-endpoint-title">
            <span>本地 ONNX 模型</span>
            <span class="settings-badge">model.onnx + tokenizer.json</span>
          </div>
          <div class="settings-endpoint-grid">
            <label class="settings-field"><span>规格名称</span><input v-model="importName" maxlength="100" placeholder="例如 bge-m3-local" /></label>
            <label class="settings-field"><span>模型标识（可选）</span><input v-model="importModel" placeholder="留空则使用规格名称" /></label>
            <label class="settings-field">
              <span>模型文件（.onnx）</span>
              <input :key="`model-${pickerKey}`" type="file" accept=".onnx" @change="onModelFileChange" />
            </label>
            <label class="settings-field">
              <span>Tokenizer（.json）</span>
              <input :key="`tokenizer-${pickerKey}`" type="file" accept=".json,application/json" @change="onTokenizerFileChange" />
            </label>
            <label class="settings-field"><span>向量维度（留空自动识别）</span><input v-model.number="importDimensions" type="number" min="1" max="8192" placeholder="自动" /></label>
            <label class="settings-field"><span>最大序列长度</span><input v-model.number="importMaxSeq" type="number" min="2" max="8192" /></label>
            <label class="settings-field"><span>版本（revision）</span><input v-model="importRevision" placeholder="1" /></label>
          </div>
          <button class="button button-soft settings-inline-action" :disabled="embeddingBusy || !apiOnline" @click="importLocalModel">
            <LoaderCircle v-if="embeddingBusy" :size="15" class="spinning" /><Upload v-else :size="15" />
            {{ embeddingBusy ? '导入并验证中…' : '导入并注册模型' }}
          </button>
        </div>

        <div v-else class="settings-endpoint settings-endpoint-active">
          <div class="settings-endpoint-title">
            <span>OpenAI 兼容向量接口</span>
            <span class="settings-badge">POST /embeddings</span>
          </div>
          <div class="settings-endpoint-grid">
            <label class="settings-field"><span>规格名称</span><input v-model="cloudName" maxlength="100" placeholder="例如 text-embedding-3-small" /></label>
            <label class="settings-field"><span>接口地址</span><input v-model="cloudEndpoint" placeholder="https://api.example.com/v1" /></label>
            <label class="settings-field"><span>模型名称</span><input v-model="cloudModel" placeholder="text-embedding-3-small" /></label>
            <label class="settings-field">
              <span>API Key</span>
              <input v-model="cloudApiKey" type="password" autocomplete="off" placeholder="请输入服务商提供的密钥" />
            </label>
            <label class="settings-field"><span>向量维度</span><input v-model.number="cloudDimensions" type="number" min="1" max="8192" /></label>
            <label class="settings-field"><span>最大序列长度</span><input v-model.number="cloudMaxSeq" type="number" min="2" max="8192" /></label>
            <label class="settings-field"><span>版本（revision）</span><input v-model="cloudRevision" placeholder="1" /></label>
          </div>
          <p class="settings-hint"><Cloud :size="13" /> 注册前会真实调用一次 /embeddings 验证端点、密钥与维度；成功后仍建议点击“测试”确认。</p>
          <button class="button button-soft settings-inline-action" :disabled="embeddingBusy || !apiOnline" @click="registerCloudModel">
            <LoaderCircle v-if="embeddingBusy" :size="15" class="spinning" /><Cloud v-else :size="15" />
            {{ embeddingBusy ? '验证并注册中…' : '测试并注册模型' }}
          </button>
        </div>

        <div class="settings-spec-list">
          <div class="settings-spec-list-head">
            <span>已注册的向量模型</span>
            <span>选中后点击底部“保存设置”生效</span>
          </div>
          <label v-for="spec in mergedSpecs" :key="spec.id" class="settings-spec-item" :class="{ selected: spec.id === embeddingSpecId }">
            <input v-model="embeddingSpecId" type="radio" :value="spec.id" @change="embeddingDirty = true" />
            <div class="settings-spec-main">
              <strong>
                {{ spec.name }}
                <span v-if="spec.id === settings.embedding.defaultSpecId" class="settings-badge">当前默认</span>
              </strong>
              <span class="settings-spec-meta">{{ providerLabel(spec.provider) }} · {{ spec.model }} · {{ spec.dimensions }} 维 · 序列 {{ spec.maxSequenceLength }} · rev {{ spec.revision }}</span>
            </div>
            <button type="button" class="settings-link-button" :disabled="embeddingBusy" @click.prevent="probeSpec(spec.id)">测试</button>
          </label>
          <p v-if="!mergedSpecs.length" class="settings-hint">还没有注册任何向量模型，请先导入本地模型或注册云端模型。</p>
        </div>

        <div v-if="settings.embedding.indexes.length" class="settings-index-list">
          <div v-for="index in settings.embedding.indexes" :key="index.knowledgeBaseId" class="settings-index-item">
            <div class="settings-index-head">
              <span>{{ knowledgeBaseName(index.knowledgeBaseId) }}</span>
              <span>{{ indexStatusLabel(index.status) }}<template v-if="index.specName"> · {{ index.specName }}</template></span>
            </div>
            <div class="settings-progress"><span :style="{ width: `${Math.max(2, Math.min(100, index.progress))}%` }"></span></div>
            <span class="settings-spec-meta">{{ index.stage || '等待后台处理' }}</span>
          </div>
        </div>

        <p v-if="embeddingMessage" class="settings-hint settings-hint-success"><Check :size="13" /> {{ embeddingMessage }}</p>
        <p v-if="embeddingError" class="settings-hint settings-hint-error">{{ embeddingError }}</p>
      </article>

      <article class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Mic :size="18" /></span>
          <div><h2>语音转写（Whisper）</h2><p>视频资料会先抽出音轨，再用 whisper.cpp 的 ggml 模型转写。模型 141 MB 起，不随安装包分发：离线环境请导入本地模型文件。</p></div>
          <button class="button button-soft settings-head-action" :disabled="!apiOnline || whisperBusy" @click="loadWhisperStatus()">
            <LoaderCircle v-if="whisperBusy" :size="14" class="spinning" /><RefreshCw v-else :size="14" /> 刷新
          </button>
        </header>

        <div class="settings-endpoint settings-endpoint-active">
          <div class="settings-endpoint-title">
            <span>当前模型</span>
            <span class="settings-badge">{{ whisperStatus?.model ?? 'base' }}</span>
          </div>
          <div class="settings-endpoint-grid">
            <label class="settings-field"><span>状态</span><input :value="whisperStateLabel" readonly /></label>
            <label class="settings-field"><span>体积</span><input :value="whisperReady ? formatSize(whisperStatus?.sizeBytes ?? 0) : '—'" readonly /></label>
            <label class="settings-field" style="grid-column: 1 / -1"><span>保存位置</span><input :value="whisperStatus?.path ?? ''" readonly /></label>
          </div>
          <div class="settings-whisper-actions">
            <label class="settings-field settings-whisper-file">
              <span>导入本地模型（.bin，ggml 格式）</span>
              <input :key="`whisper-${whisperPickerKey}`" type="file" accept=".bin" @change="onWhisperFileChange" />
            </label>
            <button class="button button-soft settings-inline-action" :disabled="whisperBusy || !apiOnline || !whisperFile" @click="importWhisper">
              <LoaderCircle v-if="whisperBusy" :size="15" class="spinning" /><Upload v-else :size="15" /> 导入模型
            </button>
            <button class="button button-soft settings-inline-action" :disabled="whisperBusy || !apiOnline || whisperReady || whisperStatus?.downloading" @click="downloadWhisper">
              <Cloud :size="15" /> 联网下载
            </button>
            <button v-if="whisperReady" class="button button-soft settings-inline-action settings-whisper-remove" :disabled="whisperBusy || !apiOnline" @click="removeWhisper">
              <Trash2 :size="15" /> 删除模型
            </button>
          </div>
        </div>

        <p class="settings-hint"><Mic :size="13" /> 导入的文件会先校验文件头是否为 ggml；校验失败不会覆盖现有模型。导入或下载成功后无需重启，下一段视频会用新模型。</p>
        <p v-if="whisperMessage" class="settings-hint settings-hint-success"><Check :size="13" /> {{ whisperMessage }}</p>
        <p v-if="whisperError" class="settings-hint settings-hint-error">{{ whisperError }}</p>
      </article>

      <article v-if="isDesktop" class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Sparkles :size="18" /></span>
          <div><h2>桌面版更新</h2><p>安装版会每 6 小时自动检查一次；下载完成后重启即可完成升级，资料、插件与数据库都保留在原位置。</p></div>
          <button class="button button-soft settings-head-action" :disabled="updateBusy" @click="checkDesktopUpdate">
            <LoaderCircle v-if="updateBusy" :size="14" class="spinning" /><RefreshCw v-else :size="14" /> 检查更新
          </button>
        </header>

        <div class="settings-endpoint settings-endpoint-active">
          <div class="settings-endpoint-title">
            <span>当前版本</span>
            <span class="settings-badge">{{ appVersion || '—' }}</span>
          </div>
          <div class="settings-endpoint-grid">
            <label class="settings-field"><span>更新状态</span><input :value="updateLabel" readonly /></label>
            <label class="settings-field"><span>Electron</span><input :value="electronVersion || '—'" readonly /></label>
          </div>
          <div class="settings-whisper-actions">
            <button v-if="updateState?.phase === 'available'" class="button button-soft settings-inline-action" :disabled="updateBusy" @click="downloadDesktopUpdate">
              <Download :size="15" /> 立即下载
            </button>
            <button v-if="canInstallUpdate" class="button button-primary settings-inline-action" :disabled="updateBusy" @click="installDesktopUpdate">
              <RotateCcw :size="15" /> 重启并安装
            </button>
          </div>
        </div>

        <p class="settings-hint"><Sparkles :size="13" /> 更新源由安装包或环境变量 <code>MAF_UPDATE_URL</code> 决定；安装前会先停止后端与便携数据库，避免文件被占用。</p>
        <p v-if="updateError" class="settings-hint settings-hint-error">{{ updateError }}</p>
      </article>

      <article v-if="isDesktop" class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Server :size="18" /></span>
          <div><h2>后台服务</h2><p>关掉窗口只是关掉界面，后端会继续在后台跑；下次打开窗口会直接接管它，不会再启动第二个后端。</p></div>
        </header>

        <div class="settings-endpoint settings-endpoint-active">
          <div class="settings-endpoint-title">
            <span>服务来源</span>
            <span class="settings-badge">{{ backendModeLabel }}</span>
          </div>
          <div class="settings-endpoint-grid">
            <label class="settings-field"><span>进程 ID</span><input :value="backendState?.processId ?? '—'" readonly /></label>
          </div>

          <div class="settings-agent-switches">
            <label class="settings-switch" @click.prevent="!backendPrefBusy && setStopBackendOnExit(!stopBackendOnExit)">
              <span class="settings-switch-text">
                <strong>关闭所有服务</strong>
                <small v-if="stopBackendOnExit">退出时一并停止后端与便携数据库；下次打开窗口会重新拉起。</small>
                <small v-else>退出只关窗口，后端与数据库保持运行，适合当常驻服务用。</small>
              </span>
              <input class="settings-switch-input" type="checkbox" :checked="stopBackendOnExit" :disabled="backendPrefBusy" @change="setStopBackendOnExit(($event.target as HTMLInputElement).checked)" />
              <span class="settings-switch-track" :class="{ on: stopBackendOnExit, disabled: backendPrefBusy }" aria-hidden="true"><span></span></span>
            </label>
          </div>
        </div>

        <p class="settings-hint"><Power :size="13" /> 命令行或系统服务方式启动的后端同样会被这个窗口接管（读运行时发现文件后探活），不会重复启动。</p>
        <p v-if="backendError" class="settings-hint settings-hint-error">{{ backendError }}</p>
      </article>

      <article class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Plug :size="18" /></span>
          <div><h2>AI 工具与 MCP</h2><p>聊天模型可以通过 function calling 检索知识库、读写本地工作目录，并调用外部 MCP 工具；本机知识库也可作为 MCP 服务端被其他模型使用。</p></div>
          <button class="button button-soft settings-head-action" :disabled="agentBusy || !apiOnline" @click="loadAgentInfo">
            <LoaderCircle v-if="agentBusy" :size="14" class="spinning" /><RefreshCw v-else :size="14" /> 刷新
          </button>
        </header>

        <div class="settings-agent-switches">
          <label class="settings-switch" @click.prevent="setToolCalling(!agentSettings.enableToolCalling)">
            <span class="settings-switch-text">
              <strong>允许模型调用工具</strong>
              <small>开启后模型会在回答前自行检索知识库、读取文件、联网搜索。</small>
            </span>
            <input class="settings-switch-input" type="checkbox" :checked="agentSettings.enableToolCalling" :disabled="!apiOnline" @change="setToolCalling(($event.target as HTMLInputElement).checked)" />
            <span class="settings-switch-track" :class="{ on: agentSettings.enableToolCalling && apiOnline, disabled: !apiOnline }" aria-hidden="true"><span></span></span>
          </label>

          <label class="settings-switch" :class="{ 'settings-switch-muted': !agentSettings.enableToolCalling }" @click.prevent="agentSettings.enableToolCalling && setWebSearch(!agentSettings.enableWebSearch)">
            <span class="settings-switch-text">
              <strong>允许联网搜索</strong>
              <small v-if="agentSettings.enableToolCalling">开启后模型可以调用联网搜索工具获取知识库以外的实时信息。</small>
              <small v-else>需先开启“允许模型调用工具”后才能使用联网搜索。</small>
            </span>
            <input class="settings-switch-input" type="checkbox" :checked="agentSettings.enableWebSearch" :disabled="!apiOnline || !agentSettings.enableToolCalling" @change="setWebSearch(($event.target as HTMLInputElement).checked)" />
            <span class="settings-switch-track" :class="{ on: agentSettings.enableWebSearch && agentSettings.enableToolCalling && apiOnline, disabled: !apiOnline || !agentSettings.enableToolCalling }" aria-hidden="true"><span></span></span>
          </label>

          <label class="settings-switch" @click.prevent="setFileAccess(!agentSettings.enableFileAccess)">
            <span class="settings-switch-text">
              <strong>允许读取本地文件</strong>
              <small>开启后模型可以读取下方“可读取的本机目录”中的文件内容。</small>
            </span>
            <input class="settings-switch-input" type="checkbox" :checked="agentSettings.enableFileAccess" :disabled="!apiOnline" @change="setFileAccess(($event.target as HTMLInputElement).checked)" />
            <span class="settings-switch-track" :class="{ on: agentSettings.enableFileAccess && apiOnline, disabled: !apiOnline }" aria-hidden="true"><span></span></span>
          </label>
        </div>
        <p class="settings-hint"><Check :size="13" /> 修改后点击页面底部“保存设置”生效。这里的开关是默认值；“AI 知识问答”页还可以对单次提问临时覆盖这些开关。</p>

        <dl class="settings-runtime">
          <div><dt>MCP 服务端</dt><dd>{{ mcpStatus ? (mcpStatus.serverEnabled ? `已开启（${mcpStatus.serverPath}）` : '未开启') : '未获取' }}</dd></div>
          <div><dt>外部 MCP 服务器</dt><dd>{{ mcpStatus ? `已连接 ${mcpStatus.connectedCount} / ${mcpStatus.servers.length}` : '未获取' }}</dd></div>
          <div><dt>可调用工具</dt><dd>{{ agentTools.length }} 个（含 MCP 工具 {{ mcpStatus?.toolCount ?? 0 }} 个）</dd></div>
        </dl>

        <div class="settings-tool-groups">
          <div class="settings-tool-group">
            <span class="settings-tool-group-title">MCP 接入配置（一键复制）</span>
            <span class="settings-spec-meta">把下面的配置粘到 Cursor 或 Claude Desktop，就能让它们连上本机知识库。端口是本机随机回环端口，以后端返回的实际值为准。</span>
            <label class="settings-field">
              <span>客户端密钥（可选，maf_ 开头）</span>
              <input v-model="mcpClientKey" placeholder="maf_…" autocomplete="off" spellcheck="false" />
            </label>
            <div class="settings-agent-switches">
              <button class="button button-soft" :disabled="!apiOnline" @click="copyMcpConfig('cursor')">
                <Copy :size="14" /> 复制 Cursor 配置
              </button>
              <button class="button button-soft" :disabled="!apiOnline" @click="copyMcpConfig('claude')">
                <Copy :size="14" /> 复制 Claude Desktop 配置
              </button>
            </div>
            <p v-if="mcpCopyMessage" class="settings-hint"><Check :size="13" /> {{ mcpCopyMessage }}</p>
            <p v-if="mcpBaseUrlWarning" class="settings-hint settings-hint-error">{{ mcpBaseUrlWarning }}</p>
            <p v-if="mcpCopyError" class="settings-hint settings-hint-error">{{ mcpCopyError }}</p>
            <span class="settings-spec-meta">密钥只需要建一次（POST /api/agent-clients，明文只出现一次）。配置里不填密钥也能复制，之后再补。</span>
          </div>
        </div>

        <div class="settings-tool-groups">
          <div class="settings-tool-group">
            <span class="settings-tool-group-title">可读取的本机目录（{{ agentInfo?.fileRoots.length ?? 0 }}）</span>
            <div v-if="agentInfo?.fileRoots.length" class="settings-root-list">
              <div v-for="root in agentInfo.fileRoots" :key="root.path" class="settings-root-item">
                <span class="settings-root-name">{{ root.name }}</span>
                <span class="settings-root-path">{{ root.path }}</span>
                <span class="settings-root-tag" :class="{ 'settings-root-missing': !root.exists }">
                  {{ !root.exists ? '目录不存在' : root.writable ? (root.deletable ? '可读/写/删' : '可读/写') : '只读' }}
                </span>
              </div>
            </div>
            <span v-else class="settings-spec-meta">尚未配置。模型只能访问智能体工作目录。</span>
          </div>
        </div>

        <div v-if="toolGroups.length" class="settings-tool-groups">
          <div v-for="group in toolGroups" :key="group.label" class="settings-tool-group">
            <span class="settings-tool-group-title">{{ group.label }}（{{ group.tools.length }}）</span>
            <div class="settings-tool-chips">
              <span v-for="tool in group.tools" :key="tool.name" class="settings-tool-chip" :title="tool.description">{{ tool.name }}</span>
            </div>
          </div>
        </div>

        <div v-if="mcpStatus?.servers.length" class="settings-index-list">
          <div v-for="server in mcpStatus.servers" :key="server.name" class="settings-index-item">
            <div class="settings-index-head">
              <span>{{ server.name }}</span>
              <span>{{ server.connected ? `已连接 · ${server.toolCount} 个工具` : '未连接' }}</span>
            </div>
            <span v-if="!server.connected" class="settings-spec-meta">{{ server.error || '无法连接' }}</span>
          </div>
        </div>

        <p class="settings-hint">外部 MCP 服务器需在 appsettings.json 的 <code>Mcp:Servers</code> 中配置（支持 http 与 stdio），修改后重启 API 生效。</p>
        <p class="settings-hint">要允许 AI 读取其它目录（如 <code>D:\学习资料</code>），在 <code>Agent:FileRoots</code> 中添加一项并设置 <code>AllowWrite</code>。默认只读，修改后重启 API。</p>
        <p v-if="agentError" class="settings-hint settings-hint-error">{{ agentError }}</p>
      </article>

      <article class="settings-card">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-peach"><UserRound :size="18" /></span>
          <div><h2>学习偏好</h2><p>用于总览问候与每日学习目标。</p></div>
        </header>
        <label class="settings-field"><span>显示名称</span><input v-model="displayName" maxlength="30" placeholder="Lin" /></label>
        <label class="settings-field"><span>每日学习目标（分钟）</span><input v-model.number="dailyGoalMinutes" type="number" min="0" max="1440" step="5" /></label>
        <div class="settings-goal-preview"><SlidersHorizontal :size="14" /> 每天学习 {{ dailyGoalMinutes }} 分钟，一个月约 {{ Math.round(dailyGoalMinutes * 30 / 60) }} 小时。</div>
      </article>

      <article class="settings-card">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-blue"><Database :size="18" /></span>
          <div><h2>本地运行信息</h2><p>只读信息，来自当前 API 配置。</p></div>
        </header>
        <dl class="settings-runtime">
          <div><dt>Embedding 模型</dt><dd>{{ settings.runtime.embeddingModelDirectory }}</dd></div>
          <div><dt>向量维度</dt><dd>{{ settings.runtime.embeddingDimensions }}</dd></div>
          <div><dt>资料存储目录</dt><dd>{{ settings.runtime.storageRootPath }}</dd></div>
          <div><dt>单文件上限</dt><dd>{{ formatSize(settings.runtime.maxFileSizeBytes) }}</dd></div>
          <div><dt>视频上限</dt><dd>{{ formatSize(settings.runtime.maxVideoFileSizeBytes) }}</dd></div>
          <div><dt>API 状态</dt><dd><span class="connection-dot" :class="{ online: apiOnline }"></span>{{ apiOnline ? '已连接' : '未连接' }}</dd></div>
        </dl>
      </article>

      <article class="settings-card settings-card-wide">
        <header class="settings-card-head">
          <span class="settings-icon settings-icon-peach"><Archive :size="18" /></span>
          <div><h2>知识库备份与分享</h2><p>导出成一个可移植的包，换机器、云备份或分享给别人。</p></div>
        </header>

        <label class="settings-field">
          <span>要导出的知识库</span>
          <select v-model="packageKnowledgeBaseId">
            <option v-if="packageKnowledgeBases.length === 0" value="">（暂无知识库）</option>
            <option v-for="item in packageKnowledgeBases" :key="item.id" :value="item.id">{{ item.name }}</option>
          </select>
        </label>

        <p class="settings-package-hint">
          包里包含：原始资料文件、资料摘要、学习笔记（含引用依据与专题）、专题。不含向量与切片——导入方会重新解析建索引，这样换机器也不会失效。
        </p>

        <div class="settings-package-actions">
          <button class="button button-primary" :disabled="!apiOnline || !packageKnowledgeBaseId || packageExporting" @click="exportPackage">
            <LoaderCircle v-if="packageExporting" :size="15" class="spinning" /><ArrowDownToLine v-else :size="15" /> 导出知识库包
          </button>
          <button class="button button-soft" :disabled="!apiOnline || packageImporting" @click="pickPackageFile">
            <LoaderCircle v-if="packageImporting" :size="15" class="spinning" /><ArrowUpFromLine v-else :size="15" /> 导入知识库包
          </button>
          <input ref="packageFileInput" type="file" accept=".zip,.zjpack,application/zip" class="settings-package-file" @change="onPackageFileSelected" />
        </div>

        <div v-if="packageMessage" class="settings-package-message"><Check :size="14" />{{ packageMessage }}</div>
        <div v-if="packageError" class="settings-package-error">{{ packageError }}</div>
        <ul v-if="packageWarnings.length" class="settings-package-warnings">
          <li v-for="(warning, index) in packageWarnings" :key="index">{{ warning }}</li>
        </ul>
      </article>
    </section>

    <div class="settings-save-bar">
      <span>设置保存在本机 PostgreSQL 数据库中，不会上传到外部服务。</span>
      <button class="button button-primary" :disabled="!canSave" @click="submit">
        <LoaderCircle v-if="busy" :size="15" class="spinning" /><Save v-else :size="15" /> 保存设置
      </button>
    </div>
  </template>
</template>

<style scoped>
.settings-card-wide { grid-column: 1 / -1; }
.settings-package-hint { margin: 0; color: #96a29a; font-size: 8px; line-height: 1.7; }
.settings-package-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.settings-package-file { display: none; }
.settings-package-message { display: flex; align-items: flex-start; gap: 6px; padding: 8px 10px; border: 1px solid #dceadf; border-radius: 8px; color: #3f7a5e; background: #f2f8f3; font-size: 9px; line-height: 1.6; }
.settings-package-error { padding: 8px 10px; border: 1px solid #f0dcdc; border-radius: 8px; color: #a54848; background: #faf1f1; font-size: 9px; line-height: 1.6; }
.settings-package-warnings { margin: 0; padding-left: 16px; color: #a08a52; font-size: 8px; line-height: 1.7; }
.settings-agent-switches { display: grid; gap: 8px; margin-top: 12px; }
.settings-switch {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 9px 11px;
  border: 1px solid #eef1ee;
  border-radius: 9px;
  background: #fbfcfb;
  cursor: pointer;
}
.settings-switch:not(.settings-switch-muted):hover { border-color: #dfe8df; background: #f9fbf9; }
.settings-switch-muted { opacity: .62; cursor: not-allowed; }
.settings-switch-muted:hover { border-color: #eef1ee; background: #fbfcfb; }
.settings-switch-text { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.settings-switch-text strong { color: #4d5d54; font-size: 10px; font-weight: 600; }
.settings-switch-text small { color: #96a29a; font-size: 8px; line-height: 1.5; }
.settings-switch-input { position: absolute; width: 1px; height: 1px; opacity: 0; pointer-events: none; }
.settings-switch-track {
  position: relative;
  flex: 0 0 auto;
  width: 32px;
  height: 18px;
  border-radius: 999px;
  background: #e2e7e2;
  transition: background .16s ease;
}
.settings-switch-track > span {
  position: absolute;
  top: 2px;
  left: 2px;
  width: 14px;
  height: 14px;
  border-radius: 50%;
  background: #fff;
  box-shadow: 0 1px 2px #28352d2b;
  transition: transform .16s ease;
}
.settings-switch-track.on { background: #6aa886; }
.settings-switch-track.on > span { transform: translateX(14px); }
/* 禁用态一律呈现“关”：灰底、滑块回左侧，避免看起来像已开启。 */
.settings-switch-track.disabled { background: #eceeec; }
.settings-switch-track.disabled.on { background: #eceeec; }
.settings-switch-track.disabled > span,
.settings-switch-track.disabled.on > span { transform: translateX(0); box-shadow: none; }

/* Whisper 模型：文件选择与按钮同排，窄屏自动折行。 */
.settings-whisper-actions { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 12px; margin-top: 4px; }
.settings-whisper-file { flex: 1 1 260px; min-width: 0; }
.settings-whisper-actions .settings-inline-action { margin-top: 0; }
.settings-whisper-remove { color: #b06a5a; }
</style>
