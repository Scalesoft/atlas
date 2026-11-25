import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import UsagePanel from "./UsagePanel.vue";
import { atlasObjectDragType } from "../../boards/boardDrop";
import { memoryUsage, type MemoryObject } from "../../objects/memoryRelations";
import type { RelationEdge, UsageApi } from "../../objects/relationsApi";

vi.mock("../../api", async importOriginal => ({ ...await importOriginal<typeof import("../../api")>(), api: vi.fn(async () => []) }));

const object = (id: string, typeId: string, title: string, extra: Partial<MemoryObject> = {}): MemoryObject => ({ id, typeId, title, spaceId: "s", spaceName: "Provoz", readable: true, ...extra });
const jana = "11111111-1111-4111-8111-111111111111", handbook = "22222222-2222-4222-8222-222222222222", plan = "33333333-3333-4333-8333-333333333333",
  budget = "44444444-4444-4444-8444-444444444444", secret = "55555555-5555-4555-8555-555555555555";
const objects = [object(jana, "atlas.contact", "Jana"), object(handbook, "atlas.page", "Příručka"), object(plan, "atlas.board", "Plán"),
  object(budget, "atlas.page", "Rozpočet", { spaceId: "f", spaceName: "Finance" }), object(secret, "atlas.page", "", { readable: false })];
const edges: RelationEdge[] = [
  { sourceId: handbook, targetId: jana, kind: "mention", count: 1, excerpt: "Za vydání odpovídá Jana" },
  { sourceId: handbook, targetId: jana, kind: "reference", count: 1, partId: "node-1" },
  { sourceId: jana, targetId: plan, kind: "placement", count: 2, partId: "card-1" },
  { sourceId: budget, targetId: jana, kind: "mention", count: 1, draft: true, card: true, excerpt: "Jana · PM projektu" },
  { sourceId: secret, targetId: jana, kind: "mention", count: 1 },
];
const api = memoryUsage(objects, edges);
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; vi.unstubAllGlobals(); });
async function show(props: Record<string, unknown> = {}, usage: UsageApi = api) {
  wrapper = mount(UsagePanel, { props: { objectId: jana, api: usage, ...props }, attachTo: document.body });
  await flushPromises();
  return wrapper;
}

it("groups the places by space, says how and why each is used, and counts the hidden ones without a name", async () => {
  const panel = await show();
  expect(panel.findAll(".usage-space h4").map(h => h.text())).toEqual(["Finance", "Provoz"]);
  const places = panel.findAll("[data-usage-place]");
  expect(places.map(place => place.find(".usage-title").text())).toEqual(["Rozpočet", "Plán", "Příručka"]);
  expect(places[2]!.findAll(".usage-kind").map(kind => kind.text())).toEqual(["zmínka", "odkaz ve stromu"]);
  expect(places[2]!.find(".usage-excerpt").text()).toBe("Za vydání odpovídá Jana");
  expect(places[1]!.text()).toContain("karta na tabuli ×2");
  expect(places[0]!.find("[data-usage-draft]").text()).toBe("koncept");
  // A contact card reads as a card, with that person's card as the excerpt.
  expect(places[0]!.find(".usage-kind").text()).toMatch(/^vizitka/);
  expect(places[0]!.find(".usage-excerpt").text()).toBe("Jana · PM projektu");
  expect(panel.find(".usage-summary").text()).toBe("Použito na 5 místech");
  expect(panel.get("[data-usage-hidden]").text()).toBe("A ještě na 1 místě, ke kterému nemáte přístup.");
  expect(panel.text()).not.toContain("Nedostupné");
});

it("walks the places with the arrows and opens one with Enter, with the card it is about", async () => {
  const panel = await show();
  const places = panel.findAll("[data-usage-place]").map(place => place.element as HTMLElement);
  expect(places.map(place => place.tabIndex)).toEqual([0, 0, 0]);
  places[0]!.focus();
  await panel.get(".usage-list").trigger("keydown", { key: "ArrowDown" });
  expect(document.activeElement).toBe(places[1]);
  await panel.get(".usage-list").trigger("keydown", { key: "End" });
  expect(document.activeElement).toBe(places[2]);
  await panel.findAll("[data-usage-place]")[1]!.trigger("keydown", { key: "Enter" });
  expect(panel.emitted("open")).toEqual([[plan, "atlas.board", "card-1"]]);
  const data = new Map<string, string>();
  await panel.findAll("[data-usage-place]")[2]!.trigger("dragstart", { dataTransfer: { setData: (type: string, value: string) => data.set(type, value) } });
  expect(JSON.parse(data.get(atlasObjectDragType)!)).toMatchObject({ id: handbook, typeId: "atlas.page", title: "Příručka" });
});

// The trash confirmations list where the object is used: plain rows, nothing to open, focus or drag out of a dialog.
it("still places only inform", async () => {
  const panel = await show({ still: true });
  const place = panel.get("[data-usage-place]");
  expect(place.attributes("role")).toBeUndefined();
  expect(place.attributes("tabindex")).toBeUndefined();
  expect(place.attributes("draggable")).toBe("false");
  await place.trigger("click");
  expect(panel.emitted("open")).toBeUndefined();
});

it("collapsed it asks only for the count and reads the places when unfolded", async () => {
  const usage = { usage: vi.fn(api.usage), count: vi.fn(api.count) };
  const panel = await show({ collapsible: true }, usage);
  expect(panel.get("[data-usage-toggle]").text()).toBe("Použito na 5 místech");
  expect(usage.usage).not.toHaveBeenCalled();
  expect(panel.find("[data-usage-place]").exists()).toBe(false);
  await panel.get("[data-usage-toggle]").trigger("click");
  await flushPromises();
  expect(usage.usage).toHaveBeenCalledOnce();
  expect(panel.get("[data-usage-toggle]").attributes("aria-expanded")).toBe("true");
  expect(panel.findAll("[data-usage-place]")).toHaveLength(3);
});

// A list mounts one collapsed panel per row (contacts, templates): a row out of view asks for nothing.
it("collapsed it asks for the count only once the row comes into view", async () => {
  // The page's one proximity observer (observeMarkdownProximity) watches the row and hydrates it in an idle batch.
  let seen: ((entries: { target: Element; isIntersecting: boolean }[]) => void) | undefined, watched: Element | undefined;
  vi.stubGlobal("IntersectionObserver", class { constructor(callback: typeof seen) { seen = callback; } observe(element: Element) { watched = element; } unobserve() {} disconnect() {} });
  const usage = { usage: vi.fn(api.usage), count: vi.fn(api.count) };
  const panel = await show({ collapsible: true }, usage);
  expect(usage.count).not.toHaveBeenCalled();
  expect(panel.get("[data-usage-toggle]").text()).toBe("Použito na…");
  seen!([{ target: watched!, isIntersecting: true }]);
  await new Promise(resolve => setTimeout(resolve, 5));
  await flushPromises();
  expect(usage.count).toHaveBeenCalledOnce();
  expect(panel.get("[data-usage-toggle]").text()).toBe("Použito na 5 místech");
});

it("pages on with Načíst další místa, says Nikde nepoužito, and an unreadable object is no error", async () => {
  const many = memoryUsage([object(jana, "atlas.contact", "Jana"), ...Array.from({ length: 3 }, (_, i) => object(`p${i}`, "atlas.page", `Stránka ${i}`))],
    Array.from({ length: 3 }, (_, i) => ({ sourceId: `p${i}`, targetId: jana, kind: "link" as const, count: 1 })), { pageSize: 2 });
  const panel = await show({}, many);
  expect(panel.findAll("[data-usage-place]")).toHaveLength(2);
  await panel.get("[data-usage-more]").trigger("click");
  await flushPromises();
  expect(panel.findAll("[data-usage-place]")).toHaveLength(3);
  expect(panel.find("[data-usage-more]").exists()).toBe(false);
  wrapper!.unmount();
  expect((await show({ objectId: plan })).text()).toContain("Nikde nepoužito");
  wrapper!.unmount();
  expect((await show({ objectId: secret })).text()).toContain("Použití není k dispozici");
});

it("offers „Přidat do stromu…“ in the row menu, so a place reaches the tree without dragging", async () => {
  const panel = await show();
  const place = panel.findAll("[data-usage-place]")[2]!.element as HTMLElement;
  place.focus();
  place.dispatchEvent(new KeyboardEvent("keydown", { key: "F10", shiftKey: true, bubbles: true, cancelable: true }));
  await flushPromises();
  const item = [...document.querySelectorAll<HTMLElement>("[data-row-action]")].find(entry => entry.textContent?.trim() === "Přidat do stromu…");
  expect(item).toBeTruthy();
  item!.click();
  await flushPromises();
  expect(document.body.textContent).toContain("Přidat do stromu");
});
