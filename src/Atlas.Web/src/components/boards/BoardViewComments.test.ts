import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { CommentApi } from "../../comments/commentApi";
import { useWorkspacePanel } from "../../composables/useWorkspacePanel";
import type { ObjectListener } from "../../live/viewSession";
import BoardView from "./BoardView.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; useWorkspacePanel().reset(); });

/** View sessions in memory with every watcher of an object, as the client keeps them. */
function fakeViews() {
  const watchers = new Map<string, ObjectListener[]>();
  const views = { open: vi.fn(() => ({
    watch: (id: string, listener: ObjectListener) => { watchers.set(id, [...(watchers.get(id) ?? []), listener]); return () => {}; },
    viewport: vi.fn(), presence: vi.fn(), close: vi.fn(),
  })) };
  return { views, watchers };
}
const board = { id: "b1", spaceId: "s1", title: "Plán", sequence: 1, canEdit: true, items: [
  { id: "n", kind: "note" as const, x: 0, y: 0, width: 200, height: 140, z: 1, text: "Ahoj", color: "yellow" as const, version: 1 },
  { id: "p", kind: "page" as const, x: 300, y: 0, width: 280, height: 160, z: 2, version: 1, props: { display: "card" as const },
    target: { type: "atlas.page" as const, resourceId: "page-1", title: "Runbook", available: true } },
] };

it("counts the board's comments for its Diskuse and its cards' objects on their cards, and toggles the board's discussion in the dock", async () => {
  const { views, watchers } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  const comments = { counts: vi.fn(async () => ({ b1: 2, "page-1": 1 })) } as unknown as CommentApi;
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", comments, deps: { api, views, references: async () => [{ id: "page-1", title: "Runbook" }] } } });
  await flushPromises();
  expect(comments.counts).toHaveBeenCalledWith(["b1", "page-1"], expect.any(AbortSignal));
  // The board's Diskuse is the shell's top-bar button (PageDiscussionButton, as a page's), not a button of the board's header.
  expect(wrapper.find("[data-board-discussion]").exists()).toBe(false);
  expect(wrapper.get('[data-board-item="p"] [data-board-card-comments]').text()).toContain("1");
  expect(wrapper.find('[data-board-item="n"] [data-board-card-comments]').exists()).toBe(false);

  const view = wrapper.vm as unknown as { toggleDiscussion(): void; toggleDock(): void; discussionOpen: boolean };
  expect(view.discussionOpen).toBe(false);
  view.toggleDiscussion();
  await flushPromises();
  const panel = useWorkspacePanel();
  expect([panel.open.value, panel.label.value, panel.activeTab.value]).toEqual([true, "Detail tabule", "comments"]);
  expect(panel.contentProps.value).toMatchObject({ boardId: "b1", item: null, tab: "comments", commentCount: 2 });
  expect(view.discussionOpen).toBe(true);
  // The tabs the board shares with a page stand in the page dock's order.
  expect(panel.tabs.value?.map(tab => tab.id).slice(-2)).toEqual(["history", "comments"]);

  // A comment on the board itself, announced by id through the board's view session: the counts are read again.
  vi.mocked(comments.counts).mockResolvedValue({ b1: 3, "page-1": 1 });
  watchers.get("b1")!.forEach(listener => listener.changed?.({ kind: "comments", count: 3, latestId: "c" }, 1));
  await flushPromises();
  expect(panel.contentProps.value).toMatchObject({ commentCount: 3 });
  // ...and is no change of the board: nothing is caught up.
  expect(api.after).not.toHaveBeenCalled();
  // The button (and Alt C) closes it again; the dock toggle opens the board's detail.
  view.toggleDiscussion();
  expect(panel.open.value).toBe(false);
  view.toggleDock();
  expect([panel.open.value, panel.activeTab.value, view.discussionOpen]).toEqual([true, "overview", false]);
  view.toggleDock();
  expect(panel.open.value).toBe(false);
});
