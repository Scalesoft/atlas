<script setup lang="ts">
import t from "../../i18n/boards";
import { computed, markRaw, nextTick, onBeforeUnmount, onUnmounted, provide, reactive, ref, shallowRef, watch } from "vue";
import type { BoardBlockNode, BoardEdit } from "../../boards/boardApi";
import type { BoardItem } from "../../boards/boardModel";
import { youtrackResolver, type PickedObject } from "../../boards/boardBlockNodes";
import { boardContextBlockHost, boardTextDocument, type BoardBlockHost } from "../../boards/editorBlocks";
import { useModules } from "../../composables/useModules";
import { kindOfNode, watchOverviewLevels, type OverviewBoardItem } from "../../overview/overviewKinds";
import { useToast } from "../../composables/useToast";
import { projectorApi } from "../../projector/projectorApi";
import { api, errorMessage, post } from "../../api";
import { useWorkspacePanel } from "../../composables/useWorkspacePanel";
import type { YouTrackIssue } from "@atlas/markdown/plugins/youtrack";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { createBoardSession, type BoardSessionDeps, type BoardSessionState, type BoardViewItem } from "../../boards/boardSession";
import { editedPage } from "../../boards/pageEditing";
import { editorUndo } from "../../boards/editorUndo";
import { lazyComponent } from "../../lazyComponent";
import { holdUnsavedWork } from "../../lib/unsavedWork";
import type { CollaborationPeer } from "../../collaborationPresence";
import DocumentTitle from "../DocumentTitle.vue";
import ConflictNote from "../settings/ConflictNote.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import SaveState, { type SaveStateKind } from "../SaveState.vue";
import type { StatusTone } from "../settings/types";
import { Button } from "../ui/button";
import BoardCanvas from "./BoardCanvas.vue";
import BoardPagePicker from "./BoardPagePicker.vue";
import BoardRemoved from "./BoardRemoved.vue";
import BoardContactPicker from "./pickers/BoardContactPicker.vue";
import BoardIssuePicker from "./pickers/BoardIssuePicker.vue";
import BoardObjectDetail from "./detail/BoardObjectDetail.vue";
import { boardDetailTabs, type BoardDetailTab } from "../../boards/boardDetail";
import { commentApi, type CommentApi } from "../../comments/commentApi";
import { commentCountsKey, commentedObjectsOf, discussionOf } from "../../comments/boardComments";
import { isCommentsChange } from "../../comments/commentThread";
import AtlasIcon from "../AtlasIcon.vue";
import { hostViewKey } from "./preview/hostView";
import { isPlainNavigationClick, pageLink } from "../../pageLinks";
import { flattenInlineMarkdown } from "./text/boardMarkdownText";
import "../settings/settings.css";

// The texts of the canvas are drawn by a chunk its nodes load on demand (a relations graph never needs it); a board asks for it up front.
void import("./text/BoardMarkdown.vue").catch(() => undefined);
// The page's editor loads only when a card asks for it.
const PageUndoWedge = lazyComponent(() => import("../PageUndoWedge.vue"));
const BoardPageEditor = lazyComponent(() => import("./preview/BoardPageEditor.vue"));

/**
 * `#/boards/<id>`: one board with its state of saving and connection, around BoardCanvas. The shell mounts it (keyed by the
 * board) only while the module Boards is enabled, in the workspace frame of a page (interaction spec §6): the shell's bar
 * carries the breadcrumbs through the tree, the actions and the avatars of who else is here (`meta`, `peers`, `goToPeer`);
 * this view the title, renamed in place like a page's, the state of saving, and the canvas filling the rest.
 */
const props = defineProps<{ boardId: string; deps?: BoardSessionDeps; comments?: CommentApi; /** Opened locked (a touch screen, the profile's „Stránky otevírat: jen pro čtení“), as a page. */ openLocked?: boolean }>();
interface BoardFrameMeta { spaceId: string; title: string; canEdit: boolean }
/** `sessionExpired`: a save was answered 401; the shell checks the session and, if it is over, shows the sign-in as it does everywhere. */
const emit = defineEmits<{ meta: [meta: BoardFrameMeta]; peers: [peers: CollaborationPeer[]]; openPage: [pageId: string, heading?: string, block?: string]; openObject: [id: string, typeId: string]; sessionExpired: [] }>();

const state = shallowRef<BoardSessionState>();
const session = createBoardSession(props.boardId, () => { state.value = session.state; }, props.deps);
state.value = session.state;
// The lock of editing (3.6-F, contract K13): the page's lock on a board. Locked, the board reads as for a reader (its texts and blocks
// read-only in place, no tools) until it is unlocked; only who may write has it.
const locked = ref(!!props.openLocked);
const lockPulse = ref(0);
const writable = computed(() => !!state.value?.canEdit && !locked.value);
// Unlocking removes the lock control the focus was on: it returns to the canvas, where the board's keys work.
function toggleLock() { locked.value = !locked.value; if (!locked.value) void nextTick(() => canvas.value?.focusSurface()); }
/** `E` on the board, as on a page; never while typing (a field, an editor being edited). */
function lockKey(event: KeyboardEvent) {
  if (event.defaultPrevented || event.key.toLowerCase() !== "e" || event.ctrlKey || event.metaKey || event.altKey || event.shiftKey || !state.value?.canEdit) return;
  const target = event.target as HTMLElement | null;
  if (target?.closest("input, textarea, select, [contenteditable]:not([contenteditable='false'])")) return;
  event.preventDefault();
  toggleLock();
}
// Disposed once the canvas is gone: a text editor taken away mid-edit hands its text to the session in its own beforeUnmount (UX-B-2).
onUnmounted(() => { session.dispose(); editedPage.value = null; });
// The tab going away (hidden, closed, reloaded): what waits behind the batch in flight goes now (W4-3); what an editor saves on the
// same event goes at once by itself (`apply` on a hidden page).
const leaving = () => { if (document.visibilityState === "hidden") session.flushOnHide(); };
window.addEventListener("pagehide", session.flushOnHide);
document.addEventListener("visibilitychange", leaving);
onUnmounted(() => { window.removeEventListener("pagehide", session.flushOnHide); document.removeEventListener("visibilitychange", leaving); });
onUnmounted(holdUnsavedWork(() => !!session.state.pending || holdingText.value));
// The shell routes by the address (App.vue goBack / home do the same for an unavailable page).
const goHome = () => { location.hash = "#/library/spaces"; };
const goBack = () => { if (history.length > 1) history.back(); else goHome(); };
// The cards' page previews follow their pages through the board's view session instead of a channel each.
provide(hostViewKey, () => session.view);
watch(() => state.value?.signedOut, signedOut => { if (signedOut) emit("sessionExpired"); });
// The editor reads and writes `a-` components (a contact in a note's text) only once its dialect, loaded on demand, has arrived;
// the board waits for it as a page's editor does (DocumentEditor). Opened straight after a reload, it failed (BD-8).
const dialectSettled = ref(false);
void loadMarkdownDialect()
  .catch(error => { console.error("Nepodařilo se načíst dialekt Markdownu.", error); })
  .finally(() => { dialectSettled.value = true; });

/**
 * What the header says about the board: read-only, offline, saving, saved. A state is always an icon and a word. Saving itself is the
 * page's shared SaveState (Atlas 3.6-B); the board's own states (access, sign-in, typed text, live changes) stay a badge.
 */
const badge = computed<{ save: SaveStateKind } | { tone: StatusTone; label: string; icon?: string }>(() => {
  const current = state.value!;
  if (current.status === "revoked") return { tone: "danger", label: t.view.revoked, icon: "lock" };
  if (current.signedOut) return { tone: "danger", label: t.view.signedOut, icon: "lock" };
  if (current.blocked) return { save: "error" };
  if (!current.canEdit) return { tone: "neutral", label: t.view.readOnly, icon: "eye" };
  if (current.offline || current.connection === "offline" || current.connection === "reconnecting")
    return current.pending ? { save: "offline" } : { tone: "warning", label: t.view.offline, icon: "triangle-alert" };
  if (current.pending) return { save: "saving" };
  if (holdingText.value) return { tone: "warning", label: t.view.unsaved, icon: "pencil" };
  if (current.liveRefused) return { tone: "warning", label: t.view.noLive, icon: "triangle-alert" };
  return { save: "saved" };
});
/** An editor on the board holds typed text it has not yet saved (it saves after a pause and when the page is hidden). */
const holdingText = ref(false);

const picking = shallowRef<((page: { id: string; title: string } | null) => void) | null>(null);
function pickPage() {
  return new Promise<{ id: string; title: string } | null>(resolve => { picking.value = resolve; });
}
function picked(page: { id: string; title: string } | null) {
  if (page) session.notePage(page.id, page.title);
  picking.value?.(page);
  picking.value = null;
}
// The issue picker answers editor blocks (boardBlockNodes.ts), each a `block` item; the contact picker directory contacts, each a
// live object card.
const pickingBlocks = shallowRef<{ kind: "issue"; resolve: (blocks: BoardBlockNode[] | null) => void } | null>(null);
const pickIssues = () => new Promise<BoardBlockNode[] | null>(resolve => { pickingBlocks.value = { kind: "issue", resolve }; });
function pickedBlocks(blocks: BoardBlockNode[] | null) {
  pickingBlocks.value?.resolve(blocks);
  pickingBlocks.value = null;
}
const pickingContacts = shallowRef<((objects: PickedObject[] | null) => void) | null>(null);
const pickContacts = () => new Promise<PickedObject[] | null>(resolve => { pickingContacts.value = resolve; });
function pickedContacts(objects: PickedObject[] | null) {
  pickingContacts.value?.(objects);
  pickingContacts.value = null;
}
// The board's own YouTrack endpoints (its space's projects, spec §4): one host for every block of the board, the paste's resolve
// and the issue picker's search.
const resolveIssue = youtrackResolver(props.boardId);
const blockHost: BoardBlockHost = {
  ...boardContextBlockHost(props.boardId, () => state.value?.spaceId),
  // Asked when a text or block acts: a lost right or a closed board turns its editors read-only in place.
  canWrite: () => writable.value && state.value?.status === "ready",
  links: { spaceId: () => state.value?.spaceId ?? "" },
  // The overview blocks over the whole board (OverviewReadCard): its items, its id the scope of their levels.
  board: { id: props.boardId, items: () => (state.value?.items ?? []) as readonly OverviewBoardItem[] },
  // The space's requirement catalog for „Požadavek z katalogu“, as a page of the space has it (U3-10).
  ...(useModules().enabled("projector") && {
    projector: { spaceId: () => state.value?.spaceId ?? "", pageId: () => undefined, api: projectorApi, open: (pageId: string) => emit("openPage", pageId) },
    // „Uspořádat jako architekturu“: the deployment's product and service cards (DeploymentBoardCard), laid out by Projector, loaded on use.
    layout: { offers: (item: BoardItem) => item.kind === "page" && (item.props?.display === "product" || item.props?.display === "service"),
      changes: async (items: readonly BoardItem[], ids: readonly string[]) => (await import("../../projector/arrangeDeploymentBoard")).arrangeDeploymentCards(items, ids) },
  }),
};
const searchIssues = (query: string, _spaceId: string, signal: AbortSignal) =>
  api<YouTrackIssue[]>(`/boards/${encodeURIComponent(props.boardId)}/youtrack/search?q=${encodeURIComponent(query)}`, { signal });

function openOwnerPage(event: MouseEvent) {
  if (!state.value?.ownerPageId || !isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("openPage", state.value.ownerPageId);
}

// Who else is on the board, as a page shows its co-editors: the same avatars and the same colour per person.
const canvas = ref<InstanceType<typeof BoardCanvas>>();
const peerIds = new Map<string, number>();
// The same list while the same people are here as before: a drag preview moves a cursor 20 times a second, and the bar's avatars
// (a TransitionGroup that measures them on every render, a layout of the whole board) must not draw again for it (COL2-1).
const peers = computed<CollaborationPeer[]>(previous => {
  const next = (state.value?.remote ?? []).map(user => {
    if (!peerIds.has(user.userId)) peerIds.set(user.userId, peerIds.size + 1);
    return { clientId: peerIds.get(user.userId)!, id: user.userId, name: user.displayName || t.session.colleague, color: user.color, cursorAvailable: !!user.cursor };
  });
  const same = (a: CollaborationPeer, b: CollaborationPeer) => a.clientId === b.clientId && a.id === b.id && a.name === b.name && a.color === b.color && a.cursorAvailable === b.cursorAvailable;
  return previous && previous.length === next.length && next.every((peer, index) => same(peer, previous[index]!)) ? previous : next;
});
function goToPeer(clientId: number) {
  const user = state.value?.remote.find(person => peerIds.get(person.userId) === clientId);
  if (user?.cursor) canvas.value?.centreOn(user.cursor);
}

// The levels of the kinds the board holds an overview of (overviewKinds.ts): data-overview-<kind> on the board's root, one state per
// board and kind for all its overviews, so the kind's components on the canvas change with them (contact blocks…). A board without an
// overview of a kind keeps its components' full look.
const root = ref<HTMLElement>();
const overviewKindsShown = computed(() => new Set((state.value?.items ?? []).filter(item => !item.deleted && item.kind === "block")
  .map(item => { const block = item.props?.block as { type?: string; attrs?: Record<string, unknown> } | undefined; return kindOfNode(block?.type, block?.attrs); })
  .filter((kind): kind is string => !!kind)));
let stopLevels: (() => void) | undefined;
watch(root, element => { stopLevels?.(); stopLevels = element ? watchOverviewLevels(element, props.boardId, kind => overviewKindsShown.value.has(kind)) : undefined; });
onBeforeUnmount(() => stopLevels?.());

// The object detail (objects spec §6) opens in the page workspace dock, the same dock and tabs pages use; it follows the board live.
const workspacePanel = useWorkspacePanel();
// Comments (comments spec §5): the quiet counts of the board (its „Diskuse“) and of its cards' objects (PageCard), read for the
// objects on the board whenever they change, again when the board's view session says its comments changed, and from the dock's
// thread as it counts.
// shortcut: a colleague's comment on a card's object updates that card's badge with the next read (a card added, the board's own
// comments, the dock); watch the cards' objects in the view session if the badges should follow at once.
const commentCounts = shallowRef<Record<string, number>>({});
const countOf = (objectId: string) => commentCounts.value[objectId.toLowerCase()] ?? 0;
provide(commentCountsKey, countOf);
let countsRead: AbortController | undefined;
function readCounts() {
  const items = state.value?.items;
  if (!items) return;
  countsRead?.abort();
  countsRead = new AbortController();
  (props.comments ?? commentApi).counts(commentedObjectsOf(props.boardId, items), countsRead.signal)
    .then(counts => { commentCounts.value = Object.fromEntries(Object.entries(counts).map(([id, count]) => [id.toLowerCase(), count])); syncDetail(); })
    .catch(() => { /* a badge is quiet: without counts the cards show none */ });
}
function counted(objectId: string, count: number) {
  commentCounts.value = { ...commentCounts.value, [objectId.toLowerCase()]: count };
  syncDetail();
}
watch(() => (state.value?.status === "ready" ? commentedObjectsOf(props.boardId, state.value.items).join() : ""), ids => { if (ids) readCounts(); });
let stopComments: (() => void) | undefined;
watch(() => state.value?.status === "ready" && session.view, view => {
  if (view && !stopComments) stopComments = view.watch(props.boardId, { changed: change => { if (isCommentsChange(change)) readCounts(); } });
}, { immediate: true });
onBeforeUnmount(() => { stopComments?.(); countsRead?.abort(); });
const detail = reactive({
  item: null as BoardViewItem | null, items: [] as readonly BoardViewItem[], boardTitle: "", tab: "overview" as BoardDetailTab,
  boardId: props.boardId, commentCount: 0, host: markRaw(blockHost), onComments: counted,
  onOpenPage: (pageId: string) => emit("openPage", pageId), onOpenObject: (id: string, typeId: string) => emit("openObject", id, typeId), onFocus: (itemId: string) => showItem(itemId),
  // The dock is outside this view's tree, so its preview gets the board's view session as a prop.
  preview: { view: () => session.view },
});
/** The card the dock shows, null for the board itself, undefined while it shows nothing of this board. */
let detailId: string | null | undefined;
const detailShown = () => workspacePanel.open.value && workspacePanel.content.value === BoardObjectDetail;
function syncDetail() {
  const current = state.value;
  if (detailId === undefined || !current) return;
  const item = detailId ? current.items.find(entry => entry.id === detailId) ?? null : null;
  // The card is gone (deleted here or by someone else): so is its detail.
  if (detailId && !item) { detailId = undefined; if (detailShown()) workspacePanel.hide(); return; }
  const discussion = discussionOf(item, props.boardId);
  // A board's own item has no count of its own: its comments are the board's.
  Object.assign(detail, { item, items: current.items, boardTitle: current.title, commentCount: item && discussion.ofBoard ? 0 : countOf(discussion.objectId) });
}
function openDetail(itemId: string | null, tab: BoardDetailTab = "overview") {
  detailId = itemId;
  detail.tab = tab;
  syncDetail();
  workspacePanel.show({ label: itemId ? t.view.objectDetail : t.view.boardDetail, content: BoardObjectDetail, props: detail, tabs: boardDetailTabs, activeTab: tab });
}
watch(() => [state.value?.items, state.value?.title], syncDetail);
watch(() => workspacePanel.activeTab.value, tab => { if (detailShown() && tab) detail.tab = tab as BoardDetailTab; });
onBeforeUnmount(() => { if (detailShown()) workspacePanel.hide(); });
/** The board's own Diskuse is in the dock: the shell's discussion button (the page's PageDiscussionButton) is pressed and hands its peek over. */
const discussionOpen = computed(() => detailShown() && !detail.item && detail.tab === "comments");
// The lock's switch, the discussion button and the dock toggle stand in the shell's top bar where a page has them (F21); the state and its E stay here.
defineExpose({ goToPeer, locked, lockPulse, toggleLock, discussionOpen, host: blockHost,
  /** The discussion button and Alt C, as on a page: the board's Diskuse in the dock, or the dock closed when it shows it. */
  toggleDiscussion: () => (discussionOpen.value ? workspacePanel.hide() : openDetail(null, "comments")),
  /** The top bar's dock toggle, as on a page: the board's detail, or the dock closed. */
  toggleDock: () => (workspacePanel.open.value ? workspacePanel.hide() : openDetail(null)) });
function showItem(itemId: string) {
  const item = state.value?.items.find(entry => entry.id === itemId);
  if (item) canvas.value?.centreOn({ x: item.x + item.width / 2, y: item.y + item.height / 2 });
}

// „Převést na stránku“: a page of the note's Markdown at the root of the board's space, titled by its first line.
const convertError = ref("");
async function createPage(markdown: string) {
  const spaceId = state.value?.spaceId;
  if (!spaceId) return null;
  const title = flattenInlineMarkdown(markdown.split("\n").find(line => line.trim()) ?? "").slice(0, 200) || t.view.noteTitle;
  try {
    const page = await post<{ id: string; title: string }>("/pages", { spaceId, parentId: null, title, contentJson: JSON.stringify(boardTextDocument(markdown)), markdown });
    convertError.value = "";
    session.notePage(page.id, page.title);
    return { id: page.id, title: page.title };
  } catch (failure) {
    convertError.value = t.view.createFailed(errorMessage(failure));
    return null;
  }
}
watch(peers, value => emit("peers", value), { immediate: true });
// What the frame shows of the board once it has loaded; a rename here or by a colleague reaches the tree and the bar this way.
watch(() => state.value?.status === "ready" ? `${state.value.spaceId}
${state.value.title}
${state.value.canEdit}` : "", key => {
  const current = state.value;
  if (key && current) emit("meta", { spaceId: current.spaceId, title: current.title, canEdit: current.canEdit });
}, { immediate: true });

// The title is renamed in place, as every rename (P3, P4, P6): typed into the field, saved as the board's `rename` change (one
// undoable step, a toast with „Zpět“) when the field is left or Enter pressed; Esc and an empty title go back to the saved one.
// Enter and Esc hand the keys to the board, never to the page's body (P7).
const toaster = useToast();
const titleDraft = ref(state.value?.title ?? "");
const titleFocused = ref(false);
watch(() => state.value?.title, saved => { if (!titleFocused.value) titleDraft.value = saved ?? ""; });
function commitTitle() {
  const saved = state.value?.title ?? "";
  const next = titleDraft.value.trim().slice(0, 200);
  titleFocused.value = false;
  if (!next || next === saved || !state.value?.canEdit) { titleDraft.value = saved; return; }
  titleDraft.value = next;
  apply([{ type: "rename", title: next }]);
  const renamed = steps;
  // „Zpět“ is the board's undo of the rename (Ctrl+Y brings it back), while it is my last step and nobody renamed the board since.
  toaster.success(t.view.renamed, { undo: () => { if (steps === renamed && state.value?.title === next) undo(); } });
}
function titleKeys(event: KeyboardEvent) {
  if (event.isComposing) return;
  if (event.key === "Escape") titleDraft.value = state.value?.title ?? "";
  if (event.key !== "Enter" && event.key !== "Escape") return;
  event.preventDefault();
  const title = event.target as HTMLElement;
  // The board takes the keys; the title's blur then saves (or, after Esc, finds nothing to save).
  canvas.value?.focusSurface();
  if (document.activeElement !== title) return;
  title.blur();
}
// My moves through the board's history (gestures, undos, redos): the rename's „Zpět“ acts only while the rename is the last of them.
let steps = 0;
function apply(changes: readonly BoardEdit[], coalesce?: string) { steps++; session.apply(changes, coalesce); }
function undo() { steps++; session.undo(); }
function redo() { steps++; session.redo(); }
</script>

<template>
  <section ref="root" class="board-view settings-ui" data-board-view>
    <!-- The loaded board's own frame (A10): the title's row and the canvas's room, so nothing jumps when the board comes. -->
    <template v-if="state!.status === 'loading' || !dialectSettled">
      <div class="board-view-header board-view-placeholder-title" aria-hidden="true"><span /></div>
      <div class="board-view-canvas board-view-placeholder" role="status" data-board-placeholder><span class="visually-hidden">{{ t.view.loading }}</span></div>
    </template>
    <!-- As an unavailable page (App.vue data-page-unavailable): the same frame, way back and way home, the focus on the message. -->
    <div v-else-if="state!.status === 'missing'" class="page-unavailable">
      <EmptyState data-focus-landing icon="lock" :title="t.view.missingTitle" :text="t.view.missingText">
        <Button variant="outline" @click="goBack"><AtlasIcon name="back" />{{ t.view.back }}</Button>
        <Button variant="default" @click="goHome"><AtlasIcon name="book" />{{ t.view.library }}</Button>
      </EmptyState>
    </div>
    <ErrorState v-else-if="state!.status === 'failed'" :title="t.view.failedTitle" :text="state!.error" @retry="session.retry()" />
    <template v-else>
      <!-- „Zpět“ of the board in the corner: a writer on an unlocked board (`writable`), while the session has a step to take back.
           Not there at all for a reader, as over a page. -->
      <PageUndoWedge v-if="state!.canEdit" :editable="writable" :can-undo="editorUndo ? editorUndo.canUndo : state!.canUndo" @undo="editorUndo ? editorUndo.undo() : undo()" />
      <header class="board-view-header">
        <DocumentTitle v-model="titleDraft" class="board-view-title" :label="t.view.titleLabel" :landing="false" :readonly="!writable || state!.status !== 'ready'" maxlength="200" data-board-title
          @focus="titleFocused = true" @blur="commitTitle" @keydown="titleKeys" />
        <SaveState v-if="'save' in badge" :state="badge.save" data-board-status /><StatusBadge v-else :tone="badge.tone" :label="badge.label" :icon="badge.icon" data-board-status />
        <!-- A page's own board (the deployment board) says whose it is and leads back; its cards follow the page. -->
        <a v-if="state!.ownerPageId" class="board-view-owner" :href="pageLink(state!.ownerPageId)" data-board-owner-page @click="openOwnerPage">
          <AtlasIcon name="file-text" :size="15" />{{ t.view.deploymentPage }}</a>
        <BoardRemoved v-if="writable && state!.status === 'ready'" :board-id="boardId" @restore="apply([{ type: 'restore', itemId: $event }])" />
      </header>
      <ConflictNote v-if="state!.rejected" :text="state!.rejected" @dismiss="session.dismissRejection()" />
      <ConflictNote v-if="convertError" :text="convertError" @dismiss="convertError = ''" />
      <ConflictNote v-if="state!.status === 'revoked'" :dismissible="false"
        :text="t.view.revokedText(state!.pending)" />
      <ConflictNote v-if="state!.signedOut" :dismissible="false" :reload-label="t.view.retry" :text="t.view.signedOutText(state!.pending)" @reload="session.resume()" />
      <div v-if="state!.blocked" class="board-view-blocked">
        <ConflictNote :dismissible="false"
          :text="t.view.blockedText(state!.blocked)" />
        <div class="board-view-choices">
          <Button size="sm" type="button" data-board-send-again @click="session.sendAgain()"><AtlasIcon name="refresh" :size="15" />{{ t.view.retry }}</Button>
          <Button variant="outline" size="sm" type="button" data-board-discard @click="session.discardQueued()"><AtlasIcon name="undo-2" :size="15" />{{ t.view.discard(state!.blocked) }}</Button>
        </div>
      </div>
      <BoardCanvas ref="canvas" class="board-view-canvas" :place-key="boardId" :items="state!.items" :label="state!.title" :can-edit="writable" :remote="state!.remote"
        :settings="state!.settings" :can-undo="state!.canUndo" :can-redo="state!.canRedo" :pick-page="pickPage" :pick-issues="pickIssues"
        :pick-contacts="pickContacts" :resolve-issue="resolveIssue" :block-host="blockHost" :create-page="writable ? createPage : undefined"
        @change="apply" @preview="session.presence($event)" @viewport="session.viewport($event)" @undo="undo" @redo="redo" @open-page="(pageId, heading, block) => emit('openPage', pageId, heading, block)"
        @open-object="(id, typeId) => emit('openObject', id, typeId)" @open-detail="openDetail" @unsaved="holdingText = $event" @keydown="lockKey" />
      <BoardPagePicker :open="!!picking" :anchor="canvas?.$el" :space-id="state!.spaceId" @pick="picked" @cancel="picked(null)" />
      <BoardIssuePicker :open="pickingBlocks?.kind === 'issue'" :anchor="canvas?.$el" :space-id="state!.spaceId" :fetch-issues="searchIssues" @pick="pickedBlocks" @cancel="pickedBlocks(null)" />
      <BoardContactPicker :open="!!pickingContacts" :anchor="canvas?.$el" @pick="pickedContacts" @cancel="pickedContacts(null)" />
      <!-- A page card's editing (Enter, F2, double-click; COL2-9); a preview's is in place. Outside the canvas, so its keys and pointers never reach it. -->
      <BoardPageEditor v-if="editedPage" :key="editedPage.pageId" :page-id="editedPage.pageId" :title="editedPage.title" @close="editedPage = null; canvas?.focusSelection()" />
    </template>
  </section>
</template>

<style scoped>
/* The board fills the workspace under the top bar to its bottom edge, inside the page gutter (U2-15: the title stood flush
   against the sidebar and 48 px stayed empty under the canvas). */
.board-view > [data-page-undo] { position: absolute; top: 0; left: 0; --undo-wedge-left: 0px; --undo-wedge-top: 6px; }
.board-view { position: relative; box-sizing: border-box; display: flex; flex-direction: column; gap: 12px; width: 100%; min-width: 0; height: calc(100dvh - var(--atlas-workspace-bar-height, var(--atlas-topbar-height, 52px))); min-height: 480px;
  padding: 12px var(--atlas-page-gutter, 24px) 16px; }
/* The title is a heading when it cannot be written (a reader, the lock) and a field otherwise: the row keeps one height for both. */
.board-view-header { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 14px; min-width: 0; min-height: 38px; }
.board-view-owner { display: inline-flex; align-items: center; gap: 6px; color: var(--accent); font-size: 13px; font-weight: 600; text-decoration: none; }
.board-view-owner:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
@media (hover: hover) and (pointer: fine) { .board-view-owner:hover { text-decoration: underline; } }
/* A phone keeps the page's 16 px from the edges. */
@container workspace-content (max-width: 680px) { .board-view { padding: 12px 16px 16px; } }
/* On a phone the title takes the first row and the controls one row of their own: the state on the left, the rest on the right (the lock was left alone on a wrapped row, 3.6 review F30). */
@container workspace-content (max-width: 680px) {
  .board-view .board-view-header > .document-title { flex-basis: 100%; }
  .board-view-header > [data-board-status] { margin-right: auto; }
}
.board-view-blocked { display: flex; flex-direction: column; gap: 8px; }
.board-view-choices { display: flex; flex-wrap: wrap; gap: 8px; }
/* A page's title, a size smaller: the canvas below is the content and takes the room. */
/* Outranks the article's centred reading column of the title (shell.css): a board's title starts where its canvas does. */
/* DocumentTitle has two roots (its status line), so the class given to it does not land: the header styles its own title. */
.board-view .board-view-header > .document-title { flex: 1 1 240px; width: auto; min-width: 0; max-width: none; margin: 0; font-size: 1.5rem; letter-spacing: -.02em; }
/* The canvas lies on the page's own white, without a frame, to the workspace's edges: the board is the page, not a card in it.
   Its controls float on their own surfaces. */
.board-view .board-view-canvas { --board-surface: var(--surface); flex: 1 1 auto; min-height: 420px; margin: 0 calc(-1 * var(--atlas-page-gutter, 24px)) -16px;
  border: 0; border-radius: 0; }
@container workspace-content (max-width: 680px) { .board-view .board-view-canvas { margin-inline: -16px; } }
/* Loading: a title bar and the empty canvas in the skeleton's grey, shown after the skeleton's short delay. */
.board-view-placeholder-title > span { display: block; width: min(320px, 60%); height: 22px; border-radius: 6px; background: var(--hover); }
.board-view-placeholder-title, .board-view-placeholder { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) var(--atlas-motion-defer) both; }
.board-view .board-view-placeholder { background: color-mix(in oklab, var(--hover) 45%, var(--surface)); }
</style>
