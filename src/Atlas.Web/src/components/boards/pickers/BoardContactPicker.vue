<script lang="ts">
import t from "../../../i18n/boards";
import people from "../../../i18n/contacts";
import { shallowRef as moduleRef } from "vue";
import type { ChoiceOption as Row } from "../../settings/types";
/** The directory contacts last added from this picker, newest first: the rows it opens with (P17).
 * shortcut: kept for the browser tab only; persist them if people miss them after a reload. */
const recent = moduleRef<{ option: Row; objectId: string }[]>([]);
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from "vue";
import type { MarkdownContact } from "@atlas/markdown/plugins/contacts";
import { errorMessage } from "../../../api";
import type { PickedObject } from "../../../boards/boardBlockNodes";
import { contactSubtitle } from "../../../boards/boardTargets";
import { contactTypeId } from "../../../contacts/contactData";
import { linkContact, type ContactAnswer, type ContactAsk, type ContactDecision } from "../../../contacts/contactLinking";
import { emptyContactForm, validateContactForm, type ContactFormErrors } from "../../../contacts/contactForm";
import { createDebouncedLookup } from "../../../lib/debouncedLookup";
import { objectApi, type ObjectApi } from "../../../objects/objectApi";
import AtlasIcon from "../../AtlasIcon.vue";
import ContactForm from "../../contacts/ContactForm.vue";
import ContactMatchPanel from "../../contacts/ContactMatchPanel.vue";
import InlineReveal from "../../settings/InlineReveal.vue";
import LoadingNote from "../../settings/LoadingNote.vue";
import type { ChoiceOption } from "../../settings/types";
import BoardPickList from "./BoardPickList.vue";
import BoardPickerPanel from "./BoardPickerPanel.vue";
import type { ContactSearch } from "../../../contacts/contactHits";
import { findPeople, learnAlias, searchWiki } from "../../../contacts/contactSources";
import { maxPicked, useMultiPick } from "./useMultiPick";

/**
 * Which contacts the board shows as live cards (objects spec §2.3, §5.1): the contacts of the wiki directory that match, then the
 * contacts written only in pages (the wiki search's contact widgets) that the directory does not have yet, in a multi-pick list, at
 * most 100 at once, and „Nový kontakt“, a form revealed under the list (InlineReveal). On confirmation every pick that is not in the
 * directory yet is linked to it first (`link`, contactLinking.ts: matched, what the match asks shown in the panel by ContactMatchPanel,
 * then resolved), so each card shows the one live contact. It emits the objects; a contact the board shows already is placed and
 * said by the board. Before anything is typed it lists the contacts last added (at most 8). A directory contact picked after typing
 * learns what was typed as its alias on confirmation, as ContactPicker teaches it (`learn`, contactSources.ts `learnAlias`).
 * `search` (the wiki search), `directory` (the object path), `link` and `learn` are replaced by the gallery. Private to the board.
 */
const props = defineProps<{ open: boolean; search?: ContactSearch; directory?: Pick<ObjectApi, "list">; link?: (contact: MarkdownContact, ask: ContactAsk) => Promise<MarkdownContact | null>;
  learn?: typeof learnAlias; anchor?: HTMLElement | null }>();
const emit = defineEmits<{ pick: [objects: PickedObject[]]; cancel: [] }>();

const minLength = 2;
const directory = computed(() => props.directory ?? objectApi);
type ContactPick = { objectId: string; contact?: MarkdownContact } | { contact: MarkdownContact };
interface Found { options: ChoiceOption[]; picks: Map<string, ContactPick> }
const query = ref("");
const options = ref<ChoiceOption[]>([]);
const searched = ref(false), loading = ref(false), failed = ref(false), busy = ref(false);
const basket = useMultiPick(maxPicked);
/** Every contact offered or made while the picker is open, by the value of its option: a pick outlives the next search. */
const picks = new Map<string, ContactPick>();
/** What was typed when an option was ticked, by its value: the alias its contact learns on confirmation. */
const typed = new Map<string, string>();
function toggle(option: ChoiceOption) {
  if (query.value.trim()) typed.set(option.value, query.value); else typed.delete(option.value);
  basket.toggle(option);
}
const creating = ref(false);
const form = ref(emptyContactForm());
const errors = ref<ContactFormErrors>({});
const note = ref("");
let created = 0;
const deciding = shallowRef<{ decision: ContactDecision; answer: (answer: ContactAnswer | null) => void } | null>(null);
const ask: ContactAsk = decision => new Promise(settle => { deciding.value = { decision, answer: answer => { deciding.value = null; settle(answer); } }; });

// The shared people search (contacts/contactSources.ts): the directory, then contacts written only in pages, each person once.
async function find(text: string, signal: AbortSignal): Promise<Found> {
  const hits = await findPeople(text, signal, { directory: directory.value, wiki: props.search ?? searchWiki });
  const found: Found = { options: [], picks: new Map() };
  for (const hit of hits) {
    found.picks.set(hit.key, hit.objectId ? { objectId: hit.objectId, contact: hit.contact } : { contact: hit.contact });
    found.options.push({ value: hit.key, label: hit.contact.name, icon: "user",
      description: hit.objectId ? contactSubtitle(hit.contact) || t.contactPicker.inDirectory : t.contactPicker.onlyInPage(hit.origin.replace(people.sources.pagePrefix, "")) });
  }
  return found;
}

const lookup = createDebouncedLookup<Found>({
  find: async (text, signal) => {
    try {
      const found = await find(text, signal);
      failed.value = false;
      return found;
    } catch (failure) {
      if (signal.aborted) throw failure;
      failed.value = true;
      return { options: [], picks: new Map() };
    }
  },
  onResult: (found) => {
    for (const [value, pick] of found.picks) picks.set(value, pick);
    options.value = found.options;
    searched.value = !!query.value.trim();
    loading.value = false;
  },
  empty: () => ({ options: [], picks: new Map() }),
  delayMs: 250,
  minLength,
});
watch(query, text => { loading.value = text.trim().length >= minLength; if (!loading.value) failed.value = false; lookup.update(text); });
watch(() => props.open, open => {
  if (open) return;
  lookup.cancel();
  query.value = ""; options.value = []; searched.value = false; loading.value = false; failed.value = false;
  basket.clear(); picks.clear(); typed.clear(); deciding.value?.answer(null);
  creating.value = false; form.value = emptyContactForm(); errors.value = {}; note.value = "";
});
onBeforeUnmount(lookup.cancel);

const shown = computed(() => (query.value.trim() ? options.value : recent.value.map(entry => entry.option)));
const emptyText = computed(() => (failed.value ? t.contactPicker.searchFailed : searched.value ? t.contactPicker.noMatch : t.contactPicker.start));
const confirmLabel = computed(() => (basket.picked.value.length ? t.contactPicker.addCount(basket.picked.value.length) : t.contactPicker.add));

async function addNew() {
  note.value = "";
  // The editor's rules, imported when the form is first used: a board that never makes a contact never loads the plugin.
  const { normalizeContact } = await import("@atlas/markdown/plugins/contacts");
  const result = validateContactForm(form.value, normalizeContact);
  errors.value = result.errors;
  if (!result.contact) return;
  if (basket.full.value) { note.value = t.contactPicker.max(maxPicked); return; }
  const value = `new:${++created}`;
  picks.set(value, { contact: result.contact });
  basket.toggle({ value, label: result.contact.name, description: contactSubtitle(result.contact) || t.contactPicker.willAdd, icon: "user" });
  form.value = emptyContactForm();
  creating.value = false;
}

/** The directory contact of a pick: itself, or the one it is linked to now; null when the person went back. */
async function objectOf(pick: ContactPick): Promise<string | null> {
  if ("objectId" in pick) return pick.objectId;
  const linked = await (props.link ?? linkContact)(pick.contact, ask);
  if (linked && !linked.objectId) throw new Error(t.contactPicker.notAllowed);
  return linked?.objectId ?? null;
}

async function confirm() {
  note.value = "";
  busy.value = true;
  try {
    const objects: PickedObject[] = [];
    const added: typeof recent.value = [];
    const refused: string[] = [];
    for (const option of basket.picked.value) {
      const pick = picks.get(option.value) ?? recent.value.find(entry => entry.option.value === option.value);
      if (!pick) continue;
      try {
        const objectId = await objectOf(pick);
        if (!objectId) return;
        objects.push({ objectId, typeId: contactTypeId });
        added.push({ objectId, option: { ...option, value: `obj:${objectId}`, description: "objectId" in pick ? option.description : t.contactPicker.inDirectory } });
      } catch (failure) { refused.push(`${option.label}: ${errorMessage(failure)}`); }
    }
    if (refused.length) { note.value = t.contactPicker.refused(refused.join(" ")); return; }
    for (const option of basket.picked.value) {
      const pick = picks.get(option.value), query = typed.get(option.value);
      if (pick && "objectId" in pick && pick.contact && query) (props.learn ?? learnAlias)({ contact: pick.contact, objectId: pick.objectId }, query);
    }
    recent.value = [...added, ...recent.value].filter((entry, at, all) => all.findIndex(other => other.objectId === entry.objectId) === at).slice(0, 8);
    emit("pick", [...new Map(objects.map(object => [object.objectId, object])).values()]);
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <BoardPickerPanel :open="open" :anchor="anchor" :title="t.contactPicker.title" @close="emit('cancel')">
    <div class="board-contact-picker" data-board-contact-picker>
      <BoardPickList v-model:query="query" :options="shown":picked="basket.picked.value" :loading="loading" :label="t.contactPicker.label" :placeholder="t.contactPicker.placeholder" :empty-text="emptyText" @toggle="toggle" @confirm="busy || confirm()" />
      <LoadingNote :active="loading" :label="t.contactPicker.searching" />
      <div class="board-contact-new">
        <button type="button" class="md-btn" aria-controls="board-contact-new" :aria-expanded="creating" data-board-contact-new @click="creating = !creating"><AtlasIcon name="plus" :size="15" />{{ t.contactPicker.newContact }}</button>
        <InlineReveal id="board-contact-new" v-model:open="creating">
          <div class="board-contact-new-form"><ContactForm v-model="form" :errors="errors" @submit="addNew" /></div>
        </InlineReveal>
      </div>
      <ContactMatchPanel v-if="deciding" :decision="deciding.decision" @answer="deciding.answer" />
      <p v-if="note" class="board-contact-note" role="alert" data-board-contact-note>{{ note }}</p>
    </div>
    <template #footer>
      <button type="button" class="md-btn" @click="emit('cancel')">{{ t.contactPicker.cancel }}</button>
      <button type="button" class="md-btn-primary" :disabled="!basket.picked.value.length || busy" :aria-busy="busy" data-board-contact-confirm @click="confirm">{{ confirmLabel }}</button>
    </template>
  </BoardPickerPanel>
</template>

<style scoped>
.board-contact-picker { display: grid; gap: 10px; }
.board-contact-new { display: grid; justify-items: start; gap: 8px; }
.board-contact-new :deep(.inline-reveal-root) { width: 100%; }
.board-contact-new-form { padding-top: 6px; }
.board-contact-note { margin: 0; color: var(--error); font-size: 13px; }
</style>
