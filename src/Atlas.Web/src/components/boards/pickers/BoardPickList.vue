<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, ref } from "vue";
import { useSearchKeys } from "../../../lib/searchKeys";
import { twinHints } from "../../../lib/twinHints";
import AtlasIcon from "../../AtlasIcon.vue";
import EmptyState from "../../settings/EmptyState.vue";
import type { ChoiceOption } from "../../settings/types";
import { maxPicked } from "./useMultiPick";

/**
 * A search field over results that are ticked, not chosen: the multi-pick list of the board's pickers (YouTrack issues, contacts).
 * The owner searches (bind `v-model:query`, pass the results as `options`; nothing is filtered here) and keeps the basket
 * (`picked`, see useMultiPick): ticking an option again removes it, a tag under the list removes it too, and past `limit`
 * nothing more can be ticked. Its rows are the page editor's picker rows (CatalogPicker's: 40 px, a 16 px icon, a quiet second line).
 * A new result rises and fades in after the one before it (`.atlas-stagger`, 40 ms apart, the first eight),
 * and one that is already there stays still. Rows are checkboxes with arrow keys between them; Down in the field enters the list.
 * Once something is typed the first result not ticked yet is preselected: Enter ticks it and clears the search for the next one,
 * while `loading` (the shown results are still the previous text's) it waits for the answer and ticks that one's first result. Enter in the
 * empty search asks to `confirm` what is ticked. The first Esc empties the search, the next one closes the dialog. Tab goes on to the list: a tick among several is no suggestion taken.
 * SearchableChoiceList cannot do this: it is a radiogroup, where an arrow key selects. SearchableTags filters its options
 * itself, with the diacritics kept, so the answer of a server search that matched „novakova“ would vanish.
 */
const props = withDefaults(defineProps<{
  options: ChoiceOption[]; picked: ChoiceOption[]; label: string; placeholder?: string; emptyText?: string; limit?: number; maxHeight?: number; loading?: boolean;
}>(), { placeholder: t.pickList.search, emptyText: t.pickList.empty, limit: maxPicked, maxHeight: 280, loading: false });
const query = defineModel<string>("query", { default: "" });
const emit = defineEmits<{ toggle: [option: ChoiceOption]; confirm: [] }>();

const list = ref<HTMLElement>();
const field = ref<HTMLInputElement>();
const chosen = computed(() => new Set(props.picked.map(option => option.value)));
// Two results of one name that nothing else tells apart read „1. z 2“, „2. z 2“ (P8).
const twins = computed(() => { const hints = twinHints(props.options); return new Map(props.options.map((option, at) => [option, hints[at]])); });
const full = computed(() => props.picked.length >= props.limit);
const first = computed(() => (query.value.trim() && !full.value ? props.options.find(option => !chosen.value.has(option.value)) : undefined));
// With the search empty again, Enter adds what is ticked: C, „jan“, Enter, Enter places the contact (BD-13). An Enter (or Tab) while
// `loading` waits for the answer and ticks its first result; the first Esc empties the search, the next one closes the dialog (P3, P8).
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => props.loading, fresh: () => props.options, tab: false,
  empty: () => { if (!props.picked.length) return false; emit("confirm"); return true; },
  take: () => {
    if (!first.value) return false;
    emit("toggle", first.value);
    query.value = "";
  } });

function focusables() {
  return [...(list.value?.querySelectorAll<HTMLInputElement>("input[type=checkbox]:not(:disabled)") ?? [])];
}
function enter() { focusables()[0]?.focus(); }
function move(event: KeyboardEvent, step: 1 | -1) {
  event.preventDefault();
  const boxes = focusables();
  const next = boxes[boxes.indexOf(event.target as HTMLInputElement) + step];
  if (next) next.focus();
  else if (step < 0) field.value?.focus();
}
</script>

<template>
  <div class="pick">
    <label class="pick-query">
      <AtlasIcon name="search" :size="16" />
      <input ref="field" v-model="query" type="search" autocomplete="off" :aria-label="t.pickList.searchIn(label)" :placeholder="placeholder" autofocus data-step-focus @keydown.down.prevent="enter" @keydown="keys.onKeydown" />
    </label>
    <ul v-if="options.length" ref="list" class="pick-items atlas-stagger" role="group" :aria-label="label" :aria-busy="loading || undefined" :style="{ maxHeight: `${maxHeight}px` }" @keydown.down="move($event, 1)" @keydown.up="move($event, -1)">
      <li v-for="option in options" :key="option.value">
        <label class="pick-item" :data-first="option === first || undefined" :data-checked="chosen.has(option.value) || undefined" :data-disabled="(full && !chosen.has(option.value)) || undefined">
          <input type="checkbox" class="pick-box" :checked="chosen.has(option.value)" :disabled="full && !chosen.has(option.value)" :data-pick="option.value" @change="emit('toggle', option)" />
          <span class="pick-icon" aria-hidden="true"><AtlasIcon :name="option.icon ?? 'circle-help'" :size="16" /></span>
          <span class="pick-text">
            <span class="pick-title">{{ option.label }}</span>
            <small v-if="full && !chosen.has(option.value)">{{ t.pickList.max(limit) }}</small>
            <small v-else-if="option.description || twins.get(option)">{{ [option.description, twins.get(option)].filter(Boolean).join(" · ") }}</small>
          </span>
        </label>
      </li>
    </ul>
    <EmptyState v-else inline icon="search" class="pick-empty" :title="emptyText" />
    <div v-if="picked.length" class="pick-basket" role="group" :aria-label="t.pickList.picked">
      <span class="pick-count" data-pick-count>{{ t.pickList.chosen(picked.length, limit) }}</span>
      <button v-for="option in picked" :key="option.value" type="button" class="pick-tag" :aria-label="t.pickList.removeOption(option.label)" :data-tag="option.value" @click="emit('toggle', option)">
        <span>{{ option.label }}</span><AtlasIcon name="close" :size="13" />
      </button>
    </div>
  </div>
</template>

<style scoped>
.pick { display: grid; gap: 8px; min-width: 0; }
/* The editor pickers' search (CatalogPicker): the label is the field, with its focus ring. */
.pick-query { display: flex; align-items: center; gap: 8px; padding: 0 10px; border: 1px solid var(--input-line, var(--line)); border-radius: var(--atlas-radius-md, 8px); color: var(--muted);
  background: var(--surface, #fff); transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.pick-query:focus-within { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
/* Over the editor popover's own field look (its :is() selector is as specific as a plain one). */
.pick .pick-query input { flex: 1; min-width: 0; height: 36px; min-height: 0; padding: 0; border: 0; outline: 0; background: transparent; box-shadow: none; color: var(--text); caret-color: var(--text); font: inherit; }
.pick-items { display: grid; gap: 1px; margin: 0; padding: 2px; overflow-y: auto; overscroll-behavior: contain; list-style: none; }
.pick-item { display: flex; align-items: center; gap: 9px; min-height: 40px; padding: 5px 8px; border-radius: 7px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .pick-item:hover:not([data-disabled]) { background: var(--hover); } }
.pick-item[data-checked] { color: var(--accent-ink, var(--accent)); }
/* The result Enter ticks, while the search has the focus. */
.pick:has(.pick-query input:focus) .pick-item[data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.pick-item[data-disabled] { opacity: .55; cursor: not-allowed; }
.pick-item:has(.pick-box:focus-visible) { outline: 2px solid var(--accent); outline-offset: -2px; }
.pick-box { flex: none; width: 16px; height: 16px; min-height: 0; margin: 0; accent-color: var(--accent); cursor: inherit; }
.pick-box:focus-visible { outline: none; }
.pick-icon { display: grid; place-items: center; flex: none; color: var(--muted); }
.pick-item[data-checked] .pick-icon { color: inherit; }
.pick-text { display: grid; min-width: 0; }
.pick-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.pick-text small { overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.pick-empty { padding: 10px 4px; }
.pick-basket { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.pick-count { margin-right: 4px; color: var(--muted); font-size: 12px; font-weight: 600; }
.pick-tag { display: inline-flex; align-items: center; gap: 6px; max-width: 100%; min-height: 0; padding: 2px 8px; border: 0; border-radius: 999px; color: var(--accent-ink, var(--accent)); background: var(--accent-soft); font: inherit; font-size: 12px; font-weight: 600; cursor: pointer; transition: background-color var(--atlas-motion-fast), transform var(--atlas-motion-fast) var(--atlas-ease-out); }
.pick-tag > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@media (hover: hover) and (pointer: fine) { .pick-tag:hover { background: color-mix(in srgb, var(--accent) 18%, var(--accent-soft)); } }
.pick-tag:active { transform: scale(var(--atlas-press)); }
.pick-tag:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
</style>
