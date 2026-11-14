<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'
// The tiny `./source` entry: `mountSourceEditor` loads Monaco through its own cached import().
import { mountSourceEditor, type MarkdownSourceDiagnostic, type SourceEditor } from '../source'
import { markdownStrings } from '../ui/strings'
import { panelsStrings as t } from '../ui/strings/panels'
import { sourceStrings } from '../ui/strings/source'
import MarkdownDraftConfirmation from './MarkdownDraftConfirmation.vue'
import { markdownSourceDraftViewActivity, type MarkdownSourceDraft, type MarkdownSourceNoticeKind } from './useMarkdownSourceDraft'

// The „Zdroj“ view of a page (docs/page-source-editor.md): Monaco with protected blocks over a
// `useMarkdownSourceDraft` draft. Presentation only; the draft owns the session and the host save. A
// replaced `draft` prop remounts the editor on the new draft.
const props = withDefaults(defineProps<{ draft: MarkdownSourceDraft; ariaLabel?: string }>(), { ariaLabel: () => t.sourceAriaLabel })
// `escape`: Esc in the editor with nothing of its own to dismiss; a host may take it as leaving the view.
const emit = defineEmits<{ applied: []; escape: [] }>()

const NOTICES = computed<Record<MarkdownSourceNoticeKind, string>>(() => ({
  saved: t.noticeSaved,
  unchanged: t.noticeUnchanged,
  invalid: t.noticeInvalid,
  conflict: t.noticeConflict,
  'live-edits': t.noticeLiveEdits,
  changed: t.noticeChanged,
  error: t.noticeError,
  unavailable: t.noticeUnavailable,
}))

const host = ref<HTMLElement>()
const editorReady = ref(false)
const mounting = ref(false)
const editorError = ref('')
const slow = ref(false)
const confirmReload = ref(false)
// Read through the prop on every access: a host may hand over a different draft.
const text = computed(() => props.draft.text.value)
const status = computed(() => props.draft.status.value)
const notice = computed(() => props.draft.notice.value)
const dirty = computed(() => props.draft.dirty.value)
const stale = computed(() => props.draft.stale.value)
const busy = computed(() => props.draft.busy.value)
const readonly = computed(() => props.draft.readonly.value)
const canApply = computed(() => props.draft.canApply.value)
const diagnostics = computed(() => props.draft.diagnostics.value)
const loading = computed(() => status.value === 'loading' || mounting.value)
// The one delayed loading status: session first, then the editor (its skeleton stays silent inside aria-busy).
const loadingMessage = computed(() => slow.value ? (status.value === 'loading' ? t.preparing : sourceStrings.loadingLabel) : '')
const alertKinds = new Set<MarkdownSourceNoticeKind>(['conflict', 'live-edits', 'changed', 'error', 'invalid', 'unavailable'])
const loadError = computed(() => status.value === 'error' ? t.loadFailed : '')
const errors = computed(() => diagnostics.value.filter(item => item.severity === 'error').length)
let editor: SourceEditor | undefined
let generation = 0
/** The generation whose mount owns `mounting`. */
let mountingToken = 0
let slowTimer: ReturnType<typeof setTimeout> | undefined
let viewActive: Ref<boolean> | undefined

const editorReadonly = () => readonly.value || busy.value
const readonlyMessage = () => busy.value ? t.saving : t.readOnly

function setViewActive(active: boolean) { if (viewActive) viewActive.value = active }
function bindViewActivity() {
  setViewActive(false)
  viewActive = markdownSourceDraftViewActivity(props.draft)
}
function onFocusOut(event: FocusEvent) {
  if (!host.value?.contains(event.relatedTarget as Node | null)) setViewActive(false)
}

function destroyEditor() {
  generation++
  editor?.destroy(); editor = undefined
  editorReady.value = false; setViewActive(false)
}

async function mountEditor() {
  // A mount of the current generation is already under way; a closed or replaced one no longer counts.
  if (editor || (mounting.value && mountingToken === generation) || status.value !== 'ready' || !host.value) return
  const token = ++generation
  const draft = props.draft
  mountingToken = token
  mounting.value = true; editorError.value = ''
  try {
    // Render `aria-busy` first: the skeleton then leaves the loading status to this panel.
    await nextTick()
    if (token !== generation || !host.value) return
    // Shows the real text at once (Monaco's metrics, no layout shift) and swaps in Monaco when it arrives.
    const mounted = await mountSourceEditor(host.value, {
      language: 'markdown', value: draft.text.value, ariaLabel: props.ariaLabel,
      readonly: editorReadonly(), readOnlyMessage: readonlyMessage(),
      protectedBlocks: draft.protectedBlocks.value,
      onChange: value => { draft.text.value = value },
      onEscape: () => emit('escape'),
    })
    if (token !== generation) { mounted.destroy(); return }
    editor = mounted
    // The draft may have been regenerated, and permissions or a save may have changed, while Monaco loaded.
    if (mounted.getValue() !== draft.text.value) mounted.setValue(draft.text.value, { protectedBlocks: draft.protectedBlocks.value })
    mounted.setReadonly(editorReadonly(), readonlyMessage())
    mounted.setDiagnostics(diagnostics.value)
    editorReady.value = true
    // Opening the source by its tab or its pressed switch button (or a control that went away) puts the caret into it once it
    // is there (UX ED-13), never away from a field the reader moved to meanwhile.
    const active = host.value?.ownerDocument.activeElement
    if (!active || active === host.value?.ownerDocument.body || active.closest('[role="tab"], [aria-pressed="true"]')) mounted.focus()
  } catch {
    if (token === generation) editorError.value = t.editorFailed
  } finally {
    // Always released, unless a newer mount already took it over.
    if (mountingToken === token) mounting.value = false
  }
}

async function start() {
  const draft = props.draft
  if (await draft.open() && draft === props.draft) { await nextTick(); await mountEditor() }
}
function retry() {
  if (status.value === 'error' || status.value === 'closed') void start()
  else void mountEditor()
}

watch(loading, value => {
  clearTimeout(slowTimer); slow.value = false
  if (value) slowTimer = setTimeout(() => { slow.value = true }, 300)
})
watch(status, value => {
  if (value === 'ready') void mountEditor()
  else if (value === 'closed') destroyEditor()
})
watch(() => props.draft, () => {
  destroyEditor(); bindViewActivity()
  editorError.value = ''; confirmReload.value = false
  void start()
})
watch(() => props.draft.revision.value, () => editor?.setValue(text.value, { protectedBlocks: props.draft.protectedBlocks.value }))
watch([readonly, busy], () => editor?.setReadonly(editorReadonly(), readonlyMessage()))
watch(diagnostics, items => editor?.setDiagnostics(items))

async function apply() {
  if (await props.draft.apply()) emit('applied')
  else if (errors.value) reveal(diagnostics.value.find(item => item.severity === 'error')!)
}
function reveal(item: MarkdownSourceDiagnostic) { editor?.revealLine(item.line) }
function requestReload() {
  if (dirty.value) confirmReload.value = true
  else void props.draft.reload()
}
function confirmDiscardAndReload() { confirmReload.value = false; void props.draft.reload() }

onMounted(() => { bindViewActivity(); void start() })
onBeforeUnmount(() => { clearTimeout(slowTimer); destroyEditor() })
</script>

<template>
  <!-- The workspace tab panel („Zdroj“) names this view; no second region here. -->
  <div class="md-source-panel">
    <div v-if="notice" class="md-source-notice" :class="'md-source-notice-' + notice.kind" :role="alertKinds.has(notice.kind) ? 'alert' : 'status'">
      <span>{{ NOTICES[notice.kind] }}<template v-if="notice.detail"> {{ notice.detail }}</template></span>
      <button v-if="(stale || notice.kind === 'unavailable') && !readonly" type="button" class="md-btn" :disabled="busy" @click="requestReload">{{ t.reloadCurrent }}</button>
    </div>
    <div class="md-source-body" :aria-busy="loading || undefined">
      <div ref="host" class="md-source-editor" @focusin="setViewActive(true)" @focusout="onFocusOut" />
      <p v-if="loading" class="md-source-status" role="status">{{ loadingMessage }}</p>
      <!-- Monaco failed (offline, CSP): the text stays readable and selectable. -->
      <pre v-if="editorError" class="md-source-fallback" tabindex="0" :aria-label="t.fallbackAriaLabel(ariaLabel)">{{ text }}</pre>
    </div>
    <div v-if="loadError || editorError" class="md-source-error" role="alert">
      <span>{{ loadError || editorError }}</span>
      <button type="button" class="md-btn" @click="retry">{{ markdownStrings.retry }}</button>
    </div>
    <ul v-if="diagnostics.length" class="md-source-diagnostics" :aria-label="t.diagnosticsLabel">
      <li v-for="(item, index) in diagnostics" :key="index" :class="'md-source-diagnostic-' + item.severity">
        <button type="button" @click="reveal(item)">
          <span class="md-source-diagnostic-kind">{{ item.severity === 'error' ? t.errorKind : t.warningKind }}</span>
          {{ t.diagnosticLine(item.line, item.message) }}
        </button>
      </li>
    </ul>
    <footer v-if="!readonly" class="md-source-actions">
      <span class="md-source-hint">{{ busy ? t.saving : dirty ? t.dirtyHint : t.cleanHint }}</span>
      <button type="button" class="md-btn" :disabled="!dirty || busy" @click="draft.discard()">{{ t.discardChanges }}</button>
      <button type="button" class="md-btn-primary" :disabled="!canApply || !editorReady" @click="apply">{{ markdownStrings.apply }}</button>
    </footer>
    <p v-else class="md-source-hint md-source-readonly">{{ t.readOnly }}</p>
    <MarkdownDraftConfirmation :open="confirmReload" :label="t.reloadCurrent" :message="t.reloadConfirmMessage"
      :discard-label="t.discardDraftAndLoad" @continue="confirmReload = false" @discard="confirmDiscardAndReload" />
  </div>
</template>

<style scoped>
.md-source-panel { height:100%; min-height:0; display:flex; flex-direction:column; color:var(--text,#102f36); background:var(--paper,#fff); font-family:var(--font-sans,inherit); }
.md-source-notice { display:flex; align-items:center; gap:12px; margin:0; padding:10px 20px; font-size:13px; line-height:1.45; border-bottom:1px solid var(--line,#d4e4e7);
  background:color-mix(in oklab, var(--amber,#b66b12) 14%, var(--paper,#fff)); }
.md-source-notice > span { flex:1; }
.md-source-notice-saved,.md-source-notice-unchanged { background:color-mix(in oklab, var(--green,#137b70) 12%, var(--paper,#fff)); }
.md-source-notice-error,.md-source-notice-invalid,.md-source-notice-conflict { background:color-mix(in oklab, var(--red,#b42318) 12%, var(--paper,#fff)); }
.md-source-body { position:relative; flex:1; min-height:0; }
.md-source-editor { position:absolute; inset:0; }
/* Same metrics as the editor skeleton and Monaco (13px/19px, gutter offset). */
.md-source-status { position:absolute; top:0; left:0; margin:0; padding:6px 12px; font-size:12px; line-height:1.4; color:var(--text-dim,#49676e); }
.md-source-fallback { position:absolute; inset:0; margin:0; padding:29px 12px 0 26px; overflow:auto; white-space:pre-wrap; overflow-wrap:anywhere;
  font:13px/19px var(--font-mono,ui-monospace,SFMono-Regular,Menlo,Consolas,monospace); color:var(--text,#102f36); background:var(--paper,#fff); }
.md-source-fallback:focus-visible { outline:var(--focus-ring,2px solid var(--accent,#58a9b9)); outline-offset:-2px; }
.md-source-error { display:flex; align-items:center; gap:12px; padding:10px 20px; font-size:13px; color:var(--red,#b42318); border-top:1px solid var(--line,#d4e4e7); }
.md-source-error > span { flex:1; }
.md-source-diagnostics { max-height:132px; overflow:auto; margin:0; padding:6px 12px; list-style:none; border-top:1px solid var(--line,#d4e4e7); font-size:12px; }
.md-source-diagnostics button { width:100%; display:flex; gap:8px; padding:5px 8px; border:0; border-radius:var(--radius-sm,6px); background:transparent; color:var(--text,#102f36); font:inherit; text-align:left; cursor:pointer; }
.md-source-diagnostics button:hover { background:var(--bg-3,#e2eef0); }
.md-source-diagnostics button:focus-visible { outline:var(--focus-ring,2px solid var(--accent,#58a9b9)); }
.md-source-diagnostic-kind { flex:none; font-weight:600; }
.md-source-diagnostic-error .md-source-diagnostic-kind { color:var(--red,#b42318); }
.md-source-diagnostic-warning .md-source-diagnostic-kind { color:var(--amber,#b66b12); }
.md-source-actions { display:flex; align-items:center; gap:8px; padding:12px 20px; border-top:1px solid var(--line,#d4e4e7); }
.md-source-hint { flex:1; font-size:12px; color:var(--text-dim,#49676e); }
.md-source-readonly { flex:none; margin:0; padding:12px 20px; border-top:1px solid var(--line,#d4e4e7); }
@media(max-width:600px) { .md-source-actions { flex-wrap:wrap; padding:10px; } .md-source-hint { flex-basis:100%; } .md-source-notice { flex-wrap:wrap; padding:10px; } }
</style>
