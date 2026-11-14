import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { entityApi, type EntityItem, type EntityList } from "../../entities/entityApi";
import ContactOverview from "./ContactOverview.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const jana: EntityItem = {
  identityKey: "email:jana@example.cz", kind: "contact", attributes: { name: "Jana Nováková", emails: ["jana@example.cz"] },
  sources: [
    { resourceId: "c1", resourceType: "atlas.contact", title: "Jana", spaceId: null, spaceName: null, anchor: null, revisionKind: "published" },
    { resourceId: "p1", resourceType: "atlas.page", title: "Příručka", spaceId: "s1", spaceName: "Provoz", anchor: "tym", revisionKind: "working" },
  ],
  sourceCount: 2,
};
const listOf = (items: EntityItem[]): EntityList => ({ items, total: items.length, page: 1, pageSize: 50 });

it("lists people at once with their places and switches the scope of a space to the whole wiki", async () => {
  const list = vi.fn(async () => listOf([jana]));
  wrapper = mount(ContactOverview, { attachTo: document.body, props: { spaceId: "s1", source: { list, exportUrl: entityApi.exportUrl } } });
  await flushPromises();
  expect(list).toHaveBeenCalledWith("contact", { scope: "space", id: "s1", q: "", page: 1 });
  const places = wrapper.findAll("[data-entity-place]");
  expect(places.map(place => place.text().split(":")[0])).toEqual(["Adresář", "Příručkarozpracováno"]);
  // „rozpracováno“ says what it means (UX1-14).
  expect(places[1]!.get(".contact-overview-draft").attributes("title")).toContain("pracovní verzi");
  expect(wrapper.findAll("[data-entity-scope] [data-value]").map(pill => pill.text())).toEqual(["Tento prostor", "Celá wiki"]);
  expect(places[0]!.attributes("href")).toBe("#/library/contacts");
  expect(places[1]!.attributes("href")).toContain("p1");
  expect(wrapper.get('[data-entity-export="vcard"]').attributes("href")).toBe("/api/entities/contact/export?scope=space&format=vcard&id=s1");

  await wrapper.get('[data-entity-scope] [data-value="instance"]').trigger("click");
  await vi.waitFor(() => expect(list).toHaveBeenLastCalledWith("contact", { scope: "instance", q: "", page: 1 }, expect.anything()));
});

it("goes from the search to the first place with ↓; Enter takes nothing from an empty search and opens that place once something is typed", async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
  const opened: string[] = [];
  const onClick = (event: Event) => { event.preventDefault(); opened.push((event.target as Element).closest("[data-entity-place]")?.getAttribute("href") ?? ""); };
  document.addEventListener("click", onClick, true);
  try {
    wrapper = mount(ContactOverview, { attachTo: document.body, props: { source: { list: vi.fn(async () => listOf([jana])), exportUrl: entityApi.exportUrl } } });
    await flushPromises();
    const search = wrapper.get<HTMLInputElement>("input[data-entity-search]");
    search.element.focus();
    await search.trigger("keydown", { key: "Enter" });
    expect(opened).toEqual([]);
    expect(document.activeElement).toBe(search.element);
    await search.trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement).toBe(wrapper.findAll("[data-entity-place]")[0]!.element);
    search.element.focus();
    await search.setValue("jana");
    await vi.advanceTimersByTimeAsync(300);
    await flushPromises();
    expect(wrapper.find("[data-entity-row][data-first]").exists()).toBe(true);
    await search.trigger("keydown", { key: "Enter" });
    expect(opened).toEqual(["#/library/contacts"]);
  } finally { document.removeEventListener("click", onClick, true); vi.useRealTimers(); }
});

// UX1-14: Knihovna and a space show the same overview with the same words; in Knihovna its Adresář is the directory slot.
it("shows the directory in Knihovna under the same switch and lists the whole wiki only once asked", async () => {
  const list = vi.fn(async () => listOf([jana]));
  wrapper = mount(ContactOverview, { attachTo: document.body, props: { source: { list, exportUrl: entityApi.exportUrl } },
    slots: { directory: '<p data-test-directory>Adresář</p>' } });
  await flushPromises();
  expect(wrapper.findAll("[data-entity-scope] [data-value]").map(pill => pill.text())).toEqual(["Adresář", "Celá wiki"]);
  expect(wrapper.find("[data-test-directory]").exists()).toBe(true);
  expect(wrapper.find("[data-entity-search]").exists()).toBe(false);
  expect(list).not.toHaveBeenCalled();
  expect(wrapper.get("section").attributes("aria-busy")).toBe("false");

  await wrapper.get('[data-entity-scope] [data-value="instance"]').trigger("click");
  await vi.waitFor(() => expect(wrapper!.findAll("[data-entity-row]")).toHaveLength(1));
  expect(list).toHaveBeenLastCalledWith("contact", { scope: "instance", q: "", page: 1 }, expect.anything());
  expect(wrapper.find("[data-test-directory]").exists()).toBe(false);
  expect(wrapper.find("[data-entity-export=\"csv\"]").exists()).toBe(true);
});

// PERF2-1: a person named in many pages lists its first five places and their count; the rest come on demand.
it("shows the count of every place and brings the rest on demand with the keyboard on the first new one", async () => {
  const place = (i: number) => ({ resourceId: `p${i}`, resourceType: "atlas.page", title: `Porada ${i}`, spaceId: "s1", spaceName: "Provoz", anchor: null, revisionKind: "published" as const });
  const eva: EntityItem = { identityKey: "email:eva@example.cz", kind: "contact", attributes: { name: "Eva" }, sources: [1, 2, 3, 4, 5].map(place), sourceCount: 7 };
  const list = vi.fn(async (_kind: string, query: { identity?: string }) =>
    listOf([query.identity ? { ...eva, sources: [1, 2, 3, 4, 5, 6, 7].map(place) } : eva]));
  wrapper = mount(ContactOverview, { attachTo: document.body, props: { source: { list, exportUrl: entityApi.exportUrl } } });
  await flushPromises();
  expect(wrapper.text()).toContain("Výskyty (7)");
  expect(wrapper.findAll("[data-entity-place]")).toHaveLength(5);
  await wrapper.get("[data-entity-all-places]").trigger("click");
  await flushPromises();
  expect(list).toHaveBeenLastCalledWith("contact", { scope: "instance", q: "", page: 1, identity: "email:eva@example.cz" });
  expect(wrapper.findAll("[data-entity-place]")).toHaveLength(7);
  expect(wrapper.find("[data-entity-all-places]").exists()).toBe(false);
  expect(document.activeElement).toBe(wrapper.findAll("[data-entity-place]")[5]!.element);
});
