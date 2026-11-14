<script setup lang="ts">
import t from "../i18n/components-a";
import { ref, watch } from "vue";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import LoadingNote from "./settings/LoadingNote.vue";

/**
 * A region whose content loads and later refreshes without moving the page. Until the first result is
 * ready it holds a LoadingSkeleton of the expected size; afterwards it keeps the shown content in place
 * during a refresh (search, filter, retry), dims it and runs a thin progress bar that takes no layout
 * space. Both cues appear only after a short delay, so fast responses do not flash.
 * `note` adds a spinner line above the content whose space is reserved from the start; use it where no
 * existing row (a count, a heading) can carry an inline LoadingNote.
 * Use LoadingSkeleton alone for a placeholder that never refreshes, LoadingNote for a one-line status.
 */
const props = withDefaults(defineProps<{ loading: boolean; ready: boolean; rows?: number; rowHeight?: string; label?: string; note?: boolean }>(), {
  rows: 4,
  note: false,
  rowHeight: undefined,
  label: () => t.loading,
});
// Content that takes the skeleton's place fades in instead of replacing it in one frame; content that was ready
// from the start stands at once, and a refresh keeps the element (and so never replays the fade).
const waited = ref(!props.ready);
watch(() => props.ready, (ready) => { if (!ready) waited.value = true; });
</script>

<template>
  <div class="loading-region" :aria-busy="loading" :data-refreshing="(ready && loading) || undefined">
    <LoadingNote v-if="note" class="loading-region-note" :active="ready && loading" :label="label" />
    <LoadingSkeleton v-if="!ready" :rows="rows" :row-height="rowHeight" :label="label" />
    <template v-else>
      <span v-if="loading && !note" role="status" class="visually-hidden">{{ label }}</span>
      <div class="loading-region-content" :data-arrived="waited ? '' : undefined"><slot /></div>
    </template>
  </div>
</template>

<style scoped>
.loading-region { position: relative; min-width: 0; }
.loading-region::before {
  content: "";
  position: absolute;
  inset: 0 0 auto;
  z-index: 1;
  height: 2px;
  border-radius: 2px;
  pointer-events: none;
  opacity: 0;
  background: linear-gradient(90deg, transparent, var(--accent), transparent);
  background-size: 200% 100%;
  transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out);
}
.loading-region-note { margin-bottom: 8px; }
.loading-region-content { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
/* No fill: once the fade has run, the refresh dimming below owns the opacity again. */
.loading-region-content[data-arrived] { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.loading-region[data-refreshing]::before { opacity: 1; transition-delay: var(--atlas-motion-defer); animation: atlas-progress var(--atlas-motion-loop) var(--atlas-ease-inout) infinite; }
.loading-region[data-refreshing] > .loading-region-content { opacity: .55; transition-delay: var(--atlas-motion-defer); }
@media (prefers-reduced-motion: reduce) { .loading-region[data-refreshing]::before { animation: none; } }
[data-atlas-reduced-motion] .loading-region[data-refreshing]::before { animation: none; }
</style>
