import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { BoardLinksApi } from "../../../boards/boardLinks";
import { waitingOfPage } from "../../../boards/boardLinks";
import { liveObjects } from "../../../objects/liveObjects";
import type { ContentRow, RelationGraph, UsagePage } from "../../../objects/relationsApi";
import { createFakeLiveFeed } from "../../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../../live/liveFeed";
import BoardLinks from "./BoardLinks.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.useRealTimers(); vi.restoreAllMocks(); document.body.replaceChildren(); });

const row = (id: string, title: string, usedCount: number, typeId = "atlas.contact"): ContentRow =>
  ({ id, typeId, title, path: "", scope: "wiki", spaceId: "s", usedCount, linksIn: 0, linksOut: 0, comments: 0, updatedAt: "2026-10-31", canEdit: false });
const summary = { types: {}, relations: { unused: 0, noIncoming: 0, broken: 0, attached: 0 } };

/** A fake of the relation reads: what the server answers for this reader (it already left out what they may not read). */
function fakeApi(over: { contents?: ContentRow[]; graph?: RelationGraph | null; usage?: UsagePage | null } = {}) {
  const contents = vi.fn(async (_space: string, query: { type?: string; q?: string }) => ({
    items: (over.contents ?? []).filter(item => !query.type || item.typeId === query.type).filter(item => !query.q || item.title.toLowerCase().includes(query.q.toLowerCase())),
    summary, nextCursor: null,
  }));
  const api: BoardLinksApi = {
    relations: { contents, relations: vi.fn(async () => over.graph ?? null) },
    usage: { usage: vi.fn(async () => over.usage ?? null), count: vi.fn(async () => null) },
    waiting: { waiting: vi.fn(async () => ({ parties: [] })), contacts: vi.fn(async () => new Map()) },
  };
  return { api, contents };
}
/** /hubs/live as the list hears it: the scope page's channel. */
let feed = createFakeLiveFeed();
const show = (props: Record<string, unknown>) => {
  feed = createFakeLiveFeed();
  return (wrapper = mount(BoardLinks, { attachTo: document.body, props: { show: "people", spaceId: "s", ...props }, global: { provide: { [liveFeedSourceKey as symbol]: feed.source } } }));
};
const names = () => wrapper!.findAll("[data-person]").map(person => person.text());

it("lists the space's people by their uses, leaves out the unused, and opens one by its row", async () => {
  const { api, contents } = fakeApi({ contents: [row("c1", "Jana Nováková", 7), row("c2", "**Petr** Svoboda", 3), row("c3", "Nikdo Nepoužitý", 0), row("p1", "Stránka", 9, "atlas.page")] });
  show({ api });
  await flushPromises();
  expect(contents).toHaveBeenCalledWith("s", { type: "atlas.contact", sort: "used" }, expect.anything());
  expect(names()).toEqual(["JNJana Nováková7×", "PSPetr Svoboda3×"]);
  await wrapper!.get("[data-person=c2]").trigger("click");
  expect(wrapper!.emitted("open")).toEqual([["c2", "atlas.contact"]]);
});

it("lists one page's people from its relations: only readable contacts it names, most named first", async () => {
  const graph: RelationGraph = {
    nodes: [
      { id: "page", typeId: "atlas.page", title: "Projekt", spaceId: "s", spaceName: "S", readable: true },
      { id: "c1", typeId: "atlas.contact", title: "Jana", spaceId: "s", spaceName: "S", readable: true },
      { id: "c2", typeId: "atlas.contact", title: "Petr", spaceId: "s", spaceName: "S", readable: true },
      { id: "c3", typeId: "atlas.contact", title: null, spaceId: "s", spaceName: "S", readable: false },
      { id: "b1", typeId: "atlas.board", title: "Tabule", spaceId: "s", spaceName: "S", readable: true },
    ],
    edges: [
      { sourceId: "page", targetId: "c1", kind: "mention", count: 1 },
      { sourceId: "page", targetId: "c2", kind: "mention", count: 2 },
      { sourceId: "page", targetId: "c2", kind: "party", count: 1 },
      { sourceId: "page", targetId: "c3", kind: "mention", count: 5 },
      { sourceId: "page", targetId: "b1", kind: "link", count: 1 },
      { sourceId: "c1", targetId: "page", kind: "link", count: 1 },
    ],
  };
  const { api } = fakeApi({ graph });
  vi.spyOn(liveObjects, "load").mockResolvedValue(undefined);
  show({ api, objectId: "page" });
  await flushPromises();
  expect(names()).toEqual(["PPetr3×", "JJana1×"]);
});

it("says how people appear when the reader may read none of them (rights filtered on the server) or the page is gone", async () => {
  show({ api: fakeApi().api });
  await flushPromises();
  expect(wrapper!.text()).toContain("Zatím tu nikdo není");
  expect(wrapper!.text()).toContain("Napište do stránek prostoru jméno člověka");
  wrapper!.unmount();
  vi.spyOn(liveObjects, "load").mockResolvedValue(undefined);
  show({ api: fakeApi({ graph: null }).api, objectId: "gone" });
  await flushPromises();
  expect(wrapper!.text()).toContain("Zatím tu nikdo není");
});

it("reads again when the tab comes back, and keeps the rows on screen meanwhile", async () => {
  const people = [row("c1", "Jana Nováková", 7)];
  const { api, contents } = fakeApi({ contents: people });
  show({ api });
  await flushPromises();
  expect(contents).toHaveBeenCalledTimes(1);
  people.push(row("c2", "Petr Svoboda", 2));
  let answer!: () => void;
  contents.mockImplementationOnce(() => new Promise(resolve => { answer = () => resolve({ items: people, summary, nextCursor: null }); }));
  document.dispatchEvent(new Event("visibilitychange"));
  await flushPromises();
  expect(contents).toHaveBeenCalledTimes(2);
  expect(names()).toEqual(["JNJana Nováková7×"]);
  answer();
  await flushPromises();
  expect(names()).toEqual(["JNJana Nováková7×", "PSPetr Svoboda2×"]);
});

it("reads again when the scope's page changes or the tab comes back, never on a timer", async () => {
  vi.useFakeTimers();
  const { api, contents } = fakeApi({ contents: [row("c1", "Jana", 1)] });
  show({ api });
  await flushPromises();
  // A whole-space scope has no channel.
  expect(feed.count()).toBe(0);
  await vi.advanceTimersByTimeAsync(10 * 60_000);
  expect(contents).toHaveBeenCalledTimes(1);
  document.dispatchEvent(new Event("visibilitychange"));
  await flushPromises();
  expect(contents).toHaveBeenCalledTimes(2);

  wrapper!.unmount();
  vi.spyOn(liveObjects, "load").mockResolvedValue(undefined);
  const page = fakeApi();
  show({ api: page.api, objectId: "P1" });
  await flushPromises();
  expect(feed.count("page:p1")).toBe(1);
  const reads = vi.mocked(page.api.relations.relations).mock.calls.length;
  feed.emit("page:p1", { pageId: "P1", kind: "published" });
  await flushPromises();
  expect(page.api.relations.relations).toHaveBeenCalledTimes(reads + 1);
  await vi.advanceTimersByTimeAsync(10 * 60_000);
  expect(page.api.relations.relations).toHaveBeenCalledTimes(reads + 1);
});

it("chooses its scope in place from the keyboard: the search has the focus, Enter takes the first result, Esc ends the editing", async () => {
  const { api, contents } = fakeApi({ contents: [row("p1", "Projekt Alfa", 2, "atlas.page"), row("p2", "Projekt Beta", 1, "atlas.page"), row("c1", "Jana", 3)] });
  show({ api, editing: true });
  await flushPromises();
  const input = wrapper!.get<HTMLInputElement>("[data-board-links-scope] input");
  expect(document.activeElement).toBe(input.element);
  // The space first, then its pages (people and waiting show a space or one of its pages).
  expect(wrapper!.findAll(".searchable-item").map(option => option.find(".searchable-title").text())).toEqual(["Celý prostor", "Projekt Alfa", "Projekt Beta"]);
  expect(contents).toHaveBeenLastCalledWith("s", { type: "atlas.page", q: "", sort: "used" }, expect.anything());

  vi.useFakeTimers();
  await input.setValue("beta");
  await vi.advanceTimersByTimeAsync(200);
  vi.useRealTimers();
  await flushPromises();
  expect(wrapper!.findAll(".searchable-item").map(option => option.find(".searchable-title").text())).toEqual(["Projekt Beta"]);
  await input.trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("scope")).toEqual([["p2"]]);
  expect(wrapper!.emitted("done")).toHaveLength(1);

  // The first Esc empties the search, the next one ends the editing without a change.
  await input.setValue("x");
  await input.trigger("keydown", { key: "Escape" });
  expect(input.element.value).toBe("");
  expect(wrapper!.emitted("done")).toHaveLength(1);
  await input.trigger("keydown", { key: "Escape" });
  expect(wrapper!.emitted("done")).toHaveLength(2);
  expect(wrapper!.emitted("scope")).toHaveLength(1);
});

it("ends the editing on a click outside, without a change", async () => {
  show({ api: fakeApi().api, editing: true });
  await flushPromises();
  document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
  expect(wrapper!.emitted("done")).toHaveLength(1);
  expect(wrapper!.emitted("scope")).toBeUndefined();
});

it("asks what to follow before it shows uses, then shows the object's uses and opens a place", async () => {
  const { api } = fakeApi({ contents: [row("c1", "Jana", 3)] });
  show({ api, show: "usage" });
  expect(wrapper!.text()).toContain("Vyberte, co sledovat");
  wrapper!.unmount();
  const usage: UsagePage = { objectId: "c1", total: 3, hidden: 1, nextCursor: null, items: [
    { placeId: "p1", placeTypeId: "atlas.page", placeTitle: "Projekt Alfa", spaceId: "s", spaceName: "Provoz", kind: "mention", count: 1, draft: false },
    { placeId: "b1", placeTypeId: "atlas.board", placeTitle: "Plán", spaceId: "s", spaceName: "Provoz", kind: "placement", count: 1, draft: false },
  ] };
  vi.spyOn(liveObjects, "load").mockResolvedValue(undefined);
  show({ api: fakeApi({ usage, contents: [row("c1", "Jana", 3)] }).api, show: "usage", objectId: "c1" });
  await flushPromises();
  expect(wrapper!.findAll("[data-usage-place]").map(place => place.find(".usage-title").text())).toEqual(["Projekt Alfa", "Plán"]);
  // A place the reader may not see is a number, never a name.
  expect(wrapper!.get("[data-usage-hidden]").text()).toContain("1 místě");
  await wrapper!.get("[data-usage-place][data-type='atlas.board']").trigger("click");
  expect(wrapper!.emitted("open")).toEqual([["b1", "atlas.board"]]);
  // The scope picker for usage offers every type, without „Celý prostor“.
  await wrapper!.setProps({ editing: true });
  await flushPromises();
  expect(wrapper!.findAll(".searchable-item").map(option => option.find(".searchable-title").text())).toEqual(["Jana"]);
});

it("names „Na co čekáme“ once, in the card's head, not again by the list inside", async () => {
  show({ show: "waiting", api: fakeApi().api });
  await flushPromises();
  expect(wrapper!.text().split("Na co čekáme")).toHaveLength(2);
  expect(wrapper!.find("#waiting-list-heading").exists()).toBe(false);
});

it("keeps one page's waiting items, a party without any drops out", () => {
  const item = (pageId: string, blockId: string) => ({ pageId, pageTitle: "P", blockId, key: blockId, title: blockId, direction: "inbound" as never, state: "waiting_for_input" as never, date: "2026-10-01" });
  expect(waitingOfPage({ parties: [{ party: "Jana", items: [item("A", "1"), item("B", "2")] }, { party: "Petr", items: [item("B", "3")] }] }, "a")).toEqual({ parties: [{ party: "Jana", items: [item("A", "1")] }] });
});
