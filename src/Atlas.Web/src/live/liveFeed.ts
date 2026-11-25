import { getCurrentInstance, getCurrentScope, inject, onScopeDispose, ref, watch, type InjectionKey } from "vue";
import { liveConnection, type LiveConnection } from "./liveConnection";

/**
 * Server push instead of polling (3.6): a page names what it follows (a topic of the tab's one /hubs/live connection) and how it reads its
 * state over HTTP; `useLiveFeed` reads it again once each time the subscription takes effect (the first time and after every reconnect,
 * which covers what was missed meanwhile) and on every accepted event, one read at a time. There is no timer and no hidden polling: when the
 * feed cannot be had, `unavailable` says so and the page offers a manual refresh (LiveUpdateNote).
 *
 * Events only say "read again": `JobRunChanged { runId, jobId, state, spaceId }`, `LogsWritten { level }`, `PdfExportChanged { pageId }`,
 * `PageChanged { pageId }`, `TreeChanged { spaceId }`. The jobs and log feeds are derived by the server from the account's rights (SubscribeAll): a client never
 * names a space; a space administrator hears only their spaces' runs, the log only effective wiki administrators.
 */
export interface LiveTopic {
  /** The connection's channel: a derived feed's kind (`jobs`, `logs`) or a channel name (`page:{id}`). */
  channel: string;
  event: string;
  /** Subscribed by SubscribeAll(kind): the server picks the channels. */
  derived?: boolean;
  /** The channel an event belongs to, when it is not this topic's alone (PageChanged reaches every page channel of the tab). */
  route?: (payload: unknown) => string | undefined;
}
export interface LiveFeedListener {
  message(payload: unknown): void;
  /** The subscription took effect (again): read the state. */
  subscribed(): void;
  /** Refused, revoked for good, or the connection is lost; a later `subscribed` ends it. */
  unavailable(): void;
}
/** Opens a topic and returns its close; the gallery and the tests stand one in (`liveFeedSourceKey`). */
export type LiveFeedSource = (topic: LiveTopic, listener: LiveFeedListener) => () => void;

export const jobsTopic: LiveTopic = { channel: "jobs", event: "JobRunChanged", derived: true };
export const logsTopic: LiveTopic = { channel: "logs", event: "LogsWritten", derived: true };
/** What GET /api/ai/capabilities answers may have changed: every signed-in account. The event carries nothing. */
export const aiTopic: LiveTopic = { channel: "ai", event: "AiChanged", derived: true };
/** The health of Správa wiki → AI may have changed: effective wiki administrators only. The event carries nothing. */
export const aiAdminTopic: LiveTopic = { channel: "ai-admin", event: "AiHealthChanged", derived: true };
const pageOf = (payload: unknown) => {
  const id = payload && typeof payload === "object" ? (payload as { pageId?: unknown }).pageId : undefined;
  return typeof id === "string" ? `page:${id.toLowerCase()}` : undefined;
};
/** A page's own channel: `PageChanged` for its readers, `PdfExportChanged` for its editors. */
export const pageTopic = (pageId: string, event: "PageChanged" | "PdfExportChanged"): LiveTopic => ({ channel: `page:${pageId.toLowerCase()}`, event, route: pageOf });

const spaceOf = (payload: unknown) => {
  const id = payload && typeof payload === "object" ? (payload as { spaceId?: unknown }).spaceId : undefined;
  return typeof id === "string" ? `tree:${id.toLowerCase()}` : undefined;
};
/** A space's tree: `TreeChanged` whenever anything one of its rows shows may have changed (place, title, publication, icon, verification, a page's own board), by anyone. */
export const treeTopic = (spaceId: string): LiveTopic => ({ channel: `tree:${spaceId.toLowerCase()}`, event: "TreeChanged", route: spaceOf });

/** The job runs as JobRunChanged names them. */
export interface JobRunChange { runId: number; jobId: string; state: string; spaceId: string | null }

/** The tab's /hubs/live connection. A revoked derived feed is asked for again (the server derives what is left, or refuses). */
export function liveConnectionSource(live: () => LiveConnection = liveConnection): LiveFeedSource {
  return (topic, listener) => {
    const connection = live();
    connection.route(topic.event, topic.route ?? (() => topic.channel));
    let closed = false, opening = true;
    let subscription = open();
    function open() {
      return connection.subscribe(topic.channel, {
        message: (_event, payload) => listener.message(payload),
        subscribed: () => listener.subscribed(),
        refused: () => listener.unavailable(),
        revoked: () => {
          listener.unavailable();
          if (!closed && topic.derived) subscription = open();
        },
        // The first status is the connection's state before it opens; only a loss after that counts.
        status: (status) => { if (!opening && (status === "offline" || status === "reconnecting")) listener.unavailable(); },
      }, topic.derived ? { join: "SubscribeAll", leave: "UnsubscribeAll", key: topic.channel } : undefined);
    }
    opening = false;
    return () => { closed = true; subscription.close(); };
  };
}

export const liveFeedSourceKey: InjectionKey<LiveFeedSource> = Symbol("liveFeedSource");
const defaultSource = liveConnectionSource();

/** One read at a time; asked again while one runs, it reads once more after it. */
function serialized(read: () => unknown) {
  let running = false, again = false;
  return function go() {
    if (running) { again = true; return; }
    running = true;
    void Promise.resolve().then(read).catch(() => {}).finally(() => {
      running = false;
      if (again) { again = false; go(); }
    });
  };
}

/**
 * Follows a loaded tree of a space until the returned close: `read` (the shell's loadTree) runs on every TreeChanged, one read at a time,
 * and after a reconnect (a change may have been missed); not on the first subscription, which comes right after the read that loaded it.
 */
export function followTree(spaceId: string, read: () => unknown, source: LiveFeedSource = defaultSource) {
  const refresh = serialized(read);
  let subscriptions = 0;
  return source(treeTopic(spaceId), { message: refresh, subscribed: () => { if (subscriptions++) refresh(); }, unavailable: () => {} });
}

/**
 * Follows `topic` (a getter is watched: undefined closes it, a new channel reopens) until the calling scope ends; `read` loads the state over
 * HTTP and runs once per subscription and per accepted event. `refresh` is the same serialized read, for a manual button.
 */
export function useLiveFeed<Payload = unknown>(topic: LiveTopic | (() => LiveTopic | undefined), read: () => unknown,
  accept: (payload: Payload) => boolean = () => true, source?: LiveFeedSource) {
  const open = source ?? (getCurrentInstance() ? inject(liveFeedSourceKey, defaultSource) : defaultSource);
  const unavailable = ref(false);
  const refresh = serialized(read);
  let close: (() => void) | undefined;
  const stop = watch(typeof topic === "function" ? topic : () => topic, (current, previous) => {
    if (current && previous && current.channel === previous.channel && current.event === previous.event) return;
    close?.();
    close = undefined;
    unavailable.value = false;
    if (!current) return;
    close = open(current, {
      message: (payload) => { if (accept(payload as Payload)) refresh(); },
      subscribed: () => { unavailable.value = false; refresh(); },
      unavailable: () => { unavailable.value = true; },
    });
  }, { immediate: true });
  function dispose() {
    stop();
    close?.();
    close = undefined;
  }
  if (getCurrentScope()) onScopeDispose(dispose);
  return { unavailable, refresh, dispose };
}
