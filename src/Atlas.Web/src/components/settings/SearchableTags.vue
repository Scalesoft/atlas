<script setup lang="ts">
import { locale } from "../../i18n";
import t from "../../i18n/settings-shell";
import settingsSearch from "../../i18n/settings-search";
import { computed, ref, useId } from "vue";
import { useSearchKeys } from "../../lib/searchKeys";
import { twinHints } from "../../lib/twinHints";
import AtlasIcon from "../AtlasIcon.vue";
import type { ChoiceOption } from "./types";

/**
 * Pick several from a long list (people in a filter) by searching, never through a dropdown. Picks
 * stay in the field as removable tags, so the selection reads at a glance; matches show inline
 * below as pills. Unsearched, only the first `idle` options are offered — order them by relevance.
 * For one short choice use OptionPills; for one pick from a long list with details, SearchableChoiceList.
 * `compact` (filter bars): the label is for screen readers only and the suggestions unfold only
 * while focus stays in the picker. The first Escape empties the field, the next one (or leaving) closes the suggestions (P3).
 * Once something is typed the first match is preselected and Enter or Tab adds it; Down enters the pills, arrows walk them.
 */
const props = withDefaults(
  defineProps<{ options: ChoiceOption[]; label: string; placeholder?: string; emptyText?: string; idle?: number; limit?: number; icon?: string; compact?: boolean }>(),
  { placeholder: undefined, emptyText: undefined, idle: 5, limit: 8, icon: undefined, compact: false },
);
const model = defineModel<string[]>({ default: () => [] });
const uid = useId();
const query = ref("");
const input = ref<HTMLInputElement>();
const root = ref<HTMLElement>();
const focused = ref(false);
const dismissed = ref(false);
const suggesting = computed(() => !props.compact || (focused.value && !dismissed.value));
function enter() { focused.value = true; dismissed.value = false; }
function leave(event: FocusEvent) {
  if (!root.value?.contains(event.relatedTarget as Node | null)) focused.value = false;
}
function dismiss(event: KeyboardEvent) {
  if (!props.compact || !suggesting.value) return;
  event.preventDefault();
  event.stopPropagation();
  // Move focus before the leaving suggestions become inert; a second Escape can dismiss the owner.
  input.value?.focus();
  dismissed.value = true;
}

const byValue = computed(() => new Map(props.options.map((option) => [option.value, option])));
const tags = computed(() => model.value.map((value) => byValue.value.get(value) ?? { value, label: value }));
const text = computed(() => query.value.trim().toLocaleLowerCase(locale));
const matches = computed(() =>
  props.options.filter(
    (option) => !option.disabled && !model.value.includes(option.value) && (!text.value || `${option.label} ${option.description ?? ""}`.toLocaleLowerCase(locale).includes(text.value)),
  ),
);
const suggestions = computed(() => matches.value.slice(0, text.value ? props.limit : props.idle));
const more = computed(() => matches.value.length - suggestions.value.length);
// Two people of one name read apart (P8): by their description, which these pills do not show, else by their order, „1. z 2“, „2. z 2“. Beside the badge, on the chosen tags too.
const twins = computed(() => { const hints = twinHints(props.options, false); return new Map(props.options.map((option, at) => [option, hints[at]])); });
const hint = (option: ChoiceOption) => [option.badge, twins.value.get(option)].filter(Boolean).join(" · ");

function add(value: string) {
  model.value = [...model.value, value];
  query.value = "";
  input.value?.focus();
}
function remove(value: string) {
  model.value = model.value.filter((picked) => picked !== value);
  input.value?.focus();
}
const first = computed(() => (suggesting.value && text.value ? suggestions.value[0] : undefined));
// Enter or Tab adds the first match; the first Esc empties the field (P3, P8). Typed or not, the field's Enter is the picker's: it never submits a form around it.
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, empty: () => true, keepFocus: true,
  take: () => { if (!first.value) return false; add(first.value.value); } });
/** Down from the field enters the pills, the arrows walk them, Up or Left from the first one returns to the field. */
function walk(event: KeyboardEvent) {
  const pills = [...(root.value?.querySelectorAll<HTMLElement>("[data-suggestion]") ?? [])];
  const at = pills.indexOf(event.target as HTMLElement);
  if (at < 0 && event.key !== "ArrowDown") return;
  event.preventDefault();
  const next = at < 0 ? 0 : at + (event.key === "ArrowDown" || event.key === "ArrowRight" ? 1 : -1);
  (next < 0 ? input.value : pills[Math.min(next, pills.length - 1)])?.focus();
}
function removeLast(event: KeyboardEvent) {
  if (query.value || !model.value.length) return;
  event.preventDefault();
  remove(model.value[model.value.length - 1]!);
}
</script>

<template>
  <div ref="root" class="searchable-tags" :class="{ 'is-compact': compact }" @focusin="enter" @focusout="leave" @keydown.esc="dismiss">
    <span :id="`${uid}-label`" :class="compact ? 'st-sr-only' : 'searchable-tags-label'">{{ label }}</span>
    <div class="searchable-tags-field" @click.self="input?.focus()">
      <AtlasIcon name="search" :size="14" />
      <TransitionGroup name="st-fade" tag="ul" class="searchable-tags-list" :aria-labelledby="`${uid}-label`">
        <li v-for="tag in tags" :key="tag.value">
          <button type="button" class="searchable-tag" :aria-label="t.tags.remove(tag.label, twins.get(tag))" :data-tag="tag.value" @click="remove(tag.value)">
            <AtlasIcon v-if="tag.icon ?? icon" :name="(tag.icon ?? icon)!" :size="13" />{{ tag.label }}<span v-if="twins.get(tag)" class="searchable-tags-badge">{{ twins.get(tag) }}</span><AtlasIcon name="close" :size="13" />
          </button>
        </li>
      </TransitionGroup>
      <input
        ref="input"
        v-model="query"
        type="search"
        autocomplete="off"
        :aria-label="t.tags.search(label)"
        :placeholder="tags.length ? '' : (placeholder ?? settingsSearch.searchPlaceholder)"
        @input="dismissed = false"
        @click="dismissed = false"
        @keydown="keys.onKeydown"
        @keydown.down="walk"
        @keydown.backspace="removeLast"
      />
    </div>
    <Transition name="st-fade" @before-leave="el => { el.setAttribute('inert', ''); el.setAttribute('aria-hidden', 'true'); }">
    <div v-if="suggesting" class="searchable-tags-suggestions" role="group" :aria-label="t.tags.suggestions(label)" @keydown.up.down.left.right="walk">
      <button v-for="option in suggestions" :key="option.value" type="button" class="searchable-tags-pill" :data-suggestion="option.value" :data-first="option === first || undefined" @click="add(option.value)">
        <AtlasIcon :name="option.icon ?? icon ?? 'plus'" :size="14" />
        <span>{{ option.label }}</span>
        <span v-if="hint(option)" class="searchable-tags-badge">{{ hint(option) }}</span>
      </button>
      <span v-if="more > 0" class="searchable-tags-hint">{{ t.tags.more(more, !!text) }}</span>
      <span v-else-if="text && !suggestions.length" class="searchable-tags-hint" role="status">{{ emptyText ?? t.tags.empty }}</span>
    </div>
    </Transition>
  </div>
</template>

<style scoped>
.searchable-tags { display: grid; gap: 6px; min-width: 0; }
/* In a filter bar the suggestions float under the field instead of pushing the list down. */
.searchable-tags.is-compact { position: relative; }
.searchable-tags.is-compact .searchable-tags-field { min-height: 32px; padding: 1px 10px; }
.searchable-tags.is-compact .searchable-tags-field input { height: 26px; }
.searchable-tags.is-compact .searchable-tags-suggestions { position: absolute; top: calc(100% + 6px); left: 0; right: 0; z-index: 20; padding: 8px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: var(--surface); box-shadow: var(--shadow-menu); }
.searchable-tags-label { font-size: 12.5px; font-weight: 650; color: var(--muted); }
.searchable-tags-field {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  min-height: 36px;
  padding: 3px 10px;
  border: 1px solid var(--input-line);
  border-radius: 18px;
  color: var(--muted);
  background: var(--surface);
  cursor: text;
  transition: border-color var(--atlas-motion-fast), box-shadow var(--atlas-motion-fast);
}
.searchable-tags-field:focus-within { outline: var(--state-focus-ring); outline-offset: var(--state-focus-offset); }
.searchable-tags-list { display: contents; margin: 0; padding: 0; list-style: none; }
.searchable-tags-field input { flex: 1; min-width: 120px; height: 28px; border: 0; outline: 0; background: transparent; color: var(--text); font: inherit; font-size: 13px; }
.searchable-tag {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 8px;
  border-radius: 999px;
  color: var(--accent);
  background: var(--accent-soft);
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color var(--atlas-motion-fast), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .searchable-tag:hover { background: color-mix(in srgb, var(--accent) 18%, var(--accent-soft)); }
}
.searchable-tag:active { transform: scale(var(--atlas-press)); }
.searchable-tag:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.searchable-tags-suggestions { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; min-width: 0; }
.searchable-tags-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 12px 0 10px;
  border: 1px dashed var(--line);
  border-radius: 999px;
  background: var(--surface);
  color: var(--text);
  font: inherit;
  font-size: 13px;
  cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-base) var(--atlas-ease-out);
}
.searchable-tags-pill > svg { color: var(--muted); }
@media (hover: hover) and (pointer: fine) {
  .searchable-tags-pill:hover { border-color: color-mix(in srgb, var(--accent) 50%, var(--line)); }
}
.searchable-tags-pill:active { transform: scale(var(--atlas-press)); transition-duration: var(--atlas-motion-fast); }
.searchable-tags-pill:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
/* The match Enter adds, while the field has the focus. */
.searchable-tags:has(input:focus) .searchable-tags-pill[data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.searchable-tags-badge { padding: 0 6px; border-radius: 999px; font-size: 11px; font-weight: 650; background: var(--hover); color: var(--muted); }
.searchable-tags-hint { color: var(--muted); font-size: 12.5px; }
</style>
