import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import LibraryPage, { type LibraryPart } from "./LibraryPage.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });
const render = (view: LibraryPart, canCreate = true) => (wrapper = mount(LibraryPage, { props: { view, spaceCount: 3, canCreate } }));

it("offers Nový prostor on Prostory only, the other parts have their own create actions", async () => {
  const view = render("spaces");
  expect(view.find("[data-space-create]").exists()).toBe(true);
  for (const part of ["personal", "shared", "recent", "contacts", "notifications"] as const) {
    await view.setProps({ view: part });
    expect(view.find("[data-space-create]").exists(), part).toBe(false);
  }
});

it("never offers Nový prostor to someone who cannot create a space", () => {
  expect(render("spaces", false).find("[data-space-create]").exists()).toBe(false);
});

it("marks the tab row that overflows, so its edges can fade", async () => {
  const view = render("spaces");
  const row = view.get(".library-tabs").element as HTMLElement;
  Object.defineProperty(row, "scrollWidth", { configurable: true, value: 873 });
  Object.defineProperty(row, "clientWidth", { configurable: true, value: 531 });
  row.dispatchEvent(new Event("scroll"));
  await new Promise(resolve => requestAnimationFrame(() => resolve(undefined)));
  expect(row.hasAttribute("data-more-after")).toBe(true);
  expect(row.hasAttribute("data-more-before")).toBe(false);
});
