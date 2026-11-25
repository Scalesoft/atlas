<script setup lang="ts">
import t from "../../i18n/settings-shell";
import settingsSearch from "../../i18n/settings-search";
import { computed, ref, useId } from "vue";
import { RadioGroupItem, RadioGroupRoot } from "reka-ui";
import { foldText } from "../../lib/foldText";
import { ownEscape, useSearchKeys } from "../../lib/searchKeys";
import { twinHints } from "../../lib/twinHints";
import AtlasIcon from "../AtlasIcon.vue";
import EmptyState from "./EmptyState.vue";
import type { ChoiceOption } from "./types";

/**
 * A long list as searchable tiles (people, groups, files, targets), never a dropdown. The list is
 * a radiogroup: arrows move and select, Down in the search field jumps into the list. Once something is
 * typed, the first result is preselected and Enter (or Tab) in the field picks it; the first Esc empties the search. With `remote` the owner searches
 * (bind `v-model:query`) and passes the results as options; nothing is filtered here, and `loading` says
 * the shown results are still those of the previous query, so Enter waits for the answer and picks its first result.
 */
const props = withDefaults(
  defineProps<{ options: ChoiceOption[]; label: string; placeholder?: string; emptyText?: string; hideLabel?: boolean; maxHeight?: number; remote?: boolean; loading?: boolean }>(),
  { placeholder: undefined, emptyText: undefined, hideLabel: true, maxHeight: 300, remote: false, loading: false },
);
const model = defineModel<string | null>({ default: null });
const query = defineModel<string>("query", { default: "" });
const emit = defineEmits<{ pick: [value: string] }>();
const uid = useId();
const list = ref<HTMLElement>();
// Every word of the query must appear somewhere, in any order and without diacritics: "musil martin" finds "Marek Musil".
const visible = computed(() => {
  if (props.remote) return props.options;
  const words = foldText(query.value).split(/\s+/).filter(Boolean);
  if (!words.length) return props.options;
  return props.options.filter((option) => {
    const text = foldText(`${option.label} ${option.description ?? ""} ${option.badge ?? ""}`);
    return words.every((word) => text.includes(word));
  });
});
// Two options of one name that nothing else tells apart read „1. z 2“, „2. z 2“ (P8).
const twins = computed(() => { const hints = twinHints(props.options); return new Map(props.options.map((option, at) => [option, hints[at]])); });
function enterList() {
  const root = list.value;
  (root?.querySelector<HTMLElement>('[role="radio"][data-state="checked"]') ?? root?.querySelector<HTMLElement>('[role="radio"]:not([data-disabled])'))?.focus();
}
const first = computed(() => (query.value.trim() ? visible.value.find((option) => !option.disabled) : undefined));
// Enter or Tab takes the first result (an Enter given while `loading` waits for the answer); the first Esc empties the search (P3, P8).
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => props.loading, ...(props.remote && { fresh: () => props.options }),
  take: () => { if (!first.value) return false; choose(first.value.value); } });
// The preselected row is announced from the search: a screen reader reads it while the focus stays in the field.
const firstId = computed(() => (first.value ? `${uid}-option-${visible.value.indexOf(first.value)}` : undefined));
function choose(value: unknown) {
  model.value = String(value);
  emit("pick", String(value));
}
</script>

<template>
  <div class="searchable-list">
    <span :id="`${uid}-label`" :class="hideLabel ? 'st-sr-only' : 'searchable-label'">{{ label }}</span>
    <label class="searchable-query">
      <AtlasIcon name="search" :size="17" />
      <input
        v-model="query"
        type="search"
        autocomplete="off"
        :aria-label="t.choiceList.search(label)"
        :placeholder="placeholder ?? settingsSearch.searchPlaceholder"
        data-step-focus
        :aria-controls="visible.length ? `${uid}-list` : undefined"
        :aria-activedescendant="firstId"
        :data-md-own-escape="ownEscape(query)"
        @keydown.down.prevent="enterList"
        @keydown="keys.onKeydown"
      />
    </label>
    <div ref="list" :aria-busy="loading || undefined">
      <RadioGroupRoot
        v-if="visible.length"
        :id="`${uid}-list`"
        :model-value="model ?? undefined"
        loop
        class="searchable-items"
        :style="{ maxHeight: `${maxHeight}px` }"
        :aria-labelledby="`${uid}-label`"
        @update:model-value="choose"
      >
        <RadioGroupItem
          v-for="(option, position) in visible"
          :key="option.value"
          :value="option.value"
          :disabled="option.disabled"
          class="searchable-item"
          :id="`${uid}-option-${position}`"
          :data-value="option.value"
          :data-first="option === first || undefined"
          :style="{ '--i': Math.min(position, 10) }"
        >
          <span class="searchable-icon" :data-tone="option.tone"><AtlasIcon :name="option.icon ?? 'circle-help'" :size="18" /></span>
          <span class="searchable-text">
            <span class="searchable-title">{{ option.label }}<span v-if="option.badge" class="searchable-badge">{{ option.badge }}</span></span>
            <small v-if="option.disabled && option.disabledReason">{{ option.disabledReason }}</small>
            <small v-else-if="option.description || twins.get(option)">{{ [option.description, twins.get(option)].filter(Boolean).join(" · ") }}</small>
          </span>
          <span class="searchable-check" aria-hidden="true"><AtlasIcon name="check" :size="13" /></span>
        </RadioGroupItem>
      </RadioGroupRoot>
      <EmptyState v-else inline icon="search" class="searchable-empty" :title="emptyText ?? t.choiceList.empty" />
    </div>
  </div>
</template>

<style scoped>
.searchable-list { display: grid; border: 1px solid var(--line); border-radius: var(--st-radius); overflow: hidden; background: var(--surface); min-width: 0; }
.searchable-label { padding: 10px 14px 0; font-size: 13px; font-weight: 650; }
/* Give the search its own inset field; the list's rounded clip must not cut its focus ring. */
.searchable-query { display: flex; align-items: center; gap: 10px; min-height: 46px; margin: 6px 6px 0; padding: 0 10px; border: 1px solid var(--line); border-radius: calc(var(--st-radius) - 7px); color: var(--muted); background: var(--canvas); transition: border-color var(--atlas-motion-fast), box-shadow var(--atlas-motion-fast); }
.searchable-query:focus-within { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.searchable-query input { flex: 1; min-width: 0; height: 44px; border: 0; outline: 0; background: transparent; color: var(--text); font: inherit; }
.searchable-items { display: grid; gap: 2px; padding: 5px; overflow-y: auto; }
.searchable-item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 11px;
  width: 100%;
  min-height: 52px;
  padding: 7px 40px 7px 9px;
  /* The list's radius less its 1 px border and the 5 px the items sit in: the first and last item follow the panel's corners. */
  border-radius: calc(var(--st-radius) - 6px);
  color: var(--text);
  font: inherit;
  text-align: left;
  cursor: pointer;
  /* Release opacity/transform after entering so disabled and pressed states can take over. */
  animation: st-rise-in var(--atlas-motion-base) var(--atlas-ease-out) backwards;
  animation-delay: calc(var(--i) * var(--st-stagger));
  transition: background-color var(--atlas-motion-fast), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .searchable-item:hover:not([data-disabled]) { background: var(--hover); }
}
/* The result Enter picks, while the search has the focus. */
.searchable-list:has(.searchable-query input:focus) .searchable-item[data-first]:not([data-state="checked"]) { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.searchable-item:active:not([data-disabled]) { transform: scale(var(--atlas-press)); }
.searchable-item:focus-visible { outline: var(--state-focus-ring); outline-offset: -2px; --focus-offset: -2px; }
.searchable-item[data-state="checked"] { background: var(--accent-soft); color: var(--accent); }
.searchable-item[data-disabled] { opacity: .55; cursor: not-allowed; }
.searchable-icon { display: grid; place-items: center; flex-shrink: 0; width: 34px; height: 34px; border-radius: 10px; color: var(--accent); background: var(--accent-soft); }
.searchable-icon[data-tone="neutral"] { color: var(--muted); background: var(--hover); border-radius: 50%; }
.searchable-icon[data-tone="warning"] { color: var(--warning); background: var(--warning-bg); }
.searchable-item[data-state="checked"] .searchable-icon { color: var(--on-accent); background: var(--accent); }
.searchable-text { display: grid; gap: 1px; min-width: 0; }
.searchable-title { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; font-weight: 600; overflow-wrap: anywhere; }
.searchable-text small { color: var(--muted); font-size: 12px; }
.searchable-badge { padding: 0 7px; border-radius: 999px; font-size: 11px; font-weight: 600; color: var(--muted); background: var(--hover); }
.searchable-check {
  position: absolute;
  right: 12px;
  display: grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 50%;
  color: var(--on-accent);
  background: var(--accent);
  opacity: 0;
  transform: scale(var(--st-pop-from, .6));
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), transform var(--atlas-motion-base) var(--atlas-ease-out);
}
.searchable-item[data-state="checked"] .searchable-check { opacity: 1; transform: none; }
.searchable-empty { padding: 18px; }
</style>
