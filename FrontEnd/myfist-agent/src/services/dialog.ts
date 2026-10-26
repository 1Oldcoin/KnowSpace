/**
 * 应用内对话框（输入框 / 确认框）。
 *
 * 为什么不用 window.prompt / window.confirm：
 *  - Electron 明确不支持 `window.prompt`（主进程的 window_setup.cc 直接让它抛错
 *    "prompt() is and will not be supported"）。打包版里所有基于 prompt 的入口
 *    ——新建专题、重命名专题、新建知识库、重命名对话、分身纠错——点了都没反应，
 *    而且异常发生在 try/catch 之外，界面上连提示都没有。
 *  - window.confirm 在 Electron 里是同步原生弹窗，样式与产品完全脱节，也无法
 *    做「危险操作」的视觉区分。
 *
 * 因此统一改成 Promise 化的应用内对话框：调用方 `await` 一个结果，
 * 渲染由唯一宿主组件 AppDialogHost.vue 负责（挂在 App.vue 根部）。
 */

import { shallowRef } from 'vue'

export type DialogTone = 'default' | 'danger'

export interface PromptDialogOptions {
  /** 标题，例如「新建专题」。 */
  title: string
  /** 标题下的一句说明。 */
  message?: string
  /** 输入框上方的字段名。 */
  label?: string
  /** 初始值（重命名时填入原名）。 */
  value?: string
  placeholder?: string
  maxLength?: number
  confirmText?: string
  cancelText?: string
}

export interface ConfirmDialogOptions {
  title: string
  message?: string
  confirmText?: string
  cancelText?: string
  /** danger 用于删除等不可恢复操作（红色确认按钮）。 */
  tone?: DialogTone
}

export interface PromptDialogRequest extends Required<Omit<PromptDialogOptions, 'message' | 'placeholder'>> {
  kind: 'prompt'
  message?: string
  placeholder?: string
  resolve: (value: string | null) => void
}

export interface ConfirmDialogRequest extends ConfirmDialogOptions {
  kind: 'confirm'
  confirmText: string
  cancelText: string
  tone: DialogTone
  resolve: (value: boolean) => void
}

export type DialogRequest = PromptDialogRequest | ConfirmDialogRequest

/** 当前展示中的对话框；只有 AppDialogHost 会读它。 */
export const currentDialog = shallowRef<DialogRequest | null>(null)

/**
 * 关闭当前对话框。
 * `result` 的含义按对话框类型而定：输入框是字符串（null = 取消），确认框是布尔值。
 */
export function closeDialog(result?: string | boolean | null): void {
  const dialog = currentDialog.value
  if (!dialog) return
  currentDialog.value = null
  if (dialog.kind === 'prompt') dialog.resolve(typeof result === 'string' ? result : null)
  else dialog.resolve(result === true)
}

/** 弹出输入框，返回用户输入（未 trim）；取消返回 null。 */
export function promptText(options: PromptDialogOptions): Promise<string | null> {
  // 同一时刻只允许一个对话框：旧请求按「取消」结算，避免 Promise 永久悬挂。
  closeDialog()
  return new Promise<string | null>((resolve) => {
    currentDialog.value = {
      kind: 'prompt',
      title: options.title,
      message: options.message,
      label: options.label ?? '名称',
      value: options.value ?? '',
      placeholder: options.placeholder,
      maxLength: options.maxLength ?? 100,
      confirmText: options.confirmText ?? '确定',
      cancelText: options.cancelText ?? '取消',
      resolve
    }
  })
}

/** 弹出确认框，确认返回 true，取消返回 false。 */
export function confirmAction(options: ConfirmDialogOptions): Promise<boolean> {
  closeDialog()
  return new Promise<boolean>((resolve) => {
    currentDialog.value = {
      kind: 'confirm',
      title: options.title,
      message: options.message,
      confirmText: options.confirmText ?? '确定',
      cancelText: options.cancelText ?? '取消',
      tone: options.tone ?? 'default',
      resolve
    }
  })
}
