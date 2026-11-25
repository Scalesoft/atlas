<script setup lang="ts">
import t from "../../i18n/contacts-ui";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { errorMessage } from "../../api";
import { contactData, contactTypeId, duplicateContactId, type ContactData, type ContactPatch } from "../../contacts/contactData";
import { emptyContactForm, validateContactForm, type ContactFormErrors } from "../../contacts/contactForm";
import { createDebouncedLookup } from "../../lib/debouncedLookup";
import { liveObjects as defaultStore, type LiveObjects } from "../../objects/liveObjects";
import { placesText, usageApi, type UsageApi } from "../../objects/relationsApi";
import { objectApi, type ObjectApi, type ObjectList } from "../../objects/objectApi";
import AtlasIcon from "../AtlasIcon.vue";
import type { ActionMenuItem } from "../ActionMenu.vue";
import RowActionsMenu from "../RowActionsMenu.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import UsagePanel from "../objects/UsagePanel.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import SearchField from "../settings/SearchField.vue";
import { focusFirst } from "../../lib/pickEnter";
import { useSearchKeys } from "../../lib/searchKeys";
import { useToast } from "../../composables/useToast";
import { rowKeys } from "../../keyboard/navigation";
import { leaveInPlace } from "../library/leaveInPlace";
import { Button } from "../ui/button";
import ContactCard from "./ContactCard.vue";
import { uploadContactPhoto } from "../../contacts/contactPhoto";
import ContactForm from "./ContactForm.vue";
import ContactTrash from "./ContactTrash.vue";
import ContactDuplicates from "./ContactDuplicates.vue";
import SkillExperts from "./SkillExperts.vue";
import type { SkillApi } from "../../contacts/skillApi";
import type { ContactApi } from "../../contacts/contactApi";
import "../settings/settings.css";
import LoadMoreButton from "../LoadMoreButton.vue";
import { showObjectDetail } from "../../objects/objectDetail";
import { pageLink } from "../../pageLinks";

/**
 * Knihovna → Kontakty (objects spec §2.3): the wiki's contact directory. Every contact once, whether a page or a board shows it or
 * not; found by any of its fields, newest change first, 50 at a time. Who may add a contact (a WikiAdmin, anyone who writes in a
 * space) adds one with the contact form revealed under the toolbar (InlineReveal) and edits each field in place (ContactCard);
 * an address the directory has already brings up that contact instead. Moving a contact to the trash asks first when it is used (it
 * leaves every board that shows it too); one nobody uses goes at once, both with the toast „Kontakt … je v koši“ whose Zpět restores
 * it. Typing preselects the first contact (`data-first`) and Enter edits it where it is (its name opens); the first Esc empties the
 * search. Rows rise in staggered, a trashed one fades out; the rows are the tab's live objects (liveObjects),
 * so what a board changes shows here and the other way round. Koš (for who may add contacts) swaps the directory for its trash
 * (ContactTrash) and back, focus on the Koš button again; Možné duplicity (ContactDuplicates) the same way, for merging two contacts
 * of one person. A colleague (the contact of an account, `directoryId`) has Aktivita: the contact's dock (ContentDetail) with their changes,
 * also opened by the address (`#/library/contacts?contact=<id>`, the editor's „Otevřít v adresáři“). `source`, `store` and `contacts` are replaced by the gallery.
 */
const props = defineProps<{ source?: Pick<ObjectApi, "list" | "create" | "trash" | "trashList" | "restore" | "purge">; store?: LiveObjects; contacts?: Pick<ContactApi, "duplicates" | "merge" | "accountSuggestions" | "resolveAccount">; usage?: Pick<UsageApi, "count">;
  /** The search from the address (`#/library/contacts?q=C%23`: who knows a topic). */ initialQuery?: string;
  /** The contact whose dock the address opens (`?contact=`). */ initialContact?: string; skills?: Pick<SkillApi, "experts" | "merge" | "contact" | "rate" | "topics" | "link" | "pages"> }>();
const toaster = useToast();
const source = computed(() => props.source ?? objectApi);
const store = computed(() => props.store ?? defaultStore);

const query = ref("");
/** How many people know the topic the search names (SkillExperts): then no contact found by its fields is no empty directory. */
const experts = ref(0);
const ids = ref<string[]>([]);
const total = ref(0), page = ref(1), canCreate = ref(false);
const loading = ref(true), loaded = ref(false), error = ref("");
const creating = ref(false), saving = ref(false);
const form = ref(emptyContactForm());
const errors = ref<ContactFormErrors>({});
const note = ref("");
const highlighted = ref("");
const used = ref(0);
const trashing = ref<string | null>(null), trashBusy = ref(false), trashError = ref("");
const trashOpen = ref(false), duplicatesOpen = ref(false);
const restored = ref(false);

function show(list: ObjectList, more: boolean) {
  for (const item of list.items) store.value.seed({ id: item.id, typeId: item.typeId, version: item.version, title: item.title, data: item.data, canEdit: list.canCreate, available: true });
  ids.value = more ? [...new Set([...ids.value, ...list.items.map(item => item.id)])] : list.items.map(item => item.id);
  total.value = list.total; page.value = list.page; canCreate.value = list.canCreate;
}
const lookup = createDebouncedLookup<ObjectList | null>({
  find: async (text, signal) => {
    try {
      const list = await source.value.list({ type: contactTypeId, scope: "wiki", q: text.trim() }, signal);
      error.value = "";
      return list;
    } catch (failure) {
      if (signal.aborted) throw failure;
      error.value = errorMessage(failure);
      return null;
    }
  },
  onResult: list => { if (list) show(list, false); loading.value = false; loaded.value = true; },
  empty: () => null,
  delayMs: 250,
  minLength: 0,
});
let typed = false;
watch(query, text => { typed = true; loading.value = true; lookup.update(text); });
// The address's search (`#/library/contacts?q=`) is typed in for the reader, now and when the address changes.
watch(() => props.initialQuery, text => { if (text) query.value = text; }, { immediate: true });
const root = ref<HTMLElement>();
// The whole directory at once, without the typing delay; what is typed meanwhile wins.
onMounted(async () => {
  // Arrived by G, pak C (focus on the page's heading or nowhere): the search takes the typing (PR-10). The directory loads after the
  // app has landed the focus, so it does it itself; a tab clicked keeps its focus.
  const active = document.activeElement;
  if (!active || active === document.body || active.matches("h1")) root.value?.querySelector<HTMLInputElement>("input[data-contact-search]")?.focus({ preventScroll: true });
  try { const list = await source.value.list({ type: contactTypeId, scope: "wiki" }); if (!typed) show(list, false); }
  catch (failure) { if (!typed) error.value = errorMessage(failure); }
  finally { if (!typed) { loading.value = false; loaded.value = true; } }
});
onBeforeUnmount(lookup.cancel);

/** A person's card and Aktivita in the dock; a page opened from it opens in the frame. */
async function showActivity(id: string) {
  await store.value.load(id);
  const object = store.value.get(id);
  if (object?.available) showObjectDetail({ id, typeId: contactTypeId, title: object.title, spaceId: "" }, { onOpen: page => { location.hash = pageLink(page); } });
}
watch(() => props.initialContact, id => { if (id) void showActivity(id); }, { immediate: true });

async function more() {
  loading.value = true;
  try { show(await source.value.list({ type: contactTypeId, scope: "wiki", q: query.value.trim(), page: page.value + 1 }), true); error.value = ""; }
  catch (failure) { error.value = errorMessage(failure); }
  finally { loading.value = false; }
}

const rows = computed(() => ids.value.flatMap(id => { const object = store.value.get(id); return object?.available && object.data ? [{ id, object, contact: object.data as unknown as ContactData }] : []; }));
/** The contact Enter takes once something is typed: the first one shown, marked `data-first`. */
const first = computed(() => (query.value.trim() ? rows.value[0] : undefined));
/** Taking a contact is editing it where it is shown (P1): its name opens for editing; a contact the reader may not change gets the keyboard. */
function openFirst() {
  const row = first.value && root.value?.querySelector<HTMLElement>(`[data-contact-row="${CSS.escape(first.value.id)}"]`);
  if (!row || !first.value) return false;
  if (first.value.object.canEdit) row.querySelector<HTMLElement>('[data-contact-field="name"]')?.click();
  else focusFirst(row, '[tabindex="0"], a[href], button');
}
// Enter takes the first contact (one typed while the list still loads waits for it); the first Esc empties the search (P3, P8). Tab goes on to the cards.
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, waiting: () => loading.value, tab: false, take: openFirst });
/** ↓ in the search goes to the first contact shown (keyboard pass); the rest is every search's. */
function searchKeys(event: KeyboardEvent) {
  if (keys.onKeydown(event) || event.key !== "ArrowDown") return;
  const directory = (event.currentTarget as HTMLElement).closest(".contact-directory");
  if (!directory?.querySelector("[data-contact-row]")) return;
  event.preventDefault();
  // Not focusFirst: the row marked `data-first` is the li, which takes no focus; the keyboard goes to its first control.
  directory.querySelector<HTMLElement>('[data-contact-row] [tabindex="0"], [data-contact-row] a[href], [data-contact-row] button')?.focus();
}

async function create() {
  note.value = "";
  const { normalizeContact } = await import("@atlas/markdown/plugins/contacts");
  const result = validateContactForm(form.value, normalizeContact);
  errors.value = result.errors;
  if (!result.contact) return;
  saving.value = true;
  try {
    const created = await source.value.create(contactTypeId, "wiki", { ...contactData(result.contact) });
    store.value.seed({ id: created.id, typeId: created.typeId, version: created.version, title: created.title, data: created.data, canEdit: created.canEdit, available: true });
    reveal(created.id);
    form.value = emptyContactForm();
    creating.value = false;
  } catch (failure) {
    const existing = duplicateContactId(failure);
    if (!existing) { note.value = errorMessage(failure); return; }
    // The directory has the person already: that contact comes up instead of a second one.
    await store.value.load(existing);
    reveal(existing);
    note.value = t.directory.duplicateEmail;
  } finally {
    saving.value = false;
  }
}
function reveal(id: string) {
  ids.value = [id, ...ids.value.filter(other => other !== id)];
  highlighted.value = id;
}

async function change(id: string, patch: ContactPatch) {
  note.value = "";
  try { await store.value.edit(id, patch); }
  catch (failure) { note.value = errorMessage(failure); }
}

/** „Co s tím jde dělat“ of a contact's row (P2): what its name and its trash button do. */
const rowActions: ActionMenuItem[] = [{ id: "edit", label: t.directory.edit, icon: "pencil", shortcut: "F2" }, { id: "trash", label: t.directory.delete, icon: "trash", shortcut: "Delete", destructive: true, group: "trash" }];
function rowAction(id: string, action: string) {
  if (action === "trash") void askTrash(id);
  else root.value?.querySelector<HTMLElement>(`[data-contact-row="${CSS.escape(id)}"] [data-contact-field="name"]`)?.click();
}

const trashed = computed(() => (trashing.value ? store.value.get(trashing.value) : undefined));
/** Owner 2026-10-30: a used contact first warns and lists where; an unused one goes to the trash at once. Permanent delete is only in the trash. */
async function askTrash(id: string) {
  trashError.value = ""; note.value = "";
  let count = 0;
  try {
    const usage = await (props.usage ?? usageApi).count(id, undefined, undefined, true);
    // Used only where the reader may not look: asked as an unknown use, the number is not told.
    count = usage?.total || (usage?.hidden ? -1 : 0);
  }
  catch { count = -1; /* unknown use: ask rather than trash silently */ }
  used.value = count;
  trashing.value = id;
  if (!count) await trash();
}
async function trash() {
  const id = trashing.value;
  if (!id) return;
  const title = trashed.value?.title;
  const next = ids.value[ids.value.indexOf(id) + 1] ?? ids.value[ids.value.indexOf(id) - 1];
  trashBusy.value = true; trashError.value = "";
  try {
    await source.value.trash(id);
    store.value.revoke(id);
    ids.value = ids.value.filter(other => other !== id);
    total.value = Math.max(0, total.value - 1);
    trashing.value = null;
    toaster.success(title ? t.directory.trashedNamed(title) : t.directory.trashed, { undo: () => void untrash(id) });
    void focusRow(next);
  } catch (failure) {
    if (used.value) trashError.value = errorMessage(failure); else { note.value = errorMessage(failure); trashing.value = null; }
  } finally {
    trashBusy.value = false;
  }
}

/** Zpět of the toast: the contact comes back from the trash and stands first, as one just found does. */
async function untrash(id: string) {
  try {
    await source.value.restore(id);
    await store.value.refresh(id);
    reveal(id);
    total.value += 1;
    void focusRow(id);
  } catch (failure) { note.value = errorMessage(failure); }
}
/** Where the keyboard goes when its row left or came back (P7, never <body>): the first control of that contact's row, else the search. */
async function focusRow(id: string | undefined) {
  await nextTick();
  const row = id ? root.value?.querySelector(`[data-contact-row="${CSS.escape(id)}"]`) : null;
  (row?.querySelector<HTMLElement>('[tabindex="0"], a[href], button') ?? root.value?.querySelector<HTMLElement>("input[data-contact-search]"))?.focus({ preventScroll: true });
}

async function closeTrash() {
  const button = trashOpen.value ? "[data-contact-trash-open]" : "[data-contact-duplicates-open]";
  trashOpen.value = false; duplicatesOpen.value = false;
  // A contact back from the trash (or merged away) shows where the directory's order puts it.
  if (restored.value) { restored.value = false; loading.value = true; lookup.update(query.value); }
  await nextTick();
  root.value?.querySelector<HTMLElement>(button)?.focus({ preventScroll: true });
}
function merged(id: string) { store.value.revoke(id); restored.value = true; }
</script>

<template>
  <section ref="root" class="contact-directory settings-ui" aria-labelledby="contact-directory-title" :aria-busy="loading">
    <h2 id="contact-directory-title" class="sr-only">{{ t.directory.contacts }}</h2>
    <ContactTrash v-if="trashOpen" :source="source" @close="closeTrash" @restored="restored = true" />
    <ContactDuplicates v-else-if="duplicatesOpen" :api="contacts" @close="closeTrash" @merged="merged" />
    <template v-else>
      <div class="contact-directory-toolbar">
        <SearchField v-model="query" :label="t.directory.searchLabel" :placeholder="t.skills.searchPlaceholder" wide data-contact-search data-focus-landing @keydown="searchKeys" />
        <Button v-if="canCreate" type="button" aria-controls="contact-directory-new" :aria-expanded="creating" data-contact-create @click="creating = !creating">
          <AtlasIcon name="plus" :size="15" />{{ t.directory.newContact }}
        </Button>
        <Button v-if="canCreate" variant="outline" type="button" :title="t.directory.trashTitle" data-contact-trash-open @click="trashOpen = true">
          <AtlasIcon name="trash" :size="15" />{{ t.directory.trash }}
        </Button>
        <Button v-if="canCreate" variant="outline" type="button" :title="t.directory.duplicatesTitle" data-contact-duplicates-open @click="duplicatesOpen = true">
          <AtlasIcon name="users" :size="15" />{{ t.directory.duplicates }}
        </Button>
      </div>
      <InlineReveal id="contact-directory-new" v-model:open="creating">
        <div class="contact-directory-new"><ContactForm v-model="form" :errors="errors" :busy="saving" :submit-label="t.directory.saveToDirectory" @submit="create" /></div>
      </InlineReveal>
      <p v-if="note" class="contact-directory-note" role="status" data-contact-note>{{ note }}</p>
      <!-- Mapa znalostí: the people who know the topic the search names, above the contacts found by their fields. -->
      <SkillExperts v-if="query.trim()" :query="query" :api="skills" @found="experts = $event" />
      <ErrorState v-if="error && !rows.length" :title="t.directory.loadFailed" :text="error" :retry-label="t.directory.reload" :busy="loading" @retry="lookup.update(query)" />
      <LoadingSkeleton v-else-if="!loaded" :label="t.directory.loading" />
      <EmptyState v-else-if="!rows.length && !(query.trim() && experts)" icon="users" :title="query.trim() ? t.directory.noMatch : t.directory.emptyDirectory"
        :text="query.trim() ? t.directory.noMatchText : canCreate ? t.directory.emptyCanCreate : t.directory.emptyReader" />
      <TransitionGroup v-if="rows.length" tag="ul" name="contact-row" class="contact-directory-items" :aria-label="t.directory.contacts" @before-leave="leaveInPlace" @keydown="rowKeys($event, '[data-contact-row]')">
        <li v-for="(row, index) in rows" :key="row.id" :style="{ '--i': Math.min(index, 8) }" :class="{ 'is-highlighted': highlighted === row.id }" :data-contact-row="row.id"
          :data-first="row === first || undefined">
          <ContactCard :contact="row.contact" :editable="row.object.canEdit" :upload="file => uploadContactPhoto(row.id, file)" @change="change(row.id, $event)" />
          <Button v-if="row.contact.directoryId" variant="ghost" size="sm" type="button" class="contact-directory-activity" :aria-label="t.activity.openOf(row.object.title)"
            :title="t.activity.openOf(row.object.title)" data-contact-activity @click="showActivity(row.id)">
            <AtlasIcon name="history" :size="15" />
          </Button>
          <Button v-if="row.object.canEdit" variant="ghost" size="sm" type="button" class="contact-directory-trash" :aria-label="t.directory.moveToTrashOf(row.object.title)"
            :title="t.directory.moveToTrashOf(row.object.title)" data-contact-trash @click="askTrash(row.id)">
            <AtlasIcon name="trash" :size="15" />
          </Button>
          <RowActionsMenu :items="row.object.canEdit ? rowActions : []" :label="t.directory.actionsOf(row.object.title)" @select="rowAction(row.id, $event)" />
        </li>
      </TransitionGroup>
      <footer v-if="rows.length" class="contact-directory-footer">
        <p class="help" role="status">{{ t.directory.count(rows.length, total) }}</p>
        <LoadMoreButton v-if="total > ids.length" :busy="loading" data-contact-more @click="more" />
      </footer>
      <ConfirmDialog :open="!!trashing && used !== 0" :title="t.directory.trashDialog" :confirm-label="t.directory.moveToTrash" confirm-icon="trash" :busy="trashBusy" :error="trashError"
        @confirm="trash" @cancel="trashing = null">
        <p>{{ t.directory.contactWord }} <strong>{{ trashed?.title }}</strong> {{ used > 0 ? t.directory.usedIn(placesText(used)) : t.directory.usedUnknown }}. {{ t.directory.trashTail }}</p>
        <UsagePanel v-if="trashing" :object-id="trashing" still />
      </ConfirmDialog>
    </template>
  </section>
</template>

<style scoped>
.contact-directory { display: grid; gap: 14px; min-height: 306px; align-content: start; color: var(--text); }
.contact-directory-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.contact-directory-toolbar :deep(.search-field) { flex: 1 1 280px; }
.contact-directory-new { padding: 14px 16px 2px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.contact-directory-note { margin: 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.contact-directory-items { position: relative; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.contact-directory-items > li { display: flex; align-items: flex-start; gap: 8px; padding: 12px 12px 12px 16px; transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out); }
.contact-directory-items > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.contact-directory-items > li > :first-child { flex: 1; min-width: 0; }
.contact-directory-items > li.is-highlighted { background: var(--accent-soft); }
/* The contact Enter takes, while the search has the focus (P8). */
.contact-directory:has(input[data-contact-search]:focus) .contact-directory-items > li[data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.contact-directory-trash, .contact-directory-activity { flex: none; color: var(--muted); }
/* Rows rise in staggered (first eight), a trashed one fades out where it was while the rest close up. */
.contact-row-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
.contact-row-leave-active { position: absolute; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.contact-row-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
.contact-directory-footer { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 10px 18px; }
.contact-directory-footer .help { margin: 0; color: var(--muted); font-size: 13px; }
</style>
