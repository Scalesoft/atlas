<script setup lang="ts">
/**
 * The corner card that explains an interrupted collaboration session.
 *
 * It is an overlay, never a block in the document flow. An earlier version inserted itself above
 * the content, so every reconnect — and a shared session reconnects often — pushed the whole
 * document down by its own height and pulled it back a second later. Connection feedback must
 * never move the text somebody is reading, so the card floats in the editor's top-right corner
 * and the live region that announces it is a zero-size layer.
 *
 * `useConnectionCard` decides when it appears; this component only draws it.
 */
import { computed } from 'vue'
import { MarkdownIcon } from '../icons'
import { markdownStrings } from './strings'
import { collaborationStrings } from './strings/collaboration'
import { connectionPresentation } from './connectionPresentation'
import { useConnectionCard } from './useConnectionCard'
import type { MarkdownEditorState } from '../core/types'

const props = defineProps<{ state: MarkdownEditorState; compact?: boolean }>()

const presentation = computed(() => connectionPresentation(props.state))
const visible = useConnectionCard(() => presentation.value)
const tone = computed(() => presentation.value.level === 'failed' ? 'error'
  : presentation.value.level === 'connecting' ? 'waiting' : 'warning')
</script>

<template>
  <div class="md-collaboration-live" role="status" aria-live="polite" aria-atomic="true">
    <div v-if="visible" class="md-collaboration-notice" :class="[{ 'is-compact': compact }, `tone-${tone}`]"
      :data-state="state.connection" :data-level="presentation.level" data-testid="collaboration-notice">
      <span class="md-collaboration-notice-icon" aria-hidden="true">
        <MarkdownIcon v-if="presentation.busy" class="md-collaboration-spinner" name="loader-circle" :size="18" />
        <MarkdownIcon v-else name="lock" :size="18" />
      </span>
      <div class="md-collaboration-notice-copy">
        <strong>{{ presentation.label }}</strong>
        <p>{{ presentation.detail }}</p>
        <p v-if="state.pendingChanges > 0" class="md-collaboration-pending">{{ collaborationStrings.pendingChangesNotice }}</p>
      </div>
      <span v-if="!state.editable && presentation.level !== 'connecting'" class="md-collaboration-readonly">{{ markdownStrings.readOnly }}</span>
    </div>
  </div>
</template>

<style scoped>
/*
 * A row in the shell's connection overlay column, contributing nothing when there is no card.
 * `.md-editor` is the positioned ancestor of that column, so nothing here can move the document.
 */
/* A plain element, not `display: contents`: a live region needs a box to stay announceable. */
.md-collaboration-live { display: flex; flex-direction: column; align-items: flex-end; }
.md-collaboration-notice {
  --notice-tone: var(--chip-amber-fg, #955109);
  display: flex; align-items: flex-start; gap: 12px; width: max-content; max-width: min(340px, 78vw); padding: 11px 13px;
  border: 1px solid color-mix(in srgb, var(--notice-tone) 24%, var(--line, #dde4e6)); border-radius: 12px;
  background: color-mix(in srgb, var(--notice-tone) 5%, var(--paper, #fff)); color: var(--text, #243943);
  box-shadow: 0 12px 34px rgb(14 56 66 / 14%);
  font-family: var(--font-sans, inherit); font-size: 13px; line-height: 1.5; overflow-wrap: anywhere;
  animation: md-collaboration-enter var(--md-motion-menu) var(--md-motion-ease);
}
.tone-waiting { --notice-tone: var(--accent, #28889b); }
.tone-error { --notice-tone: var(--chip-red-fg, #a62d38); }
.md-collaboration-notice-icon { display: grid; place-items: center; width: 30px; height: 30px; flex: 0 0 30px; border-radius: 9px; background: color-mix(in srgb, var(--notice-tone) 9%, transparent); color: var(--notice-tone); }
.md-collaboration-notice-icon svg { width: 18px; height: 18px; stroke-linecap: round; stroke-linejoin: round; }
.md-collaboration-notice-copy { flex: 1; min-width: 0; }
.md-collaboration-notice-copy strong { display: block; font-size: 13px; font-weight: 600; }
.md-collaboration-notice-copy p { margin: 2px 0 0; color: var(--text-dim, #60737c); font-size: 12px; }
.md-collaboration-notice-copy .md-collaboration-pending { margin-top: 6px; color: var(--notice-tone); }
.md-collaboration-readonly { flex-shrink: 0; padding: 3px 8px; border: 1px solid color-mix(in srgb, var(--notice-tone) 20%, transparent); border-radius: 99px; color: var(--notice-tone); font-size: 10px; font-weight: 600; }
.md-collaboration-spinner { animation: md-collaboration-spin var(--md-motion-spin) linear infinite; }
.is-compact { padding: 8px 10px; gap: 8px; border-radius: 8px; max-width: min(260px, 82vw); }
.is-compact .md-collaboration-notice-icon { width: 24px; height: 24px; flex-basis: 24px; }
.is-compact .md-collaboration-readonly { display: none; }
@keyframes md-collaboration-spin { to { transform: rotate(360deg); } }
@keyframes md-collaboration-breathe { 0%, 100% { opacity: 1; } 50% { opacity: .65; } }
@media (prefers-reduced-motion: no-preference) {
  .md-collaboration-notice[data-level="offline"] .md-collaboration-notice-icon svg {
    /* keep: functional (O7) — a slow, unobtrusive status indicator, not work in progress; it stands still under reduced motion */
    animation: md-collaboration-breathe 3.6s ease-in-out infinite;
  }
}
@keyframes md-collaboration-enter { from { opacity: 0; transform: translateY(calc(var(--md-motion-rise) * -1)); } }
@media (prefers-reduced-motion: reduce) {
  .md-collaboration-notice { animation: none; }
}
@media (max-width: 480px) { .md-collaboration-readonly { display: none; } }
</style>
