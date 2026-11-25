<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from "vue";
import { findInEditor, openEditorSearch } from "@atlas/markdown/plugins/editor-tools";
import { riseIn, RISE_IN_ROWS, scrollBehavior } from "../motion/aiMotion";
import {
  editingEditor, findMatches, hideShownBlocks, indexPage, placeOf, pulseMatch as pulse, rangeOf, revealMatch, snippetAround, stepResult,
  type PageIndex,
} from "../editor/pageSearch";
import { pageSearchFocus, pageSearchQuery } from "../editor/pageSearchState";
import AtlasIcon from "./AtlasIcon.vue";
import SearchField from "./settings/SearchField.vue";
import SearchHighlight from "./SearchHighlight.vue";
import { Button } from "./ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";

/**
 * Hledat ve stránce (3.5 J), the tab „Hledat“ of the page dock (PageSidePanel): every match marked in the page, the current one
 * strongly, a list of the matches with the words around them and where they lie, and quick jumps. Unlike the editor's „Najít a
 * nahradit“ (a popover over the editor while editing, decorations per match) it works on the rendered page in reading and in
 * editing and finds hidden text (folded sections, requirement notes); replacing stays the editor's, one button away.
 * Keys: Enter jumps to the preselected first result and then on, Shift+Enter back, ↑ ↓ through the list, F3 / Shift+F3 anywhere
 * while the tab is open, Esc back to the page at the current result.
 */
const props = defineProps<{ root: HTMLElement | null | undefined }>();

interface Result { range: Range; snippet: string; place: string }
const results = shallowRef<Result[]>([]);
const more = ref(false);
const current = ref(0);
/** The result the page was last moved to; Enter first goes to the preselected one, then on. */
let landed = -1;
const searched = ref("");
const editing = ref(false);
const list = ref<HTMLElement>();
const field = ref<InstanceType<typeof SearchField>>();
const listId = `page-search-${useId()}`;
const optionId = (index: number) => `${listId}-${index}`;

const supported = typeof CSS !== "undefined" && "highlights" in CSS && typeof Highlight === "function";
const ALL = "atlas-page-search", CURRENT = "atlas-page-search-current";
function paint() {
  if (!supported) return;
  const ranges = results.value.map(result => result.range), active = ranges[current.value];
  if (!ranges.length) { CSS.highlights.delete(ALL); CSS.highlights.delete(CURRENT); return; }
  CSS.highlights.set(ALL, new Highlight(...ranges));
  const strong = new Highlight(...(active ? [active] : []));
  strong.priority = 1;
  CSS.highlights.set(CURRENT, strong);
}

// The page's text is read once and again only after its DOM changed (typing, another version, reading ↔ editing). While editing
// a keystroke only marks it stale: it is read after a second of quiet, or at once by Enter or F3 (perf P8).
let index: PageIndex | undefined;
let timer: ReturnType<typeof setTimeout> | undefined;
const QUIET_MS = 250, TYPING_QUIET_MS = 1000;
const observer = typeof MutationObserver === "function" ? new MutationObserver(() => {
  index = undefined;
  if (pageSearchQuery.value) schedule(editing.value ? TYPING_QUIET_MS : QUIET_MS);
}) : undefined;
watch(() => props.root, root => {
  observer?.disconnect();
  index = undefined;
  if (root) observer?.observe(root, { childList: true, characterData: true, subtree: true });
  schedule(0);
}, { immediate: true });

function schedule(ms: number) { clearTimeout(timer); timer = setTimeout(search, ms); }
watch(pageSearchQuery, () => schedule(120));

function search() {
  const root = props.root, query = pageSearchQuery.value;
  const again = query === searched.value, before = results.value.length;
  searched.value = query;
  if (!root || !query.trim()) { results.value = []; more.value = false; landed = -1; paint(); return; }
  index ??= indexPage(root);
  const found = findMatches(index.text, query), page = index;
  results.value = found.matches.flatMap(match => {
    const range = rangeOf(page, match);
    return range ? [{ range, snippet: snippetAround(page.text.original, match), place: placeOf(page, match) }] : [];
  });
  more.value = found.more;
  editing.value = !!root.querySelector('.ProseMirror[contenteditable="true"]');
  // A changed page keeps the reader's place in the list; a new search starts at its first result.
  current.value = again ? Math.min(current.value, Math.max(0, results.value.length - 1)) : 0;
  if (!again) landed = -1;
  paint();
  if (!before && results.value.length) void nextTick(() => { if (list.value) riseIn(list.value.children, RISE_IN_ROWS); });
}

const status = computed(() => {
  if (!searched.value.trim()) return "";
  if (!results.value.length) return t.psearch.none;
  return t.psearch.position(current.value + 1, results.value.length, more.value);
});

/** Stops a landing that still waits for the scroll to end (a new jump, or the panel closed). */
let cancelLanding: (() => void) | undefined;

/** The page centred on the match once it has stopped, then one short pulse around it. */
function land(range: Range) {
  cancelLanding?.();
  const rect = range.getBoundingClientRect();
  const delta = rect.top + rect.height / 2 - window.innerHeight / 2;
  if (Math.abs(delta) < 4) { pulse(range); return; }
  let settled = false;
  const settle = () => {
    if (settled) return;
    settled = true;
    cancelLanding = undefined;
    window.removeEventListener("scrollend", settle);
    clearTimeout(fallback);
    // Blocks `content-visibility` skipped took their real height on the way: the match may have moved.
    const after = range.getBoundingClientRect(), off = after.top + after.height / 2 - window.innerHeight / 2;
    if (Math.abs(off) > window.innerHeight / 4) window.scrollBy({ top: off, behavior: "instant" });
    pulse(range);
  };
  const fallback = setTimeout(settle, 900);
  window.addEventListener("scrollend", settle);
  cancelLanding = () => { settled = true; window.removeEventListener("scrollend", settle); clearTimeout(fallback); cancelLanding = undefined; };
  window.scrollBy({ top: delta, behavior: scrollBehavior(document.documentElement) });
}

function jump(to: number) {
  const result = results.value[to];
  if (!result) return;
  current.value = to;
  landed = to;
  paint();
  revealMatch(result.range);
  // Unfolding redraws before the next frame; only then has the match a place to scroll to.
  requestAnimationFrame(() => { if (result.range.startContainer.isConnected) land(result.range); });
}
const go = (delta: number) => { if (results.value.length) jump(stepResult(current.value, results.value.length, delta)); };
function next(back: boolean) {
  if (!index && searched.value) { clearTimeout(timer); search(); }
  if (!results.value.length) return;
  if (!back && landed !== current.value) jump(current.value);
  else go(back ? -1 : 1);
}

/** Esc: the focus goes back to the page, on the current result (the editor's caret selects it; in reading the text is selected). */
function backToPage() {
  const result = results.value[current.value];
  if (!result) return;
  if (landed !== current.value) jump(current.value);
  const editor = editingEditor(result.range.startContainer);
  if (editor) { editor.view.focus(); return; }
  const element = result.range.startContainer.parentElement;
  if (!element) return;
  if (!element.hasAttribute("tabindex")) {
    element.setAttribute("tabindex", "-1");
    element.addEventListener("blur", () => element.removeAttribute("tabindex"), { once: true });
  }
  element.focus({ preventScroll: true });
  const selection = window.getSelection();
  selection?.removeAllRanges();
  selection?.addRange(result.range.cloneRange());
}

function keys(event: KeyboardEvent) {
  if (event.isComposing || event.altKey || event.ctrlKey || event.metaKey) return;
  if (event.key === "Enter") { event.preventDefault(); next(event.shiftKey); }
  else if ((event.key === "ArrowDown" || event.key === "ArrowUp") && results.value.length) { event.preventDefault(); go(event.key === "ArrowDown" ? 1 : -1); }
  else if (event.key === "Escape" && !event.shiftKey && results.value.length) {
    // The field's own first Esc would clear it (P3); here the owner's rule: back to the page, the search kept for Ctrl+F.
    event.preventDefault(); event.stopImmediatePropagation(); backToPage();
  }
}
/** F3 / Shift+F3 wherever the focus is, while the tab is open: the browser's own find would only find the visible text. */
function f3(event: KeyboardEvent) {
  if (event.key !== "F3" || event.ctrlKey || event.metaKey || event.altKey || !results.value.length) return;
  event.preventDefault();
  next(event.shiftKey);
}

/** Replacing stays the editor's own „Najít a nahradit“, opened with this search. */
function replace() {
  const surface = props.root?.querySelector('.ProseMirror[contenteditable="true"]') as (Element & { editor?: Parameters<typeof findInEditor>[0] }) | null;
  const editor = surface?.editor;
  if (!editor || editor.isDestroyed) return;
  findInEditor(editor, pageSearchQuery.value);
  void openEditorSearch(editor);
}

watch(current, at => { list.value?.querySelector(`#${CSS.escape(optionId(at))}`)?.scrollIntoView({ block: "nearest" }); }, { flush: "post" });

// Ctrl+F (App.vue) asks for the field, also before this tab's code has arrived.
let focused = 0;
function focusField() {
  const input = (field.value?.$el as HTMLElement | undefined)?.querySelector("input");
  input?.focus();
  input?.select();
}
watch(pageSearchFocus, request => { if (request > focused) { focused = request; void nextTick(focusField); } });
onMounted(() => {
  window.addEventListener("keydown", f3);
  if (pageSearchFocus.value > focused) { focused = pageSearchFocus.value; focusField(); }
});
onBeforeUnmount(() => {
  cancelLanding?.();
  window.removeEventListener("keydown", f3);
  observer?.disconnect();
  clearTimeout(timer);
  hideShownBlocks();
  results.value = [];
  paint();
});
</script>

<template>
  <section class="page-search" :aria-label="t.psearch.search">
    <div class="page-search-bar">
      <SearchField ref="field" v-model="pageSearchQuery" wide :label="t.psearch.search" :placeholder="t.psearch.searchEllipsis" data-page-search-input
        role="combobox" aria-autocomplete="list" :aria-expanded="results.length > 0" :aria-controls="listId"
        :aria-activedescendant="results.length ? optionId(current) : undefined" @keydown="keys" />
      <span class="page-search-count" role="status" aria-live="polite">{{ status }}</span>
      <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" :disabled="!results.length" :aria-label="t.psearch.previousLabel" @click="next(true)"><AtlasIcon name="chevron-up" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.psearch.previousTitle }}</TooltipContent></Tooltip>
      <Tooltip><TooltipTrigger as-child><Button variant="ghost" size="icon-sm" :disabled="!results.length" :aria-label="t.psearch.nextLabel" @click="next(false)"><AtlasIcon name="chevron-down" :size="16" /></Button></TooltipTrigger><TooltipContent>{{ t.psearch.nextTitle }}</TooltipContent></Tooltip>
    </div>
    <p v-if="!searched.trim()" class="page-search-hint">{{ t.psearch.hint0 }} <kbd>Enter</kbd> {{ t.psearch.hint1 }} <kbd>Shift Enter</kbd> {{ t.psearch.hint2 }} <kbd>↑</kbd> <kbd>↓</kbd> {{ t.psearch.hint3 }} <kbd>Esc</kbd> {{ t.psearch.hint4 }}</p>
    <Button v-if="editing && results.length" variant="outline" size="sm" class="page-search-replace" @click="replace"><AtlasIcon name="search" :size="15" />{{ t.psearch.replace }}</Button>
    <ol v-show="results.length" :id="listId" ref="list" class="page-search-results" role="listbox" :aria-label="t.psearch.results">
      <li v-for="(result, at) in results" :id="optionId(at)" :key="at" role="option" class="page-search-result" :aria-selected="at === current" @click="jump(at)">
        <small v-if="result.place" class="page-search-place">{{ result.place }}</small>
        <span class="page-search-snippet"><SearchHighlight :text="result.snippet" :query="searched" /></span>
      </li>
    </ol>
  </section>
</template>

<style scoped>
.page-search { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
.page-search-bar { display: flex; align-items: center; gap: 4px; min-width: 0; }
.page-search-bar > :first-child { flex: 1; min-width: 0; }
.page-search-count { flex: none; min-width: 3.5em; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; text-align: right; }
.page-search-hint { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.6; }
.page-search-replace { align-self: flex-start; }
.page-search-results { display: flex; flex-direction: column; gap: 2px; margin: 0; padding: 0; list-style: none; }
.page-search-result {
  display: flex; flex-direction: column; gap: 2px; padding: 7px 10px; border-radius: 8px; cursor: pointer;
  box-shadow: inset 2px 0 0 transparent;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) { .page-search-result:hover { background: var(--hover); } }
.page-search-result[aria-selected="true"] { background: var(--accent-soft, var(--hover)); box-shadow: inset 2px 0 0 var(--accent); }
.page-search-place { overflow: hidden; color: var(--muted); font-size: 11.5px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.page-search-snippet { color: var(--text); font-size: 13px; line-height: 1.45; overflow-wrap: anywhere; }
</style>

<style>
/* The page's matches (CSS Custom Highlight API; custom properties do not reach highlight pseudo-elements everywhere, so the
   colours are literal): every match softly, the current one strongly and underlined, as the browser's own find does. */
::highlight(atlas-page-search) { background-color: rgb(250 204 21 / .32); }
::highlight(atlas-page-search-current) { background-color: rgb(249 115 22 / .55); color: #1c1917; text-decoration: underline 2px rgb(194 65 12); text-underline-offset: 3px; }
:root[data-mode="dark"] ::highlight(atlas-page-search) { background-color: rgb(250 204 21 / .26); }
:root[data-mode="dark"] ::highlight(atlas-page-search-current) { background-color: rgb(251 146 60 / .85); color: #1c1917; text-decoration-color: rgb(255 237 213); }
</style>
