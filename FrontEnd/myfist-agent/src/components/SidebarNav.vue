<script setup lang="ts">
import { computed } from 'vue'
import { BookOpen, Command, FileText, Fingerprint, FolderOpen, Heart, House, MessageSquareText, NotebookPen, Plus, Settings2, Sparkles } from 'lucide-vue-next'
import type { KnowledgeBase, Topic } from '../services/api'
import type { ViewName } from '../types'

const props = defineProps<{
  activeView: ViewName
  knowledgeBases: KnowledgeBase[]
  activeKnowledgeBaseId: string
  topics: Topic[]
  activeTopicId: string
  materialCount: number
  noteCount: number
  displayName: string
}>()
const emit = defineEmits<{
  navigate: [view: ViewName]
  selectKnowledgeBase: [id: string]
  createKnowledgeBase: []
  createTopic: []
  selectTopic: [id: string]
  editTopics: []
}>()
const activeKnowledgeBase = computed(() => props.knowledgeBases.find(item => item.id === props.activeKnowledgeBaseId))
const primaryNav = computed(() => [
  { id: 'overview' as const, label: '总览', icon: House },
  { id: 'materials' as const, label: '我的资料', icon: FolderOpen, count: String(props.materialCount) },
  { id: 'notes' as const, label: '学习笔记', icon: NotebookPen, count: String(props.noteCount) },
])

const topicColorClass = (color: string) => ['lavender', 'mint', 'peach', 'blue', 'rose', 'sage'].includes(color) ? color : 'lavender'
</script>

<template>
  <aside class="sidebar">
    <div class="brand-row">
      <div class="brand-mark"><BookOpen :size="19" :stroke-width="2.2" /></div>
      <div class="brand-name">知间<span>·</span></div>
      <button class="icon-button sidebar-collapse" aria-label="收起侧栏"><Command :size="16" /></button>
    </div>

    <div class="workspace-switcher">
      <div class="workspace-avatar">{{ activeKnowledgeBase?.name.slice(0, 1) ?? '知' }}</div>
      <label class="workspace-label"><strong>当前知识库</strong><select :value="activeKnowledgeBaseId" aria-label="切换知识库" :disabled="knowledgeBases.length === 0" @change="emit('selectKnowledgeBase', ($event.target as HTMLSelectElement).value)"><option v-for="knowledgeBase in knowledgeBases" :key="knowledgeBase.id" :value="knowledgeBase.id">{{ knowledgeBase.name }}</option></select></label>
      <button class="workspace-add-button" aria-label="新建知识库" title="新建知识库" @click="emit('createKnowledgeBase')"><Plus :size="16" /></button>
    </div>

    <div class="sidebar-label">工作台</div>
    <nav class="primary-nav" aria-label="主导航">
      <button v-for="item in primaryNav" :key="item.id" class="nav-item" :class="{ active: activeView === item.id }" @click="emit('navigate', item.id)">
        <component :is="item.icon" :size="18" :stroke-width="1.8" />
        <span>{{ item.label }}</span>
        <span v-if="item.count" class="nav-count">{{ item.count }}</span>
      </button>
      <button class="nav-item" :class="{ active: activeView === 'assistant' }" @click="emit('navigate', 'assistant')">
        <MessageSquareText :size="18" :stroke-width="1.8" /><span>AI 知识问答</span><span class="nav-dot"></span>
      </button>
      <button class="nav-item" :class="{ active: activeView === 'companions' }" @click="emit('navigate', 'companions')">
        <Heart :size="18" :stroke-width="1.8" /><span>AI 伴侣</span>
      </button>
      <button class="nav-item" :class="{ active: activeView === 'avatar' }" @click="emit('navigate', 'avatar')">
        <Fingerprint :size="18" :stroke-width="1.8" /><span>我的分身</span>
      </button>
    </nav>

    <div class="sidebar-section-heading">
      <button class="sidebar-section-title" :class="{ active: activeView === 'topics' }" @click="emit('navigate', 'topics')">我的专题</button>
      <button class="tiny-icon-button" aria-label="新建专题" title="新建专题" @click="emit('createTopic')"><Plus :size="16" /></button>
    </div>
    <div class="collection-list">
      <button
        v-for="topic in topics"
        :key="topic.id"
        class="collection-item"
        :class="{ active: activeTopicId === topic.id }"
        @click="emit('selectTopic', topic.id)"
        @dblclick="emit('editTopics')"
      >
        <span class="collection-dot" :class="topicColorClass(topic.color)"></span>
        <span class="collection-name">{{ topic.name }}</span>
        <small>{{ topic.documentCount }}</small>
      </button>
      <button v-if="topics.length === 0" class="collection-empty" @click="emit('createTopic')">
        <Plus :size="13" /> 创建第一个专题
      </button>
      <button v-if="topics.length > 0" class="collection-manage" @click="emit('editTopics')">管理专题</button>
    </div>

    <div class="sidebar-spacer"></div>
    <div class="sidebar-tip">
      <div class="tip-icon"><Sparkles :size="16" /></div>
      <div><strong>让知识产生连接</strong><p>上传资料，开始构建你的第二大脑。</p></div>
    </div>
    <button class="nav-item settings-item" :class="{ active: activeView === 'settings' }" @click="emit('navigate', 'settings')"><Settings2 :size="18" :stroke-width="1.8" /><span>设置</span></button>
    <div class="profile-row">
      <div class="profile-avatar">{{ displayName.slice(0, 1) }}</div><div class="profile-meta"><strong>{{ displayName }}</strong><small>持续学习中</small></div><FileText :size="16" class="muted-icon" />
    </div>
  </aside>
</template>
