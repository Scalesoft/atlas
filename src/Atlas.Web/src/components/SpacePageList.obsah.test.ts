import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SpacePageList from "./SpacePageList.vue";
import { objectDetailShown } from "../objects/objectDetail";
import { useWorkspacePanel } from "../composables/useWorkspacePanel";
import { memoryRelations, memoryUsage, type MemoryObject } from "../objects/memoryRelations";
import type { ContentQuery, RelationEdge, RelationsApi } from "../objects/relationsApi";
import RelationsGraph from "./objects/RelationsGraph.vue";

// Obsah is SpacePageList with a spaceId: the same list over every object of the space, from the server's contents contract
// (here objects/memoryRelations.ts answers it).
const object = (id: string, typeId: string, title: string, extra: Partial<MemoryObject> = {}): MemoryObject => ({ id, typeId, title, spaceId: "s", spaceName: "Provoz", readable: true, ...extra });
const ids = { handbook: "11111111-1111-4111-8111-111111111111", release: "22222222-2222-4222-8222-222222222222", board: "66666666-6666-4666-8666-666666666666", jana: "88888888-8888-4888-8888-888888888888" };
const objects: MemoryObject[] = [
  object(ids.handbook, "atlas.page", "Provozní příručka", { path: "Příručky" }),
  object(ids.release, "atlas.page", "Postup **vydání**", { path: "Příručky › Vydání" }),
  object(ids.board, "atlas.board", "Plán vydání"),
  object(ids.jana, "atlas.contact", "Jana Nováková", { scope: "wiki" }),
];
const edges: RelationEdge[] = [
  { sourceId: ids.handbook, targetId: ids.release, kind: "link", count: 1 },
  { sourceId: ids.handbook, targetId: ids.board, kind: "placement", count: 1 },
  { sourceId: ids.release, targetId: ids.board, kind: "placement", count: 1 },
];
let wrapper: VueWrapper | undefined;
const panel = useWorkspacePanel();
afterEach(() => { wrapper?.unmount(); wrapper = undefined; panel.reset(); vi.useRealTimers(); document.body.innerHTML = ""; });

async function show(api: RelationsApi = memoryRelations(objects, edges), props: Record<string, unknown> = {}) {
  wrapper = mount(SpacePageList, { props: { pages: [], canWrite: false, spaceId: "s", relations: api, ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}
const titles = (app: VueWrapper) => app.findAll(".content-row .space-page-title").map(title => title.text());
async function type(app: VueWrapper, text: string) {
  const input = app.get<HTMLInputElement>("[data-content-search]");
  await input.setValue(text);
  await new Promise(resolve => setTimeout(resolve, 260));
  await flushPromises();
  return input;
}

describe("Obsah list", () => {
  it("lists every object of the space with its type, the plain words of its title, where it lives and a mark for the wiki's contacts", async () => {
    const app = await show();
    expect(titles(app)).toEqual(["Jana Nováková", "Plán vydání", "Postup vydání", "Provozní příručka"]);
    const row = app.get(`[data-content-id="${ids.release}"]`);
    expect(row.get(".content-sub").text()).toBe("Příručky › Vydání");
    expect(app.get(`[data-content-id="${ids.jana}"]`).find("[data-content-scope]").text()).toBe("z celé wiki");
    expect(app.get(`[data-content-id="${ids.board}"] .content-used`).text()).toBe("2");
    expect(app.get(`[data-content-id="${ids.handbook}"] .content-links`).attributes("aria-label")).toBe("Odkazů dovnitř: 0, ven: 1");
  });

  it("counts the space in tiles; a click filters by that type and the same click clears the filter", async () => {
    const app = await show();
    const tile = (key: string) => app.get(`[data-content-tile="${key}"]`);
    expect(tile("type:atlas.page").text()).toContain("2");
    await tile("type:atlas.contact").trigger("click"); await flushPromises();
    expect(titles(app)).toEqual(["Jana Nováková"]);
    expect(tile("type:atlas.contact").attributes("aria-pressed")).toBe("true");
    await tile("type:atlas.contact").trigger("click"); await flushPromises();
    expect(titles(app)).toHaveLength(4);
  });

  it("holds the tiles' row and the tree's type pills from the first frame, before the summary arrives (F3)", async () => {
    let answer!: () => void;
    const api = memoryRelations(objects, edges);
    const contents = api.contents.bind(api);
    api.contents = (...args: Parameters<RelationsApi["contents"]>) => new Promise(resolve => { answer = () => resolve(contents(...args)); });
    const tree = [{ id: ids.handbook, spaceId: "s", title: "Provozní příručka", parentId: null, isPublished: true, version: 1, sortOrder: 0 }];
    wrapper = mount(SpacePageList, { props: { pages: tree, canWrite: false, spaceId: "s", relations: api }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.findAll("[data-content-tiles-loading] .content-tile")).toHaveLength(5);
    expect(wrapper.findAll(".content-type-pills [data-value]").map(pill => pill.attributes("data-value"))).toEqual(["all", "atlas.page"]);
    answer(); await flushPromises();
    expect(wrapper.find("[data-content-tiles-loading]").exists()).toBe(false);
    expect(wrapper.get('[data-content-tile="type:atlas.page"]').text()).toContain("2");
  });

  it("filters by relation: the contact placed nowhere is the only unused one", async () => {
    const app = await show();
    await app.get('[data-content-tile="relation:unused"]').trigger("click"); await flushPromises();
    expect(titles(app)).toEqual(["Jana Nováková"]);
  });

  it("with only „Nepoužité“ on and nothing found says that all is used, a search with no result says nothing matches", async () => {
    const app = await show();
    await app.get('[data-content-tile="relation:unused"]').trigger("click"); await flushPromises();
    await app.get('[data-content-tile="type:atlas.page"]').trigger("click"); await flushPromises();
    expect(titles(app)).toEqual([]);
    expect(app.text()).toContain("Všechno se někde používá.");
    expect(app.text()).not.toContain("Nic neodpovídá");
    await type(app, "zzz");
    expect(app.text()).toContain("Nic neodpovídá");
  });

  // „Použito na“ unfolds under its row: from the count, or Alt+↓ on the title; Alt+↑ and a new filter fold it.
  it("unfolds where a row is used under it and folds it again", async () => {
    const app = await show(undefined, { usage: memoryUsage(objects, edges) });
    const row = () => app.findAll(".content-row").find(item => item.find(".space-page-title").text() === "Postup vydání")!;
    await row().get("[data-content-usage]").trigger("click"); await flushPromises();
    expect(app.get("[data-content-usage-row]").findAll("[data-usage-place]").map(place => place.find(".usage-title").text())).toEqual(["Plán vydání", "Provozní příručka"]);
    expect(row().get("[data-content-usage]").attributes("aria-expanded")).toBe("true");
    await row().get(".space-page-title").trigger("keydown", { key: "ArrowUp", altKey: true }); await flushPromises();
    expect(app.find("[data-content-usage-row]").exists()).toBe(false);
    await row().get(".space-page-title").trigger("keydown", { key: "ArrowDown", altKey: true }); await flushPromises();
    expect(app.find("[data-content-usage-row]").exists()).toBe(true);
    await app.get('[data-content-tile="relation:unused"]').trigger("click"); await flushPromises();
    expect(app.find("[data-content-usage-row]").exists()).toBe(false);
  });

  it("preselects the first result of a search, ↑/↓ move the choice and Enter opens it", async () => {
    const app = await show();
    const input = await type(app, "p");
    expect(titles(app)).toEqual(["Plán vydání", "Postup vydání", "Provozní příručka"]);
    expect(app.get(".content-row.is-active .space-page-title").text()).toBe("Plán vydání");
    await input.trigger("keydown", { key: "ArrowDown" });
    expect(app.get(".content-row.is-active .space-page-title").text()).toBe("Postup vydání");
    await input.trigger("keydown", { key: "ArrowDown" });
    await input.trigger("keydown", { key: "ArrowDown" });
    expect(app.get(".content-row.is-active .space-page-title").text()).toBe("Provozní příručka");
    await input.trigger("keydown", { key: "ArrowUp" });
    await input.trigger("keydown", { key: "Enter" });
    expect(app.emitted("open")).toEqual([[ids.release, "page"]]);
  });

  it("does not open a stale first row when Enter comes before the server has answered the typed text", async () => {
    const api = memoryRelations(objects, edges);
    const app = await show(api);
    const input = app.get<HTMLInputElement>("[data-content-search]");
    await input.setValue("jana");
    await input.trigger("keydown", { key: "Enter" });
    await flushPromises();
    // The contact is no page or board: Enter shows its dock instead of navigating.
    expect(app.emitted("open")).toBeUndefined();
    expect(objectDetailShown()).toBe(true);
    expect((panel.contentProps.value.row as { id: string }).id).toBe(ids.jana);
  });

  describe("Enter in the search", () => {
    /** Answers of contents are held until released, so a test can press Enter while the server is still working. */
    function gated() {
      const inner = memoryRelations(objects, edges);
      const state = { hold: false, calls: [] as { release(): void; fail(): void }[] };
      const api: RelationsApi = {
        relations: inner.relations,
        contents: (spaceId: string, query: ContentQuery, signal?: AbortSignal) => state.hold
          ? new Promise((resolve, reject) => state.calls.push({ release: () => inner.contents(spaceId, query, signal).then(resolve, reject), fail: () => reject(new Error("Server neodpovídá.")) }))
          : inner.contents(spaceId, query, signal),
      };
      return { api, state };
    }
    const wait = async () => { await new Promise(resolve => setTimeout(resolve, 260)); await flushPromises(); };

    it("waits for the answer that is still running instead of opening the row of the previous query", async () => {
      const { api, state } = gated();
      const app = await show(api);
      state.hold = true;
      const input = app.get<HTMLInputElement>("[data-content-search]");
      await input.setValue("pla"); await wait();
      await input.trigger("keydown", { key: "Enter" }); await flushPromises();
      expect(app.emitted("open")).toBeUndefined();
      expect(panel.open.value).toBe(false);
      state.calls.at(-1)!.release(); await flushPromises();
      expect(app.emitted("open")).toEqual([[ids.board, "board"]]);
    });

    it("works right after an empty result too", async () => {
      const { api, state } = gated();
      const app = await show(api);
      await type(app, "zzz");
      expect(app.text()).toContain("Nic neodpovídá");
      state.hold = true;
      const input = app.get<HTMLInputElement>("[data-content-search]");
      await input.setValue("pla");
      await input.trigger("keydown", { key: "Enter" }); await flushPromises();
      state.calls.at(-1)!.release(); await flushPromises();
      expect(app.emitted("open")).toEqual([[ids.board, "board"]]);
    });

    it("forgets the Enter when the answer fails", async () => {
      const { api, state } = gated();
      const app = await show(api);
      state.hold = true;
      const input = app.get<HTMLInputElement>("[data-content-search]");
      await input.setValue("pla"); await wait();
      await input.trigger("keydown", { key: "Enter" }); await flushPromises();
      state.calls.at(-1)!.fail(); await flushPromises();
      state.hold = false;
      await type(app, "post");
      expect(app.emitted("open")).toBeUndefined();
      expect(panel.open.value).toBe(false);
    });
  });

  it("Escape in the search clears it", async () => {
    const app = await show();
    const input = await type(app, "zzz");
    expect(app.text()).toContain("Nic neodpovídá");
    await input.trigger("keydown", { key: "Escape" });
    expect(input.element.value).toBe("");
  });

  it("opens the object from its title and the dock from anywhere else in the row", async () => {
    const app = await show();
    await app.get(`[data-content-id="${ids.handbook}"] .space-page-title`).trigger("click");
    expect(app.emitted("open")).toEqual([[ids.handbook, "page"]]);
    expect(panel.open.value).toBe(false);
    await app.get(`[data-content-id="${ids.board}"] .content-changed`).trigger("click");
    expect(panel.open.value).toBe(true);
    expect(objectDetailShown()).toBe(true);
    expect(panel.tabs.value?.map(tab => tab.id)).toEqual(["overview", "relations", "usage", "comments", "history"]);
  });

  it("makes every row draggable as its object, a page also as a page", async () => {
    const app = await show();
    const data = new Map<string, string>();
    await app.get(`[data-content-id="${ids.handbook}"]`).trigger("dragstart", { dataTransfer: { setData: (type: string, value: string) => data.set(type, value) } });
    expect(JSON.parse(data.get("application/x-atlas-object")!)).toMatchObject({ id: ids.handbook, typeId: "atlas.page", title: "Provozní příručka" });
    expect(data.get("application/x-atlas-page")).toBe(ids.handbook);
  });

  it("loads the next page by cursor", async () => {
    const app = await show(memoryRelations(objects, edges, { pageSize: 3 }));
    expect(titles(app)).toHaveLength(3);
    await app.get("[data-more-pages]").trigger("click"); await flushPromises();
    expect(titles(app)).toHaveLength(4);
    expect(app.find("[data-more-pages]").exists()).toBe(false);
  });

  it("says so when the space has nothing and when the server fails", async () => {
    const empty = await show(memoryRelations([], []));
    expect(empty.text()).toContain("V prostoru zatím nic není");
    empty.unmount(); wrapper = undefined;
    const broken: RelationsApi = { contents: async () => { throw new Error("Server neodpovídá."); }, relations: async () => null };
    const failed = await show(broken);
    expect(failed.get("[role=alert]").text()).toContain("Server neodpovídá.");
  });
});

describe("Obsah Vazby", () => {
  it("shows the neighbourhood of the object with the most links, one step, and two on request", async () => {
    const calls: number[] = [];
    const inner = memoryRelations(objects, edges);
    const api: RelationsApi = { contents: inner.contents, relations: (id, depth, signal) => { calls.push(depth); return inner.relations(id, depth, signal); } };
    const app = await show(api);
    await app.get('[data-content-view="graph"]').trigger("click"); await flushPromises();
    // Not the first row (the contact, placed nowhere: „Bez vazeb“) but the one with the most links.
    expect(app.get(".content-graph-subject").text()).toContain("Postup vydání");
    expect(calls).toEqual([1]);
    expect(app.get("[data-relations-graph]").text()).not.toContain("Bez vazeb");
    expect(app.findAll(".vue-flow__node").length).toBeGreaterThanOrEqual(3);
    await app.get('[data-value="2"]').trigger("click"); await flushPromises();
    expect(calls.at(-1)).toBe(2);
  });

  it("draws the neighbourhood of the object whose dock is open, and a click in the graph does not move the graph", async () => {
    const app = await show();
    await app.get(`[data-content-id="${ids.jana}"] [data-content-detail]`).trigger("click"); await flushPromises();
    await app.get('[data-content-view="graph"]').trigger("click"); await flushPromises();
    expect(app.get(".content-graph-subject").text()).toContain("Jana Nováková");
    app.findComponent(RelationsGraph).vm.$emit("select", ids.board, "atlas.board"); await flushPromises();
    expect(app.get(".content-graph-subject").text()).toContain("Jana Nováková");
  });

  it("reports the count of every object of the space for the tab, the tiles' number", async () => {
    const app = await show();
    expect(app.emitted("counted")).toEqual([[4]]);
  });

  it("keeps a node the list has no row for as the subject instead of falling back to the first row", async () => {
    const app = await show();
    await app.get('[data-content-tile="type:atlas.page"]').trigger("click"); await flushPromises();
    await app.get('[data-content-view="graph"]').trigger("click"); await flushPromises();
    expect(app.get(".content-graph-subject").text()).toContain("Postup vydání");
    app.findComponent(RelationsGraph).vm.$emit("select", ids.board, "atlas.board"); await flushPromises();
    (panel.contentProps.value as { onFocus: (id: string) => void }).onFocus(ids.board); await flushPromises();
    expect(app.get(".content-graph-subject").text()).toContain("Plán vydání");
  });

  it("marks the open row with aria-current and keeps the row's detail button out of the Tab order", async () => {
    const app = await show();
    const row = app.get(`[data-content-id="${ids.board}"]`);
    expect(row.attributes("aria-selected")).toBeUndefined();
    expect(row.attributes("aria-current")).toBeUndefined();
    await row.get(".content-changed").trigger("click");
    expect(row.attributes("aria-current")).toBe("true");
    expect(row.get("[data-content-detail]").attributes("tabindex")).toBe("-1");
  });

  it("puts the object a row's dock asks about into the graph", async () => {
    const app = await show();
    await app.get(`[data-content-id="${ids.board}"] .content-changed`).trigger("click");
    (panel.contentProps.value as { onFocus: (id: string) => void }).onFocus(ids.board);
    await flushPromises();
    expect(app.get(".content-graph-subject").text()).toContain("Plán vydání");
  });
});
