<script setup lang="ts">
import { computed, ref } from "vue";
import { foldText } from "../../lib/foldText";
import { ownEscape, useSearchKeys } from "../../lib/searchKeys";
import { twinHints } from "../../lib/twinHints";
import { DropdownMenuCheckboxItem, DropdownMenuItemIndicator, DropdownMenuRadioGroup, DropdownMenuRadioItem } from "reka-ui";
import AtlasIcon from "../AtlasIcon.vue";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "../ui/dropdown-menu";
import type { ChoiceOption } from "./types";

/**
 * One filter of a list as a compact trigger, "Kdy: Kdykoli", that opens a choice menu (the shared
 * menu variant: no title, Escape and outside click close it). A filter bar of these takes one line
 * instead of a block of pill rows, so the list itself stays on screen. The option with value ""
 * means "no filter"; any other choice highlights the trigger. Several filters side by side belong
 * here; one prominent choice in a form stays ChoiceCards or OptionPills.
 *
 * `multiple`: several values at once (`v-model:values`, none ticked = no filter, `allLabel` on the trigger then), as checkboxes that
 * keep the menu open. With `searchLabel` and more than eight options (either mode) a search field above them filters the list with the
 * shared keys (P3, P8): the first Esc empties it, a typed text preselects the first match, Enter ticks or picks it (a single choice then
 * closes the menu), ↓ walks the list; options of one name are told apart.
 */
// The menu root renders no element; attributes (data-*) belong on the trigger.
defineOptions({ inheritAttrs: false });
const props = withDefaults(defineProps<{ label: string; options: ChoiceOption[]; icon?: string; multiple?: boolean; allLabel?: string; searchLabel?: string }>(),
  { icon: undefined, allLabel: "", searchLabel: undefined });
const model = defineModel<string>({ default: "" });
const values = defineModel<string[]>("values", { default: () => [] });
const ticked = computed(() => props.options.filter((option) => values.value.includes(option.value)));
const current = computed(() => props.multiple
  ? { value: values.value.join(","), icon: ticked.value[0]?.icon, label: !ticked.value.length ? props.allLabel : ticked.value.length > 1 ? `${ticked.value[0]!.label} +${ticked.value.length - 1}` : ticked.value[0]!.label }
  : props.options.find((option) => option.value === model.value) ?? props.options[0]);
const active = computed(() => props.multiple ? ticked.value.length > 0 : !!model.value);
const toggle = (value: string) => { values.value = values.value.includes(value) ? values.value.filter((each) => each !== value) : [...values.value, value]; };

const open = ref(false);
const query = ref("");
const search = ref<HTMLInputElement>();
const searchable = computed(() => !!props.searchLabel && props.options.length > 8);
const shown = computed(() => props.options.filter((option) => !query.value.trim() || foldText(option.label).includes(foldText(query.value.trim()))));
const first = computed(() => shown.value.find((option) => !option.disabled));
const hints = computed(() => { const found = twinHints(props.options, false); return new Map(props.options.map((option, index) => [option, found[index]] as const)); });
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, tab: false,
  take: () => {
    if (!first.value) return false;
    if (props.multiple) toggle(first.value.value);
    else { model.value = first.value.value; open.value = false; }
  } });
/** In the search field: the shared keys first, ↓ goes into the list; other typing stays in the field (no menu typeahead). */
function searchKey(event: KeyboardEvent) {
  if (keys.onKeydown(event)) return event.stopPropagation();
  if (event.key === "Escape" || event.key === "Tab") return;
  event.stopPropagation();
  if (event.key === "ArrowDown") { event.preventDefault(); search.value?.parentElement?.querySelector<HTMLElement>("[data-filter-option]")?.focus(); }
}
function opened(event: Event) {
  if (!searchable.value) return;
  event.preventDefault();
  search.value?.focus();
}
</script>

<template>
  <!-- Not modal: a modal menu locks the body's scroll and pointer, which restyles the whole page on opening and closing (a 600-block
       editor: 100+ ms, 3.6 Přehled); a single choice needs neither. -->
  <DropdownMenu v-model:open="open" :modal="false" @update:open="isOpen => { if (!isOpen) query = ''; }">
    <DropdownMenuTrigger as-child>
      <button v-bind="$attrs" type="button" class="filter-menu-trigger" :data-active="active || undefined" :aria-label="`${label}: ${current?.label ?? ''}`">
        <AtlasIcon :name="(active ? current?.icon : icon) ?? current?.icon ?? 'filter'" :size="14" />
        <span class="filter-menu-name">{{ label }}:</span>
        <span :key="current?.value" class="filter-menu-value">{{ current?.label }}</span>
        <AtlasIcon name="down" :size="14" class="filter-menu-chevron" />
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuContent class="filter-menu-panel settings-ui" align="start" :side-offset="6" :collision-padding="12" :aria-label="label" @open-auto-focus="opened" @escape-key-down="event => { if (query) event.preventDefault(); }">
      <template v-if="multiple">
        <input v-if="searchable" ref="search" v-model="query" class="filter-menu-search" type="search" autocomplete="off" :placeholder="searchLabel" :aria-label="searchLabel" :data-md-own-escape="ownEscape(query)" @keydown="searchKey">
        <DropdownMenuCheckboxItem v-for="option in shown" :key="option.value || '__none'" :model-value="values.includes(option.value)" class="filter-menu-item"
          :data-tone="option.tone" :data-value="option.value" :data-preselected="(searchable && keys.lit.value && option === first) || undefined" data-filter-option :disabled="option.disabled"
          @update:model-value="toggle(option.value)" @select="event => event.preventDefault()">
          <span class="filter-menu-box" aria-hidden="true"><DropdownMenuItemIndicator class="filter-menu-check"><AtlasIcon name="check" :size="13" /></DropdownMenuItemIndicator></span>
          <AtlasIcon v-if="option.icon" :name="option.icon" :size="16" />
          <span class="filter-menu-item-label">{{ option.label }}<small v-if="hints.get(option)" class="filter-menu-twin">{{ hints.get(option) }}</small></span>
          <span v-if="option.badge" class="filter-menu-badge">{{ option.badge }}</span>
        </DropdownMenuCheckboxItem>
      </template>
      <input v-if="!multiple && searchable" ref="search" v-model="query" class="filter-menu-search" type="search" autocomplete="off" :placeholder="searchLabel" :aria-label="searchLabel" :data-md-own-escape="ownEscape(query)" @keydown="searchKey">
      <DropdownMenuRadioGroup v-if="!multiple" v-model="model">
        <DropdownMenuRadioItem v-for="option in shown" :key="option.value || '__all'" :value="option.value" class="filter-menu-item" :data-tone="option.tone" :data-value="option.value || 'all'" :data-preselected="(searchable && keys.lit.value && option === first) || undefined" data-filter-option :disabled="option.disabled">
          <AtlasIcon :name="option.icon ?? 'filter'" :size="16" />
          <span class="filter-menu-item-label">{{ option.label }}<small v-if="hints.get(option)" class="filter-menu-twin">{{ hints.get(option) }}</small></span>
          <span v-if="option.badge" class="filter-menu-badge">{{ option.badge }}</span>
          <DropdownMenuItemIndicator class="filter-menu-check"><AtlasIcon name="check" :size="15" /></DropdownMenuItemIndicator>
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style scoped>
.filter-menu-trigger {
  display: inline-flex; align-items: center; gap: 6px; min-height: 32px; padding: 4px 10px;
  border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--text);
  font: inherit; font-size: 13px; cursor: pointer; white-space: nowrap;
  transition: border-color var(--atlas-motion-fast), background-color var(--atlas-motion-fast), color var(--atlas-motion-fast), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
.filter-menu-trigger > svg { color: var(--muted); flex-shrink: 0; }
@media (hover: hover) and (pointer: fine) {
  .filter-menu-trigger:hover { border-color: color-mix(in srgb, var(--accent) 45%, var(--line)); }
}
.filter-menu-trigger:active { transform: scale(var(--atlas-press)); }
.filter-menu-trigger:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.filter-menu-trigger[data-state="open"] { border-color: var(--accent); }
.filter-menu-trigger[data-active] { border-color: color-mix(in srgb, var(--accent) 55%, var(--line)); background: var(--accent-soft); color: var(--accent); }
.filter-menu-trigger[data-active] > svg { color: var(--accent); }
.filter-menu-name { color: var(--muted); }
.filter-menu-trigger[data-active] .filter-menu-name { color: inherit; opacity: .8; }
.filter-menu-value { font-weight: 600; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.filter-menu-chevron { transition: rotate var(--atlas-motion-fast) var(--atlas-ease-out); }
.filter-menu-trigger[data-state="open"] .filter-menu-chevron { rotate: 180deg; }
</style>

<style>
/* The panel is portalled out of this component, so its rules cannot be scoped. */
.filter-menu-panel { z-index: 50; display: grid; min-width: 200px; max-width: calc(100vw - 24px); max-height: var(--reka-dropdown-menu-content-available-height, calc(100dvh - 16px)); overflow: auto; padding: 6px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); color: var(--text); background: var(--surface); box-shadow: var(--shadow-menu); }
.filter-menu-item { display: flex; align-items: center; gap: 10px; min-height: var(--control-h, 36px); padding: 7px 10px; border-radius: var(--atlas-radius-sm); font-size: 13px; cursor: pointer; outline: none; user-select: none; }
.filter-menu-item > svg:first-child { color: var(--muted); flex-shrink: 0; }
.filter-menu-item[data-highlighted] { background: var(--hover); }
.filter-menu-item[data-state="checked"] { color: var(--accent); font-weight: 600; }
.filter-menu-item[data-state="checked"] > svg:first-child { color: var(--accent); }
.filter-menu-item[data-disabled] { opacity: .5; cursor: not-allowed; }
.filter-menu-item-label { flex: 1; }
.filter-menu-search { margin: 0 0 6px; padding: 7px 10px; border: 1px solid var(--input-line, var(--line)); border-radius: var(--atlas-radius-sm); background: var(--surface); color: var(--text); font: inherit; font-size: 13px; }
.filter-menu-twin { margin-left: 6px; color: var(--muted); font-size: 11.5px; }
.filter-menu-search:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; }
/* Ticked or not, a box; the first item a search preselected shows that Enter ticks it. */
.filter-menu-box { display: inline-grid; place-items: center; flex: none; width: 16px; height: 16px; border: 1px solid var(--input-line, var(--line)); border-radius: 4px; }
.filter-menu-item[data-state="checked"] .filter-menu-box { border-color: var(--accent); background: var(--accent); }
.filter-menu-item[data-state="checked"] .filter-menu-box .filter-menu-check { color: var(--on-accent, #fff); }
.filter-menu-item[data-preselected] { background: var(--hover); }
.filter-menu-badge { padding: 0 7px; border-radius: 999px; color: var(--muted); background: var(--hover); font-size: 11.5px; font-weight: 600; }
.filter-menu-check { display: inline-flex; color: var(--accent); }
.filter-menu-item[data-tone="warning"] > svg:first-child { color: var(--warning); }
.filter-menu-item[data-tone="danger"] > svg:first-child { color: var(--error); }
.filter-menu-item[data-tone="success"] > svg:first-child { color: var(--success); }
</style>
