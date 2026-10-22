<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { ArrowRight, BookOpen, Clock3, Feather, LoaderCircle, NotebookPen, Pencil, Pin, PinOff, Plus, Search, Sparkles, Trash2, X } from 'lucide-vue-next'
import type { NoteDraft, NoteItem } from '../types'

const props = defineProps<{
  notes: NoteItem[]
  query: string
  apiOnline: boolean
  busy: boolean
  loading: boolean
  activeKnowledgeBaseId: string
}>()
const emit = defineEmits<{
  create: [draft: NoteDraft]
  update: [noteId: string, draft: NoteDraft]
  delete: [note: NoteItem]
  pin: [note: NoteItem]
  notice: [message: string]
}>()

const activeCategory = ref('全部笔记')
const activeTag = ref('')
const categories = computed(() => ['全部笔记', ...new Set(props.notes.map(note => note.category).filter(Boolean))])
const tags = computed(() => [...new Set(props.notes.flatMap(note => note.tags))].slice(0, 12))

const filteredNotes = computed(() => {
  const needle = props.query.trim().toLowerCase()
  return props.notes.filter((note) => {
    const matchesQuery = !needle
      || `${note.title} ${note.excerpt} ${note.content} ${note.category} ${note.tags.join(' ')}`.toLowerCase().includes(needle)
    const matchesCategory = activeCategory.value === '全部笔记' || note.category === activeCategory.value
    const matchesTag = !activeTag.value || note.tags.includes(activeTag.value)
    return matchesQuery && matchesCategory && matchesTag
  })
})

const isEditorOpen = ref(false)
const editingId = ref('')
const draft = ref<NoteDraft>(emptyDraft())
const titleInput = ref<HTMLInputElement>()
const colors = ['lilac', 'mint', 'peach', 'blue', 'rose', 'sage']
const categorySuggestions = ['灵感随记', '读书摘录', '课程复盘', '资料摘录', '待整理']

function emptyDraft(): NoteDraft {
  return { title: '', content: '', category: '灵感随记', color: 'lilac', tags: [] }
}

const tagInput = ref('')
watch(isEditorOpen, async (open) => {
  if (!open) return
  await nextTick()
  titleInput.value?.focus()
})

function openCreate() {
  editingId.value = ''
  draft.value = emptyDraft()
  tagInput.value = ''
  isEditorOpen.value = true
}

function openEdit(note: NoteItem) {
  editingId.value = note.id
  draft.value = {
    title: note.title,
    content: note.content,
    category: note.category,
    color: note.color,
    tags: [...note.tags],
  }
  tagInput.value = note.tags.join(', ')
  isEditorOpen.value = true
}

function closeEditor() {
  isEditorOpen.value = false
  editingId.value = ''
}

function submit() {
  if (!props.apiOnline || props.busy) return
  const title = draft.value.title.trim()
  if (!title) {
    emit('notice', '请填写笔记标题。')
    return
  }
  const payload: NoteDraft = {
    ...draft.value,
    title,
    content: draft.value.content.replace(/\r\n/g, '\n'),
    category: draft.value.category.trim() || '灵感随记',
    tags: parseTags(tagInput.value),
  }
  if (editingId.value) emit('update', editingId.value, payload)
  else emit('create', payload)
  closeEditor()
}

function parseTags(value: string) {
  return [...new Set(value.split(/[,，\s]+/).map(tag => tag.trim().replace(/^#/, '')).filter(Boolean))].slice(0, 10)
}

function removeTag(tag: string, event: Event) {
  event.stopPropagation()
  activeTag.value = activeTag.value === tag ? '' : tag
}

function exportMarkdown() {
  const title = draft.value.title.trim() || '未命名笔记'
  const tags = parseTags(tagInput.value)
  const lines = [
    `# ${title}`,
    '',
    `- 分类：${draft.value.category || '灵感随记'}`,
    `- 标签：${tags.length ? tags.map(tag => `#${tag}`).join(' ') : '无'}`,
    `- 更新时间：${new Date().toLocaleString('zh-CN')}`,
    '',
    draft.value.content,
  ]
  const blob = new Blob([lines.join('\n')], { type: 'text/markdown;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `${title.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '_').slice(0, 60)}.md`
  document.body.appendChild(link)
  link.click()
  link.remove()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}
</script>

<template>
  <section class="page-heading-row notes-page-heading">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> IDEAS IN PROGRESS</div>
      <h1>学习笔记</h1>
      <p class="welcome-copy">记录那些值得反复回看的瞬间。</p>
    </div>
    <button class="button button-primary" :disabled="!apiOnline || !activeKnowledgeBaseId" @click="openCreate"><Plus :size="17" /> 写一篇笔记</button>
  </section>

  <section class="note-summary-row">
    <div class="note-summary-card">
      <div class="summary-icon lilac"><NotebookPen :size="18" /></div>
      <div><strong>{{ notes.length }} 篇笔记</strong><span>都在慢慢变成你的知识</span></div>
    </div>
    <div class="note-summary-card">
      <div class="summary-icon mint"><Pin :size="18" /></div>
      <div><strong>{{ notes.filter(note => note.isPinned).length }} 篇置顶</strong><span>{{ loading ? '正在读取…' : '置顶笔记会排在最前' }}</span></div>
      <ArrowRight :size="16" class="summary-arrow" />
    </div>
  </section>

  <section class="notes-toolbar">
    <div class="filter-pills">
      <button v-for="category in categories" :key="category" class="filter-pill" :class="{ selected: activeCategory === category }" @click="activeCategory = category">{{ category }}</button>
    </div>
    <button v-if="activeTag" class="topic-filter-chip" @click="activeTag = ''"># {{ activeTag }}<span aria-hidden="true">×</span></button>
  </section>

  <section v-if="tags.length" class="note-tag-cloud">
    <button v-for="tag in tags" :key="tag" class="note-tag-pill" :class="{ selected: activeTag === tag }" @click="activeTag = activeTag === tag ? '' : tag"># {{ tag }}</button>
  </section>

  <section class="notes-masonry">
    <article
      v-for="(note, index) in filteredNotes"
      :key="note.id"
      class="note-card"
      :class="[`note-card-${note.color}`, { 'note-card-tall': index === 1 || index === 4 }]"
      @click="openEdit(note)"
    >
      <div class="note-card-top">
        <span class="note-type"><Feather :size="14" /> {{ note.category }}</span>
        <span class="note-date"><Clock3 :size="12" /> {{ note.date }}</span>
      </div>
      <h2>{{ note.title }}</h2>
      <p>{{ note.excerpt }}</p>
      <div class="note-tags"><span v-for="tag in note.tags" :key="tag" @click.stop="removeTag(tag, $event)"># {{ tag }}</span></div>
      <div class="note-card-footer">
        <span class="note-source"><BookOpen :size="13" /> {{ note.isPinned ? '已置顶' : '来自学习记录' }}</span>
        <span class="note-card-actions" @click.stop>
          <button class="topic-action" :aria-label="note.isPinned ? '取消置顶' : '置顶'" :title="note.isPinned ? '取消置顶' : '置顶'" @click="emit('pin', note)">
            <PinOff v-if="note.isPinned" :size="14" /><Pin v-else :size="14" />
          </button>
          <button class="topic-action" aria-label="编辑" title="编辑" @click="openEdit(note)"><Pencil :size="14" /></button>
          <button class="topic-action topic-action-danger" aria-label="删除" title="删除" :disabled="busy" @click="emit('delete', note)"><Trash2 :size="14" /></button>
        </span>
      </div>
      <Sparkles v-if="note.isPinned" class="note-sparkle" :size="17" />
    </article>
  </section>

  <div v-if="filteredNotes.length === 0" class="empty-state">
    <LoaderCircle v-if="loading" :size="22" class="spinning" />
    <Search v-else :size="22" />
    <strong>{{ loading ? '正在读取笔记…' : notes.length === 0 ? '还没有笔记' : '没有找到相关笔记' }}</strong>
    <span>{{ loading ? '请稍候' : notes.length === 0 ? '点击右上角“写一篇笔记”开始记录。' : '换个关键词或分类试试吧' }}</span>
  </div>

  <div class="library-disclaimer">
    <span class="demo-tag">已持久化</span>笔记保存在本机 PostgreSQL 中，支持分类、标签、置顶与全文检索。
  </div>

  <Teleport to="body">
    <div v-if="isEditorOpen" class="modal-scrim" @click.self="closeEditor">
      <section class="note-editor" role="dialog" aria-modal="true" aria-label="编辑笔记">
        <header class="note-editor-head">
          <span class="settings-icon"><NotebookPen :size="18" /></span>
          <div><h2>{{ editingId ? '编辑笔记' : '写一篇笔记' }}</h2><p>内容会保存到本机知识库，可随时回来补充。</p></div>
          <button class="icon-button" aria-label="关闭" @click="closeEditor"><X :size="16" /></button>
        </header>

        <label class="settings-field">
          <span>标题</span>
          <input ref="titleInput" v-model="draft.title" maxlength="120" placeholder="这一篇你想记住什么？" />
        </label>

        <label class="settings-field">
          <span>正文</span>
          <textarea v-model="draft.content" rows="10" maxlength="50000" placeholder="用自己的语言写下理解、疑问或连接…" />
        </label>

        <div class="note-editor-grid">
          <label class="settings-field">
            <span>分类</span>
            <input v-model="draft.category" list="note-category-options" maxlength="40" placeholder="灵感随记" />
            <datalist id="note-category-options">
              <option v-for="category in categorySuggestions" :key="category" :value="category" />
            </datalist>
          </label>
          <label class="settings-field">
            <span>标签（逗号分隔）</span>
            <input v-model="tagInput" maxlength="200" placeholder="学习方法, 输出" />
          </label>
        </div>

        <div class="note-color-row">
          <span>卡片颜色</span>
          <button v-for="color in colors" :key="color" class="note-color-swatch" :class="[`swatch-${color}`, { selected: draft.color === color }]" :aria-label="color" @click="draft.color = color"></button>
        </div>

        <footer class="note-editor-foot">
          <span>{{ draft.content.length }} / 50000 字符</span>
          <div>
            <button class="button button-soft" @click="exportMarkdown">导出 Markdown</button>
            <button class="button button-soft" @click="closeEditor">取消</button>
            <button class="button button-primary" :disabled="busy || !apiOnline" @click="submit">
              <LoaderCircle v-if="busy" :size="15" class="spinning" />{{ editingId ? '保存修改' : '保存笔记' }}
            </button>
          </div>
        </footer>
      </section>
    </div>
  </Teleport>
</template>
