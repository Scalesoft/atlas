<script setup lang="ts">
import { onBeforeUnmount, onErrorCaptured, ref, shallowRef, watch } from 'vue'
import type { MarkdownDocumentSchema } from '../core/types'
import type { MarkdownPlugin } from '../core/plugins/types'
import type { RecoveryScope } from '../collab/recovery/types'
import { cloneJournal, type DurableJournalStore, type JournalRecord } from '../collab/recovery/durableJournal'
import { normalizeRecoveryScope } from '../collab/recovery/indexedDbRecoveryStore'
import { prepareDurableRecovery } from '../collab/recovery/durablePreview'
import { preflight, type MarkdownRecoveryPreview } from '../collab/recovery/preview'
import MarkdownRenderer from '../vue/MarkdownRenderer.vue'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import { collaborationStrings } from '../ui/strings/collaborationAddons'

const props = defineProps<{ store: DurableJournalStore; scope: RecoveryScope; schema: MarkdownDocumentSchema;
  fragment?: string; plugins?: readonly MarkdownPlugin[]; activeBranchId?: string; resourcePolicy?: MarkdownResourcePolicy }>()
const emit = defineEmits<{ resume: [branchId: string] }>()
const rows = shallowRef<JournalRecord[]>([]), selected = shallowRef<JournalRecord | null>(null)
const preview = shallowRef<MarkdownRecoveryPreview | null>(null), error = ref(''), busy = ref(false), confirmDiscard = ref(false)
let generation = 0, abort: AbortController | undefined
function cancel() { ++generation; abort?.abort(); abort = undefined; selected.value = null; preview.value = null; confirmDiscard.value = false; busy.value = false }
async function refresh() {
  cancel(); error.value = ''; rows.value = []; const current = generation; busy.value = true
  try {
    const scope = normalizeRecoveryScope(props.scope)
    const result = await props.store.list(scope).then(values => {
      if (current !== generation) return []
      if (!Array.isArray(values) || values.length > 4096) throw new Error(collaborationStrings.invalidBranchList)
      preflight(values, 64 * 1024 * 1024, 1_000_000, 256)
      return values.map(value => {
        const row = cloneJournal(value)
        if (JSON.stringify(row.scope) !== JSON.stringify(scope)) throw new Error(collaborationStrings.branchScopeMismatch)
        return row
      })
    })
    if (current === generation) rows.value = result
  }
  catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : String(cause) }
  finally { if (current === generation) busy.value = false }
}
async function inspect(row: JournalRecord) {
  cancel(); selected.value = row; error.value = ''; busy.value = true; abort = new AbortController(); const current = generation
  try {
    const result = await prepareDurableRecovery({ store: props.store, scope: props.scope, branchId: row.branchId,
      revision: row.revision, epoch: row.epoch, schema: props.schema, fragment: props.fragment ?? 'default', plugins: props.plugins, signal: abort.signal })
    if (current === generation) preview.value = result
  } catch (cause) { if (current === generation) error.value = cause instanceof Error ? cause.message : String(cause) }
  finally { if (current === generation) busy.value = false }
}
function download(kind: 'json' | 'md') {
  if (!preview.value || !selected.value) return
  const content = kind === 'json' ? JSON.stringify(preview.value.projection.rich, null, 2) : preview.value.projection.markdown
  const url = URL.createObjectURL(new Blob([content], { type: kind === 'json' ? 'application/json' : 'text/markdown;charset=utf-8' }))
  try { const link = document.createElement('a'); link.href = url; link.download = `recovery-${selected.value.branchId}.${kind}`; link.click() }
  finally { setTimeout(() => URL.revokeObjectURL(url), 0) }
}
async function discard() {
  const row = selected.value
  if (!row || row.branchId === props.activeBranchId) return
  if (!confirmDiscard.value) { confirmDiscard.value = true; return }
  const current = generation; busy.value = true
  try { await props.store.discard(props.scope, row.branchId, row.revision, row.owner); if (current === generation) await refresh() }
  catch (cause) { if (current === generation) { error.value = cause instanceof Error ? cause.message : String(cause); confirmDiscard.value = false } }
  finally { if (current === generation) busy.value = false }
}
watch([() => props.scope, () => props.store, () => props.schema, () => props.fragment, () => props.plugins, () => props.resourcePolicy], refresh, { immediate: true, deep: true })
onBeforeUnmount(cancel)
onErrorCaptured(cause => { error.value = cause instanceof Error ? cause.message : String(cause); preview.value = null; return false })
</script>

<template>
  <section class="md-recovery-panel" :aria-label="collaborationStrings.recoveryPanelAriaLabel" :aria-busy="busy">
    <p>{{ collaborationStrings.recoveryIntro }}</p>
    <button type="button" class="bb" :disabled="busy" @click="refresh">{{ collaborationStrings.loadBranches }}</button>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="!busy && !rows.length">{{ collaborationStrings.noBranches }}</p>
    <ul>
      <li v-for="row in rows" :key="row.branchId">
        <button type="button" class="bb" :disabled="busy" @click="inspect(row)">{{ collaborationStrings.inspectBranch(row.branchId.slice(0, 8)) }}</button>
        <span>{{ collaborationStrings.branchSummary(row.revision, row.pending.length, row.branchId === activeBranchId) }}</span>
      </li>
    </ul>
    <div v-if="selected">
      <p>{{ collaborationStrings.selectedBranchSummary(selected.branchId, selected.epoch) }}</p>
      <button type="button" class="bb" @click="cancel">{{ collaborationStrings.closePreview }}</button>
      <template v-if="preview">
        <div class="md-recovery-actions" role="group" :aria-label="collaborationStrings.branchActionsAriaLabel">
          <button type="button" class="bb" @click="download('json')">{{ collaborationStrings.downloadJson }}</button>
          <button type="button" class="bb" @click="download('md')">{{ collaborationStrings.downloadMarkdown }}</button>
          <button type="button" class="bb" :disabled="busy || selected.branchId === activeBranchId" @click="emit('resume', selected.branchId)">{{ collaborationStrings.resumeBranch }}</button>
          <button type="button" class="bb" :disabled="busy || selected.branchId === activeBranchId" @click="discard">{{ confirmDiscard ? collaborationStrings.confirmDiscardBranch : collaborationStrings.discardBranch }}</button>
        </div>
        <p v-if="preview.projection.markdownLossy">{{ collaborationStrings.markdownLossyNotice }}</p>
        <!-- @vue-generic {'rich'} -->
        <MarkdownRenderer profile="rich" :value="preview.projection.rich" :plugins="plugins" :resource-policy="resourcePolicy" :ariaLabel="collaborationStrings.recoveryPreviewAriaLabel" />
      </template>
    </div>
  </section>
</template>

<style scoped>
.md-recovery-panel { padding: 1rem; border: 1px solid var(--md-border, var(--line, #d4e4e7)); border-radius: .5rem; color: var(--md-text, var(--text, #102f36)); background: var(--md-bg, var(--paper, #fffefe)); font-family: var(--font-sans, sans-serif); overflow-wrap: anywhere; }
.md-recovery-panel ul { padding-left: 1.25rem; }
.md-recovery-panel li { margin: .5rem 0; }
.md-recovery-panel li span { margin-left: .5rem; }
.md-recovery-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin: .75rem 0; }
</style>
