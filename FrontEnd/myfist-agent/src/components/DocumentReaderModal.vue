<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { BookOpenCheck, Check, Copy, FileText, LoaderCircle, Minus, Plus, X } from 'lucide-vue-next'
import { getDocumentContent } from '../services/api'

const props = defineProps<{
  documentId: string
  title: string
  fileName?: string
  apiOnline: boolean
}>()
const emit = defineEmits<{
  close: []
  'save-note': [payload: { title: string; content: string }]
}>()

const text = ref('')
const offset = ref(0)
const totalLength = ref(0)
const chunkCount = ref(0)
const hasMore = ref(false)
const loading = ref(false)
const error = ref('')
const fontSize = ref(13)
const copied = ref(false)

const extension = computed(() => {
  const name = props.fileName ?? ''
  const match = /\.[^.]+$/.exec(name)
  return match ? match[0].slice(1).toUpperCase() : ''
})

/** HTML/HTM 在入库时已抽取为纯文本，这里提示用户看到的是正文而非源码。 */
const isHtml = computed(() => ['HTML', 'HTM'].includes(extension.value))

const progress = computed(() => totalLength.value === 0 ? 0 : Math.min(100, Math.round(offset.value / totalLength.value * 100)))

async function loadPage(reset = false) {
  if (loading.value || !props.apiOnline) return
  loading.value = true
  error.value = ''
  try {
    const page = await getDocumentContent(props.documentId, reset ? 0 : offset.value, 12000)
    text.value = reset ? page.content : text.value + page.content
    offset.value = page.offset + page.content.length
    totalLength.value = page.totalLength
    chunkCount.value = page.chunkCount
    hasMore.value = page.hasMore
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : '读取原文失败。'
  } finally {
    loading.value = false
  }
}

async function copyText() {
  try {
    await navigator.clipboard.writeText(text.value)
    copied.value = true
    window.setTimeout(() => { copied.value = false }, 1800)
  } catch {
    error.value = '复制失败，请手动选择文本。'
  }
}

function saveAsNote() {
  if (!text.value.trim()) return
  emit('save-note', {
    title: `${props.title} · 摘录`,
    content: text.value.slice(0, 5000),
  })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close')
}

onMounted(() => {
  void loadPage(true)
  document.addEventListener('keydown', onKeydown)
})
onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div class="reader-scrim" @click.self="emit('close')">
      <section class="reader-panel" role="dialog" aria-modal="true" aria-label="原文阅读">
        <header class="reader-head">
          <span class="reader-icon"><FileText :size="17" /></span>
          <div class="reader-title">
            <strong>{{ title }}</strong>
            <small>{{ fileName || '原文' }}<template v-if="extension"> · {{ extension }}</template><template v-if="isHtml"> · 已提取 HTML 正文</template><template v-if="chunkCount"> · {{ chunkCount }} 个解析片段</template></small>
          </div>
          <div class="reader-tools">
            <button class="reader-tool" title="缩小字号" aria-label="缩小字号" @click="fontSize = Math.max(11, fontSize - 1)"><Minus :size="14" /></button>
            <span class="reader-font-size">{{ fontSize }}</span>
            <button class="reader-tool" title="放大字号" aria-label="放大字号" @click="fontSize = Math.min(22, fontSize + 1)"><Plus :size="14" /></button>
            <button class="reader-tool" title="复制已加载文本" aria-label="复制文本" @click="copyText"><Check v-if="copied" :size="14" /><Copy v-else :size="14" /></button>
            <button class="reader-tool" title="存为笔记" aria-label="存为笔记" @click="saveAsNote"><BookOpenCheck :size="14" /></button>
            <button class="reader-tool" title="关闭" aria-label="关闭" @click="emit('close')"><X :size="15" /></button>
          </div>
        </header>

        <div class="reader-body">
          <div v-if="!apiOnline" class="reader-state">API 未连接，无法读取原文。</div>
          <div v-else-if="error && !text" class="reader-state reader-state-error">{{ error }}</div>
          <div v-else-if="loading && !text" class="reader-state"><LoaderCircle :size="18" class="spinning" /> 正在读取原文…</div>
          <div v-else-if="!text" class="reader-state">这份资料还没有可阅读的文本内容。</div>
          <pre v-else class="reader-text" :style="{ fontSize: `${fontSize}px` }">{{ text }}</pre>
        </div>

        <footer class="reader-foot">
          <span class="reader-progress">
            <span class="reader-progress-bar"><i :style="{ width: `${progress}%` }"></i></span>
            {{ progress }}% · 已加载 {{ offset }} / {{ totalLength }} 字符
          </span>
          <span v-if="error" class="reader-error">{{ error }}</span>
          <button class="button button-soft" :disabled="!hasMore || loading" @click="loadPage(false)">
            <LoaderCircle v-if="loading" :size="14" class="spinning" />{{ hasMore ? (loading ? '加载中…' : '加载更多') : '已到末尾' }}
          </button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>
