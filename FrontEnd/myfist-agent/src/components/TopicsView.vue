<script setup lang="ts">
import { computed } from 'vue'
import { ArrowRight, FolderOpen, Layers, Pencil, Plus, Trash2 } from 'lucide-vue-next'
import type { Topic } from '../services/api'
import type { MaterialItem } from '../types'

const props = defineProps<{
  topics: Topic[]
  materials: MaterialItem[]
  apiOnline: boolean
  activeTopicId: string
}>()
const emit = defineEmits<{
  create: []
  rename: [topic: Topic]
  delete: [topic: Topic]
  open: [topicId: string]
}>()

const uncategorizedCount = computed(() => props.materials.filter(item => !item.topicId).length)
const countFor = (topicId: string) => props.materials.filter(item => item.topicId === topicId).length
const recentFor = (topicId: string) => props.materials.filter(item => item.topicId === topicId).slice(0, 3)
const colorClass = (color: string) => ['lavender', 'mint', 'peach', 'blue', 'rose', 'sage'].includes(color) ? color : 'lavender'
</script>

<template>
  <section class="page-heading-row">
    <div>
      <div class="eyebrow"><span class="eyebrow-line"></span> KNOWLEDGE COLLECTIONS</div>
      <h1>我的专题</h1>
      <p class="welcome-copy">为资料建立自己的秩序，让同一主题的思考彼此靠近。</p>
    </div>
    <button class="button button-primary" :disabled="!apiOnline" @click="emit('create')"><Plus :size="17" /> 新建专题</button>
  </section>

  <section class="note-summary-row topic-summary-row">
    <div class="note-summary-card">
      <div class="summary-icon lilac"><Layers :size="18" /></div>
      <div><strong>{{ topics.length }} 个专题</strong><span>把散落的资料收进主题里</span></div>
    </div>
    <div class="note-summary-card topic-uncategorized-card" @click="emit('open', 'none')">
      <div class="summary-icon mint"><FolderOpen :size="18" /></div>
      <div><strong>{{ uncategorizedCount }} 份未分类资料</strong><span>点击查看并归类</span></div>
      <ArrowRight :size="16" class="summary-arrow" />
    </div>
  </section>

  <section v-if="topics.length > 0" class="topic-card-grid">
    <article
      v-for="topic in topics"
      :key="topic.id"
      class="topic-card"
      :class="[`topic-${colorClass(topic.color)}`, { 'topic-card-active': activeTopicId === topic.id }]"
      @click="emit('open', topic.id)"
    >
      <div class="topic-card-top">
        <span class="topic-swatch"></span>
        <span class="topic-count">{{ countFor(topic.id) }} 份资料</span>
      </div>
      <h2>{{ topic.name }}</h2>
      <p class="topic-description">{{ recentFor(topic.id).length ? recentFor(topic.id).map(item => item.title).join(' · ') : '还没有资料，点击进入后上传或移动资料。' }}</p>
      <div class="topic-card-footer">
        <span>{{ new Intl.DateTimeFormat('zh-CN', { month: 'numeric', day: 'numeric' }).format(new Date(topic.createdAt)) }} 创建</span>
        <span class="topic-card-actions" @click.stop>
          <button class="topic-action" aria-label="重命名专题" title="重命名" @click="emit('rename', topic)"><Pencil :size="14" /></button>
          <button class="topic-action topic-action-danger" aria-label="删除专题" title="删除" @click="emit('delete', topic)"><Trash2 :size="14" /></button>
        </span>
      </div>
    </article>
  </section>

  <div v-else class="empty-state">
    <Layers :size="22" />
    <strong>还没有创建专题</strong>
    <span>点击右上角“新建专题”，为你的资料建立主题。</span>
  </div>

  <div class="library-disclaimer"><span class="demo-tag">提示</span>删除专题不会删除资料，只会把其中的资料移回“未分类”。</div>
</template>
