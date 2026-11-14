<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from "vue";
import { markdownOverlaysFor, type MarkdownRichValue } from "@atlas/markdown/core";
import type { Editor } from "@tiptap/core";
import { placeCaret, type EditPoint } from "../../../boards/boardCaret";
import { api, errorMessage, readPageBundle, type Appearance, type Page, type User } from "../../../api";
import type { CollaborationStatus } from "../../../collaboration";
import type { CollaborationPeer } from "../../../collaborationPresence";
import { readDocument } from "../../../document";
import { useToast } from "../../../composables/useToast";
import { createCheckpointScheduler } from "../../../editor/pageAutosave";
import { contactApi } from "../../../contacts/contactApi";
import { seedPageReferences } from "../../../linking/pageReferenceAnswers";
import { pageLink } from "../../../pageLinks";
import { putWithCollaborationRetry, untilProjected } from "../../../savePage";
import { clearEditorUndo, setEditorUndo } from "../../../boards/editorUndo";
import AppDialog from "../../AppDialog.vue";
import AtlasIcon from "../../AtlasIcon.vue";
import CollaboratorAvatars from "../../CollaboratorAvatars.vue";
import DocumentEditor from "../../DocumentEditor.vue";
import SaveState, { type SaveStateKind } from "../../SaveState.vue";
import { Button } from "../../ui/button";

/**
 * A page edited from its card on a board (COL2-9, roadmap 3.0-D „jeden ve wiki a druhý v tabuli“): the expanded card, a panel over
 * the board with the page's own editor (DocumentEditor), joined to the page's one live document session as the wiki's editor joins
 * it (`/pages/{id}/collaboration`, plan §6 one identity). What is typed here and in the wiki is one document: both converge, and the
 * colleagues' carets and avatars show as in the wiki. Live edits are shared and kept by the server as they are typed, and saved as the
 * page's working revision by themselves, as the wiki's page saves (3.6-B: the same checkpoint scheduler and SaveState). The session
 * lasts while the panel is open, whatever gets the focus; closing it takes the last changes and leaves the session.
 * Why not open the page itself: the board stays as it was under the panel (its viewport, selection, the card in place).
 *
 * `inline`: the same editing in place of a page preview on a board (PagePreview, 3.6: no „Upravit“, the writer clicks into the text):
 * no panel, header or footer, only the editor, which builds unseen over the preview and says `entered` once it can take the caret; the
 * caret goes `at` the point aimed at. Esc and a press outside it end the editing (`close`), as a board text's do.
 */
const props = defineProps<{ pageId: string; title?: string | null; inline?: boolean; at?: EditPoint }>();
const emit = defineEmits<{ close: []; entered: [] }>();

const open = ref(true);
const page = shallowRef<Page>();
const value = shallowRef<MarkdownRichValue>();
const identity = shallowRef<{ id: string; displayName: string }>();
const failure = ref("");
const status = shallowRef<CollaborationStatus | null>(null);
const peers = shallowRef<CollaborationPeer[]>([]);
const editor = ref<InstanceType<typeof DocumentEditor>>();
const body = ref<HTMLElement>();
/** The editor took the place of what it edits (inline: the preview); the panel's is in place from the start. */
let entered = !props.inline;
const dirty = ref(false);
const saved = ref(false);
const saveError = ref("");
const toaster = useToast();
// The page's look follows the app's (data-mode, data-accent on the root), as the board's own texts do.
const root = document.documentElement;
const dark = root.dataset.mode === "dark";
const accent = (root.dataset.accent ?? "ocean") as Appearance["accent"];

async function load() {
  failure.value = "";
  try {
    // The page with its bundle, as a page opens (3.6, one editor): the live Yjs state builds the shared editor at once, no local
    // stand-in swapped for it later; its contacts and linked pages ask nothing more.
    const [loaded, user] = await Promise.all([readPageBundle(encodeURIComponent(props.pageId)), api<User>("/auth/me")]);
    if (loaded.bundle?.contacts) contactApi.seed(loaded.bundle.contacts.items);
    if (loaded.bundle?.pages) seedPageReferences(loaded.bundle.pages.items);
    value.value = readDocument(loaded.contentJson);
    identity.value = { id: user.id, displayName: user.displayName };
    page.value = loaded;
  } catch (error) {
    failure.value = errorMessage(error);
    // Inline, the failure and its „Zkusit znovu“ show where the preview was.
    if (!entered) { entered = true; emit("entered"); }
  }
}
void load();

const canEdit = computed(() => !!page.value?.canEdit);
const state = computed(() => {
  const current = status.value;
  if (!current?.enabled) return "";
  if (current.connection === "connecting") return t.pageEditor.connecting;
  if (current.connection !== "online") return t.pageEditor.offline;
  return current.pendingChanges ? t.pageEditor.sending : t.pageEditor.live;
});
/** The person pressed a key or the pointer since the panel opened (inline: since the editor took the preview's place): the focus is theirs from then on. */
let touched = false;
const touch = () => { if (entered) touched = true; };
/**
 * The caret goes into the page once its editor can take it (the shared session hydrated: `focus()` says false until then), tried at
 * its `ready` and at every status change. The editor places it a frame later and the dialog puts its own first focus meanwhile, so it
 * is checked again for a short while until it is really in the text, and never after the person's first key or press.
 */
let placing = false;
/** The editor element the caret was last put at the end of (a rebuilt editor is a new element and gets it again). */
let placedIn: Element | undefined;
function caretIn() {
  // One attempt at a time: focusing changes the editor's state, which reports a status, which would start another.
  if (touched || placing) return;
  placing = true;
  let tries = 40;
  const attempt = async () => {
    if (touched || tries-- <= 0 || !open.value) { placing = false; return; }
    // Kept for two seconds even once in the text: the editor may still be rebuilt (the shared session taking over from the page's
    // value) and the new one starts without the focus. The editor places its caret a frame later; its element takes the focus now.
    const text = body.value?.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror");
    if (!document.activeElement?.closest?.(".board-page-editor-body .ProseMirror") && editor.value?.focus()) {
      text?.focus({ preventScroll: true });
      // Inline: the preview gives way now (and lays the editor where it stood), before the point aimed at is read in the editor.
      if (!entered) { entered = true; emit("entered"); await nextTick(); }
    }
    // Where it was aimed, else at the end of the page, as a board's text starts its editing (not at its start, where the editor puts a
    // caret nobody aimed); inline, Enter and F2 aim at the start of what the preview shows (PagePreview).
    if (entered && text?.editor && placedIn !== text && placeCaret(text.editor, props.at)) placedIn = text;
    setTimeout(attempt, 50);
  };
  void nextTick(attempt);
}
function statusChanged(next: CollaborationStatus) {
  status.value = next;
  caretIn();
}
function ready() { caretIn(); }
/**
 * Esc closes the panel (J6.3: the last open thing) once nothing of the editor's is open (a menu, a popover take their Esc first). The
 * editor takes every Esc by its key code, so the dialog itself never hears one from the text.
 */
function escape(event: KeyboardEvent) {
  if (event.key !== "Escape" || event.isComposing) return;
  const target = event.target as Element | null, text = target?.closest?.(".ProseMirror");
  // The block grip (Alt+F10) takes its own Esc back to the text, as on a page; the next one closes the panel.
  if (target?.closest?.(".dh-wrap")) return;
  if (markdownOverlaysFor(text ?? null).some(overlay => !overlay.hidden && overlay.dataset.mdOverlayConsumesEscape !== "false" && overlay.getAttribute("aria-hidden") !== "true")) return;
  event.preventDefault();
  event.stopPropagation();
  close();
}

/** A change of the shared document (here or a colleague's): it waits for the next checkpoint, as in the wiki. */
function changed() {
  dirty.value = true;
  autosave.changed();
}
/** The newest title and version: a colleague may have saved or renamed the page meanwhile; the content is the shared one. */
async function send(snapshot: ReturnType<NonNullable<typeof editor.value>["capture"]>) {
  const current = await api<Page>(`/pages/${encodeURIComponent(props.pageId)}`);
  page.value = await putWithCollaborationRetry<Page>(`/pages/${encodeURIComponent(props.pageId)}`,
    { title: current.title, ...snapshot, expectedVersion: current.version });
}
/** One checkpoint: once the server's projection of the shared log is current, the shared document as the working revision. */
async function checkpoint() {
  if (!dirty.value || !editor.value || !status.value?.canSave) return;
  dirty.value = false;
  saveError.value = "";
  try {
    await untilProjected(() => status.value);
    if (!editor.value) { dirty.value = true; return; }
    await send(editor.value.capture({ checkpoint: true }));
    saved.value = true;
  } catch (error) {
    dirty.value = true;
    saveError.value = errorMessage(error);
    throw error;
  }
}
const autosave = createCheckpointScheduler(checkpoint);
watch(() => status.value?.canSave, ready => { if (ready && dirty.value) autosave.changed(); });
const saveState = computed<SaveStateKind | null>(() => {
  if (!saved.value && !dirty.value && !saveError.value) return null;
  const connection = status.value?.connection;
  if ((dirty.value || saveError.value) && (connection === "offline" || connection === "reconnecting")) return "offline";
  if (saveError.value) return "error";
  return dirty.value ? "saving" : "saved";
});
/** Closing takes the last changes at once, before the editor goes (the wiki's leaveCheckpoint); offline they stay in the shared session. */
function close() {
  if (!open.value) return;
  open.value = false;
  leave();
  // The panel says it once it has slid away (AppDialog `closed`); in place there is nothing to wait for.
  if (props.inline) emit("close");
}
/** Inline: a press outside the editor (and its menus) ends the editing, as a board text's does. */
function outside(event: PointerEvent) {
  const target = event.target as Node | null;
  if (!target || body.value?.contains(target)) return;
  if (markdownOverlaysFor(body.value?.querySelector(".ProseMirror") ?? null).some(overlay => overlay.contains(target))) return;
  close();
}
/** The last changes at once: on closing, the tab hidden or left, the panel taken away without closing (a `:key` swap). */
function leave() {
  if (!dirty.value || !editor.value || !status.value?.canSave) return autosave.cancel();
  const snapshot = editor.value.capture({ checkpoint: true });
  autosave.cancel();
  dirty.value = false;
  void autosave.flush().catch(() => {}).then(() => send(snapshot)).catch(failure => {
    toaster.error(t.pageEditor.saveFailed(page.value?.title ?? "", errorMessage(failure)));
  });
}
function hidden() { if (document.visibilityState === "hidden") leave(); }
onMounted(() => {
  document.addEventListener("pointerdown", touch, true);
  document.addEventListener("keydown", touch, true);
  window.addEventListener("pagehide", leave);
  document.addEventListener("visibilitychange", hidden);
  if (props.inline) document.addEventListener("pointerdown", outside, true);
});
// The board's „Zpět“ wedge undoes this editor's steps while the page is edited here (boards/editorUndo.ts).
const token = {};
onBeforeUnmount(() => {
  clearEditorUndo(token);
  document.removeEventListener("pointerdown", outside, true);
  document.removeEventListener("pointerdown", touch, true);
  document.removeEventListener("keydown", touch, true);
  window.removeEventListener("pagehide", leave);
  document.removeEventListener("visibilitychange", hidden);
  if (open.value) leave();
});
</script>

<template>
  <!-- Inline, only the body: the preview's card is its frame. -->
  <component :is="inline ? 'div' : AppDialog" class="board-page-editor" :data-inline="inline || undefined"
    v-bind="inline ? {} : { title: page?.title ?? title ?? t.pageEditor.page, size: 'side', open, closeLabel: t.pageEditor.close }" @close="close" @closed="emit('close')">
    <template v-if="!inline" #header-extra>
      <CollaboratorAvatars v-if="peers.length" class="board-page-editor-peers" :peers="peers" :label="t.pageEditor.whoEdits" />
    </template>
    <div ref="body" class="board-page-editor-body" data-board-page-editor @keydown.capture="escape">
      <div v-if="failure" class="notice error" role="alert">
        <p>{{ t.pageEditor.openFailed(failure) }}</p>
        <Button variant="outline" size="sm" type="button" @click="load"><AtlasIcon name="refresh" :size="15" />{{ t.pageEditor.retry }}</Button>
      </div>
      <div v-else-if="!page || !value || !identity" class="board-page-editor-placeholder" aria-busy="true" :aria-label="t.pageEditor.loading"><span /><span /><span /></div>
      <template v-else>
        <p v-if="!canEdit" class="notice" role="status">{{ t.pageEditor.readOnly }}</p>
        <DocumentEditor ref="editor" :value="value" :readonly="!canEdit" :dark="dark" :accent="accent" :space-id="page.spaceId" :page-id="page.id" :editor-class="inline ? 'board-text' : undefined"
          :document-title="page.title" :identity="identity" :collaboration-allowed="canEdit" :can-edit="canEdit" :collaboration="page.bundle?.collaboration"
          @change="changed" @can-undo="setEditorUndo(token, $event, () => editor?.undo())" @ready="ready" @collaboration-status="statusChanged" @collaboration-peers="peers = $event" />
      </template>
    </div>
    <template v-if="!inline" #footer>
      <span class="board-page-editor-state" role="status" data-board-page-editor-state>{{ state }}</span>
      <SaveState v-if="saveState" :state="saveState" :error="saveError" data-board-page-editor-save />
      <a class="board-page-editor-wiki" :href="pageLink(pageId)">{{ t.pageEditor.openWiki }}<AtlasIcon name="external" :size="14" /></a>
    </template>
  </component>
</template>

<style scoped>
.board-page-editor :deep(.app-dialog-footer) { align-items: center; margin: 0; padding: 12px 22px; border-top: 1px solid var(--line); }
.board-page-editor-body { display: flex; flex: 1; flex-direction: column; min-height: 0; padding: 0 24px 24px; overflow: auto; }
/* In place, the preview's scroller holds it: no padding or scroll of its own, so the text lies where the preview's lay. */
.board-page-editor[data-inline] .board-page-editor-body { padding: 0; overflow: visible; }
.board-page-editor-peers { margin-left: auto; }
.board-page-editor-placeholder { display: grid; gap: 12px; padding-top: 24px; }
.board-page-editor-placeholder span { height: 12px; border-radius: 6px; background: var(--hover); }
.board-page-editor-placeholder span:first-child { width: 55%; height: 20px; }
.board-page-editor-placeholder span:last-child { width: 80%; }
.board-page-editor-state { flex: 1; min-width: 0; color: var(--muted); font-size: 13px; }
.board-page-editor-wiki { display: inline-flex; align-items: center; gap: 4px; color: var(--accent); font-size: 14px; }
</style>
