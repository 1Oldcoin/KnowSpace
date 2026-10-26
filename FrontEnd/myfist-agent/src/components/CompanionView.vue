<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ArrowLeft, Heart, LoaderCircle, MessageCircle, Plus, Send, Sparkles, Trash2, Upload, UserRound } from 'lucide-vue-next'
import {
  createCompanionSession,
  deleteCompanion,
  generateCompanionPersona,
  getCompanionIndex,
  importChatHistory,
  listCompanionMessages,
  listCompanionSessions,
  listCompanions,
  reindexCompanion,
  sendCompanionMessage,
  type CompanionMessage,
  type CompanionProfile,
  type CompanionSession,
} from '../services/api'
import { confirmAction } from '../services/dialog'

const profiles = ref<CompanionProfile[]>([])
const selectedProfile = ref<CompanionProfile | null>(null)
const sessions = ref<CompanionSession[]>([])
const selectedSession = ref<CompanionSession | null>(null)
const messages = ref<CompanionMessage[]>([])
const displayMessages = computed(() => messages.value)
const profileName = ref('')
const targetSender = ref('')
const platform = ref('微信')
const file = ref<File | null>(null)
const draft = ref('')
const busy = ref(false)
const loading = ref(false)
const notice = ref('')
const errorMessage = ref('')
// 当前向量模型下已建索引的消息数；null = 还没取到（例如向量模型未配置）。
const indexedCount = ref<number | null>(null)

onMounted(() => { void refreshProfiles() })

async function refreshProfiles() {
  loading.value = true
  errorMessage.value = ''
  try {
    profiles.value = await listCompanions()
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    loading.value = false
  }
}

async function importFile() {
  if (!file.value || !profileName.value.trim() || !targetSender.value.trim()) {
    errorMessage.value = '请填写人物名称、导出中的发言者名称并选择聊天记录文件。'
    return
  }
  busy.value = true
  errorMessage.value = ''
  try {
    const profile = await importChatHistory(file.value, profileName.value.trim(), targetSender.value.trim(), platform.value)
    profiles.value.unshift(profile)
    profileName.value = ''
    targetSender.value = ''
    file.value = null
    notice.value = `已导入 ${profile.messageCount} 条消息。`
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    busy.value = false
  }
}

async function openProfile(profile: CompanionProfile) {
  selectedProfile.value = profile
  selectedSession.value = null
  messages.value = []
  errorMessage.value = ''
  indexedCount.value = null
  try {
    sessions.value = await listCompanionSessions(profile.id)
    if (sessions.value.length) await openSession(sessions.value[0]!)
    else await newSession()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
  // 索引状态是次要信息，取不到不能影响页面打开。
  void refreshIndexCount(profile.id)
}

async function refreshIndexCount(profileId: string) {
  try {
    indexedCount.value = (await getCompanionIndex(profileId)).indexed
  } catch {
    indexedCount.value = null
  }
}

async function reindexHistory() {
  if (!selectedProfile.value) return
  busy.value = true
  errorMessage.value = ''
  notice.value = ''
  try {
    const outcome = await reindexCompanion(selectedProfile.value.id)
    indexedCount.value = outcome.total
    notice.value = outcome.warning
      ? `向量索引不可用：${outcome.warning}。语义检索暂不可用，仍可按关键词回忆。`
      : `已补充 ${outcome.indexed} 条消息的向量（共 ${outcome.total} 条可语义检索）。`
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    busy.value = false
  }
}

async function openSession(session: CompanionSession) {
  selectedSession.value = session
  try {
    messages.value = await listCompanionMessages(session.id)
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function newSession() {
  if (!selectedProfile.value) return
  try {
    const session = await createCompanionSession(selectedProfile.value.id, '新的陪伴对话')
    sessions.value.unshift(session)
    selectedSession.value = session
    messages.value = []
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

async function sendMessage() {
  const text = draft.value.trim()
  if (!text || busy.value || !selectedProfile.value) return
  if (!selectedSession.value) await newSession()
  if (!selectedSession.value) return

  const profile = selectedProfile.value
  const session = selectedSession.value
  messages.value.push({ id: `local-${crypto.randomUUID()}`, role: 'user', content: text, createdAt: new Date().toISOString() })
  draft.value = ''
  busy.value = true
  errorMessage.value = ''
  try {
    const result = await sendCompanionMessage(profile.id, session.id, text)
    messages.value.push({ id: `local-${crypto.randomUUID()}`, role: 'assistant', content: result.answer, createdAt: new Date().toISOString() })
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    busy.value = false
  }
}

async function createPersona() {
  if (!selectedProfile.value) return
  busy.value = true
  errorMessage.value = ''
  try {
    const updated = await generateCompanionPersona(selectedProfile.value.id)
    selectedProfile.value = updated
    profiles.value = profiles.value.map(profile => profile.id === updated.id ? updated : profile)
    notice.value = '表达风格画像已生成。'
  } catch (error) {
    errorMessage.value = errorText(error)
  } finally {
    busy.value = false
  }
}

async function removeProfile(profile: CompanionProfile) {
  if (!(await confirmAction({
    title: '删除档案',
    message: `确定删除“${profile.name}”的档案、导入消息和所有陪伴会话吗？此操作不可撤销。`,
    confirmText: '删除',
    tone: 'danger'
  }))) return
  try {
    await deleteCompanion(profile.id)
    profiles.value = profiles.value.filter(item => item.id !== profile.id)
    if (selectedProfile.value?.id === profile.id) backToProfiles()
  } catch (error) {
    errorMessage.value = errorText(error)
  }
}

function backToProfiles() {
  selectedProfile.value = null
  selectedSession.value = null
  sessions.value = []
  messages.value = []
  void refreshProfiles()
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请检查 API 服务。'
}

function messageTime(value: string) {
  return new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit' }).format(new Date(value))
}
</script>

<template>
  <section class="companion-page">
    <header class="companion-heading">
      <div>
        <div class="eyebrow"><span class="eyebrow-line"></span> YOUR PERSONAL COMPANION</div>
        <h1>{{ selectedProfile ? selectedProfile.name : 'AI 伴侣' }}</h1>
        <p class="welcome-copy">导入你有权保存的聊天记录，建立基于对话样本的纪念性 AI 模拟。</p>
      </div>
      <button v-if="selectedProfile" class="button button-soft" @click="backToProfiles"><ArrowLeft :size="16" /> 返回人物档案</button>
    </header>

    <div class="companion-notice"><Heart :size="16" /><span>这是 AI 生成的纪念性模拟，并非本人。陪伴对话按人物档案单独保存，不会与知识库问答混在一起。若配置为云 API，聊天内容和相关记录会发送给所配置的服务商。请只导入你有权处理的数据。</span></div>
    <div v-if="errorMessage" class="companion-error" role="alert">{{ errorMessage }}</div>
    <div v-if="notice" class="companion-success" role="status">{{ notice }}<button aria-label="关闭提示" @click="notice = ''">×</button></div>

    <template v-if="!selectedProfile">
      <div class="companion-import-panel">
        <div class="companion-panel-title"><div class="companion-title-icon"><Upload :size="18" /></div><div><h2>导入聊天记录</h2><p>支持 QQ / 微信导出的文本、通用 JSON / TXT / CSV，最大 20 MB。</p></div></div>
        <div class="companion-form">
          <label>人物档案名称<input v-model="profileName" maxlength="100" placeholder="例如：小林" /></label>
          <label>平台<select v-model="platform"><option>微信</option><option>QQ</option><option>其他</option></select></label>
          <label>导出中的发言者名称<input v-model="targetSender" maxlength="100" placeholder="必须与导出记录中的名称一致" /></label>
          <label class="companion-file-label">聊天记录文件<input type="file" accept=".json,.txt,.csv,application/json,text/plain,text/csv" @change="file = ($event.target as HTMLInputElement).files?.[0] ?? null" /><small>{{ file?.name || '选择 .json / .txt / .csv 文件' }}</small></label>
          <button class="button button-primary companion-import-button" :disabled="busy" @click="importFile"><LoaderCircle v-if="busy" class="spin" :size="16" /><Upload v-else :size="16" /> 导入并保存在本机</button>
        </div>
        <details class="companion-format"><summary>通用文件格式说明</summary><p><strong>QQ</strong>：消息管理器导出的 txt，每条消息上方一行「时间 昵称(QQ号)」，下面的多行都属于这条消息。<strong>微信/其它</strong>：每行「[时间] 昵称: 内容」或「昵称 时间: 内容」，未带时间的续行并入上一条。<strong>JSON</strong>：顶层数组或 messages / records 数组，字段支持 sender / content / timestamp（也认 from / text / time）。<strong>CSV</strong>：有表头时自动识别时间/发送者/内容列，没有表头就按前三列读。加密数据库无法直接导入，请先用客户端导出成文本。</p></details>
      </div>

      <div class="companion-list-heading"><h2>人物档案</h2><span>{{ profiles.length }} 个</span></div>
      <div v-if="loading" class="companion-empty"><LoaderCircle class="spin" :size="20" />正在读取档案…</div>
      <div v-else-if="profiles.length" class="companion-profile-grid">
        <article v-for="profile in profiles" :key="profile.id" class="companion-profile-card">
          <button class="companion-profile-open" @click="openProfile(profile)"><span class="companion-avatar"><Heart :size="20" /></span><strong>{{ profile.name }}</strong><small>{{ profile.platform }} · {{ profile.messageCount.toLocaleString() }} 条消息</small><span class="companion-card-action"><MessageCircle :size="15" /> 开始陪伴对话</span></button>
          <button class="companion-delete" :aria-label="`删除${profile.name}档案`" @click="removeProfile(profile)"><Trash2 :size="16" /></button>
        </article>
      </div>
      <div v-else class="companion-empty"><UserRound :size="22" /><strong>还没有人物档案</strong><span>导入一份聊天记录后，就可以生成表达风格画像并开始对话。</span></div>
    </template>

    <template v-else>
      <div class="companion-workspace">
        <aside class="companion-session-list">
          <div class="companion-session-heading"><strong>专属陪伴对话</strong><button class="tiny-icon-button" aria-label="新建陪伴对话" @click="newSession"><Plus :size="17" /></button></div>
          <button v-for="session in sessions" :key="session.id" class="companion-session-item" :class="{ selected: selectedSession?.id === session.id }" @click="openSession(session)">{{ session.title }}</button>
          <button class="companion-persona-button" :disabled="busy" @click="createPersona"><Sparkles :size="15" />{{ selectedProfile.personaPrompt ? '重新生成表达画像' : '生成表达风格画像' }}</button>
          <div class="companion-index-row">
            <span class="companion-index-label">语义记忆 <b>{{ indexedCount === null ? '—' : indexedCount.toLocaleString() }}</b> 条</span>
            <button class="tiny-icon-button" :disabled="busy" title="补齐/重建这条档案的向量索引（换向量模型或导入时建索引失败后使用）" aria-label="重建语义索引" @click="reindexHistory"><LoaderCircle v-if="busy" :size="14" class="spin" /><Sparkles v-else :size="14" /></button>
          </div>
          <small class="companion-index-hint">已索引的消息可以按意思检索（“上次聊到的那件事”）；没索引时只能按关键词回忆。</small>
        </aside>
        <div class="companion-chat">
          <div class="companion-chat-head"><div class="companion-avatar"><Heart :size="18" /></div><div><strong>{{ selectedProfile.name }} · AI 模拟</strong><small>参考 {{ selectedProfile.messageCount.toLocaleString() }} 条已导入消息</small></div><span class="simulation-tag">AI 模拟</span></div>
          <div class="companion-messages">
            <div v-if="!displayMessages.length" class="companion-chat-empty"><div class="companion-chat-orb"><Heart :size="22" /></div><strong>在这里开始一段对话</strong><span>AI 会参考导入的消息风格和相关片段，但无法确认聊天记录之外的真实记忆。</span></div>
            <div v-for="message in displayMessages" :key="message.id" class="companion-message" :class="`companion-message-${message.role}`"><div class="companion-message-avatar"><Heart v-if="message.role === 'assistant'" :size="15" /><UserRound v-else :size="15" /></div><div class="companion-message-body"><div class="companion-message-meta"><strong>{{ message.role === 'assistant' ? `${selectedProfile.name} · AI 模拟` : '我' }}</strong><time>{{ messageTime(message.createdAt) }}</time></div><p>{{ message.content }}</p></div></div>
            <div v-if="busy" class="companion-message companion-message-assistant"><div class="companion-message-avatar"><Heart :size="15" /></div><div class="companion-typing"><i></i><i></i><i></i></div></div>
          </div>
          <div class="companion-composer"><textarea v-model="draft" rows="2" placeholder="写下你想说的话…" :disabled="busy" @keydown.enter.exact.prevent="sendMessage"></textarea><button class="send-button" :disabled="!draft.trim() || busy" aria-label="发送消息" @click="sendMessage"><Send :size="17" /></button></div>
          <p class="companion-chat-disclaimer">AI 生成的纪念性模拟，不是本人；具体回忆可能不准确。</p>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.companion-page { display: grid; gap: 20px; max-width: 1280px; margin: 0 auto; }
.companion-heading { display: flex; justify-content: space-between; align-items: flex-start; gap: 20px; }
.companion-heading h1 { margin: 8px 0 4px; }
.companion-notice, .companion-error, .companion-success { display: flex; align-items: center; gap: 10px; border-radius: 12px; padding: 12px 15px; font-size: 13px; line-height: 1.6; }
.companion-notice { color: #715536; background: #fff8ed; border: 1px solid #f2e4cb; }
.companion-error { color: #a8333b; background: #fff0f0; }
.companion-success { color: #267257; background: #edf8f2; justify-content: space-between; }
.companion-success button { border: 0; background: none; color: inherit; cursor: pointer; font-size: 18px; }
.companion-import-panel { padding: 22px; border: 1px solid #ebe9f2; border-radius: 18px; background: #fff; }
.companion-panel-title { display: flex; align-items: center; gap: 12px; margin-bottom: 18px; }
.companion-title-icon { display: grid; place-items: center; width: 40px; height: 40px; border-radius: 12px; color: #7559bd; background: #f2effb; }
.companion-panel-title h2, .companion-panel-title p { margin: 0; }
.companion-panel-title h2, .companion-list-heading h2 { font-size: 16px; }
.companion-panel-title p { margin-top: 4px; color: #898795; font-size: 12px; }
.companion-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; }
.companion-form label { display: grid; gap: 7px; color: #595765; font-size: 12px; font-weight: 600; }
.companion-form input:not([type=file]), .companion-form select { width: 100%; height: 40px; padding: 0 11px; border: 1px solid #e6e4ed; border-radius: 9px; background: #fff; color: #34323d; font: inherit; font-weight: 400; }
.companion-file-label { position: relative; display: flex !important; flex-direction: row; align-items: center; min-height: 40px; padding: 0 11px; border: 1px dashed #d8d2e9; border-radius: 9px; color: #6e5a9d !important; }
.companion-file-label input { position: absolute; inset: 0; width: 100%; opacity: 0; cursor: pointer; }
.companion-file-label small { overflow: hidden; color: #8b8895; font-weight: 400; text-overflow: ellipsis; white-space: nowrap; }
.companion-import-button { justify-self: start; min-height: 40px; }
.companion-format { margin-top: 14px; color: #777482; font-size: 12px; }
.companion-format summary { cursor: pointer; }
.companion-format p { max-width: 850px; line-height: 1.8; }
.companion-list-heading { display: flex; align-items: center; gap: 10px; }
.companion-list-heading h2 { margin: 0; }
.companion-list-heading span { color: #96939f; font-size: 12px; }
.companion-profile-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
.companion-profile-card { position: relative; min-height: 175px; overflow: hidden; border: 1px solid #ebe9f2; border-radius: 16px; background: #fff; }
.companion-profile-open { display: flex; width: 100%; height: 100%; min-height: 175px; padding: 18px; flex-direction: column; align-items: flex-start; gap: 8px; border: 0; background: none; color: #34323d; text-align: left; cursor: pointer; }
.companion-avatar { display: grid; place-items: center; width: 38px; height: 38px; border-radius: 50%; color: #b35f7d; background: #fbecf1; }
.companion-profile-open strong { font-size: 15px; }
.companion-profile-open small { color: #908d99; font-size: 12px; }
.companion-card-action { display: flex; align-items: center; gap: 6px; margin-top: auto; color: #7c63bd; font-size: 12px; }
.companion-delete { position: absolute; top: 12px; right: 12px; display: grid; place-items: center; width: 30px; height: 30px; border: 0; border-radius: 8px; color: #9a96a3; background: transparent; cursor: pointer; }
.companion-delete:hover { color: #b8434b; background: #fff0f0; }
.companion-empty { display: flex; min-height: 155px; flex-direction: column; align-items: center; justify-content: center; gap: 8px; border: 1px dashed #dedbe7; border-radius: 16px; color: #8d8997; text-align: center; font-size: 13px; }
.companion-empty strong { color: #5d5a66; }
.companion-empty span { max-width: 420px; }
.companion-workspace { display: grid; grid-template-columns: 230px minmax(0, 1fr); min-height: 580px; overflow: hidden; border: 1px solid #ebe9f2; border-radius: 18px; background: #fff; }
.companion-session-list { display: flex; padding: 15px; flex-direction: column; gap: 6px; border-right: 1px solid #efedf3; background: #fbfaff; }
.companion-session-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; color: #5a5764; font-size: 13px; }
.companion-session-item { overflow: hidden; padding: 10px; border: 0; border-radius: 8px; background: transparent; color: #777482; text-align: left; text-overflow: ellipsis; white-space: nowrap; cursor: pointer; }
.companion-session-item.selected, .companion-session-item:hover { color: #7259ac; background: #f0edf8; }
.companion-persona-button { display: flex; align-items: center; justify-content: center; gap: 7px; margin-top: auto; padding: 10px 7px; border: 1px solid #e8e2f4; border-radius: 9px; color: #7055b0; background: #fff; font-size: 12px; cursor: pointer; }
.companion-persona-button:disabled { opacity: .6; cursor: wait; }
/* 语义记忆索引：显示已建向量条数，并提供一次性重建 */
.companion-index-row { display: flex; align-items: center; justify-content: space-between; gap: 6px; margin-top: 8px; padding: 6px 8px; border-radius: 8px; background: #f7f5fc; }
.companion-index-label { color: #6b6480; font-size: 10px; }
.companion-index-label b { color: #4c3f78; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.companion-index-hint { display: block; margin-top: 5px; color: #a9a4b4; font-size: 9px; line-height: 1.45; }
.companion-chat { display: grid; min-width: 0; grid-template-rows: auto minmax(250px, 1fr) auto auto; }
.companion-chat-head { display: flex; align-items: center; gap: 10px; padding: 14px 18px; border-bottom: 1px solid #efedf3; }
.companion-chat-head .companion-avatar { width: 34px; height: 34px; }
.companion-chat-head strong, .companion-chat-head small { display: block; }
.companion-chat-head strong { font-size: 13px; }
.companion-chat-head small { margin-top: 3px; color: #9995a1; font-size: 11px; }
.simulation-tag { margin-left: auto; padding: 5px 8px; border-radius: 20px; color: #8c6280; background: #fbf0f5; font-size: 10px; }
.companion-messages { display: flex; max-height: 55vh; min-height: 280px; padding: 18px; flex-direction: column; gap: 18px; overflow: auto; }
.companion-chat-empty { display: flex; flex: 1; flex-direction: column; align-items: center; justify-content: center; gap: 10px; color: #8d8997; text-align: center; font-size: 12px; }
.companion-chat-empty strong { color: #5d5a66; font-size: 14px; }
.companion-chat-empty span { max-width: 360px; line-height: 1.7; }
.companion-chat-orb { display: grid; place-items: center; width: 48px; height: 48px; border-radius: 50%; color: #b35f7d; background: #fbecf1; }
.companion-message { display: flex; gap: 9px; max-width: 88%; }
.companion-message-user { align-self: flex-end; flex-direction: row-reverse; }
.companion-message-avatar { display: grid; flex: 0 0 28px; place-items: center; width: 28px; height: 28px; border-radius: 50%; color: #a25d78; background: #fbecf1; }
.companion-message-user .companion-message-avatar { color: #6556a1; background: #efedfa; }
.companion-message-body { min-width: 0; }
.companion-message-meta { display: flex; align-items: center; gap: 8px; margin-bottom: 5px; color: #87838f; font-size: 10px; }
.companion-message-user .companion-message-meta { justify-content: flex-end; }
.companion-message-body p { margin: 0; padding: 10px 12px; border-radius: 3px 12px 12px; background: #f8f6fb; color: #4c4955; font-size: 13px; line-height: 1.7; white-space: pre-wrap; overflow-wrap: anywhere; }
.companion-message-user .companion-message-body p { border-radius: 12px 3px 12px 12px; color: #fff; background: #7965bb; }
.companion-typing { display: flex; align-items: center; gap: 4px; padding: 12px; }
.companion-typing i { width: 5px; height: 5px; border-radius: 50%; background: #aa9acb; animation: companionPulse 1s infinite alternate; }
.companion-typing i:nth-child(2) { animation-delay: .2s; }
.companion-typing i:nth-child(3) { animation-delay: .4s; }
.companion-composer { display: flex; align-items: flex-end; gap: 10px; margin: 0 18px; padding: 10px; border: 1px solid #e7e4ee; border-radius: 12px; }
.companion-composer textarea { flex: 1; resize: vertical; min-height: 42px; max-height: 140px; border: 0; outline: 0; color: #403d49; font: inherit; font-size: 13px; }
.companion-composer .send-button { display: grid; flex: 0 0 34px; place-items: center; width: 34px; height: 34px; border: 0; border-radius: 9px; color: #fff; background: #7965bb; cursor: pointer; }
.companion-composer .send-button:disabled { opacity: .45; cursor: not-allowed; }
.companion-chat-disclaimer { margin: 7px 18px 12px; color: #9995a1; font-size: 10px; text-align: center; }
.spin { animation: companionSpin 1s linear infinite; }
@keyframes companionSpin { to { transform: rotate(360deg); } }
@keyframes companionPulse { to { opacity: .35; transform: translateY(-3px); } }
@media (max-width: 760px) {
  .companion-form { grid-template-columns: 1fr; }
  .companion-workspace { grid-template-columns: 1fr; }
  .companion-session-list { flex-direction: row; flex-wrap: wrap; border-right: 0; border-bottom: 1px solid #efedf3; }
  .companion-session-heading { width: 100%; }
  .companion-persona-button { margin: 0 0 0 auto; }
  .companion-messages { max-height: 48vh; }
  .companion-heading { align-items: flex-start; flex-direction: column; }
}
</style>
