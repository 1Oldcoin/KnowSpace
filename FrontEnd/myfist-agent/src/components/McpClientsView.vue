<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  Activity, ArrowLeft, Check, CircleAlert, Copy, KeyRound, LoaderCircle, Plug, Plus,
  RefreshCw, RotateCcw, Save, ShieldCheck, Trash2, X,
} from 'lucide-vue-next'
import {
  createAgentClient, deleteAgentClient, getAgentClientCatalog, getAgentClientOverview,
  getLocalMcpConfig, listAgentCalls, rotateAgentClientKey, updateAgentClient,
} from '../services/api'
import type {
  AgentCallRecordView, AgentClientCatalog, AgentClientOverview, AgentClientOverviewEntry,
  AgentClientPayload, LocalMcpConfig,
} from '../services/api'

const props = defineProps<{
  apiOnline: boolean
  knowledgeBases: Array<{ id: string; name: string; isDefault?: boolean }>
  activeKnowledgeBaseId: string
}>()
/** `back`：侧边栏没有这个页面，返回入口只能放在页头。 */
const emit = defineEmits<{ notice: [message: string]; back: [] }>()

type Tab = 'overview' | 'capabilities' | 'scope' | 'calls' | 'connect'

/**
 * 能力的人类可读名。
 *
 * 这份表是**界面文案**，不参与鉴权：真正的能力清单来自 `/api/agent-clients/catalog`，
 * 这里只负责把 `knowledge:read` 翻成「读取知识库」。漏翻译时直接显示原始串，
 * 宁可难看也不要显示错的解释。
 */
const capabilityLabels: Record<string, string> = {
  'knowledge:read': '读取知识库（搜索、查看资料）',
  'knowledge:write': '写入知识库（上传资料）',
  'workspace:read': '读取智能体工作目录',
  'workspace:write': '写入智能体工作目录',
  'files:read': '读取授权的本机目录',
  'files:write': '写入授权的本机目录',
  'avatar:read': '读取分身记忆',
  'avatar:write': '投喂与整理分身记忆',
  'chat:read': '读取聊天历史（含伴侣/分身对话）',
  '*': '全部能力',
}

const overview = ref<AgentClientOverview | null>(null)
const catalog = ref<AgentClientCatalog | null>(null)
const localConfig = ref<LocalMcpConfig | null>(null)
const isLoading = ref(false)
const loadError = ref('')
const windowHours = ref(24)

const busyAction = ref('')

const selectedId = ref('')
const activeTab = ref<Tab>('overview')
const clientCalls = ref<AgentCallRecordView[]>([])
const callsLoading = ref(false)

/** 密钥明文只在这个对象里存在一次，关闭面板即丢弃。 */
const revealedKey = ref<{ key: string; name: string } | null>(null)

const showCreate = ref(false)
const createForm = reactive({
  name: '',
  description: '',
  capabilities: ['knowledge:read'] as string[],
  knowledgeBaseIds: [] as string[],
  requestsPerMinute: 60,
})

const capabilityDraft = ref<string[]>([])
const toolDraft = ref<string[]>([])
const scopeDraft = ref<string[]>([])
const descriptionDraft = ref('')
const rateLimitDraft = ref(60)

let refreshTimer: ReturnType<typeof setInterval> | undefined

const clients = computed(() => overview.value?.clients ?? [])
const totals = computed(() => overview.value?.totals ?? {
  clients: 0, enabled: 0, disabled: 0, online: 0, calls: 0, success: 0, denied: 0, failed: 0,
})
const onlineWindow = computed(() => overview.value?.onlineWindowMinutes ?? 5)
const selected = computed(() => clients.value.find(client => client.id === selectedId.value) ?? null)

/** 只列出真正可选的能力：`*` 是通配符，单独勾它没有意义，放到末尾解释一句即可。 */
const assignableCapabilities = computed(() =>
  (catalog.value?.capabilities ?? []).filter(capability => capability !== '*'))

const toolsByCapability = computed(() => {
  const groups = new Map<string, { name: string; isWrite: boolean; usesKnowledgeBase: boolean }[]>()
  for (const tool of catalog.value?.tools ?? []) {
    const list = groups.get(tool.capability) ?? []
    list.push({ name: tool.name, isWrite: tool.isWrite, usesKnowledgeBase: tool.usesKnowledgeBase })
    groups.set(tool.capability, list)
  }
  return groups
})

const successRate = computed(() => {
  const total = totals.value.calls
  if (total === 0) return '—'
  return `${((totals.value.success / total) * 100).toFixed(1)}%`
})

const tabs = computed<Array<{ id: Tab; label: string }>>(() => [
  { id: 'overview', label: '概览' },
  { id: 'capabilities', label: '能力与工具' },
  { id: 'scope', label: '知识库范围' },
  { id: 'calls', label: `调用记录${clientCalls.value.length ? ` (${clientCalls.value.length})` : ''}` },
  { id: 'connect', label: '接入配置' },
])

function capabilityLabel(capability: string) {
  return capabilityLabels[capability] ?? capability
}

function knowledgeBaseName(id: string) {
  return props.knowledgeBases.find(base => base.id === id)?.name ?? id
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

function statusOf(client: AgentClientOverviewEntry) {
  if (!client.enabled) return { label: '已停用', tone: 'idle' }
  if (client.online) return { label: '最近活跃', tone: 'ok' }
  return { label: '待机', tone: 'warn' }
}

function scopeSummary(client: AgentClientOverviewEntry) {
  return client.allowedKnowledgeBaseIds.length === 0
    ? '全部知识库'
    : `${client.allowedKnowledgeBaseIds.length} 个知识库`
}

async function refresh(options: { silent?: boolean } = {}) {
  if (!props.apiOnline) return
  if (!options.silent) isLoading.value = true
  try {
    const [nextOverview, nextCatalog] = await Promise.all([
      getAgentClientOverview(windowHours.value),
      catalog.value ? Promise.resolve(catalog.value) : getAgentClientCatalog(),
    ])
    overview.value = nextOverview
    catalog.value = nextCatalog
    loadError.value = ''
  } catch (error) {
    // 静默刷新失败不要抹掉已有数据：一次网络抖动不该让整页变空。
    if (!options.silent) {
      loadError.value = error instanceof Error ? error.message : '无法读取接入客户端。'
      overview.value = null
    }
  } finally {
    isLoading.value = false
  }
}

async function loadLocalConfig() {
  if (!props.apiOnline) return
  try {
    localConfig.value = await getLocalMcpConfig()
  } catch {
    // 配置片段只是锦上添花，取不到就退回默认路径提示。
    localConfig.value = null
  }
}

async function openClient(client: AgentClientOverviewEntry, tab: Tab = 'overview') {
  selectedId.value = client.id
  activeTab.value = tab
  capabilityDraft.value = [...client.capabilities]
  toolDraft.value = [...client.allowedTools]
  scopeDraft.value = [...client.allowedKnowledgeBaseIds]
  descriptionDraft.value = client.description ?? ''
  rateLimitDraft.value = client.requestsPerMinute
  await loadCalls(client.id)
}

async function loadCalls(clientId: string) {
  callsLoading.value = true
  try {
    clientCalls.value = await listAgentCalls(clientId, 50)
  } catch {
    clientCalls.value = []
  } finally {
    callsLoading.value = false
  }
}

async function applyUpdate(payload: AgentClientPayload, successMessage: string) {
  const client = selected.value
  if (!client) return
  busyAction.value = client.id
  try {
    await updateAgentClient(client.id, payload)
    await refresh({ silent: true })
    emit('notice', successMessage)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '保存失败。')
  } finally {
    busyAction.value = ''
  }
}

async function saveCapabilities() {
  await applyUpdate(
    { capabilities: capabilityDraft.value, allowedTools: toolDraft.value },
    '能力与工具白名单已更新。')
}

async function saveToolWhitelist() {
  await applyUpdate({ allowedTools: toolDraft.value }, '工具白名单已更新。')
}

async function saveScope() {
  await applyUpdate(
    { allowedKnowledgeBaseIds: scopeDraft.value },
    scopeDraft.value.length === 0
      ? '已改为不限制知识库。'
      : `已限定到 ${scopeDraft.value.length} 个知识库。`)
}

async function saveBasics() {
  await applyUpdate(
    { description: descriptionDraft.value, requestsPerMinute: rateLimitDraft.value },
    '基础信息已更新。')
}

async function toggleEnabled(client: AgentClientOverviewEntry) {
  busyAction.value = client.id
  try {
    await updateAgentClient(client.id, { enabled: !client.enabled })
    await refresh({ silent: true })
    emit('notice', client.enabled ? `已停用「${client.name}」。` : `已启用「${client.name}」。`)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '操作失败。')
  } finally {
    busyAction.value = ''
  }
}

async function rotate(client: AgentClientOverviewEntry) {
  busyAction.value = client.id
  try {
    const result = await rotateAgentClientKey(client.id)
    revealedKey.value = { key: result.key, name: client.name }
    await refresh({ silent: true })
    // 轮换后旧密钥立刻失效，客户端会一直 401 直到用户把新密钥填过去——所以必须明确说。
    emit('notice', `「${client.name}」的密钥已轮换，旧密钥立即失效。`)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '轮换失败。')
  } finally {
    busyAction.value = ''
  }
}

async function remove(client: AgentClientOverviewEntry) {
  const confirmed = window.confirm(
    `删除客户端「${client.name}」？\n\n它的密钥会立即失效，调用审计历史会保留。此操作不可撤销。`)
  if (!confirmed) return
  busyAction.value = client.id
  try {
    await deleteAgentClient(client.id)
    if (selectedId.value === client.id) selectedId.value = ''
    await refresh({ silent: true })
    emit('notice', `已删除「${client.name}」。`)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '删除失败。')
  } finally {
    busyAction.value = ''
  }
}

function openCreate() {
  createForm.name = ''
  createForm.description = ''
  createForm.capabilities = ['knowledge:read']
  // 默认限定到当前正在用的知识库，而不是「不限制」：新建客户端时最省事的选项
  // 也是最容易越权的选项，默认值必须偏向保守。
  createForm.knowledgeBaseIds = props.activeKnowledgeBaseId ? [props.activeKnowledgeBaseId] : []
  createForm.requestsPerMinute = 60
  showCreate.value = true
}

async function submitCreate() {
  const name = createForm.name.trim()
  if (!name) {
    emit('notice', '客户端名称必填。')
    return
  }

  busyAction.value = 'create'
  try {
    const result = await createAgentClient({
      name,
      description: createForm.description.trim() || null,
      capabilities: createForm.capabilities,
      allowedKnowledgeBaseIds: createForm.knowledgeBaseIds,
      requestsPerMinute: createForm.requestsPerMinute,
    })
    showCreate.value = false
    revealedKey.value = { key: result.key, name }
    await refresh({ silent: true })
    emit('notice', `已创建「${name}」。密钥明文只在这一步出现一次。`)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '创建失败。')
  } finally {
    busyAction.value = ''
  }
}

function toggleInList(list: string[], value: string): string[] {
  return list.includes(value) ? list.filter(item => item !== value) : [...list, value]
}

function toggleCapability(capability: string) {
  capabilityDraft.value = toggleInList(capabilityDraft.value, capability)
  // 取消某能力时，把该能力下的工具从白名单里一并摘掉：
  // 留着它们不会有安全后果（能力校验在前），但界面会显示一堆"允许但其实调不动"的工具。
  const allowed = toolsByCapability.value.get(capability) ?? []
  const names = new Set(allowed.map(tool => tool.name))
  toolDraft.value = toolDraft.value.filter(name => !names.has(name))
}

function toggleTool(toolName: string) {
  toolDraft.value = toggleInList(toolDraft.value, toolName)
}

function toggleScope(knowledgeBaseId: string) {
  scopeDraft.value = toggleInList(scopeDraft.value, knowledgeBaseId)
}

async function copyText(text: string, label: string) {
  try {
    await navigator.clipboard.writeText(text)
    emit('notice', `${label}已复制。`)
  } catch {
    emit('notice', '复制失败，请手动选择文本。')
  }
}

/** 各客户端的记住配置格式。密钥留空时用占位符，方便用户先抄结构再补密钥。 */
function snippets(key?: string) {
  const endpoint = localConfig.value?.endpoint ?? 'http://127.0.0.1:<端口>/mcp'
  const secret = key ?? 'maf_xxxxxxxxxxxxxxxx'
  return [
    {
      id: 'cursor',
      label: 'Cursor（~/.cursor/mcp.json）',
      language: 'json',
      content: JSON.stringify({
        mcpServers: { zhijian: { url: endpoint, headers: { 'X-Api-Key': secret } } },
      }, null, 2),
    },
    {
      id: 'claude-desktop',
      label: 'Claude Desktop（claude_desktop_config.json）',
      language: 'json',
      content: JSON.stringify({
        mcpServers: { zhijian: { url: endpoint, headers: { 'X-Api-Key': secret } } },
      }, null, 2),
    },
    {
      id: 'claude-code',
      label: 'Claude Code',
      language: 'bash',
      content: `claude mcp add --transport http zhijian ${endpoint} --header "X-Api-Key: ${secret}"`,
    },
    {
      id: 'curl',
      label: 'curl（自检）',
      language: 'bash',
      content: `curl -s ${endpoint} -H "Content-Type: application/json" -H "X-Api-Key: ${secret}" \\\n  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'`,
    },
  ]
}

const revealSnippets = computed(() => snippets(revealedKey.value?.key))
const detailSnippets = computed(() => snippets())

watch(() => props.apiOnline, (online) => {
  if (online) {
    void refresh()
    void loadLocalConfig()
  }
})

watch(windowHours, () => { void refresh() })

onMounted(() => {
  void refresh()
  void loadLocalConfig()
  // 每 20 秒刷新一次：「在线」是「最近 5 分钟有请求」，不刷新的话相对时间会越来越不准。
  refreshTimer = setInterval(() => { void refresh({ silent: true }) }, 20_000)
})

onBeforeUnmount(() => {
  if (refreshTimer) clearInterval(refreshTimer)
})
</script>

<template>
  <section class="page-heading-row">
    <div>
      <p class="section-kicker">MCP 接入</p>
      <h1>接入的 AI 工具</h1>
      <p class="welcome-copy">外部 Agent 通过 <code>/mcp</code> 接入本机知识库。这里管理它们的密钥、能力、可访问的知识库，并查看调用记录。</p>
    </div>
    <div class="mcp-heading-actions">
      <button class="button button-soft" @click="emit('back')">
        <ArrowLeft :size="14" /> 返回设置
      </button>
      <label class="mcp-window">
        <span>统计窗口</span>
        <select v-model.number="windowHours" :disabled="!apiOnline">
          <option :value="1">1 小时</option>
          <option :value="24">24 小时</option>
          <option :value="168">7 天</option>
          <option :value="720">30 天</option>
        </select>
      </label>
      <button class="button button-soft" :disabled="!apiOnline || isLoading" @click="refresh()">
        <LoaderCircle v-if="isLoading" :size="14" class="spinning" /><RefreshCw v-else :size="14" /> 刷新
      </button>
      <button class="button button-primary" :disabled="!apiOnline" @click="openCreate">
        <Plus :size="14" /> 新建客户端
      </button>
    </div>
  </section>

  <p v-if="!apiOnline" class="empty-state">
    <strong>API 未连接</strong>
    <span>接入客户端需要后端在线。</span>
  </p>
  <p v-else-if="loadError" class="empty-state">
    <strong>读取失败</strong>
    <span>{{ loadError }}</span>
  </p>
  <p v-else-if="isLoading && !overview" class="empty-state">
    <strong>正在读取接入客户端…</strong>
  </p>

  <template v-else>
    <!-- 统计口径要说清：MCP 是无状态传输，没有"当前连接"可查，只能按最后请求时间近似。 -->
    <p class="mcp-banner">
      <CircleAlert :size="13" />
      服务端不知道谁「正连着」：<code>/mcp</code> 是 Streamable HTTP 无状态传输，每个请求都是独立往返。
      因此「最近活跃」= 最后 <b>{{ onlineWindow }}</b> 分钟内带有效密钥调用过。
    </p>

    <div class="mcp-stats">
      <div class="mcp-stat">
        <span class="mcp-stat-label">接入客户端</span>
        <strong class="mcp-stat-value">{{ totals.clients }}</strong>
        <span class="mcp-stat-foot">启用 {{ totals.enabled }} · 停用 {{ totals.disabled }}</span>
      </div>
      <div class="mcp-stat mcp-stat-ok">
        <span class="mcp-stat-label">最近活跃</span>
        <strong class="mcp-stat-value">{{ totals.online }}</strong>
        <span class="mcp-stat-foot">{{ onlineWindow }} 分钟内调用过</span>
      </div>
      <div class="mcp-stat">
        <span class="mcp-stat-label">窗口内调用</span>
        <strong class="mcp-stat-value">{{ totals.calls }}</strong>
        <span class="mcp-stat-foot">成功 {{ totals.success }} · 成功率 {{ successRate }}</span>
      </div>
      <div class="mcp-stat" :class="{ 'mcp-stat-warn': totals.denied + totals.failed > 0 }">
        <span class="mcp-stat-label">被拒绝 / 失败</span>
        <strong class="mcp-stat-value">{{ totals.denied + totals.failed }}</strong>
        <span class="mcp-stat-foot">拒绝 {{ totals.denied }} · 失败 {{ totals.failed }}</span>
      </div>
    </div>

    <div v-if="clients.length === 0" class="empty-state">
      <strong>还没有接入客户端</strong>
      <span>点右上角「新建客户端」，把密钥填进 Cursor / Claude Desktop 就能用。</span>
    </div>

    <section v-else class="mcp-grid">
      <article
        v-for="client in clients"
        :key="client.id"
        class="mcp-card"
        :class="{ 'mcp-card-idle': !client.enabled }"
        @click="openClient(client)"
      >
        <header class="mcp-card-head">
          <span class="mcp-icon"><Plug :size="16" /></span>
          <div class="mcp-title">
            <strong>{{ client.name }}</strong>
            <small>{{ client.keyPrefix }}…</small>
          </div>
          <span class="mcp-state" :class="`mcp-state-${statusOf(client).tone}`">{{ statusOf(client).label }}</span>
        </header>

        <p v-if="client.description" class="mcp-card-desc">{{ client.description }}</p>

        <div class="mcp-card-meta">
          <span>{{ client.capabilities.length }} 项能力</span>
          <span>{{ scopeSummary(client) }}</span>
          <span>{{ client.requestsPerMinute === 0 ? '不限流' : `${client.requestsPerMinute}/分钟` }}</span>
        </div>

        <div class="mcp-card-usage">
          <span class="mcp-usage-main">
            {{ client.usage ? `${client.usage.total} 次调用` : '窗口内无调用' }}
          </span>
          <span v-if="client.usage" class="mcp-usage-sub">
            成功 {{ client.usage.success }} · 拒绝 {{ client.usage.denied }} · 失败 {{ client.usage.failed }}
          </span>
          <span class="mcp-usage-time">最后活跃 {{ formatRelative(client.lastSeenAt) }}</span>
        </div>

        <footer class="mcp-card-foot" @click.stop>
          <button class="mcp-quick" :disabled="busyAction !== ''" title="详情与配置" @click="openClient(client)">详情</button>
          <button class="mcp-quick" :disabled="busyAction !== ''" :title="client.enabled ? '停用' : '启用'" @click="toggleEnabled(client)">
            {{ client.enabled ? '停用' : '启用' }}
          </button>
          <button class="mcp-quick" :disabled="busyAction !== ''" title="轮换密钥" @click="rotate(client)">
            <RotateCcw :size="12" /> 轮换
          </button>
          <button class="mcp-quick mcp-quick-danger" :disabled="busyAction !== ''" title="删除" @click="remove(client)">
            <Trash2 :size="12" />
          </button>
        </footer>
      </article>
    </section>
  </template>

  <!-- 密钥明文：只出现一次，关掉就没了。 -->
  <Teleport to="body">
    <div v-if="revealedKey" class="mcp-scrim" @click.self="revealedKey = null">
      <section class="mcp-modal" role="dialog" aria-modal="true" aria-label="客户端密钥">
        <header class="mcp-modal-head">
          <span class="mcp-icon mcp-icon-warn"><KeyRound :size="16" /></span>
          <div>
            <h2>「{{ revealedKey.name }}」的密钥</h2>
            <p>这是明文<b>唯一一次</b>显示。系统只保存哈希，关掉这个窗口就再也取不回来了。</p>
          </div>
        </header>

        <div class="mcp-secret">
          <code>{{ revealedKey.key }}</code>
          <button class="button button-soft" @click="copyText(revealedKey.key, '密钥')"><Copy :size="13" /> 复制</button>
        </div>

        <p class="mcp-modal-hint">
          <ShieldCheck :size="13" /> 把它填进客户端的请求头（<code>X-Api-Key</code> 或 <code>Authorization: Bearer</code>）。
          密钥泄露时立刻点「轮换」，旧密钥会立即失效。
        </p>

        <div class="mcp-snippets">
          <div v-for="snippet in revealSnippets" :key="snippet.id" class="mcp-snippet">
            <div class="mcp-snippet-head">
              <span>{{ snippet.label }}</span>
              <button class="text-button" @click="copyText(snippet.content, snippet.label)"><Copy :size="11" /> 复制</button>
            </div>
            <pre>{{ snippet.content }}</pre>
          </div>
        </div>

        <footer class="mcp-modal-foot">
          <button class="button button-primary" @click="revealedKey = null">我已保存</button>
        </footer>
      </section>
    </div>
  </Teleport>

  <!-- 新建客户端 -->
  <Teleport to="body">
    <div v-if="showCreate" class="mcp-scrim" @click.self="showCreate = false">
      <section class="mcp-modal" role="dialog" aria-modal="true" aria-label="新建接入客户端">
        <header class="mcp-modal-head">
          <span class="mcp-icon"><Plus :size="16" /></span>
          <div>
            <h2>新建接入客户端</h2>
            <p>密钥由服务端生成，明文只在创建后显示一次。</p>
          </div>
        </header>

        <label class="mcp-field">
          <span>名称</span>
          <input v-model="createForm.name" maxlength="100" placeholder="例如：Cursor、Claude Desktop" />
        </label>

        <label class="mcp-field">
          <span>说明（可选）</span>
          <input v-model="createForm.description" maxlength="500" placeholder="这台机器上哪个工具在用" />
        </label>

        <div class="mcp-field">
          <span>能力</span>
          <div class="mcp-check-list">
            <label v-for="capability in assignableCapabilities" :key="capability" class="mcp-check" :class="{ 'mcp-check-on': createForm.capabilities.includes(capability) }">
              <input type="checkbox" :checked="createForm.capabilities.includes(capability)" @change="createForm.capabilities = toggleInList(createForm.capabilities, capability)" />
              <span>{{ capabilityLabel(capability) }}<small>{{ capability }}</small></span>
            </label>
          </div>
          <small class="mcp-field-hint">默认只给「读取知识库」。写能力、分身、聊天历史按需再加。</small>
        </div>

        <div class="mcp-field">
          <span>可访问的知识库</span>
          <div class="mcp-check-list">
            <label v-for="base in knowledgeBases" :key="base.id" class="mcp-check" :class="{ 'mcp-check-on': createForm.knowledgeBaseIds.includes(base.id) }">
              <input type="checkbox" :checked="createForm.knowledgeBaseIds.includes(base.id)" @change="createForm.knowledgeBaseIds = toggleInList(createForm.knowledgeBaseIds, base.id)" />
              <span>{{ base.name }}<small>{{ base.id }}</small></span>
            </label>
          </div>
          <small class="mcp-field-hint">一个都不勾 = 不限制（所有知识库）。已默认勾上当前知识库。</small>
        </div>

        <label class="mcp-field mcp-field-inline">
          <span>每分钟请求上限</span>
          <input v-model.number="createForm.requestsPerMinute" type="number" min="0" max="100000" />
          <small class="mcp-field-hint">0 = 不限流</small>
        </label>

        <footer class="mcp-modal-foot">
          <button class="button button-soft" @click="showCreate = false">取消</button>
          <button class="button button-primary" :disabled="busyAction === 'create'" @click="submitCreate">
            <LoaderCircle v-if="busyAction === 'create'" :size="14" class="spinning" /> 创建
          </button>
        </footer>
      </section>
    </div>
  </Teleport>

  <!-- 客户端详情 -->
  <Teleport to="body">
    <div v-if="selected" class="mcp-scrim" @click.self="selectedId = ''">
      <section class="mcp-detail" role="dialog" aria-modal="true" aria-label="接入客户端详情">
        <header class="mcp-detail-head">
          <span class="mcp-icon"><Plug :size="17" /></span>
          <div class="mcp-detail-title">
            <h2>{{ selected.name }}<span class="mcp-state" :class="`mcp-state-${statusOf(selected).tone}`">{{ statusOf(selected).label }}</span></h2>
            <small>{{ selected.keyPrefix }}… · {{ selected.id }}</small>
          </div>
          <button class="icon-button" aria-label="关闭" @click="selectedId = ''"><X :size="16" /></button>
        </header>

        <nav class="mcp-tabs">
          <button v-for="tab in tabs" :key="tab.id" class="mcp-tab" :class="{ active: activeTab === tab.id }" @click="activeTab = tab.id">{{ tab.label }}</button>
        </nav>

        <div class="mcp-detail-body">
          <div v-show="activeTab === 'overview'" class="mcp-pane">
            <div class="mcp-pane-stats">
              <div><span>窗口内调用</span><strong>{{ selected.usage?.total ?? 0 }}</strong></div>
              <div><span>成功</span><strong>{{ selected.usage?.success ?? 0 }}</strong></div>
              <div><span>拒绝</span><strong>{{ selected.usage?.denied ?? 0 }}</strong></div>
              <div><span>失败</span><strong>{{ selected.usage?.failed ?? 0 }}</strong></div>
            </div>

            <dl class="mcp-facts">
              <div><dt>最后活跃</dt><dd>{{ formatTime(selected.lastSeenAt) }}（{{ formatRelative(selected.lastSeenAt) }}）</dd></div>
              <div><dt>最后来源</dt><dd class="mcp-mono">{{ selected.lastSeenAddress ?? '—' }}</dd></div>
              <div><dt>窗口内最后调用</dt><dd>{{ formatTime(selected.usage?.lastCallAt) }}</dd></div>
              <div><dt>创建 / 更新</dt><dd>{{ formatTime(selected.createdAt) }} / {{ formatTime(selected.updatedAt) }}</dd></div>
              <div><dt>能力</dt><dd class="mcp-mono">{{ selected.capabilities.join(', ') || '（无）' }}</dd></div>
              <div><dt>知识库</dt><dd>{{ scopeSummary(selected) }}</dd></div>
            </dl>

            <div class="mcp-field">
              <span>说明</span>
              <input v-model="descriptionDraft" maxlength="500" placeholder="这台机器上哪个工具在用" />
            </div>
            <label class="mcp-field mcp-field-inline">
              <span>每分钟请求上限</span>
              <input v-model.number="rateLimitDraft" type="number" min="0" max="100000" />
              <small class="mcp-field-hint">0 = 不限流</small>
            </label>

            <div class="mcp-actions-row">
              <button class="button button-primary" :disabled="busyAction !== ''" @click="saveBasics"><Save :size="13" /> 保存</button>
              <button class="button button-soft" :disabled="busyAction !== ''" @click="toggleEnabled(selected)">
                {{ selected.enabled ? '停用该客户端' : '启用该客户端' }}
              </button>
              <button class="button button-soft" :disabled="busyAction !== ''" @click="rotate(selected)"><RotateCcw :size="13" /> 轮换密钥</button>
              <button class="button button-soft mcp-danger" :disabled="busyAction !== ''" @click="remove(selected)"><Trash2 :size="13" /> 删除</button>
            </div>
          </div>

          <div v-show="activeTab === 'capabilities'" class="mcp-pane">
            <p class="mcp-pane-hint">能力决定「能不能调这类工具」，工具白名单进一步限定到具体工具名。取消能力会自动摘掉它下面的工具。</p>

            <div class="mcp-check-list">
              <label v-for="capability in assignableCapabilities" :key="capability" class="mcp-check" :class="{ 'mcp-check-on': capabilityDraft.includes(capability) }">
                <input type="checkbox" :checked="capabilityDraft.includes(capability)" @change="toggleCapability(capability)" />
                <span>{{ capabilityLabel(capability) }}<small>{{ capability }}</small></span>
              </label>
            </div>

            <div class="mcp-tool-groups">
              <div v-for="[capability, tools] in [...toolsByCapability]" :key="capability" class="mcp-tool-group">
                <span class="mcp-block-label">{{ capabilityLabel(capability) }}<small>{{ capability }}</small></span>
                <div class="mcp-tool-chips">
                  <button
                    v-for="tool in tools"
                    :key="tool.name"
                    class="mcp-tool-chip"
                    :class="{ 'mcp-tool-chip-on': toolDraft.includes(tool.name), 'mcp-tool-chip-off': !capabilityDraft.includes(capability) }"
                    :title="tool.isWrite ? '写操作' : '只读'"
                    @click="capabilityDraft.includes(capability) && toggleTool(tool.name)"
                  >
                    {{ tool.name }}<i v-if="tool.isWrite">写</i>
                  </button>
                </div>
              </div>
            </div>
            <p class="mcp-pane-hint">一个工具都不选 = 该能力范围内的工具全部放开。</p>

            <div class="mcp-actions-row">
              <button class="button button-primary" :disabled="busyAction !== ''" @click="saveCapabilities"><Save :size="13" /> 保存</button>
            </div>
          </div>

          <div v-show="activeTab === 'scope'" class="mcp-pane">
            <p class="mcp-pane-hint">
              不勾任何知识库 = 不限制。勾选后该客户端只能看到并访问这些库，
              <b>连库名都不会出现在 <code>list_knowledge_bases</code> 里</b>。
            </p>

            <div class="mcp-check-list">
              <label v-for="base in knowledgeBases" :key="base.id" class="mcp-check" :class="{ 'mcp-check-on': scopeDraft.includes(base.id) }">
                <input type="checkbox" :checked="scopeDraft.includes(base.id)" @change="toggleScope(base.id)" />
                <span>{{ base.name }}<small>{{ base.id }}</small></span>
              </label>
            </div>

            <p class="mcp-pane-hint">
              已选：{{ scopeDraft.length === 0 ? '不限制（全部知识库）' : scopeDraft.map(knowledgeBaseName).join('、') }}
            </p>

            <div class="mcp-actions-row">
              <button class="button button-primary" :disabled="busyAction !== ''" @click="saveScope"><Save :size="13" /> 保存</button>
              <button class="button button-soft" :disabled="busyAction !== ''" @click="scopeDraft = []">清空（改为不限制）</button>
            </div>
          </div>

          <div v-show="activeTab === 'calls'" class="mcp-pane">
            <p v-if="callsLoading" class="mcp-pane-hint"><LoaderCircle :size="13" class="spinning" /> 正在读取…</p>
            <p v-else-if="clientCalls.length === 0" class="mcp-pane-hint">还没有调用记录。</p>
            <div v-else class="mcp-calls">
              <div class="mcp-calls-row mcp-calls-head">
                <span>时间</span><span>结果</span><span>工具</span><span>知识库</span><span>耗时</span>
              </div>
              <div v-for="call in clientCalls" :key="call.id" class="mcp-calls-row">
                <span :title="formatTime(call.occurredAt)">{{ formatRelative(call.occurredAt) }}</span>
                <span class="mcp-state" :class="call.outcome === 'success' ? 'mcp-state-ok' : 'mcp-state-bad'">{{ call.outcome }}</span>
                <span class="mcp-mono">{{ call.tool ?? '—' }}</span>
                <span class="mcp-mono" :title="call.knowledgeBaseId ?? ''">{{ call.knowledgeBaseId ? knowledgeBaseName(call.knowledgeBaseId) : '—' }}</span>
                <span>{{ call.durationMs }} ms</span>
              </div>
            </div>
            <p v-if="selected.usage" class="mcp-pane-hint">
              窗口内共 {{ selected.usage.total }} 次调用，这里显示最近 {{ clientCalls.length }} 条明细（不含请求正文）。
            </p>
            <div class="mcp-actions-row">
              <button class="button button-soft" :disabled="callsLoading" @click="loadCalls(selected.id)"><RefreshCw :size="13" /> 刷新</button>
            </div>
          </div>

          <div v-show="activeTab === 'connect'" class="mcp-pane">
            <p class="mcp-pane-hint">
              端点来自当前实例的实际监听地址。密钥不在这里显示——如果是新建的，请回到密钥面板复制；
              忘了就点「轮换」生成新的。
            </p>
            <div class="mcp-snippets">
              <div v-for="snippet in detailSnippets" :key="snippet.id" class="mcp-snippet">
                <div class="mcp-snippet-head">
                  <span>{{ snippet.label }}</span>
                  <button class="text-button" @click="copyText(snippet.content, snippet.label)"><Copy :size="11" /> 复制</button>
                </div>
                <pre>{{ snippet.content }}</pre>
              </div>
            </div>
          </div>
        </div>

        <footer class="mcp-detail-foot">
          <span v-if="localConfig" class="mcp-foot-note"><Activity :size="12" /> {{ localConfig.endpoint }}</span>
          <span v-else class="mcp-foot-note"><CircleAlert :size="12" /> 未取到本机端点，配置片段使用默认路径</span>
          <button class="button button-soft" @click="selectedId = ''">关闭</button>
        </footer>
      </section>
    </div>
  </Teleport>

  <p v-if="overview" class="mcp-footnote">
    <Check :size="12" /> 审计只记录「谁、调了什么工具、打到哪个库、结果如何」，不含请求正文与搜索词。
  </p>
</template>

<style scoped>
.mcp-heading-actions { display: flex; align-items: center; gap: 9px; margin-bottom: 3px; }
.mcp-window { display: flex; align-items: center; gap: 6px; color: #9aa49d; font-size: 9px; }
.mcp-window select { height: 30px; padding: 0 6px; border: 1px solid #e6ebe6; border-radius: 7px; color: #6d7a72; background: #fff; font-size: 9px; }

.mcp-banner { display: flex; align-items: flex-start; gap: 7px; margin: 0 0 14px; padding: 9px 12px; border: 1px solid #edf0ec; border-radius: 9px; color: #7d8a82; background: #fafbf9; font-size: 9px; line-height: 1.75; }
.mcp-banner code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.mcp-banner b { color: #5f7b69; font-weight: 600; }

.mcp-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin-bottom: 20px; }
.mcp-stat { display: flex; flex-direction: column; gap: 5px; padding: 13px 15px; border: 1px solid #e9ede8; border-radius: 11px; background: #fff; }
.mcp-stat-ok { border-color: #d5e6da; background: #f8fcf9; }
.mcp-stat-warn { border-color: #f0e3dc; background: #fdfaf8; }
.mcp-stat-label { color: #a2aaa4; font-size: 8px; letter-spacing: .5px; }
.mcp-stat-value { color: #344039; font-size: 21px; line-height: 1; letter-spacing: -.6px; font-weight: 600; }
.mcp-stat-foot { color: #a2aaa4; font-size: 8px; }

.mcp-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 13px; }
.mcp-card { display: flex; flex-direction: column; gap: 10px; padding: 14px 15px 12px; border: 1px solid #e9ede8; border-radius: 11px; background: #fff; cursor: pointer; transition: transform .18s, box-shadow .18s, border-color .18s; }
.mcp-card:hover { transform: translateY(-2px); border-color: #d8e4da; box-shadow: 0 9px 22px #263b2f0d; }
.mcp-card-idle { opacity: .78; }
.mcp-card-head { display: flex; align-items: flex-start; gap: 10px; }
.mcp-icon { width: 32px; height: 32px; display: grid; place-items: center; flex: 0 0 auto; border-radius: 9px; color: #4f8369; background: #eaf3eb; }
.mcp-icon-warn { color: #b98a4d; background: #fdf4e6; }
.mcp-title { min-width: 0; display: flex; flex-direction: column; gap: 3px; flex: 1; }
.mcp-title strong { overflow: hidden; color: #45544b; font-size: 11px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.mcp-title small { overflow: hidden; color: #a9b1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; text-overflow: ellipsis; white-space: nowrap; }

.mcp-state { display: inline-flex; align-items: center; flex: 0 0 auto; padding: 3px 7px; border-radius: 5px; font-size: 8px; font-weight: 600; white-space: nowrap; }
.mcp-state-ok { color: #3f7b5f; background: #e9f3ea; }
.mcp-state-warn { color: #97835f; background: #f7f2e6; }
.mcp-state-idle { color: #8d968f; background: #f1f3f0; }
.mcp-state-bad { color: #b07068; background: #fbeceb; }

.mcp-card-desc { margin: 0; overflow: hidden; color: #8a958e; font-size: 9px; line-height: 1.6; text-overflow: ellipsis; white-space: nowrap; }
.mcp-card-meta { display: flex; flex-wrap: wrap; gap: 6px; color: #9aa49e; font-size: 8px; }
.mcp-card-meta span { padding: 3px 6px; border: 1px solid #eef1ed; border-radius: 5px; background: #fbfcfa; }

.mcp-card-usage { display: flex; flex-direction: column; gap: 3px; padding: 9px 10px; border-radius: 8px; background: #fafbf9; }
.mcp-usage-main { color: #4e5c53; font-size: 10px; font-weight: 600; }
.mcp-usage-sub { color: #9aa49e; font-size: 8px; }
.mcp-usage-time { color: #b0b7b1; font-size: 8px; }

.mcp-card-foot { display: flex; align-items: center; gap: 6px; padding-top: 2px; }
.mcp-quick { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border: 1px solid #e8ece7; border-radius: 6px; color: #7d8a82; background: #fff; font-size: 8px; }
.mcp-quick:hover { border-color: #cbdccf; color: #42785a; background: #f7fbf8; }
.mcp-quick-danger:hover { border-color: #e6c8c4; color: #b07068; background: #fdf6f5; }
.mcp-quick:disabled { opacity: .5; }

.mcp-scrim { position: fixed; inset: 0; z-index: 90; display: grid; place-items: center; padding: 24px; background: #1c2b2216; backdrop-filter: blur(2px); }
.mcp-modal { width: min(660px, 100%); max-height: 88vh; overflow: auto; padding: 20px 22px 16px; border: 1px solid #e6ebe6; border-radius: 14px; background: #fff; box-shadow: 0 24px 60px #1c2b2224; }
.mcp-modal-head { display: flex; align-items: flex-start; gap: 11px; margin-bottom: 16px; }
.mcp-modal-head h2 { margin: 0; color: #33413a; font-size: 14px; font-weight: 600; }
.mcp-modal-head p { margin: 5px 0 0; color: #8f9a93; font-size: 9px; line-height: 1.7; }
.mcp-modal-head b { color: #b07068; }
.mcp-modal-hint { display: flex; align-items: flex-start; gap: 6px; margin: 10px 0 14px; color: #8f9a93; font-size: 9px; line-height: 1.7; }
.mcp-modal-hint code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.mcp-modal-foot { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; padding-top: 14px; border-top: 1px solid #f0f2ef; }

.mcp-secret { display: flex; align-items: center; gap: 10px; padding: 11px 13px; border: 1px dashed #d8c9a8; border-radius: 9px; background: #fdfaf3; }
.mcp-secret code { flex: 1; overflow-wrap: anywhere; color: #7a5f2e; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11px; }

.mcp-field { display: flex; flex-direction: column; gap: 6px; margin-bottom: 14px; }
.mcp-field > span { color: #6a7a70; font-size: 9px; font-weight: 600; }
.mcp-field input { height: 33px; padding: 0 10px; border: 1px solid #e6ebe6; border-radius: 8px; color: #45544b; background: #fff; font-size: 10px; }
.mcp-field input:focus { border-color: #b6d2bf; outline: 0; }
.mcp-field-inline { flex-direction: row; align-items: center; gap: 9px; }
.mcp-field-inline input { width: 110px; }
.mcp-field-hint { color: #a4ada7; font-size: 8px; line-height: 1.7; }

.mcp-check-list { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 7px; }
.mcp-check { display: flex; align-items: flex-start; gap: 8px; padding: 9px 11px; border: 1px solid #edf0ec; border-radius: 8px; background: #fff; cursor: pointer; }
.mcp-check:hover { border-color: #d8e4da; }
.mcp-check-on { border-color: #c6dfcd; background: #f7fbf8; }
.mcp-check input { margin-top: 2px; }
.mcp-check > span { display: flex; flex-direction: column; gap: 3px; color: #5c6a62; font-size: 9px; line-height: 1.55; }
.mcp-check small { color: #adb5af; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.mcp-detail { width: min(920px, 100%); max-height: 90vh; display: flex; flex-direction: column; border: 1px solid #e6ebe6; border-radius: 14px; background: #fff; box-shadow: 0 24px 60px #1c2b2224; }
.mcp-detail-head { display: flex; align-items: flex-start; gap: 11px; padding: 18px 20px 13px; }
.mcp-detail-title { flex: 1; min-width: 0; }
.mcp-detail-title h2 { display: flex; align-items: center; gap: 8px; margin: 0; color: #33413a; font-size: 14px; font-weight: 600; }
.mcp-detail-title small { display: block; margin-top: 5px; color: #adb5af; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.mcp-tabs { display: flex; gap: 2px; padding: 0 20px; border-bottom: 1px solid #f0f2ef; }
.mcp-tab { padding: 9px 11px; border: 0; border-bottom: 2px solid transparent; color: #8e9992; background: transparent; font-size: 9px; }
.mcp-tab:hover { color: #57836a; }
.mcp-tab.active { border-bottom-color: #4d8a69; color: #397c65; font-weight: 600; }

.mcp-detail-body { flex: 1; overflow: auto; padding: 16px 20px; }
.mcp-pane { display: flex; flex-direction: column; gap: 13px; }
.mcp-pane-hint { margin: 0; color: #8f9a93; font-size: 9px; line-height: 1.75; }
.mcp-pane-hint code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.mcp-pane-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 10px; }
.mcp-pane-stats div { display: flex; flex-direction: column; gap: 5px; padding: 11px 12px; border: 1px solid #eef1ed; border-radius: 9px; background: #fafbf9; }
.mcp-pane-stats span { color: #a2aaa4; font-size: 8px; }
.mcp-pane-stats strong { color: #35423b; font-size: 17px; font-weight: 600; }

.mcp-facts { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 9px 18px; margin: 0; }
.mcp-facts div { display: flex; flex-direction: column; gap: 3px; }
.mcp-facts dt { color: #a5ada7; font-size: 8px; }
.mcp-facts dd { margin: 0; color: #4f5d55; font-size: 9px; line-height: 1.6; word-break: break-all; }
.mcp-mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.mcp-actions-row { display: flex; flex-wrap: wrap; gap: 8px; padding-top: 4px; }
.mcp-danger:hover { border-color: #e6c8c4; color: #b07068; background: #fdf6f5; }

.mcp-tool-groups { display: flex; flex-direction: column; gap: 11px; }
.mcp-tool-group { display: flex; flex-direction: column; gap: 6px; }
.mcp-block-label { display: flex; align-items: baseline; gap: 6px; color: #6a7a70; font-size: 9px; font-weight: 600; }
.mcp-block-label small { color: #b0b7b1; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-weight: 400; }
.mcp-tool-chips { display: flex; flex-wrap: wrap; gap: 5px; }
.mcp-tool-chip { display: inline-flex; align-items: center; gap: 4px; padding: 4px 8px; border: 1px dashed #e2e8e2; border-radius: 6px; color: #96a09a; background: #fcfdfb; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.mcp-tool-chip-on { border-style: solid; border-color: #c6dfcd; color: #3f7b5f; background: #f4faf6; }
.mcp-tool-chip-off { opacity: .45; cursor: not-allowed; }
.mcp-tool-chip i { padding: 1px 3px; border-radius: 3px; color: #b07068; background: #fbeceb; font-size: 7px; font-style: normal; }

.mcp-calls { display: flex; flex-direction: column; border: 1px solid #eef1ed; border-radius: 9px; overflow: hidden; }
.mcp-calls-row { display: grid; grid-template-columns: minmax(78px, .8fr) 62px minmax(130px, 1.4fr) minmax(110px, 1.2fr) 62px; align-items: center; gap: 10px; padding: 0 11px; height: 32px; color: #7f8b84; font-size: 8px; }
.mcp-calls-head { height: 30px; color: #a2aaa4; background: #fafbf9; }
.mcp-calls-row + .mcp-calls-row { border-top: 1px solid #f2f4f1; }

.mcp-snippets { display: flex; flex-direction: column; gap: 10px; }
.mcp-snippet { border: 1px solid #eef1ed; border-radius: 9px; background: #fbfcfa; }
.mcp-snippet-head { display: flex; align-items: center; justify-content: space-between; padding: 7px 11px; border-bottom: 1px solid #f0f2ef; color: #7d8a82; font-size: 8px; }
.mcp-snippet pre { margin: 0; padding: 10px 11px; overflow: auto; color: #4d5a53; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 9px; line-height: 1.7; }

.mcp-detail-foot { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 20px; border-top: 1px solid #f0f2ef; }
.mcp-foot-note { display: inline-flex; align-items: center; gap: 5px; color: #a4ada7; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.mcp-footnote { display: flex; align-items: center; gap: 6px; margin-top: 18px; color: #a8b0aa; font-size: 8px; }

@media (max-width: 1080px) {
  .mcp-stats, .mcp-pane-stats { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .mcp-grid { grid-template-columns: minmax(0, 1fr); }
  .mcp-check-list { grid-template-columns: minmax(0, 1fr); }
}
</style>
