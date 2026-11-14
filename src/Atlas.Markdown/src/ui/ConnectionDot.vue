<script setup lang="ts">
/**
 * The quiet corner of a shared document: one small dot that says how the link is doing, and a
 * detail panel that only appears on hover or keyboard focus.
 *
 * It is deliberately tiny and deliberately an overlay. Games settle on this shape for the same
 * reason an editor needs it — the indicator has to be reachable at a glance without ever moving
 * the thing the player (or writer) is looking at. It lives in the shell's overlay column, so
 * nothing it draws can take part in the document flow.
 */
import { computed, ref, useId, watchEffect } from 'vue'
import { MarkdownIcon } from '../icons'
import { SILENT_LEVELS, connectionPresentation } from './connectionPresentation'
import { collaborationStrings } from './strings/collaboration'
import type { MarkdownEditorState } from '../core/types'

const props = defineProps<{ state: MarkdownEditorState }>()
const detailId = useId()
const hovered = ref(false)
const focused = ref(false)
const pinned = ref(false)
const root = ref<HTMLElement | null>(null)
const dismissed = ref(false)
// Hover and keyboard focus each open the panel on their own; a click keeps it open for a reader
// who needs longer with the numbers than a hover allows.
const open = computed(() => !dismissed.value && (pinned.value || hovered.value || focused.value))
function dismiss() {
  pinned.value = false
  dismissed.value = true
}
function togglePinned() {
  if (pinned.value) dismiss()
  else { dismissed.value = false; pinned.value = true }
}
watchEffect(onCleanup => {
  const element = root.value
  if (!open.value || !element) return
  const doc = element.ownerDocument
  const outside = (event: Event) => {
    if (!event.composedPath().includes(element)) dismiss()
  }
  const escape = (event: KeyboardEvent) => {
    if (event.key !== 'Escape' || event.isComposing) return
    event.preventDefault()
    event.stopPropagation()
    dismiss()
  }
  doc.addEventListener('pointerdown', outside, true)
  doc.addEventListener('focusin', outside, true)
  doc.addEventListener('keydown', escape, true)
  onCleanup(() => {
    doc.removeEventListener('pointerdown', outside, true)
    doc.removeEventListener('focusin', outside, true)
    doc.removeEventListener('keydown', escape, true)
  })
})
const presentation = computed(() => connectionPresentation(props.state))
const shown = computed(() => !SILENT_LEVELS.has(presentation.value.level))
const peerNames = computed(() => props.state.peers.map(peer => peer.name).join(', '))
const latency = computed(() => {
  const value = props.state.link?.latencyMs
  if (value === null || value === undefined) return collaborationStrings.notMeasuredYet
  return value < 1000 ? `${Math.round(value)} ms` : `${(value / 1000).toFixed(1)} s`
})
const outages = computed(() => {
  const drops = props.state.link?.drops ?? 0
  if (drops === 0) return collaborationStrings.noOutages
  return collaborationStrings.outageCount(drops)
})
</script>

<template>
  <div v-if="shown" ref="root" class="md-link-dot" :data-level="presentation.level"
    data-testid="connection-dot" @mouseenter="hovered = true; dismissed = false" @mouseleave="hovered = false">
    <button type="button" class="md-link-dot-button" :aria-label="collaborationStrings.connectionStatusAriaLabel(presentation.label)"
      :aria-expanded="open" :aria-controls="detailId"
      @focus="focused = true; dismissed = false" @blur="focused = false" @click="togglePinned">
      <span class="md-link-dot-mark" aria-hidden="true">
        <MarkdownIcon :name="presentation.bars === 0 ? 'link-meter-off' : 'link-meter'" :size="14" />
      </span>
    </button>
    <div v-show="open" :id="detailId" class="md-link-dot-detail" data-testid="connection-detail">
      <strong>{{ presentation.label }}</strong>
      <p>{{ presentation.detail }}</p>
      <dl>
        <div><dt>{{ collaborationStrings.responseTimeLabel }}</dt><dd>{{ latency }}</dd></div>
        <div><dt>{{ collaborationStrings.reliabilityLabel }}</dt><dd>{{ outages }}</dd></div>
        <div><dt>{{ collaborationStrings.pendingChangesLabel }}</dt><dd>{{ state.pendingChanges }}</dd></div>
        <div><dt>{{ collaborationStrings.collaboratorsLabel }}</dt><dd>{{ state.peers.length === 0 ? collaborationStrings.noOtherCollaborators : peerNames }}</dd></div>
      </dl>
    </div>
  </div>
</template>

<style scoped>
/*
 * A row in the shell's connection column, not a free-floating badge: the detail panel is laid
 * out under the dot so the card below is pushed down instead of being covered.
 */
.md-link-dot {
  --dot-tone: var(--chip-green-fg, #2f7d5c);
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 6px;
  line-height: 0;
}
.md-link-dot[data-level="weak"] { --dot-tone: var(--chip-amber-fg, #955109); }
.md-link-dot[data-level="connecting"], .md-link-dot[data-level="reconnecting"] { --dot-tone: var(--chip-amber-fg, #955109); }
.md-link-dot[data-level="offline"] { --dot-tone: var(--text-dim, #60737c); }
.md-link-dot[data-level="failed"] { --dot-tone: var(--chip-red-fg, #a62d38); }

.md-link-dot-button {
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  padding: 0;
  border: 0;
  border-radius: 50%;
  background: transparent;
  color: var(--dot-tone);
  cursor: pointer;
  /* A healthy link should be findable but never demand attention. */
  opacity: .32;
  transition: opacity var(--md-motion-fast), color var(--md-motion-fast);
}
.md-link-dot:hover .md-link-dot-button,
.md-link-dot-button:focus-visible { opacity: 1; }
.md-link-dot:not([data-level="live"]) .md-link-dot-button { opacity: 1; }

/* Segments dim from the top down, so the meter reads as strength without swapping drawings.
   Dim, never invisible: a lone lit segment would stop reading as a meter at this size. */
.md-link-dot-mark :deep(path) { opacity: .3; }
.md-link-dot[data-level="live"] .md-link-dot-mark :deep(path) { opacity: 1; }
.md-link-dot[data-level="weak"] .md-link-dot-mark :deep(path:nth-child(-n+2)) { opacity: 1; }
.md-link-dot:is([data-level="connecting"], [data-level="reconnecting"]) .md-link-dot-mark :deep(path:nth-child(1)) { opacity: 1; }
.md-link-dot:is([data-level="offline"], [data-level="failed"]) .md-link-dot-mark :deep(path:nth-child(4)) { opacity: 1; }

.md-link-dot:is([data-level="connecting"], [data-level="reconnecting"]) .md-link-dot-mark {
  animation: md-link-sweep var(--md-motion-pulse) var(--md-motion-ease-io) infinite;
}
@keyframes md-link-sweep { 0%, 100% { opacity: 1 } 50% { opacity: .45 } }

.md-link-dot-detail {
  width: max-content;
  max-width: min(280px, 80vw);
  padding: 10px 12px;
  border: 1px solid var(--line, #dde4e6);
  border-radius: 10px;
  background: var(--paper, #fff);
  box-shadow: 0 10px 30px rgb(14 56 66 / 14%);
  color: var(--text, #243943);
  font-family: var(--font-sans, inherit);
  font-size: 12px;
  line-height: 1.45;
  text-align: left;
}
.md-link-dot-detail strong { display: block; color: var(--dot-tone); font-size: 12px; }
.md-link-dot-detail p { margin: 2px 0 8px; color: var(--text-dim, #60737c); font-size: 11px; }
.md-link-dot-detail dl { display: grid; gap: 2px; margin: 0; }
.md-link-dot-detail dl div { display: flex; justify-content: space-between; gap: 14px; }
.md-link-dot-detail dt { color: var(--text-dim, #60737c); font-size: 11px; }
.md-link-dot-detail dd { margin: 0; font-size: 11px; font-variant-numeric: tabular-nums; }
</style>
