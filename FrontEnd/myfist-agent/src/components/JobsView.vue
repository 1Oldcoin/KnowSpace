<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Activity, Ban, CircleAlert, Clock3, Info, ListChecks, LoaderCircle, RefreshCw, RotateCcw, X } from 'lucide-vue-next'
import { cancelJob, getJob, getJobCounts, getJobQuotas, listJobs, listJobTypes, openJobStream, retryJob } from '../services/api'
import type { JobCounts, JobQuotaSnapshot, JobRecord, JobStatus, JobStreamEvent, JobStreamState } from '../services/api'

const props = defineProps<{ apiOnline: boolean }>()
const emit = defineEmits<{ notice: [message: string]; counts: [pending: number] }>()

// 状态顺序固定：界面上的筛选项与卡片都按这个顺序排，避免每次刷新跳位。
const statusOrder: JobStatus[] = ['Queued', 'Running', 'Succeeded', 'Failed', 'Cancelled']
const statusMeta: Record<JobStatus, { label: string; tone: string }> = {
  Queued: { label: '排队中', tone: 'queued' },
  Running: { label: '运行中', tone: 'running' },
  Succeeded: { label: '已完成', tone: 'succeeded' },
  Failed: { label: '失败', tone: 'failed' },
  Cancelled: { label: '已取消', tone: 'cancelled' },
}

// 已知类型的友好名称；未知类型直接显示原始 type（插件可以注册新类型）。
const typeLabels: Record<string, string> = {
  'document.ingest': '资料解析',
  'plugin.sync': '插件同步',
}

const jobs = ref<JobRecord[]>([])
const counts = ref<JobCounts | null>(null)
const jobTypes = ref<string[]>([])
const isLoading = ref(false)
const loadError = ref('')
const busyJobId = ref('')
const statusFilter = ref<'' | JobStatus>('')
const typeFilter = ref('')
const detailJob = ref<JobRecord | null>(null)
const detailLoading = ref(false)
const streamState = ref<JobStreamState>('connecting')
const quotaSnapshot = ref<JobQuotaSnapshot | null>(null)

let closeStream: (() => void) | null = null
let pollTimer: ReturnType<typeof setInterval> | undefined
let countTimer: ReturnType<typeof setTimeout> | undefined
let listTimer: ReturnType<typeof setTimeout> | undefined

const visibleJobs = computed(() => jobs.value)
const totalCount = computed(() => {
  const value = counts.value
  if (!value) return jobs.value.length
  return value.queued + value.running + value.succeeded + value.failed + value.cancelled
})
const liveLabel = computed(() => streamState.value === 'live' ? '实时' : streamState.value === 'connecting' ? '连接中' : '轮询中')
const oldestQueuedText = computed(() => {
  const oldest = counts.value?.oldestQueuedAt
  return oldest ? `最早的任务已等待 ${formatRelative(oldest)}` : ''
})
// 只展示「有单独限流」的类型：没配额的类型就等于不限，写出来只是噪声。
const quotaText = computed(() => {
  const limited = quotaSnapshot.value?.quotas.filter(item => item.limit !== null) ?? []
  if (limited.length === 0) return ''
  return limited.map(item => `${typeLabel(item.type)} ${item.running}/${item.limit}`).join(' · ')
})

function isTerminal(status: JobStatus) {
  return status === 'Succeeded' || status === 'Failed' || status === 'Cancelled'
}

function countFor(status: JobStatus) {
  const value = counts.value
  if (!value) return 0
  switch (status) {
    case 'Queued': return value.queued
    case 'Running': return value.running
    case 'Succeeded': return value.succeeded
    case 'Failed': return value.failed
    case 'Cancelled': return value.cancelled
  }
}

function typeLabel(type: string) {
  return typeLabels[type] ?? type
}

function statusLabel(status: JobStatus) {
  return statusMeta[status]?.label ?? status
}

function statusTone(status: JobStatus) {
  return statusMeta[status]?.tone ?? 'queued'
}

function formatRelative(value?: string | null) {
  if (!value) return '—'
  const time = new Date(value).getTime()
  if (Number.isNaN(time)) return '—'

  const seconds = Math.round((Date.now() - time) / 1000)
  if (seconds < 0) return '刚刚'
  if (seconds < 60) return '刚刚'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟前`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时前`
  return `${Math.floor(seconds / 86400)} 天前`
}

function formatClock(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

function formatDuration(job: JobRecord) {
  if (!job.startedAt) return '—'
  const start = new Date(job.startedAt).getTime()
  const end = job.completedAt ? new Date(job.completedAt).getTime() : Date.now()
  if (Number.isNaN(start) || Number.isNaN(end) || end < start) return '—'
  const seconds = Math.round((end - start) / 1000)
  if (seconds < 60) return `${seconds} 秒`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`
  return `${Math.floor(seconds / 3600)} 小时 ${Math.floor((seconds % 3600) / 60)} 分`
}

function prettyJson(raw?: string | null) {
  if (!raw) return ''
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

function jobSummary(job: JobRecord) {
  // 第一行只放最有信息量的一句：失败原因 > 进度文案 > 排队/完成提示。
  if (job.lastError) return job.lastError
  if (job.progressMessage) return job.progressMessage
  if (job.status === 'Queued') return job.attempts > 0 ? `等待第 ${job.attempts + 1} 次尝试` : '等待执行'
  if (job.status === 'Running') return '正在执行'
  return job.result ? '已完成' : '未产生结果'
}

async function refresh(options: { silent?: boolean } = {}) {
  if (!props.apiOnline) return
  if (!options.silent) isLoading.value = true
  loadError.value = ''

  try {
    const [list, nextCounts] = await Promise.all([
      listJobs({ status: statusFilter.value || undefined, type: typeFilter.value || undefined, limit: 100 }),
      getJobCounts(),
    ])
    jobs.value = list
    counts.value = nextCounts
    void loadQuotas()
  } catch (error) {
    loadError.value = error instanceof Error ? error.message : '无法读取任务列表。'
  } finally {
    isLoading.value = false
  }
}

async function loadTypes() {
  if (!props.apiOnline) return
  try {
    jobTypes.value = await listJobTypes()
  } catch {
    // 类型下拉是锦上添花：拿不到就不显示，不打扰用户。
    jobTypes.value = []
  }
}

async function loadQuotas() {
  if (!props.apiOnline) return
  try {
    quotaSnapshot.value = await getJobQuotas()
  } catch {
    // 老版本后端没有 /api/jobs/quotas：静默降级为不显示配额。
    quotaSnapshot.value = null
  }
}

function scheduleCountRefresh() {
  if (countTimer) clearTimeout(countTimer)
  countTimer = setTimeout(() => { void refresh({ silent: true }) }, 500)
}

function scheduleListRefresh() {
  if (listTimer) clearTimeout(listTimer)
  listTimer = setTimeout(() => { void refresh({ silent: true }) }, 300)
}

function matchesFilter(job: Pick<JobRecord, 'status' | 'type'>) {
  if (statusFilter.value && job.status !== statusFilter.value) return false
  if (typeFilter.value && job.type !== typeFilter.value) return false
  return true
}

function applyStreamEvent(event: JobStreamEvent) {
  const index = jobs.value.findIndex(job => job.id === event.jobId)

  if (index < 0) {
    // 新任务（或当前筛选隐藏了它）：拉一次列表，别在本地猜测完整字段。
    if (matchesFilter(event)) scheduleListRefresh()
    scheduleCountRefresh()
    return
  }

  const current = jobs.value[index]
  if (!current) return

  const patched: JobRecord = {
    ...current,
    status: event.status,
    progress: event.progress,
    progressMessage: event.message ?? current.progressMessage,
    lastError: event.status === 'Succeeded' ? null : (event.error ?? current.lastError),
    updatedAt: event.occurredAt,
  }

  if (!matchesFilter(patched)) {
    jobs.value = jobs.value.filter(job => job.id !== event.jobId)
    scheduleCountRefresh()
    return
  }

  jobs.value = jobs.value.map(job => job.id === event.jobId ? patched : job)

  // 终态字段（completedAt/result/attempts）只有库里有权威值，到达终态时补拉一次。
  if (isTerminal(event.status)) {
    void getJob(event.jobId)
      .then(job => {
        if (matchesFilter(job)) jobs.value = jobs.value.map(item => item.id === job.id ? job : item)
        if (detailJob.value?.id === job.id) detailJob.value = job
      })
      .catch(() => { /* 下一帧快照会自愈 */ })
    scheduleCountRefresh()
  }
}

function connectStream() {
  closeStream?.()
  closeStream = openJobStream({
    onStateChange: (state) => {
      streamState.value = state
      ensurePolling(state !== 'live')
    },
    onSnapshot: (snapshot) => {
      streamState.value = 'live'
      counts.value = snapshot.counts
      // 快照只含最新 20 条且不含筛选条件：有筛选时不要覆盖列表。
      if (!statusFilter.value && !typeFilter.value) jobs.value = snapshot.jobs
    },
    onJobEvent: applyStreamEvent,
  })
}

function ensurePolling(shouldPoll: boolean) {
  if (shouldPoll) {
    if (pollTimer) return
    // SSE 不可用时退回轮询：界面仍然能动，只是慢一点。
    pollTimer = setInterval(() => { void refresh({ silent: true }) }, 5000)
    return
  }
  if (pollTimer) {
    clearInterval(pollTimer)
    pollTimer = undefined
  }
}

async function cancel(job: JobRecord) {
  busyJobId.value = job.id
  try {
    const updated = await cancelJob(job.id)
    jobs.value = jobs.value.map(item => item.id === updated.id ? updated : item)
    if (detailJob.value?.id === updated.id) detailJob.value = updated
    emit('notice', job.status === 'Running' ? '已请求取消，任务会在下一次心跳（≤5 秒）内停下。' : '任务已取消。')
    scheduleCountRefresh()
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '取消失败。')
  } finally {
    busyJobId.value = ''
  }
}

async function retry(job: JobRecord) {
  busyJobId.value = job.id
  try {
    const updated = await retryJob(job.id)
    jobs.value = jobs.value.map(item => item.id === updated.id ? updated : item)
    if (detailJob.value?.id === updated.id) detailJob.value = updated
    emit('notice', '已重新排队。')
    scheduleCountRefresh()
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '重跑失败。')
  } finally {
    busyJobId.value = ''
  }
}

async function openDetail(job: JobRecord) {
  detailJob.value = job
  detailLoading.value = true
  try {
    detailJob.value = await getJob(job.id)
  } catch (error) {
    emit('notice', error instanceof Error ? error.message : '无法读取任务详情。')
  } finally {
    detailLoading.value = false
  }
}

function resetFilters() {
  statusFilter.value = ''
  typeFilter.value = ''
}

watch([statusFilter, typeFilter], () => { void refresh() })

// 侧栏角标：在这一页时用真实计数（SSE 驱动），回到其它页再靠 App 的 15 秒轮询。
watch(counts, (value) => {
  emit('counts', value ? value.queued + value.running : 0)
}, { immediate: true })

watch(() => props.apiOnline, (online) => {
  if (online) {
    void refresh()
    void loadTypes()
    connectStream()
    return
  }
  closeStream?.()
  closeStream = null
  ensurePolling(false)
  streamState.value = 'offline'
})

onMounted(() => {
  void refresh()
  void loadTypes()
  connectStream()
})

onBeforeUnmount(() => {
  closeStream?.()
  closeStream = null
  ensurePolling(false)
  if (countTimer) clearTimeout(countTimer)
  if (listTimer) clearTimeout(listTimer)
})
</script>

<template>
  <section class="page-heading-row">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> TASK CENTER</div>
      <h1>任务中心</h1>
      <p class="welcome-copy">长任务都在这里排队、执行、重试。状态以数据库为准，重启不会丢。</p>
    </div>
    <div class="jobs-heading-actions">
      <span class="jobs-live" :class="`jobs-live-${streamState}`"><i></i>{{ liveLabel }}</span>
      <button class="button button-soft" :disabled="!apiOnline || isLoading" @click="refresh()">
        <RefreshCw :size="14" :class="{ spinning: isLoading }" /> 刷新
      </button>
    </div>
  </section>

  <section class="jobs-summary-row">
    <article class="jobs-summary-card">
      <div class="summary-icon lilac"><Clock3 :size="17" /></div>
      <div>
        <strong>{{ counts?.queued ?? 0 }}</strong>
        <span>排队中</span>
      </div>
    </article>
    <article class="jobs-summary-card">
      <div class="summary-icon mint"><Activity :size="17" /></div>
      <div>
        <strong>{{ counts?.running ?? 0 }}</strong>
        <span>运行中</span>
      </div>
    </article>
    <article class="jobs-summary-card">
      <div class="summary-icon rose"><CircleAlert :size="17" /></div>
      <div>
        <strong>{{ counts?.failed ?? 0 }}</strong>
        <span>失败（保留期内）</span>
      </div>
    </article>
    <article class="jobs-summary-card">
      <div class="summary-icon sage"><ListChecks :size="17" /></div>
      <div>
        <strong>{{ counts?.succeeded ?? 0 }}</strong>
        <span>已完成（保留期内）</span>
      </div>
    </article>
  </section>

  <div class="jobs-toolbar">
    <div class="filter-pills">
      <button class="filter-pill" :class="{ selected: statusFilter === '' }" @click="statusFilter = ''">
        全部<span>{{ totalCount }}</span>
      </button>
      <button
        v-for="status in statusOrder"
        :key="status"
        class="filter-pill"
        :class="{ selected: statusFilter === status }"
        @click="statusFilter = status"
      >
        {{ statusLabel(status) }}<span>{{ countFor(status) }}</span>
      </button>
    </div>
    <div class="jobs-toolbar-right">
      <label v-if="jobTypes.length > 0" class="jobs-type-select">
        <span>类型</span>
        <select v-model="typeFilter">
          <option value="">全部类型</option>
          <option v-for="type in jobTypes" :key="type" :value="type">{{ typeLabel(type) }}（{{ type }}）</option>
        </select>
      </label>
      <button v-if="statusFilter || typeFilter" class="text-button" @click="resetFilters">清除筛选</button>
    </div>
  </div>

  <p v-if="oldestQueuedText" class="jobs-hint"><Info :size="13" />{{ oldestQueuedText }}</p>
  <p v-if="quotaText" class="jobs-hint"><Info :size="13" />按类型限流：{{ quotaText }}（当前占用/上限）</p>

  <div v-if="!apiOnline" class="empty-state">
    <strong>后端未连接</strong>
    <span>启动 API 后任务列表会自动出现。</span>
  </div>

  <div v-else-if="loadError" class="empty-state">
    <strong>读取任务失败</strong>
    <span>{{ loadError }}</span>
    <button class="button button-soft" @click="refresh()">重试</button>
  </div>

  <div v-else-if="isLoading && jobs.length === 0" class="empty-state">
    <LoaderCircle :size="20" class="spinning" />
    <span>正在读取任务…</span>
  </div>

  <div v-else-if="visibleJobs.length === 0" class="empty-state">
    <strong>{{ statusFilter || typeFilter ? '没有符合条件的任务' : '还没有任务' }}</strong>
    <span>{{ statusFilter || typeFilter ? '换个筛选条件试试。' : '上传资料或触发插件同步后，这里会出现执行记录。' }}</span>
  </div>

  <div v-else class="jobs-table">
    <div class="jobs-table-head">
      <span>任务</span>
      <span>状态</span>
      <span>进度</span>
      <span>尝试</span>
      <span>时间</span>
      <span></span>
    </div>
    <div
      v-for="job in visibleJobs"
      :key="job.id"
      class="jobs-row"
      :class="{ 'jobs-row-terminal': isTerminal(job.status) }"
      @click="openDetail(job)"
    >
      <div class="jobs-cell-main">
        <strong>{{ typeLabel(job.type) }}</strong>
        <small :title="job.id">{{ job.subjectId ? `对象 ${job.subjectId.slice(0, 8)}` : job.id.slice(0, 8) }}</small>
        <p class="jobs-summary">{{ jobSummary(job) }}</p>
      </div>

      <div class="jobs-cell-status">
        <span class="jobs-badge" :class="`jobs-badge-${statusTone(job.status)}`">
          <LoaderCircle v-if="job.status === 'Running'" :size="11" class="spinning" />
          {{ statusLabel(job.status) }}
        </span>
        <small v-if="job.cancelRequested && job.status === 'Running'" class="jobs-cancel-pending">取消中…</small>
      </div>

      <div class="jobs-cell-progress">
        <div class="jobs-progress-track">
          <span :class="`jobs-progress-fill-${statusTone(job.status)}`" :style="{ width: `${job.status === 'Succeeded' ? 100 : Math.min(100, Math.max(0, job.progress))}%` }"></span>
        </div>
        <small>{{ job.status === 'Succeeded' ? 100 : Math.min(100, Math.max(0, job.progress)) }}%</small>
      </div>

      <div class="jobs-cell-attempts">
        <span>{{ job.attempts }}<i>/{{ job.maxAttempts }}</i></span>
        <small>{{ job.workerId ? `槽位 ${job.workerId.split('_').pop()}` : '—' }}</small>
      </div>

      <div class="jobs-cell-time">
        <span :title="formatClock(job.createdAt)">{{ formatRelative(job.createdAt) }}</span>
        <small>{{ formatDuration(job) }}</small>
      </div>

      <div class="jobs-cell-actions" @click.stop>
        <button
          v-if="!isTerminal(job.status)"
          class="jobs-action"
          :disabled="busyJobId === job.id || job.cancelRequested"
          title="取消任务"
          @click="cancel(job)"
        >
          <LoaderCircle v-if="busyJobId === job.id" :size="13" class="spinning" />
          <Ban v-else :size="13" />
        </button>
        <button
          v-else
          class="jobs-action"
          :disabled="busyJobId === job.id"
          title="重跑任务"
          @click="retry(job)"
        >
          <LoaderCircle v-if="busyJobId === job.id" :size="13" class="spinning" />
          <RotateCcw v-else :size="13" />
        </button>
      </div>
    </div>
  </div>

  <div class="library-disclaimer">
    <span class="demo-tag">实时</span>进度与状态由 SSE 推送；连接断开时自动退回 5 秒轮询。取消运行中的任务最多需要 5 秒生效。
  </div>

  <Teleport to="body">
    <div v-if="detailJob" class="modal-scrim" @click.self="detailJob = null">
      <section class="jobs-detail" role="dialog" aria-modal="true" aria-label="任务详情">
        <header class="jobs-detail-head">
          <div>
            <div class="eyebrow"><span class="eyebrow-line"></span> JOB DETAIL</div>
            <h2>{{ typeLabel(detailJob.type) }}<span class="jobs-badge" :class="`jobs-badge-${statusTone(detailJob.status)}`">{{ statusLabel(detailJob.status) }}</span></h2>
            <p class="jobs-detail-id">{{ detailJob.id }}</p>
          </div>
          <button class="icon-button" aria-label="关闭" @click="detailJob = null"><X :size="16" /></button>
        </header>

        <div class="jobs-detail-stats">
          <div><span>进度</span><strong>{{ detailJob.progress }}%</strong></div>
          <div><span>尝试</span><strong>{{ detailJob.attempts }} / {{ detailJob.maxAttempts }}</strong></div>
          <div><span>耗时</span><strong>{{ formatDuration(detailJob) }}</strong></div>
          <div><span>优先级</span><strong>{{ detailJob.priority }}</strong></div>
        </div>

        <dl class="jobs-detail-meta">
          <div><dt>创建</dt><dd>{{ formatClock(detailJob.createdAt) }}</dd></div>
          <div><dt>开始</dt><dd>{{ formatClock(detailJob.startedAt) }}</dd></div>
          <div><dt>结束</dt><dd>{{ formatClock(detailJob.completedAt) }}</dd></div>
          <div><dt>下次运行</dt><dd>{{ formatClock(detailJob.nextRunAt) }}</dd></div>
          <div><dt>执行槽位</dt><dd>{{ detailJob.workerId || '—' }}</dd></div>
          <div><dt>幂等键</dt><dd class="jobs-mono">{{ detailJob.idempotencyKey || '—' }}</dd></div>
          <div><dt>业务对象</dt><dd class="jobs-mono">{{ detailJob.subjectId || '—' }}</dd></div>
          <div><dt>知识库</dt><dd class="jobs-mono">{{ detailJob.knowledgeBaseId || '—' }}</dd></div>
        </dl>

        <p v-if="detailJob.progressMessage" class="jobs-detail-line">{{ detailJob.progressMessage }}</p>
        <p v-if="detailJob.lastError" class="jobs-detail-error">{{ detailJob.lastError }}</p>

        <label v-if="detailJob.payload" class="jobs-detail-block">
          <span>任务参数</span>
          <pre>{{ prettyJson(detailJob.payload) }}</pre>
        </label>
        <label v-if="detailJob.result" class="jobs-detail-block">
          <span>执行结果</span>
          <pre>{{ prettyJson(detailJob.result) }}</pre>
        </label>

        <footer class="jobs-detail-foot">
          <span>{{ detailLoading ? '正在读取最新状态…' : '状态以数据库为准' }}</span>
          <div>
            <button class="button button-soft" @click="detailJob = null">关闭</button>
            <button
              v-if="!isTerminal(detailJob.status)"
              class="button button-soft"
              :disabled="busyJobId === detailJob.id || detailJob.cancelRequested"
              @click="cancel(detailJob)"
            ><Ban :size="13" /> 取消任务</button>
            <button
              v-else
              class="button button-primary"
              :disabled="busyJobId === detailJob.id"
              @click="retry(detailJob)"
            ><RotateCcw :size="13" /> 重跑</button>
          </div>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.jobs-heading-actions { display: flex; align-items: center; gap: 9px; margin-bottom: 3px; }
.jobs-live { display: inline-flex; align-items: center; gap: 6px; padding: 6px 9px; border: 1px solid #e8ede8; border-radius: 7px; color: #89958c; background: #fff; font-size: 9px; }
.jobs-live i { width: 6px; height: 6px; border-radius: 50%; background: #c9d2ca; }
.jobs-live-live i { background: #70ad81; box-shadow: 0 0 0 3px #70ad811b; }
.jobs-live-connecting i { background: #d7a177; }
.jobs-live-offline i { background: #c9a6a0; }

.jobs-summary-row { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 12px; margin: 22px 0 20px; }
.jobs-summary-card { min-height: 66px; display: flex; align-items: center; gap: 11px; padding: 11px 14px; border: 1px solid #e9ede8; border-radius: 10px; background: #fff; }
.jobs-summary-card > div:last-child { display: flex; flex-direction: column; gap: 3px; }
.jobs-summary-card strong { color: #3b4a41; font-size: 18px; line-height: 1.1; letter-spacing: -.4px; }
.jobs-summary-card span { color: #a2aaa4; font-size: 8px; }
.summary-icon.rose { color: #b3776f; background: #fbeceb; }
.summary-icon.sage { color: #5c8668; background: #eef4ec; }

.jobs-toolbar { display: flex; align-items: center; justify-content: space-between; gap: 14px; padding-bottom: 13px; border-bottom: 1px solid #e8ece7; }
.jobs-toolbar-right { display: flex; align-items: center; gap: 11px; }
.jobs-type-select { display: inline-flex; align-items: center; gap: 6px; color: #98a29b; font-size: 9px; }
.jobs-type-select select { max-width: 190px; padding: 5px 7px; border: 1px solid #e6ebe6; border-radius: 7px; color: #5f6d64; background: #fff; font-size: 9px; }
.jobs-hint { display: flex; align-items: center; gap: 6px; margin: 12px 0 0; color: #a09577; font-size: 9px; }

.jobs-table { margin-top: 14px; overflow: hidden; border: 1px solid #e9ede8; border-radius: 10px; background: #fff; }
.jobs-table-head, .jobs-row { display: grid; grid-template-columns: minmax(200px, 2.2fr) minmax(90px, .8fr) minmax(110px, 1fr) minmax(70px, .6fr) minmax(78px, .65fr) 34px; align-items: center; gap: 12px; padding: 0 14px; }
.jobs-table-head { height: 34px; color: #a2aaa4; background: #fafbf9; font-size: 8px; }
.jobs-row { min-height: 62px; border-top: 1px solid #f0f2ef; cursor: pointer; transition: background .16s; }
.jobs-row:hover { background: #fcfdfb; }
.jobs-row-terminal { opacity: .92; }

.jobs-cell-main { min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.jobs-cell-main strong { color: #4a574f; font-size: 10px; font-weight: 600; }
.jobs-cell-main small { color: #aab1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.jobs-summary { margin: 3px 0 0; overflow: hidden; color: #85918a; font-size: 8px; line-height: 1.5; text-overflow: ellipsis; white-space: nowrap; }

.jobs-cell-status { display: flex; flex-direction: column; gap: 3px; align-items: flex-start; }
.jobs-badge { display: inline-flex; align-items: center; gap: 4px; padding: 3px 7px; border-radius: 5px; font-size: 8px; font-weight: 600; white-space: nowrap; }
.jobs-badge-queued { color: #94795a; background: #f6efe4; }
.jobs-badge-running { color: #3f7b5f; background: #e9f3ea; }
.jobs-badge-succeeded { color: #4f7f68; background: #edf5ee; }
.jobs-badge-failed { color: #b07068; background: #fbeceb; }
.jobs-badge-cancelled { color: #8b918c; background: #f1f3f1; }
.jobs-cancel-pending { color: #b08a5e; font-size: 7px; }

.jobs-cell-progress { display: flex; align-items: center; gap: 7px; }
.jobs-progress-track { flex: 1; height: 5px; overflow: hidden; border-radius: 6px; background: #eff1ed; }
.jobs-progress-track span { display: block; height: 100%; border-radius: inherit; transition: width .3s ease; }
.jobs-progress-fill-queued { background: #e0c9a4; }
.jobs-progress-fill-running { background: #7fbb93; }
.jobs-progress-fill-succeeded { background: #86b79a; }
.jobs-progress-fill-failed { background: #d3a19a; }
.jobs-progress-fill-cancelled { background: #cbcfcb; }
.jobs-cell-progress small { color: #9aa59e; font-size: 8px; }

.jobs-cell-attempts, .jobs-cell-time { display: flex; flex-direction: column; gap: 3px; }
.jobs-cell-attempts span { color: #6f7d75; font-size: 9px; }
.jobs-cell-attempts i { color: #b0b8b2; font-style: normal; }
.jobs-cell-attempts small, .jobs-cell-time small { color: #aab1ab; font-size: 8px; }
.jobs-cell-time span { color: #6f7d75; font-size: 9px; }

.jobs-cell-actions { display: flex; justify-content: flex-end; }
.jobs-action { width: 26px; height: 26px; display: grid; place-items: center; border: 1px solid #e8ece7; border-radius: 7px; color: #7d8a82; background: #fff; }
.jobs-action:hover:not(:disabled) { border-color: #d3e0d6; color: #3f7b5f; background: #f8fbf8; }
.jobs-action:disabled { cursor: not-allowed; opacity: .5; }

.jobs-detail { width: min(720px, calc(100vw - 40px)); max-height: calc(100vh - 60px); display: flex; flex-direction: column; gap: 14px; overflow-y: auto; padding: 20px 22px 16px; border: 1px solid #e6ece6; border-radius: 14px; background: #fff; box-shadow: 0 24px 60px #1d2f2424; }
.jobs-detail-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 14px; }
.jobs-detail-head h2 { display: flex; align-items: center; gap: 8px; margin: 0; color: #33423a; font-size: 17px; font-weight: 600; letter-spacing: -.4px; }
.jobs-detail-id { margin: 6px 0 0; color: #a9b1ab; font-size: 8px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.jobs-detail-stats { display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 9px; }
.jobs-detail-stats > div { display: flex; flex-direction: column; gap: 4px; padding: 9px 11px; border: 1px solid #edf0ec; border-radius: 9px; background: #fafbf9; }
.jobs-detail-stats span { color: #a2aaa4; font-size: 8px; }
.jobs-detail-stats strong { color: #46554b; font-size: 12px; font-weight: 600; }

.jobs-detail-meta { display: grid; grid-template-columns: 1fr 1fr; gap: 6px 16px; margin: 0; }
.jobs-detail-meta > div { min-width: 0; display: flex; gap: 8px; padding: 3px 0; border-bottom: 1px dashed #f1f3f0; }
.jobs-detail-meta dt { flex: 0 0 62px; color: #a2aaa4; font-size: 8px; }
.jobs-detail-meta dd { min-width: 0; margin: 0; overflow: hidden; color: #63706a; font-size: 8px; text-overflow: ellipsis; white-space: nowrap; }
.jobs-mono { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }

.jobs-detail-line { margin: 0; color: #7c8a82; font-size: 9px; }
.jobs-detail-error { margin: 0; padding: 9px 11px; border: 1px solid #f0dcd9; border-radius: 8px; color: #a8635a; background: #fdf6f5; font-size: 9px; line-height: 1.6; }
.jobs-detail-block { display: flex; flex-direction: column; gap: 5px; }
.jobs-detail-block > span { color: #88958b; font-size: 8px; font-weight: 600; }
.jobs-detail-block pre { max-height: 190px; margin: 0; overflow: auto; padding: 9px 11px; border: 1px solid #edf0ec; border-radius: 8px; color: #5b685f; background: #fafbf9; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 8px; line-height: 1.65; white-space: pre-wrap; word-break: break-all; }
.jobs-detail-foot { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding-top: 12px; border-top: 1px solid #f0f2ef; }
.jobs-detail-foot > span { color: #a9b1ab; font-size: 8px; }
.jobs-detail-foot > div { display: flex; gap: 7px; }

@media (max-width: 1180px) {
  .jobs-summary-row { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
@media (max-width: 900px) {
  .jobs-table { overflow-x: auto; }
  .jobs-table-head, .jobs-row { min-width: 720px; }
  .jobs-detail-meta { grid-template-columns: 1fr; }
}
</style>
