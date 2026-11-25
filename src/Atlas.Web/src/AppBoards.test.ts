// Module Boards in the shell (interaction spec §6, catalog §2): GET /api/modules decides whether boards exist at all. With the
// module on, a board is a node of the space tree next to the pages, opens in a page's workspace frame (breadcrumbs through the
// tree, the title, the star, the link, the menu, the avatars), is created wherever a page is, and appears in Knihovna, the
// search and the trash. Without the module none of it renders and the board's chunk is never imported.
import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { useModules } from "./composables/useModules";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

const imported = vi.hoisted(() => ({ view: 0, goToPeer: [] as number[], calls: [] as string[] }));
vi.mock("./components/boards/BoardView.vue", async () => {
  imported.view++;
  const { defineComponent, h, onMounted, ref } = await import("vue");
  return { __esModule: true, default: defineComponent({ props: ["boardId"], emits: ["meta", "peers", "openPage", "sessionExpired"], setup: (props, { emit, expose }) => {
    const locked = ref(false);
    expose({ goToPeer: (id: number) => imported.goToPeer.push(id), locked, lockPulse: ref(0), toggleLock: () => { locked.value = !locked.value; },
      discussionOpen: ref(false), host: {}, toggleDiscussion: () => imported.calls.push("discussion"), toggleDock: () => imported.calls.push("dock") });
    onMounted(() => {
      emit("meta", { spaceId: "space-1", title: props.boardId === "b1" ? "Plán" : "Nová", canEdit: true });
      emit("peers", [{ clientId: 1, id: "user-3", name: "Kolegyně", color: "#a00", cursorAvailable: true }]);
    });
    return () => h("div", { "data-board-view-stub": props.boardId }, "plátno");
  } }) };
});
// The creation flow is its own suite (PageCreationFlow.test.ts); here only what the shell hands it and does with its answer.
vi.mock("./components/PageCreationFlow.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return { __esModule: true, default: defineComponent({ props: ["space", "pages", "initialParentId", "asBoard", "asTemplate"], emits: ["created", "createdBoard", "cancel", "dirty", "busy"],
    setup: (props, { emit }) => () => h("button", { "data-creation-stub": props.asBoard ? "board" : "page", "data-parent": props.initialParentId,
      "data-page-ids": (props.pages as { id: string }[]).map(page => page.id).join(","), onClick: () => emit("createdBoard", "b2") }, "Vytvořit") }) };
});

const appearance = { layout: "connected", accent: "blue", mode: "light" };
const member = { id: "user-2", username: "member", displayName: "Test Member", isWikiAdmin: false, appearance };
const space = { id: "space-1", name: "Tým", icon: "book", color: "blue", role: "Admin", pinPolicy: "None", pinned: true, pageCount: 1, lastChangedAt: null };
const tree = { spaceId: "space-1", version: 4, items: [
  { id: "p1", parentId: null, title: "Návod", version: 2, sortOrder: 1024, isPublished: true, canEdit: true, type: "page" },
  { id: "b1", parentId: "p1", title: "Plán", version: 0, sortOrder: 1024, isPublished: true, canEdit: true, type: "board" },
] };

let wrapper: VueWrapper | undefined;
let requests: { path: string; method: string; body?: unknown }[] = [];
afterEach(() => { wrapper?.unmount(); wrapper = undefined; useModules().stop(); resetBrowser(); imported.goToPeer = []; imported.calls = []; });
async function settle() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); }
async function settleMenu() { await flushPromises(); await new Promise(resolve => setTimeout(resolve, 40)); await flushPromises(); }
const menuItem = (selector: string) => document.querySelector<HTMLElement>(`[role=menu] ${selector}`);

async function start(hash: string, modules: string[], extra?: Route) {
  const wiki: Route = (path, init) => {
    if (path === "/api/auth/me") return response(member);
    if (path === "/api/spaces") return response([space]);
    if (path === "/api/modules") return response({ modules: modules.map(id => ({ id, version: 1 })), contentTypes: [] });
    if (path === "/api/ai/capabilities") return response({});
    if (path === "/api/spaces/space-1/tree") return response(modules.includes("boards") ? tree : { ...tree, items: tree.items.filter(item => item.type !== "board") });
    // Obsah of the space: the tree's items as the server lists them (the type filter in the query).
    if (path.startsWith("/api/spaces/space-1/contents")) {
      const onlyBoards = path.includes("type=atlas.board");
      const items = tree.items.filter(item => item.type === "board" || !onlyBoards).filter(item => modules.includes("boards") || item.type !== "board")
        .map(item => ({ id: item.id, typeId: `atlas.${item.type}`, title: item.title, path: null, scope: "space", spaceId: "space-1", usedCount: 0, linksIn: 0, linksOut: 0, comments: 0, updatedAt: "2026-10-27T08:00:00Z", canEdit: true }));
      return response({ items, summary: { types: { "atlas.page": 1, "atlas.board": 1 }, relations: { unused: 0, noIncoming: 0, broken: 0, attached: 0 } }, nextCursor: null });
    }
    if (path === "/api/boards/b1/move" || path === "/api/boards/b1/untrash") return response({ version: 5, affectedIds: ["b1"], hasSiblingTitleCollision: false });
    if (path === "/api/boards/b1" && init?.method === "DELETE") return new Response(null, { status: 204 });
    if (path === "/api/boards/b1/favorite") return response({ isFavorite: true, visitedAt: null });
    if (path.startsWith("/api/integrations/youtrack/")) return response([]);
  };
  requests = [];
  const record: Route = (path, init) => { requests.push({ path, method: init?.method ?? "GET", body: init?.body ? JSON.parse(String(init.body)) : undefined }); return undefined; };
  wrapper = mountApp({ url: "/" + hash, fetch: routedFetch(record, extra, wiki, sessionRoutes, quietRoutes) });
  await settle();
  return wrapper;
}
const sent = (path: string, method = "POST") => requests.filter(request => request.path === path && request.method === method);
const tabs = (app: VueWrapper) => app.findAll(".space-tabs [role=tab]").map(tab => tab.attributes("data-space-tab"));
const crumbs = (app: VueWrapper) => app.findAll(".breadcrumb-trail li").map(item => item.text().replace(/^\/\s*/, "").trim()).filter(text => text && text !== "…");

describe("App: module Boards", () => {
  it("renders no board, no board creation and imports no board chunk while the module is off", async () => {
    let app = await start("#/spaces/space-1", ["pages"]);
    expect(requests.some(request => request.path === "/api/modules")).toBe(true);
    expect(tabs(app)).toEqual(["pages", "contacts", "about"]);
    expect(app.find("[data-tree-type=board]").exists()).toBe(false);
    // Nová stránka stays a plain button.
    expect(app.find(".space-hero-actions [data-space-new-page]").element.tagName).toBe("BUTTON");
    expect(app.find('[aria-label="Nový obsah v prostoru"]').exists()).toBe(false);
    app.unmount();
    useModules().stop();
    resetBrowser();
    app = await start("#/boards/b1", ["pages"]);
    expect(app.find("[data-board-view-stub]").exists()).toBe(false);
    expect(requests.some(request => request.path.includes("/boards"))).toBe(false);
    expect(imported.view).toBe(0);
  });

  it("lists a board in the tree like a page and opens it in the page's frame, with its visit, star, link and peers", async () => {
    const app = await start("#/spaces/space-1", ["pages", "boards"]);
    // No section of its own any more: the tree and the space's list carry the boards.
    expect(tabs(app)).toEqual(["pages", "contacts", "about"]);
    expect(app.find("[data-sidebar-space-boards]").exists()).toBe(false);
    const row = app.get('.sidebar-space-tree [data-tree-item="b1"]');
    expect(row.attributes("href")).toContain("#/boards/b1");
    row.element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    await settle();
    expect(location.hash).toBe("#/boards/b1");
    expect(app.get("[data-board-view-stub]").attributes("data-board-view-stub")).toBe("b1");
    // Breadcrumbs through the tree, the board last and current; the space's row is not the current place, the board's is.
    expect(crumbs(app)).toEqual(["Atlas", "Tým", "Návod", "Plán"]);
    expect(app.get("[data-breadcrumb-board] [aria-current=page]").text()).toBe("Plán");
    expect(app.get('.sidebar-space-tree [data-tree-item="b1"]').attributes("aria-current")).toBe("page");
    expect(app.get("[data-sidebar-space='space-1'] .sidebar-space-row").classes()).not.toContain("active");
    // The visit and the personal state are the board's own, as a page's are.
    expect(sent("/api/boards/b1/visit")).toHaveLength(1);
    expect(sent("/api/boards/b1/personal-state", "GET")).toHaveLength(1);
    const actions = app.get("[data-board-actions]");
    expect(actions.find('[aria-label="Kopírovat odkaz na tabuli"]').exists()).toBe(true);
    // The lock stands in the top bar, where a page has it (F21), and switches the board's own state.
    await actions.get("[data-edit-lock]").trigger("click");
    expect(actions.get("[data-edit-lock]").attributes("aria-pressed")).toBe("true");
    await actions.get(".favorite-button").trigger("click");
    await settle();
    expect(sent("/api/boards/b1/favorite", "PUT")).toEqual([{ path: "/api/boards/b1/favorite", method: "PUT", body: { isFavorite: true } }]);
    expect(actions.get(".favorite-button").attributes("aria-pressed")).toBe("true");
    // The avatars sit where a page's do and lead to the colleague on the canvas.
    await actions.get('[data-client-id="1"]').trigger("click");
    expect(imported.goToPeer).toEqual([1]);
  });

  it("gives a board the page's Diskuse: the same button with its count in the top bar, Alt C, the ⋯ item and the dock toggle", async () => {
    const app = await start("#/boards/b1", ["pages", "boards"], path => (path === "/api/objects/b1/comments" ? response({ items: [], hasMore: false, count: 2, canPost: true }) : undefined));
    await settle();
    const actions = app.get("[data-board-actions]");
    expect(actions.get("[data-page-discussion-count]").text()).toBe("2");
    await actions.get("[data-page-discussion]").trigger("click");
    document.body.dispatchEvent(new KeyboardEvent("keydown", { key: "c", code: "KeyC", altKey: true, bubbles: true, cancelable: true }));
    await actions.get(".page-dock-toggle").trigger("click");
    expect(imported.calls).toEqual(["discussion", "discussion", "dock"]);
  });

  it("finds the board's space from the board itself when opened by its address", async () => {
    const app = await start("#/boards/b1", ["pages", "boards"]);
    expect(app.get("[data-board-view-stub]").attributes("data-board-view-stub")).toBe("b1");
    expect(crumbs(app)).toEqual(["Atlas", "Tým", "Návod", "Plán"]);
    expect(app.get('.sidebar-space-tree [data-tree-item="b1"]').attributes("aria-current")).toBe("page");
  });

  it("moves a board in the tree through the board's own move with the tree's version", async () => {
    const app = await start("#/spaces/space-1", ["pages", "boards"]);
    await new Promise(resolve => setTimeout(resolve, 5));
    const dataTransfer = { setData: vi.fn(), effectAllowed: "", dropEffect: "" };
    const drag = new Event("dragstart", { bubbles: true, cancelable: true });
    Object.assign(drag, { dataTransfer });
    app.get('.sidebar-space-tree [data-tree-item="b1"]').element.closest(".tree-row")!.dispatchEvent(drag);
    const drop = new Event("drop", { bubbles: true, cancelable: true });
    Object.assign(drop, { dataTransfer });
    app.get("[data-sidebar-space='space-1'] .sidebar-space-row").element.dispatchEvent(drop);
    await settle();
    expect(sent("/api/boards/b1/move")).toEqual([{ path: "/api/boards/b1/move", method: "POST", body: { parentId: null, beforePageId: null, expectedTreeVersion: 4 } }]);
    expect(dataTransfer.setData).not.toHaveBeenCalledWith("application/x-atlas-page", "b1");
  });

  it("offers Stránka or Tabule where Nová stránka was, and a board made beside an open board goes under the same page", async () => {
    const app = await start("#/spaces/space-1", ["pages", "boards"]);
    await app.get('.space-hero-actions [aria-label="Nový obsah v prostoru"]').trigger("click"); await settleMenu();
    menuItem("[data-space-new-board]")!.click(); await settle();
    expect(location.hash).toBe("#/spaces/space-1/new-board");
    const flow = app.get("[data-creation-stub]");
    expect(flow.attributes("data-creation-stub")).toBe("board");
    // Parents are pages only.
    expect(flow.attributes("data-page-ids")).toBe("p1");
    await flow.trigger("click"); await settle();
    expect(location.hash).toBe("#/boards/b2");
    expect(app.get("[data-board-view-stub]").attributes("data-board-view-stub")).toBe("b2");

    wrapper!.unmount(); useModules().stop(); resetBrowser();
    const onBoard = await start("#/boards/b1", ["pages", "boards"]);
    await onBoard.get('[aria-label="Další akce prostoru Tým"]').trigger("click"); await settleMenu();
    menuItem("[data-space-menu=new-board]")!.click(); await settle();
    expect(onBoard.get("[data-creation-stub]").attributes("data-parent")).toBe("p1");
  });

  it("offers Nová tabule in the command palette", async () => {
    const app = await start("#/spaces/space-1", ["pages", "boards"]);
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }));
    await settle();
    await app.get(".search-dialog input").setValue(">tabule");
    await settle();
    expect(document.querySelector("#command-create-board")?.textContent).toContain("Nová tabule");
    (document.querySelector("#command-create-board") as HTMLElement).click();
    await settle(); await new Promise(resolve => setTimeout(resolve, 250)); await settle();
    expect(location.hash).toBe("#/spaces/space-1/new-board");
  });

  it("shows board hits of the search as Tabule and opens them as a board", async () => {
    const search: Route = path => path.startsWith("/api/search") ? response({ items: [], hasMore: false, spaces: [], nextOffset: null,
      resources: [{ type: "board", id: "b1", spaceId: "space-1", spaceName: "Tým", title: "Plán", snippet: "Plán vydání a termíny" }] }) : undefined;
    const app = await start("#/spaces/space-1", ["pages", "boards"], search);
    window.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true }));
    await settle();
    await app.get(".search-dialog input").setValue("plán");
    await new Promise(resolve => setTimeout(resolve, 260)); await settle();
    const hit = document.querySelector<HTMLElement>("[data-search-board]")!;
    expect(hit.closest("[data-kind]")?.getAttribute("data-kind")).toBe("boards");
    expect(hit.querySelector("[data-search-type]")?.textContent).toBe("Tabule");
    expect(hit.getAttribute("href")).toContain("#/boards/b1");
    expect(hit.textContent).toContain("Plán vydání a termíny");
    hit.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    await settle();
    expect(location.hash).toBe("#/boards/b1");
  });

  it("opens a favourite board from Knihovna", async () => {
    const library: Route = path => path.startsWith("/api/me/library/favorites") ? response({ hasMore: false, nextOffset: null, items: [
      { id: "b1", type: "board", spaceId: "space-1", spaceName: "Tým", title: "Plán", parentId: null, version: 0, isPublished: true, isFavorite: true, visitedAt: null, publishedAt: null }] }) : undefined;
    const app = await start("#/library/favorites", ["pages", "boards"], library);
    const item = app.get('.library-item[data-library-type="board"]');
    item.element.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
    await settle();
    expect(location.hash).toBe("#/boards/b1");
  });

  it("U14: moves an open board from its „…“ menu with the page's location picker and the board's own move", async () => {
    const app = await start("#/boards/b1", ["pages", "boards"]);
    await app.get('[aria-label="Další akce tabule"]').trigger("click"); await settleMenu();
    menuItem("[data-board-move]")!.click(); await settle();
    const dialog = document.querySelector<HTMLElement>("dialog[open]")!;
    expect(dialog.textContent).toContain("Přesunout tabuli");
    dialog.querySelector<HTMLElement>('#organization-parent [data-page-id=""]')!.click(); await flushPromises();
    dialog.querySelector<HTMLFormElement>('form[aria-label="Přesunout tabuli"]')!.requestSubmit(); await settle();
    expect(sent("/api/boards/b1/move")).toEqual([{ path: "/api/boards/b1/move", method: "POST", body: { parentId: null, beforePageId: null, expectedTreeVersion: 4 } }]);
  });

  it("moves an open board to the trash after asking, and lands on its space", async () => {
    const app = await start("#/boards/b1", ["pages", "boards"]);
    await app.get('[aria-label="Další akce tabule"]').trigger("click"); await settleMenu();
    // U14: no disabled placeholder for what does not exist yet.
    expect(menuItem("[data-board-history]")).toBeNull();
    menuItem("[data-board-trash]")!.click(); await settle();
    expect(document.body.textContent).toContain("Přesunout tabuli do koše?");
    document.querySelector<HTMLElement>("[data-confirm-action]")!.click();
    await settle();
    expect(sent("/api/boards/b1", "DELETE")).toHaveLength(1);
    expect(location.hash).toBe("#/spaces/space-1");
    expect(app.text()).toContain("Tabule byla přesunuta do koše.");
  });

  it("keeps the space's boards address as its list filtered to boards", async () => {
    const app = await start("#/spaces/space-1/boards", ["pages", "boards"]);
    expect(app.findAll("[data-content-row]").map(row => row.text())).toEqual(["Plán"]);
    expect(app.get(".space-tabs [aria-selected=true]").attributes("data-space-tab")).toBe("pages");
  });
});
