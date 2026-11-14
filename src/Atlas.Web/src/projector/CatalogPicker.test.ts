import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import CatalogPicker from "./CatalogPicker.vue";
import { catalogModules } from "./catalogSelection";
import type { CatalogDefinition } from "./projectorApi";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const definition = (key: string, title: string): CatalogDefinition => ({ key, title, titleMarkdown: title, direction: "inbound", requires: [], pageId: "cat", bundle: "Core" });
const definitions = [definition("portal.vpn", "VPN přístup pro správu serveru"), definition("portal.orders.tested", "Objednávky otestovány")];

it("searches for what was typed while it opened and inserts the first match for the Enter after it", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions, modules: catalogModules(definitions), selected: [], label: "Katalog",
    typeAhead: { take: () => ({ text: "vpn", enter: true }) } } });
  await nextTick();
  await nextTick();
  expect(wrapper.get<HTMLInputElement>("[data-catalog-query]").element.value).toBe("vpn");
  expect(wrapper.emitted("submit")).toEqual([[false, ["portal.vpn"]]]);
});

// P8: Enter takes the highlighted row once something is typed or the arrows moved to it, never from a list nobody has touched.
it("inserts nothing on Enter in the untouched empty search, the row the arrows moved to once they did, and empties the search with the first Esc", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions, modules: catalogModules(definitions), selected: [], label: "Katalog" } });
  await nextTick();
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  await input.trigger("keydown", { key: "Enter" });
  expect(wrapper.emitted("submit")).toBeUndefined();
  // No row looks chosen while Enter would take none, and a Space there ticks nothing; the first arrow lands on the first row.
  expect(wrapper.find("[data-active]").exists()).toBe(false);
  expect(input.attributes("aria-activedescendant")).toBeUndefined();
  const space = new KeyboardEvent("keydown", { key: " ", bubbles: true, cancelable: true });
  input.element.dispatchEvent(space);
  expect(space.defaultPrevented).toBe(false);
  await input.trigger("keydown", { key: "ArrowDown" });
  expect(wrapper.get("[data-active]").attributes("data-catalog-row")).toBe(wrapper.get("[data-catalog-row]").attributes("data-catalog-row"));
  expect(input.attributes("aria-activedescendant")).toBeDefined();
  await input.trigger("keydown", { key: "Enter" });
  expect(wrapper.emitted("submit")).toHaveLength(1);
  await input.setValue("vpn");
  const escape = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
  input.element.dispatchEvent(escape);
  await nextTick();
  expect(input.element.value).toBe("");
  expect(escape.defaultPrevented).toBe(true);
  const second = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
  input.element.dispatchEvent(second);
  expect(second.defaultPrevented).toBe(false);
});

it("shows a skeleton of the rows to come, not a bare line, while the catalog loads", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: [], modules: [], selected: [], label: "Katalog", status: "loading" } });
  await nextTick();
  expect(wrapper.find(".loading-skeleton").exists()).toBe(true);
  expect(wrapper.get(".loading-skeleton").text()).toContain("Načítání katalogu…");
});

// PR-7 (UX cycle 1): „Celý modul Core (2) · 2 požadavky“ while one of them is on the page already read as two more to come.
it("says of a module partly on the page how much of it is there", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions, modules: catalogModules(definitions), selected: [], label: "Katalog", present: new Set(["portal.vpn"]) } });
  await nextTick();
  const module = wrapper.findAll("[data-catalog-row]").find(row => row.attributes("data-catalog-row")!.startsWith("module:"))!;
  expect(module.text()).toContain("část už je na stránce (1/2)");
  expect(module.text()).not.toContain("2 požadavky");
});

it("takes the keys while the catalog loads and inserts the first match of what was typed once it is there", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: [], modules: [], selected: [], label: "Katalog", status: "loading" } });
  await nextTick();
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  expect(document.activeElement).toBe(input.element);
  await input.setValue("otestov objedn");
  await input.trigger("keydown", { key: "Enter" });
  expect(wrapper.emitted("submit")).toBeUndefined();
  await wrapper.setProps({ definitions, modules: catalogModules(definitions), status: "ready" });
  await nextTick();
  expect(wrapper.emitted("submit")).toEqual([[false, ["portal.orders.tested"]]]);
});

// PR2-2 (UX cycle 2): Ctrl+A pressed before the catalog came was lost, and the Enter after it then made the deployment of the first row only.
it("ticks every row for a Ctrl+A pressed while the catalog loads, so the Enter after it takes them all", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: [], modules: [], selected: [], label: "Katalog", status: "loading",
    "onUpdate:selected": (keys: string[]) => wrapper!.setProps({ selected: keys }) } });
  await nextTick();
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  await input.trigger("keydown", { key: "a", ctrlKey: true });
  await input.trigger("keydown", { key: "Enter" });
  const two = [definitions[0]!, { ...definitions[1]!, bundle: "Objednávky" }]; // two modules: the highlighted row alone is the first of them
  await wrapper.setProps({ definitions: two, modules: catalogModules(two), status: "ready" });
  await nextTick();
  await nextTick();
  expect(wrapper.emitted("submit")).toEqual([[false, ["portal.vpn", "portal.orders.tested"]]]);
});

// Categories: modules stand under their heading, a search finds the heading's name and shows the whole category, the arrows skip it.
const categorized = [
  { ...definition("portal.vpn", "VPN"), bundle: "Core", category: "Jádro" },
  { ...definition("mpi.login", "Přihlášení"), bundle: "NIA", category: "Autentizace" },
  { ...definition("mpi.sms", "SMS brána"), bundle: "Bankovní identita", category: "Autentizace" },
];
const headings = (view: VueWrapper) => view.findAll("[data-catalog-category]").map(item => item.text());

it("groups the modules under the headings of their categories in catalog order, and finds a category by its name", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: categorized, modules: catalogModules(categorized), modulesOnly: true, selected: [], label: "Katalog" } });
  await nextTick();
  expect(headings(wrapper)).toEqual(["Jádro", "Autentizace"]);
  expect(wrapper.findAll("[data-catalog-row]")).toHaveLength(3);
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  await input.setValue("autent");
  expect(headings(wrapper)).toEqual(["Autentizace"]);
  expect(wrapper.findAll("[data-catalog-row]").map(row => row.text())).toEqual([expect.stringContaining("NIA"), expect.stringContaining("Bankovní identita")]);
  expect(wrapper.get("[data-active]").attributes("data-catalog-row")).toContain("module:");
  await input.setValue("jádro");
  expect(headings(wrapper)).toEqual(["Jádro"]);
  expect(wrapper.findAll("[data-catalog-row]")).toHaveLength(1);
});

it("lets the arrows skip a heading, which is no row, and shows the category whole beside the requirements", async () => {
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: categorized, modules: catalogModules(categorized), selected: [], label: "Katalog" } });
  await nextTick();
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  await input.setValue("autent");
  expect(headings(wrapper)).toEqual(["Autentizace"]);
  expect(wrapper.findAll("[data-catalog-row]")).toHaveLength(4);
  await input.trigger("keydown", { key: "ArrowDown" });
  await input.trigger("keydown", { key: "ArrowDown" });
  expect(wrapper.findAll("[data-catalog-category]").every(item => !item.attributes("data-active") && item.attributes("aria-hidden") === "true")).toBe(true);
  // A screen reader hears the category as the name of its rows' group in the listbox.
  const group = wrapper.get("[role=listbox] > [role=group]");
  expect(group.attributes("aria-label")).toBe("Autentizace");
  expect(group.findAll("[role=option]")).toHaveLength(4);
  expect(wrapper.get("[data-active]").attributes("data-catalog-row")).toBe(wrapper.findAll("[data-catalog-row]")[2]!.attributes("data-catalog-row"));
});

// Plan 3.5 C: „Moduly produktu“: the modules under their product and category, whose rows tick them all, and a preview of the highlighted one.
it("as a tree ticks a whole category from its heading row and previews what the highlighted module brings and what the page has", async () => {
  const of = (key: string, title: string, bundle: string, category: string, requires: string[] = []): CatalogDefinition =>
    ({ key, title, titleMarkdown: title, direction: "inbound", requires, pageId: "cat", bundle, category, product: "Portál pacienta" });
  const tree = [of("portal.core", "Základ portálu", "Portál — Core", "Jádro"),
    { ...of("portal.web.terminy", "URL Termíny", "Webové objednávání", "Rezervace"), summary: "Objednání pacienta přes web nemocnice." },
    { ...of("portal.web", "Webové objednávání funguje", "Webové objednávání", "Rezervace", ["portal.web.terminy", "portal.core"]), summary: "Objednání pacienta přes web nemocnice." },
    of("portal.doctor", "Objednávání lékařem funguje", "Objednávání lékařem", "Rezervace", ["portal.web"])];
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: tree, modules: catalogModules(tree), selected: [], label: "Katalog", tree: true, modulesOnly: true,
    present: new Set(["portal.web.terminy"]), "onUpdate:selected": (keys: string[]) => wrapper!.setProps({ selected: keys }) } });
  await nextTick();
  const input = wrapper.get<HTMLInputElement>("[data-catalog-query]");
  await input.setValue("webov");
  // The first module found is highlighted; its preview says where it stands, what it is, what comes, what is there and what comes along
  // from another module, for which of its requirements (O7).
  expect(wrapper.get("[data-active]").attributes("data-catalog-row")).toMatch(/^module:/);
  expect(wrapper.get("[data-active] .catalog-picker-title").text()).toBe("Webové objednávání");
  const preview = wrapper.get("[data-catalog-preview]");
  expect(preview.get("[data-catalog-preview-path]").text()).toBe("Portál pacienta › Rezervace");
  expect(preview.get("[data-catalog-preview-summary]").text()).toBe("Objednání pacienta přes web nemocnice.");
  expect(preview.findAll("[data-catalog-preview-item]").map(item => item.attributes("data-status"))).toEqual(["present", "new"]);
  expect(preview.get("[data-catalog-preview-along]").text()).toContain("Portál — Core › Základ portálu — kvůli „Webové objednávání funguje“");
  // The category's row stands before its modules and ticks both.
  const rows = wrapper.findAll("[data-catalog-row]").map(row => row.attributes("data-catalog-row"));
  expect(rows.slice(0, 2)).toEqual(["product:Portál pacienta › ", "category:Portál pacienta › Rezervace"]);
  await wrapper.get('[data-catalog-row="category:Portál pacienta › Rezervace"]').trigger("click");
  expect(wrapper.emitted<string[][]>("update:selected")!.at(-1)![0]).toEqual(["portal.web", "portal.doctor"]);
  expect(wrapper.get('[data-catalog-row="product:Portál pacienta › "]').attributes("data-mixed")).toBeDefined();
});

// O2: one catalog entry; the tree lists modules, a search also the single requirements, each under its module, the best match highlighted.
it("as a tree finds a single requirement by a search and shows it under its module, the best match highlighted and marked in the preview", async () => {
  const of = (key: string, title: string, bundle: string): CatalogDefinition =>
    ({ key, title, titleMarkdown: title, direction: "inbound", requires: [], pageId: "cat", bundle, category: "Rezervace", product: "Portál pacienta" });
  const tree = [of("portal.web.terminy", "URL Termíny", "Webové objednávání"), of("portal.web", "Webové objednávání funguje", "Webové objednávání"),
    of("portal.doctor", "Objednávání lékařem funguje", "Objednávání lékařem")];
  wrapper = mount(CatalogPicker, { attachTo: document.body, props: { definitions: tree, modules: catalogModules(tree), selected: [], label: "Katalog", tree: true } });
  await nextTick();
  const titles = () => wrapper!.findAll("[data-catalog-row] .catalog-picker-title").map(title => title.text());
  expect(titles()).toEqual(["Portál pacienta", "Rezervace", "Webové objednávání", "Objednávání lékařem"]);
  await wrapper.get<HTMLInputElement>("[data-catalog-query]").setValue("terminy");
  expect(titles()).toEqual(["Portál pacienta", "Rezervace", "Webové objednávání", "URL Termíny"]);
  expect(wrapper.get("[data-active]").attributes("data-catalog-row")).toBe("portal.web.terminy");
  expect(wrapper.get("[data-catalog-preview] strong").text()).toBe("Webové objednávání");
  expect(wrapper.get("[data-catalog-preview-item][data-current]").text()).toContain("URL Termíny");
});
