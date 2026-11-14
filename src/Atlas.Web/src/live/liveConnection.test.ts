// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { checkSessionAfterLiveFailure, liveProbeGapMs, onSessionEnded } from "../api";
import { createLiveConnection, type LiveHub } from "./liveConnection";

/** A hub connection in memory: records invocations, lets the test emit server messages and connection events. */
function fakeHub(answers: Record<string, (...args: unknown[]) => unknown> = {}) {
  const handlers = new Map<string, (payload: unknown) => void>();
  let reconnecting = (_error?: Error) => {};
  let reconnected = (_id?: string) => {};
  let closed = (_error?: Error) => {};
  const hub = {
    on: vi.fn((event: string, handler: (payload: unknown) => void) => { handlers.set(event, handler); }),
    onreconnecting: (callback: typeof reconnecting) => { reconnecting = callback; },
    onreconnected: (callback: typeof reconnected) => { reconnected = callback; },
    onclose: (callback: typeof closed) => { closed = callback; },
    start: vi.fn(async () => {}),
    stop: vi.fn(async () => {}),
    invoke: vi.fn(async (method: string, ...args: unknown[]) => (answers[method] ? answers[method](...args) : method === "Subscribe" ? { sequence: 0 } : undefined)),
    send: vi.fn(async () => {}),
  };
  return {
    hub: hub as unknown as LiveHub & typeof hub,
    emit: (event: string, payload: unknown) => handlers.get(event)?.(payload),
    reconnecting: () => reconnecting(),
    reconnected: () => reconnected("next"),
    closed: () => closed(new Error("gone")),
    invoked: (method: string) => hub.invoke.mock.calls.filter(call => call[0] === method).map(call => call.slice(1)),
  };
}
function listener() {
  return { message: vi.fn(), subscribed: vi.fn(), refused: vi.fn(), revoked: vi.fn(), status: vi.fn() };
}
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

afterEach(() => { vi.useRealTimers(); });

describe("live connection", () => {
  it("shares one connection and one hub subscription per channel between its listeners", async () => {
    const fake = fakeHub({ Subscribe: () => ({ sequence: 7 }) });
    const connect = vi.fn(async () => fake.hub);
    const live = createLiveConnection(connect);
    const first = listener(), second = listener(), other = listener();
    live.subscribe("board:1", first);
    live.subscribe("board:1", second);
    live.subscribe("board:2", other);
    await settle();
    expect(connect).toHaveBeenCalledTimes(1);
    expect(fake.invoked("Subscribe")).toEqual([["board:1"], ["board:2"]]);
    expect(first.subscribed).toHaveBeenCalledWith({ sequence: 7 });
    expect(second.subscribed).toHaveBeenCalledWith({ sequence: 7 });
    expect(first.status).toHaveBeenLastCalledWith("online");
  });

  it("delivers a message only to the listeners of its channel, by the channel field or a registered route", async () => {
    const fake = fakeHub();
    const live = createLiveConnection(async () => fake.hub);
    live.route("BoardChanged", payload => `board:${(payload as { boardId: string }).boardId}`);
    const mine = listener(), other = listener();
    live.subscribe("board:1", mine);
    live.subscribe("board:2", other);
    await settle();
    fake.emit("BoardChanged", { boardId: "1", sequence: 3 });
    fake.emit("Presence", { channel: "board:2", userId: "u" });
    expect(mine.message).toHaveBeenCalledExactlyOnceWith("BoardChanged", { boardId: "1", sequence: 3 });
    expect(other.message).toHaveBeenCalledExactlyOnceWith("Presence", { channel: "board:2", userId: "u" });
  });

  it("unsubscribes a channel with its last listener and stops the connection with the last channel", async () => {
    const fake = fakeHub();
    const live = createLiveConnection(async () => fake.hub);
    const first = live.subscribe("board:1", listener());
    const second = live.subscribe("board:1", listener());
    await settle();
    first.close();
    expect(fake.invoked("Unsubscribe")).toEqual([]);
    second.close();
    await settle();
    expect(fake.invoked("Unsubscribe")).toEqual([["board:1"]]);
    expect(fake.hub.stop).toHaveBeenCalledTimes(1);
  });

  it("subscribes every channel again after a reconnect and reports the server's sequence for the catch-up", async () => {
    let sequence = 4;
    const fake = fakeHub({ Subscribe: () => ({ sequence }) });
    const live = createLiveConnection(async () => fake.hub);
    const board = listener();
    live.subscribe("board:1", board);
    await settle();
    fake.reconnecting();
    expect(board.status).toHaveBeenLastCalledWith("reconnecting");
    expect(live.status).toBe("reconnecting");
    sequence = 9;
    fake.reconnected();
    await settle();
    expect(fake.invoked("Subscribe")).toEqual([["board:1"], ["board:1"]]);
    expect(board.subscribed).toHaveBeenLastCalledWith({ sequence: 9 });
    expect(board.status).toHaveBeenLastCalledWith("online");
  });

  it("stops delivering to a channel the server revoked", async () => {
    const fake = fakeHub();
    const live = createLiveConnection(async () => fake.hub);
    const board = listener();
    const subscription = live.subscribe("board:1", board);
    await settle();
    fake.emit("Revoked", { channel: "board:1" });
    fake.emit("Presence", { channel: "board:1", userId: "u" });
    expect(board.revoked).toHaveBeenCalledTimes(1);
    expect(board.message).not.toHaveBeenCalled();
    fake.reconnected();
    await settle();
    expect(fake.invoked("Subscribe")).toHaveLength(1);
    subscription.close();
    expect(fake.invoked("Unsubscribe")).toEqual([]);
  });

  it("reports a refused subscription by the code of the HubException", async () => {
    const fake = fakeHub({ Subscribe: () => { throw new Error("An unexpected error occurred invoking 'Subscribe' on the server. HubException: forbidden"); } });
    const live = createLiveConnection(async () => fake.hub);
    const board = listener();
    live.subscribe("board:1", board);
    await settle();
    expect(board.refused).toHaveBeenCalledWith("forbidden");
    expect(board.subscribed).not.toHaveBeenCalled();
  });

  it("hands a listener that joins a channel already subscribed the state and the server's answer the others heard", async () => {
    const fake = fakeHub({ Subscribe: (name: unknown) => { if (name === "board:2") throw new Error("HubException: not_found"); return { sequence: 4 }; } });
    const live = createLiveConnection(async () => fake.hub);
    live.subscribe("user:1", listener());
    live.subscribe("board:2", listener());
    await settle();
    const late = listener(), refused = listener();
    live.subscribe("user:1", late);
    live.subscribe("board:2", refused);
    expect(late.status).toHaveBeenCalledExactlyOnceWith("online");
    expect(late.subscribed).toHaveBeenCalledExactlyOnceWith({ sequence: 4 });
    expect(refused.refused).toHaveBeenCalledExactlyOnceWith("not_found");
    expect(fake.invoked("Subscribe")).toHaveLength(2);
    // While reconnecting there is no answer to hand on.
    fake.reconnecting();
    const meanwhile = listener();
    live.subscribe("user:1", meanwhile);
    expect(meanwhile.status).toHaveBeenCalledExactlyOnceWith("reconnecting");
    expect(meanwhile.subscribed).not.toHaveBeenCalled();
  });

  it("sends presence at most every 50 ms per channel, always the latest, and nothing while offline", async () => {
    vi.useFakeTimers();
    const fake = fakeHub();
    const live = createLiveConnection(async () => fake.hub);
    const board = live.subscribe("board:1", listener());
    await vi.advanceTimersByTimeAsync(0);
    board.presence({ n: 1 });
    board.presence({ n: 2 });
    board.presence({ n: 3 });
    expect(fake.hub.send.mock.calls).toEqual([["SendPresence", "board:1", { n: 1 }]]);
    await vi.advanceTimersByTimeAsync(50);
    expect(fake.hub.send.mock.calls).toEqual([["SendPresence", "board:1", { n: 1 }], ["SendPresence", "board:1", { n: 3 }]]);
    fake.reconnecting();
    await vi.advanceTimersByTimeAsync(100);
    board.presence({ n: 4 });
    expect(fake.hub.send).toHaveBeenCalledTimes(2);
  });

  it("starts again for a subscription that follows one that left while the connection was still being made", async () => {
    const fake = fakeHub();
    let release!: () => void;
    const connect = vi.fn()
      .mockImplementationOnce(() => new Promise<LiveHub>(resolve => { release = () => resolve(fake.hub); }))
      .mockImplementation(async () => fake.hub);
    const live = createLiveConnection(connect);
    live.subscribe("board:1", listener()).close();
    release();
    await settle();
    expect(live.status).toBe("offline");
    expect(fake.hub.start).not.toHaveBeenCalled();
    const board = listener();
    live.subscribe("board:1", board);
    await settle();
    expect(board.status).toHaveBeenLastCalledWith("online");
    expect(fake.invoked("Subscribe")).toEqual([["board:1"]]);
    expect(board.subscribed).toHaveBeenCalledTimes(1);
  });

  it("ends idle when the last channel closes while the hub is starting, and a later subscription ends online and joined", async () => {
    const fake = fakeHub();
    let started!: () => void;
    fake.hub.start.mockImplementationOnce(() => new Promise<void>(resolve => { started = resolve; }));
    const live = createLiveConnection(async () => fake.hub);
    const first = live.subscribe("board:1", listener());
    await settle();
    first.close();
    started();
    await settle();
    expect(live.status).toBe("offline");
    expect(fake.invoked("Subscribe")).toEqual([]);
    expect(fake.hub.stop).toHaveBeenCalledTimes(1);
    const board = listener();
    live.subscribe("board:1", board);
    await settle();
    expect(board.status).toHaveBeenLastCalledWith("online");
    expect(fake.invoked("Subscribe")).toEqual([["board:1"]]);
    expect(board.subscribed).toHaveBeenCalledTimes(1);
  });

  it("does not let a start that was given up on disturb the connection made after it", async () => {
    vi.useFakeTimers();
    const first = fakeHub(), second = fakeHub();
    let fail!: (error: Error) => void;
    first.hub.start.mockImplementationOnce(() => new Promise<void>((_, reject) => { fail = reject; }));
    const connect = vi.fn().mockResolvedValueOnce(first.hub).mockResolvedValueOnce(second.hub);
    const live = createLiveConnection(connect, { retryMs: () => 1000 });
    const left = live.subscribe("board:1", listener());
    await vi.advanceTimersByTimeAsync(0);
    left.close();
    const board = listener();
    live.subscribe("board:1", board);
    await vi.advanceTimersByTimeAsync(0);
    expect(board.status).toHaveBeenLastCalledWith("online");
    fail(new Error("stopped during negotiation"));
    await vi.advanceTimersByTimeAsync(5000);
    expect(live.status).toBe("online");
    expect(second.hub.start).toHaveBeenCalledTimes(1);
    expect(second.invoked("Subscribe")).toEqual([["board:1"]]);
  });

  it("keeps the connection that is being made when a new subscription arrives before it is ready", async () => {
    const fake = fakeHub();
    let release!: () => void;
    const connect = vi.fn(() => new Promise<LiveHub>(resolve => { release = () => resolve(fake.hub); }));
    const live = createLiveConnection(connect);
    live.subscribe("board:1", listener()).close();
    const board = listener();
    live.subscribe("board:1", board);
    release();
    await settle();
    expect(connect).toHaveBeenCalledTimes(1);
    expect(board.status).toHaveBeenLastCalledWith("online");
    expect(board.subscribed).toHaveBeenCalledTimes(1);
  });

  it("retries a connection that could not start, while someone still listens", async () => {
    vi.useFakeTimers();
    const fake = fakeHub();
    fake.hub.start.mockRejectedValueOnce(new Error("offline"));
    const live = createLiveConnection(async () => fake.hub, { retryMs: () => 1000 });
    const board = listener();
    live.subscribe("board:1", board);
    await vi.advanceTimersByTimeAsync(0);
    expect(board.status).toHaveBeenLastCalledWith("offline");
    await vi.advanceTimersByTimeAsync(1000);
    expect(fake.hub.start).toHaveBeenCalledTimes(2);
    expect(board.status).toHaveBeenLastCalledWith("online");
    expect(board.subscribed).toHaveBeenCalledTimes(1);
  });
});

// UX1-1: a board stuck „Offline, připojujeme se“ for good, because the hub refused an ended session as if the network were lost.
describe("live connection of an ended session", () => {
  afterEach(() => { onSessionEnded(undefined); vi.unstubAllGlobals(); });

  it("ends the session when negotiate answers 401", async () => {
    const ended = vi.fn();
    onSessionEnded(ended);
    const fetch = vi.fn();
    vi.stubGlobal("fetch", fetch);
    const fake = fakeHub();
    fake.hub.start.mockRejectedValueOnce(Object.assign(new Error("Unauthorized"), { statusCode: 401 }));
    const live = createLiveConnection(async () => fake.hub, { retryMs: () => 60_000 });
    live.subscribe("board:1", listener());
    await settle();
    expect(ended).toHaveBeenCalledTimes(1);
    expect(fetch).not.toHaveBeenCalled();
  });

  it("asks /auth/me when the hub drops the connection and ends the session only on its 401", async () => {
    const ended = vi.fn();
    onSessionEnded(ended);
    const fetch = vi.fn(async () => new Response(null, { status: 401 }));
    vi.stubGlobal("fetch", fetch);
    const fake = fakeHub();
    const live = createLiveConnection(async () => fake.hub, { retryMs: () => 60_000 });
    live.subscribe("board:1", listener());
    await settle();
    // The handshake of an ended session: the hub aborts it right after it started.
    fake.reconnecting();
    await settle();
    expect(fetch).toHaveBeenCalledWith("/api/auth/me", expect.objectContaining({ credentials: "same-origin" }));
    expect(ended).toHaveBeenCalledTimes(1);
  });

  it("asks /auth/me at most once per gap on a flapping network, shares a check in flight, and ends a 401 at once (PERF3-3)", async () => {
    vi.useFakeTimers();
    const ended = vi.fn();
    onSessionEnded(ended);
    let status = 200;
    const fetch = vi.fn(async () => new Response(null, { status }));
    vi.stubGlobal("fetch", fetch);
    const first = checkSessionAfterLiveFailure(new Error("gone"));
    expect(checkSessionAfterLiveFailure(new Error("gone"))).toBe(first);
    await first;
    for (let attempt = 0; attempt < 5; attempt++) {
      await vi.advanceTimersByTimeAsync(1000);
      await checkSessionAfterLiveFailure(new Error("gone"));
    }
    expect(fetch).toHaveBeenCalledTimes(1);
    // A negotiate refused as signed out needs no check, whatever the gap.
    await checkSessionAfterLiveFailure(Object.assign(new Error("Unauthorized"), { statusCode: 401 }));
    expect(ended).toHaveBeenCalledTimes(1);
    // The session ended meanwhile: the first check after the gap says so.
    status = 401;
    await vi.advanceTimersByTimeAsync(liveProbeGapMs);
    await checkSessionAfterLiveFailure(new Error("gone"));
    expect(fetch).toHaveBeenCalledTimes(2);
    expect(ended).toHaveBeenCalledTimes(2);
  });

  it("keeps a lost network offline and retries without ending the session", async () => {
    vi.useFakeTimers();
    const ended = vi.fn();
    onSessionEnded(ended);
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    const fake = fakeHub();
    fake.hub.start.mockRejectedValueOnce(new Error("Failed to complete negotiation with the server: TypeError: Failed to fetch"));
    const live = createLiveConnection(async () => fake.hub, { retryMs: () => 1000 });
    const board = listener();
    live.subscribe("board:1", board);
    await vi.advanceTimersByTimeAsync(0);
    expect(board.status).toHaveBeenLastCalledWith("offline");
    await vi.advanceTimersByTimeAsync(1000);
    expect(fake.hub.start).toHaveBeenCalledTimes(2);
    expect(board.status).toHaveBeenLastCalledWith("online");
    expect(ended).not.toHaveBeenCalled();
  });
});
