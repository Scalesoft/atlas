<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import type { MarkdownPlugin, MarkdownRichValue } from '../core'
import type { MarkdownSectionReviewSource } from '../features/review/types'
import { canEditSection, sectionActions } from '../features/review/types'
import { useSectionReview } from '../features/review/useSectionReview'
import MarkdownEditor from './MarkdownEditor.vue'
import MarkdownRenderer from './MarkdownRenderer.vue'
import MarkdownDocumentHistory from './MarkdownDocumentHistory.vue'
import MarkdownReviewModes from './MarkdownReviewModes.vue'
import MarkdownSectionMenu from './MarkdownSectionMenu.vue'
import { reviewStrings } from '../ui/strings/review'
import '../styles/section-review.css'

const props = defineProps<{ source: MarkdownSectionReviewSource; actor: string; plugins?: readonly MarkdownPlugin[] }>()
const { document: state, access, drafts, failures, error, busy, dirty, status, refresh, edit, flush, decide, add, retry, discard } = useSectionReview(props.source)
const view = ref<'document' | 'changes' | 'history'>('document'), showPending = ref(true)
const active = ref<string>(), panelId = useId()
const openMenu = ref<string>()
const empty: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } }
const labels = { approved: reviewStrings.statusApproved, draft: reviewStrings.statusDraft, pending: reviewStrings.statusPending }
const sections = computed(() => {
  if (!state.value) return []
  if (!showPending.value) return state.value.approvedOrder.flatMap(id => {
    const section = state.value!.sections.find(section => section.id === id)
    return section?.approved ? [{ ...section, working: section.approved, status: 'approved' as const, deleted: false }] : []
  })
  const missing = [...drafts.value].filter(([id]) => !state.value!.sections.some(section => section.id === id)).map(([, draft]) => draft.section)
  return [...state.value.sections, ...missing]
})
watch(() => props.source, () => { throw new Error('Remount MarkdownSectionReview with a new key when changing its source or authentication context.') })
watch([view, showPending], () => { openMenu.value = undefined })
watch(sections, value => { if (!value.some(section => section.id === openMenu.value)) openMenu.value = undefined })
function focus(id: string) { if (active.value && active.value !== id) void flush(active.value); active.value = id }
function blur(event: FocusEvent, id: string) {
  if ((event.currentTarget as HTMLElement).contains(event.relatedTarget as Node)) return
  // Moving to another section or outside the editor sends the latest saved content for review.
  if (active.value === id) { active.value = undefined; void flush(id) }
}
async function newSection() {
  if (active.value) await flush(active.value)
  await add(state.value?.sections.at(-1)?.id ?? null, empty)
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value || busy.value) { event.preventDefault(); event.returnValue = '' } }
onMounted(() => window.addEventListener('beforeunload', beforeUnload))
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
</script>
<template>
  <section class="md-section-review" :data-mode="view" :aria-label="reviewStrings.documentAriaLabel">
    <MarkdownReviewModes v-model="view" :panel-id="panelId" />
    <div v-if="error" role="alert" class="md-review-error">{{ error }} <button type="button" class="bb" :disabled="busy" @click="refresh">{{ reviewStrings.refresh }}</button></div>
    <div :id="panelId" role="tabpanel" :aria-labelledby="`${panelId}-${view}`">
      <MarkdownDocumentHistory v-if="view === 'history'" :source="source.history" :plugins="plugins" />
      <template v-else>
        <div class="md-review-view-options"><span role="status" data-testid="section-operation-status">{{ status }}</span><label><input v-model="showPending" type="checkbox">{{ reviewStrings.unapprovedChangesLabel }}</label></div>
        <div class="md-review-sections">
          <section v-for="(section, index) in sections" :key="section.id" class="md-review-section" :data-section-id="section.id" :data-state="section.status" @focusin="focus(section.id)" @focusout="blur($event, section.id)">
            <MarkdownSectionMenu :open="openMenu === section.id" :label="reviewStrings.sectionLabel(index + 1, labels[section.status])"
              :author="section.author" :revision="section.revision" :actions="showPending ? sectionActions(section, actor, access) : []"
              :disabled="busy || drafts.has(section.id) || failures.has(section.id)"
              @update:open="value => { if (value) openMenu = section.id; else if (openMenu === section.id) openMenu = undefined }"
              @decide="decide(section.id, $event)" />
            <div v-if="view === 'changes' && section.status !== 'approved' && section.approved" class="md-review-before">
              <small>{{ reviewStrings.publishedVersionLabel }}</small>
              <!-- @vue-generic {'rich'} -->
              <MarkdownRenderer :value="section.approved" :plugins="plugins" profile="rich" :ariaLabel="reviewStrings.publishedVersionAriaLabel" />
            </div>
            <p v-if="section.deleted" class="md-review-deletion">{{ reviewStrings.sectionDeletionProposed }}</p>
            <!-- @vue-generic {'rich'} -->
            <MarkdownEditor v-if="showPending && view === 'document' && !section.deleted && canEditSection(section, actor, access)" :model-value="drafts.get(section.id)?.value ?? section.working" :plugins="plugins" profile="rich" :fullscreenable="false" :ariaLabel="reviewStrings.sectionContentAriaLabel(index + 1)" @update:model-value="edit(section.id, $event)" />
            <!-- @vue-generic {'rich'} -->
            <MarkdownRenderer v-else :value="showPending ? drafts.get(section.id)?.value ?? section.working : section.working" :plugins="plugins" profile="rich" :ariaLabel="reviewStrings.sectionPreviewAriaLabel(index + 1)" />
            <div v-if="failures.has(section.id)" class="md-review-recovery" role="alert"><p>{{ failures.get(section.id) }}</p><button type="button" class="bb" :disabled="busy" @click="retry(section.id)">{{ reviewStrings.retrySave }}</button><details><summary>{{ reviewStrings.keptLocalEditsSummary }}</summary><textarea readonly :value="JSON.stringify(drafts.get(section.id)?.value, null, 2)" :aria-label="reviewStrings.localEditsBackupAriaLabel" /><button type="button" class="bb" :disabled="busy" @click="discard(section.id)">{{ reviewStrings.discardLocalEditsAndReload }}</button></details></div>
          </section>
          <button v-if="showPending && view === 'document' && (access.canContribute || access.canEdit)" class="md-review-add" type="button" :disabled="busy" @click="newSection">{{ reviewStrings.addSection }}</button>
        </div>
      </template>
    </div>
  </section>
</template>
