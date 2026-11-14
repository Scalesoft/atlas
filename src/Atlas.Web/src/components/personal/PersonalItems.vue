<script setup lang="ts">
import t from "../../i18n/components-personal";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import type { accessApi } from "../../accessApi";
import { ApiError, errorMessage } from "../../api";
import { formatShortDate } from "../../formatDate";
import { useToast } from "../../composables/useToast";
import { useSearchKeys } from "../../lib/searchKeys";
import { draftKeeper, drafts } from "../../lib/unsavedWork";
import { rowKeys } from "../../keyboard/navigation";
import { personalApi as defaultApi, type NoteData, type PersonalApi, type PersonalItem } from "../../personal/personalApi";
import { leaveInPlace } from "../library/leaveInPlace";
import AtlasIcon from "../AtlasIcon.vue";
import type { ActionMenuItem } from "../ActionMenu.vue";
import RowActionsMenu from "../RowActionsMenu.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import SearchField from "../settings/SearchField.vue";
import { Button } from "../ui/button";
import { Input } from "../ui/input";
import NoteCard from "./NoteCard.vue";
import PersonalTrash from "./PersonalTrash.vue";
import ShareItemDialog from "./ShareItemDialog.vue";
import "../settings/settings.css";

/**
 * Knihovna → Můj prostor (`mode` mine) and Sdíleno se mnou (`shared`), spec 2026-10-29: private notes, newest change first. Every row
 * is the same: an icon, the title, a plain-text excerpt and who shares it or with how many. A row opens its note in place (NoteCard,
 * edited with the page editor where the reader may), reading the whole note only then: the list carries excerpts. In Můj prostor the
 * owner creates a note from a title revealed under the toolbar (it opens at once, ready for its text), shares one (ShareItemDialog)
 * and moves it to the trash at once (a toast with „Zpět“). The search filters titles, excerpts and owners as typed, and stays away while there
 * is nothing to search; ↓ goes to the first row, and once something is typed the first note is preselected (`data-first`) and Enter opens
 * it (the empty search opens nothing); the first Esc empties the search. `source` is replaced by the gallery.
 */
const props = withDefaults(defineProps<{ mode: "mine" | "shared"; refreshKey?: number; source?: PersonalApi; shareApi?: Pick<typeof accessApi, "resource" | "candidates" | "grant" | "revoke"> }>(),
  { refreshKey: 0, source: undefined, shareApi: undefined });
const source = computed(() => props.source ?? defaultApi);
const mine = computed(() => props.mode === "mine");

const items = ref<PersonalItem[]>([]);
const loading = ref(true), loaded = ref(false), error = ref(""), note = ref("");
const query = ref("");
const creating = ref(false);
const newTitle = ref(""), saving = ref(false);
const fresh = ref("");
const sharing = ref<PersonalItem | null>(null);
const toaster = useToast();
const root = ref<HTMLElement>();
const trashOpen = ref(false), restoredAny = ref(false);
/**
 * A change the server refused, by note: the text stays in the note as typed (PER2-2), with the reason and a retry that sends it all
 * again over `version`, the version it was written on. `theirs`: someone else saved the note meanwhile (PER-A-1, a 409); nothing goes
 * over their change until the reader chooses to save again over `theirs` or to take the current note instead. Kept with the unsaved
 * work of the tab as it changes, so it outlives this list (another tab of the library) and the tab asks before it closes (UX-P-6).
 */
type Unsaved = { fields: Partial<NoteData>; error: string; version: number; theirs?: number };
const draftPrefix = "personal-note:";
const unsaved = ref<Record<string, Unsaved>>(Object.fromEntries([...drafts].filter(([key]) => key.startsWith(draftPrefix))
  .map(([key, value]) => [key.slice(draftPrefix.length), value as Unsaved])));
const keepDraft = draftKeeper();
function setUnsaved(id: string, value: Unsaved | undefined) {
  if (value) unsaved.value[id] = value;
  else delete unsaved.value[id];
  keepDraft(draftPrefix + id, value);
}
/** The note open in place: its whole text, read when it opened. */
const open = ref<{ id: string; data?: NoteData; canEdit?: boolean; error?: string; editText?: boolean }>();
let noteAbort: AbortController | undefined;
/** Back from the trash: the list again (reloaded when something came back), the keyboard on Koš. */
function closeTrash() {
  trashOpen.value = false;
  if (restoredAny.value) { restoredAny.value = false; void load(); }
  void nextTick(() => root.value?.querySelector<HTMLElement>("[data-personal-trash-open]")?.focus());
}
let abort: AbortController | undefined;

async function load() {
  abort?.abort();
  const controller = abort = new AbortController();
  loading.value = true;
  try {
    const list = await (mine.value ? source.value.mine(controller.signal) : source.value.shared(controller.signal));
    if (controller.signal.aborted) return;
    // A row already shown stays the same object, so a save in the air lands on it, and never goes back to an older version.
    const shownBefore = new Map(items.value.map(old => [old.id, old]));
    items.value = list.map(fresh => {
      const old = shownBefore.get(fresh.id);
      return old ? Object.assign(old, fresh, { version: Math.max(old.version, fresh.version) }) : fresh;
    });
    error.value = "";
  } catch (failure) {
    if (!controller.signal.aborted) error.value = errorMessage(failure);
  } finally {
    if (!controller.signal.aborted) { loading.value = false; loaded.value = true; }
  }
}
onMounted(() => {
  // Arrived by the sidebar or an address with nothing focused: the search takes the typing, as in Kontakty.
  const active = document.activeElement;
  if (!active || active === document.body || active.matches("h1")) root.value?.querySelector<HTMLInputElement>("input[data-personal-search]")?.focus({ preventScroll: true });
  void load();
});
watch(() => props.refreshKey, () => void load());
onBeforeUnmount(() => { abort?.abort(); noteAbort?.abort(); });

const words = computed(() => query.value.toLocaleLowerCase("cs").split(/\s+/).filter(Boolean));
const shown = computed(() => items.value.filter(item => {
  const text = `${item.title} ${item.excerpt ?? ""} ${item.ownerName ?? ""}`.toLocaleLowerCase("cs");
  return words.value.every(word => text.includes(word));
}));
/** Nothing to search yet: the empty state speaks alone. */
const searchable = computed(() => !loaded.value || items.value.length > 0 || !!query.value);

const rowButton = (id: string) => root.value?.querySelector<HTMLElement>(`[data-personal-row="${id}"] [data-personal-open]`);
/** Opens a note in place (or closes the open one), reading its whole text. An editable note opens with the caret in its text, a read-only
 * one keeps the keyboard on its row, so typing never falls through to the search (UX2-2); closing returns the keyboard to the row. */
async function toggle(item: PersonalItem, editText = false) {
  noteAbort?.abort();
  if (open.value?.id === item.id && !editText) { open.value = undefined; rowButton(item.id)?.focus({ preventScroll: true }); return; }
  if (open.value?.id === item.id && open.value.data) { open.value = { ...open.value, editText }; return; }
  const controller = noteAbort = new AbortController();
  open.value = { id: item.id, editText };
  try {
    // Read after the reader's own saves, so the text and the version are what they saved.
    await changing;
    if (controller.signal.aborted) return;
    const read = await source.value.note(item.id, controller.signal);
    if (controller.signal.aborted || open.value?.id !== item.id) return;
    // Reopened with a refused change: the draft is still what the note shows.
    const data = { ...read.data, ...unsaved.value[item.id]?.fields };
    item.version = Math.max(item.version, read.version);
    open.value = { ...open.value, data, canEdit: read.canEdit, editText: read.canEdit };
    if (!read.canEdit) rowButton(item.id)?.focus({ preventScroll: true });
  } catch (failure) {
    if (!controller.signal.aborted && open.value?.id === item.id) open.value = { ...open.value, error: errorMessage(failure) };
  }
}

function startCreating() {
  creating.value = !creating.value;
  newTitle.value = "";
  note.value = "";
  if (creating.value) void nextTick(() => root.value?.querySelector<HTMLInputElement>("input[data-personal-title]")?.focus());
}
async function create() {
  const title = newTitle.value.trim();
  if (!title || saving.value || !creating.value) return;
  saving.value = true; note.value = "";
  try {
    const created = await source.value.createNote(title);
    items.value = [{ id: created.id, typeId: created.typeId, title: created.title, updatedAt: created.updatedAt, version: created.version, canEdit: true, shares: 0,
      ownerName: null, level: null, expiresAt: null, excerpt: "" }, ...items.value];
    fresh.value = created.id;
    creating.value = false;
    // A new note opens ready for its text; it is all known already.
    noteAbort?.abort();
    open.value = { id: created.id, data: { title, text: "" }, canEdit: true, editText: true };
  } catch (failure) {
    note.value = errorMessage(failure);
  } finally {
    saving.value = false;
  }
}

/** The excerpt of a changed text as the server would cut it, near enough until the next load. */
const excerptOf = (text: string) => text.replace(/[#>*_`~[\]]|^\s*[-+]\s|^\s*\d+\.\s/gm, "").replace(/\s+/g, " ").trim().slice(0, 200);
/** One change at a time, each with the version the one before it answered: a note shared for writing has two editors (PER-A-1). */
let changing = Promise.resolve();
const queue = (step: () => Promise<void>) => { changing = changing.then(step); };
function change(item: PersonalItem, fields: Partial<NoteData>) {
  const current = open.value;
  if (current?.id !== item.id || !current.data) return;
  note.value = "";
  current.data = { ...current.data, ...fields };
  if (fields.text !== undefined) item.excerpt = excerptOf(fields.text);
  if (fields.title !== undefined) item.title = excerptOf(fields.title);
  queue(() => save(item, fields));
}
/** `overwrite`: saved again on purpose over the change someone else made (after a conflict). */
async function save(item: PersonalItem, fields: Partial<NoteData>, overwrite = false) {
  const kept = unsaved.value[item.id];
  // A field refused before goes again with the new one: what the note shows is what gets saved.
  if (kept) fields = { ...kept.fields, ...fields };
  // A conflict waits for the reader's choice: more typing joins the draft, nothing goes over the other change unseen.
  if (kept?.theirs !== undefined && !overwrite) { setUnsaved(item.id, { ...kept, fields }); return; }
  const version = !kept ? item.version : overwrite && kept.theirs !== undefined ? kept.theirs : kept.version;
  try {
    item.version = (await source.value.changeNote(item.id, fields, version)).version;
    item.updatedAt = new Date().toISOString();
    setUnsaved(item.id, undefined);
  } catch (failure) {
    if (!(failure instanceof ApiError && failure.code === "version_conflict")) {
      setUnsaved(item.id, { fields, error: errorMessage(failure), version: kept?.version ?? version, theirs: kept?.theirs });
      return;
    }
    // Changed by someone else meanwhile: the text stays as typed, and saving it again is a choice made knowing that.
    const theirs = (await source.value.note(item.id).catch(() => undefined))?.version ?? version;
    setUnsaved(item.id, { fields, error: t.items.conflict, version: kept?.version ?? version, theirs });
  }
}
/** After a conflict: the reader's text goes, the note as saved now comes in its place. */
function takeCurrent(item: PersonalItem) {
  queue(async () => {
    try {
      const read = await source.value.note(item.id);
      setUnsaved(item.id, undefined);
      item.version = read.version;
      item.title = excerptOf(read.data.title);
      item.excerpt = excerptOf(read.data.text);
      if (open.value?.id === item.id) open.value = { ...open.value, data: read.data, canEdit: read.canEdit };
    } catch (failure) {
      const kept = unsaved.value[item.id];
      if (kept) setUnsaved(item.id, { ...kept, error: errorMessage(failure) });
    }
  });
}

/** The trash is reversible (P6): the note goes at once, the toast offers „Zpět“, and the keyboard goes to the next row (P7, never <body>). */
async function trash(item: PersonalItem) {
  const next = shown.value[shown.value.indexOf(item) + 1] ?? shown.value[shown.value.indexOf(item) - 1];
  note.value = "";
  try {
    await source.value.trash(item);
  } catch (failure) {
    note.value = errorMessage(failure);
    return;
  }
  items.value = items.value.filter(other => other.id !== item.id);
  if (open.value?.id === item.id) open.value = undefined;
  toaster.success(t.items.trashed(item.title), { undo: () => void untrash(item) });
  void focusRow(next?.id);
}
/** Zpět of the toast: the note comes back from the trash, with its shares. */
async function untrash(item: PersonalItem) {
  try {
    await source.value.restore(item.id);
    await load();
    void focusRow(item.id);
  } catch (failure) { note.value = errorMessage(failure); }
}
async function focusRow(id: string | undefined) {
  await nextTick();
  const target = (id ? rowButton(id) : null) ?? root.value?.querySelector<HTMLElement>("input[data-personal-search], [data-personal-new-note]");
  target?.focus({ preventScroll: true });
}

/** „Co s tím jde dělat“ of a note of Můj prostor (P2): what its two buttons do. */
const rowActions: ActionMenuItem[] = [{ id: "share", label: t.items.share, icon: "users" }, { id: "trash", label: t.items.delete, icon: "trash", destructive: true, group: "trash" }];
const rowAction = (item: PersonalItem, id: string) => { if (id === "trash") void trash(item); else sharing.value = item; };

/** The note Enter opens once something is typed: the first one shown, marked `data-first`. */
const first = computed(() => (query.value.trim() ? shown.value[0] : undefined));
// Enter opens the preselected first note, with the caret in its text where it may be edited; the first Esc empties the search (P3, P8). Tab goes on to the rows.
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, tab: false,
  // A note that is open and that the reader may only read stays open: Enter opens, it never toggles.
  take: () => { if (!first.value) return false; if (open.value?.id === first.value.id && !first.value.canEdit) return; void toggle(first.value, first.value.canEdit); } });
/** ↓ in the search goes to the first row; the rest is every search's. */
function searchKeys(event: KeyboardEvent) {
  if (keys.onKeydown(event) || event.key !== "ArrowDown" || !shown.value[0]) return;
  event.preventDefault();
  rowButton(shown.value[0].id)?.focus();
}

const levelLabel = (item: PersonalItem) => (item.level === "write" ? t.items.youEdit : t.items.youRead);
function meta(item: PersonalItem) {
  const parts: string[] = [];
  if (mine.value) {
    if (item.shares) parts.push(t.items.sharedCount(item.shares));
  } else {
    parts.push(t.items.sharedBy(item.ownerName), levelLabel(item));
    if (item.expiresAt) parts.push(t.items.until(formatShortDate(item.expiresAt)));
  }
  parts.push(t.items.changed(formatShortDate(item.updatedAt)));
  return parts.join(" · ");
}
const emptyTitle = computed(() => (query.value.trim() ? t.items.emptyNoMatch : mine.value ? t.items.emptyMine : t.items.emptyShared));
const emptyText = computed(() => query.value.trim() ? t.items.emptyTextSearch
  : mine.value ? t.items.emptyTextMine
    : t.items.emptyTextShared);
</script>

<template>
  <section ref="root" class="personal-items settings-ui" :aria-labelledby="`personal-items-${mode}`" :aria-busy="loading" :data-personal-items="mode">
    <h2 :id="`personal-items-${mode}`" class="sr-only">{{ mine ? t.items.heading.mine : t.items.heading.shared }}</h2>
    <PersonalTrash v-if="trashOpen" :source="source" @close="closeTrash" @restored="restoredAny = true" />
    <template v-else>
    <div v-if="searchable || mine" class="personal-items-toolbar">
      <SearchField v-if="searchable" v-model="query" :label="mine ? t.items.searchLabel.mine : t.items.searchLabel.shared" :placeholder="t.items.searchPlaceholder" wide
        data-personal-search data-focus-landing @keydown="searchKeys" />
      <template v-if="mine">
        <Button type="button" :variant="creating ? 'default' : 'outline'" aria-controls="personal-items-new" :aria-expanded="creating" data-personal-new-note @click="startCreating">
          <AtlasIcon name="plus" :size="15" />{{ t.items.newNote }}
        </Button>
        <Button variant="outline" type="button" :title="t.items.trashTitle" data-personal-trash-open @click="trashOpen = true">
          <AtlasIcon name="trash" :size="15" />{{ t.items.trash }}
        </Button>
      </template>
    </div>
    <InlineReveal v-if="mine" id="personal-items-new" :open="creating" @update:open="value => { if (!value) creating = false; }">
      <form class="personal-items-new" @submit.prevent="create">
        <label class="personal-items-new-label" for="personal-items-title">{{ t.items.newTitleLabel }}</label>
        <div class="personal-items-new-row">
          <Input id="personal-items-title" v-model="newTitle" maxlength="200" autocomplete="off" data-personal-title :placeholder="t.items.newTitlePlaceholder"
            @keydown.esc.prevent="creating = false" />
          <Button type="submit" :disabled="!newTitle.trim() || saving" :aria-busy="saving" data-personal-create>{{ t.items.create }}</Button>
        </div>
        <p class="help">{{ t.items.privateHelp }}</p>
      </form>
    </InlineReveal>
    <p v-if="note" class="personal-items-note" role="status" data-personal-note>{{ note }}</p>
    <ErrorState v-if="error && !items.length" :title="mine ? t.items.loadFailed.mine : t.items.loadFailed.shared" :text="error" :retry-label="t.items.reload" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-else-if="!loaded" :label="mine ? t.items.loading.mine : t.items.loading.shared" />
    <EmptyState v-else-if="!shown.length" :icon="mine ? 'user' : 'users'" :title="emptyTitle" :text="emptyText" />
    <TransitionGroup v-if="shown.length" tag="ul" name="personal-row" class="personal-items-list" :aria-label="mine ? t.items.listLabel.mine : t.items.listLabel.shared"
      @before-leave="leaveInPlace" @keydown="rowKeys($event, '[data-personal-row]')">
      <li v-for="(item, index) in shown" :key="item.id" :style="{ '--i': Math.min(index, 8) }" :class="{ 'is-highlighted': fresh === item.id }" :data-personal-row="item.id" :data-first="item === first || undefined">
        <div class="personal-item-head">
          <button type="button" class="personal-item-open" :aria-expanded="open?.id === item.id" :aria-controls="`personal-note-${item.id}`" data-personal-open @click="toggle(item)">
            <span class="personal-item-icon" aria-hidden="true"><AtlasIcon name="file-text" :size="16" /></span>
            <span class="personal-item-copy">
              <span class="personal-item-title" :title="item.title">{{ item.title }}</span>
              <span v-if="item.excerpt && open?.id !== item.id" class="personal-item-excerpt" data-personal-excerpt>{{ item.excerpt }}</span>
              <span class="personal-item-meta"><AtlasIcon v-if="!mine || item.shares" :name="mine ? 'users' : 'user'" :size="13" />{{ meta(item) }}</span>
            </span>
            <AtlasIcon class="personal-item-chevron" name="chevron-down" :size="16" aria-hidden="true" />
          </button>
          <div v-if="mine" class="personal-item-actions">
            <Button variant="ghost" size="sm" type="button" :aria-label="t.items.shareItem(item.title)" :title="t.items.shareItem(item.title)" data-personal-share @click="sharing = item">
              <AtlasIcon name="users" :size="15" /><span class="personal-item-action-label">{{ t.items.share }}</span>
            </Button>
            <Button variant="ghost" size="sm" type="button" :aria-label="t.items.trashItem(item.title)" :title="t.items.trashItem(item.title)" data-personal-trash
              @click="trash(item)">
              <AtlasIcon name="trash" :size="15" />
            </Button>
          </div>
        </div>
        <RowActionsMenu :items="mine ? rowActions : []" :label="t.items.actions(item.title)" @select="rowAction(item, $event)" />
        <InlineReveal :id="`personal-note-${item.id}`" :open="open?.id === item.id">
          <div class="personal-item-note" data-personal-note-body>
            <NoteCard v-if="open?.id === item.id && open.data" :key="item.id" :note="open.data" :editable="open.canEdit" :start-editing="open.editText && open.canEdit"
              @change="change(item, $event)" />
            <ErrorState v-else-if="open?.id === item.id && open.error" :title="t.items.noteLoadFailed" :text="open.error" :retry-label="t.items.reload" @retry="toggle(item, open.editText)" />
            <LoadingSkeleton v-else :rows="1" row-height="64px" :label="t.items.noteLoading" />
            <ErrorState v-if="unsaved[item.id] && open?.id === item.id && open.data" :title="t.items.saveFailed"
              :text="t.items.unsavedText(unsaved[item.id].error)" :retry-label="t.items.saveAgain" data-personal-unsaved
              @retry="queue(() => save(item, {}, true))">
              <Button v-if="unsaved[item.id].theirs !== undefined" variant="ghost" size="sm" type="button" class="personal-item-take" data-personal-take-current
                @click="takeCurrent(item)">{{ t.items.takeCurrent }}</Button>
            </ErrorState>
          </div>
        </InlineReveal>
      </li>
    </TransitionGroup>
    </template>
    <ShareItemDialog v-if="sharing" :open="!!sharing" :item-id="sharing.id" :title="sharing.title" :api="shareApi" @close="sharing = null" @changed="load()" />
  </section>
</template>

<style scoped>
.personal-items { display: grid; gap: 14px; min-height: 306px; align-content: start; color: var(--text); }
.personal-items-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
.personal-items-toolbar :deep(.search-field) { flex: 1 1 280px; }
.personal-items-new { display: grid; gap: 6px; padding: 14px 16px; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.personal-items-new-label { font-size: 13px; font-weight: 600; }
.personal-items-new-row { display: flex; flex-wrap: wrap; gap: 8px; }
.personal-items-new-row > :first-child { flex: 1 1 240px; }
.personal-items-new .help { margin: 0; color: var(--muted); font-size: 12.5px; }
.personal-items-note { margin: 0; color: var(--muted); font-size: 13px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.personal-items-list { position: relative; margin: 0; padding: 0; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); overflow: hidden; }
.personal-items-list > li { container-type: inline-size; display: grid; padding: 6px 8px; transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out); }
.personal-items-list > li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.personal-items-list > li.is-highlighted { background: var(--accent-soft); }
/* The note Enter opens, while the search has the focus (P8). */
.personal-items:has(input[data-personal-search]:focus) .personal-items-list > li[data-first] { background: var(--state-active-row-bg); box-shadow: var(--state-active-row-mark); }
.personal-item-head { display: flex; align-items: flex-start; gap: 4px; min-width: 0; }
.personal-item-open { display: flex; flex: 1; align-items: flex-start; gap: 12px; min-width: 0; padding: 6px 8px; border: 0; border-radius: 10px; background: none; color: var(--text);
  font: inherit; text-align: left; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .personal-item-open:hover { background: var(--hover); } }
.personal-item-open:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.personal-item-open:active { transform: scale(var(--atlas-press)); }
.personal-item-icon { display: grid; flex: none; place-items: center; width: 32px; height: 32px; border-radius: 9px; color: var(--accent); background: var(--accent-soft); }
.personal-item-copy { display: grid; flex: 1; gap: 2px; min-width: 0; }
.personal-item-title { overflow: hidden; font-size: 15px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.personal-item-excerpt { display: -webkit-box; overflow: hidden; color: var(--muted); font-size: 13.5px; -webkit-box-orient: vertical; -webkit-line-clamp: 2; line-clamp: 2; }
.personal-item-meta { display: flex; align-items: center; gap: 5px; color: var(--muted); font-size: 12.5px; }
.personal-item-chevron { rotate: -90deg; flex: none; margin-top: 8px; color: var(--muted); transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
.personal-item-open[aria-expanded="true"] .personal-item-chevron { rotate: 0deg; }
.personal-item-actions { display: flex; flex: none; gap: 2px; padding-top: 6px; color: var(--muted); }
.personal-item-note { display: grid; gap: 10px; padding: 4px 8px 10px 52px; }
.personal-item-take { margin: 6px 0 0 -10px; }
@container (max-width: 420px) { .personal-item-action-label { display: none; } .personal-item-note { padding-left: 8px; } }
/* Rows rise in staggered (first eight), a trashed one fades out where it was while the rest close up. */
.personal-row-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
.personal-row-leave-active { position: absolute; animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.personal-row-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
</style>
