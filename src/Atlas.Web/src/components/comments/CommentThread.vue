<script setup lang="ts">
import { observeMarkdownProximity } from "@atlas/markdown/core";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { errorMessage } from "../../api";
import { inertBlockHost, type BoardBlockHost } from "../../boards/editorBlocks";
import { commentApi, type CommentApi, type ObjectComment } from "../../comments/commentApi";
import { createCommentThread, isCommentsChange } from "../../comments/commentThread";
import { formatAgo, formatDateTime } from "../../formatDate";
import { focusWhenPresent } from "../../keyboard/navigation";
import { requestedComment, requestedQuote } from "../../comments/commentFocus";
import { scrollBehavior } from "../../motion/aiMotion";
import { viewSessions, type ViewHandle, type ViewSessions } from "../../live/viewSession";
import { draftKeeper, drafts } from "../../lib/unsavedWork";
import AtlasIcon from "../AtlasIcon.vue";
import BoardMarkdown from "../boards/text/BoardMarkdown.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";
import EmptyState from "../settings/EmptyState.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import { Button } from "../ui/button";
import CommentComposer from "./CommentComposer.vue";
import t from "../../i18n/components-search";

/**
 * The discussion of one object (comments spec §5), the one thread of a page (a section under its content), of a board (its
 * „Diskuse“ in the dock) and of a card's object (the dock's „Diskuse“ tab): a flat timeline as in Wiki.js, newest first, with the
 * author's avatar and name, the relative time („upraveno“ after an edit), a placeholder for a deleted comment, „Starší komentáře“
 * for the next twenty and the field „Napsat komentář…“ with the page editor's Markdown (Ctrl+Enter sends). The reader edits and
 * deletes what the server allows (own comments; any as a manager of the object). A deleted comment has no „Zpět“: the comments API has no restore
 * (a gap of the rules, docs/interaction-rules.md P6).
 *
 * Fast first: nothing is read before the thread comes near the viewport (`lazy`). New comments arrive through the object's view
 * session (`viewKey`, e.g. `page:{id}` or the board's `board:{id}` for a card's object): the notice carries ids only and the thread
 * reads the newest page itself. Comments rise in, a deleted one cross-fades to its placeholder; reduced motion keeps the fades.
 * Key it by the object: one instance is one object's thread.
 *
 * A page section without comments stays one quiet row (its title and „Napsat komentář“): the field opens on demand, so an empty
 * discussion neither takes the page's room nor loads an editor; the dock's tab, opened on purpose, shows the field at once.
 *
 * Why not another list: no list of the app holds a timeline that is written to and edited in place; the rows reuse SubjectAvatar,
 * BoardMarkdown (a text rendered as a page renders it) and the editor's setup through CommentComposer.
 */
const props = withDefaults(defineProps<{
  objectId: string;
  /** The view session to hear new comments in; none: the thread does not follow others' comments. */
  viewKey?: string;
  /** The place of a new comment (a board item); null for the object as a whole. */
  anchor?: string | null;
  /** A heading with the count (the page section); none in the dock, whose tab names it. */
  title?: string;
  /** Editor services (`[[` pages, YouTrack, `@` people). */
  host?: BoardBlockHost;
  /** This thread takes the text the reader selected in the page (Alt C, `requestedQuote`) as the quote its new comment starts with: the dock's Diskuse of a page. */
  quotes?: boolean;
  /** Read only once near the viewport. */
  lazy?: boolean;
  api?: CommentApi;
  views?: Pick<ViewSessions, "open">;
}>(), { viewKey: undefined, anchor: null, title: undefined, host: () => inertBlockHost, quotes: false, lazy: true, api: () => commentApi, views: undefined });
const emit = defineEmits<{ count: [count: number]; anchor: [anchor: string] }>();

const thread = createCommentThread(props.objectId, props.api);
const { items, count, hasMore, canPost, status, error, loadingOlder, pending } = thread;
const root = ref<HTMLElement>();
const composer = ref<InstanceType<typeof CommentComposer>>();
const posting = ref(false);
const actionError = ref("");
const editing = ref("");
const saving = ref(false);
const deleting = ref<ObjectComment | null>(null);
const deletingBusy = ref(false);
watch(count, value => emit("count", value));

let handle: ViewHandle | undefined;
let unwatch: (() => void) | undefined;
let near: ReturnType<typeof observeMarkdownProximity> | undefined;
let started = false;
function start() {
  if (started) return;
  started = true;
  void thread.load();
  if (!props.viewKey) return;
  handle = (props.views ?? viewSessions()).open(props.viewKey);
  // A summary (null: the object is outside the view's viewport) may be a comment too; the read is one cheap request.
  unwatch = handle.watch(props.objectId, {
    changed: change => { if (change === null || isCommentsChange(change)) void thread.refresh(); },
    missed: () => void thread.refresh(),
  });
}
// A comment asked for by address (a notification of a mention): the thread reads at once, older pages until it holds the comment,
// scrolls to it, highlights it for a moment and puts the keyboard on it. Only the page's own thread (titled) answers.
const highlighted = ref("");
async function revealRequested() {
  const id = requestedComment.value;
  if (!id || !props.title || status.value !== "ready") return;
  requestedComment.value = undefined;
  try { for (let pages = 0; pages < 10 && hasMore.value && !items.value.some(comment => comment.id === id); pages++) await thread.older(); } catch { /* the thread shows what it has */ }
  await nextTick();
  const row = rowOf(id) as HTMLElement | null | undefined;
  if (!row) return;
  row.scrollIntoView({ block: "center", behavior: scrollBehavior(row) });
  row.focus({ preventScroll: true });
  highlighted.value = id;
  setTimeout(() => { if (highlighted.value === id) highlighted.value = ""; }, 3000);
}
watch([status, requestedComment], () => void revealRequested());
// A selection of the page asked for the discussion: the field opens (an empty page section unfolds) with it quoted and the caret under it.
async function takeQuote() {
  const text = requestedQuote.value;
  if (!text || !props.quotes || status.value !== "ready" || !canPost.value) return;
  requestedQuote.value = undefined;
  composing.value = true;
  await nextTick();
  await composer.value?.quote(text);
}
watch([status, requestedQuote], () => void takeQuote(), { flush: "post" });
onMounted(() => {
  // The page's one proximity observer (within a viewport of the screen, or at once on a focus or a press inside).
  if (!props.lazy || !root.value || requestedComment.value) start();
  else near = observeMarkdownProximity(root.value, start);
});
onBeforeUnmount(() => { near?.dispose(); unwatch?.(); handle?.close(); });

async function run(action: () => Promise<void>) {
  actionError.value = "";
  try { await action(); } catch (failure) { actionError.value = errorMessage(failure); throw failure; }
}
async function post(markdown: string) {
  posting.value = true;
  // Sent: a draft the field kept of it when it went meanwhile (the reader left during the send) is not a draft any more.
  try { await run(() => thread.post(markdown, props.anchor)); keepDraft(draftKey, undefined); composer.value?.reset(); } catch { /* shown */ } finally { posting.value = false; }
}
// Keyboard pass: the caret goes into the field once it has crossed in, and back to the row's pencil when the edit ends.
const rowOf = (id: string) => root.value?.querySelector(`[data-comment-id="${CSS.escape(id)}"]`);
async function editComment(id: string) {
  editing.value = id;
  await nextTick();
  const row = rowOf(id);
  if (row) void focusWhenPresent(row, '[contenteditable="true"]', 90);
}
async function stopEditing(id: string) {
  editing.value = "";
  await nextTick();
  const row = rowOf(id);
  const active = document.activeElement;
  if (row && (!active || active === document.body || row.contains(active))) void focusWhenPresent(row, "[data-comment-edit]", 60);
}
async function save(comment: ObjectComment, markdown: string) {
  saving.value = true;
  try { await run(() => thread.edit(comment.id, markdown)); void stopEditing(comment.id); } catch { /* shown */ } finally { saving.value = false; }
}
async function confirmDelete() {
  const comment = deleting.value;
  if (!comment) return;
  deletingBusy.value = true;
  try { await run(() => thread.remove(comment.id)); deleting.value = null; } catch { /* shown */ } finally { deletingBusy.value = false; }
}
// A new comment not sent yet is kept by its object and place (UX-P-6).
const draftKey = `comment:${props.objectId.toLowerCase()}:${props.anchor ?? ""}`;
const keepDraft = draftKeeper();
// The page section without comments: one row until the reader starts writing (Zrušit or Escape on the empty field folds it again);
// open at once with a kept draft.
const composing = ref(drafts.has(draftKey));
const folded = computed(() => !!props.title && status.value === "ready" && canPost.value && !items.value.length && !composing.value);
async function startWriting() {
  composing.value = true;
  await nextTick();
  if (root.value) void focusWhenPresent(root.value, '.comment-composer [contenteditable="true"]');
}
async function stopWriting() {
  composing.value = false;
  await nextTick();
  if (root.value) void focusWhenPresent(root.value, "[data-comment-start]");
}
const hidden = () => status.value === "missing" || (status.value === "ready" && !canPost.value && !items.value.length);
</script>

<template>
  <section ref="root" class="comment-thread" :aria-label="title ?? t.comments.discussion" :aria-busy="status === 'loading' || undefined" :hidden="hidden()" data-comment-thread>
    <header v-if="title" class="comment-thread-head">
      <h2 class="comment-thread-title">{{ title }}</h2>
      <span v-if="count" class="comment-thread-count" data-comment-count>{{ count }}</span>
      <Button v-if="folded" class="comment-thread-start" variant="ghost" size="sm" type="button" data-comment-start @click="startWriting">
        <AtlasIcon name="quote" :size="15" />{{ t.comments.write }}</Button>
    </header>
    <template v-if="status === 'ready'">
      <CommentComposer v-if="canPost && !folded" ref="composer" :host="host" :busy="posting" :cancelable="!!title && !items.length" :draft-key="draftKey" @submit="post"
        @cancel="stopWriting" />
      <p v-else-if="!canPost" class="comment-thread-note" data-comments-off>{{ t.comments.off }}</p>
    </template>
    <p v-if="actionError" class="comment-thread-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ actionError }}</p>

    <TransitionGroup tag="ol" name="comment" class="comment-list" :aria-label="t.comments.comments">
      <li v-if="pending !== null" key="pending" class="comment" data-comment-pending aria-busy="true">
        <SubjectAvatar kind="user" name="" size="sm" />
        <div class="comment-main">
          <div class="comment-meta"><span class="comment-time">{{ t.comments.sending }}</span></div>
          <BoardMarkdown class="comment-body" :text="pending" :aria-label="t.comments.text" />
        </div>
      </li>
      <li v-for="comment in items" :key="comment.id" class="comment" :data-comment-id="comment.id" :data-deleted="comment.deleted || undefined" :data-target="highlighted === comment.id || undefined" tabindex="-1">
        <SubjectAvatar kind="user" :name="comment.authorName" size="sm" />
        <div class="comment-main">
          <div class="comment-meta">
            <strong class="comment-author" :title="comment.authorName">{{ comment.authorName }}</strong>
            <time class="comment-time" :datetime="comment.createdAt" :title="formatDateTime(comment.createdAt)">{{ formatAgo(comment.createdAt) }}</time>
            <span v-if="comment.editedAt && !comment.deleted" class="comment-edited" :title="t.comments.editedTitle(formatDateTime(comment.editedAt))" data-comment-edited>{{ t.comments.edited }}</span>
            <button v-if="comment.anchor && !comment.deleted" type="button" class="comment-anchor" data-comment-anchor @click="emit('anchor', comment.anchor)">
              <AtlasIcon name="map-pin" :size="13" />{{ t.comments.place }}</button>
            <span v-if="!comment.deleted && editing !== comment.id && (comment.canEdit || comment.canDelete)" class="comment-actions">
              <Button v-if="comment.canEdit" variant="ghost" size="icon-xs" type="button" :aria-label="t.comments.editComment" :title="t.comments.edit" data-comment-edit
                @click="editComment(comment.id)"><AtlasIcon name="pencil" :size="13" /></Button>
              <Button v-if="comment.canDelete" variant="ghost" size="icon-xs" type="button" :aria-label="t.comments.deleteComment" :title="t.comments.delete" data-comment-delete
                @click="deleting = comment"><AtlasIcon name="trash" :size="13" /></Button>
            </span>
          </div>
          <Transition name="comment-swap" mode="out-in">
            <p v-if="comment.deleted" key="deleted" class="comment-deleted" data-comment-placeholder>{{ t.comments.removed }}</p>
            <CommentComposer v-else-if="editing === comment.id" key="editing" :initial="comment.body" :submit-label="t.comments.apply" cancelable :busy="saving" :host="host"
              :aria-label="t.comments.editing" @submit="save(comment, $event)" @cancel="stopEditing(comment.id)" />
            <BoardMarkdown v-else :key="`body-${comment.editedAt}`" class="comment-body" :text="comment.body" :aria-label="t.comments.text" />
          </Transition>
        </div>
      </li>
    </TransitionGroup>

    <EmptyState v-if="status === 'ready' && !items.length && !title" compact icon="quote" :title="t.comments.emptyTitle" :text="t.comments.emptyText" data-comments-empty />
    <Button v-if="hasMore" class="comment-older" variant="outline" size="sm" type="button" :disabled="loadingOlder" :aria-busy="loadingOlder || undefined"
      data-comment-older @click="thread.older().catch(failure => (actionError = errorMessage(failure)))">{{ t.comments.older }}</Button>
    <p v-if="status === 'failed'" class="comment-thread-error" role="alert">
      <AtlasIcon name="triangle-alert" :size="15" />{{ error }} <Button variant="ghost" size="sm" type="button" @click="thread.load()">{{ t.comments.retry }}</Button></p>

    <ConfirmDialog :open="!!deleting" :title="t.comments.deleteTitle" :confirm-label="t.comments.delete" :busy="deletingBusy" @confirm="confirmDelete" @cancel="deleting = null">
      <p>{{ t.comments.deleteText }}</p>
    </ConfirmDialog>
  </section>
</template>

<style scoped>
.comment-thread { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
.comment-thread[hidden] { display: none; }
.comment-thread-head { display: flex; align-items: center; gap: 8px; min-height: 32px; }
.comment-thread-title { margin: 0; font-size: 18px; font-weight: 650; }
/* Folded (no comments yet): the title steps back to a quiet label and the row asks for the first comment. */
.comment-thread-head:has(.comment-thread-start) .comment-thread-title { color: var(--muted); font-size: 15px; font-weight: 600; }
.comment-thread-start { margin-left: auto; color: var(--muted); }
.comment-thread-count { padding: 0 7px; border-radius: 999px; background: var(--hover); color: var(--muted); font-size: 12px; font-weight: 600; line-height: 20px; }
.comment-thread-note { margin: 0; color: var(--muted); font-size: 13px; }
.comment-thread-error { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin: 0; color: var(--error); font-size: 13px; }
.comment-list { display: flex; flex-direction: column; gap: 2px; margin: 0 -10px; padding: 0; list-style: none; }
/* A row is its own quiet surface on hover or while its actions hold focus; the bleed keeps text aligned with the field above. */
.comment { display: flex; gap: 10px; min-width: 0; padding: 10px; border-radius: 10px; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .comment:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); } }
.comment:focus-within { background: color-mix(in srgb, var(--hover) 45%, transparent); }
.comment[data-comment-pending] { opacity: .6; }
.comment[data-target] { background: color-mix(in srgb, var(--accent) 12%, transparent); }
.comment:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
.comment-main { display: flex; flex: 1; flex-direction: column; gap: 4px; min-width: 0; }
/* One line even in the dock: the author's name gives way (ellipsis) before the time or the actions wrap under it. */
.comment-meta { display: flex; align-items: center; gap: 8px; min-width: 0; min-height: 24px; font-size: 13px; white-space: nowrap; }
.comment-author { min-width: 0; max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.comment-time, .comment-edited { flex-shrink: 0; color: var(--muted); font-size: 12px; }
.comment-anchor { display: inline-flex; flex-shrink: 0; align-items: center; gap: 3px; padding: 0 4px; border: 0; border-radius: 6px; background: none; color: var(--accent); font: inherit; font-size: 12px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .comment-anchor:hover { background: var(--hover); } }
.comment-anchor:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.comment-body { min-width: 0; font-size: 14px; overflow-wrap: anywhere; }
.comment-deleted { margin: 0; color: var(--muted); font-size: 13px; font-style: italic; }
/* Edit and delete sit at the end of the meta line and show with the row (hover or keyboard focus); always on touch. Nothing moves. */
.comment-actions { display: inline-flex; gap: 2px; margin-left: auto; color: var(--muted); }
@media (hover: hover) and (pointer: fine) {
  .comment-actions { opacity: 0; transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }
  .comment:hover .comment-actions, .comment:focus-within .comment-actions { opacity: 1; }
}
.comment-older { align-self: flex-start; }
/* A comment rises in (own and others'), leaves fading; a body cross-fades to its placeholder or editor. Reduced motion: fades (motion.css). */
.comment-enter-active { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both; }
.comment-leave-active { animation: st-fade-out var(--atlas-motion-base) var(--atlas-ease-out) both; }
.comment-move { transition: transform var(--atlas-motion-base) var(--atlas-ease-inout); }
.comment-swap-enter-active { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.comment-swap-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out) both; }
</style>
