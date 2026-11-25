import { afterEach, describe, expect, it, vi, type Mock } from "vitest";
import { ApiError } from "../api";
import { createLiveConnection, type LiveHub } from "../live/liveConnection";
import { createViewSessions, type ObjectListener, type ViewListener } from "../live/viewSession";
import type { BoardChange, BoardDto } from "./boardApi";
import { createBoardSession, type BoardSessionDeps } from "./boardSession";

const note = (id: string, x = 0) => ({ id, kind: "note" as const, x, y: 0, width: 200, height: 120, z: 1, text: "", color: "yellow" as const, version: 1 });
function dto(extra: Partial<BoardDto> = {}): BoardDto {
  return { id: "b1", spaceId: "s1", title: "Plán", sequence: 10, canEdit: true, items: [note("a")], ...extra };
}
const move = (itemId: string, x: number): BoardChange => ({ type: "move", itemId, x, y: 0, width: 200, height: 120 });

type AnyMock = Mock<(...args: any[]) => any>;
const mock = (implementation: (...args: any[]) => any): AnyMock => vi.fn(implementation);

function harness(overrides: Partial<Record<"get" | "submit" | "after", AnyMock>> = {}, deps: Partial<BoardSessionDeps> = {}, boardId = "b1") {
  const watchers = new Map<string, ObjectListener>();
  let opener: ViewListener | undefined;
  const view = {
    watch: vi.fn((objectId: string, listener: ObjectListener) => { watchers.set(objectId, listener); return vi.fn(() => watchers.delete(objectId)); }),
    viewport: vi.fn(), presence: vi.fn(), close: vi.fn(),
  };
  const views = { open: vi.fn((_key: string, listener?: ViewListener) => { opener = listener; return view; }) };
  const api = {
    get: mock(async () => dto()),
    submit: mock(async () => ({ sequence: 11, duplicate: false })),
    after: mock(async () => ({ sequence: 10, batches: [] })),
    ...overrides,
  };
  const changed = vi.fn();
  const session = createBoardSession(boardId, changed, { api, views, references: async () => [], retryMs: () => 1000, ...deps });
  const board = () => watchers.get(boardId)!;
  return {
    session, api, views, view, changed, board, opener: () => opener!, watchers,
    /** A batch of the board as its view session delivers it. */
    hear: (batch: { sequence: number; [field: string]: unknown }) => board().changed!(batch, batch.sequence),
    /** A colleague's cursor or drag preview at the board object. */
    cursor: (presence: { userId: string; displayName: string; payload: Record<string, unknown> }) =>
      board().presence!({ objectId: boardId, color: "#2a9d8f", via: `board:${boardId}`, ...presence }),
  };
}
const settle = () => vi.advanceTimersByTimeAsync(0);

afterEach(() => { vi.useRealTimers(); });

describe("board session", () => {
  it("loads the board, joins its live channel and catches up when the server is already further", async () => {
    vi.useFakeTimers();
    const { session, api, views, board } = harness({
      after: mock(async () => ({ sequence: 11, batches: [{ sequence: 11, operationId: "x", changes: [move("a", 40)] }] })),
    });
    await settle();
    expect(session.state).toMatchObject({ status: "ready", title: "Plán", canEdit: true, spaceId: "s1" });
    expect(views.open).toHaveBeenCalledWith("board:b1", expect.anything());
    board().missed!(11);
    await settle();
    expect(api.after).toHaveBeenCalledWith("b1", 10);
    expect(session.state.items[0]).toMatchObject({ id: "a", x: 40 });
  });

  it("sends one batch at a time, in order, and confirms each with its answer", async () => {
    vi.useFakeTimers();
    let answer!: (value: { sequence: number; duplicate: boolean }) => void;
    const { session, api } = harness({ submit: mock(() => new Promise(resolve => { answer = resolve; })) });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    await settle();
    expect(api.submit).toHaveBeenCalledTimes(1);
    expect(session.state).toMatchObject({ pending: 2, saving: true });
    expect(session.state.items[0]!.x).toBe(20);
    answer({ sequence: 11, duplicate: false });
    await settle();
    expect(api.submit).toHaveBeenCalledTimes(2);
    expect(api.submit.mock.calls[1]![2]).toEqual([move("a", 20)]);
    answer({ sequence: 12, duplicate: false });
    await settle();
    expect(session.state).toMatchObject({ pending: 0, saving: false });
  });

  it("undoes a contact card's field edit through the object path, in the board's one undo stack, sending the server nothing (P6)", async () => {
    vi.useFakeTimers();
    let data: Record<string, unknown> = { name: "Jana Novák" };
    const objects = { get: vi.fn(() => ({ id: "k1", data })), apply: vi.fn(), refresh: vi.fn(async () => {}), revoke: vi.fn(),
      edit: vi.fn(async (_id: string, fields: Record<string, unknown>) => { data = { ...data, ...fields }; }) };
    const { session, api, changed } = harness({}, { objects: objects as unknown as BoardSessionDeps["objects"] });
    await settle();
    changed.mockClear();
    session.apply([{ type: "object", itemId: "a", objectId: "k1", before: { name: "Jana" }, after: { name: "Jana Novák" } }], "a#1");
    await settle();
    expect(changed).toHaveBeenCalled();
    expect(session.state.canUndo).toBe(true);
    expect(api.submit).not.toHaveBeenCalled();
    session.undo();
    await settle();
    expect(objects.edit).toHaveBeenCalledWith("k1", { name: "Jana" });
    expect(session.state).toMatchObject({ canUndo: false, canRedo: true });
    session.redo();
    expect(objects.edit).toHaveBeenLastCalledWith("k1", { name: "Jana Novák" });
    expect(api.submit).not.toHaveBeenCalled();
    // A refused undo of the edit is said, as a refused batch is, not swallowed.
    objects.edit.mockRejectedValueOnce(new Error("Kontakt už nesmíte měnit."));
    session.undo();
    await settle();
    expect(session.state.rejected).toBe("Kontakt už nesmíte měnit.");
  });

  it("sends what waits behind a batch in flight as one batch when the page is hidden, without waiting for the answer (W4-3)", async () => {
    vi.useFakeTimers();
    const { session, api } = harness({ submit: mock(() => new Promise(() => {})) });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    session.apply([move("a", 30)]);
    await settle();
    expect(api.submit).toHaveBeenCalledTimes(1);
    session.flushOnHide();
    await settle();
    expect(api.submit).toHaveBeenCalledTimes(2);
    expect(api.submit.mock.calls[1]![2]).toEqual([move("a", 20), move("a", 30)]);
    // Hidden again with nothing new: nothing more goes; something new goes on its own.
    session.flushOnHide();
    session.apply([move("a", 40)]);
    session.flushOnHide();
    await settle();
    expect(api.submit.mock.calls.slice(2).map(call => call[2])).toEqual([[move("a", 40)]]);
  });

  it("sends what waits when the board is left, then the queue still runs to its end in its order (UX-B-2)", async () => {
    vi.useFakeTimers();
    let answer!: (value: { sequence: number; duplicate: boolean }) => void;
    const submit = mock(() => new Promise(resolve => { answer = resolve; }));
    const { session, api } = harness({ submit });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    session.apply([move("a", 30)]);
    await settle();
    // The early batch reaches the server before the one it depends on and is refused.
    submit.mockImplementationOnce(async () => { throw new ApiError(409, "Conflict", "item_deleted"); });
    session.dispose();
    await settle();
    expect(api.submit.mock.calls.map(call => call[2])).toEqual([[move("a", 10)], [move("a", 20), move("a", 30)]]);
    submit.mockImplementation(async () => ({ sequence: 12, duplicate: false }));
    answer({ sequence: 11, duplicate: false });
    await vi.advanceTimersByTimeAsync(60_000);
    // Sent again in its turn, under the same operation id; then nothing more (no reload of a board that was left).
    expect(api.submit).toHaveBeenCalledTimes(3);
    expect(api.submit.mock.calls[2][1]).toBe(api.submit.mock.calls[1][1]);
    expect(api.submit.mock.calls[2][2]).toEqual([move("a", 20), move("a", 30)]);
    expect(api.get).toHaveBeenCalledTimes(1);
  });

  it("drops a rejected batch, says why and loads the board again", async () => {
    vi.useFakeTimers();
    const { session, api } = harness({ submit: mock(async () => { throw new ApiError(409, "Conflict", "item_deleted"); }) });
    await settle();
    api.get.mockResolvedValueOnce(dto({ sequence: 12, items: [] }));
    session.apply([move("a", 10)]);
    await settle();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(session.state.rejected).toBe("Změnu nešlo uložit: prvek mezitím někdo smazal. Tabule je načtená znovu.");
    expect(session.state).toMatchObject({ pending: 0, items: [] });
    session.dismissRejection();
    expect(session.state.rejected).toBe("");
  });

  it("keeps a batch that could not reach the server and sends it again with the same operation id", async () => {
    vi.useFakeTimers();
    const { session, api } = harness();
    await settle();
    api.submit.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    session.apply([move("a", 10)]);
    await settle();
    expect(session.state).toMatchObject({ pending: 1, offline: true });
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.submit).toHaveBeenCalledTimes(2);
    expect(api.submit.mock.calls[1]![1]).toBe(api.submit.mock.calls[0]![1]);
    expect(session.state).toMatchObject({ pending: 0, offline: false });
  });

  it("fetches the missing batches after a gap and reloads when the server asks for a resync", async () => {
    vi.useFakeTimers();
    const { session, api, hear } = harness({ after: mock(async () => { throw new ApiError(409, "Resync", "resync_required"); }) });
    await settle();
    api.get.mockResolvedValueOnce(dto({ sequence: 900, items: [note("a", 77)] }));
    hear({ boardId: "b1", sequence: 12, operationId: "x", authorId: "u", changes: [move("a", 1)] });
    await settle();
    expect(api.after).toHaveBeenCalledWith("b1", 10);
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(session.state.items[0]!.x).toBe(77);
  });

  it("shows other people's drag previews until they let go, and sends its own only while editing", async () => {
    vi.useFakeTimers();
    const { session, view, cursor } = harness();
    await settle();
    const dragging = [{ itemId: "a", x: 5, y: 6, width: 200, height: 120 }];
    cursor({ userId: "u2", displayName: "Jana", payload: { dragging } });
    expect(session.state.remote).toEqual([{ userId: "u2", displayName: "Jana", color: "#2a9d8f", dragging }]);
    cursor({ userId: "u2", displayName: "Jana", payload: { dragging: [] } });
    expect(session.state.remote).toEqual([]);
    cursor({ userId: "u2", displayName: "Jana", payload: { dragging } });
    await vi.advanceTimersByTimeAsync(5000);
    expect(session.state.remote).toEqual([]);
    session.presence({ dragging });
    expect(view.presence).toHaveBeenCalledWith("b1", { dragging });
  });

  it("shows other people's connectors in progress, only what is well formed, and sends its own in whole pixels (A8)", async () => {
    vi.useFakeTimers();
    const { session, view, cursor } = harness();
    await settle();
    cursor({ userId: "u2", displayName: "Jana", payload: { connecting: { from: "a", side: "nowhere", to: 7, x: 10, y: 20, extra: "x" } } });
    expect(session.state.remote).toEqual([{ userId: "u2", displayName: "Jana", color: "#2a9d8f", dragging: [], connecting: { from: "a", x: 10, y: 20 } }]);
    cursor({ userId: "u2", displayName: "Jana", payload: { connecting: { from: "a", x: Number.NaN, y: 2 } } });
    expect(session.state.remote).toEqual([]);
    session.presence({ dragging: [], connecting: { from: "a", side: "right", to: "b", x: 1.6, y: 2.4 } });
    expect(view.presence).toHaveBeenCalledWith("b1", { dragging: [], connecting: { from: "a", side: "right", to: "b", x: 2, y: 2 } });
  });

  it("shows other people's cursors until they leave the board or fall silent", async () => {
    vi.useFakeTimers();
    const { session, cursor } = harness();
    await settle();
    cursor({ userId: "u2", displayName: "Jana", payload: { cursor: { x: 10, y: 20 } } });
    expect(session.state.remote).toEqual([{ userId: "u2", displayName: "Jana", color: "#2a9d8f", dragging: [], cursor: { x: 10, y: 20 } }]);
    cursor({ userId: "u2", displayName: "Jana", payload: {} });
    expect(session.state.remote).toEqual([]);
    cursor({ userId: "u2", displayName: "Jana", payload: { cursor: { x: 1, y: Number.NaN } } });
    expect(session.state.remote).toEqual([]);
    cursor({ userId: "u2", displayName: "Jana", payload: { cursor: { x: 1, y: 2 } } });
    await vi.advanceTimersByTimeAsync(5000);
    expect(session.state.remote).toEqual([]);
  });

  it("gives a page card everything the server says of its page, and the canvas the board's settings", async () => {
    vi.useFakeTimers();
    const page = { ...note("p"), kind: "page" as const, target: { type: "atlas.page" as const, resourceId: "r1", title: "Příručka", spaceTitle: "Tým", excerpt: "Úvod", available: true } };
    const { session } = harness({ get: mock(async () => dto({ items: [page], settings: { background: "lines" } })) });
    await settle();
    expect(session.state.items.find(item => item.id === "p")!.target).toMatchObject({ title: "Příručka", spaceTitle: "Tým", excerpt: "Úvod", available: true });
    expect(session.state.settings).toEqual({ background: "lines", snapToGrid: false, gridSize: 24 });
  });

  it("turns read-only when access is revoked and the board can no longer be read, keeping the unsaved changes visible", async () => {
    vi.useFakeTimers();
    const { session, api, views, view, board } = harness({ submit: mock(async () => { throw new TypeError("Failed to fetch"); }) });
    await settle();
    session.apply([move("a", 10)]);
    await settle();
    expect(session.state).toMatchObject({ pending: 1, offline: true });
    api.get.mockRejectedValueOnce(new ApiError(404, "Nenalezeno"));
    board().revoked!();
    await settle();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(session.state).toMatchObject({ status: "revoked", canEdit: false, pending: 1 });
    expect(session.state.items[0]).toMatchObject({ id: "a", x: 10 });
    expect(views.open).toHaveBeenCalledTimes(1);
    session.apply([move("a", 20)]);
    session.presence({ dragging: [] });
    await vi.advanceTimersByTimeAsync(5000);
    expect(api.submit).toHaveBeenCalledTimes(1);
    // Only the editor's announcement when it joined: nothing once the board is revoked.
    expect(view.presence.mock.calls).toEqual([["b1", { editing: true }]]);
  });

  it("stays in its view and sends what waits when a revoked board is still readable", async () => {
    vi.useFakeTimers();
    const { session, api, views, view, board } = harness();
    await settle();
    api.submit.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    session.apply([move("a", 10)]);
    await settle();
    expect(session.state).toMatchObject({ pending: 1, offline: true });
    board().revoked!();
    await settle();
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(views.open).toHaveBeenCalledTimes(1);
    expect(view.close).not.toHaveBeenCalled();
    expect(session.state).toMatchObject({ status: "ready", canEdit: true, pending: 0, offline: false });
    expect(api.submit).toHaveBeenCalledTimes(2);
  });

  it("keeps trying to read a revoked board while it cannot be reached", async () => {
    vi.useFakeTimers();
    const { session, api, views, board } = harness();
    await settle();
    api.get.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    board().revoked!();
    await settle();
    expect(session.state.status).toBe("ready");
    expect(views.open).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.get).toHaveBeenCalledTimes(3);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.get).toHaveBeenCalledTimes(3);
    expect(views.open).toHaveBeenCalledTimes(1);
  });

  it("joins the live channel of the lower-case board id, which is how the server names it", async () => {
    vi.useFakeTimers();
    const { views } = harness({}, {}, "0A1B2C3D-0000-0000-0000-00000000000A");
    await settle();
    expect(views.open).toHaveBeenCalledWith("board:0a1b2c3d-0000-0000-0000-00000000000a", expect.anything());
  });

  it("asks for page titles in chunks of 100 and asks a failing chunk again only when something new arrives", async () => {
    vi.useFakeTimers();
    const references = vi.fn(async (ids: string[]) => {
      if (ids.includes("p150")) throw new Error("boom");
      return ids.map(id => ({ id, title: `T ${id}` }));
    });
    const { session, hear } = harness({}, { references });
    await settle();
    const create = (n: number): BoardChange => ({ type: "create", item: { id: `c${n}`, kind: "page", x: 0, y: 0, width: 240, height: 140, z: n + 1, targetResourceId: `p${n}` } });
    hear({ boardId: "b1", sequence: 11, operationId: "x", changes: Array.from({ length: 250 }, (_, n) => create(n)) });
    await settle();
    expect(references.mock.calls.map(([ids]) => ids.length)).toEqual([100, 100, 50]);
    const target = (id: string) => session.state.items.find(item => item.id === id)!.target;
    expect(target("c0")).toEqual({ title: "T p0", available: true });
    expect(target("c249")).toEqual({ title: "T p249", available: true });
    expect(target("c120")).toBeUndefined();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(references).toHaveBeenCalledTimes(3);
    hear({ boardId: "b1", sequence: 12, operationId: "y", changes: [move("c0", 1)] });
    await settle();
    expect(references).toHaveBeenCalledTimes(4);
    expect(references.mock.calls[3]![0]).toHaveLength(100);
    expect(references.mock.calls[3]![0][0]).toBe("p100");
  });

  it("asks for the missing batches again, with the same backoff as a send, when the first try fails", async () => {
    vi.useFakeTimers();
    const { session, api, board } = harness();
    await settle();
    api.after.mockRejectedValueOnce(new TypeError("Failed to fetch")).mockResolvedValueOnce({ sequence: 11, batches: [{ sequence: 11, operationId: "x", changes: [move("a", 40)] }] });
    board().missed!(11);
    await settle();
    expect(api.after).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(999);
    expect(api.after).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.after).toHaveBeenCalledTimes(2);
    expect(session.state.items[0]).toMatchObject({ id: "a", x: 40 });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.after).toHaveBeenCalledTimes(2);
  });

  it("stops asking for batches when the answer is 401", async () => {
    vi.useFakeTimers();
    const { session, api, board } = harness({ after: mock(async () => { throw new ApiError(401, "Nepřihlášeno"); }) });
    await settle();
    board().missed!(11);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.after).toHaveBeenCalledTimes(1);
    expect(session.state.signedOut).toBe(true);
  });

  it("stops after a batch the server keeps failing on, says how many changes wait and reloads the board on request", async () => {
    vi.useFakeTimers();
    const { session, api } = harness({ submit: mock(async () => { throw new ApiError(500, "Chyba serveru"); }) });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(6);
    expect(new Set(api.submit.mock.calls.map(call => call[1])).size).toBe(1);
    expect(session.state).toMatchObject({ blocked: 2, pending: 2, offline: false, saving: false });
    expect(session.state.items[0]!.x).toBe(20);
    api.get.mockResolvedValueOnce(dto({ sequence: 12, items: [note("a", 77)] }));
    session.discardQueued();
    await settle();
    expect(session.state).toMatchObject({ blocked: 0, pending: 0, offline: false, canUndo: false });
    expect(session.state.items[0]!.x).toBe(77);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(6);
  });

  it("sends again from the head batch on request, every pending batch kept, and the queue drains when the server answers", async () => {
    vi.useFakeTimers();
    let sequence = 10;
    const { session, api } = harness({ submit: mock(async () => { throw new ApiError(500, "Chyba serveru"); }) });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(6);
    expect(session.state).toMatchObject({ blocked: 2, pending: 2 });
    const head = api.submit.mock.calls[0]![1];
    api.submit.mockImplementation(async () => ({ sequence: ++sequence, duplicate: false }));
    session.sendAgain();
    await settle();
    expect(session.state).toMatchObject({ blocked: 0, pending: 0, offline: false, saving: false });
    expect(api.get).toHaveBeenCalledTimes(1);
    expect(api.submit.mock.calls[6]![1]).toBe(head);
    expect(api.submit.mock.calls[6]![2]).toEqual([move("a", 10)]);
    expect(api.submit.mock.calls[7]![2]).toEqual([move("a", 20)]);
  });

  it("counts the attempts anew after sending again, so a server that still fails blocks again only after as many", async () => {
    vi.useFakeTimers();
    const { session, api } = harness({ submit: mock(async () => { throw new ApiError(500, "Chyba serveru"); }) });
    await settle();
    session.apply([move("a", 10)]);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(6);
    session.sendAgain();
    await settle();
    expect(session.state).toMatchObject({ blocked: 0, offline: true, pending: 1 });
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(12);
    expect(session.state).toMatchObject({ blocked: 1, pending: 1 });
  });

  it("keeps trying without limit while the server cannot be reached at all", async () => {
    vi.useFakeTimers();
    const { session, api } = harness({ submit: mock(async () => { throw new TypeError("Failed to fetch"); }) });
    await settle();
    session.apply([move("a", 10)]);
    await vi.advanceTimersByTimeAsync(20_000);
    expect(api.submit.mock.calls.length).toBeGreaterThan(15);
    expect(session.state).toMatchObject({ blocked: 0, offline: true, pending: 1 });
  });

  it("treats 401 as signed out: sends nothing more, keeps the changes and sends them after the board is loaded again", async () => {
    vi.useFakeTimers();
    let sequence = 10;
    const { session, api } = harness({ submit: mock(async () => { throw new ApiError(401, "Nepřihlášeno"); }) });
    await settle();
    session.apply([move("a", 10)]);
    session.apply([move("a", 20)]);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.submit).toHaveBeenCalledTimes(1);
    expect(session.state).toMatchObject({ signedOut: true, pending: 2, offline: false, rejected: "" });
    expect(session.state.items[0]!.x).toBe(20);
    api.submit.mockImplementation(async () => ({ sequence: ++sequence, duplicate: false }));
    session.resume();
    await settle();
    expect(session.state).toMatchObject({ signedOut: false, pending: 0 });
    expect(api.submit).toHaveBeenCalledTimes(3);
  });

  it.each([408, 429])("retries a %i like a lost connection, and no sooner than the server asks", async status => {
    vi.useFakeTimers();
    const { session, api } = harness();
    await settle();
    api.submit.mockRejectedValueOnce(new ApiError(status, "Zkuste to později", undefined, undefined, 5000));
    session.apply([move("a", 10)]);
    await settle();
    expect(session.state).toMatchObject({ pending: 1, offline: true, rejected: "", signedOut: false });
    await vi.advanceTimersByTimeAsync(4999);
    expect(api.submit).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1);
    expect(api.submit).toHaveBeenCalledTimes(2);
    expect(session.state).toMatchObject({ pending: 0, offline: false });
  });

  it("names page cards: from the board, from the picker, and by asking the server for the rest", async () => {
    vi.useFakeTimers();
    const references = vi.fn(async (ids: string[]) => ids.filter(id => id === "p2").map(id => ({ id, title: "Provoz" })));
    const page = (id: string, resourceId: string) => ({ id, kind: "page" as const, x: 0, y: 0, width: 240, height: 140, z: 1, version: 1, target: { resourceId, type: "atlas.page" as const, title: resourceId === "p1" ? "Známá" : null, available: resourceId === "p1" } });
    const { session, hear } = harness({ get: mock(async () => dto({ items: [page("c1", "p1")] })) }, { references });
    await settle();
    expect(session.state.items[0]!.target).toEqual({ title: "Známá", available: true });
    session.notePage("p3", "Vybraná");
    hear({ boardId: "b1", sequence: 11, operationId: "x", changes: [
      { type: "create", item: { id: "c2", kind: "page", x: 0, y: 0, width: 240, height: 140, z: 2, targetResourceId: "p2" } },
      { type: "create", item: { id: "c3", kind: "page", x: 0, y: 0, width: 240, height: 140, z: 3, targetResourceId: "p3" } },
      { type: "create", item: { id: "c4", kind: "page", x: 0, y: 0, width: 240, height: 140, z: 4, targetResourceId: "p4" } },
    ] });
    expect(session.state.items.find(item => item.id === "c2")!.target).toBeUndefined();
    await settle();
    expect(references).toHaveBeenCalledWith(["p2", "p4"]);
    expect(session.state.items.map(item => item.target)).toEqual([
      { title: "Známá", available: true }, { title: "Provoz", available: true }, { title: "Vybraná", available: true }, { title: null, available: false },
    ]);
  });

  it("refreshes its cards when their sources are renamed, trashed, restored or purged (COL2-8)", async () => {
    vi.useFakeTimers();
    let readable = new Map([["p1", "Provoz"]]);
    const references = vi.fn(async (ids: string[]) => ids.filter(id => readable.has(id)).map(id => ({ id, title: readable.get(id)! })));
    const objects = { get: vi.fn((id: string) => (id === "k1" ? { id } : undefined)), apply: vi.fn(), refresh: vi.fn(async () => {}), revoke: vi.fn() };
    const page = { id: "c1", kind: "page" as const, x: 0, y: 0, width: 240, height: 140, z: 1, version: 1,
      target: { resourceId: "p1", type: "atlas.page" as const, title: "Provoz", available: true, spaceTitle: "Wiki", excerpt: "Úvod" } };
    const contact = { id: "c2", kind: "object" as const, x: 300, y: 0, width: 280, height: 160, z: 2, version: 1,
      target: { resourceId: "k1", type: "atlas.contact", title: "Jana", available: true }, props: { objectId: "k1", typeId: "atlas.contact" } };
    const { session, watchers } = harness({ get: mock(async () => dto({ items: [page, contact] as BoardDto["items"] })) }, { references, objects: objects as unknown as BoardSessionDeps["objects"] });
    await settle();
    const target = () => session.state.items.find(item => item.id === "c1")!.target;

    // Renamed and published: read again, the space and excerpt kept; a burst of saves is one lookup a second.
    readable = new Map([["p1", "Provoz 2"]]);
    watchers.get("p1")!.changed!({ kind: "published" }, 2);
    watchers.get("p1")!.changed!({ kind: "working" }, 3);
    watchers.get("p1")!.changed!({ kind: "working" }, 4);
    await settle();
    expect(target()).toEqual({ title: "Provoz 2", available: true, spaceTitle: "Wiki", excerpt: "Úvod" });
    expect(references).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(references).toHaveBeenCalledTimes(2);
    // A comment is no change of the card.
    watchers.get("p1")!.changed!({ kind: "comments" }, 5);
    await vi.advanceTimersByTimeAsync(1000);
    expect(references).toHaveBeenCalledTimes(2);

    // Trashed (revoked): unavailable, nothing of it kept; restored (the view gains it again): back with its title.
    readable = new Map();
    watchers.get("p1")!.revoked!();
    await vi.advanceTimersByTimeAsync(1000);
    expect(target()).toEqual({ title: null, available: false });
    readable = new Map([["p1", "Provoz 2"]]);
    watchers.get("p1")!.missed!(6);
    await vi.advanceTimersByTimeAsync(1000);
    expect(target()).toEqual({ title: "Provoz 2", available: true });

    // A contact: its changes go to the tab's live objects, a revoke locks it, a restore reads it again.
    watchers.get("k1")!.changed!({ fields: { name: "Jana Nová" } }, 2);
    expect(objects.apply).toHaveBeenCalledWith({ objectId: "k1", version: 2, change: { fields: { name: "Jana Nová" } } });
    watchers.get("k1")!.revoked!();
    expect(objects.revoke).toHaveBeenCalledWith("k1");
    watchers.get("k1")!.missed!(3);
    expect(objects.refresh).toHaveBeenCalledWith("k1");
    expect(references.mock.calls.flat(2)).not.toContain("k1");
  });

  it("reports a board it may not open", async () => {
    vi.useFakeTimers();
    const { session } = harness({ get: mock(async () => { throw new ApiError(404, "Nenalezeno"); }) });
    await settle();
    expect(session.state.status).toBe("missing");
  });

  it("stops every retry once disposed", async () => {
    vi.useFakeTimers();
    const { session, api, board } = harness();
    await settle();
    const lost = new TypeError("Failed to fetch");
    let failGet!: (error: unknown) => void, failAfter!: (error: unknown) => void;
    api.get.mockImplementationOnce(() => new Promise((_, reject) => { failGet = reject; }));
    api.after.mockImplementation(() => new Promise((_, reject) => { failAfter = reject; }));
    board().revoked!();
    board().missed!(11);
    await settle();
    session.dispose();
    failGet(lost);
    failAfter(lost);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(api.get).toHaveBeenCalledTimes(2);
    expect(api.after).toHaveBeenCalledTimes(1);
  });

  it("shows on a card who edits its page elsewhere, through which view, in the server's colour, until they stop", async () => {
    vi.useFakeTimers();
    const card = (id: string, resourceId: string, title: string | null) => ({ id, kind: "page" as const, x: 0, y: 0, width: 240, height: 140, z: 1, version: 1,
      target: { resourceId, type: "atlas.page" as const, title, available: true } });
    const { session, watchers } = harness({ get: mock(async () => dto({ items: [card("c1", "p1", "Provoz"), card("c2", "p2", null), note("a")] })) });
    await settle();
    expect([...watchers.keys()].sort()).toEqual(["b1", "p1", "p2"]);
    const at = (objectId: string, userId: string, displayName: string, via: string | null, payload: Record<string, unknown> | null = { editing: true }) =>
      watchers.get(objectId)!.presence!({ objectId, userId, displayName, color: "#8d5cf6", payload, via });
    at("p1", "u2", "Jana", "page:p1");
    at("p1", "u3", "Petr", null, null);
    at("p2", "u2", "Jana", "board:b9");
    const editors = (id: string) => session.state.items.find(item => item.id === id)!.editors?.map(peer => [peer.activity, peer.color]);
    expect(editors("c1")).toEqual([["Jana upravuje přes stránku Provoz", "#8d5cf6"], ["Petr upravuje jinde", "#8d5cf6"]]);
    expect(editors("c2")).toEqual([["Jana upravuje přes jinou tabuli", "#8d5cf6"]]);
    expect(editors("a")).toBeUndefined();
    at("p1", "u2", "Jana", "page:p1", { editing: false });
    expect(editors("c1")).toEqual([["Petr upravuje jinde", "#8d5cf6"]]);
    await vi.advanceTimersByTimeAsync(25_000);
    expect(editors("c1")).toBeUndefined();
  });

  it("hands the view the same items through a colleague's drag preview, and only the moved one anew after their batch, once per frame (COL2-1)", async () => {
    vi.useFakeTimers();
    const frames: (() => void)[] = [];
    const card = { id: "c1", kind: "page" as const, x: 0, y: 300, width: 240, height: 140, z: 2, version: 1, target: { resourceId: "p1", type: "atlas.page" as const, title: "Provoz", available: true } };
    const { session, changed, cursor, hear, watchers } = harness({ get: mock(async () => dto({ items: [note("a"), note("b", 400), card] })) }, { frame: run => frames.push(run) });
    await settle();
    const before = session.state.items;
    changed.mockClear();
    // Five drag previews and a cursor within one frame: the items stay the same array, the view hears once.
    for (let x = 10; x <= 50; x += 10) cursor({ userId: "u2", displayName: "Jana", payload: { cursor: { x, y: 0 }, dragging: [{ itemId: "a", x, y: 0, width: 200, height: 120 }] } });
    expect(changed).not.toHaveBeenCalled();
    expect(frames).toHaveLength(1);
    frames.shift()!();
    expect(changed).toHaveBeenCalledTimes(1);
    expect(session.state.items).toBe(before);
    expect(session.state.remote[0]!.dragging[0]!.x).toBe(50);
    // Their batch: a new array, the moved item new, the others (a page card with its target too) the same objects.
    hear({ sequence: 11, operationId: "o1", changes: [move("a", 60)] });
    frames.shift()!();
    const after = session.state.items;
    expect(after).not.toBe(before);
    expect(after.find(item => item.id === "a")).toMatchObject({ x: 60 });
    expect(after.find(item => item.id === "b")).toBe(before.find(item => item.id === "b"));
    expect(after.find(item => item.id === "c1")).toBe(before.find(item => item.id === "c1"));
    // Someone opens the card's page: that card alone is new, with its editor.
    watchers.get("p1")!.presence!({ objectId: "p1", userId: "u3", displayName: "Petr", color: "#8d5cf6", payload: { editing: true }, via: null });
    frames.shift()!();
    expect(session.state.items.find(item => item.id === "c1")!.editors).toHaveLength(1);
    expect(session.state.items.find(item => item.id === "b")).toBe(before.find(item => item.id === "b"));
    // My own gesture is told at once, not a frame later (the canvas lets go of its local geometry in the same tick).
    changed.mockClear();
    session.apply([move("b", 500)]);
    expect(changed).toHaveBeenCalled();
  });

  it("follows the presence of the objects its cards show as cards come and go", async () => {
    vi.useFakeTimers();
    const { session, watchers } = harness();
    await settle();
    session.apply([{ type: "create", item: { id: "c1", kind: "page", x: 0, y: 0, width: 240, height: 140, z: 2, targetResourceId: "P1" } }]);
    expect(watchers.has("p1")).toBe(true);
    session.apply([{ type: "delete", itemId: "c1" }]);
    expect(watchers.has("p1")).toBe(false);
  });

  it("passes the canvas's visible objects to its view, and sends its presence in whole pixels and within the server's size", async () => {
    vi.useFakeTimers();
    const { session, view } = harness();
    await settle();
    session.viewport(["p1"]);
    expect(view.viewport).toHaveBeenCalledWith(["p1"]);
    const dragging = Array.from({ length: 50 }, (_, n) => ({ itemId: crypto.randomUUID(), x: n + 0.123456789, y: -12345.987654, width: 240.5, height: 140.25 }));
    session.presence({ cursor: { x: 1.6, y: 2.4 }, dragging });
    const [objectId, payload] = view.presence.mock.calls.at(-1)!;
    expect(objectId).toBe("b1");
    expect(payload).toMatchObject({ cursor: { x: 2, y: 2 } });
    expect(payload.dragging[0]).toMatchObject({ x: 0, y: -12346, width: 241, height: 140 });
    expect(JSON.stringify(payload).length).toBeLessThanOrEqual(2048);
  });

  it("closes its channel when disposed", async () => {
    vi.useFakeTimers();
    const { session, view } = harness();
    await settle();
    session.dispose();
    expect(view.close).toHaveBeenCalled();
  });
});

describe("board session over the real view sessions (UX3-1)", () => {
  /** A hub that joins every channel; `JoinView` of a refused board throws as the server does. */
  function online(refuse = false) {
    const hub = {
      on: () => {}, onreconnecting: () => {}, onreconnected: () => {}, onclose: () => {},
      start: async () => {}, stop: async () => {}, send: async () => {},
      invoke: async (method: string) => {
        if (method === "JoinView" && refuse) throw new Error("HubException: forbidden");
        return method === "JoinView" ? { members: [] } : { sequence: 0 };
      },
    } as unknown as LiveHub;
    const live = createLiveConnection(async () => hub);
    // The account's channel keeps the tab's connection online before the board is opened, as after in-app navigation.
    live.subscribe("user:u1", {});
    return createViewSessions(live);
  }

  it("shows a board opened by in-app navigation as online, not „Offline, připojujeme se“", async () => {
    vi.useFakeTimers();
    const views = online();
    await settle();
    const { session } = harness({}, { views });
    await settle();
    expect(session.state).toMatchObject({ status: "ready", connection: "online", liveRefused: false });
    // A second board view of the same board (a second tab within the app) is online too.
    const second = harness({}, { views });
    await settle();
    expect(second.session.state.connection).toBe("online");
  });

  it("shows „Bez živých změn“ for a board whose view the server refused, also for a second opener", async () => {
    vi.useFakeTimers();
    const views = online(true);
    await settle();
    const { session } = harness({}, { views });
    await settle();
    expect(session.state).toMatchObject({ connection: "online", liveRefused: true });
    const second = harness({}, { views });
    await settle();
    expect(second.session.state).toMatchObject({ connection: "online", liveRefused: true });
  });
});
