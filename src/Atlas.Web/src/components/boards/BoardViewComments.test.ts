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

it("counts the board's comments on its „Diskuse“ and its cards' objects on their cards, and opens the discussion in the dock", async () => {
  const { views, watchers } = fakeViews();
  const api = { get: vi.fn(async () => board), submit: vi.fn(), after: vi.fn(async () => ({ sequence: 1, batches: [] })) };
  const comments = { counts: vi.fn(async () => ({ b1: 2, "page-1": 1 })) } as unknown as CommentApi;
  wrapper = mount(BoardView, { attachTo: document.body, props: { boardId: "b1", comments, deps: { api, views, references: async () => [{ id: "page-1", title: "Runbook" }] } } });
  await flushPromises();
  expect(comments.counts).toHaveBeenCalledWith(["b1", "page-1"], expect.any(AbortSignal));
  expect(wrapper.get("[data-board-discussion]").text()).toContain("Diskuse2");
  expect(wrapper.get('[data-board-item="p"] [data-board-card-comments]').text()).toContain("1");
  expect(wrapper.find('[data-board-item="n"] [data-board-card-comments]').exists()).toBe(false);

  await wrapper.get("[data-board-discussion]").trigger("click");
  const panel = useWorkspacePanel();
  expect([panel.open.value, panel.label.value, panel.activeTab.value]).toEqual([true, "Detail tabule", "comments"]);
  expect(panel.contentProps.value).toMatchObject({ boardId: "b1", item: null, tab: "comments", commentCount: 2 });

  // A comment on the board itself, announced by id through the board's view session: the counts are read again.
  vi.mocked(comments.counts).mockResolvedValue({ b1: 3, "page-1": 1 });
  watchers.get("b1")!.forEach(listener => listener.changed?.({ kind: "comments", count: 3, latestId: "c" }, 1));
  await flushPromises();
  expect(wrapper.get("[data-board-discussion]").text()).toContain("Diskuse3");
  // ...and is no change of the board: nothing is caught up.
  expect(api.after).not.toHaveBeenCalled();
});
