/**
 * 前端支持上传的资料类型。
 * 与后端 `LocalDocumentStore.AllowedExtensions` 保持一致，避免 accept、校验和提示三处漂移。
 */

export const VIDEO_EXTENSIONS: readonly string[] = ['.mp4', '.mov', '.mkv', '.webm', '.avi']
export const IMAGE_EXTENSIONS: readonly string[] = ['.png', '.jpg', '.jpeg', '.webp', '.gif']
export const HTML_EXTENSIONS: readonly string[] = ['.html', '.htm']

/**
 * 纯音频：会被转写成可检索文本（语音消息、录音、播客）。
 * 与后端 `LocalDocumentStore.IsAudioExtension` 保持一致。
 */
export const AUDIO_EXTENSIONS: readonly string[] = ['.mp3', '.wav', '.m4a', '.aac', '.ogg', '.opus', '.flac', '.weba', '.amr']

/** 会被解析成正文再切片、向量化的文本类资料。 */
export const TEXT_EXTENSIONS: readonly string[] = ['.txt', '.md']

export const SUPPORTED_EXTENSIONS: readonly string[] = [
  '.pdf',
  '.docx',
  ...TEXT_EXTENSIONS,
  ...HTML_EXTENSIONS,
  ...IMAGE_EXTENSIONS,
  ...VIDEO_EXTENSIONS,
  ...AUDIO_EXTENSIONS,
]

/** 文件选择框的 accept 值，例如 `.pdf,.docx,...`。 */
export const SUPPORTED_ACCEPT = SUPPORTED_EXTENSIONS.join(',')

export const MAX_FILE_SIZE_BYTES = 50 * 1024 ** 2
export const MAX_VIDEO_FILE_SIZE_BYTES = 5 * 1024 ** 3
/** 与后端 `Storage:MaxAudioFileSizeBytes` 的默认值一致。 */
export const MAX_AUDIO_FILE_SIZE_BYTES = 300 * 1024 ** 2

/** 取小写扩展名（含点）；没有扩展名时返回空字符串。 */
export function fileExtension(fileName: string): string {
  const index = fileName.lastIndexOf('.')
  return index < 0 ? '' : fileName.slice(index).toLowerCase()
}

export function isVideoExtension(extension: string): boolean {
  return VIDEO_EXTENSIONS.includes(extension.toLowerCase())
}

export function isImageExtension(extension: string): boolean {
  return IMAGE_EXTENSIONS.includes(extension.toLowerCase())
}

export function isAudioExtension(extension: string): boolean {
  return AUDIO_EXTENSIONS.includes(extension.toLowerCase())
}

/** HTML/HTM 会由后端抽取纯文本后再入库，前端只负责按类型展示。 */
export function isHtmlExtension(extension: string): boolean {
  return HTML_EXTENSIONS.includes(extension.toLowerCase())
}

export function maxSizeForExtension(extension: string): number {
  if (isVideoExtension(extension)) return MAX_VIDEO_FILE_SIZE_BYTES
  if (isAudioExtension(extension)) return MAX_AUDIO_FILE_SIZE_BYTES
  return MAX_FILE_SIZE_BYTES
}
