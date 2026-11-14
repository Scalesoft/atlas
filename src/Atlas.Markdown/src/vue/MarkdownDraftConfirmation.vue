<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import { panelsStrings } from '../ui/strings/panels'

// Resolving an unsaved draft before a detail closes or switches (docs/openapi-detail-workspace.md):
// Continue editing / Discard / Apply. A native modal alert dialog: focus starts on the safe choice,
// Escape continues editing. Place it inside the workspace (its `confirmation` slot) so it keeps the
// document's theme tokens. Without `applyLabel` it asks only whether to discard.
const props = withDefaults(defineProps<{
  open: boolean
  /** Accessible name, e.g. „Rozpracované změny YAML“. */
  label: string
  message: string
  applyLabel?: string
  discardLabel?: string
  continueLabel?: string
  /** Disables the choices while Apply is in flight. */
  busy?: boolean
}>(), { discardLabel: () => panelsStrings.discardAndContinue, continueLabel: () => panelsStrings.continueEditing })
const emit = defineEmits<{ continue: []; discard: []; apply: [] }>()
const dialog = ref<HTMLDialogElement>()
const messageId = `${useId()}-message`
watch(() => props.open, async open => {
  await nextTick()
  const element = dialog.value
  if (!element) return
  if (open && !element.open) element.showModal()
  else if (!open && element.open) element.close()
}, { immediate: true })
onBeforeUnmount(() => { if (dialog.value?.open) dialog.value.close() })
</script>

<template>
  <dialog ref="dialog" class="md-draft-confirmation" role="alertdialog" :aria-label="label" :aria-describedby="messageId" :aria-busy="busy || undefined"
    @cancel.prevent="!busy && emit('continue')">
    <p :id="messageId">{{ message }}</p>
    <div class="md-draft-confirmation-actions">
      <button type="button" class="md-btn" :disabled="busy" @click="emit('continue')">{{ continueLabel }}</button>
      <button type="button" class="md-btn" :disabled="busy" @click="emit('discard')">{{ discardLabel }}</button>
      <button v-if="applyLabel" type="button" class="md-btn-primary" :disabled="busy" @click="emit('apply')">{{ applyLabel }}</button>
    </div>
  </dialog>
</template>

<style scoped>
.md-draft-confirmation { max-width:min(520px, calc(100vw - 32px)); padding:20px; border:1px solid var(--line,#d4e4e7); border-radius:var(--radius-lg,12px);
  color:var(--text,#102f36); background:var(--paper,#fff); box-shadow:var(--shadow-modal,0 20px 90px rgb(16 32 57 / .2)); font:inherit; }
.md-draft-confirmation::backdrop { background:var(--backdrop,rgb(16 28 48 / .56)); }
.md-draft-confirmation p { margin:0 0 16px; line-height:1.5; }
.md-draft-confirmation-actions { display:flex; flex-wrap:wrap; justify-content:flex-end; gap:8px; }
</style>
