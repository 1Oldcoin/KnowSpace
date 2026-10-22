<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { BookOpen, CornerDownLeft, FileText, Search, Sparkles, X } from 'lucide-vue-next'
import type { SemanticSearchResult } from '../services/api'
import type { MaterialItem, NoteItem } from '../types'

const props = defineProps<{
  query: string
  materials: MaterialItem[]
  notes: NoteItem[]
  semanticResults: SemanticSearchResult[]
  semanticLoading: boolean
  apiOnline: boolean
}>()
const emit = defineEmits<{
  'update:query': [value: string]
  close: []
  submit: []
  'open-material': []
  'open-note': []
  'open-document': [documentId: string]
}>()

const inputRef = ref<HTMLInputElement>()
const normalizedQuery = computed(() => props.query.trim().toLowerCase())

const materialResults = computed(() => {
  if (!normalizedQuery.value) return props.materials.slice(0, 3)
  return props.materials.filter(item =>
    `${item.title} ${item.fileName ?? ''} ${item.category} ${item.summary ?? ''}`.toLowerCase().includes(normalizedQuery.value),
  ).slice(0, 5)
})

const noteResults = computed(() => {
  if (!normalizedQuery.value) return props.notes.slice(0, 3)
  return props.notes.filter(note =>
    `${note.title} ${note.excerpt} ${note.category}`.toLowerCase().includes(normalizedQuery.value),
  ).slice(0, 3)
})

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    emit('close')
  } else if (event.key === 'Enter') {
    emit('submit')
  }
}

onMounted(() => {
  inputRef.value?.focus()
  document.addEventListener('keydown', onKeydown)
})
onBeforeUnmount(() => document.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <div class="search-overlay" @click.self="emit('close')">
      <section class="search-panel" role="dialog" aria-modal="true" aria-label="搜索">
        <div class="search-panel-input">
          <Search :size="17" />
          <input
            ref="inputRef"
            :value="query"
            placeholder="搜索资料、笔记或直接检索知识库…"
            @input="emit('update:query', ($event.target as HTMLInputElement).value)"
          />
          <button v-if="query" class="search-clear" aria-label="清空" @click="emit('update:query', '')"><X :size="14" /></button>
          <kbd>Esc</kbd>
        </div>

        <div class="search-panel-body">
          <template v-if="materialResults.length">
            <div class="search-group-title"><FileText :size="13" /> 资料 <span>{{ materialResults.length }}</span></div>
            <button v-for="item in materialResults" :key="item.id" class="search-result" @click="emit('open-document', item.id)">
              <span class="search-result-icon"><FileText :size="15" /></span>
              <span class="search-result-text"><strong>{{ item.title }}</strong><small>{{ item.category }} · {{ item.detail }}</small></span>
              <CornerDownLeft :size="13" />
            </button>
          </template>

          <template v-if="noteResults.length">
            <div class="search-group-title"><BookOpen :size="13" /> 笔记 <span>{{ noteResults.length }}</span></div>
            <button v-for="note in noteResults" :key="note.id" class="search-result" @click="emit('open-note')">
              <span class="search-result-icon note"><BookOpen :size="15" /></span>
              <span class="search-result-text"><strong>{{ note.title }}</strong><small>{{ note.excerpt }}</small></span>
              <CornerDownLeft :size="13" />
            </button>
          </template>

          <div v-if="query.trim().length >= 2" class="search-group-title">
            <Sparkles :size="13" /> 知识库语义检索
            <span v-if="semanticLoading" class="search-loading">检索中…</span>
            <span v-else-if="!apiOnline">服务未连接</span>
            <span v-else>{{ semanticResults.length }}</span>
          </div>
          <template v-if="query.trim().length >= 2 && semanticResults.length">
            <button v-for="(result, index) in semanticResults" :key="`${result.documentId}-${index}`" class="search-result search-result-semantic" @click="emit('open-document', result.documentId)">
              <span class="search-result-icon semantic"><Sparkles :size="15" /></span>
              <span class="search-result-text"><strong>{{ result.fileName }}</strong><small>{{ result.content }}</small></span>
              <span class="search-similarity">{{ Math.round(result.similarity * 100) }}%</span>
            </button>
          </template>

          <div v-if="!materialResults.length && !noteResults.length && !semanticResults.length && !semanticLoading" class="search-empty">
            <Search :size="20" />
            <strong>{{ query ? '没有找到匹配内容' : '输入关键词开始搜索' }}</strong>
            <span>支持资料名称、摘要、笔记文本，以及知识库向量检索。</span>
          </div>
        </div>

        <footer class="search-panel-foot">
          <span><kbd>Enter</kbd> 查看全部资料</span>
          <span><kbd>Esc</kbd> 关闭</span>
          <span class="search-foot-hint">⌘ / Ctrl + K 随时唤起</span>
        </footer>
      </section>
    </div>
  </Teleport>
</template>
