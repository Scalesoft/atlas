<script setup lang="ts">
import { computed } from "vue";
import StatusBadge from "./settings/StatusBadge.vue";
import type { StatusTone } from "./settings/types";
import t from "../i18n/components-c";

/**
 * The one state of saving a page or a board that saves by itself (Atlas 3.6-B): Ukládám… / Uloženo / Offline, uloží se po připojení
 * / Offline: psaní je pozastavené / Chyba. A component never says „uloženo“ on its own (3.6-0, rule 8); a failure says what went wrong
 * in `error`. Choose it where work saves in the background; a form saved by its own button keeps SaveBar.
 * Uloženo goes quiet by itself (a CSS animation, --atlas-motion-quiet-after): the chip fades to its icon and the word stays for
 * readers of the screen and as the title; the other states are what the reader needs to see and stay in full. A new state mounts a
 * new badge, so the next change brings the full chip back at once.
 */
export type SaveStateKind = "saving" | "saved" | "offline" | "paused" | "error";
const props = defineProps<{ state: SaveStateKind; error?: string }>();
const shown = (): Record<SaveStateKind, { tone: StatusTone; label: string; icon?: string }> => ({
  saving: { tone: "neutral", label: t.saveState.saving, icon: "loader-circle" },
  saved: { tone: "success", label: t.saveState.saved },
  offline: { tone: "warning", label: t.saveState.offline, icon: "triangle-alert" },
  // The browser is offline: the page editor stays read-only until a fresh join (allowOfflineEditing: false), so typing is paused.
  paused: { tone: "warning", label: t.saveState.paused, icon: "triangle-alert" },
  error: { tone: "danger", label: t.saveState.error, icon: "triangle-alert" },
});
const badge = computed(() => shown()[props.state]);
// The new status fades in, the old one goes at once. Not by CSS classes: Vue lays out the whole page to start a CSS leave, and every
// drop of a board card swapped Ukládám… for Uloženo (82–137 ms of forced layout on a large board).
function fadeIn(el: Element, done: () => void) { el.classList.add("save-state-in"); done(); }
</script>

<template>
  <span class="save-state" role="status" aria-live="polite" aria-atomic="true" :data-save-state="state" :title="state === 'error' ? error : state === 'saved' ? badge.label : undefined">
    <Transition :css="false" @enter="fadeIn">
      <StatusBadge :key="state" size="sm" :tone="badge.tone" :label="badge.label" :icon="badge.icon" />
    </Transition>
    <span v-if="state === 'error' && error" class="sr-only">{{ error }}</span>
  </span>
</template>

<style scoped>
.save-state { display: inline-flex; align-items: center; min-height: 22px; }
.save-state :deep([data-tone="neutral"] svg) { animation: atlas-spin var(--atlas-motion-spin) linear infinite; }
/* Uloženo: full for --atlas-motion-quiet-after, then the fill and the word fade out and the icon stays, muted. The word keeps its room (nothing moves)
   and stays in the page for screen readers; opacity never hides it from them. */
.save-state[data-save-state="saved"] :deep(.status-badge) { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both, save-state-quiet var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-quiet-after) both; }
.save-state[data-save-state="saved"] :deep(.status-badge > span) { animation: save-state-quiet-word var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-quiet-after) both; }
.save-state-in { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
@media (prefers-reduced-motion: reduce) { .save-state :deep([data-tone="neutral"] svg) { animation: none; } }
:global([data-atlas-reduced-motion] .save-state [data-tone="neutral"] svg) { animation: none; }
</style>
