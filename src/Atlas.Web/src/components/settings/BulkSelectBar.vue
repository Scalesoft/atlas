<script setup lang="ts">
import { formatNumber } from "../../i18n";
import t from "../../i18n/selection";
import { Button } from "../ui/button";
import { Checkbox } from "../ui/checkbox";
import InlineReveal from "./InlineReveal.vue";
import type { BulkSelection } from "./bulkSelection";

/**
 * The one bulk selection of a list (docs/ux-concepts.md „Bulk selection“): Obsah, Schvalování konceptů and Stav obsahu. Notion's
 * model: no „Vybrat“ mode, ticking a row starts it. Put it right above the list: „Vybrat zobrazené (n)“ with its indeterminate
 * box stands there while some row may be ticked, and the bar with the count, the host's actions (the slot) and „Zrušit výběr“
 * unfolds above it at the first tick. The rows give their `ui/checkbox` the class `bulk-box` (in a list with one Tab stop also
 * tabindex -1; Space on a row ticks it either way, rowKeys), the row `bulk-row` and the list `bulk-selecting` while something is
 * ticked: a box shows on hover and focus of its row,
 * on every row once something is ticked, and always on a touch screen, and its room is kept, so nothing moves. Not for picking
 * items inside a form or a dialog (BoardPickList, the AI proposal): those are choices, not a selection to act on.
 */
defineProps<{ selection: BulkSelection; disabled?: boolean }>();
</script>

<template>
  <div class="bulk-select" data-bulk-select>
    <InlineReveal :open="selection.count > 0">
      <div class="bulk-select-bar" role="group" :aria-label="t.selected(selection.count)" data-bulk-bar>
        <strong>{{ t.selected(selection.count) }}</strong>
        <slot />
        <Button variant="ghost" size="sm" type="button" class="bulk-select-clear" :disabled="disabled" data-bulk-clear @click="selection.clear()">{{ t.clear }}</Button>
      </div>
    </InlineReveal>
    <label v-if="selection.total" class="bulk-select-all">
      <Checkbox :model-value="selection.all" :aria-label="t.selectShownCount(selection.total)" :disabled="disabled" data-bulk-all @update:model-value="selection.toggleAll()" />
      <span>{{ t.selectShown }} <span class="bulk-select-total">({{ formatNumber(selection.total) }})</span></span>
    </label>
  </div>
</template>

<style scoped>
.bulk-select { display: grid; min-width: 0; }
.bulk-select-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 8px; margin-bottom: 6px; padding: 6px 8px 6px 12px; border: 1px solid color-mix(in srgb, var(--accent) 30%, var(--line)); border-radius: 10px; background: var(--accent-soft); font-size: 13px; }
.bulk-select-bar > strong { margin-right: 4px; font-weight: 600; font-variant-numeric: tabular-nums; }
.bulk-select-clear { margin-left: auto; }
.bulk-select-all { display: inline-flex; align-items: center; gap: 10px; justify-self: start; padding: 4px 0; color: var(--muted); font-size: 13px; cursor: pointer; }
.bulk-select-total { font-variant-numeric: tabular-nums; }
/* The rows' boxes (see above). Unlayered, so the fade replaces the box's own shadow transition, which it keeps. */
:global(.bulk-box) { opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
:global(.bulk-selecting .bulk-box), :global(.bulk-row:focus-within .bulk-box), :global(.bulk-box[data-state="checked"]), :global(.bulk-box:focus-visible) { opacity: 1; }
@media (hover: hover) and (pointer: fine) { :global(.bulk-row:hover .bulk-box) { opacity: 1; } }
@media (hover: none) { :global(.bulk-box) { opacity: 1; } }
</style>
