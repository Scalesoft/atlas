<script lang="ts">
import { appearanceCategoryLabel, appearanceIcons, type AppearanceIcon } from "./appearanceIcons";
import type { IconPackCatalog } from "../../iconPack";
import { locale } from "../../i18n";

interface BrowserIcon extends AppearanceIcon { category: string }
const labelOrder = new Intl.Collator(locale);
const lists = new WeakMap<IconPackCatalog, { recommended: BrowserIcon[]; everything: BrowserIcon[] }>();
/**
 * Built once per loaded catalog, not per mount: the search caches its folded texts per icon object, so a list rebuilt on
 * every opening would fold two thousand texts again. Inside a category the 87 come first, then by label.
 */
function iconLists(catalog: IconPackCatalog) {
  let list = lists.get(catalog);
  if (!list) {
    const categoryLabels = new Map(catalog.categories.map((category) => [category.id, appearanceCategoryLabel(category)]));
    const recommended: BrowserIcon[] = appearanceIcons.map((item) => ({ ...item, category: catalog.core[item.id] ?? "" }));
    const everything: BrowserIcon[] = [
      ...recommended,
      ...catalog.icons
        .map((item) => ({ id: item.id, label: item.label, group: categoryLabels.get(item.category) ?? "", words: item.words, category: item.category }))
        .sort((a, b) => labelOrder.compare(a.label, b.label)),
    ];
    list = { recommended, everything };
    lists.set(catalog, list);
  }
  return list;
}
</script>

<script setup lang="ts">
import t from "../../i18n/settings-appearance";
import { computed, nextTick, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import OptionPills from "./OptionPills.vue";
import SearchField from "./SearchField.vue";
import { appearanceGroupLabel, appearanceIconGroups, findAppearanceIcons } from "./appearanceIcons";
import { iconPackCatalog, readRecentIcons, rememberRecentIcon } from "../../iconPack";
import { moveIconFocus, type IconGridKey } from "./iconGrid";
import type { ChoiceOption } from "./types";
import { useSearchKeys } from "../../lib/searchKeys";

/**
 * The opened part of AppearancePicker ("Další ikony"): every icon a space or a page may take, the 87 of the
 * appearance catalog and the icon pack (iconPack.ts), with one ranked Czech and English search, category pills and
 * the Czech name under each drawing. Internal to the picker: it is mounted only once the pack catalog has loaded
 * (iconPackCatalog), and nothing else should use it. A long list of people, groups or files stays
 * SearchableChoiceList, whose rows carry a name with a description and are chosen while moving.
 *
 * The results are not a radio group: with almost two thousand options, choosing on every arrow key would change
 * the preview and the form at each step. They are a single-select listbox with one tab stop: Down from the search
 * enters it, arrows move in two dimensions (iconGrid.ts), Enter, Space or a click chooses, Escape returns to the
 * search. The first view is Doporučené, the 87 people know; typing there searches everything. A short query finds
 * hundreds of icons: the best 200 are drawn and "Zobrazit všech" adds the rest.
 */
const props = withDefaults(defineProps<{ color: string; variant?: "tile" | "ink"; disabled?: boolean }>(), { variant: "tile", disabled: false });
/** A search draws this many results; a short query finds up to 1 800, and mounting them all at the first keystroke is felt. */
const resultsCap = 200;
/** The chosen icon id. */
const icon = defineModel<string>({ required: true });
const query = defineModel<string>("query", { default: "" });

const catalog = iconPackCatalog.value!;
const { recommended, everything } = iconLists(catalog);
const byId = new Map(everything.map((item) => [item.id, item]));

const scope = ref(query.value.trim() ? "all" : "recommended");
const recent = ref(readRecentIcons());
const recentIcons = computed(() => recent.value.flatMap((id) => byId.get(id) ?? []));
const scopes = computed<ChoiceOption[]>(() => [
  { value: "all", label: t.browser.all },
  { value: "recommended", label: t.browser.recommended },
  ...(recentIcons.value.length ? [{ value: "recent", label: t.browser.recent }] : []),
  ...catalog.categories.map((category) => ({ value: category.id, label: appearanceCategoryLabel(category) })),
]);
const scopeIcons = computed<readonly BrowserIcon[]>(() => {
  if (scope.value === "all") return everything;
  if (scope.value === "recommended") return recommended;
  if (scope.value === "recent") return recentIcons.value;
  return everything.filter((item) => item.category === scope.value);
});
const searching = computed(() => !!query.value.trim());
/** The groups of the results; none is empty. A search and a single category are one group without a heading. */
const sections = computed(() => {
  const list = scopeIcons.value;
  const groups: { heading: string; icons: readonly BrowserIcon[] }[] = searching.value ? [{ heading: "", icons: findAppearanceIcons(query.value, list) }]
    : scope.value === "recommended" ? appearanceIconGroups.map((group) => ({ heading: appearanceGroupLabel(group), icons: list.filter((item) => item.group === group) }))
    : scope.value === "all" ? catalog.categories.map((category) => ({ heading: appearanceCategoryLabel(category), icons: list.filter((item) => item.category === category.id) }))
    : [{ heading: "", icons: list }];
  return groups.filter((group) => group.icons.length);
});
const matched = computed(() => sections.value.reduce((sum, section) => sum + section.icons.length, 0));
/** A search shows its best results first; the rest wait behind "Zobrazit všech". A category or "Vše" without a query is drawn whole. */
const showingAll = ref(false);
const capped = computed(() => searching.value && !showingAll.value && matched.value > resultsCap);
const listed = computed(() => (capped.value ? [{ ...sections.value[0]!, icons: sections.value[0]!.icons.slice(0, resultsCap) }] : sections.value));
const shown = computed(() => new Set(listed.value.flatMap((section) => section.icons.map((item) => item.id))));
/** The query finds nothing in the chosen scope, but would among every icon: the empty state offers to widen it. */
const elsewhere = computed(() => searching.value && scope.value !== "all" && !sections.value.length && findAppearanceIcons(query.value, everything).length > 0);
const countText = computed(() => (searching.value
  ? t.browser.found(matched.value, scopeIcons.value.length)
  : t.browser.icons(matched.value)));

const root = ref<HTMLElement>();
const results = ref<HTMLElement>();
/** Every group is a grid of the same columns; the resolved value lists one track for each. */
const columnsOf = (group: Element) => getComputedStyle(group).gridTemplateColumns.split(" ").length;
/** The columns of the groups' grid, read again when the list changes width: the estimated height of a group not drawn yet counts its rows. */
const columns = ref(3);
let resizing: ResizeObserver | undefined;
watch(results, (node) => {
  resizing?.disconnect();
  if (!node || typeof ResizeObserver !== "function") return;
  resizing = new ResizeObserver(() => {
    const group = node.querySelector(".appearance-browser-group");
    if (group) columns.value = columnsOf(group);
  });
  resizing.observe(node);
}, { flush: "post" });
onBeforeUnmount(() => resizing?.disconnect());
/** The option that last had the focus; a new query or scope forgets it. */
const focused = ref<string>();
/** Where Down from the search lands: the chosen option when it is shown, else the first. */
const entry = computed(() => (shown.value.has(icon.value) ? icon.value : sections.value[0]?.icons[0]?.id));
/** A search's best result, which Enter or Tab in the field chooses. */
const first = computed(() => (searching.value ? sections.value[0]?.icons[0]?.id : undefined));
// The first Esc empties the search, the next one goes on to the panel around it (P3, P8).
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; },
  take: () => { if (!first.value) return false; choose(first.value); } });
const tabStop = computed(() => (focused.value && shown.value.has(focused.value) ? focused.value : entry.value));

/** The scope typing left, so clearing the query returns to it instead of leaving the reader in "Vše". */
let typedFrom: string | undefined;
watch(query, (value, before) => {
  if (value.trim() && !before.trim() && (scope.value === "recommended" || scope.value === "recent")) {
    typedFrom = scope.value;
    scope.value = "all";
  } else if (!value.trim() && typedFrom) {
    scope.value = typedFrom;
    typedFrom = undefined;
  }
});
// A scope the reader picks while a query is active is theirs.
watch(scope, (value) => { if (value !== "all") typedFrom = undefined; });
watch(scope, () => { recent.value = readRecentIcons(); });
// A new query or scope starts over: no remembered option, the best results on top.
watch([query, scope], () => {
  focused.value = undefined;
  showingAll.value = false;
  if (results.value) results.value.scrollTop = 0;
});

function choose(id: string) {
  if (props.disabled) return;
  icon.value = id;
  focused.value = id;
  const remembered = rememberRecentIcon(id);
  // Its own view keeps its order until it is opened again, so the chosen option stays under the focus.
  if (scope.value !== "recent") recent.value = remembered;
}
const focusOption = (id: string) => results.value?.querySelector<HTMLElement>(`[role="option"][data-value="${id}"]`)?.focus();
function enterResults() {
  if (entry.value) focusOption(entry.value);
}
const focusSearch = () => root.value?.querySelector<HTMLElement>("[data-appearance-search]")?.focus();
/** The button is gone from under the focus: it goes back to the search, where Down enters the results. */
function searchEverywhere() {
  scope.value = "all";
  focusSearch();
}
/** The first icon it adds takes the focus: the button is gone, and the reader carries on where the list continues. */
async function showAll() {
  const next = sections.value[0]!.icons[resultsCap]!.id;
  showingAll.value = true;
  await nextTick();
  focusOption(next);
}

// One listener each on the list instead of three on every one of up to 1 864 options.
const optionOf = (target: EventTarget | null) => (target as Element).closest<HTMLElement>('[role="option"]');
const gridKeys: ReadonlySet<string> = new Set<IconGridKey>(["ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End"]);
function onClick(event: MouseEvent) {
  const id = optionOf(event.target)?.dataset.value;
  if (id) choose(id);
}
function onFocusin(event: FocusEvent) {
  const id = optionOf(event.target)?.dataset.value;
  if (id) focused.value = id;
}
function onKeydown(event: KeyboardEvent) {
  const option = optionOf(event.target);
  if (!option || event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === "Escape") {
    // Back to the search first; a panel around the picker closes from there.
    event.stopPropagation();
    focusSearch();
  } else if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    choose(option.dataset.value!);
  } else if (gridKeys.has(event.key)) {
    event.preventDefault();
    const groups = [...results.value!.querySelectorAll('[role="group"]')].map((group) => [...group.querySelectorAll<HTMLElement>('[role="option"]')]);
    const section = groups.findIndex((group) => group.includes(option));
    const at = { section, index: groups[section]!.indexOf(option) };
    const to = moveIconFocus(groups.map((group) => group.length), at, event.key as IconGridKey, columnsOf(option.parentElement!));
    // Down from the last row leaves the grid for the button after the best results, when there is one.
    if (event.key === "ArrowDown" && capped.value && to.section === at.section && to.index === at.index) {
      results.value!.querySelector<HTMLElement>("[data-appearance-show-all]")?.focus();
    } else {
      groups[to.section]![to.index]!.focus();
    }
  }
}
/** Up goes back into the grid, to the option the focus came from; Escape to the search, as from an option. */
function onShowAllKeydown(event: KeyboardEvent) {
  if (event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === "Escape") {
    event.stopPropagation();
    focusSearch();
  } else if (event.key === "ArrowUp" && tabStop.value) {
    event.preventDefault();
    focusOption(tabStop.value);
  }
}
</script>

<template>
  <div ref="root" class="appearance-browser" :data-variant="variant">
    <SearchField
      v-model="query"
      :label="t.browser.search"
      :placeholder="t.browser.searchPlaceholder(everything.length)"
      :disabled="disabled"
      wide
      data-appearance-search
      @keydown.down.prevent="enterResults"
      @keydown="keys.onKeydown"
    />
    <OptionPills v-model="scope" class="appearance-browser-scopes" :options="scopes" :label="t.browser.categories" hide-label :disabled="disabled" data-appearance-scopes />
    <p class="appearance-browser-count" role="status" data-appearance-count>{{ countText }}</p>
    <!-- The button after the best results is not an option, so it sits beside the listbox, inside the scrolling list. -->
    <div
      v-if="sections.length"
      ref="results"
      class="appearance-browser-results"
      @click="onClick"
      @focusin="onFocusin"
      @keydown="onKeydown"
    >
      <div class="appearance-browser-list" role="listbox" :aria-label="t.browser.listbox" data-appearance-results>
        <div v-for="section in listed" :key="section.heading" class="appearance-browser-group" role="group" :aria-label="section.heading || undefined" :style="{ '--group-rows': Math.ceil(section.icons.length / columns) }">
          <p v-if="section.heading" class="appearance-browser-heading" aria-hidden="true">{{ section.heading }}</p>
          <button
            v-for="item in section.icons"
            :key="item.id"
            type="button"
            class="appearance-browser-option"
            role="option"
            :aria-selected="item.id === icon"
            :tabindex="item.id === tabStop ? 0 : -1"
            :data-value="item.id"
            :data-first="item.id === first || undefined"
            :data-color="item.id === icon ? color : undefined"
            :title="item.label"
            :disabled="disabled"
          >
            <AtlasIcon :name="item.id" :size="20" />
            <span class="appearance-browser-label">{{ item.label }}</span>
          </button>
        </div>
      </div>
      <Button v-if="capped" type="button" variant="ghost" size="sm" class="appearance-browser-more" :disabled="disabled" data-appearance-show-all @click="showAll" @keydown="onShowAllKeydown">
        {{ t.browser.showAll(matched) }}
      </Button>
    </div>
    <p v-else class="appearance-browser-empty"><AtlasIcon name="search" :size="15" />{{ t.browser.empty }}</p>
    <Button v-if="elsewhere" type="button" variant="outline" size="sm" class="appearance-browser-everywhere" :disabled="disabled" data-appearance-search-all @click="searchEverywhere">{{ t.browser.everywhere }}</Button>
  </div>
</template>

<style scoped>
.appearance-browser { display: grid; gap: 10px; min-width: 0; container: appearance-browser / inline-size; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.appearance-browser-count { margin: 0; color: var(--muted); font-size: 12px; font-variant-numeric: tabular-nums; }

/* The list and its groups clip what is painted outside an option or the button, so their focus ring sits inside (the global ring reads this token). */
.appearance-browser-results { --focus-offset: -2px; display: grid; gap: 10px; max-height: 336px; overflow-y: auto; padding: 2px 6px 8px 2px; scrollbar-width: thin; scrollbar-gutter: stable; }
.appearance-browser-list { display: grid; gap: 10px; min-width: 0; }
/* A group outside the list's viewport is not laid out or painted until it is scrolled to or focused. Until it has been, its height
   is its rows (--group-rows, set per group) of option and gap, so the scroll thumb does not jump when one is drawn; the heading is not counted. */
.appearance-browser-group {
  --option-height: 66px; --grid-gap: 4px;
  display: grid; grid-template-columns: repeat(auto-fill, minmax(76px, 1fr)); gap: var(--grid-gap); min-width: 0; content-visibility: auto;
  contain-intrinsic-block-size: auto calc(var(--group-rows) * (var(--option-height) + var(--grid-gap)));
}
.appearance-browser-heading { grid-column: 1 / -1; margin: 0 0 2px; color: var(--muted); font-size: 11px; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.appearance-browser-option {
  display: grid; justify-items: center; align-content: start; gap: 4px; height: var(--option-height); min-width: 0; padding: 8px 3px 6px;
  border: 0; border-radius: 9px; background: transparent; color: var(--muted); font: inherit; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), color var(--atlas-motion-fast) var(--atlas-ease-out), transform var(--atlas-motion-fast) var(--atlas-ease-out);
}
/* Two lines, then an ellipsis; the whole name is in the title. */
.appearance-browser-label { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 2; overflow: hidden; max-width: 100%; font-size: 11px; line-height: 1.2; text-align: center; overflow-wrap: anywhere; }
@media (hover: hover) and (pointer: fine) {
  .appearance-browser-option:hover:not(:disabled, [aria-selected="true"]) { background: var(--hover); color: var(--text); }
}
.appearance-browser-option:active:not(:disabled) { transform: scale(var(--atlas-press)); }
/* Inside the box: the group and the list clip what is painted outside an option. */
.appearance-browser-option:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; --focus-offset: -2px; }
.appearance-browser-option[aria-selected="true"] { color: var(--appearance-fg); background: var(--appearance-bg); }
/* The result Enter in the search chooses, while the search has the focus. */
.appearance-browser:has([data-appearance-search]:focus) .appearance-browser-option[data-first]:not([aria-selected="true"]) { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
[data-variant="ink"] .appearance-browser-option[aria-selected="true"] { color: var(--appearance-ink); background: color-mix(in srgb, var(--appearance-ink) 12%, var(--surface)); }
.appearance-browser-option:disabled { cursor: not-allowed; }
/* The answer to a search that found nothing, and the way out of a category, fade in rather than pop over the vanished grid. */
.appearance-browser-empty { display: flex; align-items: center; gap: 8px; margin: 8px 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.appearance-browser-everywhere { justify-self: start; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }

/* In a narrow panel the pills stay one line that scrolls sideways instead of wrapping into five. */
@container appearance-browser (max-width: 600px) {
  /* The padding leaves room for the focus ring that overflow would clip; the margin takes it back on the left, where the pills line up with the field. Not on the right: it would spill past the container. */
  .appearance-browser-scopes :deep(.option-pills-group) { flex-wrap: nowrap; overflow-x: auto; scrollbar-width: none; margin: -4px 0 -4px -4px; padding: 4px; }
  .appearance-browser-scopes :deep(.option-pill) { flex: none; white-space: nowrap; }
}
</style>
