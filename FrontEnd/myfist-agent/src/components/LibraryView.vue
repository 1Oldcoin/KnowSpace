<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ArrowDownUp, BookOpenText, Check, Clock3, Copy, Download, FileText, Filter, FolderInput, Globe, Grid2X2, List, LoaderCircle, MoreHorizontal, Play, Plus, RotateCw, Search, Trash2 } from 'lucide-vue-next'
import type { Topic } from '../services/api'
import type { MaterialItem } from '../types'
import { SUPPORTED_ACCEPT } from '../fileTypes'

const props = defineProps<{ materials: MaterialItem[]; query: string; topics: Topic[]; activeTopicId: string }>()
const emit = defineEmits<{
  upload: [files: FileList | null]
  retry: [documentId: string]
  delete: [item: MaterialItem]
  download: [item: MaterialItem]
  notice: [message: string]
  selectTopic: [topicId: string]
  assignTopic: [documentId: string, topicId: string | null]
  read: [item: MaterialItem]
}>()
const viewMode = ref<'grid' | 'list'>('grid')
const activeFilter = ref('全部资料')
const filters = ['全部资料', '文档', '网页', '视频', '笔记']
const openMenuId = ref('')
const menuPosition = ref({ top: 0, left: 0 })
const filteredMaterials = computed(() => props.materials.filter((item) => {
  const matchesQuery = !props.query || `${item.title} ${item.category} ${item.fileName ?? ''}`.toLowerCase().includes(props.query.toLowerCase())
  const matchesFilter = activeFilter.value === '全部资料'
    || (activeFilter.value === '文档' && ['pdf', 'document', 'image', 'html'].includes(item.kind))
    || (activeFilter.value === '网页' && item.kind === 'html')
    || (activeFilter.value === '视频' && item.kind === 'video')
    || (activeFilter.value === '笔记' && item.kind === 'note')
  const matchesTopic = !props.activeTopicId
    || (props.activeTopicId === 'none' ? !item.topicId : item.topicId === props.activeTopicId)
  return matchesQuery && matchesFilter && matchesTopic
}))

const activeTopic = computed(() => props.topics.find(topic => topic.id === props.activeTopicId))
const activeTopicLabel = computed(() => props.activeTopicId === 'none' ? '未分类' : activeTopic.value?.name ?? '')
function categoryOf(item: MaterialItem) {
  return props.topics.find(topic => topic.id === item.topicId)?.name ?? item.category
}

function changeTopic(event: Event) {
  const value = (event.target as HTMLSelectElement).value
  emit('selectTopic', value)
}

function assignTopic(item: MaterialItem, event: Event) {
  const value = (event.target as HTMLSelectElement).value
  emit('assignTopic', item.id, value || null)
}

function isPendingUpload(item: MaterialItem) {
  return item.id.startsWith('upload-')
}

function toggleMenu(itemId: string, event: MouseEvent) {
  if (openMenuId.value === itemId) {
    openMenuId.value = ''
    return
  }
  // 菜单使用 fixed 定位，避免被卡片的 overflow: hidden 裁剪。
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  menuPosition.value = { top: rect.bottom + 6, left: Math.max(8, rect.right - 148) }
  openMenuId.value = itemId
}

function closeMenu() {
  openMenuId.value = ''
}

function requestAction(action: 'reprocess' | 'download' | 'delete' | 'copy', item: MaterialItem) {
  openMenuId.value = ''
  if (action === 'reprocess') emit('retry', item.id)
  else if (action === 'download') emit('download', item)
  else if (action === 'delete') emit('delete', item)
  else void copyName(item)
}

async function copyName(item: MaterialItem) {
  try {
    await navigator.clipboard.writeText(item.fileName || item.title)
    emit('notice', '资料名称已复制。')
  } catch {
    emit('notice', '复制失败，请手动复制。')
  }
}

onMounted(() => {
  document.addEventListener('click', closeMenu)
  window.addEventListener('scroll', closeMenu, true)
})
onBeforeUnmount(() => {
  document.removeEventListener('click', closeMenu)
  window.removeEventListener('scroll', closeMenu, true)
})
</script>

<template>
  <section class="page-heading-row">
    <div><div class="eyebrow"><span class="eyebrow-line"></span> YOUR LIBRARY</div><h1>我的资料</h1><p class="welcome-copy">把零散的知识，收进自己的秩序里。</p></div>
    <label class="button button-primary"><Plus :size="17" /> 添加资料<input type="file" multiple :accept="SUPPORTED_ACCEPT" @change="emit('upload', ($event.target as HTMLInputElement).files); ($event.target as HTMLInputElement).value = ''"></label>
  </section>

  <section class="library-toolbar">
    <div class="filter-pills">
      <button v-for="filter in filters" :key="filter" class="filter-pill" :class="{ selected: activeFilter === filter }" @click="activeFilter = filter">{{ filter }} <span v-if="filter === '全部资料'">{{ materials.length }}</span></button>
      <span v-if="activeTopicId" class="topic-filter-chip">{{ activeTopicLabel }}<button aria-label="清除专题筛选" @click="emit('selectTopic', '')">×</button></span>
    </div>
    <div class="toolbar-actions">
      <label class="topic-select-wrap" title="按专题筛选">
        <FolderInput :size="14" />
        <select class="topic-select" :value="activeTopicId" @change="changeTopic">
          <option value="">全部专题</option>
          <option value="none">未分类</option>
          <option v-for="topic in topics" :key="topic.id" :value="topic.id">{{ topic.name }}</option>
        </select>
      </label>
      <button class="button button-soft"><Filter :size="15" /> 筛选</button><button class="button button-soft"><ArrowDownUp :size="15" /> 最近添加</button><div class="view-toggle"><button :class="{ selected: viewMode === 'grid' }" aria-label="网格视图" @click="viewMode = 'grid'"><Grid2X2 :size="16" /></button><button :class="{ selected: viewMode === 'list' }" aria-label="列表视图" @click="viewMode = 'list'"><List :size="17" /></button></div></div>
  </section>

  <section v-if="viewMode === 'grid'" class="library-grid">
    <article v-for="item in filteredMaterials" :key="item.id" class="material-card library-material-card">
      <div class="material-cover" :class="`cover-${item.color}`"><div class="cover-top"><span class="file-badge" :class="`file-${item.kind}`"><FileText v-if="item.kind !== 'video' && item.kind !== 'html'" :size="17" /><Globe v-else-if="item.kind === 'html'" :size="16" /><Play v-else :size="15" /></span><button class="tiny-icon-button" aria-label="更多操作" @click.stop="toggleMenu(item.id, $event)"><MoreHorizontal :size="17" /></button></div><div class="cover-art"><span class="cover-orbit"></span><span class="cover-glyph">{{ item.kind === 'video' ? '影' : item.kind === 'html' ? '网' : categoryOf(item).slice(0, 1) }}</span></div><div class="cover-footer"><span>{{ categoryOf(item) }}</span><span>{{ item.detail }}</span></div></div>
      <div v-if="openMenuId === item.id" class="material-menu" :style="{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }" @click.stop>
        <button v-if="!isPendingUpload(item)" @click="closeMenu(); emit('read', item)"><BookOpenText :size="14" /> 阅读原文</button>
        <button v-if="!isPendingUpload(item) && (item.status === 'ready' || item.status === 'failed')" @click="requestAction('reprocess', item)"><RotateCw :size="14" /> 重新解析</button>
        <button @click="requestAction('copy', item)"><Copy :size="14" /> 复制名称</button>
        <div class="material-menu-topic">
          <label><FolderInput :size="14" /> 移至专题</label>
          <select :value="item.topicId ?? ''" :disabled="isPendingUpload(item)" @change="assignTopic(item, $event)">
            <option value="">未分类</option>
            <option v-for="topic in topics" :key="topic.id" :value="topic.id">{{ topic.name }}</option>
          </select>
        </div>
        <button v-if="!isPendingUpload(item)" @click="requestAction('download', item)"><Download :size="14" /> 下载原文件</button>
        <button v-if="!isPendingUpload(item)" class="material-menu-danger" @click="requestAction('delete', item)"><Trash2 :size="14" /> 删除资料</button>
      </div>
      <div class="material-card-info"><h3>{{ item.title }}</h3><div class="material-meta"><span><Clock3 :size="13" /> {{ item.updated }}</span><span v-if="item.status === 'ready'" class="status-ready"><Check :size="12" /> 已保存</span><span v-else-if="item.status === 'processing'" class="status-processing"><LoaderCircle :size="12" /> {{ item.processingStage || '排队中' }}<template v-if="item.progress"> · {{ item.progress }}%</template></span><span v-else-if="item.status === 'failed'" class="status-failed">处理失败</span><span v-else class="status-demo">示例资料</span></div><p v-if="item.summary" class="material-summary">{{ item.summary }}</p><p v-if="item.error" class="material-error">{{ item.error }}</p><button v-if="item.status === 'failed' && !item.id.startsWith('upload-')" class="retry-button" @click.stop="emit('retry', item.id)">重试解析</button></div>
    </article>
  </section>

  <section v-else class="material-list-table">
    <div class="table-head"><span>资料名称</span><span>所属专题</span><span>最近更新</span><span>状态</span><span></span></div>
     <article v-for="item in filteredMaterials" :key="item.id" class="table-row"><span class="table-title"><span class="table-file-icon" :class="`file-${item.kind}`"><FileText v-if="item.kind !== 'video' && item.kind !== 'html'" :size="16" /><Globe v-else-if="item.kind === 'html'" :size="15" /><Play v-else :size="14" /></span><span><strong>{{ item.title }}</strong><small>{{ item.detail }}</small><small v-if="item.summary" class="table-summary">{{ item.summary }}</small></span></span><span class="table-category">{{ categoryOf(item) }}</span><span class="table-date">{{ item.updated }}</span><span><span v-if="item.status === 'ready'" class="status-ready"><Check :size="12" /> 已保存</span><span v-else-if="item.status === 'processing'" class="status-processing"><LoaderCircle :size="12" /> {{ item.processingStage || '排队中' }}<template v-if="item.progress"> · {{ item.progress }}%</template></span><span v-else-if="item.status === 'failed'" class="status-failed">处理失败 <button v-if="!item.id.startsWith('upload-')" class="retry-button" @click.stop="emit('retry', item.id)">重试</button></span><span v-else class="status-demo">示例资料</span></span><button class="tiny-icon-button" aria-label="更多操作" @click.stop="toggleMenu(item.id, $event)"><MoreHorizontal :size="17" /></button><div v-if="openMenuId === item.id" class="material-menu" :style="{ top: `${menuPosition.top}px`, left: `${menuPosition.left}px` }" @click.stop><button v-if="!isPendingUpload(item)" @click="closeMenu(); emit('read', item)"><BookOpenText :size="14" /> 阅读原文</button><button v-if="!isPendingUpload(item) && (item.status === 'ready' || item.status === 'failed')" @click="requestAction('reprocess', item)"><RotateCw :size="14" /> 重新解析</button><button @click="requestAction('copy', item)"><Copy :size="14" /> 复制名称</button><div class="material-menu-topic"><label><FolderInput :size="14" /> 移至专题</label><select :value="item.topicId ?? ''" :disabled="isPendingUpload(item)" @change="assignTopic(item, $event)"><option value="">未分类</option><option v-for="topic in topics" :key="topic.id" :value="topic.id">{{ topic.name }}</option></select></div><button v-if="!isPendingUpload(item)" @click="requestAction('download', item)"><Download :size="14" /> 下载原文件</button><button v-if="!isPendingUpload(item)" class="material-menu-danger" @click="requestAction('delete', item)"><Trash2 :size="14" /> 删除资料</button></div></article>
    <div v-if="filteredMaterials.length === 0" class="empty-state"><Search :size="22" /><strong>没有找到相关资料</strong><span>换个关键词试试吧</span></div>
  </section>
  <div v-if="viewMode === 'grid' && filteredMaterials.length === 0" class="empty-state"><Search :size="22" /><strong>没有找到相关资料</strong><span>换个关键词试试吧</span></div>
  <div class="library-disclaimer"><span class="demo-tag">说明</span>HTML/HTM 会先抽取正文（标题、列表、表格）再切片向量化；视频支持分片续传（最大 5 GiB），默认转写音轨并生成摘要；画面分析需启用关键帧并配置视觉模型。</div>
</template>

<style scoped>
.material-menu {
  position: fixed;
  z-index: 60;
  width: 148px;
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 5px;
  border: 1px solid #e8ede8;
  border-radius: 9px;
  background: #fff;
  box-shadow: 0 10px 26px #304a3a1f;
}
.material-menu button {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px;
  border: 0;
  border-radius: 6px;
  color: #536258;
  background: transparent;
  text-align: left;
  white-space: nowrap;
  font-size: 11px;
  cursor: pointer;
}
.material-menu button:hover {
  color: #397c65;
  background: #edf5ee;
}
.material-menu .material-menu-danger {
  color: #a54848;
}
.material-menu .material-menu-danger:hover {
  background: #f9eded;
}
</style>
