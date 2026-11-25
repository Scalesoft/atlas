import { afterEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { emptyDocument } from "./document";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

/*
 * One signal for everything a tree row shows (TreeChanged on tree:{spaceId}): the server raises it for every such write, whoever made it
 * („Tabule nasazení“ making the page's own board, a rename, an icon, a reference node), and every loaded tree reads itself again, with no
 * reload and no refresh of its own at the place of the action.
 */
const live = vi.hoisted(() => ({ handlers: new Map<string, (payload: unknown) => void>(), invoked: [] as unknown[][] }));
vi.mock("./live/liveConnection", async original => {
  const real = await original<typeof import("./live/liveConnection")>();
  const hub = {
    on: (event: string, handler: (payload: unknown) => void) => { live.handlers.set(event, handler); },
    onreconnecting: () => {}, onreconnected: () => {}, onclose: () => {},
    start: async () => {}, stop: async () => {},
    invoke: async (...args: unknown[]) => { live.invoked.push(args); return { sequence: 0 }; },
    send: async () => {},
  };
  const shared = real.createLiveConnection(async () => hub as unknown as import("./live/liveConnection").LiveHub, { failed: () => {} });
  return { ...real, liveConnection: () => shared };
});
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus"],
  setup(_props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: "{}", markdown: "" }), navigateToHeading: vi.fn(), selectionActions: () => [] });
    onMounted(() => emit("ready"));
    return () => h("div", { class: "edit-content" });
  },
}) }));

const item = { id: "page-1", parentId: null, title: "NPK — zakázka 2026", version: 2, sortOrder: 1024, isPublished: true, canEdit: true, type: "page" };
let attached: string | undefined;
let treeReads = 0;
const site: Route = path => {
  if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "light" } });
  if (path === "/api/spaces") return response([{ id: "space-1", name: "Zakázky", role: "Writer", icon: "book", color: "blue" }]);
  if (path === "/api/spaces/space-1/tree") { treeReads++; return response({ spaceId: "space-1", version: attached ? 5 : 4, items: [{ ...item, ...(attached ? { attachedBoardId: attached } : {}) }] }); }
  if (path === "/api/pages/page-1") return response({ ...item, spaceId: "space-1", publishedVersion: 2, contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z" });
  if (path === "/api/ai/capabilities") return response({});
  if (path.startsWith("/api/search?")) return response({ items: [], spaces: [], hasMore: false });
};

let wrapper: VueWrapper | undefined;
const settle = async () => { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); };
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); attached = undefined; treeReads = 0; live.invoked.length = 0; });

describe("App: a loaded tree follows its space", () => {
  it("shows the page's new board mark when the server says the tree changed, and only for that space", async () => {
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(site, sessionRoutes, quietRoutes) });
    await settle();
    expect(live.invoked).toContainEqual(["Subscribe", "tree:space-1"]);
    expect(document.querySelector("[data-tree-attached]")).toBeNull();
    const reads = treeReads;

    // Another space's change is not this tree's.
    live.handlers.get("TreeChanged")!({ spaceId: "space-2" });
    await settle();
    expect(treeReads).toBe(reads);

    // „Tabule nasazení“ made the page's own board (here or in another tab): the server's TreeChanged brings the mark.
    attached = "board-1";
    live.handlers.get("TreeChanged")!({ spaceId: "space-1" });
    await settle();
    expect(treeReads).toBe(reads + 1);
    expect(document.querySelector("[data-tree-attached]")).not.toBeNull();
  });
});
