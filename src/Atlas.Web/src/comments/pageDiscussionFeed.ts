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
    const handle = views.open(`page:${id}`);
    const stop = handle.watch(id, { changed: change => { if (change === null || isCommentsChange(change)) void read(); }, missed: () => void read() });
    onCleanup(() => { alive = false; stop(); handle.close(); });
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
