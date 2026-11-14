import { checkSessionAfterLiveFailure } from "../api";
import { throttleLatest } from "./throttleLatest";

/**
 * The one SignalR connection of a browser tab to `/hubs/live` (spec §7, plan §7.2 and §11): channels such as `board:{id}`
 * share it, each with a reference-counted hub subscription. It reconnects on its own, subscribes every channel again and
 * hands each listener the server's sequence so the owner can catch up. This is the seed of the future WorkspaceSession,
 * so it knows nothing about boards: a module registers how its events name their channel (`route`). View sessions
 * (viewSession.ts) are channels joined by their own protocol, and hear their object events with `on`.
 */
export type LiveStatus = "connecting" | "online" | "reconnecting" | "offline";

/** The part of @microsoft/signalr's HubConnection this uses (a fake in the tests). */
export interface LiveHub {
  on(event: string, handler: (payload: unknown) => void): void;
  onreconnecting(callback: (error?: Error) => void): void;
  onreconnected(callback: (connectionId?: string) => void): void;
  onclose(callback: (error?: Error) => void): void;
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T = unknown>(method: string, ...args: unknown[]): Promise<T>;
  send(method: string, ...args: unknown[]): Promise<void>;
}

export interface ChannelListener<State = { sequence: number }> {
  message?(event: string, payload: unknown): void;
  /** Subscribed, also again after a reconnect: the server's answer (a channel's current sequence, a view's members). */
  subscribed?(state: State): void;
  /** The server refused the subscription: `forbidden`, `not_found`, `unsupported`, or `failed` for anything else. */
  refused?(code: string): void;
  /** The server ended the subscription (`Revoked`, optionally with a reason): nothing more arrives for this channel until it is subscribed again. Whether access is really gone is for the owner to find out (over HTTP). */
  revoked?(): void;
  status?(status: LiveStatus): void;
}
export interface ChannelSubscription {
  /** Ephemeral presence (cursor, drag preview): at most one message per interval, the latest wins, dropped while offline. */
  presence(payload: unknown): void;
  /** Asks the server again (after it ended the subscription by other means than `Revoked`, as a view's revoked object). */
  join(): void;
  close(): void;
}
/** How a channel is joined and left when not by Subscribe/Unsubscribe of its name: a view session (JoinView/LeaveView of its key). */
export interface ChannelProtocol { join: string; leave: string; key: string }

interface Channel {
  listeners: Set<ChannelListener<any>>; presence: ReturnType<typeof throttleLatest<unknown>>; protocol?: ChannelProtocol;
  /** The server's last answer while online, for a listener that comes after it (UX3-1). */
  answer?: { state: unknown } | { refused: string };
}

const refusalCodes = ["forbidden", "not_found", "unsupported"];
function refusalOf(error: unknown) {
  const message = error instanceof Error ? error.message : String(error);
  return refusalCodes.find(code => new RegExp(`\\b${code}\\b`).test(message)) ?? "failed";
}
const channelField = (payload: unknown) => {
  const channel = payload && typeof payload === "object" ? (payload as { channel?: unknown }).channel : undefined;
  return typeof channel === "string" ? channel : undefined;
};

async function connectLiveHub(): Promise<LiveHub> {
  const { HubConnectionBuilder, LogLevel } = await import("@microsoft/signalr");
  return new HubConnectionBuilder()
    .withUrl("/hubs/live")
    // Never gives up: 1, 2, 4 … 30 s between attempts while the tab is open.
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ({ previousRetryCount, retryReason }) => {
      // A reconnect refused as signed out (UX1-1) ends the session; the shell's sign-out then closes every channel.
      void checkSessionAfterLiveFailure(retryReason);
      return Math.min(30_000, 1000 * 2 ** previousRetryCount);
    } })
    .configureLogging(LogLevel.None)
    .build();
}

export function createLiveConnection(
  connect: () => Promise<LiveHub> = connectLiveHub,
  options: { presenceIntervalMs?: number; retryMs?: (attempt: number) => number; failed?: (error: unknown) => void } = {},
) {
  // A failure may be an ended session rather than a lost network (UX1-1): that one goes to sign-in, the rest stay offline and retry.
  const failed = options.failed ?? ((error: unknown) => void checkSessionAfterLiveFailure(error));
  // 20 presence messages a second per channel; the server drops beyond 30 per connection.
  const interval = options.presenceIntervalMs ?? 50;
  const retryMs = options.retryMs ?? ((attempt: number) => Math.min(30_000, 1000 * 2 ** attempt));
  const channels = new Map<string, Channel>();
  const routes = new Map<string, (payload: unknown) => string | undefined>([["Presence", channelField], ["Revoked", channelField]]);
  /** Events that belong to no single channel (a view session's object messages), for whoever registered them. */
  const handlers = new Map<string, Set<(payload: unknown) => void>>();
  let hub: LiveHub | undefined;
  let opening: Promise<void> | undefined;
  let status: LiveStatus = "offline";
  let retry: ReturnType<typeof setTimeout> | undefined;
  let attempt = 0;

  function setStatus(next: LiveStatus) {
    status = next;
    for (const channel of channels.values()) {
      channel.answer = undefined;
      for (const listener of channel.listeners) listener.status?.(next);
    }
  }
  function dispatch(event: string, payload: unknown) {
    handlers.get(event)?.forEach(handler => handler(payload));
    const name = routes.get(event)?.(payload);
    const channel = name ? channels.get(name) : undefined;
    if (!channel) return;
    if (event === "Revoked") {
      channels.delete(name!);
      channel.presence.cancel();
      channel.listeners.forEach(listener => listener.revoked?.());
      closeIfIdle();
      return;
    }
    channel.listeners.forEach(listener => listener.message?.(event, payload));
  }
  function listen(target: LiveHub, event: string) {
    target.on(event, payload => dispatch(event, payload));
  }
  async function join(name: string) {
    const protocol = channels.get(name)?.protocol;
    if (!hub || status !== "online") return;
    try {
      const state = await hub.invoke(protocol?.join ?? "Subscribe", protocol?.key ?? name);
      const channel = channels.get(name);
      if (channel) channel.answer = { state };
      channel?.listeners.forEach(listener => listener.subscribed?.(state));
    } catch (error) {
      const code = refusalOf(error);
      const channel = channels.get(name);
      if (channel) channel.answer = { refused: code };
      channel?.listeners.forEach(listener => listener.refused?.(code));
    }
  }
  async function start() {
    const target = hub;
    if (!target || !channels.size) return;
    try {
      await target.start();
      // The last channel closed while it started: `closeIfIdle` has stopped this hub, so nothing of it is ours any more.
      if (hub !== target) return;
      attempt = 0;
      setStatus("online");
      await Promise.all([...channels.keys()].map(join));
    } catch (error) {
      if (hub !== target) return;
      failed(error);
      setStatus("offline");
      if (channels.size) retry = setTimeout(() => void start(), retryMs(attempt++));
    }
  }
  function open() {
    opening ??= connect().then(async connection => {
      // The last channel closed while the connection was being made: stay idle; the next subscription opens one again.
      if (!channels.size) { opening = undefined; return; }
      hub = connection;
      for (const event of new Set([...routes.keys(), ...handlers.keys()])) listen(connection, event);
      connection.onreconnecting(error => { failed(error); setStatus("reconnecting"); });
      connection.onreconnected(() => { setStatus("online"); void Promise.all([...channels.keys()].map(join)); });
      connection.onclose(error => {
        if (hub === connection && channels.size) { failed(error); setStatus("offline"); retry = setTimeout(() => void start(), retryMs(attempt++)); }
      });
      setStatus("connecting");
      await start();
    }).catch(() => {
      // The client library itself failed to load: offline until the next subscription tries again.
      opening = undefined;
      setStatus("offline");
    });
  }
  function closeIfIdle() {
    if (channels.size || !hub) return;
    const closing = hub;
    hub = undefined;
    opening = undefined;
    clearTimeout(retry);
    status = "offline";
    void closing.stop().catch(() => {});
  }
  function send(method: string, ...args: unknown[]) {
    if (hub && status === "online") void hub.send(method, ...args).catch(() => {});
  }
  const known = (event: string) => routes.has(event) || handlers.has(event);

  return {
    get status() { return status; },
    /** Names the channel of a server event that does not carry a `channel` field (BoardChanged → `board:{boardId}`). */
    route(event: string, channelOf: (payload: unknown) => string | undefined) {
      const listened = known(event);
      routes.set(event, channelOf);
      if (hub && !listened) listen(hub, event);
    },
    /** Hears an event that belongs to no single channel; for the life of the connection. */
    on(event: string, handler: (payload: unknown) => void) {
      const listened = known(event);
      handlers.set(event, (handlers.get(event) ?? new Set()).add(handler));
      if (hub && !listened) listen(hub, event);
    },
    /** A hub method while online; rejects otherwise. */
    invoke(method: string, ...args: unknown[]): Promise<unknown> {
      return hub && status === "online" ? hub.invoke(method, ...args) : Promise.reject(new Error("offline"));
    },
    /** A hub message while online, dropped otherwise. */
    send,
    subscribe<State = { sequence: number }>(name: string, listener: ChannelListener<State>, protocol?: ChannelProtocol): ChannelSubscription {
      let channel = channels.get(name);
      const first = !channel;
      if (!channel) channels.set(name, (channel = { listeners: new Set(), presence: throttleLatest(interval, payload => send("SendPresence", name, payload)), protocol }));
      channel.listeners.add(listener);
      // A listener that joins a channel already subscribed hears what the others heard: the state now and the server's answer.
      listener.status?.(status);
      const answer = channel.answer;
      if (answer && "state" in answer) listener.subscribed?.(answer.state as State);
      else if (answer) listener.refused?.(answer.refused);
      if (!hub) open();
      else if (first) void join(name);
      const mine = channel;
      return {
        presence(payload) {
          if (channels.get(name) === mine && status === "online") mine.presence.push(payload);
        },
        join() { if (channels.get(name) === mine) void join(name); },
        close() {
          if (!mine.listeners.delete(listener) || mine.listeners.size || channels.get(name) !== mine) return;
          channels.delete(name);
          mine.presence.cancel();
          if (hub && status === "online") void hub.invoke(mine.protocol?.leave ?? "Unsubscribe", mine.protocol?.key ?? name).catch(() => {});
          closeIfIdle();
        },
      };
    },
  };
}
export type LiveConnection = ReturnType<typeof createLiveConnection>;

let shared: LiveConnection | undefined;
/** The tab's connection, created on first use. */
export function liveConnection(): LiveConnection {
  return (shared ??= createLiveConnection());
}
