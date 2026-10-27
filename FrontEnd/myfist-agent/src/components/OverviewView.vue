<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ArrowRight, ArrowUpRight, BookOpen, CalendarCheck, CalendarDays, Check, Clock3, CloudUpload, FileText, Flame, Globe, LoaderCircle, MoreHorizontal, Play, Search, Sparkles, X } from 'lucide-vue-next'
import type { StudySummary } from '../services/api'
import type { MaterialItem, NoteItem } from '../types'
import { SUPPORTED_ACCEPT } from '../fileTypes'
import CheckinCalendarModal from './CheckinCalendarModal.vue'

const props = defineProps<{
  materials: MaterialItem[]
  notes: NoteItem[]
  isApiOnline: boolean
  study: StudySummary | null
  displayName: string
  dailyGoalMinutes: number
}>()
const emit = defineEmits<{
  upload: [files: FileList | null]
  navigate: [view: 'materials' | 'notes' | 'assistant' | 'topics']
  search: []
  checkin: [minutes: number, note?: string]
  read: [item: MaterialItem]
}>()

function onDrop(event: DragEvent) {
  event.preventDefault()
  emit('upload', event.dataTransfer?.files ?? null)
}

const greeting = computed(() => {
  const hour = new Date().getHours()
  if (hour < 6) return '夜深了'
  if (hour < 12) return '早上好'
  if (hour < 18) return '下午好'
  return '晚上好'
})

const weekdayLabels = ['一', '二', '三', '四', '五', '六', '日']
const todayKey = formatDate(new Date())
const week = computed(() => props.study?.week ?? weekdayLabels.map(() => ({ date: '', minutes: 0, note: null })))
const todayWeekIndex = computed(() => week.value.findIndex(day => day.date === todayKey))
const chartBars = computed(() => {
  const minutes = week.value.map(day => day.minutes)
  const max = Math.max(60, ...minutes)
  return minutes.map(value => Math.round(value / max * 100))
})

const showCheckin = ref(false)
const showCalendar = ref(false)
const checkinMinutes = ref(30)
const checkinNote = ref('')

watch(() => props.dailyGoalMinutes, (value) => {
  if (!props.study?.todayCheckedIn) checkinMinutes.value = value > 0 ? Math.min(value, 600) : 30
}, { immediate: true })

watch(() => props.study?.todayMinutes, (value) => {
  if (typeof value === 'number' && value > 0) checkinMinutes.value = value
}, { immediate: true })

function openCheckin() {
  checkinMinutes.value = props.study?.todayMinutes || props.dailyGoalMinutes || 30
  checkinNote.value = ''
  showCheckin.value = true
}

function submitCheckin() {
  emit('checkin', Math.max(0, Math.min(1440, Math.round(checkinMinutes.value || 0))), checkinNote.value.trim() || undefined)
  showCheckin.value = false
}

/**
 * 整张打卡卡可点开日历。键盘等价：Enter / 空格，否则卡片在 Tab 序列里是「不可用控件」。
 * 注意卡片内部还有一个「今日打卡」按钮，它的 click 必须 .stop，否则会连带打开日历。
 */
function openCalendar() {
  showCalendar.value = true
}

function onStudyCardKeydown(event: KeyboardEvent) {
  if (event.key !== 'Enter' && event.key !== ' ') return
  event.preventDefault()
  openCalendar()
}

function formatDate(date: Date) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}
</script>

<template>
  <section class="welcome-row">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> {{ new Intl.DateTimeFormat('zh-CN', { dateStyle: 'full' }).format(new Date()) }}</div>
      <h1>{{ greeting }}，{{ displayName }} <span class="wave">✳</span></h1>
      <p class="welcome-copy">把今天学到的，变成明天的能力。</p>
    </div>
    <div class="welcome-actions">
      <button class="button button-soft" @click="emit('search')"><Search :size="16" /> 搜索知识库</button>
      <button class="button button-primary" @click="emit('navigate', 'assistant')"><Sparkles :size="17" /> 开始探索 <ArrowUpRight :size="15" /></button>
    </div>
  </section>

  <section class="stats-grid">
    <article class="stat-card stat-primary">
      <div class="stat-top"><span class="stat-icon"><BookOpen :size="17" /></span><span class="stat-trend"><ArrowUpRight :size="13" /> 12%</span></div>
      <div class="stat-value">{{ materials.length }}<span> 份</span></div><div class="stat-label">已收录资料</div>
      <div class="stat-sparkline"><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i></div>
    </article>
    <article class="stat-card">
      <div class="stat-top"><span class="stat-icon icon-lilac"><FileText :size="17" /></span><span class="stat-caption">持续积累</span></div>
      <div class="stat-value">{{ notes.length }}<span> 篇</span></div><div class="stat-label">学习笔记</div>
      <div class="stat-progress"><span style="width: 62%"></span></div><div class="stat-foot">本月目标 <strong>10 篇</strong></div>
    </article>
    <article class="stat-card stat-study-card stat-study-card-clickable" role="button" tabindex="0" aria-label="查看打卡记录" @click="openCalendar" @keydown="onStudyCardKeydown">
      <div class="stat-top"><span class="stat-icon icon-peach"><Flame :size="17" /></span><span class="stat-caption">{{ study?.todayCheckedIn ? '今日已打卡' : '保持节奏' }}</span></div>
      <div class="stat-value">{{ study?.currentStreak ?? 0 }}<span> 天</span></div><div class="stat-label">连续学习 · 累计 {{ study?.totalDays ?? 0 }} 天</div>
      <div class="streak-dots">
        <i v-for="(day, index) in week" :key="day.date || index" :class="{ done: day.minutes > 0, today: index === todayWeekIndex }">{{ weekdayLabels[index] }}</i>
      </div>
      <div class="stat-study-foot">
        <button class="checkin-button" :class="{ done: study?.todayCheckedIn }" @click.stop="openCheckin">
          <CalendarCheck :size="12" />
          {{ study?.todayCheckedIn ? `已打卡 · ${study.todayMinutes} 分钟` : '今日打卡' }}
        </button>
        <span class="stat-study-link"><CalendarDays :size="11" /> 查看记录</span>
      </div>
    </article>
  </section>

  <section class="upload-banner" @dragover.prevent @drop="onDrop">
    <div class="upload-orbit orbit-one"></div><div class="upload-orbit orbit-two"></div>
    <div class="upload-banner-content">
      <div class="upload-badge"><CloudUpload :size="20" /></div>
      <div><h2>让新知，找到它的位置</h2><p>拖拽文件到这里，或 <label class="inline-upload">浏览文件<input type="file" multiple :accept="SUPPORTED_ACCEPT" @change="emit('upload', ($event.target as HTMLInputElement).files); ($event.target as HTMLInputElement).value = ''"></label></p></div>
    </div>
    <div class="upload-support"><span>PDF</span><span>DOCX</span><span>TXT / MD</span><span>HTML</span><span>图片</span><span>视频</span><i>视频最大 5 GiB · 其他文件 50 MiB</i></div>
  </section>

  <section class="section-block">
    <div class="section-heading">
      <div><div class="section-kicker">KEEP EXPLORING</div><h2>最近收录 <span class="section-count">{{ materials.length }}</span></h2></div>
      <button class="text-button" @click="emit('navigate', 'materials')">查看全部 <ArrowRight :size="15" /></button>
    </div>
    <div class="material-preview-grid">
      <article v-for="item in materials.slice(0, 3)" :key="item.id" class="material-card" @click="emit('navigate', 'materials')">
        <div class="material-cover" :class="`cover-${item.color}`">
          <div class="cover-top"><span class="file-badge" :class="`file-${item.kind}`"><FileText v-if="item.kind !== 'video' && item.kind !== 'html'" :size="17" /><Globe v-else-if="item.kind === 'html'" :size="16" /><Play v-else :size="15" /></span><button class="tiny-icon-button" aria-label="阅读原文" title="阅读原文" @click.stop="emit('read', item)"><MoreHorizontal :size="17" /></button></div>
          <div class="cover-art"><span class="cover-orbit"></span><span class="cover-glyph">{{ item.kind === 'video' ? '影' : item.kind === 'html' ? '网' : item.category.slice(0, 1) }}</span></div>
          <div class="cover-footer"><span>{{ item.category }}</span><span>{{ item.detail }}</span></div>
        </div>
        <div class="material-card-info"><h3>{{ item.title }}</h3><div class="material-meta"><span><Clock3 :size="13" /> {{ item.updated }}</span><span v-if="item.status === 'ready'" class="status-ready"><Check :size="12" /> 已保存</span><span v-else-if="item.status === 'processing'" class="status-processing"><LoaderCircle :size="12" /> {{ item.processingStage || '排队中' }}<template v-if="item.progress"> · {{ item.progress }}%</template></span><span v-else-if="item.status === 'failed'" class="status-failed">处理失败</span><span v-else class="status-demo">示例资料</span></div><p v-if="item.summary" class="material-summary">{{ item.summary }}</p></div>
      </article>
    </div>
  </section>

  <section class="bottom-insights-grid">
    <article class="insight-card focus-card">
      <div class="insight-heading"><div><div class="section-kicker">YOUR RHYTHM</div><h3>本周学习节奏</h3></div><button class="tiny-icon-button" aria-label="今日打卡" title="今日打卡" @click="openCheckin"><CalendarCheck :size="17" /></button></div>
      <div class="focus-chart"><div class="chart-y-labels"><span>{{ Math.max(60, ...week.map(day => day.minutes)) }}m</span><span>0</span></div><div class="chart-bars"><div v-for="(bar, index) in chartBars" :key="week[index]?.date || index" class="chart-day"><div class="bar-track"><span :style="{ height: `${Math.max(bar, 3)}%` }" :class="{ 'bar-today': index === todayWeekIndex }"></span></div><small>{{ weekdayLabels[index] }}</small></div></div></div>
      <div class="chart-note"><span class="chart-note-dot"></span>本周已学习 <strong>{{ study?.thisWeekMinutes ?? 0 }} 分钟</strong>，每日目标 {{ dailyGoalMinutes }} 分钟</div>
    </article>
    <article class="insight-card note-teaser">
      <div class="insight-heading"><div><div class="section-kicker">QUICK NOTE</div><h3>继续你的思考</h3></div><button class="tiny-icon-button" aria-label="打开笔记" @click="emit('navigate', 'notes')"><ArrowUpRight :size="17" /></button></div>
      <div v-if="notes[0]" class="note-teaser-body"><span class="note-quote-mark">“</span><p>{{ notes[0].excerpt }}</p></div>
      <div class="note-teaser-foot"><span><span class="note-color-dot"></span>{{ notes[0]?.category ?? '灵感随记' }}</span><span>{{ notes[0]?.date ?? '今天' }}</span></div>
    </article>
  </section>

  <div class="api-footnote"><span class="connection-dot" :class="{ online: isApiOnline }"></span>{{ isApiOnline ? '本地知识库服务已连接' : '演示界面 · API 暂未连接' }}<span class="footnote-separator">·</span>你的资料只保存在本机</div>

  <Teleport to="body">
    <div v-if="showCheckin" class="modal-scrim" @click.self="showCheckin = false">
      <section class="checkin-modal" role="dialog" aria-modal="true" aria-label="学习打卡">
        <header class="checkin-modal-head">
          <span class="checkin-modal-icon"><CalendarCheck :size="18" /></span>
          <div><h2>{{ study?.todayCheckedIn ? '更新今日打卡' : '记录今天的学习' }}</h2><p>连续学习 {{ study?.currentStreak ?? 0 }} 天 · 最长 {{ study?.longestStreak ?? 0 }} 天</p></div>
          <button class="icon-button" aria-label="关闭" @click="showCheckin = false"><X :size="16" /></button>
        </header>
        <label class="settings-field"><span>今天学习了多少分钟？</span><input v-model.number="checkinMinutes" type="number" min="0" max="1440" step="5" /></label>
        <label class="settings-field"><span>今天学到了什么？（可选）</span><textarea v-model="checkinNote" rows="3" maxlength="500" placeholder="一句话记录今天的收获…"></textarea></label>
        <footer class="checkin-modal-foot">
          <span>打卡记录只保存在本机。</span>
          <div><button class="button button-soft" @click="showCheckin = false">取消</button><button class="button button-primary" @click="submitCheckin"><Check :size="15" /> 完成打卡</button></div>
        </footer>
      </section>
    </div>
  </Teleport>

  <CheckinCalendarModal v-if="showCalendar" :study="study" :api-online="isApiOnline" @close="showCalendar = false" />
</template>
