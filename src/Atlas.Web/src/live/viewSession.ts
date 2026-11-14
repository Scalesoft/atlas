import { liveConnection, type ChannelSubscription, type LiveConnection, type LiveStatus } from "./liveConnection";
import { throttleLatest } from "./throttleLatest";

/**
 * The client of the server's view sessions on `/hubs/live` (objects spec §4.2, F1 plan §2.4). A view (`board:{id}`,
 * `page:{id}`) is joined once for everyone in the tab who opens it and left with the last of them; the server then holds the
 * subscriptions of its objects, and this routes their messages to whoever watches an object, whichever view brought them.
 *
 * - Versions: each watcher's object version is followed from the join's members, `ViewMembers` and `ObjectChanged`. When the
 *   server is further than the watcher (a reconnect, a join after the object's own load, a member gained again), the watcher
 *   is told `missed` and catches up its own way (a board reads its operations, a page preview reloads); so is one that knew no
 *   version when `ViewMembers` adds its object (a restored source of a card shown unavailable).
 * - Viewport: what the canvas shows gets the detail (changes, presence payloads), the rest a summary; it is sent at most every
 *   interval, the view's own object always first in it (its changes drive the view), and again after every join.
 * - Presence goes per object at most every interval, the latest wins; asked for before the join it waits for it (the server
 *   relays presence only at objects of a joined view).
 * - A revoked view object is joined again at once: the server answers whether it is still readable (`refused` otherwise).
 */
export interface ViewMember { objectId: string; typeId: string; version: number }
export interface ObjectPresence {
  objectId: string; userId: string; displayName: string;
  /** The server's colour of the person, the same in every view. */
  color: string;
  /** Null outside the viewport. */
  payload: Record<string, unknown> | null;
  /** The view the colleague works through, when the reader may know it; null: elsewhere. */
  via: string | null;
}
export interface ObjectListener {
  /** A confirmed change: in detail inside the viewport, null as a summary ("it changed"). */
  changed?(change: unknown, version: number): void;
  /** The server is further than this listener heard: catch up. */
  missed?(version: number): void;
  presence?(presence: ObjectPresence): void;
  /** The reader lost the object; nothing more of it arrives. */
  revoked?(): void;
}
export interface ViewListener {
  status?(status: LiveStatus): void;
  /** The server refused the view: `forbidden`, `not_found`, `unsupported`, `failed`. */
  refused?(code: string): void;
}
export interface ViewHandle {
  /** Listens to an object; `version` is the one the caller already has. Returns the stop. */
  watch(objectId: string, listener: ObjectListener, version?: number): () => void;
  /** The objects on screen. */
  viewport(objectIds: readonly string[]): void;
  /** Presence at an object of the view (needs write access), at most every interval per object, the latest wins. */
  presence(objectId: string, payload: Record<string, unknown>): void;
  close(): void;
}

/** The server's limit of SetViewport. */
export const maxViewport = 2000;

interface Watcher { listener: ObjectListener; version?: number }
interface View {
  key: string;
  objectId: string;
  listeners: Set<ViewListener>;
  subscription: ChannelSubscription;
  joined: boolean;
  /** The server's refusal of the current join, for an opener that comes after it. */
  refused?: string;
  /** The last viewport asked for, and the last one the server has. */
  wanted?: string[];
  sent?: string;
  report: ReturnType<typeof throttleLatest<void>>;
  /** Presence asked for before the join, which the server needs first: the latest per object. */
  waiting: Map<string, Record<string, unknown>>;
}

const field = (payload: unknown, name: string) => (payload && typeof payload === "object" ? (payload as Record<string, unknown>)[name] : undefined);
const idOf = (payload: unknown) => { const id = field(payload, "objectId"); return typeof id === "string" ? id.toLowerCase() : undefined; };
const membersOf = (value: unknown): ViewMember[] => (Array.isArray(value) ? value.filter(member => typeof idOf(member) === "string" && typeof field(member, "version") === "number") : []);

export function createViewSessions(
  live: Pick<LiveConnection, "status" | "subscribe" | "on" | "invoke" | "send">,
  options: { viewportIntervalMs?: number; presenceIntervalMs?: number } = {},
) {
  const viewportInterval = options.viewportIntervalMs ?? 250;
  // 20 a second per object; the server drops beyond 30 per connection.
  const presenceInterval = options.presenceIntervalMs ?? 50;
  const views = new Map<string, View>();
  const watchers = new Map<string, Set<Watcher>>();
  /** The newest version the server told of each object. */
  const versions = new Map<string, number>();
  const presences = new Map<string, ReturnType<typeof throttleLatest<Record<string, unknown>>>>();

  const watchersOf = (objectId: string) => [...(watchers.get(objectId) ?? [])];
  /** The server has `version` of the object: watchers behind it catch up, watchers that knew none take it; an object `added` to a
   * view (a restore, a card placed) is news to those too, as they may show it unavailable. */
  function reconcile(objectId: string, version: number, added = false) {
    versions.set(objectId, Math.max(versions.get(objectId) ?? version, version));
    for (const watcher of watchersOf(objectId)) {
      const behind = watcher.version === undefined ? added : version > watcher.version;
      watcher.version = Math.max(watcher.version ?? version, version);
      if (behind) watcher.listener.missed?.(version);
    }
  }
  function sendPresence(objectId: string, payload: Record<string, unknown>) {
    let throttle = presences.get(objectId);
    if (!throttle) presences.set(objectId, (throttle = throttleLatest(presenceInterval, body => live.send("SendObjectPresence", objectId, body))));
    throttle.push(payload);
  }
  function sendViewport(view: View) {
    if (!view.joined || !view.wanted) return;
    const ids = [view.objectId, ...view.wanted.filter(id => id !== view.objectId)].slice(0, maxViewport);
    const text = ids.join();
    if (text === view.sent) return;
    view.sent = text;
    void live.invoke("SetViewport", view.key, ids).catch(() => { if (view.sent === text) view.sent = undefined; });
  }

  live.on("ObjectChanged", payload => {
    const objectId = idOf(payload), version = field(payload, "version");
    if (!objectId || typeof version !== "number") return;
    versions.set(objectId, Math.max(versions.get(objectId) ?? version, version));
    for (const watcher of watchersOf(objectId)) {
      watcher.version = Math.max(watcher.version ?? version, version);
      watcher.listener.changed?.(field(payload, "change") ?? null, version);
    }
  });
  live.on("ObjectPresence", payload => {
    const objectId = idOf(payload);
    if (objectId) watchersOf(objectId).forEach(watcher => watcher.listener.presence?.({ ...(payload as ObjectPresence), objectId }));
  });
  live.on("ViewMembers", payload => {
    for (const member of membersOf(field(payload, "added"))) reconcile(idOf(member)!, member.version, true);
  });
  live.on("ObjectRevoked", payload => {
    const objectId = idOf(payload);
    if (!objectId) return;
    watchersOf(objectId).forEach(watcher => watcher.listener.revoked?.());
    for (const view of views.values()) {
      if (view.objectId !== objectId) continue;
      // The server has left the view; joining again says whether it is still readable.
      view.joined = false;
      view.sent = undefined;
      view.subscription.join();
    }
  });

  function join(key: string): View {
    const view = {
      key, objectId: key.slice(key.indexOf(":") + 1), listeners: new Set<ViewListener>(), joined: false, waiting: new Map(),
      report: throttleLatest<void>(viewportInterval, () => sendViewport(view)),
    } as View;
    view.subscription = live.subscribe<{ members?: unknown }>(`view:${key}`, {
      status(status) {
        if (status !== "online") { view.joined = false; view.sent = undefined; }
        view.refused = undefined;
        view.listeners.forEach(listener => listener.status?.(status));
      },
      subscribed(state) {
        view.joined = true;
        view.sent = undefined;
        view.refused = undefined;
        for (const member of membersOf(state?.members)) reconcile(idOf(member)!, member.version);
        sendViewport(view);
        view.waiting.forEach((payload, objectId) => sendPresence(objectId, payload));
        view.waiting.clear();
      },
      refused(code) {
        view.joined = false;
        view.refused = code;
        view.listeners.forEach(listener => listener.refused?.(code));
      },
    }, { join: "JoinView", leave: "LeaveView", key });
    return view;
  }

  return {
    open(viewKey: string, listener: ViewListener = {}): ViewHandle {
      // Lower case, as the server names views and objects.
      const key = viewKey.toLowerCase();
      const view = views.get(key) ?? join(key);
      views.set(key, view);
      // One entry per opener: the view is left when the last of them closes.
      const opener: ViewListener = { status: status => listener.status?.(status), refused: code => listener.refused?.(code) };
      view.listeners.add(opener);
      // Every opener hears the state now, also of a connection already online, which reports no change of it (UX3-1).
      opener.status!(live.status);
      if (view.refused) opener.refused!(view.refused);
      const mine = new Set<{ objectId: string; watcher: Watcher }>();
      let closed = false;
      const unwatch = (entry: { objectId: string; watcher: Watcher }) => {
        mine.delete(entry);
        const set = watchers.get(entry.objectId);
        if (set?.delete(entry.watcher) && !set.size) watchers.delete(entry.objectId);
      };
      return {
        watch(objectId, objectListener, version) {
          const id = objectId.toLowerCase();
          const entry = { objectId: id, watcher: { listener: objectListener, version: version ?? versions.get(id) } };
          if (closed) return () => {};
          watchers.set(id, (watchers.get(id) ?? new Set()).add(entry.watcher));
          mine.add(entry);
          const known = versions.get(id);
          if (version !== undefined && known !== undefined && known > version) reconcile(id, known);
          return () => unwatch(entry);
        },
        viewport(objectIds) {
          if (closed) return;
          view.wanted = objectIds.map(id => id.toLowerCase());
          view.report.push();
        },
        presence(objectId, payload) {
          if (closed) return;
          const id = objectId.toLowerCase();
          if (view.joined) sendPresence(id, payload); else view.waiting.set(id, payload);
        },
        close() {
          if (closed) return;
          closed = true;
          [...mine].forEach(unwatch);
          view.listeners.delete(opener);
          if (view.listeners.size) return;
          views.delete(key);
          view.report.cancel();
          view.subscription.close();
        },
      };
    },
  };
}
export type ViewSessions = ReturnType<typeof createViewSessions>;

let shared: ViewSessions | undefined;
/** The tab's view sessions, over its live connection. */
export function viewSessions(): ViewSessions {
  return (shared ??= createViewSessions(liveConnection()));
}
