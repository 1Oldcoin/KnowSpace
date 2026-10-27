<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { CalendarCheck, ChevronLeft, ChevronRight, Flame, LoaderCircle, X } from 'lucide-vue-next'
import { getStudyCheckinRange } from '../services/api'
import type { StudyCheckinDay, StudySummary } from '../services/api'

const props = defineProps<{
  study: StudySummary | null
  apiOnline: boolean
}>()
const emit = defineEmits<{
  close: []
}>()

const weekdayLabels = ['一', '二', '三', '四', '五', '六', '日']

function startOfMonth(date: Date) {
  return new Date(date.getFullYear(), date.getMonth(), 1)
}

function formatDateKey(date: Date) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

const todayKey = formatDateKey(new Date())
const cursor = ref(startOfMonth(new Date()))
const records = ref<Record<string, StudyCheckinDay>>({})
const loading = ref(false)
const error = ref('')
const selectedKey = ref('')

/**
 * 每个月单独请求，而不是一次拉全部历史。
 * 一次拉全部在「用了三年」的库上是几万行 JSON，而用户翻月才需要一个月的数据。
 * 请求版本号用于丢弃过期响应：快速连点左右箭头时，先发的慢请求不能覆盖后发的快请求。
 */
let requestVersion = 0

const monthLabel = computed(() => `${cursor.value.getFullYear()} 年 ${cursor.value.getMonth() + 1} 月`)
const isCurrentMonth = computed(() => {
  const now = new Date()
  return cursor.value.getFullYear() === now.getFullYear() && cursor.value.getMonth() === now.getMonth()
})

const cells = computed(() => {
  const first = cursor.value
  const leading = (first.getDay() + 6) % 7 // 周一为一周起始
  const dayCount = new Date(first.getFullYear(), first.getMonth() + 1, 0).getDate()
  const result: Array<{ key: string; day: number; record?: StudyCheckinDay; isToday: boolean; isFuture: boolean } | null> = []
  for (let index = 0; index < leading; index++) result.push(null)
  for (let day = 1; day <= dayCount; day++) {
    const date = new Date(first.getFullYear(), first.getMonth(), day)
    const key = formatDateKey(date)
    result.push({ key, day, record: records.value[key], isToday: key === todayKey, isFuture: key > todayKey })
  }
  return result
})

const checkedCount = computed(() => Object.keys(records.value).length)
const monthMinutes = computed(() => Object.values(records.value).reduce((sum, day) => sum + (day.minutes || 0), 0))
const selectedRecord = computed(() => (selectedKey.value ? records.value[selectedKey.value] : undefined))

async function loadMonth() {
  const version = ++requestVersion
  const first = cursor.value
  const from = formatDateKey(first)
  const to = formatDateKey(new Date(first.getFullYear(), first.getMonth() + 1, 0))

  if (!props.apiOnline) {
    records.value = {}
    error.value = 'API 未连接，暂时读不到打卡记录。'
    loading.value = false
    return
  }

  loading.value = true
  error.value = ''
  try {
    const list = await getStudyCheckinRange(from, to)
    if (version !== requestVersion) return
    const map: Record<string, StudyCheckinDay> = {}
    for (const day of list) map[day.date] = day
    records.value = map
    // 选中态只保留在当月且确实有记录的日期上，翻月后旧选中会变成幽灵高亮。
    if (selectedKey.value && !map[selectedKey.value]) selectedKey.value = ''
  } catch (exception) {
    if (version !== requestVersion) return
    records.value = {}
    error.value = exception instanceof Error ? exception.message : '读取打卡记录失败。'
  } finally {
    if (version === requestVersion) loading.value = false
  }
}

function shiftMonth(delta: number) {
  cursor.value = new Date(cursor.value.getFullYear(), cursor.value.getMonth() + delta, 1)
}

function goToday() {
  cursor.value = startOfMonth(new Date())
}

function pickDay(key: string, record?: StudyCheckinDay) {
  selectedKey.value = record && selectedKey.value !== key ? key : ''
}

function minutesLabel(minutes: number) {
  if (minutes <= 0) return '已打卡'
  if (minutes < 60) return `${minutes} 分钟`
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest ? `${hours} 小时 ${rest} 分` : `${hours} 小时`
}

function prettyDate(key: string) {
  const [year, month, day] = key.split('-').map(Number)
  return `${year} 年 ${month} 月 ${day} 日`
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close')
}

watch(cursor, () => { void loadMonth() })

onMounted(() => {
  document.addEventListener('keydown', onKeydown)
  void loadMonth()
})
onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div class="modal-scrim" @click.self="emit('close')">
      <section class="checkin-calendar-modal" role="dialog" aria-modal="true" aria-label="打卡记录">
        <header class="checkin-modal-head">
          <span class="checkin-modal-icon"><CalendarCheck :size="18" /></span>
          <div>
            <h2>打卡记录</h2>
            <p>连续 {{ study?.currentStreak ?? 0 }} 天 · 累计 {{ study?.totalDays ?? 0 }} 天 · 最长 {{ study?.longestStreak ?? 0 }} 天</p>
          </div>
          <button class="icon-button" aria-label="关闭" @click="emit('close')"><X :size="16" /></button>
        </header>

        <div class="checkin-calendar-bar">
          <button class="icon-button" aria-label="上个月" @click="shiftMonth(-1)"><ChevronLeft :size="15" /></button>
          <strong>{{ monthLabel }}</strong>
          <button class="icon-button" aria-label="下个月" @click="shiftMonth(1)"><ChevronRight :size="15" /></button>
          <button class="checkin-calendar-today" :disabled="isCurrentMonth" @click="goToday">回到本月</button>
        </div>

        <div class="checkin-calendar-weekdays">
          <span v-for="label in weekdayLabels" :key="label">{{ label }}</span>
        </div>

        <div class="checkin-calendar-grid" :class="{ loading }">
          <template v-for="(cell, index) in cells" :key="cell?.key ?? `blank-${index}`">
            <span v-if="!cell" class="checkin-day blank"></span>
            <button
              v-else
              type="button"
              class="checkin-day"
              :class="{ done: !!cell.record, today: cell.isToday, future: cell.isFuture, picked: selectedKey === cell.key }"
              :disabled="!cell.record"
              :title="cell.record ? `${cell.key} · ${minutesLabel(cell.record.minutes)}` : cell.key"
              @click="pickDay(cell.key, cell.record)"
            >
              <span class="checkin-day-number">{{ cell.day }}</span>
              <span v-if="cell.record" class="checkin-day-minutes">{{ cell.record.minutes }}′</span>
              <i v-if="cell.record" class="checkin-day-dot"></i>
            </button>
          </template>
        </div>

        <div v-if="loading" class="checkin-calendar-state"><LoaderCircle :size="14" class="spinning" /> 正在读取打卡记录…</div>
        <div v-else-if="error" class="checkin-calendar-state checkin-calendar-error">{{ error }}</div>
        <div v-else class="checkin-calendar-legend">
          <span><i class="checkin-day-dot"></i> 已打卡</span>
          <span>本月 <strong>{{ checkedCount }}</strong> 天 · 共 <strong>{{ minutesLabel(monthMinutes) }}</strong></span>
          <span class="checkin-calendar-hint"><Flame :size="11" /> 点击有记录的日期查看备注</span>
        </div>

        <div v-if="selectedRecord" class="checkin-calendar-detail">
          <div><strong>{{ prettyDate(selectedRecord.date) }}</strong><span>{{ minutesLabel(selectedRecord.minutes) }}</span></div>
          <p v-if="selectedRecord.note">{{ selectedRecord.note }}</p>
          <p v-else class="checkin-calendar-detail-empty">当天没有写备注。</p>
        </div>

        <footer class="checkin-modal-foot">
          <span>只显示已打卡的日期；空白格表示当天没有记录。</span>
          <div><button class="button button-soft" @click="emit('close')">关闭</button></div>
        </footer>
      </section>
    </div>
  </Teleport>
</template>
