<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { createSuggestionList } from "@atlas/markdown/core";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from "vue";
import { errorMessage } from "../../api";
import { emptyContactForm, validateContactForm, type ContactFormErrors } from "../../contacts/contactForm";
import type { ContactAnswer, ContactAsk, ContactDecision } from "../../contacts/contactLinking";
import { describePeople, rankPeople, sameName, type PersonHit } from "../../contacts/contactSearch";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { listKeys, ownEscape, useSearchKeys } from "../../lib/searchKeys";
import type { TypeAhead } from "../../lib/typeAhead";
import InlineReveal from "../settings/InlineReveal.vue";
import ContactForm from "./ContactForm.vue";
import ContactMatchPanel from "./ContactMatchPanel.vue";

/**
 * One person into a text, from the keyboard, where the caret is (the editor's „/Kontakt“ and „/Kontakt v textu“): type a name in any
 * order and form („jan kovarik“ finds „Honza Kovařík“), the first person is preselected, arrows move, Enter or Tab puts the person in, the
 * first Esc empties the search and the next one leaves (the field says so to the popover while it holds text: `data-md-own-escape`).
 * An Enter pressed before the answer came takes the answer's first person. Two people of one name read apart by their
 * organization, e-mail and where they were found. The last row, „Nový kontakt „…““, reveals the contact form with the typed name; a
 * name that is already there is said, with those people to take instead. Every person picked, new or found, is linked to the
 * directory first (`link`, contactLinking.ts); what its match asks shows in place of the list (ContactMatchPanel), then the person goes in.
 * Why not the board's BoardContactPicker: that one ticks several directory contacts for cards in a dialog; this one puts one person
 * in a text at the caret and never leaves the keyboard. Both search through contacts/contactSources.ts.
 */
const props = defineProps<{
  /** `cached`: the people already known (the page's roster), ranked by the text, read at once so the first render has rows (P17); the answer replaces them. */
  search: ((text: string, signal: AbortSignal) => Promise<PersonHit[]>) & { cached?: (text: string) => PersonHit[] };
  /** Links the person to the directory contact, asking what a match needs; null: the person went back. Without it the pick goes in as it is. */
  link?: (contact: MarkdownContact, ask: ContactAsk) => Promise<MarkdownContact | null>;
  pick: (contact: MarkdownContact) => void;
  close: () => void;
  /** The editor's own contact rules (`normalizeContact`). */
  normalize: (value: unknown) => unknown;
  /** What was typed while the picker opened: the first search, and an Enter after it picks its first person. */
  typeAhead?: TypeAhead;
  /** A new contact of this name, matched against the directory at once (the typed party picker's „Nový kontakt“): no search, no form. */
  create?: string;
  /** Called after a person found by the search went in, with what was searched: the directory learns „MMu“ or „Mára“ as their alias
   * (contactSources.ts `learnAlias`, which teaches nothing for a plain beginning of the name). */
  learn?: (hit: Pick<PersonHit, "contact" | "objectId">, query: string) => void;
}>();

const listId = `contact-picker-${useId()}`;
const query = ref("");
const known = (text: string) => props.search.cached?.(text) ?? [];
const hits = ref<PersonHit[]>(known(""));
const active = ref("");
const creating = ref(false);
const form = ref(emptyContactForm());
const errors = ref<ContactFormErrors>({});
const error = ref("");
const busy = ref(false);
const input = ref<HTMLInputElement>();
const host = ref<HTMLElement>();
const formHost = ref<HTMLElement>();
let waiting = false;
const deciding = shallowRef<{ decision: ContactDecision; answer: (answer: ContactAnswer | null) => void } | null>(null);
const ask: ContactAsk = decision => new Promise(settle => { deciding.value = { decision, answer: answer => { deciding.value = null; settle(answer); } }; });

const NEW = "new";
const list = createSuggestionList({ document, label: t.picker.contacts, testId: "contact-picker-list", emptyText: t.picker.nobody,
  onSelect: id => choose(id), onActiveChange: id => { active.value = id; } });
list.element.id = listId;

const shown = computed(() => rankPeople(query.value, hits.value).slice(0, 8));
watch([shown, query], ([people, text]) => {
  const lines = describePeople(people);
  list.update([...people.map(hit => ({ id: hit.key, title: hit.contact.name, description: lines.get(hit.key) || undefined, icon: "svg:user" })),
    { id: NEW, title: text.trim() ? t.picker.newNamed(text.trim()) : t.picker.newContact, description: t.picker.willAdd, icon: "svg:plus" }]);
}, { immediate: true });

const lookup = createDebouncedLookup<PersonHit[]>({
  find: (text, signal) => props.search(text, signal), empty: () => [], delayMs: 150, minLength: 1,
  // The list is redrawn by the watcher above: an Enter given before the answer takes the answer's first person once it is there.
  onResult: found => { waiting = false; hits.value = found.length ? found : known(query.value); void nextTick(keys.settled); },
});
// Enter or Tab puts the active person in (an Enter before the answer waits for it); the first Esc empties the search, the next one
// closes the popover. Nothing typed and no arrow pressed, nothing to take: the lone „Nový kontakt“ row is not a result and is not highlighted (P3, P8).
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => waiting, empty: () => true, keepFocus: true,
  take: () => list.onKeyDown(new KeyboardEvent("keydown", { key: "Enter" })) });
watch(query, text => {
  waiting = !!text.trim();
  // The people known already answer the typed text in this frame; the search then replaces them.
  const early = known(text);
  if (early.length) hits.value = early;
  lookup.update(text);
});

function choose(id: string) {
  if (id === NEW) return startNew();
  const hit = hits.value.find(entry => entry.key === id);
  // What was searched, read now: the pick closes the picker.
  const searched = query.value;
  if (hit) void put(withObjectId(hit), linked => props.learn?.({ contact: hit.contact, objectId: linked.objectId ?? hit.objectId }, searched));
}
async function put(contact: MarkdownContact, picked?: (linked: MarkdownContact) => void) {
  if (!props.link) { props.pick(contact); picked?.(contact); return; }
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    const linked = await props.link(contact, ask);
    if (linked) { props.pick(linked); picked?.(linked); }
    else void nextTick(() => (creating.value ? formHost.value?.querySelector<HTMLInputElement>("[data-contact-name]") : input.value)?.focus());
  } catch (failure) {
    error.value = errorMessage(failure);
  } finally {
    busy.value = false;
  }
}
/** A directory contact is picked with its object id, so the page can say where the contact is used. */
const withObjectId = (hit: PersonHit): MarkdownContact => (hit.objectId ? { ...hit.contact, objectId: hit.objectId } : hit.contact);
function startNew() {
  form.value = { ...emptyContactForm(), name: query.value.trim() };
  errors.value = {};
  creating.value = true;
  void nextTick(() => formHost.value?.querySelector<HTMLInputElement>("[data-contact-name]")?.focus());
}

/** The people of the name the new contact has, to take instead. */
const same = computed(() => (creating.value ? sameName(form.value.name, hits.value) : []));
const sameLines = computed(() => describePeople(same.value));

async function add() {
  error.value = "";
  const result = validateContactForm(form.value, props.normalize);
  errors.value = result.errors;
  if (result.contact) await put(result.contact);
}

const lit = keys.lit;
function onKeydown(event: KeyboardEvent) {
  if (!keys.onKeydown(event)) listKeys(event, keys, list);
}
// Esc in the form goes back to the search; the popover's own Esc closes the picker.
function onFormKeydown(event: KeyboardEvent) {
  if (event.key !== "Escape") return;
  event.preventDefault();
  event.stopPropagation();
  creating.value = false;
  input.value?.focus();
}

onMounted(() => {
  host.value?.append(list.element);
  const ahead = props.typeAhead?.take();
  if (ahead?.text) { query.value = ahead.text; if (ahead.enter) keys.ahead(); }
  input.value?.focus({ preventScroll: true });
  // The same path as „Nový kontakt“ in the list: the match question, then the contact; going back lands in the search with the name.
  const name = props.create?.trim();
  if (name) { query.value = name; void put(props.normalize({ name }) as MarkdownContact); }
});
onBeforeUnmount(() => { lookup.cancel(); list.destroy(); });
</script>

<template>
  <div class="contact-picker" :data-untouched="!lit || undefined" data-contact-picker>
    <input ref="input" v-model="query" class="md-input contact-picker-query" type="text" role="combobox" :aria-label="t.picker.search" aria-autocomplete="list" aria-expanded="true"
      :aria-controls="listId" :aria-activedescendant="(lit && active) || undefined" :placeholder="t.picker.placeholder" autocomplete="off" spellcheck="false"
      data-contact-picker-query :data-md-own-escape="ownEscape(query)" @keydown="onKeydown" />
    <ContactMatchPanel v-if="deciding" :decision="deciding.decision" @answer="deciding.answer" />
    <!-- While the new contact's form is open, the people of its name are offered above the form, so the list steps aside (PR-11). -->
    <div v-show="!creating && !deciding" ref="host" class="contact-picker-list" @mouseover="keys.move()" />
    <p v-if="!creating && !deciding" class="contact-picker-keys"><kbd>↑↓</kbd> {{ t.picker.select }} · <kbd>Enter</kbd> {{ t.picker.insert }} · <kbd>Esc</kbd> {{ t.picker.close }}</p>
    <InlineReveal v-model:open="creating">
      <div v-show="!deciding" ref="formHost" class="contact-picker-new" @keydown="onFormKeydown">
        <div v-if="same.length" class="contact-picker-same" role="status" data-contact-picker-same>
          <p>{{ t.picker.same(form.name.trim(), same.length) }}</p>
          <button v-for="hit in same" :key="hit.key" type="button" class="md-btn" @click="put(withObjectId(hit))">{{ hit.contact.name }}<small v-if="sameLines.get(hit.key)"> · {{ sameLines.get(hit.key) }}</small></button>
        </div>
        <ContactForm v-model="form" :errors="errors" :busy="busy" :submit-label="t.picker.submit" @submit="add" />
      </div>
    </InlineReveal>
    <p v-if="error" class="contact-picker-error" role="alert">{{ error }}</p>
  </div>
</template>

<style scoped>
.contact-picker { display: grid; gap: 8px; min-width: 0; }
.contact-picker .contact-picker-query { width: 100%; caret-color: var(--text); }
/* Nothing typed or moved to yet: the list's selected first row is not drawn, as Enter takes none. */
.contact-picker[data-untouched] :deep(.md-suggestion-list .row.on) { background: transparent; color: var(--text); }
.contact-picker[data-untouched] :deep(.md-suggestion-list .row.on .ic) { color: var(--muted); }
.contact-picker-list :deep(.md-suggestion-list) { position: static; width: auto; max-width: none; max-height: min(320px, 45vh); box-shadow: none; animation: none; }
.contact-picker-keys { margin: 0; color: var(--muted); font-size: 11.5px; }
.contact-picker-keys kbd { padding: 0 4px; border: 1px solid var(--line); border-radius: 4px; font: inherit; font-size: 11px; }
.contact-picker-new { display: grid; gap: 8px; padding-top: 4px; }
.contact-picker-same { display: grid; gap: 4px; padding: 8px 10px; border-radius: 8px; background: color-mix(in srgb, var(--warning, #975a16) 10%, transparent); font-size: 13px; }
.contact-picker-same p { margin: 0; }
.contact-picker-same button { justify-self: start; }
.contact-picker-same small { color: var(--muted); }
.contact-picker-error { margin: 0; color: var(--error); }
</style>
