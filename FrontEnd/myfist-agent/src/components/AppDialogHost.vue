<script setup lang="ts">
/**
 * 应用内对话框宿主（唯一渲染者）。
 * 业务代码只调用 services/dialog.ts 里的 promptText / confirmAction，
 * 组件挂在 App.vue 根部一次即可。
 */
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { AlertTriangle, X } from 'lucide-vue-next'
import { closeDialog, currentDialog } from '../services/dialog'

const inputRef = ref<HTMLInputElement | null>(null)
const text = ref('')

const dialog = computed(() => currentDialog.value)
const isOpen = computed(() => dialog.value !== null)
// 分开两个 computed，模板里才能收窄类型（v-if 一个布尔量不会让联合类型变窄）。
const promptDialog = computed(() => (currentDialog.value?.kind === 'prompt' ? currentDialog.value : null))
const confirmDialog = computed(() => (currentDialog.value?.kind === 'confirm' ? currentDialog.value : null))
const isDanger = computed(() => confirmDialog.value?.tone === 'danger')

function cancel() {
  closeDialog()
}

function submit() {
  const active = currentDialog.value
  if (!active) return
  if (active.kind === 'prompt') closeDialog(text.value)
  else closeDialog(true)
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    cancel()
  }
}

watch(dialog, async (next) => {
  if (!next) return
  text.value = next.kind === 'prompt' ? next.value : ''
  await nextTick()
  if (next.kind === 'prompt') {
    inputRef.value?.focus()
    inputRef.value?.select()
  }
})

watch(isOpen, (open) => {
  if (open) window.addEventListener('keydown', onKeydown)
  else window.removeEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="app-dialog">
      <div v-if="dialog" class="app-dialog-scrim" @click.self="cancel">
        <form class="app-dialog" role="dialog" aria-modal="true" :aria-label="dialog.title" @submit.prevent="submit">
          <button type="button" class="app-dialog-close" aria-label="关闭" @click="cancel"><X :size="15" /></button>
          <header class="app-dialog-head">
            <span class="app-dialog-icon" :class="{ danger: isDanger }"><AlertTriangle :size="16" /></span>
            <div>
              <h2>{{ dialog.title }}</h2>
              <p v-if="dialog.message">{{ dialog.message }}</p>
            </div>
          </header>

          <label v-if="promptDialog" class="app-dialog-field">
            <span>{{ promptDialog.label }}</span>
            <input
              ref="inputRef"
              v-model="text"
              type="text"
              :placeholder="promptDialog.placeholder"
              :maxlength="promptDialog.maxLength"
              autocomplete="off"
              spellcheck="false"
              @keydown.enter.prevent="submit"
            />
            <small v-if="promptDialog.maxLength">{{ text.length }} / {{ promptDialog.maxLength }}</small>
          </label>

          <footer class="app-dialog-actions">
            <button type="button" class="app-dialog-button" @click="cancel">{{ dialog.cancelText }}</button>
            <button type="submit" class="app-dialog-button primary" :class="{ danger: isDanger }">{{ dialog.confirmText }}</button>
          </footer>
        </form>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.app-dialog-scrim {
  position: fixed;
  inset: 0;
  z-index: 90;
  display: grid;
  place-items: center;
  padding: 24px;
  background: #24372e55;
  backdrop-filter: blur(2px);
}

.app-dialog {
  position: relative;
  width: min(420px, 100%);
  display: flex;
  flex-direction: column;
  gap: 16px;
  padding: 20px 20px 16px;
  border: 1px solid #e6ece6;
  border-radius: 14px;
  background: #fff;
  box-shadow: 0 24px 60px #2235282e;
}

.app-dialog-close {
  position: absolute;
  top: 12px;
  right: 12px;
  width: 26px;
  height: 26px;
  display: grid;
  place-items: center;
  border: 0;
  border-radius: 8px;
  color: #a0aaa3;
  background: transparent;
}

.app-dialog-close:hover { color: #3b705d; background: #eff4ef; }

.app-dialog-head {
  display: flex;
  align-items: flex-start;
  gap: 11px;
  padding-right: 26px;
}

.app-dialog-icon {
  flex: 0 0 auto;
  width: 30px;
  height: 30px;
  display: grid;
  place-items: center;
  border-radius: 10px 10px 10px 4px;
  color: #4c8c6f;
  background: #e8f3ea;
}

.app-dialog-icon.danger { color: #b4564a; background: #fbebe8; }

.app-dialog-head h2 {
  margin: 3px 0 0;
  color: #2f3f37;
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -.2px;
}

.app-dialog-head p {
  margin: 6px 0 0;
  color: #89948d;
  font-size: 11px;
  line-height: 1.65;
}

.app-dialog-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.app-dialog-field > span {
  color: #7d8a82;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: .3px;
}

.app-dialog-field input {
  width: 100%;
  height: 36px;
  padding: 0 11px;
  border: 1px solid #e2eae2;
  border-radius: 9px;
  color: #35433a;
  background: #fbfdfb;
  font-size: 12px;
  outline: 0;
}

.app-dialog-field input:focus { border-color: #9cc9ae; background: #fff; box-shadow: 0 0 0 3px #9cc9ae26; }

.app-dialog-field small {
  align-self: flex-end;
  color: #aab2ac;
  font-size: 9px;
}

.app-dialog-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
}

.app-dialog-button {
  min-height: 33px;
  padding: 0 14px;
  border: 1px solid #e4ebe4;
  border-radius: 8px;
  color: #5d6b62;
  background: #fff;
  font-size: 11px;
  font-weight: 600;
}

.app-dialog-button:hover { background: #f4f8f4; border-color: #d7e3d8; }

.app-dialog-button.primary {
  border-color: transparent;
  color: #fff;
  background: #397c65;
}

.app-dialog-button.primary:hover { background: #2f6b57; }

.app-dialog-button.primary.danger { background: #c05a4d; }

.app-dialog-button.primary.danger:hover { background: #a94a3e; }

.app-dialog-enter-active,
.app-dialog-leave-active { transition: opacity .16s ease; }

.app-dialog-enter-active .app-dialog,
.app-dialog-leave-active .app-dialog { transition: transform .16s ease, opacity .16s ease; }

.app-dialog-enter-from,
.app-dialog-leave-to { opacity: 0; }

.app-dialog-enter-from .app-dialog,
.app-dialog-leave-to .app-dialog { transform: translateY(8px) scale(.985); opacity: 0; }
</style>
