<script setup lang="ts">
import { ref, useId, watch, watchEffect } from 'vue'
import type { MarkdownSectionDecision } from '../features/review/types'
import { useAnchoredPopover } from '../ui/useAnchoredPopover'
import { CheckIcon, CloseIcon, UndoIcon, TrashIcon } from '../ui/icons'
import { reviewStrings } from '../ui/strings/review'
// controls.css and contextToolbar.css come with editor.css (the review's editor): imported here too, the lazy review chunk
// carried a second copy of them into the global stylesheet.

const props = defineProps<{
  open: boolean; label: string; author: string; revision: string
  actions: MarkdownSectionDecision[]; disabled: boolean
}>()
const emit = defineEmits<{ 'update:open': [open: boolean]; decide: [action: MarkdownSectionDecision] }>()
const trigger = ref<HTMLButtonElement | null>(null), popup = ref<HTMLElement | null>(null), id = useId()
const labels = { approve: reviewStrings.approveAction, reject: reviewStrings.rejectAction, withdraw: reviewStrings.withdrawAction, delete: reviewStrings.deleteAction }
const icons = { approve: CheckIcon, reject: CloseIcon, withdraw: UndoIcon, delete: TrashIcon }
function close(restore = false) {
  emit('update:open', false)
  if (restore) trigger.value?.focus({ preventScroll: true })
}
function choose(action: MarkdownSectionDecision) {
  if (props.disabled || !props.actions.includes(action)) return
  close(true)
  emit('decide', action)
}
// `right-start`, not the default `bottom-start`: sections stack tightly enough that a
// 4-item menu (36px each, D3's shared height) opening downward can reach past the very
// short gap into the next section's own trigger.
useAnchoredPopover(() => trigger.value, () => popup.value, () => {
  if (popup.value && !popup.value.contains(popup.value.ownerDocument.activeElement)) popup.value.focus({ preventScroll: true })
}, undefined, 'right-start')
// A remote revision or permission change invalidates the displayed decisions.
watch([() => props.revision, () => props.actions.join(',')], () => { if (props.open) close() })
watchEffect(onCleanup => {
  if (!props.open || !trigger.value) return
  const doc = trigger.value.ownerDocument
  const outside = (event: Event) => {
    const target = event.target as Node | null
    if (!trigger.value?.contains(target) && !popup.value?.contains(target)) close()
  }
  const keyboard = (event: KeyboardEvent) => {
    if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(true) }
    else if (event.key === 'Tab') {
      // Firefox advances focus before Vue removes the menu. Exclude its items
      // immediately so native Tab continues from the trigger into the document.
      if (popup.value) popup.value.hidden = true
      close(true)
    }
  }
  doc.addEventListener('pointerdown', outside, true)
  doc.addEventListener('focusin', outside)
  doc.addEventListener('keydown', keyboard, true)
  onCleanup(() => {
    doc.removeEventListener('pointerdown', outside, true)
    doc.removeEventListener('focusin', outside)
    doc.removeEventListener('keydown', keyboard, true)
  })
})
</script>
<template>
  <div class="md-review-section-menu" :data-open="open || undefined">
    <button ref="trigger" type="button" class="md-review-section-trigger" :aria-label="label" :title="`${label} · ${author}`"
      :aria-haspopup="actions.length ? 'menu' : undefined" :aria-expanded="actions.length ? open : undefined"
      :aria-controls="open ? id : undefined" :aria-disabled="!actions.length || undefined"
      @click="actions.length && emit('update:open', !open)" @keydown.down.prevent="actions.length && emit('update:open', true)">
      <span class="md-review-status-dot" aria-hidden="true" />
    </button>
    <div v-if="open" :id="id" ref="popup" class="md-review-actions md-context-popover md-context-menu md-context-menu-compact"
      role="menu" :aria-label="label" tabindex="-1">
      <div class="md-context-popover-body">
        <button v-for="action in actions" :key="action" type="button" role="menuitem" class="md-context-menu-item"
          :disabled="disabled" @click="choose(action)"><component :is="icons[action]" />{{ labels[action] }}</button>
      </div>
    </div>
  </div>
</template>
