<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Boxes, Check, Cloud, Cpu, Database, KeyRound, LoaderCircle, Plug, RefreshCw, Save, SlidersHorizontal, Upload, UserRound } from 'lucide-vue-next'
import { getAgentInfo, getMcpStatus, importLocalEmbeddingSpec, listAgentTools, probeEmbeddingSpec, registerCloudEmbeddingSpec } from '../services/api'
import { normalizeAgentSettings } from '../services/api'
import type { AgentInfo, AgentSettings, AgentToolInfo, AppSettings, EmbeddingSpec, McpStatus, SaveSettingsPayload } from '../services/api'

const props = defineProps<{
  settings: AppSettings | null
  apiOnline: boolean
  busy: boolean
  loading: boolean
  knowledgeBaseNames: Record<string, string>
}>()
const emit = defineEmits<{
  save: [payload: SaveSettingsPayload]
  reload: []
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

onMounted(() => { void loadAgentInfo() })
watch(() => props.apiOnline, online => { if (online) void loadAgentInfo() })

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

function providerLabel(provider: string) {
  return provider === 'onnx-local' ? '本地' : '云端'
}

function formatSize(bytes: number) {
  if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`
  if (bytes < 1024 ** 3) return `${(bytes / 1024 / 1024).toFixed(0)} MB`
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`
}
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
</style>
