// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { createLiveConnection, type LiveHub } from "./liveConnection";
import { createViewSessions, type ObjectListener } from "./viewSession";

const board = "0a1b2c3d-0000-0000-0000-0000000000b1";
const page = "0a1b2c3d-0000-0000-0000-0000000000a1";
const other = "0a1b2c3d-0000-0000-0000-0000000000c1";
const boardKey = `board:${board}`;

/** The hub in memory: answers JoinView with `members`, records what the client invoked and sent, emits server messages. */
function fakeHub(members: () => { objectId: string; typeId: string; version: number }[]) {
  const handlers = new Map<string, (payload: unknown) => void>();
  let reconnected = (_id?: string) => {};
  let reconnecting = (_error?: Error) => {};
  const hub = {
    on: vi.fn((event: string, handler: (payload: unknown) => void) => { handlers.set(event, handler); }),
    onreconnecting: (callback: typeof reconnecting) => { reconnecting = callback; },
    onreconnected: (callback: typeof reconnected) => { reconnected = callback; },
    onclose: () => {},
    start: vi.fn(async () => {}),
    stop: vi.fn(async () => {}),
    invoke: vi.fn(async (method: string, ..._args: unknown[]): Promise<unknown> => {
      if (method === "JoinView") return { members: members() };
      return undefined;
    }),
    send: vi.fn(async (_method: string, ..._args: unknown[]) => {}),
  };
  return {
    hub: hub as unknown as LiveHub,
    emit: (event: string, payload: unknown) => handlers.get(event)?.(payload),
    reconnect: () => { reconnecting(); reconnected("next"); },
    invoked: (method: string) => hub.invoke.mock.calls.filter(call => call[0] === method).map(call => call.slice(1)),
    sent: (method: string) => hub.send.mock.calls.filter(call => call[0] === method).map(call => call.slice(1)),
  };
}
type Of<K extends keyof ObjectListener> = NonNullable<ObjectListener[K]>;
function watcher() {
  return { changed: vi.fn<Of<"changed">>(), missed: vi.fn<Of<"missed">>(), presence: vi.fn<Of<"presence">>(), revoked: vi.fn<Of<"revoked">>() };
}
function setup(versions: Record<string, number> = { [board]: 10, [page]: 3 }) {
  const fake = fakeHub(() => Object.entries(versions).map(([objectId, version]) => ({ objectId, typeId: objectId === board ? "atlas.board" : "atlas.page", version })));
  const live = createLiveConnection(async () => fake.hub);
  const views = createViewSessions(live, { viewportIntervalMs: 250, presenceIntervalMs: 50 });
  return { fake, views, versions };
}
const settle = () => vi.advanceTimersByTimeAsync(0);

afterEach(() => { vi.useRealTimers(); });

describe("view sessions", () => {
  it("joins a view once for all who open it and leaves it with the last of them", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const first = views.open(boardKey);
    const second = views.open(boardKey.toUpperCase().replace("BOARD", "board"));
    await settle();
    expect(fake.invoked("JoinView")).toEqual([[boardKey]]);
    first.close();
    await settle();
    expect(fake.invoked("LeaveView")).toEqual([]);
    second.close();
    await settle();
    expect(fake.invoked("LeaveView")).toEqual([[boardKey]]);
  });

  it("routes a change and a presence to the listeners of their object only, until they stop listening", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(boardKey);
    const atBoard = watcher(), atPage = watcher();
    view.watch(board, atBoard);
    const stop = view.watch(page.toUpperCase(), atPage);
    await settle();
    fake.emit("ObjectChanged", { objectId: board, version: 11, change: { sequence: 11 } });
    fake.emit("ObjectChanged", { objectId: page, version: 4, change: null });
    const jana = { objectId: page, userId: "u2", displayName: "Jana", color: "#2a9d8f", payload: null, via: `page:${page}` };
    fake.emit("ObjectPresence", jana);
    expect(atBoard.changed).toHaveBeenCalledExactlyOnceWith({ sequence: 11 }, 11);
    expect(atPage.changed).toHaveBeenCalledExactlyOnceWith(null, 4);
    expect(atPage.presence).toHaveBeenCalledExactlyOnceWith(jana);
    expect(atBoard.presence).not.toHaveBeenCalled();
    stop();
    fake.emit("ObjectChanged", { objectId: page, version: 5, change: null });
    expect(atPage.changed).toHaveBeenCalledTimes(1);
    view.close();
    fake.emit("ObjectChanged", { objectId: board, version: 12, change: null });
    expect(atBoard.changed).toHaveBeenCalledTimes(1);
  });

  it("joins again after a reconnect and sends each object that moved on meanwhile to its own catch-up", async () => {
    vi.useFakeTimers();
    const { fake, views, versions } = setup();
    const view = views.open(boardKey);
    const atBoard = watcher(), atPage = watcher();
    // The board was loaded at 9 before the view was joined: the join answers 10, one change behind.
    view.watch(board, atBoard, 9);
    view.watch(page, atPage);
    await settle();
    expect(atBoard.missed).toHaveBeenCalledExactlyOnceWith(10);
    expect(atPage.missed).not.toHaveBeenCalled();
    fake.emit("ObjectChanged", { objectId: board, version: 11, change: { sequence: 11 } });
    versions[board] = 11;
    versions[page] = 5;
    fake.reconnect();
    await settle();
    expect(fake.invoked("JoinView")).toEqual([[boardKey], [boardKey]]);
    expect(atBoard.missed).toHaveBeenCalledTimes(1);
    expect(atPage.missed).toHaveBeenCalledExactlyOnceWith(5);
  });

  it("tells a listener that starts after the join whether its object is already further", async () => {
    vi.useFakeTimers();
    const { views } = setup();
    const view = views.open(boardKey);
    await settle();
    const behind = watcher(), current = watcher();
    view.watch(board, behind, 8);
    view.watch(board, current, 10);
    expect(behind.missed).toHaveBeenCalledExactlyOnceWith(10);
    expect(current.missed).not.toHaveBeenCalled();
  });

  it("checks the version of an object the view gains", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup({ [board]: 10 });
    const view = views.open(boardKey);
    const atOther = watcher();
    view.watch(other, atOther, 1);
    await settle();
    fake.emit("ViewMembers", { viewKey: boardKey, added: [{ objectId: other, typeId: "atlas.page", version: 4 }], removed: [] });
    expect(atOther.missed).toHaveBeenCalledExactlyOnceWith(4);
  });

  it("tells a watcher that knew no version when the view gains its object (a restored source), but not at the join", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(boardKey);
    const atPage = watcher(), atOther = watcher();
    view.watch(page, atPage);
    view.watch(other, atOther);
    await settle();
    expect(atPage.missed).not.toHaveBeenCalled();
    fake.emit("ObjectRevoked", { objectId: other });
    fake.emit("ViewMembers", { viewKey: boardKey, added: [{ objectId: other, typeId: "atlas.contact", version: 2 }], removed: [] });
    expect(atOther.revoked).toHaveBeenCalledOnce();
    expect(atOther.missed).toHaveBeenCalledExactlyOnceWith(2);
  });

  it("reports the visible objects at most every interval, the view itself always among them, and again after a rejoin", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(boardKey);
    view.viewport([page]);
    expect(fake.invoked("SetViewport")).toEqual([]);
    await settle();
    expect(fake.invoked("SetViewport")).toEqual([[boardKey, [board, page]]]);
    view.viewport([page, other]);
    view.viewport([other]);
    expect(fake.invoked("SetViewport")).toHaveLength(1);
    await vi.advanceTimersByTimeAsync(250);
    expect(fake.invoked("SetViewport")).toEqual([[boardKey, [board, page]], [boardKey, [board, other]]]);
    await vi.advanceTimersByTimeAsync(250);
    view.viewport([other]);
    await vi.advanceTimersByTimeAsync(250);
    expect(fake.invoked("SetViewport")).toHaveLength(2);
    fake.reconnect();
    await settle();
    expect(fake.invoked("SetViewport")).toHaveLength(3);
    expect(fake.invoked("SetViewport")[2]).toEqual([boardKey, [board, other]]);
  });

  it("names at most 2000 objects in a viewport", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(boardKey);
    await settle();
    view.viewport(Array.from({ length: 2500 }, (_, index) => `id-${index}`));
    const [[, ids]] = fake.invoked("SetViewport") as [[string, string[]]];
    expect(ids).toHaveLength(2000);
    expect(ids[0]).toBe(board);
  });

  it("sends presence at an object at most every interval, always the latest", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(boardKey);
    await settle();
    view.presence(board, { n: 1 });
    view.presence(board, { n: 2 });
    view.presence(page, { editing: true });
    view.presence(board, { n: 3 });
    expect(fake.sent("SendObjectPresence")).toEqual([[board, { n: 1 }], [page, { editing: true }]]);
    await vi.advanceTimersByTimeAsync(50);
    expect(fake.sent("SendObjectPresence")).toEqual([[board, { n: 1 }], [page, { editing: true }], [board, { n: 3 }]]);
  });

  it("holds presence back until the view is joined, which the server requires", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const view = views.open(`page:${page}`);
    view.presence(page, { editing: true });
    expect(fake.sent("SendObjectPresence")).toEqual([]);
    await settle();
    expect(fake.sent("SendObjectPresence")).toEqual([[page, { editing: true }]]);
  });

  it("passes a revoked object to its listeners and joins a revoked view again, which says whether it is still readable", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const refused = vi.fn();
    const view = views.open(boardKey, { refused });
    const atBoard = watcher(), atPage = watcher();
    view.watch(board, atBoard);
    view.watch(page, atPage);
    await settle();
    fake.emit("ObjectRevoked", { objectId: page });
    expect(atPage.revoked).toHaveBeenCalledTimes(1);
    expect(fake.invoked("JoinView")).toHaveLength(1);
    (fake.hub.invoke as ReturnType<typeof vi.fn>).mockImplementationOnce(async () => { throw new Error("HubException: forbidden"); });
    fake.emit("ObjectRevoked", { objectId: board });
    await settle();
    expect(atBoard.revoked).toHaveBeenCalledTimes(1);
    expect(fake.invoked("JoinView")).toHaveLength(2);
    expect(refused).toHaveBeenCalledWith("forbidden");
  });

  it("tells the openers of a view the connection's state", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    const status = vi.fn();
    views.open(boardKey, { status });
    await settle();
    expect(status).toHaveBeenLastCalledWith("online");
    fake.reconnect();
    expect(status).toHaveBeenCalledWith("reconnecting");
  });

  it("tells a view opened on a connection already online that it is online (in-app navigation, UX3-1)", async () => {
    vi.useFakeTimers();
    const fake = fakeHub(() => []);
    const live = createLiveConnection(async () => fake.hub);
    // The account's channel holds the connection open before any board is shown.
    live.subscribe("user:u1", {});
    await settle();
    expect(live.status).toBe("online");
    const views = createViewSessions(live);
    const status = vi.fn();
    views.open(boardKey, { status });
    expect(status).toHaveBeenLastCalledWith("online");
  });

  it("tells a second opener of a joined view that it is online", async () => {
    vi.useFakeTimers();
    const { views } = setup();
    views.open(boardKey);
    await settle();
    const status = vi.fn();
    views.open(boardKey, { status });
    expect(status).toHaveBeenCalledExactlyOnceWith("online");
  });

  it("tells every opener of a refused view the refusal, also one that comes after it, and forgets it on the next join", async () => {
    vi.useFakeTimers();
    const { fake, views } = setup();
    (fake.hub.invoke as ReturnType<typeof vi.fn>).mockImplementationOnce(async () => { throw new Error("HubException: forbidden"); });
    const first = { status: vi.fn(), refused: vi.fn() };
    views.open(boardKey, first);
    await settle();
    expect(first.refused).toHaveBeenCalledExactlyOnceWith("forbidden");
    const second = { status: vi.fn(), refused: vi.fn() };
    views.open(boardKey, second);
    expect(second.status).toHaveBeenLastCalledWith("online");
    expect(second.refused).toHaveBeenCalledExactlyOnceWith("forbidden");
    // A reconnect joins again, now granted: a later opener hears no old refusal.
    fake.reconnect();
    await settle();
    const third = { status: vi.fn(), refused: vi.fn() };
    views.open(boardKey, third);
    expect(third.status).toHaveBeenLastCalledWith("online");
    expect(third.refused).not.toHaveBeenCalled();
  });
});
