<script setup lang="ts">
import t from "../../i18n/boards";
import { createSuggestionList } from "@atlas/markdown/core";
import { onBeforeUnmount, ref, useId, watch } from "vue";
import { createPageSource } from "../../editorDirectories";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { foldText } from "../../lib/foldText";
import { isPickEnter } from "../../lib/pickEnter";
import { useSearchKeys } from "../../lib/searchKeys";
import { useLexicon } from "../../linking/lexicon";
import BoardPickerPanel from "./pickers/BoardPickerPanel.vue";

/**
 * Which page a new card shows: the editor's `[[` page list, its search (`createPageSource`: the current space first, then the
 * others, only pages the reader may see) and its rows, in the board's picker panel. Typing preselects the first page, arrows move,
 * Enter or Tab adds it (an Enter before the answer came adds the answer's first page), the first Esc empties the search and the next
 * one leaves. The space's pages its lexicon knows are the first rows, at once (P17: the pages of the space with nothing typed, those
 * whose title has the text, a title beginning with it first); the search only refines them. Private to the board; the page itself is
 * never loaded here.
 */
const props = defineProps<{ open: boolean; spaceId?: string; anchor?: HTMLElement | null }>();
const emit = defineEmits<{ pick: [page: { id: string; title: string }]; cancel: [] }>();

const listId = `board-page-picker-${useId()}`;
const query = ref("");
const active = ref("");
let pages: { id: string; title: string }[] = [];
// Until the typed text is answered the shown pages are the previous text's: Enter then waits for the answer.
let waiting = false;
const hint = t.pagePicker.hint;
const list = createSuggestionList({ document, label: t.pagePicker.pages, testId: "board-page-list", emptyText: t.pagePicker.empty,
  onSelect: id => choose(id), onActiveChange: id => { active.value = id; } });
list.element.id = listId;

const source = createPageSource(() => [], { spaceId: () => props.spaceId });
const lexicon = useLexicon(() => props.spaceId);
const row = (page: { id: string; title: string; detail?: string }) => ({ id: page.id, title: page.title, description: page.detail, icon: "svg:file-text" });
/** The lexicon's pages for the text, shown before (and when) nothing has answered; false when it knows none. */
function showKnown(text: string): boolean {
  const term = foldText(text.trim()), titled = (lexicon.value?.pages ?? []).map(page => ({ page, title: foldText(page.title) }));
  const known = titled.filter(({ title }) => title.includes(term)).sort((a, b) => Number(!b.title.startsWith(term)) - Number(!a.title.startsWith(term)))
    .slice(0, 8).map(({ page }) => row(page));
  if (!known.length) return false;
  pages = known;
  list.update(known);
  return true;
}
if (!showKnown("")) list.setStatus("ready", hint);
// A lexicon that comes while nothing is typed fills the empty list.
watch(lexicon, () => { if (!query.value.trim()) showKnown(""); });
const lookup = createDebouncedLookup({
  find: async (text, signal) => (await source.search(text, { signal })).map(row),
  onResult: found => {
    waiting = false;
    if (query.value.trim()) { pages = found; list.update(found); } else if (!showKnown("")) { pages = []; list.setStatus("ready", hint); }
    keys.settled();
  },
  empty: () => [],
  delayMs: 150,
  minLength: 1,
});
// Enter or Tab adds the active page (an Enter before the answer waits for it); the first Esc empties the search, the next one closes the panel (P3, P8).
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => waiting, empty: () => true, keepFocus: true,
  take: () => list.onKeyDown(new KeyboardEvent("keydown", { key: "Enter" })) });
watch(query, text => { waiting = !!text.trim(); showKnown(text); lookup.update(text); });
watch(() => props.open, open => {
  if (open) return;
  lookup.cancel();
  query.value = "";
  waiting = false;
  if (!showKnown("")) pages = [];
});
onBeforeUnmount(() => { lookup.cancel(); list.destroy(); });

function choose(id: string) {
  const page = pages.find(entry => entry.id === id);
  if (page) emit("pick", { id: page.id, title: page.title });
}
function onKeydown(event: KeyboardEvent) {
  if (keys.onKeydown(event)) return;
  if ((event.key !== "Enter" || isPickEnter(event)) && list.onKeyDown(event)) event.preventDefault();
}
// The list is the editor's own element: it joins the panel each time the panel opens, in the same render (P17).
const host = (element: unknown) => { if (element instanceof HTMLElement) element.append(list.element); };
</script>

<template>
  <BoardPickerPanel :open="open" :anchor="anchor" :title="t.pagePicker.title" @close="emit('cancel')">
    <input v-model="query" class="md-input board-page-query" type="text" role="combobox" :aria-label="t.pagePicker.search" aria-autocomplete="list" aria-expanded="true"
      :aria-controls="listId" :aria-activedescendant="active || undefined" :placeholder="t.pagePicker.placeholder" autocomplete="off" spellcheck="false" data-board-page-query @keydown="onKeydown" />
    <div :ref="host" class="board-page-list" />
    <p class="board-picker-keys"><kbd>↑↓</kbd> {{ t.pagePicker.select }} · <kbd>Enter</kbd> {{ t.pagePicker.add }} · <kbd>Esc</kbd> {{ t.pagePicker.close }}</p>
  </BoardPickerPanel>
</template>

<style scoped>
.board-page-query { width: 100%; caret-color: var(--text); }
.board-page-list :deep(.md-suggestion-list) { position: static; width: auto; max-width: none; max-height: min(320px, 45vh); border: 0; box-shadow: none; animation: none; }
</style>
