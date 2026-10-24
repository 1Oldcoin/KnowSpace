<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { ArrowRight, BookOpen, Clock3, Download, Feather, LoaderCircle, NotebookPen, Pencil, Pin, PinOff, Plus, Search, Sparkles, Trash2, X } from 'lucide-vue-next'
import type { NoteDraft, NoteItem, NoteSourceItem } from '../types'
import type { Topic } from '../services/api'

const props = defineProps<{
  notes: NoteItem[]
  query: string
  apiOnline: boolean
  busy: boolean
  loading: boolean
  activeKnowledgeBaseId: string
  /** 用于笔记编辑器里的专题选择，以及卡片上的专题名展示。 */
  topics?: Topic[]
}>()
const emit = defineEmits<{
  create: [draft: NoteDraft]
  update: [noteId: string, draft: NoteDraft]
  delete: [note: NoteItem]
  pin: [note: NoteItem]
  exportNotes: [noteIds: string[]]
  readSource: [documentId: string, fileName: string]
  notice: [message: string]
}>()

const activeCategory = ref('全部笔记')
const activeTag = ref('')
const availableTopics = computed(() => props.topics ?? [])
const categories = computed(() => ['全部笔记', ...new Set(props.notes.map(note => note.category).filter(Boolean))])
const tags = computed(() => [...new Set(props.notes.flatMap(note => note.tags))].slice(0, 12))

function topicName(topicId?: string | null) {
  if (!topicId) return ''
  return availableTopics.value.find(topic => topic.id === topicId)?.name ?? ''
}

function sourceLocation(source: NoteSourceItem) {
  const parts = [source.fileName]
  if (typeof source.chunkIndex === 'number' && source.chunkIndex >= 0) parts.push(`第 ${source.chunkIndex + 1} 段`)
  return parts.join(' · ')
}

/** 引用片段在卡片上最多显示前两条，其余用“+N”合并——卡片是索引，不是正文。 */
function visibleSources(note: NoteItem) {
  return (note.sources ?? []).slice(0, 2)
}

function extraSourceCount(note: NoteItem) {
  return Math.max(0, (note.sources?.length ?? 0) - 2)
}

/** 卡片上的小提示：正文里引用了多少条资料。0 表示没写锚点，不显示。 */
function noteAnchoredCount(note: NoteItem) {
  if (!note.content) return 0
  const found = new Set<number>()
  for (const segment of segmentContent(note.content, note.sources?.length ?? 0)) {
    for (const index of segment.indices) found.add(index)
  }
  return found.size
}

/** 只导出当前筛选结果，避免“看到三篇却导出全部”。 */
function exportVisible() {
  if (!filteredNotes.value.length) {
    emit('notice', '当前没有可导出的笔记。')
    return
  }
  emit('exportNotes', filteredNotes.value.map(note => note.id))
}

const filteredNotes = computed(() => {
  const needle = props.query.trim().toLowerCase()
  return props.notes.filter((note) => {
    const sourceText = (note.sources ?? []).map(source => source.fileName).join(' ')
    const matchesQuery = !needle
      || `${note.title} ${note.excerpt} ${note.content} ${note.category} ${note.tags.join(' ')} ${sourceText}`.toLowerCase().includes(needle)
    const matchesCategory = activeCategory.value === '全部笔记' || note.category === activeCategory.value
    const matchesTag = !activeTag.value || note.tags.includes(activeTag.value)
    return matchesQuery && matchesCategory && matchesTag
  })
})

const isEditorOpen = ref(false)
const editingId = ref('')
const draft = ref<NoteDraft>(emptyDraft())
/** 正在编辑的这篇笔记的引用列表：锚点 [n] 的序号就是它的下标 +1。 */
const editingSources = ref<NoteSourceItem[]>([])
const titleInput = ref<HTMLInputElement>()
const contentInput = ref<HTMLTextAreaElement | null>(null)
const colors = ['lilac', 'mint', 'peach', 'blue', 'rose', 'sage']
const categorySuggestions = ['灵感随记', '读书摘录', '课程复盘', '资料摘录', '待整理']

function emptyDraft(): NoteDraft {
  return { title: '', content: '', category: '灵感随记', color: 'lilac', tags: [], topicId: null }
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
  editingSources.value = []
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
    topicId: note.topicId ?? null,
  }
  editingSources.value = [...(note.sources ?? [])]
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

// ---- 正文引用锚点（T-5）----
// 语法与后端 NoteCitationAnchor 一致：只认方括号里的 1-2 位数字（可逗号分组），
// 所以 [2024](url) 这类链接、[TODO] 这类标签都不会被当成引用。
const ANCHOR_PATTERN = /\[(\d{1,2}(?:\s*[,，、]\s*\d{1,2})*)\]/g

/** 正文的一段：纯文本，或一个可点击/悬空的锚点。 */
interface AnchorSegment {
  text: string
  anchor: boolean
  indices: number[]
  dangling: number[]
}

function parseAnchorIndices(list: string) {
  const indices: number[] = []
  for (const part of list.split(/[,，、]/)) {
    const value = Number.parseInt(part.trim(), 10)
    if (!Number.isFinite(value) || value < 1 || value > 20) continue
    if (!indices.includes(value)) indices.push(value)
  }
  return indices
}

/** 切分正文，相邻纯文本合并（与后端一致），前端负责把它渲染成可点的序号。 */
function segmentContent(content: string, sourceCount: number): AnchorSegment[] {
  const segments: AnchorSegment[] = []
  let cursor = 0
  const pushText = (text: string, dangling: number[] = []) => {
    if (!text && dangling.length === 0) return
    const previous = segments[segments.length - 1]
    if (previous && !previous.anchor) {
      previous.text += text
      previous.dangling.push(...dangling)
      return
    }
    segments.push({ text, anchor: false, indices: [], dangling })
  }

  for (const match of content.matchAll(ANCHOR_PATTERN)) {
    const start = match.index ?? 0
    const parsed = parseAnchorIndices(match[1] ?? '')
    if (parsed.length === 0) continue
    const valid = parsed.filter(index => index <= sourceCount)
    const dangling = parsed.filter(index => index > sourceCount)
    pushText(content.slice(cursor, start))
    if (valid.length > 0) segments.push({ text: match[0], anchor: true, indices: valid, dangling })
    else pushText(match[0], dangling)
    cursor = start + match[0].length
  }

  pushText(content.slice(cursor))
  return segments
}

const previewSegments = computed(() => segmentContent(draft.value.content, editingSources.value.length))
const danglingCount = computed(() => previewSegments.value.reduce((total, segment) => total + segment.dangling.length, 0))
const referencedCount = computed(() => {
  const found = new Set<number>()
  for (const segment of previewSegments.value) for (const index of segment.indices) found.add(index)
  return found.size
})

/** 在光标处插入 [n]；没有光标时追加到末尾。 */
function insertAnchor(sourceIndex: number) {
  const token = `[${sourceIndex}]`
  const element = contentInput.value
  const value = draft.value.content
  if (!element) {
    draft.value.content = value + token
    return
  }

  const start = element.selectionStart ?? value.length
  const end = element.selectionEnd ?? start
  draft.value.content = value.slice(0, start) + token + value.slice(end)
  void nextTick(() => {
    element.focus()
    const caret = start + token.length
    element.setSelectionRange(caret, caret)
  })
}

function anchorSource(index: number) {
  return editingSources.value[index - 1]
}

function openAnchor(index: number) {
  const source = anchorSource(index)
  if (!source) return
  emit('readSource', source.documentId, source.fileName)
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
    <div class="notes-heading-actions">
      <button class="button button-soft" :disabled="!apiOnline || busy || filteredNotes.length === 0" title="把当前筛选出的笔记导出为一份 Markdown" @click="exportVisible"><Download :size="16" /> 批量导出（{{ filteredNotes.length }}）</button>
      <button class="button button-primary" :disabled="!apiOnline || !activeKnowledgeBaseId" @click="openCreate"><Plus :size="17" /> 写一篇笔记</button>
    </div>
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
      <div v-if="note.topicId" class="note-topic-row"><span class="note-topic-chip">{{ topicName(note.topicId) || '已归类专题' }}</span></div>
      <h2>{{ note.title }}</h2>
      <p>{{ note.excerpt }}</p>
      <div v-if="(note.sources?.length ?? 0) > 0" class="note-source-row" @click.stop>
        <button v-for="source in visibleSources(note)" :key="`${source.documentId}-${source.chunkIndex}`" class="note-source-chip" :title="source.snippet || sourceLocation(source)" @click="emit('readSource', source.documentId, source.fileName)"><BookOpen :size="11" /> {{ sourceLocation(source) }}</button>
        <span v-if="extraSourceCount(note) > 0" class="note-source-more">+{{ extraSourceCount(note) }}</span>
        <span v-if="noteAnchoredCount(note)" class="note-anchor-badge" title="正文里写了引用序号，打开笔记可以逐句核对出处">正文 {{ noteAnchoredCount(note) }} 处引用</span>
      </div>
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
          <textarea ref="contentInput" v-model="draft.content" rows="10" maxlength="50000" placeholder="用自己的语言写下理解、疑问或连接…" />
        </label>

        <section v-if="editingSources.length" class="note-anchor-panel">
          <div class="note-anchor-head">
            <span>正文引用锚点</span>
            <small>{{ referencedCount }} / {{ editingSources.length }} 条已被正文引用</small>
          </div>
          <div class="note-anchor-sources">
            <button
              v-for="(source, index) in editingSources"
              :key="`${source.documentId}-${source.chunkIndex}`"
              type="button"
              class="note-anchor-source"
              :title="source.snippet || sourceLocation(source)"
              @click="insertAnchor(index + 1)"
            >
              <span class="note-anchor-number">{{ index + 1 }}</span>
              <span class="note-anchor-file">{{ sourceLocation(source) }}</span>
              <span class="note-anchor-insert">插入</span>
            </button>
          </div>
          <div class="note-anchor-preview">
            <template v-for="(segment, index) in previewSegments" :key="index">
              <span v-if="!segment.anchor">{{ segment.text }}</span>
              <span v-else class="note-anchor-chips">
                <button
                  v-for="anchor in segment.indices"
                  :key="anchor"
                  type="button"
                  class="note-anchor-chip"
                  :title="`第 ${anchor} 条引用：${anchorSource(anchor)?.fileName ?? ''}`"
                  @click="openAnchor(anchor)"
                >{{ anchor }}</button>
                <span
                  v-for="anchor in segment.dangling"
                  :key="`missing-${anchor}`"
                  class="note-anchor-chip note-anchor-chip-missing"
                  :title="`第 ${anchor} 条引用不存在，检查一下序号`"
                >{{ anchor }}</span>
              </span>
            </template>
            <em v-if="!draft.content.trim()">正文还是空的；写内容时用上面的按钮插入引用序号。</em>
          </div>
          <p v-if="danglingCount" class="note-anchor-warning">有 {{ danglingCount }} 处序号对不上已有引用，导出时会保留原文但不会生成脚注。</p>
        </section>

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

        <label class="settings-field">
          <span>所属专题</span>
          <select :value="draft.topicId ?? ''" @change="draft.topicId = ($event.target as HTMLSelectElement).value || null">
            <option value="">未归类</option>
            <option v-for="topic in availableTopics" :key="topic.id" :value="topic.id">{{ topic.name }}</option>
          </select>
        </label>

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
