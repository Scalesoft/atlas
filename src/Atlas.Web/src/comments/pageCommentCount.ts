import { shallowRef, watch, type Ref } from "vue";
import { viewSessions, type ViewSessions } from "../live/viewSession";
import { commentApi, type CommentApi } from "./commentApi";
import { isCommentsChange } from "./commentThread";

/**
 * The quiet count at the page's discussion button (the dock's Diskuse): the live comments of the page, read once and again at
 * each notice of the page's view session (the one the thread of the dock and of the page's section hear too). `fresh` says the count
 * grew after the first read (a colleague wrote), until `seen()` (the reader opened the discussion). `pageId` undefined: nothing is read
 * (the page's text comes first). A failed read leaves the count as it was: a badge is quiet.
 */
export function usePageCommentCount(pageId: () => string | undefined, api: Pick<CommentApi, "counts"> = commentApi, views: Pick<ViewSessions, "open"> = viewSessions()) {
  const count = shallowRef(0);
  const fresh = shallowRef(false);
  watch(pageId, (id, _before, onCleanup) => {
    count.value = 0;
    fresh.value = false;
    if (!id) return;
    let alive = true, known = false;
    const read = async () => {
      try {
        const answer = await api.counts([id]);
        if (!alive) return;
        const next = Object.entries(answer).find(([key]) => key.toLowerCase() === id.toLowerCase())?.[1] ?? 0;
        if (known && next > count.value) fresh.value = true;
        known = true;
        count.value = next;
      } catch { /* quiet */ }
    };
    void read();
    const handle = views.open(`page:${id}`);
    const stop = handle.watch(id, { changed: change => { if (change === null || isCommentsChange(change)) void read(); }, missed: () => void read() });
    onCleanup(() => { alive = false; stop(); handle.close(); });
  }, { immediate: true });
  return {
    count: count as Readonly<Ref<number>>,
    fresh: fresh as Readonly<Ref<boolean>>,
    /** The open thread knows its count at once (also after the reader's own comment). */
    set(value: number) { count.value = value; },
    seen() { fresh.value = false; },
  };
}
