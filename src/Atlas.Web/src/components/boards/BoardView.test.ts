import { afterEach, beforeAll, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearEditorUndo, setEditorUndo } from "../../boards/editorUndo";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { nextTick, onBeforeUnmount } from "vue";
import { toast } from "vue-sonner";
import { hasUnsavedWork } from "../../lib/unsavedWork";

// The board waits for the editor's dialect (BoardView `dialectSettled`); loaded cold it outlasted the first tests' flushPromises.
// The undo wedge is a lazy chunk (990238839); cold, its first import outlasted a flushPromises too.
beforeAll(() => Promise.all([loadMarkdownDialect(), import("../PageUndoWedge.vue")]), 60_000);

vi.mock("vue-sonner", () => ({ toast: { success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismiss: vi.fn() } }));
import { ApiError } from "../../api";
import type { ObjectListener, ViewListener } from "../../live/viewSession";

/** View sessions in memory: the opener's listener and each watched object's listener, for the test to drive. */
function fakeViews() {
  const opener: ViewListener = {};
  const objects = new Map<string, ObjectListener>();
  const views = { open: vi.fn((_key: string, listener?: ViewListener) => {
    Object.assign(opener, listener);
    // The first watcher of an object is the board session's (the comment counts watch the board after it).
    return { watch: (id: string, next: ObjectListener) => { if (!objects.has(id)) objects.set(id, next); return () => { if (objects.get(id) === next) objects.delete(id); }; },
      viewport: vi.fn(), presence: vi.fn(), close: vi.fn() };
  }) };
  return { views, opener, objects };
}
import BoardView from "./BoardView.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => {
  // jsdom 31 starts its frame interval on demand (a frame asked for, focus leaving a removed element). Started on the fake clock it
  // never runs again, and requestAnimationFrame stays silent for the rest of the file: one frame lets it finish and stop itself.
  if (vi.isFakeTimers()) vi.advanceTimersByTime(17);
  vi.useRealTimers();
  wrapper?.unmount();
  document.body.innerHTML = "";
});

const board = { id: "b1", spaceId: "s1", title: "Plán", sequence: 1, canEdit: true, items: [{ id: "a", kind: "note" as const, x: 0, y: 0, width: 200, height: 140, z: 1, text: "Ahoj", color: "yellow" as const, version: 1 }] };

it("does not call a board offline when only its live view is refused, and clears that once the view joins again (UX1-1)", async () => {
  const { views, opener } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  opener.status!("online");
  opener.refused!("not_found");
  await flushPromises();
  const badge = () => wrapper!.get("[data-board-status]").text();
  expect(badge()).toBe("Bez živých změn");
  expect(badge()).not.toContain("Offline");
  // A reconnect joins the view again; a refusal would come again after it.
  opener.status!("online");
  await flushPromises();
  expect(badge()).toBe("Uloženo");
});
it("says whether changes are saved, waiting offline or refused, next to the board", async () => {
  const { views, opener } = fakeViews();
  const status = (next: "online" | "offline") => opener.status!(next);
  let answer!: { resolve: (value: unknown) => void; reject: (error: unknown) => void };
  const api = {
    get: vi.fn(async () => board),
    submit: vi.fn(() => new Promise<never>((resolve, reject) => { answer = { resolve: resolve as (value: unknown) => void, reject }; })),
    after: vi.fn(async () => ({ sequence: 1, batches: [] })),
  };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  status("online");
  await flushPromises();
  const badge = () => wrapper!.get("[data-board-status]").text();
  expect((wrapper.get(".board-view-title textarea").element as HTMLTextAreaElement).value).toBe("Plán");
  expect(badge()).toBe("Uloženo");

  await wrapper.get('[data-board-item="a"]').trigger("keydown", { key: "Delete" });
  expect(badge()).toBe("Ukládám…");
  // The item leaves at once in the model; on screen it fades out (spec §5).
  await nextTick();
  expect(wrapper.get('[data-board-item="a"] .board-node').classes()).toContain("is-leaving");

  status("offline");
  await flushPromises();
  expect(badge()).toBe("Offline, uloží se po připojení");

  status("online");
  answer.reject(new ApiError(409, "Conflict", "item_deleted"));
  await flushPromises();
  expect(wrapper.get("[data-conflict-note]").text()).toContain("prvek mezitím někdo smazal");
  expect(api.get).toHaveBeenCalledTimes(2);
  expect(wrapper.find('[data-board-item="a"]').exists()).toBe(true);
  expect(badge()).toBe("Uloženo");
});

// U3-1: a text being typed on the board said „Uloženo“ although a reload would lose it.
it("says „Neuloženo“ while an editor on the board holds typed text not yet saved", async () => {
  const { views, opener } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(async () => { throw new Error("not sent in this test"); }), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  opener.status!("online");
  await flushPromises();
  const canvas = wrapper.findComponent({ name: "BoardCanvas" });
  canvas.vm.$emit("unsaved", true);
  await nextTick();
  expect(wrapper.get("[data-board-status]").text()).toBe("Neuloženo");
  canvas.vm.$emit("unsaved", false);
  await nextTick();
  expect(wrapper.get("[data-board-status]").text()).toBe("Uloženo");
});

it("counts typed text as unsaved work and still saves what an editor hands over as the board is left (UX-B-2)", async () => {
  const { views } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(async () => ({ sequence: 2, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  const canvas = wrapper.findComponent({ name: "BoardCanvas" });
  canvas.vm.$emit("unsaved", true);
  expect(hasUnsavedWork()).toBe(true);
  // A text editor taken away mid-edit hands its text over in its own beforeUnmount, which runs after the view's.
  const moved = { type: "move" as const, itemId: "a", x: 40, y: 0, width: 200, height: 140 };
  onBeforeUnmount(() => canvas.vm.$emit("change", [moved]), canvas.vm.$);
  wrapper.unmount();
  wrapper = undefined;
  await flushPromises();
  expect(api.submit).toHaveBeenCalledWith("b1", expect.any(String), [moved]);
  expect(hasUnsavedWork()).toBe(false);
});

it("sits in the page's frame: reports itself to the shell's bar and is renamed in place as one board change", async () => {
  const { views } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(async () => ({ sequence: 2, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  // No header of its own any more: no way back, the bar has the breadcrumbs; the avatars go to the bar too.
  expect(wrapper.find("[data-board-back]").exists()).toBe(false);
  expect(wrapper.emitted("meta")).toEqual([[{ spaceId: "s1", title: "Plán", canEdit: true }]]);
  expect(wrapper.emitted("peers")?.at(-1)).toEqual([[]]);
  const field = wrapper.get<HTMLTextAreaElement>(".board-view-title textarea");
  expect(field.attributes("aria-label")).toBe("Název tabule");
  await field.trigger("focus");
  await field.setValue("Plán vydání ");
  await field.trigger("keydown", { key: "Enter" });
  await field.trigger("blur");
  await flushPromises();
  expect(api.submit).toHaveBeenCalledTimes(1);
  expect(api.submit.mock.calls[0]).toEqual(["b1", expect.any(String), [{ type: "rename", title: "Plán vydání" }]]);
  expect(wrapper.emitted("meta")?.at(-1)).toEqual([{ spaceId: "s1", title: "Plán vydání", canEdit: true }]);
  // An emptied title goes back to the saved one and sends nothing.
  await field.trigger("focus");
  await field.setValue("  ");
  await field.trigger("blur");
  await flushPromises();
  expect(field.element.value).toBe("Plán vydání");
  expect(api.submit).toHaveBeenCalledTimes(1);
});

it("renames like every rename: Enter saves and gives the keys to the board, a toast's „Zpět“ renames it back, Esc keeps the saved title", async () => {
  const { views } = fakeViews();
  let sequence = 1;
  const api = { get: vi.fn(async () => board), submit: vi.fn(async (..._args: unknown[]) => ({ sequence: ++sequence, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  const field = wrapper.get<HTMLTextAreaElement>(".board-view-title textarea");
  field.element.focus();
  await field.setValue("Plán vydání");
  await field.trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(document.activeElement).toBe(wrapper.get("[data-board-surface]").element);
  expect(api.submit.mock.calls.at(-1)?.[2]).toEqual([{ type: "rename", title: "Plán vydání" }]);
  expect(toast.success).toHaveBeenCalledWith("Tabule přejmenována", expect.objectContaining({ action: expect.objectContaining({ label: "Zpět" }) }));
  const back = (vi.mocked(toast.success).mock.calls.at(-1)![1]!.action as { onClick: (event: MouseEvent) => void }).onClick;
  back(new MouseEvent("click"));
  await flushPromises();
  expect(api.submit.mock.calls.at(-1)?.[2]).toEqual([{ type: "rename", title: "Plán" }]);
  // „Zpět“ is the board's undo of the rename (P6): Ctrl+Y brings it back, and the toast's „Zpět“ then no longer acts.
  wrapper.findComponent({ name: "BoardCanvas" }).vm.$emit("redo");
  await flushPromises();
  expect(api.submit.mock.calls.at(-1)?.[2]).toEqual([{ type: "rename", title: "Plán vydání" }]);
  back(new MouseEvent("click"));
  await flushPromises();
  expect(api.submit).toHaveBeenCalledTimes(3);
  wrapper.findComponent({ name: "BoardCanvas" }).vm.$emit("undo");
  await flushPromises();
  expect(api.submit.mock.calls.at(-1)?.[2]).toEqual([{ type: "rename", title: "Plán" }]);
  // Esc throws the draft away (a single-line field, P3) and also leaves the keys on the board.
  field.element.focus();
  await field.setValue("Jiný");
  await field.trigger("keydown", { key: "Escape" });
  await flushPromises();
  expect(field.element.value).toBe("Plán");
  expect(document.activeElement).toBe(wrapper.get("[data-board-surface]").element);
  expect(api.submit).toHaveBeenCalledTimes(4);
});

it("saves the title on Enter also while the canvas takes no focus (hidden behind a deployment board's list in Chrome)", async () => {
  const { views } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(async (..._args: unknown[]) => ({ sequence: 2, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  // Chrome does not focus an element under display:none; jsdom would.
  vi.spyOn(wrapper.get<HTMLElement>("[data-board-surface]").element, "focus").mockImplementation(() => undefined);
  const field = wrapper.get<HTMLTextAreaElement>(".board-view-title textarea");
  field.element.focus();
  await field.setValue("Plán vydání");
  await field.trigger("keydown", { key: "Enter" });
  await flushPromises();
  expect(document.activeElement).not.toBe(field.element);
  expect(api.submit.mock.calls.at(-1)?.[2]).toEqual([{ type: "rename", title: "Plán vydání" }]);
});

it("shows a board the reader may only look at as read-only", async () => {
  const { views } = fakeViews();
  const api = { get: vi.fn(async () => ({ ...board, canEdit: false })), submit: vi.fn(), after: vi.fn() };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  expect(wrapper.get("[data-board-status]").text()).toBe("Jen ke čtení");
  // Its title is a heading, not a field.
  expect(wrapper.get("h1.board-view-title").text()).toBe("Plán");
  expect(wrapper.find('[data-board-tool="note"]').exists()).toBe(false);
});

// The states the session reaches when saving cannot go on: a server that keeps failing, a session that ended, access that went away.
async function stuckBoard(submit: (...args: unknown[]) => Promise<{ sequence: number; duplicate: boolean }>) {
  const { views, opener, objects } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(submit), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [], retryMs: () => 1000 } } });
  await letTimePass(0);
  opener.status!("online");
  await nextTick();
  return { api, objects };
}
const letTimePass = async (ms: number) => { await vi.advanceTimersByTimeAsync(ms); await nextTick(); };

it("stops after a batch the server keeps failing on and offers to send again, or to load the board and drop the batches that wait", async () => {
  vi.useFakeTimers();
  const { api } = await stuckBoard(async () => { throw new ApiError(500, "Chyba serveru"); });
  await wrapper!.get('[data-board-item="a"]').trigger("keydown", { key: "Delete" });
  await letTimePass(60_000);
  expect(api.submit).toHaveBeenCalledTimes(6);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Chyba");
  expect(wrapper!.get("[data-conflict-note]").text()).toContain("Čekající změny: 1");
  // Sending again is the main choice (the filled button); dropping the changes is the other one, with what it drops.
  expect(wrapper!.get("[data-board-send-again]").text()).toBe("Zkusit znovu");
  expect(wrapper!.get("[data-board-send-again]").classes()).toContain("bg-primary");
  expect(wrapper!.get("[data-board-discard]").text()).toBe("Načíst tabuli znovu a zahodit změny (1)");
  expect(wrapper!.get("[data-board-discard]").classes()).not.toContain("bg-primary");
  await wrapper!.get("[data-board-discard]").trigger("click");
  await letTimePass(0);
  expect(api.get).toHaveBeenCalledTimes(2);
  expect(wrapper!.find("[data-conflict-note]").exists()).toBe(false);
  expect(wrapper!.find('[data-board-item="a"]').exists()).toBe(true);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Uloženo");
});

it("sends the waiting changes again with the same operation id and clears the note once the server answers", async () => {
  vi.useFakeTimers();
  const { api } = await stuckBoard(async () => { throw new ApiError(500, "Chyba serveru"); });
  await wrapper!.get('[data-board-item="a"]').trigger("keydown", { key: "Delete" });
  await letTimePass(60_000);
  expect(wrapper!.find("[data-board-send-again]").exists()).toBe(true);
  api.submit.mockImplementation(async () => ({ sequence: 2, duplicate: false }));
  await wrapper!.get("[data-board-send-again]").trigger("click");
  await letTimePass(0);
  expect(api.submit).toHaveBeenCalledTimes(7);
  expect(api.submit.mock.calls[6]![1]).toBe(api.submit.mock.calls[0]![1]);
  expect(api.get).toHaveBeenCalledTimes(1);
  expect(wrapper!.find("[data-conflict-note]").exists()).toBe(false);
  expect(wrapper!.find("[data-board-send-again]").exists()).toBe(false);
  expect(wrapper!.find('[data-board-item="a"]').exists()).toBe(false);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Uloženo");
});

it("asks the shell to check the session when a save answers 401, keeps the change and tries again on request", async () => {
  vi.useFakeTimers();
  const { api } = await stuckBoard(async () => { throw new ApiError(401, "Nepřihlášeno"); });
  await wrapper!.get('[data-board-item="a"]').trigger("keydown", { key: "Delete" });
  await letTimePass(0);
  expect(wrapper!.emitted("sessionExpired")).toHaveLength(1);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Přihlášení skončilo");
  expect(wrapper!.get("[data-conflict-note]").text()).toContain("Čekající změny: 1");
  api.submit.mockImplementation(async () => ({ sequence: 2, duplicate: false }));
  await wrapper!.get("[data-conflict-reload]").trigger("click");
  await letTimePass(0);
  expect(wrapper!.find("[data-conflict-note]").exists()).toBe(false);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Uloženo");
});

it("says access is gone only when the board cannot be read any more, and how many changes were not saved", async () => {
  vi.useFakeTimers();
  const { api, objects } = await stuckBoard(async () => { throw new TypeError("Failed to fetch"); });
  await wrapper!.get('[data-board-item="a"]').trigger("keydown", { key: "Delete" });
  await letTimePass(0);
  api.get.mockRejectedValueOnce(new ApiError(404, "Nenalezeno"));
  objects.get("b1")!.revoked!();
  await letTimePass(0);
  expect(wrapper!.get("[data-board-status]").text()).toBe("Přístup odebrán");
  expect(wrapper!.get("[data-conflict-note]").text()).toContain("Čekající změny: 1");
  expect(wrapper!.find('[data-board-tool="note"]').exists()).toBe(false);
});

it("shows on a page card who edits its page elsewhere, with the server's colour and through which view", async () => {
  const card = { id: "c", kind: "page" as const, x: 0, y: 0, width: 240, height: 140, z: 1, version: 1, target: { type: "atlas.page" as const, resourceId: "p1", title: "Provoz", available: true } };
  const api = { get: vi.fn(async () => ({ ...board, items: [card] })), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  const { views, objects } = fakeViews();
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  objects.get("p1")!.presence!({ objectId: "p1", userId: "u2", displayName: "Jana Nováková", color: "#2a9d8f", payload: { editing: true }, via: "page:p1" });
  // What colleagues do reaches the view once per frame (COL2-1).
  await new Promise(requestAnimationFrame);
  await nextTick();
  const avatar = wrapper.get('[data-board-item="c"] [aria-label="Kdo objekt upravuje"] .collaborator-avatar');
  expect(avatar.attributes("aria-label")).toBe("Jana Nováková upravuje přes stránku Provoz");
  expect(avatar.attributes("style")).toContain("#2a9d8f");
  objects.get("p1")!.presence!({ objectId: "p1", userId: "u2", displayName: "Jana Nováková", color: "#2a9d8f", payload: { editing: false }, via: "page:p1" });
  await nextTick();
  await new Promise(resolve => setTimeout(resolve, 400));
  expect(wrapper.find('[data-board-item="c"] .collaborator-avatar').exists()).toBe(false);
});

it("tells the shell's bar of colleagues only when who is here changes, not on every move of their pointer (COL2-1)", async () => {
  const api = { get: vi.fn(async () => board), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  const { views, objects } = fakeViews();
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises();
  const point = async (x: number, color = "#2a9d8f") => {
    objects.get("b1")!.presence!({ objectId: "b1", userId: "u2", displayName: "Jana", color, payload: { cursor: { x, y: 0 } }, via: "board:b1" });
    await new Promise(requestAnimationFrame);
    await nextTick();
  };
  await point(10);
  const told = () => (wrapper!.emitted("peers") ?? []).length;
  const after = told();
  expect(wrapper.emitted("peers")!.at(-1)![0]).toMatchObject([{ id: "u2", name: "Jana", cursorAvailable: true }]);
  await point(40);
  await point(80);
  expect(told()).toBe(after);
  await point(90, "#e85d75");
  expect(told()).toBe(after + 1);
});
// Contract K13: the page's lock on a board. Locked, the canvas reads as for a reader, until E or the toggle opens it again.
it("opens locked when asked, as a page does, and E or the lock toggle switches the board's editing", async () => {
  const { views, opener } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", openLocked: true, deps: { api, views, references: async () => [] } } });
  await flushPromises();
  opener.status!("online");
  await flushPromises();
  const canvas = () => wrapper!.findComponent({ name: "BoardCanvas" });
  expect(canvas().props("canEdit")).toBe(false);
  // The switch itself stands in the shell's top bar (App.vue, F21); the board exposes its state and toggle to it.
  const lock = wrapper.vm as unknown as { locked: boolean; toggleLock: () => void };
  expect(lock.locked).toBe(true);
  await wrapper.get('[data-board-item="a"]').trigger("keydown", { key: "e" });
  expect(canvas().props("canEdit")).toBe(true);
  lock.toggleLock(); await flushPromises();
  expect(canvas().props("canEdit")).toBe(false);
});

// The board's „Zpět“ wedge: only for a writer on an unlocked board with a step to take back (never a reader, never the lock).
it("shows the undo wedge only to a writer on an unlocked board with something to undo", async () => {
  const { views, opener } = fakeViews();
  const mountBoard = async (canEdit: boolean, openLocked: boolean) => {
    const api = { get: vi.fn(async () => ({ ...board, canEdit })), submit: vi.fn(async () => ({ sequence: 2, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
    wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", openLocked, deps: { api, views, references: async () => [] } } });
    await flushPromises(); opener.status!("online"); await flushPromises();
    wrapper.findComponent({ name: "BoardCanvas" }).vm.$emit("change", [{ type: "move", itemId: "a", x: 40, y: 0, width: 200, height: 140 }]);
    await flushPromises();
    const wedge = wrapper.find("[data-page-undo]");
    return wedge.exists() ? wedge.attributes("data-state") : undefined;
  };
  expect(await mountBoard(true, false)).toBe("faint");
  wrapper!.unmount();
  // A reader gets no wedge at all (2c7eaa4fc).
  expect(await mountBoard(false, false)).toBeUndefined();
  wrapper!.unmount();
  expect(await mountBoard(true, true)).toBe("hidden");
});

it("lets the undo wedge follow the editor being edited, and the board again once it is left", async () => {
  const { views, opener } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(async () => ({ sequence: 2, duplicate: false })), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", deps: { api, views, references: async () => [] } } });
  await flushPromises(); opener.status!("online"); await flushPromises();
  const undone = vi.fn(), owner = {};
  setEditorUndo(owner, true, undone);
  await flushPromises();
  expect(wrapper.get("[data-page-undo]").attributes("data-state")).toBe("faint");
  await wrapper.get("[data-page-undo] button").trigger("click");
  expect(undone).toHaveBeenCalledTimes(1);
  setEditorUndo(owner, false, undone);
  await flushPromises();
  expect(wrapper.get("[data-page-undo]").attributes("data-state")).toBe("hidden");
  clearEditorUndo(owner);
});
