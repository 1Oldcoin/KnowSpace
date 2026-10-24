<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  Ban, Check, CircleAlert, Clock3, KeyRound, LoaderCircle, Play, Plug,
  RefreshCw, RotateCcw, Save, ShieldCheck, SlidersHorizontal, Trash2, Upload, X,
} from 'lucide-vue-next'
import {
  checkPluginHealth, deleteTrustedKey, drainPlugin, getPlugin, installPlugin, listPluginAudit,
  listPlugins, listPluginSyncStates, listTrustedKeys, pluginPermissionLabels, pluginStateLabels,
  restartPlugin, rollbackPlugin, savePluginConfig, savePluginPermissions, savePluginScope,
  saveTrustedKey, setPluginEnabled, syncPluginAsync, uninstallPlugin,
} from '../services/api'
import type {
  PluginAuditEntry, PluginDetail, PluginState, PluginStatus, PluginSyncStateView, PluginTrustedKeyView,
} from '../services/api'

const props = defineProps<{
  apiOnline: boolean
  knowledgeBases: Array<{ id: string; name: string }>
  activeKnowledgeBaseId: string
}>()
const emit = defineEmits<{ notice: [message: string] }>()

type Tab = 'overview' | 'config' | 'permissions' | 'scope' | 'sync' | 'audit'

interface SchemaField {
  name: string
  type: 'string' | 'number' | 'integer' | 'boolean' | 'enum' | 'string-array' | 'unknown'
  title: string
  description: string
  required: boolean
  options: string[]
  fallback: unknown
}

const plugins = ref<PluginStatus[]>([])
const rootPath = ref('')
const pluginsEnabled = ref(true)
const isLoading = ref(false)
const loadError = ref('')
const installing = ref(false)
const busyAction = ref('')
const installInput = ref<HTMLInputElement | null>(null)

const detail = ref<PluginDetail | null>(null)
const detailLoading = ref(false)
const activeTab = ref<Tab>('overview')
const auditEntries = ref<PluginAuditEntry[]>([])
const syncStates = ref<PluginSyncStateView[]>([])

const trustedKeys = ref<PluginTrustedKeyView[]>([])
const keysLoading = ref(false)
const showKeyForm = ref(false)
const keyForm = reactive({ keyId: '', pluginId: '', algorithm: 'ecdsa', publicKeyPem: '', note: '' })
const savingKey = ref(false)

// 配置表单：schema 能解析就用表单，解析不了就退回原始 JSON 编辑。
const configFields = ref<SchemaField[]>([])
// 注意：v-model 在 <input type="number"> 上会把值转成 number，所以这里不能假定是 string。
const configValues = reactive<Record<string, string | number>>({}) as Record<string, string | number>
const configRaw = ref('')
const configUsesForm = ref(false)
const configSaving = ref(false)

const permissionsDraft = ref<string[]>([])
const permissionsSaving = ref(false)
const scopeDraft = reactive({ knowledgeBaseIds: [] as string[], maxDocuments: 0 })
const scopeSaving = ref(false)
const syncCapability = ref('')
const syncKnowledgeBaseId = ref('')

let refreshTimer: ReturnType<typeof setInterval> | undefined

const stateTone: Record<PluginState, string> = {
  Discovered: 'idle',
  Starting: 'busy',
  Running: 'ok',
  Degraded: 'warn',
  Failed: 'bad',
  Stopped: 'idle',
  NeedsConfiguration: 'warn',
  Disabled: 'idle',
  PermissionRequired: 'warn',
  Draining: 'busy',
}

const auditActionLabels: Record<string, string> = {
  install: '安装', upgrade: '升级', uninstall: '卸载', rollback: '回滚',
  enable: '启用', disable: '停用', start: '启动', stop: '停止',
  invoke: '调用', sync: '同步', config: '配置', permission: '授权', scope: '作用域',
  trust_key: '信任公钥', health: '健康检查', drain: '排空',
}

const allPermissions = computed(() => {
  const union = new Set<string>()
  for (const plugin of plugins.value) {
    for (const permission of [...plugin.grantedPermissions, ...plugin.missingPermissions]) union.add(permission)
  }
  for (const permission of detail.value?.declaredPermissions ?? []) union.add(permission)
  for (const permission of Object.keys(pluginPermissionLabels)) union.add(permission)
  return [...union].sort()
})

const pendingPlugins = computed(() =>
  plugins.value.filter(plugin => plugin.state === 'PermissionRequired' || plugin.state === 'NeedsConfiguration'))

const syncCapabilities = computed(() =>
  (detail.value?.declaredCapabilities ?? []).filter(capability => capability.kind === 'platform-connector'))

const activeKnowledgeBaseName = computed(() =>
  props.knowledgeBases.find(item => item.id === props.activeKnowledgeBaseId)?.name ?? '默认知识库')

function stateLabel(state: PluginState | string) {
  return pluginStateLabels[state as PluginState] ?? state
}

function toneOf(state: PluginState | string) {
  return stateTone[state as PluginState] ?? 'idle'
}

// integrity 是一组状态而非布尔：只有 verified 才算“签名已验证”，
// not-provided / skipped 是中性状态，不能显示成绿色对钩。
const integrityWarnings: Record<string, string> = {
  'digest-mismatch': '包摘要与清单不一致',
  'signature-invalid': '签名无效',
  'untrusted-publisher': '发布者公钥未被信任',
}

function integrityWarning(integrity: string | null | undefined) {
  if (!integrity) return ''
  return integrityWarnings[integrity] ?? ''
}

function permissionLabel(permission: string) {
  return pluginPermissionLabels[permission] ?? permission
}

function actionLabel(action: string) {
  return auditActionLabels[action] ?? action
}

function formatTime(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

function formatRelative(value?: string | null) {
  if (!value) return '—'
  const time = new Date(value).getTime()
  if (Number.isNaN(time)) return '—'
  const seconds = Math.round((Date.now() - time) / 1000)
  if (seconds < 60) return '刚刚'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟前`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时前`
  return `${Math.floor(seconds / 86400)} 天前`
}

function prettyJson(raw?: string | null) {
  if (!raw) return ''
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

async function refresh(options: { silent?: boolean } = {}) {
  if (!props.apiOnline) return
  if (!options.silent) isLoading.value = true
  loadError.value = ''
  try {
    const result = await listPlugins()
    plugins.value = result.plugins
    rootPath.value = result.rootPath
    pluginsEnabled.value = result.enabled
    // 详情面板跟着刷新（版本、状态、在途调用都可能变）
    if (detail.value) await reloadDetail(detail.value.status.id, { silent: true })
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : '无法读取插件列表。'
  } finally {
    isLoading.value = false
  }
}

function parseSchema(schemaText: string | null): SchemaField[] {
  if (!schemaText || !schemaText.trim()) return []
  let schema: unknown
  try {
    schema = JSON.parse(schemaText)
  } catch {
    return []
  }
  if (!schema || typeof schema !== 'object') return []
  const root = schema as { properties?: Record<string, unknown>; required?: unknown }
  if (!root.properties) return []
  const required = Array.isArray(root.required) ? root.required.map(String) : []

  const fields: SchemaField[] = []
  for (const [name, raw] of Object.entries(root.properties)) {
    if (!raw || typeof raw !== 'object') continue
    const property = raw as Record<string, unknown>
    const declaredType = typeof property.type === 'string' ? property.type : Array.isArray(property.type) ? String(property.type[0]) : ''
    const options = Array.isArray(property.enum) ? property.enum.map(String) : []
    const items = property.items as { type?: string } | undefined

    let type: SchemaField['type'] = 'unknown'
    if (options.length > 0) type = 'enum'
    else if (declaredType === 'boolean') type = 'boolean'
    else if (declaredType === 'number') type = 'number'
    else if (declaredType === 'integer') type = 'integer'
    else if (declaredType === 'string') type = 'string'
    else if (declaredType === 'array' && items?.type === 'string') type = 'string-array'

    fields.push({
      name,
      type,
      title: typeof property.title === 'string' ? property.title : name,
      description: typeof property.description === 'string' ? property.description : '',
      required: required.includes(name),
      options,
      fallback: property.default,
    })
  }
  return fields
}

function toInputValue(value: unknown, type: SchemaField['type']): string {
  if (value === null || value === undefined) return ''
  if (type === 'string-array' && Array.isArray(value)) return value.join(', ')
  if (type === 'boolean') return value === true ? 'true' : 'false'
  return String(value)
}

function buildConfigPayload(): Record<string, unknown> {
  if (!configUsesForm.value) return JSON.parse(configRaw.value || '{}')

  const payload: Record<string, unknown> = {}
  for (const field of configFields.value) {
    const stored = configValues[field.name]
    const raw = stored === null || stored === undefined ? '' : String(stored).trim()
    if (raw === '') {
      if (field.required) throw new Error(`「${field.title}」是必填项。`)
      continue
    }
    switch (field.type) {
      case 'boolean':
        payload[field.name] = raw === 'true'
        break
      case 'number':
      case 'integer': {
        const value = Number(raw)
        if (Number.isNaN(value)) throw new Error(`「${field.title}」需要填写数字。`)
        payload[field.name] = field.type === 'integer' ? Math.trunc(value) : value
        break
      }
      case 'string-array':
        payload[field.name] = raw.split(',').map(part => part.trim()).filter(Boolean)
        break
      default:
        payload[field.name] = raw
    }
  }
  return payload
}

function applyDetailConfig(next: PluginDetail) {
  configFields.value = parseSchema(next.configSchema)
  configUsesForm.value = configFields.value.length > 0

  let current: Record<string, unknown> = {}
  if (next.config) {
    try {
      const parsed = JSON.parse(next.config)
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) current = parsed as Record<string, unknown>
    } catch {
      // 配置不是合法 JSON：直接进原始模式，别假装能渲染。
      configUsesForm.value = false
    }
  }

  for (const key of Object.keys(configValues)) delete configValues[key]
  for (const field of configFields.value) {
    const value = current[field.name] ?? field.fallback
    configValues[field.name] = toInputValue(value, field.type)
  }
  configRaw.value = current && Object.keys(current).length > 0 ? JSON.stringify(current, null, 2) : (next.config ?? '')
}

async function reloadDetail(pluginId: string, options: { silent?: boolean } = {}) {
  if (!options.silent) detailLoading.value = true
  try {
    const next = await getPlugin(pluginId)
    detail.value = next
    applyDetailConfig(next)
    permissionsDraft.value = [...next.status.grantedPermissions]
    scopeDraft.knowledgeBaseIds = [...next.allowedKnowledgeBaseIds]
    scopeDraft.maxDocuments = next.maxDocuments
    if (!syncCapability.value || !syncCapabilities.value.some(item => item.id === syncCapability.value)) {
      syncCapability.value = syncCapabilities.value[0]?.id ?? ''
    }
    if (!syncKnowledgeBaseId.value) syncKnowledgeBaseId.value = props.activeKnowledgeBaseId

    const [audit, states] = await Promise.all([
      listPluginAudit(pluginId, 50).catch(() => []),
      listPluginSyncStates(pluginId).catch(() => []),
    ])
    auditEntries.value = audit
    syncStates.value = states
  } catch (error) {
    // 卸载后旧 id 会 404：这不是错误，只是这个面板该关了。
    // 静默刷新（10 秒定时器）也绝不能弹提示，否则每 10 秒吵一次。
    const message = error instanceof Error ? error.message : '无法读取插件详情。'
    if (/（404）|404/.test(message) && detail.value?.status.id === pluginId) {
      detail.value = null
    } else if (!options.silent) {
      emit('notice', message)
    }
  } finally {
    detailLoading.value = false
  }
}

async function openDetail(plugin: PluginStatus) {
  activeTab.value = 'overview'
  detail.value = null
  await reloadDetail(plugin.id)
}

async function run(action: string, pluginId: string, work: () => Promise<void>, successMessage: string) {
  busyAction.value = `${action}:${pluginId}`
  try {
    await work()
    // 卸载之后这个插件已经不存在，先关面板再刷新，否则会拿旧 id 去请求一个 404。
    if (action === 'uninstall') detail.value = null
    emit('notice', successMessage)
    await refresh({ silent: true })
    if (detail.value?.status.id === pluginId) await reloadDetail(pluginId, { silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '操作失败。')
  } finally {
    busyAction.value = ''
  }
}

function isBusy(action: string, pluginId: string) {
  return busyAction.value === `${action}:${pluginId}`
}

async function handleInstall(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return

  installing.value = true
  try {
    const result = await installPlugin(file)
    emit('notice', `已安装 ${result.pluginId} ${result.version}（状态：${stateLabel(result.state ?? '')}）。`)
    if (result.warnings?.length) emit('notice', `安装警告：${result.warnings.join('；')}`)
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '安装失败。')
  } finally {
    installing.value = false
  }
}

async function saveConfig() {
  const pluginId = detail.value?.status.id
  if (!pluginId) return
  configSaving.value = true
  try {
    const payload = buildConfigPayload()
    await savePluginConfig(pluginId, payload)
    emit('notice', '配置已保存。')
    await reloadDetail(pluginId, { silent: true })
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '保存配置失败。')
  } finally {
    configSaving.value = false
  }
}

async function clearConfig() {
  const pluginId = detail.value?.status.id
  if (!pluginId) return
  configSaving.value = true
  try {
    await savePluginConfig(pluginId, null)
    emit('notice', '配置已清空（插件会回到待配置状态）。')
    await reloadDetail(pluginId, { silent: true })
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '清空配置失败。')
  } finally {
    configSaving.value = false
  }
}

function togglePermission(permission: string) {
  const index = permissionsDraft.value.indexOf(permission)
  if (index >= 0) permissionsDraft.value.splice(index, 1)
  else permissionsDraft.value.push(permission)
}

async function savePermissions() {
  const pluginId = detail.value?.status.id
  if (!pluginId) return
  permissionsSaving.value = true
  try {
    await savePluginPermissions(pluginId, [...permissionsDraft.value])
    emit('notice', '授权已更新。')
    await reloadDetail(pluginId, { silent: true })
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '保存授权失败。')
  } finally {
    permissionsSaving.value = false
  }
}

function toggleScopeKnowledgeBase(id: string) {
  const index = scopeDraft.knowledgeBaseIds.indexOf(id)
  if (index >= 0) scopeDraft.knowledgeBaseIds.splice(index, 1)
  else scopeDraft.knowledgeBaseIds.push(id)
}

async function saveScope() {
  const pluginId = detail.value?.status.id
  if (!pluginId) return
  scopeSaving.value = true
  try {
    await savePluginScope(pluginId, [...scopeDraft.knowledgeBaseIds], scopeDraft.maxDocuments)
    emit('notice', '作用域已保存。')
    await reloadDetail(pluginId, { silent: true })
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '保存作用域失败。')
  } finally {
    scopeSaving.value = false
  }
}

async function runSync() {
  const pluginId = detail.value?.status.id
  if (!pluginId || !syncCapability.value) {
    emit('notice', '请选择要同步的能力。')
    return
  }
  const knowledgeBaseId = syncKnowledgeBaseId.value || props.activeKnowledgeBaseId
  busyAction.value = `sync:${pluginId}`
  try {
    await syncPluginAsync(pluginId, syncCapability.value, knowledgeBaseId)
    emit('notice', '同步已排入任务队列，可在任务中心查看进度。')
    await reloadDetail(pluginId, { silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '同步失败。')
  } finally {
    busyAction.value = ''
  }
}

async function loadKeys() {
  if (!props.apiOnline) return
  keysLoading.value = true
  try {
    trustedKeys.value = await listTrustedKeys()
  } catch {
    trustedKeys.value = []
  } finally {
    keysLoading.value = false
  }
}

async function submitKey() {
  if (!keyForm.keyId.trim() || !keyForm.pluginId.trim() || !keyForm.publicKeyPem.trim()) {
    emit('notice', 'keyId、pluginId 与公钥都不能为空。')
    return
  }
  savingKey.value = true
  try {
    await saveTrustedKey(
      keyForm.keyId.trim(),
      keyForm.pluginId.trim(),
      keyForm.publicKeyPem,
      keyForm.algorithm,
      keyForm.note.trim() || undefined,
    )
    emit('notice', '公钥已信任，立即生效。')
    keyForm.keyId = ''
    keyForm.pluginId = ''
    keyForm.publicKeyPem = ''
    keyForm.note = ''
    showKeyForm.value = false
    await loadKeys()
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '保存公钥失败。')
  } finally {
    savingKey.value = false
  }
}

async function removeKey(keyId: string) {
  try {
    await deleteTrustedKey(keyId)
    emit('notice', '公钥已删除。')
    await loadKeys()
    await refresh({ silent: true })
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '删除公钥失败。')
  }
}

watch(() => props.apiOnline, (online) => {
  if (online) {
    void refresh()
    void loadKeys()
    if (!refreshTimer) refreshTimer = setInterval(() => { void refresh({ silent: true }) }, 10000)
    return
  }
  if (refreshTimer) {
    clearInterval(refreshTimer)
    refreshTimer = undefined
  }
})

onMounted(() => {
  void refresh()
  void loadKeys()
  if (props.apiOnline) refreshTimer = setInterval(() => { void refresh({ silent: true }) }, 10000)
})

onBeforeUnmount(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <section class="page-heading-row">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> PLUGINS</div>
      <h1>插件</h1>
      <p class="welcome-copy">插件在独立进程里运行，通过协议把内容交回核心摄取。声明了敏感权限的插件需要你手动授权。</p>
    </div>
    <div class="plugins-heading-actions">
      <button class="button button-soft" :disabled="!apiOnline || isLoading" @click="refresh()">
        <RefreshCw :size="14" :class="{ spinning: isLoading }" /> 刷新
      </button>
      <button class="button button-primary" :disabled="!apiOnline || installing" @click="installInput?.click()">
        <LoaderCircle v-if="installing" :size="14" class="spinning" />
        <Upload v-else :size="14" />
        安装插件
      </button>
      <input ref="installInput" type="file" accept=".zip" class="plugins-file-input" @change="handleInstall" />
    </div>
  </section>

  <p v-if="rootPath" class="plugins-root">插件目录：<code>{{ rootPath }}</code></p>

  <p v-if="!pluginsEnabled" class="plugins-banner">
    <CircleAlert :size="14" /> `Plugins:Enabled=false`，插件已全局停用：不会启动，也不接受调用。
  </p>

  <p v-if="pendingPlugins.length > 0" class="plugins-banner plugins-banner-warn">
    <ShieldCheck :size="14" /> {{ pendingPlugins.length }} 个插件在等你处理（待授权或待配置），它们不会自动启动。
  </p>

  <div v-if="!apiOnline" class="empty-state">
    <strong>后端未连接</strong>
    <span>启动 API 后插件列表会自动出现。</span>
  </div>

  <div v-else-if="loadError" class="empty-state">
    <strong>读取插件失败</strong>
    <span>{{ loadError }}</span>
    <button class="button button-soft" @click="refresh()">重试</button>
  </div>

  <div v-else-if="isLoading && plugins.length === 0" class="empty-state">
    <LoaderCircle :size="20" class="spinning" />
    <span>正在读取插件…</span>
  </div>

  <div v-else-if="plugins.length === 0" class="empty-state">
    <strong>还没有安装插件</strong>
    <span>上传一个插件包（ZIP）开始。编写规范见 docs/PLUGIN-AUTHOR-GUIDE.md。</span>
  </div>

  <section v-else class="plugins-grid">
    <article
      v-for="plugin in plugins"
      :key="plugin.id"
      class="plugin-card"
      :class="{ 'plugin-card-idle': !plugin.enabled }"
      @click="openDetail(plugin)"
    >
      <header class="plugin-card-head">
        <span class="plugin-icon"><Plug :size="17" /></span>
        <div class="plugin-title">
          <strong>{{ plugin.name }}</strong>
          <small>{{ plugin.id }}</small>
        </div>
        <span class="plugin-state" :class="`plugin-state-${toneOf(plugin.state)}`">{{ stateLabel(plugin.state) }}</span>
      </header>

      <div class="plugin-meta-row">
        <span>v{{ plugin.version }}</span>
        <span>·</span>
        <span>{{ plugin.publisher }}</span>
        <span v-if="plugin.capabilities.length" class="plugin-cap-count">{{ plugin.capabilities.length }} 个能力</span>
      </div>

      <p v-if="plugin.missingPermissions.length > 0" class="plugin-warn">
        <ShieldCheck :size="12" /> 待授权：{{ plugin.missingPermissions.map(permissionLabel).join('、') }}
      </p>
      <p v-else-if="plugin.state === 'NeedsConfiguration'" class="plugin-warn">
        <SlidersHorizontal :size="12" /> 需要填写配置才能启动
      </p>
      <p v-else-if="plugin.lastError" class="plugin-error">{{ plugin.lastError }}</p>
      <p v-else-if="plugin.integrity === 'verified'" class="plugin-integrity"><Check :size="12" /> 已校验签名</p>
      <p v-else-if="integrityWarning(plugin.integrity)" class="plugin-warn"><ShieldCheck :size="12" /> {{ integrityWarning(plugin.integrity) }}</p>

      <footer class="plugin-card-foot" @click.stop>
        <button class="plugin-quick" :disabled="busyAction !== ''" title="详情与配置" @click="openDetail(plugin)">
          <SlidersHorizontal :size="13" /> 管理
        </button>
        <button
          class="plugin-quick"
          :disabled="busyAction !== ''"
          :title="plugin.enabled ? '停用插件' : '启用插件'"
          @click="run(plugin.enabled ? 'disable' : 'enable', plugin.id, () => setPluginEnabled(plugin.id, !plugin.enabled), plugin.enabled ? '插件已停用。' : '插件已启用。')"
        >
          <LoaderCircle v-if="isBusy(plugin.enabled ? 'disable' : 'enable', plugin.id)" :size="13" class="spinning" />
          <Ban v-else :size="13" />
          {{ plugin.enabled ? '停用' : '启用' }}
        </button>
        <span v-if="plugin.inFlightInvocations > 0" class="plugin-inflight">{{ plugin.inFlightInvocations }} 在途</span>
      </footer>
    </article>
  </section>

  <section class="plugins-keys">
    <div class="plugins-keys-head">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> TRUSTED KEYS</div>
        <h2>受信任发布者公钥</h2>
        <p class="welcome-copy">决定"谁的签名算数"。这里新增的公钥立即生效；`config:` 前缀来自配置文件，只能在 appsettings 里改。</p>
      </div>
      <button class="button button-soft" :disabled="!apiOnline || keysLoading" @click="showKeyForm = !showKeyForm">
        <KeyRound :size="14" /> {{ showKeyForm ? '取消' : '添加公钥' }}
      </button>
    </div>

    <form v-if="showKeyForm" class="plugins-key-form" @submit.prevent="submitKey">
      <label class="settings-field">
        <span>KeyId</span>
        <input v-model="keyForm.keyId" placeholder="acme-release-2026" maxlength="120" />
      </label>
      <label class="settings-field">
        <span>插件 ID</span>
        <input v-model="keyForm.pluginId" placeholder="com.example.notes" maxlength="200" />
      </label>
      <label class="settings-field">
        <span>算法</span>
        <select v-model="keyForm.algorithm">
          <option value="ecdsa">ecdsa</option>
          <option value="rsa">rsa</option>
        </select>
      </label>
      <label class="settings-field plugins-key-pem">
        <span>公钥（PEM，只接受 PUBLIC KEY）</span>
        <textarea v-model="keyForm.publicKeyPem" rows="5" placeholder="-----BEGIN PUBLIC KEY-----&#10;...&#10;-----END PUBLIC KEY-----" />
      </label>
      <label class="settings-field">
        <span>备注（可选）</span>
        <input v-model="keyForm.note" placeholder="2026 年发布密钥" maxlength="200" />
      </label>
      <div class="plugins-key-actions">
        <button class="button button-primary" type="submit" :disabled="savingKey">
          <LoaderCircle v-if="savingKey" :size="13" class="spinning" /><Save v-else :size="13" /> 保存公钥
        </button>
      </div>
    </form>

    <div v-if="trustedKeys.length === 0" class="plugins-keys-empty">
      {{ keysLoading ? '正在读取…' : '还没有受信任的公钥。未配置时只能安装未签名或自签名的插件（取决于 IntegrityPolicy）。' }}
    </div>
    <div v-else class="plugins-keys-table">
      <div class="plugins-keys-row plugins-keys-row-head">
        <span>KeyId</span><span>插件</span><span>算法</span><span>指纹</span><span>来源</span><span></span>
      </div>
      <div v-for="key in trustedKeys" :key="key.keyId" class="plugins-keys-row">
        <span class="plugins-mono">{{ key.keyId }}</span>
        <span>{{ key.pluginId }}</span>
        <span>{{ key.algorithm }}</span>
        <span class="plugins-mono" :title="key.fingerprint">{{ key.fingerprint }}</span>
        <span>{{ key.keyId.startsWith('config:') ? '配置文件' : '数据库' }}</span>
        <span class="plugins-keys-actions">
          <button
            v-if="!key.keyId.startsWith('config:')"
            class="jobs-action"
            title="删除公钥"
            @click="removeKey(key.keyId)"
          ><Trash2 :size="13" /></button>
        </span>
      </div>
    </div>
  </section>

  <Teleport to="body">
    <div v-if="detail" class="modal-scrim" @click.self="detail = null">
      <section class="plugin-detail" role="dialog" aria-modal="true" aria-label="插件详情">
        <header class="plugin-detail-head">
          <span class="plugin-icon"><Plug :size="18" /></span>
          <div class="plugin-detail-title">
            <h2>{{ detail.status.name }}<span class="plugin-state" :class="`plugin-state-${toneOf(detail.status.state)}`">{{ stateLabel(detail.status.state) }}</span></h2>
            <p>{{ detail.status.id }} · v{{ detail.status.version }} · {{ detail.status.publisher }}</p>
          </div>
          <button class="icon-button" aria-label="关闭" @click="detail = null"><X :size="16" /></button>
        </header>

        <nav class="plugin-tabs">
          <button v-for="tab in ([
            { id: 'overview', label: '概览' },
            { id: 'config', label: '配置' },
            { id: 'permissions', label: '授权' },
            { id: 'scope', label: '作用域' },
            { id: 'sync', label: '同步' },
            { id: 'audit', label: '审计' },
          ] as Array<{ id: Tab; label: string }>)" :key="tab.id" class="plugin-tab" :class="{ active: activeTab === tab.id }" @click="activeTab = tab.id">
            {{ tab.label }}
          </button>
        </nav>

        <div class="plugin-detail-body">
          <p v-if="detailLoading" class="plugin-detail-loading"><LoaderCircle :size="14" class="spinning" /> 正在读取…</p>

          <!-- 概览 -->
          <div v-show="activeTab === 'overview'" class="plugin-pane">
            <div class="plugin-stats">
              <div><span>健康</span><strong>{{ detail.health ?? '—' }}</strong></div>
              <div><span>在途调用</span><strong>{{ detail.inFlightInvocations }}</strong></div>
              <div><span>排空</span><strong>{{ detail.draining ? '是' : '否' }}</strong></div>
              <div><span>配置</span><strong>{{ detail.status.configured ? '已填写' : '未填写' }}</strong></div>
            </div>

            <p v-if="detail.lastError" class="jobs-detail-error">{{ detail.lastError }}</p>

            <dl class="plugin-facts">
              <div><dt>当前版本</dt><dd>{{ detail.status.activeVersion ?? detail.status.version }}</dd></div>
              <div><dt>已安装版本</dt><dd>{{ detail.status.installedVersions.join('、') || '—' }}</dd></div>
              <div><dt>目录</dt><dd class="plugins-mono">{{ detail.status.directory }}</dd></div>
              <div><dt>完整性</dt><dd class="plugins-mono">{{ detail.status.integrity ?? '未签名' }}</dd></div>
              <div><dt>受信任公钥</dt><dd class="plugins-mono">{{ detail.trustedKey ? `${detail.trustedKey.keyId}（${detail.trustedKey.fingerprint}）` : '—' }}</dd></div>
              <div><dt>并发上限</dt><dd>{{ detail.limits?.maxConcurrentInvocations ?? '—' }}</dd></div>
            </dl>

            <div class="plugin-capabilities">
              <span class="plugin-block-label">能力（{{ detail.declaredCapabilities.length }}）</span>
              <div v-if="detail.declaredCapabilities.length === 0" class="plugins-keys-empty">插件未声明任何能力。</div>
              <div v-for="capability in detail.declaredCapabilities" :key="capability.name" class="plugin-capability">
                <div>
                  <strong>{{ capability.name }}</strong>
                  <small>{{ capability.kind }} · 副作用 {{ capability.sideEffect ?? 'read' }}</small>
                </div>
                <p v-if="capability.description">{{ capability.description }}</p>
              </div>
            </div>

            <div class="plugin-actions-row">
              <button class="button button-soft" :disabled="busyAction !== ''" @click="run('restart', detail.status.id, () => restartPlugin(detail!.status.id), '插件已重启。')">
                <RotateCcw :size="13" /> 重启
              </button>
              <button class="button button-soft" :disabled="busyAction !== ''" @click="run('health', detail.status.id, async () => { await checkPluginHealth(detail!.status.id) }, '健康检查已执行。')">
                <Check :size="13" /> 健康检查
              </button>
              <button class="button button-soft" :disabled="busyAction !== ''" title="不再接受新调用，等在途调用结束" @click="run('drain', detail.status.id, async () => { const r = await drainPlugin(detail!.status.id); emit('notice', r.drained ? '已排空。' : `排空超时，仍有 ${r.inFlight} 个在途调用。`) }, '排空完成。')">
                <Clock3 :size="13" /> 排空
              </button>
              <button class="button button-soft" :disabled="busyAction !== '' || detail.status.installedVersions.length < 2" title="回到上一个已安装版本" @click="run('rollback', detail.status.id, () => rollbackPlugin(detail!.status.id), '已回滚到上一个版本。')">
                <RotateCcw :size="13" /> 回滚
              </button>
              <button class="button button-soft plugin-danger" :disabled="busyAction !== ''" @click="run('uninstall', detail.status.id, () => uninstallPlugin(detail!.status.id), '插件已卸载。')">
                <Trash2 :size="13" /> 卸载
              </button>
            </div>
          </div>

          <!-- 配置 -->
          <div v-show="activeTab === 'config'" class="plugin-pane">
            <p v-if="!detail.configSchema" class="plugins-keys-empty">
              这个插件没有声明配置 schema（或 manifest 未提供）。可以直接填写 JSON；插件收不到就保持未配置状态。
            </p>

            <template v-if="configUsesForm">
              <label v-for="field in configFields" :key="field.name" class="settings-field">
                <span>{{ field.title }}<i v-if="field.required" class="plugin-required">必填</i></span>
                <select v-if="field.type === 'enum'" v-model="configValues[field.name]">
                  <option value="">（未设置）</option>
                  <option v-for="option in field.options" :key="option" :value="option">{{ option }}</option>
                </select>
                <select v-else-if="field.type === 'boolean'" v-model="configValues[field.name]">
                  <option value="">（未设置）</option>
                  <option value="true">true</option>
                  <option value="false">false</option>
                </select>
                <input
                  v-else-if="field.type === 'number' || field.type === 'integer'"
                  v-model="configValues[field.name]"
                  type="number"
                  :placeholder="field.type === 'integer' ? '整数' : '数字'"
                />
                <input v-else v-model="configValues[field.name]" :placeholder="field.type === 'string-array' ? '用英文逗号分隔' : ''" />
                <small v-if="field.description" class="plugin-field-hint">{{ field.description }}</small>
                <small v-else-if="field.type === 'unknown'" class="plugin-field-hint">类型未知（{{ field.name }}），按文本保存。</small>
              </label>
            </template>

            <label v-else class="settings-field">
              <span>配置 JSON</span>
              <textarea v-model="configRaw" rows="10" placeholder='{ "baseUrl": "https://example.com" }' />
            </label>

            <div class="plugin-actions-row">
              <button class="button button-primary" :disabled="configSaving" @click="saveConfig">
                <LoaderCircle v-if="configSaving" :size="13" class="spinning" /><Save v-else :size="13" /> 保存配置
              </button>
              <button class="button button-soft" :disabled="configSaving" @click="clearConfig">清空配置</button>
            </div>
          </div>

          <!-- 授权 -->
          <div v-show="activeTab === 'permissions'" class="plugin-pane">
            <p class="plugin-pane-hint">
              插件声明的权限必须逐条授权。声明了 <code>network</code> / <code>file-read</code> / <code>file-write</code> /
              <code>clipboard</code> / <code>process</code> 的插件在授权前不会启动。权限是准入控制，不是内核级隔离。
            </p>
            <div class="plugin-permission-list">
              <label v-for="permission in allPermissions" :key="permission" class="plugin-permission" :class="{ 'plugin-permission-granted': permissionsDraft.includes(permission) }">
                <input type="checkbox" :checked="permissionsDraft.includes(permission)" @change="togglePermission(permission)" />
                <div>
                  <strong>{{ permissionLabel(permission) }}</strong>
                  <small>{{ permission }}</small>
                </div>
              </label>
            </div>
            <div class="plugin-actions-row">
              <button class="button button-primary" :disabled="permissionsSaving" @click="savePermissions">
                <LoaderCircle v-if="permissionsSaving" :size="13" class="spinning" /><Save v-else :size="13" /> 保存授权
              </button>
            </div>
          </div>

          <!-- 作用域 -->
          <div v-show="activeTab === 'scope'" class="plugin-pane">
            <p class="plugin-pane-hint">不勾选任何知识库 = 不限制。配额 0 表示跟随全局默认。</p>
            <div class="plugin-permission-list">
              <label v-for="knowledgeBase in knowledgeBases" :key="knowledgeBase.id" class="plugin-permission" :class="{ 'plugin-permission-granted': scopeDraft.knowledgeBaseIds.includes(knowledgeBase.id) }">
                <input type="checkbox" :checked="scopeDraft.knowledgeBaseIds.includes(knowledgeBase.id)" @change="toggleScopeKnowledgeBase(knowledgeBase.id)" />
                <div>
                  <strong>{{ knowledgeBase.name }}</strong>
                  <small class="plugins-mono">{{ knowledgeBase.id }}</small>
                </div>
              </label>
            </div>
            <label class="settings-field">
              <span>最多写入资料数（0 = 跟随全局）</span>
              <input v-model.number="scopeDraft.maxDocuments" type="number" min="0" />
            </label>
            <div class="plugin-actions-row">
              <button class="button button-primary" :disabled="scopeSaving" @click="saveScope">
                <LoaderCircle v-if="scopeSaving" :size="13" class="spinning" /><Save v-else :size="13" /> 保存作用域
              </button>
            </div>
          </div>

          <!-- 同步 -->
          <div v-show="activeTab === 'sync'" class="plugin-pane">
            <p v-if="syncCapabilities.length === 0" class="plugins-keys-empty">
              这个插件没有声明 <code>platform-connector</code> 能力，因此没有可同步的内容。
            </p>
            <template v-else>
              <div class="plugin-sync-row">
                <label class="settings-field">
                  <span>能力</span>
                  <select v-model="syncCapability">
                    <option v-for="capability in syncCapabilities" :key="capability.id" :value="capability.id">{{ capability.name }}（{{ capability.id }}）</option>
                  </select>
                </label>
                <label class="settings-field">
                  <span>写入知识库</span>
                  <select v-model="syncKnowledgeBaseId">
                    <option v-for="knowledgeBase in knowledgeBases" :key="knowledgeBase.id" :value="knowledgeBase.id">{{ knowledgeBase.name }}</option>
                  </select>
                </label>
              </div>
              <div class="plugin-actions-row">
                <button class="button button-primary" :disabled="busyAction !== ''" @click="runSync">
                  <LoaderCircle v-if="isBusy('sync', detail.status.id)" :size="13" class="spinning" /><Play v-else :size="13" /> 立即同步
                </button>
                <span class="plugin-pane-hint">同步会进任务队列，去「任务中心」看进度与取消。</span>
              </div>

              <span class="plugin-block-label">同步游标</span>
              <div v-if="syncStates.length === 0" class="plugins-keys-empty">还没有同步记录。</div>
              <div v-else class="plugins-keys-table">
                <div class="plugins-keys-row plugins-keys-row-head plugins-sync-cols">
                  <span>能力</span><span>知识库</span><span>上次同步</span><span>状态</span><span>间隔</span><span>游标</span>
                </div>
                <div v-for="state in syncStates" :key="state.scopeKey" class="plugins-keys-row plugins-sync-cols">
                  <span>{{ state.capabilityId }}</span>
                  <span>{{ knowledgeBases.find(item => item.id === state.knowledgeBaseId)?.name ?? state.knowledgeBaseId.slice(0, 8) }}</span>
                  <span :title="formatTime(state.lastSyncedAt)">{{ formatRelative(state.lastSyncedAt) }}</span>
                  <span>
                    <span class="plugin-state" :class="state.lastStatus === 'succeeded' ? 'plugin-state-ok' : state.lastStatus ? 'plugin-state-bad' : 'plugin-state-idle'">{{ state.lastStatus ?? '—' }}</span>
                  </span>
                  <span>{{ state.nextSyncAfterSeconds ? `${state.nextSyncAfterSeconds}s` : '手动' }}</span>
                  <span class="plugins-mono" :title="state.cursor ?? ''">{{ state.cursor ?? '—' }}</span>
                </div>
              </div>
            </template>
          </div>

          <!-- 审计 -->
          <div v-show="activeTab === 'audit'" class="plugin-pane">
            <div v-if="auditEntries.length === 0" class="plugins-keys-empty">还没有审计记录。</div>
            <div v-else class="plugin-audit">
              <div v-for="entry in auditEntries" :key="entry.id" class="plugin-audit-row">
                <span class="plugin-audit-time" :title="formatTime(entry.occurredAt)">{{ formatRelative(entry.occurredAt) }}</span>
                <span class="plugin-state" :class="entry.outcome === 'success' ? 'plugin-state-ok' : 'plugin-state-bad'">{{ entry.outcome }}</span>
                <span class="plugin-audit-action">{{ actionLabel(entry.action) }}<i v-if="entry.capabilityId"> · {{ entry.capabilityId }}</i></span>
                <span class="plugin-audit-detail">{{ entry.detail ?? '' }}</span>
                <span class="plugin-audit-duration">{{ entry.durationMs }} ms</span>
              </div>
            </div>
          </div>
        </div>

        <footer class="plugin-detail-foot">
          <span>{{ activeKnowledgeBaseName }} · 状态 10 秒自动刷新</span>
          <div>
            <button class="button button-soft" @click="reloadDetail(detail.status.id)">重新读取</button>
            <button class="button button-soft" @click="detail = null">关闭</button>
          </div>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.plugins-heading-actions { display: flex; align-items: center; gap: 9px; margin-bottom: 3px; }
.plugins-file-input { display: none; }
.plugins-root { margin: -14px 0 14px; color: #a2aaa4; font-size: 8px; }
.plugins-root code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.plugins-banner { display: flex; align-items: center; gap: 7px; margin: 0 0 12px; padding: 9px 12px; border: 1px solid #e8ede8; border-radius: 9px; color: #7d8a82; background: #fafbf9; font-size: 9px; }
.plugins-banner-warn { border-color: #f0e6d3; color: #97835f; background: #fdfaf4; }

.plugins-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 13px; margin-top: 4px; }
.plugin-card { display: flex; flex-direction: column; gap: 10px; padding: 14px 15px 12px; border: 1px solid #e9ede8; border-radius: 11px; background: #fff; cursor: pointer; transition: transform .18s, box-shadow .18s, border-color .18s; }
.plugin-card:hover { transform: translateY(-2px); border-color: #d8e4da; box-shadow: 0 9px 22px #263b2f0d; }
.plugin-card-idle { opacity: .78; }
.plugin-card-head { display: flex; align-items: flex-start; gap: 10px; }
.plugin-icon { width: 32px; height: 32px; display: grid; place-items: center; flex: 0 0 auto; border-radius: 9px; color: #4f8369; background: #eaf3eb; }
.plugin-title { min-width: 0; display: flex; flex-direction: column; gap: 3px; flex: 1; }
.plugin-title strong { overflow: hidden; color: #45544b; font-size: 11px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.plugin-title small { overflow: hidden; color: #a9b1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; text-overflow: ellipsis; white-space: nowrap; }

.plugin-state { display: inline-flex; align-items: center; padding: 3px 7px; border-radius: 5px; font-size: 8px; font-weight: 600; white-space: nowrap; }
.plugin-state-ok { color: #3f7b5f; background: #e9f3ea; }
.plugin-state-bad { color: #b07068; background: #fbeceb; }
.plugin-state-warn { color: #94795a; background: #f6efe4; }
.plugin-state-busy { color: #6b7f9a; background: #eef2f7; }
.plugin-state-idle { color: #8b918c; background: #f1f3f1; }

.plugin-meta-row { display: flex; align-items: center; gap: 6px; color: #9aa59e; font-size: 8px; }
.plugin-cap-count { margin-left: auto; padding: 2px 6px; border-radius: 4px; color: #7f9384; background: #eff4ef; }
.plugin-warn { display: flex; align-items: center; gap: 5px; margin: 0; color: #a3814f; font-size: 8px; }
.plugin-error { margin: 0; overflow: hidden; color: #b07068; font-size: 8px; line-height: 1.5; text-overflow: ellipsis; white-space: nowrap; }
.plugin-integrity { display: flex; align-items: center; gap: 5px; margin: 0; color: #6f8b77; font-size: 8px; }
.plugin-card-foot { display: flex; align-items: center; gap: 7px; margin-top: auto; padding-top: 9px; border-top: 1px solid #f2f4f1; }
.plugin-quick { display: inline-flex; align-items: center; gap: 5px; padding: 5px 9px; border: 1px solid #e8ece7; border-radius: 7px; color: #77857c; background: #fff; font-size: 8px; }
.plugin-quick:hover:not(:disabled) { border-color: #d3e0d6; color: #3f7b5f; background: #f8fbf8; }
.plugin-quick:disabled { cursor: not-allowed; opacity: .5; }
.plugin-inflight { margin-left: auto; color: #9aa59e; font-size: 8px; }

.plugins-keys { margin-top: 30px; padding-top: 22px; border-top: 1px solid #eaece8; }
.plugins-keys-head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; }
.plugins-keys-head h2 { margin: 0; color: #34413a; font-size: 14px; font-weight: 600; letter-spacing: -.2px; }
.plugins-keys-empty { padding: 16px 2px; color: #a2aaa4; font-size: 9px; }
.plugins-keys-table { margin-top: 12px; overflow: hidden; border: 1px solid #e9ede8; border-radius: 9px; background: #fff; }
.plugins-keys-row { display: grid; grid-template-columns: minmax(120px, 1.1fr) minmax(120px, 1.1fr) 60px minmax(90px, .9fr) 70px 34px; align-items: center; gap: 10px; padding: 0 12px; min-height: 40px; border-top: 1px solid #f2f4f1; color: #79867d; font-size: 9px; }
.plugins-keys-row-head { min-height: 32px; border-top: 0; color: #a2aaa4; background: #fafbf9; font-size: 8px; }
.plugins-keys-row > span { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.plugins-mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 8px; }
.plugins-keys-actions { display: flex; justify-content: flex-end; }

.plugins-key-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 4px 14px; margin-top: 14px; padding: 14px; border: 1px solid #e9ede8; border-radius: 10px; background: #fbfcfa; }
.plugins-key-pem { grid-column: 1 / -1; }
.plugins-key-pem textarea { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 9px; }
.plugins-key-actions { grid-column: 1 / -1; display: flex; justify-content: flex-end; padding-top: 6px; }
.plugins-key-form select { padding: 7px 9px; border: 1px solid #e4eae4; border-radius: 8px; color: #57655c; background: #fff; font-size: 10px; }

.plugin-detail { width: min(760px, calc(100vw - 40px)); max-height: calc(100vh - 56px); display: flex; flex-direction: column; border: 1px solid #e6ece6; border-radius: 14px; background: #fff; box-shadow: 0 24px 60px #1d2f2424; overflow: hidden; }
.plugin-detail-head { display: flex; align-items: flex-start; gap: 11px; padding: 18px 20px 14px; }
.plugin-detail-title { flex: 1; min-width: 0; }
.plugin-detail-title h2 { display: flex; align-items: center; gap: 8px; margin: 0; color: #33423a; font-size: 16px; font-weight: 600; letter-spacing: -.3px; }
.plugin-detail-title p { margin: 5px 0 0; color: #a9b1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.plugin-tabs { display: flex; gap: 3px; padding: 0 20px; border-bottom: 1px solid #eef1ed; }
.plugin-tab { padding: 8px 11px; border: 0; border-bottom: 2px solid transparent; color: #98a29b; background: transparent; font-size: 9px; font-weight: 600; }
.plugin-tab:hover { color: #3f7b5f; }
.plugin-tab.active { border-bottom-color: #4a8a6b; color: #2f7058; }

.plugin-detail-body { flex: 1; min-height: 0; overflow-y: auto; padding: 16px 20px; }
.plugin-pane { display: flex; flex-direction: column; gap: 12px; }
.plugin-detail-loading { display: flex; align-items: center; gap: 6px; margin: 0; color: #9aa59e; font-size: 9px; }

.plugin-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; }
.plugin-stats > div { display: flex; flex-direction: column; gap: 3px; padding: 8px 10px; border: 1px solid #edf0ec; border-radius: 8px; background: #fafbf9; }
.plugin-stats span { color: #a2aaa4; font-size: 8px; }
.plugin-stats strong { color: #46554b; font-size: 11px; font-weight: 600; }

.plugin-facts { display: grid; grid-template-columns: 1fr 1fr; gap: 5px 16px; margin: 0; }
.plugin-facts > div { min-width: 0; display: flex; gap: 8px; padding: 3px 0; border-bottom: 1px dashed #f1f3f0; }
.plugin-facts dt { flex: 0 0 68px; color: #a2aaa4; font-size: 8px; }
.plugin-facts dd { min-width: 0; margin: 0; overflow: hidden; color: #63706a; font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }

.plugin-block-label { color: #88958b; font-size: 8px; font-weight: 600; }
.plugin-capabilities { display: flex; flex-direction: column; gap: 7px; }
.plugin-capability { padding: 9px 11px; border: 1px solid #edf0ec; border-radius: 8px; background: #fbfcfa; }
.plugin-capability strong { color: #4f5d54; font-size: 9px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.plugin-capability small { margin-left: 8px; color: #a5aea7; font-size: 8px; }
.plugin-capability p { margin: 5px 0 0; color: #8a958d; font-size: 8px; line-height: 1.6; }

.plugin-actions-row { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; padding-top: 10px; border-top: 1px solid #f2f4f1; }
.plugin-actions-row .button { min-height: 30px; font-size: 9px; }
.plugin-danger { color: #a8635a; border-color: #f0dcd9; }
.plugin-danger:hover { border-color: #e2c3bf; background: #fdf6f5; }

.plugin-pane-hint { margin: 0; color: #939e97; font-size: 8px; line-height: 1.7; }
.plugin-pane-hint code { padding: 1px 4px; border-radius: 3px; background: #f1f4f1; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.plugin-required { margin-left: 5px; padding: 1px 4px; border-radius: 3px; color: #a3814f; background: #f6efe4; font-size: 7px; font-style: normal; }
.plugin-field-hint { color: #a5aea7; font-size: 8px; }

.plugin-permission-list { display: grid; grid-template-columns: 1fr 1fr; gap: 7px; }
.plugin-permission { display: flex; align-items: center; gap: 9px; padding: 9px 11px; border: 1px solid #edf0ec; border-radius: 9px; background: #fbfcfa; cursor: pointer; }
.plugin-permission-granted { border-color: #cfe3d5; background: #f5faf6; }
.plugin-permission input { flex: 0 0 auto; }
.plugin-permission div { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.plugin-permission strong { color: #4f5d54; font-size: 9px; font-weight: 600; }
.plugin-permission small { overflow: hidden; color: #a9b1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; text-overflow: ellipsis; white-space: nowrap; }

.plugin-sync-row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; }
.plugin-sync-row select { padding: 7px 9px; border: 1px solid #e4eae4; border-radius: 8px; color: #57655c; background: #fff; font-size: 10px; }
.plugins-sync-cols { grid-template-columns: minmax(100px, 1.2fr) minmax(90px, 1fr) 78px 62px 52px minmax(70px, .8fr); }

.plugin-audit { display: flex; flex-direction: column; gap: 1px; }
.plugin-audit-row { display: grid; grid-template-columns: 66px 62px minmax(110px, 1.1fr) minmax(120px, 2fr) 58px; align-items: center; gap: 9px; padding: 6px 2px; border-bottom: 1px dashed #f2f4f1; color: #7d8a82; font-size: 8px; }
.plugin-audit-time { color: #a9b1ab; }
.plugin-audit-action { color: #55635a; font-weight: 600; }
.plugin-audit-action i { color: #a2aaa4; font-style: normal; font-weight: 400; }
.plugin-audit-detail { min-width: 0; overflow: hidden; color: #97a29a; text-overflow: ellipsis; white-space: nowrap; }
.plugin-audit-duration { color: #a9b1ab; text-align: right; }

.plugin-detail-foot { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 11px 20px; border-top: 1px solid #eef1ed; background: #fbfcfa; }
.plugin-detail-foot > span { color: #a9b1ab; font-size: 8px; }
.plugin-detail-foot > div { display: flex; gap: 7px; }

@media (max-width: 1180px) {
  .plugins-grid { grid-template-columns: 1fr; }
  .plugin-permission-list { grid-template-columns: 1fr; }
  .plugins-key-form { grid-template-columns: 1fr; }
  .plugin-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .plugin-facts { grid-template-columns: 1fr; }
  .plugin-audit-row { grid-template-columns: 58px 56px minmax(90px, 1fr) 50px; }
  .plugin-audit-detail { display: none; }
}
</style>
