<script setup lang="ts">
import { createSuggestionList } from "@atlas/markdown/core";
import { nextTick, onBeforeUnmount, onMounted, ref, useId } from "vue";
import t from "../i18n/projector";
import { learnAlias } from "../contacts/contactSources";
import { createDebouncedLookup } from "../lib/debouncedLookup";
import { isPickEnter } from "../lib/pickEnter";
import { learnParty, type PartyRow, type PartySuggest } from "./partySuggestions";

/**
 * The party field of a requirement („od koho“, „komu“, „kdo“, „na koho čekáme“): free text with the known names offered as it is
 * typed (partySuggestions.ts), the editor's suggestion list under it. The first is preselected; arrows move; Enter takes it and saves the form
 * around, Tab takes it and goes on to the next field, Esc closes the list and keeps what was typed. An Enter with the list closed is the form's.
 * A person of the directory picked after typing something that is no plain beginning of the name („MMu“) teaches the directory
 * contact that text as an alias when the form around applies the party (partySuggestions.ts `learnParty`, the server decides;
 * `learn` is the gallery's seam). A cancelled form teaches nothing, nor a row of an older text taken before the answer came.
 */
const value = defineModel<string>({ required: true });
/** The directory contact the party is (plan 3.1 §2.3): set by a picked person of the directory, "" once the text is typed over. */
const contactId = defineModel<string>("contactId", { default: "" });
const props = withDefaults(defineProps<{ suggest: PartySuggest; placeholder?: string; name?: string; label?: string;
  learn?: typeof learnAlias }>(), { learn: learnAlias });
const listId = `party-${useId()}`;
const host = ref<HTMLElement>();
const open = ref(false);
const active = ref("");
let rows: PartyRow[] = [];
// PR2-1: an Enter typed before the answer waits for it (as ContactPicker's does), so „senc“ + Enter takes the first name instead of saving „senc“.
let waiting = false, enterAhead = false;

const list = createSuggestionList({ document, label: t.party.knownNames, testId: "requirement-party-list", emptyText: t.party.noKnownName,
  onSelect: id => take(id), onActiveChange: id => { active.value = id; } });
list.element.id = listId;
function show(found: PartyRow[]) {
  rows = found;
  list.update(found.map(row => ({ id: row.id, title: row.value, description: row.contactId ? t.party.fromDirectory(row.description) : row.description, icon: "svg:user" })));
}
// P17: the names known already (the page's, the space's, the cached people) are the first rows; the answer only refines them.
const known = (text: string) => { const found = props.suggest.cached?.(text) ?? []; if (found.length) show(found); };
known(value.value);
const lookup = createDebouncedLookup<PartyRow[]>({ find: (text, signal) => props.suggest(text, signal), empty: () => [], delayMs: 120, minLength: 0,
  onResult: found => {
    waiting = false; open.value = found.length > 0 && open.value;
    show(found);
    if (!enterAhead) return;
    enterAhead = false;
    if (found.length) { take(found[0]!.id); save(); } else host.value?.closest("form")?.requestSubmit();
  } });

/** The person of the directory picked and the text it was picked for, learned when the form applies the party. */
let picked: { row: PartyRow; typed: string } | undefined;
function take(id: string) {
  const row = rows.find(entry => entry.id === id);
  if (row) {
    // Rows still answering an older text (a Tab before the answer) teach nothing: „MMu“ must not go to whoever „MM“ listed first.
    picked = row.contactId && !waiting ? { row, typed: value.value } : undefined;
    value.value = row.value; contactId.value = row.contactId ?? "";
  }
  open.value = false;
}
/** One Enter picks and saves (3.5.1 B9): the form applies the value the pick has just set. */
function save() { void nextTick(() => form?.requestSubmit()); }
function applied() {
  if (picked && contactId.value === picked.row.contactId) learnParty(picked.row, picked.typed, props.learn);
  picked = undefined;
}
function offer() { open.value = true; waiting = true; enterAhead = false; known(value.value); lookup.update(value.value); }
function onInput() { picked = undefined; contactId.value = ""; offer(); }
function onKeydown(event: KeyboardEvent) {
  if (waiting && value.value.trim() && isPickEnter(event)) { event.preventDefault(); enterAhead = true; return; }
  if (!open.value || !rows.length) {
    // ↓ only offers the names: the party and its contact stay as they are, so leaving the panel saves nothing.
    if (event.key === "ArrowDown") { event.preventDefault(); offer(); }
    return;
  }
  if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); open.value = false; }
  // Tab takes the highlighted name and goes on to the next field.
  else if (event.key === "Tab" && !event.shiftKey) list.onKeyDown(new KeyboardEvent("keydown", { key: "Enter" }));
  else if ((event.key !== "Enter" || isPickEnter(event)) && list.onKeyDown(event)) { event.preventDefault(); if (event.key === "Enter") save(); }
}
// Capturing: before the form's own submit handler, which may close the panel and unmount this field.
let form: HTMLFormElement | null | undefined;
onMounted(() => { host.value?.append(list.element); form = host.value?.closest("form"); form?.addEventListener("submit", applied, true); });
onBeforeUnmount(() => { form?.removeEventListener("submit", applied, true); lookup.cancel(); list.destroy(); });
</script>

<template>
  <span class="party-input">
    <input v-model="value" class="md-input" :name="name" maxlength="200" :placeholder="placeholder" :aria-label="label" role="combobox" aria-autocomplete="list"
      :aria-expanded="open" :aria-controls="listId" :aria-activedescendant="open && active ? active : undefined" autocomplete="off" spellcheck="false"
      data-requirement-party-input @input="onInput" @keydown="onKeydown" @blur="open = false; enterAhead = false" />
    <div v-show="open" ref="host" class="party-input-list" />
  </span>
</template>

<style scoped>
.party-input { display: grid; min-width: 0; }
.party-input-list :deep(.md-suggestion-list) { position: static; width: auto; max-width: none; margin-top: 4px; box-shadow: none; }
</style>
