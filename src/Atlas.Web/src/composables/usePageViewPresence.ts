import { onScopeDispose, shallowRef, watch } from "vue";
import t from "../i18n/shell";
import type { CollaborationPeer } from "../collaborationPresence";
import { createPresenceRoster, presenceActivity } from "../live/objectPresence";
import { viewSessions, type ViewHandle, type ViewSessions } from "../live/viewSession";

/** How often an edited page says again that it is edited; the boards forget a presence after 25 s without news. */
export const pagePresenceHeartbeatMs = 10_000;

/**
 * The page workspace in the page's view session `page:{id}` (objects spec §4.1) while the page is edited: it tells the views
 * showing the page (a board's card) that the user edits it, again every `pagePresenceHeartbeatMs` and `{ editing: false }` when
 * the editing ends, and answers who works at the page through another view (`elsewhere`, as avatars with their activity). The
 * co-editors in this page are the Yjs collaboration's (kept as it is), so the presence through this page itself is left out.
 */
export function usePageViewPresence(editedPageId: () => string | undefined, views: Pick<ViewSessions, "open"> = viewSessions()) {
  const elsewhere = shallowRef<CollaborationPeer[]>([]);
  let pageId: string | undefined;
  let view: ViewHandle | undefined;
  let heartbeat: ReturnType<typeof setInterval> | undefined;
  const roster = createPresenceRoster(publish);

  function viewTitle(via: string | null) {
    const kind = via?.split(":")[0];
    return kind === "board" ? t.presence.board : kind === "page" ? t.presence.page : null;
  }
  function publish() {
    const own = `page:${pageId}`;
    elsewhere.value = pageId ? roster.of(pageId).filter(person => person.via !== own).map(person => {
      const name = person.displayName || t.presence.colleague;
      // Negative: never one of the Yjs client ids beside them in the same avatars.
      return { clientId: -person.clientId, id: person.userId, name, color: person.color, cursorAvailable: false, activity: presenceActivity(name, viewTitle(person.via)) };
    }) : [];
  }
  function leave() {
    clearInterval(heartbeat);
    if (view && pageId) view.presence(pageId, { editing: false });
    view?.close();
    view = undefined;
    pageId = undefined;
    roster.dispose();
    publish();
  }

  watch(editedPageId, next => {
    const id = next?.toLowerCase();
    if (id === pageId) return;
    leave();
    if (!id) return;
    pageId = id;
    view = views.open(`page:${id}`);
    view.watch(id, { presence: presence => roster.receive(presence) });
    const announce = () => view?.presence(id, { editing: true });
    announce();
    heartbeat = setInterval(announce, pagePresenceHeartbeatMs);
  }, { immediate: true });
  onScopeDispose(leave);

  return elsewhere;
}

/**
 * `previous` while `next` lists the same peers as it, else `next`: every collaboration status builds a new list, and the avatars (a
 * TransitionGroup that measures its rows) redrawn for it forced a layout of the whole page (a lock toggle: 1.2 s at 4× CPU).
 */
export function keepPeers<T extends CollaborationPeer>(previous: readonly T[] | undefined, next: readonly T[]) {
  const same = (a: T, b: T) => a.clientId === b.clientId && a.id === b.id && a.name === b.name && a.color === b.color
    && a.cursorAvailable === b.cursorAvailable && a.activity === b.activity;
  return previous?.length === next.length && previous.every((peer, index) => same(peer, next[index]!)) ? previous : next;
}
