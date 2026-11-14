import { shallowRef } from "vue";
import { ApiError, errorMessage } from "../api";
import { cleanBoardText } from "../boards/boardText";
import type { CommentApi, ObjectComment } from "./commentApi";

/** The server's limit of a comment's Markdown. */
export const maxCommentText = 10_000;

// By the instant, not the text: the server writes as many fraction digits as a time has („…:00Z“ beside „…:00.1Z“).
const newestFirst = (a: ObjectComment, b: ObjectComment) =>
  Date.parse(b.createdAt) - Date.parse(a.createdAt) || (a.id < b.id ? 1 : a.id > b.id ? -1 : 0);

/** One of each comment (the one read later wins), newest first by time and then id, as the server pages them. */
export function mergeComments(current: readonly ObjectComment[], incoming: readonly ObjectComment[]): ObjectComment[] {
  const byId = new Map(current.map(comment => [comment.id, comment]));
  for (const comment of incoming) byId.set(comment.id, comment);
  return [...byId.values()].sort(newestFirst);
}

/** The object's view session says its comments changed (`{ kind: "comments", count, latestId }`). */
export function isCommentsChange(change: unknown): boolean {
  return !!change && typeof change === "object" && (change as { kind?: unknown }).kind === "comments";
}

/** A comment's Markdown as the server takes it (the board texts' character rule, the comment's length), trailing space trimmed. */
export function cleanCommentText(markdown: string): string {
  return cleanBoardText(markdown, { max: maxCommentText }).trimEnd();
}

/**
 * The comments of one object as a thread shows them (comments spec §3): the newest page first, older pages by the last comment's
 * id, a notice of the object's view session reading the newest page again (merged, or started over when more arrived than a
 * page holds). Posting, editing and deleting apply the server's answer at once; a deleted comment stays as its placeholder.
 */
// shortcut: a colleague's edit or deletion of a comment beyond the newest page shows with the next load; carry the changed id in
// the notice if long discussions need it.
export function createCommentThread(objectId: string, api: CommentApi) {
  const items = shallowRef<ObjectComment[]>([]);
  const count = shallowRef(0);
  const hasMore = shallowRef(false);
  const canPost = shallowRef(false);
  const status = shallowRef<"idle" | "loading" | "ready" | "missing" | "failed">("idle");
  const error = shallowRef("");
  const loadingOlder = shallowRef(false);
  /** The comment on its way to the server: shown at once as a pending row, replaced by the server's answer or dropped when it fails. */
  const pending = shallowRef<string | null>(null);
  let reads = 0;

  async function readNewest() {
    const read = ++reads;
    try {
      const page = await api.page(objectId);
      if (read !== reads) return;
      const known = new Set(items.value.map(comment => comment.id));
      // More arrived than one page shows: what is between would be missing, so the thread starts over from the newest.
      const gap = page.hasMore && items.value.length > 0 && !page.items.some(comment => known.has(comment.id));
      items.value = gap || !items.value.length ? page.items : mergeComments(items.value, page.items);
      if (gap || status.value !== "ready") hasMore.value = page.hasMore;
      count.value = page.count;
      canPost.value = page.canPost;
      status.value = "ready";
      error.value = "";
    } catch (failure) {
      if (read !== reads) return;
      const gone = failure instanceof ApiError && (failure.status === 404 || failure.status === 403);
      status.value = gone ? "missing" : "failed";
      error.value = errorMessage(failure);
    }
  }

  return {
    items, count, hasMore, canPost, status, error, loadingOlder, pending,
    /** The first read (and a retry after a failure). */
    async load() {
      status.value = "loading";
      await readNewest();
    },
    /** A notice of the view session: the newest page again, the older comments kept. */
    refresh: readNewest,
    async older() {
      const last = items.value.at(-1);
      if (!last || loadingOlder.value) return;
      loadingOlder.value = true;
      try {
        const page = await api.page(objectId, last.id);
        items.value = mergeComments(items.value, page.items);
        hasMore.value = page.hasMore;
      } finally {
        loadingOlder.value = false;
      }
    },
    async post(body: string, anchor?: string | null) {
      pending.value = body;
      try {
        const created = await api.post(objectId, body, anchor);
        items.value = mergeComments(items.value, [created]);
        count.value++;
      } finally {
        pending.value = null;
      }
    },
    async edit(commentId: string, body: string) {
      const updated = await api.edit(objectId, commentId, body);
      items.value = mergeComments(items.value, [updated]);
    },
    async remove(commentId: string) {
      await api.remove(objectId, commentId);
      items.value = items.value.map(comment => comment.id === commentId
        ? { ...comment, body: "", anchor: null, deleted: true, canEdit: false, canDelete: false }
        : comment);
      count.value = Math.max(0, count.value - 1);
    },
  };
}
export type CommentThread = ReturnType<typeof createCommentThread>;
