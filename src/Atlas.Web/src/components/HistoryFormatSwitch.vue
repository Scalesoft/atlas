<script setup lang="ts">
import { ref } from "vue";
import { useHistoryFormat } from "../historyFormat";
import { useSegmentedThumb } from "./library/segmentedThumb";
import t from "../i18n/components-history";

/** Náhled | Markdown of a history view: the page as it reads, or its stored Markdown. Shared by every history view. */
const { format, choose } = useHistoryFormat();
// The raised segment is one thumb that glides to the chosen option (segmentedThumb.ts).
const track = ref<HTMLElement>();
const thumb = useSegmentedThumb(track, '[aria-pressed="true"]', format);
</script>

<template>
  <div ref="track" v-bind="thumb" class="history-format" role="group" :aria-label="t.format.label">
    <button type="button" :aria-pressed="format === 'rendered'" @click="choose('rendered')">{{ t.format.rendered }}</button>
    <button type="button" :aria-pressed="format === 'source'" @click="choose('source')">{{ t.format.source }}</button>
  </div>
</template>

<style scoped>
/* The same segmented switch as „Změny | Celá verze“ beside it in Historie stránky (PageHistoryPanel): one look for one
   kind of control. The chosen segment keeps its weight, so choosing does not change the switch's width. The thumb is
   concentric with the track: 6 px radius − 1 px border − 3 px padding. */
.history-format { --thumb-inset: 3px; --thumb-radius: calc(6px - 1px - 3px); --thumb-shadow: var(--shadow); display: inline-flex; gap: 2px; padding: 3px; border: 1px solid var(--line); border-radius: 6px; background: var(--sidebar); }
.history-format button { padding: 3px 9px; border: 0; border-radius: calc(6px - 1px - 3px); background: transparent; color: var(--muted); font: inherit; font-size: 12px; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.history-format button[aria-pressed="true"] { background: var(--surface); color: var(--text); box-shadow: var(--shadow); }
/* While the thumb is drawn it carries the raised segment; without it (reduced motion) the chosen option fades its own. */
.history-format[data-thumb] button[aria-pressed="true"] { background: transparent; box-shadow: none; }
@media (hover: hover) and (pointer: fine) { .history-format button:hover:not([aria-pressed="true"]) { color: var(--text); } }
.history-format button:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
</style>
