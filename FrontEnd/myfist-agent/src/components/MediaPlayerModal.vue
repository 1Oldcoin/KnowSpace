<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { Clock, Film, X } from 'lucide-vue-next'
import { apiBaseUrl } from '../services/api'

/**
 * 从引用跳到「第几分几秒」的播放弹窗。
 *
 * 直接用 `/api/documents/{id}/download`（后端已开 range 请求），
 * 不额外做转码：视频原文件本来就在本地，浏览器能播的直接播。
 */
const props = defineProps<{
  documentId: string
  title: string
  fileName?: string
  seconds: number
  apiOnline: boolean
}>()
const emit = defineEmits<{ close: [] }>()

const video = ref<HTMLVideoElement | null>(null)
const error = ref('')

const source = computed(() => `${apiBaseUrl}/api/documents/${encodeURIComponent(props.documentId)}/download`)

const startLabel = computed(() => {
  const total = Math.max(0, Math.floor(props.seconds))
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const secs = total % 60
  const pad = (value: number) => value.toString().padStart(2, '0')
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(secs)}` : `${minutes}:${pad(secs)}`
})

function seek() {
  const element = video.value
  if (!element) return
  // 元数据还没到时 currentTime 赋值会被丢弃，等 loadedmetadata 再跳一次。
  try {
    element.currentTime = props.seconds
  } catch {
    /* 某些浏览器在 readyState 不足时抛错，loadedmetadata 会补上 */
  }
}

function onLoadedMetadata() {
  seek()
  void video.value?.play().catch(() => { /* 自动播放被拦截时留给用户点 */ })
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close')
}

onMounted(() => {
  window.addEventListener('keydown', onKeydown)
  // 组件挂载后立刻尝试一次：src 可能在 loadedmetadata 之前就绪。
  window.setTimeout(seek, 0)
})

onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))

watch(() => props.seconds, seek)
</script>

<template>
  <Teleport to="body">
    <div class="media-scrim" @click.self="emit('close')">
      <section class="media-panel" role="dialog" aria-modal="true" aria-label="视频定位播放">
        <header class="media-head">
          <span class="media-icon"><Film :size="17" /></span>
          <div class="media-title">
            <strong>{{ title }}</strong>
            <small>{{ fileName || '原始视频' }} · 从 <b>{{ startLabel }}</b> 开始播放</small>
          </div>
          <button class="media-close" title="关闭" aria-label="关闭" @click="emit('close')"><X :size="15" /></button>
        </header>

        <div class="media-body">
          <div v-if="!apiOnline" class="media-state">API 未连接，无法播放原始视频。</div>
          <div v-else-if="error" class="media-state media-state-error">{{ error }}</div>
          <video
            v-show="apiOnline && !error"
            ref="video"
            class="media-player"
            controls
            preload="metadata"
            :src="source"
            @loadedmetadata="onLoadedMetadata"
            @error="error = '浏览器无法播放这个视频格式（可用系统播放器打开原文件）。'"
          ></video>
        </div>

        <footer class="media-foot">
          <span class="media-hint"><Clock :size="12" /> 引用来自视频的时间轴切片，画面变化处会各自成段。</span>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.media-scrim { position: fixed; inset: 0; z-index: 60; display: flex; align-items: center; justify-content: center; padding: 24px; background: rgba(23, 32, 27, 0.46); backdrop-filter: blur(2px); }
.media-panel { display: flex; flex-direction: column; width: min(980px, 100%); max-height: 90vh; overflow: hidden; border-radius: 14px; background: #fff; box-shadow: 0 24px 60px rgba(23, 32, 27, 0.28); }
.media-head { display: flex; align-items: center; gap: 10px; padding: 12px 16px; border-bottom: 1px solid #e8ede8; }
.media-icon { display: grid; place-items: center; width: 30px; height: 30px; border-radius: 9px; color: #397c65; background: #edf5ee; }
.media-title { min-width: 0; flex: 1; display: flex; flex-direction: column; gap: 2px; }
.media-title strong { color: #2c3a31; font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.media-title small { color: #8b968f; font-size: 10px; }
.media-title b { color: #397c65; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.media-close { display: grid; place-items: center; width: 26px; height: 26px; border: 0; border-radius: 7px; color: #71807a; background: transparent; }
.media-close:hover { color: #397c65; background: #f1f6f2; }
.media-body { display: flex; align-items: center; justify-content: center; min-height: 240px; padding: 0; background: #0f1512; }
.media-player { width: 100%; max-height: 68vh; background: #0f1512; }
.media-state { padding: 40px 20px; color: #c8d2cb; font-size: 12px; }
.media-state-error { color: #f0b7a8; }
.media-foot { display: flex; align-items: center; gap: 6px; padding: 9px 16px; border-top: 1px solid #e8ede8; color: #8b968f; font-size: 10px; }
.media-hint { display: inline-flex; align-items: center; gap: 5px; }
</style>
