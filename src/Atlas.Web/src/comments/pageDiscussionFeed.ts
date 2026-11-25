import { shallowRef, watch, type Ref } from "vue";
import { viewSessions, type ViewSessions } from "../live/viewSession";
import { plainTitle } from "../objects/relationsApi";
import { commentApi, type CommentApi } from "./commentApi";
import { isCommentsChange } from "./commentThread";

/** A colleague's new comment as the peek under the avatars shows it. */
export interface DiscussionBubble { id: string; authorId: string; authorName: string; text: string }

/** At most this many bubbles wait for the peek; the older ones are still counted as unread. */
export const maxBubbles = 5;

/**
 * What the page's discussion button and the peek under the avatars show (the dock's Diskuse is the full thread, this is its quiet
 * edge): the page's live comment count, a colleague's new comments as `bubbles` and how many of them are `unread`. The newest page
 * is read once after the page's text (it only seeds what is known) and again at each notice of the page's view session, the one the
 * threads hear too; what arrives is what the page did not show yet. While the discussion is `open` (the dock's Diskuse) nothing
 * queues and nothing stays unread: opening it takes the bubbles away and clears the count, and what comes while it is open is read
 * in its list. `pageId` undefined: nothing is read. A failed read leaves things as they were: the edge is quiet.
 */
export function usePageDiscussionFeed(
  pageId: () => string | undefined,
  options: { selfId: () => string | undefined; open: () => boolean },
  api: Pick<CommentApi, "page"> = commentApi,
  views: Pick<ViewSessions, "open"> = viewSessions(),
) {
  const count = shallowRef(0);
  const unread = shallowRef(0);
  const bubbles = shallowRef<DiscussionBubble[]>([]);
  const seen = () => { unread.value = 0; bubbles.value = []; };
  watch(options.open, open => { if (open) seen(); });
  watch(pageId, (id, _before, onCleanup) => {
    count.value = 0;
    seen();
    if (!id) return;
    let alive = true;
    const known = new Set<string>();
    let seeded = false;
    const read = async () => {
      try {
        const page = await api.page(id);
        if (!alive) return;
        count.value = page.count;
        const arrived = page.items.filter(comment => !known.has(comment.id) && !comment.deleted);
        page.items.forEach(comment => known.add(comment.id));
        if (!seeded) { seeded = true; return; }
        if (options.open()) return;
        const others = arrived.filter(comment => comment.authorId.toLowerCase() !== options.selfId()?.toLowerCase()).reverse();
        if (!others.length) return;
        unread.value += others.length;
        bubbles.value = [...bubbles.value, ...others.map(comment => ({ id: comment.id, authorId: comment.authorId, authorName: comment.authorName, text: plainTitle(comment.body).slice(0, 160) }))].slice(-maxBubbles);
      } catch { /* quiet */ }
    };
    void read();
    const handle = views.open(`page:${id}`);
    const stop = handle.watch(id, { changed: change => { if (change === null || isCommentsChange(change)) void read(); }, missed: () => void read() });
    onCleanup(() => { alive = false; stop(); handle.close(); });
  }, { immediate: true });
  return {
    count: count as Readonly<Ref<number>>,
    unread: unread as Readonly<Ref<number>>,
    bubbles: bubbles as Readonly<Ref<readonly DiscussionBubble[]>>,
    /** The open thread knows its count at once (also after the reader's own comment). */
    set(value: number) { count.value = value; },
    /** A bubble went (its time ran out, or it was dismissed); it stays counted as unread. */
    dismiss(id: string) { bubbles.value = bubbles.value.filter(bubble => bubble.id !== id); },
    seen,
  };
}
