import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AppearancePickerExample from "./AppearancePickerExample.vue";
import { appearanceIcons, quickAppearanceIconIds } from "../settings/appearanceIcons";
import { iconPackCount, loadIconPackCatalog } from "../../iconPack";

let wrapper: VueWrapper;
afterEach(() => wrapper.unmount());

// The gallery page must not load the icon pack by itself, and the closed picker has to stay the only state on show
// until someone asks for more (tests/browser/shared-controls-disclosure.spec.ts starts from it).
it("names the counts from the data and stages the icon browser only once the pack has been loaded", async () => {
  wrapper = mount(AppearancePickerExample, { attachTo: document.body });
  await flushPromises();
  const total = (appearanceIcons.length + iconPackCount).toLocaleString("cs-CZ");
  const intro = wrapper.get("p").text();
  expect(intro).toContain(`${quickAppearanceIconIds.length} `);
  expect(intro).toContain(`${appearanceIcons.length} `);
  expect(intro).toContain(`Další ikony (${total})`);
  expect(wrapper.text()).toContain("SearchableChoiceList");
  expect(wrapper.text()).not.toContain("Vlastní…");
  expect(wrapper.find("[data-appearance-search]").exists()).toBe(false);

  await loadIconPackCatalog();
  await flushPromises();
  // A query with results, best first, and a query that finds nothing.
  const searches = wrapper.findAll<HTMLInputElement>("[data-appearance-search]");
  expect(searches.map((search) => search.element.value)).toEqual(["databáze", expect.any(String)]);
  expect(wrapper.findAll("[data-appearance-results]")).toHaveLength(1);
  expect(wrapper.findAll("[data-appearance-results] [role=option]")[0]!.attributes("data-value")).toBe("database");
  expect(wrapper.findAll("[data-appearance-count]")[1]!.text()).toBe(`Nalezeno 0 z ${total}`);
  expect(wrapper.find(".appearance-picker [data-appearance-search]").exists()).toBe(false);
});
