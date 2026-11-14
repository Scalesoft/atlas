import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import PageConnections from "./PageConnections.vue";
import { ApiError } from "../api";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { LINKS_HERE_MORE, pageConnectionsApi, type LinkingPage, type PageConnections as Connections, type PageConnectionsApi } from "../pageConnections";

const here = (id: string, title: string, spaceId = "space-1", spaceName = "IT a infrastruktura"): LinkingPage => ({ id, spaceId, spaceName, title });
const sample = [here("p-1", "Onboarding IT"), here("p-2", "Práce z domova", "space-2", "Lidé a HR"), here("p-3", "Bezpečnostní zásady")];
const connections = (items: LinkingPage[], total = items.length, related?: LinkingPage[]): Connections => ({ linksHere: { items, total }, ...(related ? { related: { items: related } } : {}) });
const similar = [here("r-1", "Certifikáty zařízení"), here("r-2", "Wi-Fi pro hosty", "space-2", "Lidé a HR"), here("r-3", "Vzdálená plocha")];

let wrapper: VueWrapper | undefined;
let animations: { el: Element; frames: Keyframe[]; options: KeyframeAnimationOptions }[] = [];

beforeEach(() => {
  animations = [];
  Element.prototype.animate = vi.fn(function (this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) {
    animations.push({ el: this, frames, options });
    return { finished: Promise.resolve(), cancel: vi.fn() } as unknown as Animation;
  }) as unknown as Element["animate"];
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks(); document.body.innerHTML = ""; });

function fakeApi(handler: (pageId: string, limit: number, signal?: AbortSignal) => Promise<Connections | null>) {
  const get = vi.fn(handler);
  return { api: { get, relations: async () => null } as PageConnectionsApi, get };
}
async function show(api: PageConnectionsApi, props: { layout: "graph" | "list" } & Record<string, unknown>) {
  wrapper = mount(PageConnections, { props: { pageId: "page-9", api, ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}
const titles = (app: VueWrapper) => app.findAll(".connections-list-title").map(title => title.text());

describe("PageConnections graph", () => {
  it("starts with small navigable tags and opens the map only on request, resetting on another article", async () => {
    const app = await show(fakeApi(async () => connections(sample, 3, similar)).api, { layout: "graph" });
    expect(app.findAll(".connections-tags a")).toHaveLength(6);
    expect(app.find("h2").exists()).toBe(false);
    expect(app.get("details").attributes("open")).toBeUndefined();
    expect(app.get("summary").text()).toContain("Mapa souvislostí");
    (app.get("details").element as HTMLDetailsElement).open = true;
    await app.get("details").trigger("toggle");
    await app.setProps({ pageId: "another" });
    await flushPromises();
    expect(app.get("details").attributes("open")).toBeUndefined();
  });

  it("opens authorized real targets and distinguishes incoming, outgoing and mutual links from topic similarity", async () => {
    const document: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "paragraph", content: [
        { type: "pageReference", attrs: { id: "p-1", title: "Old title" } },
        { type: "pageReference", attrs: { id: "out", title: "Old outgoing title" } },
        { type: "pageReference", attrs: { id: "hidden", title: "Secret title" } },
        { type: "pageReference", attrs: { id: "page-9", title: "Self" } },
      ] },
    ] } };
    const pageSource = { resolve: async (id: string) => id === "p-1" ? { id, title: "Onboarding IT", url: "#/pages/p-1" }
      : id === "out" ? { id, title: "A complete long outgoing title that remains readable", url: "#/pages/out" } : null };
    const app = await show(fakeApi(async () => connections(sample, 3, similar)).api,
      { layout: "graph", pageTitle: "Current article", document, pageSource });
    expect(app.get(".connections-graph-center").text()).toContain("Current article");
    const nodes = app.findAll(".connections-graph-node");
    expect(nodes.map(node => new URL(node.attributes("href")!).hash).sort()).toEqual(["#/pages/out", "#/pages/p-1", "#/pages/p-2", "#/pages/p-3"]);
    expect(nodes[0]!.text()).toContain("Vzájemné odkazy");
    expect(nodes[1]!.text()).toContain("Odkazuje sem");
    expect(nodes[3]!.text()).toContain("Odkaz ve stránce");
    expect(nodes[3]!.text()).toContain("A complete long outgoing title that remains readable");
    expect(app.text()).not.toContain("Secret title");
    expect(app.text()).not.toContain("Old title");
    expect(app.findAll(".connections-tags a[title^='Podobné tématem']")).toHaveLength(3);
    expect(app.findAll(".connections-graph-node[href*='r-']")).toHaveLength(0);
    (nodes[3]!.element as HTMLAnchorElement).focus();
    expect(window.document.activeElement).toBe(nodes[3]!.element);
  });

  it("shows an honest empty state only after a successful authorized load", async () => {
    const app = await show(fakeApi(async () => connections([])).api, { layout: "graph", pageTitle: "Empty article" });
    expect(app.find("section").exists()).toBe(false);
    expect(app.find(".connections-graph").exists()).toBe(false);
    await app.setProps({ api: fakeApi(async () => null).api, pageId: "gone" });
    await flushPromises();
    expect(app.find("section").exists()).toBe(false);
  });

  it("shows every connection in the map at once, never paged", async () => {
    const pages = Array.from({ length: 8 }, (_, index) => here(`node-${index}`, `Article ${index}`));
    const app = await show(fakeApi(async () => connections(pages)).api, { layout: "graph", pageTitle: "Article" });
    expect(app.findAll(".connections-graph-node")).toHaveLength(8);
    expect(app.find('[aria-label="Další vazby"]').exists()).toBe(false);
  });

  it("names the space only on pages of another space", async () => {
    const app = await show(fakeApi(async () => connections(sample)).api, { layout: "graph", spaceId: "space-1" });
    expect(app.findAll(".connections-graph-node .links-here-space").map(space => space.text())).toEqual(["Lidé a HR"]);
  });

  it("shows only the tags, without a map, when nothing links here but similar articles exist", async () => {
    const app = await show(fakeApi(async () => connections([], 0, similar)).api, { layout: "graph" });
    expect(app.findAll(".connections-tags a[title^='Podobné tématem']")).toHaveLength(3);
    expect(app.find("details").exists()).toBe(false);
  });

  it.each([
    ["a failing server", async () => { throw new ApiError(500, "Selhalo"); }],
    ["a total without readable items", async () => connections([], 4)],
  ])("renders nothing for %s", async (_name, result) => {
    const app = await show(fakeApi(result).api, { layout: "graph" });
    expect(app.find("section").exists()).toBe(false);
    expect(animations).toHaveLength(0);
  });

  it("hides the section when a refresh after a publication removes every link", async () => {
    let result = connections(sample, 3, similar);
    const app = await show(fakeApi(async () => result).api, { layout: "graph", refreshKey: 0 });
    expect(app.find("section").exists()).toBe(true);
    result = connections([], 0);
    await app.setProps({ refreshKey: 1 });
    await flushPromises();
    expect(app.find("section").exists()).toBe(false);
  });
});

describe("PageConnections graph motion (prototype: Čtení stránky, moment 5)", () => {
  it("lets the nodes rise one by one and the tags 90 ms later, on the first render of a page only", async () => {
    const app = await show(fakeApi(async () => connections(sample, 3, similar)).api, { layout: "graph", refreshKey: 0 });
    const nodes = animations.filter(call => call.el.tagName === "A");
    const tags = animations.filter(call => call.el.tagName === "LI");
    expect(nodes.map(call => call.options.delay)).toEqual([0, 60, 120]);
    expect(tags.map(call => call.options.delay)).toEqual([90, 150, 210, 270, 330, 390]);
    for (const call of [...nodes, ...tags]) {
      expect(call.options.duration).toBe(220);
      expect(call.frames[0]).toMatchObject({ opacity: 0, transform: "translateY(8px)" });
    }
    const first = animations.map(call => call.options.delay);
    animations.length = 0;
    await app.setProps({ refreshKey: 1 });
    await flushPromises();
    expect(animations).toHaveLength(0);
    await app.setProps({ pageId: "page-10" });
    await flushPromises();
    expect(animations.map(call => call.options.delay)).toEqual(first);
  });

  it("animates the similar tags when they first appear in a refresh, and the nodes not again", async () => {
    let result = connections(sample);
    const app = await show(fakeApi(async () => result).api, { layout: "graph", refreshKey: 0 });
    animations.length = 0;
    result = connections(sample, 3, similar);
    await app.setProps({ refreshKey: 1 });
    await flushPromises();
    expect(animations.filter(call => call.el.tagName === "A")).toHaveLength(0);
    const tags = animations.filter(call => call.el.tagName === "LI");
    expect(tags.length).toBeGreaterThan(0);
    expect(tags.every(call => (call.options.delay as number) >= 90)).toBe(true);
    animations.length = 0;
    await app.setProps({ refreshKey: 2 });
    await flushPromises();
    expect(animations).toHaveLength(0);
  });
});

describe("PageConnections (R2 Odkazuje sem)", () => {
  it("turns a 404 of the endpoint into nothing and any other failure into an error", async () => {
    const fetchMock = vi.fn(async (_input: unknown) => new Response(JSON.stringify({ title: "Nenalezeno" }), { status: 404, headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    expect(await pageConnectionsApi.get("a/b", 10)).toBeNull();
    expect(fetchMock.mock.calls[0]![0]).toBe("/api/pages/a%2Fb/connections?limit=10");
    fetchMock.mockResolvedValueOnce(new Response("{}", { status: 500 }));
    await expect(pageConnectionsApi.get("a", 10)).rejects.toBeInstanceOf(ApiError);
    vi.unstubAllGlobals();
  });

  it("answers the graph under the article and the dock from one request, and reads again after a reader gave up", async () => {
    const fetchMock = vi.fn(async (_input: unknown) => new Response(JSON.stringify(connections([here("p1", "Onboarding")])), { headers: { "Content-Type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
    const footer = new AbortController();
    const [first, second] = await Promise.all([pageConnectionsApi.get("shared", 50, footer.signal), pageConnectionsApi.get("shared", 50)]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(second).toBe(first);
    // The dock, mounted a moment later, takes that answer.
    expect(await pageConnectionsApi.get("shared", 50)).toBe(first);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    // A re-read after a change (fresh) starts its own request instead of joining one that began before the change.
    const stale = pageConnectionsApi.get("shared", 50, footer.signal, true);
    footer.abort();
    await Promise.all([stale, pageConnectionsApi.get("shared", 50, undefined, true)]);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    vi.unstubAllGlobals();
  });

  it("keeps the previous page's section in place until the other page's links arrive (no vanishing column)", async () => {
    let release!: (value: Connections) => void;
    const { api, get } = fakeApi(async id => id === "page-9" ? connections(sample) : new Promise<Connections>(resolve => { release = resolve; }));
    const app = await show(api, { layout: "list" });
    await app.setProps({ pageId: "page-10" });
    expect(app.find("section").exists()).toBe(true);
    release(connections([here("q-1", "Jiná stránka")]));
    await flushPromises();
    expect(titles(app)).toEqual(["Jiná stránka"]);
    expect(get).toHaveBeenCalledTimes(2);
  });

  it("ignores a response that arrives after the page changed", async () => {
    const pending: Record<string, (value: Connections) => void> = {};
    const { api } = fakeApi((id) => new Promise<Connections>(resolve => { pending[id] = resolve; }));
    wrapper = mount(PageConnections, { props: { pageId: "a", api, layout: "list" }, attachTo: document.body });
    await wrapper.setProps({ pageId: "b" });
    pending.b!(connections([here("b-1", "Odkaz na B")]));
    await flushPromises();
    pending.a!(connections([here("a-1", "Odkaz na A")]));
    await flushPromises();
    expect(titles(wrapper)).toEqual(["Odkaz na B"]);
  });

  it("quietens the previous page's cards until the new page's arrive", async () => {
    const pending: Record<string, (value: Connections) => void> = {};
    const { api } = fakeApi((id) => new Promise<Connections>(resolve => { pending[id] = resolve; }));
    wrapper = mount(PageConnections, { props: { pageId: "a", api, layout: "list" }, attachTo: document.body });
    pending.a!(connections([here("a-1", "Odkaz na A")]));
    await flushPromises();
    const section = () => wrapper!.get("[data-connections-list]");
    expect(section().classes()).not.toContain("is-stale");
    await wrapper.setProps({ pageId: "b" });
    expect(section().classes()).toContain("is-stale");
    expect(section().attributes("aria-busy")).toBe("true");
    pending.b!(connections([here("b-1", "Odkaz na B")]));
    await flushPromises();
    expect(section().classes()).not.toContain("is-stale");
  });
});

describe("PageConnections (R1 Související)", () => {
  const similarCards = (app: VueWrapper) => app.findAll(".connections-list-card").filter(card => card.get("small").text().startsWith("Podobné tématem"));

  it("hides it silently on the next load when the capability is gone and brings it back with the capability", async () => {
    let result = connections(sample, 3, similar);
    const app = await show(fakeApi(async () => result).api, { layout: "list", refreshKey: 0 });
    expect(similarCards(app)).toHaveLength(3);
    result = connections(sample);
    await app.setProps({ refreshKey: 1 }); await flushPromises();
    expect(similarCards(app)).toHaveLength(0);
    expect(titles(app)).toHaveLength(3);
    result = connections(sample, 3, similar);
    await app.setProps({ refreshKey: 2 }); await flushPromises();
    expect(similarCards(app)).toHaveLength(3);
  });
});

describe("PageConnections list (page dock „Odtud dál“)", () => {
  it("names how every page relates, references first and similar pages after, six until more are asked for", async () => {
    const document: MarkdownRichValue = { format: "atlas-rich", version: 1, document: { type: "doc", content: [
      { type: "paragraph", content: [{ type: "pageReference", attrs: { id: "p-1", title: "Onboarding IT" } }, { type: "pageReference", attrs: { id: "out", title: "Out" } }] },
    ] } };
    const pageSource = { resolve: async (id: string) => ({ id, title: id === "out" ? "Odchozí článek" : "Onboarding IT", url: `#/pages/${id}` }) };
    const { api, get } = fakeApi(async () => connections(sample, 3, [...similar, here("r-4", "Tiskárny"), here("p-2", "Práce z domova", "space-2", "Lidé a HR")]));
    const app = await show(api, { layout: "list", document, pageSource, spaceId: "space-1" });
    expect(get).toHaveBeenCalledWith("page-9", LINKS_HERE_MORE, expect.any(AbortSignal), false);
    const section = app.get("[data-connections-list]");
    expect(section.get("h2").text()).toBe("Odtud dál");
    const cards = app.findAll(".connections-list-card");
    expect(cards.map(card => card.get(".connections-list-title").text())).toEqual(["Onboarding IT", "Práce z domova", "Bezpečnostní zásady", "Odchozí článek", "Certifikáty zařízení", "Wi-Fi pro hosty"]);
    expect(cards.map(card => card.get("small").text().split(" · ")[0])).toEqual(["Vzájemné odkazy", "Odkazuje sem", "Odkazuje sem", "Odkaz z textu", "Podobné tématem", "Podobné tématem"]);
    // The space is named only for pages of another space; a page that is both linked and similar is listed once.
    expect(cards[1]!.text()).toContain("Lidé a HR");
    expect(cards[0]!.text()).not.toContain("IT a infrastruktura");
    // A title cut by the narrow column stays readable as a tooltip.
    expect(cards[2]!.get(".connections-list-title").attributes("title")).toBe("Bezpečnostní zásady");
    // The relation line (it carries a long space name) is cut by the column too, so it has its full text as a tooltip.
    expect(cards[1]!.get("small").attributes("title")).toBe("Odkazuje sem · Lidé a HR");
    expect(cards[0]!.get("small").attributes("title")).toBe("Vzájemné odkazy");
    await app.get(".connections-list-more").trigger("click");
    await flushPromises();
    expect(app.findAll(".connections-list-card").map(card => card.get(".connections-list-title").text()).slice(6)).toEqual(["Vzdálená plocha", "Tiskárny"]);
    expect(app.find(".connections-list-more").exists()).toBe(false);
  });

  it("holds its place with a skeleton until the first answer (F2), so nothing below it is pushed down", async () => {
    let answer!: (value: Connections) => void;
    wrapper = mount(PageConnections, { props: { pageId: "a", api: fakeApi(() => new Promise<Connections>(resolve => { answer = resolve; })).api, layout: "list" }, attachTo: document.body });
    await flushPromises();
    expect(wrapper.get("[data-connections-list] h2").text()).toBe("Odtud dál");
    expect(wrapper.find("[data-connections-list] .loading-skeleton").exists()).toBe(true);
    answer(connections([here("a-1", "Odkaz na A")]));
    await flushPromises();
    expect(wrapper.find("[data-connections-list] .loading-skeleton").exists()).toBe(false);
    expect(titles(wrapper)).toEqual(["Odkaz na A"]);
  });

  it("says plainly that nothing connects yet, and stays hidden when the load fails", async () => {
    const empty = await show(fakeApi(async () => connections([])).api, { layout: "list" });
    expect(empty.get("[data-connections-empty]").text()).toContain("Žádná stránka sem neodkazuje");
    empty.unmount(); wrapper = undefined;
    const failed = await show(fakeApi(async () => { throw new Error("down"); }).api, { layout: "list" });
    expect(failed.find("[data-connections-list]").exists()).toBe(false);
  });
});

describe("PageConnections places beyond links (GET /api/objects/{id}/relations)", () => {
  const node = (id: string, typeId: string, title: string | null, readable = true) => ({ id, typeId, title, spaceId: "space-1", spaceName: "IT a infrastruktura", readable });
  const graph = {
    nodes: [node("page-9", "atlas.page", "Tento článek"), node("b-1", "atlas.board", "Plán **vydání**"), node("c-1", "atlas.contact", "Jana Nováková"), node("b-2", "atlas.board", "Tabule nasazení"), node("x", "atlas.board", null, false)],
    edges: [{ sourceId: "page-9", targetId: "b-1", kind: "placement", count: 1 }, { sourceId: "page-9", targetId: "c-1", kind: "mention", count: 1 }, { sourceId: "b-2", targetId: "page-9", kind: "attached", count: 1 },
      { sourceId: "page-9", targetId: "x", kind: "placement", count: 1 }, { sourceId: "page-9", targetId: "p-1", kind: "link", count: 1 }],
  };
  const withRelations = (relations: PageConnectionsApi["relations"]) => ({ get: vi.fn(async () => connections(sample, 3)), relations } as PageConnectionsApi);

  it("lists the boards that show the page, its mentioned contacts and its deployment board, as names or links, and never the unreadable one", async () => {
    const app = await show(withRelations(async () => graph as never), { layout: "list" });
    const cards = app.findAll(".connections-list-card");
    expect(cards.map(card => card.get(".connections-list-title").text())).toEqual(expect.arrayContaining(["Plán vydání", "Jana Nováková", "Tabule nasazení"]));
    expect(cards.map(card => card.get(".connections-list-title").text())).not.toContain("x");
    const byTitle = (title: string) => cards.find(card => card.get(".connections-list-title").text() === title)!;
    expect(byTitle("Plán vydání").element.tagName).toBe("A");
    expect(byTitle("Plán vydání").get("small").text()).toContain("je na tabuli");
    expect(byTitle("Jana Nováková").element.tagName).toBe("SPAN");
  });

  it("keeps the links when the relations fail", async () => {
    const app = await show(withRelations(async () => { throw new Error("down"); }), { layout: "list" });
    expect(titles(app)).toEqual(["Onboarding IT", "Práce z domova", "Bezpečnostní zásady"]);
  });
});
