import { shallowRef, watch, type Ref } from "vue";
import { viewSessions, type ViewSessions } from "../live/viewSession";
import { plainTitle } from "../objects/relationsApi";
import { commentApi, type CommentApi } from "./commentApi";
import { isCommentsChange } from "./commentThread";

/** A colleague's new comment as the peek under the avatars shows it. */
export interface DiscussionBubble { id: string; authorId: string; authorName: string; text: string }

/** A quick reply starts with a mention of the bubble's author (the editor's contact form with the account, which notifies them), so it reads as an answer to them. */
export const replyTo = (bubble: Pick<DiscussionBubble, "authorId" | "authorName">, markdown: string) => {
  const name = bubble.authorName.replace(/[\\[\]]/g, (char) => `\\${char}`);
  const query = `name=${encodeURIComponent(bubble.authorName)}&internalUser.id=${encodeURIComponent(bubble.authorId)}&internalUser.name=${encodeURIComponent(bubble.authorName)}`;
  return `[${name}](a-contact:?${query}) ${markdown}`;
};

/** At most this many bubbles wait for the peek; the older ones are still counted as unread. */
export const maxBubbles = 2;

/**
 * What the page's discussion button and the peek under the avatars show (the dock's Diskuse is the full thread, this is its quiet
 * edge): the page's live comment count, a colleague's new comments as `bubbles` and how many of them are `unread`. The newest page
 * is read once after the page's text (it only seeds what is known) and again when a notice of the page's view session (the one the
 * threads hear too) names a newest comment the feed does not know; what arrives is what the page did not show yet. The notice's
 * count is the count (the server's, the same as the boards' counts), so an edit or a deletion reads nothing.
 * While the page's thread is `open` (the dock's Diskuse, or the section under the content on a phone) that thread reads the list and
 * tells its count (`set`): the feed reads no page (only the boards' counts endpoint once, if it opens with the thread open), nothing
 * queues and nothing stays unread. Opening it takes the bubbles away and clears the count; closing it seeds once more, so what was
 * read in the thread never comes back as a bubble. `pageId` undefined: nothing is read. A failed read leaves things as they were.
 */
export function usePageDiscussionFeed(
  pageId: () => string | undefined,
  options: { selfId: () => string | undefined; open: () => boolean },
  api: Pick<CommentApi, "page" | "counts"> = commentApi,
  views: Pick<ViewSessions, "open"> = viewSessions(),
) {
  const count = shallowRef(0);
  const unread = shallowRef(0);
  const bubbles = shallowRef<DiscussionBubble[]>([]);
  const canPost = shallowRef(false);
  /** Bubbles with a quick reply open: the room made for a new one never takes them. */
  const pinned = new Set<string>();
  const trim = (list: DiscussionBubble[]) => {
    while (list.length > maxBubbles) { const at = list.findIndex(bubble => !pinned.has(bubble.id)); if (at < 0) break; list.splice(at, 1); }
    return list;
  };
  const seen = () => { unread.value = 0; bubbles.value = []; };
  watch(options.open, open => { if (open) seen(); });
  watch(pageId, (id, _before, onCleanup) => {
    count.value = 0;
    seen();
    if (!id) return;
    let alive = true;
    const known = new Set<string>();
    let seeded = false;
    let latest = 0;
    const read = async () => {
      const mine = ++latest;
      try {
        if (options.open()) {
          const counts = await api.counts([id]);
          if (alive && mine === latest) count.value = counts[id.toLowerCase()] ?? counts[id] ?? 0;
          return;
        }
        const page = await api.page(id);
        // An older answer arriving after a newer one must not overwrite it.
        if (!alive || mine !== latest) return;
        count.value = page.count;
        canPost.value = page.canPost;
        const arrived = page.items.filter(comment => !known.has(comment.id) && !comment.deleted);
        page.items.forEach(comment => known.add(comment.id));
        if (!seeded) { seeded = true; return; }
        if (options.open()) return;
        const others = arrived.filter(comment => comment.authorId.toLowerCase() !== options.selfId()?.toLowerCase()).reverse();
        if (!others.length) return;
        unread.value += others.length;
        bubbles.value = trim([...bubbles.value, ...others.map(comment => ({ id: comment.id, authorId: comment.authorId, authorName: comment.authorName, text: plainTitle(comment.body).slice(0, 160) }))]);
      } catch { /* quiet */ }
    };
    void read();
    const notice = (change: unknown) => {
      if (change !== null && !isCommentsChange(change)) return;
      const { count: told, latestId } = (change ?? {}) as { count?: unknown; latestId?: unknown };
      if (typeof told === "number") count.value = told;
      // The open thread reads the list; a notice whose newest comment is known (an edit, a deletion) brings nothing new.
      if (options.open() || (typeof told === "number" && seeded && (latestId == null || known.has(String(latestId))))) return;
      void read();
    };
    const handle = views.open(`page:${id}`);
    const stop = handle.watch(id, { changed: notice, missed: () => { if (!options.open()) void read(); } });
    // Closed again: what the thread showed is seeded, never a bubble.
    const stopOpen = watch(options.open, open => { if (!open) { seeded = false; void read(); } });
    onCleanup(() => { alive = false; stop(); stopOpen(); handle.close(); });
  }, { immediate: true });
  return {
    count: count as Readonly<Ref<number>>,
    unread: unread as Readonly<Ref<number>>,
    bubbles: bubbles as Readonly<Ref<readonly DiscussionBubble[]>>,
    /** The reader may write comments here (a quick reply is offered only then). */
    canPost: canPost as Readonly<Ref<boolean>>,
    /** A quick reply is open on the bubble (or closed again): it is never pushed out by newer ones. */
    pin(id: string, on: boolean) { if (on) pinned.add(id); else pinned.delete(id); },
    /** The open thread knows its count at once (also after the reader's own comment). */
    set(value: number) { count.value = value; },
    /** A bubble went (its time ran out, or it was dismissed); it stays counted as unread. */
    dismiss(id: string) { bubbles.value = bubbles.value.filter(bubble => bubble.id !== id); },
    seen,
  };
}
