import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SpaceSwitcher from "./SpaceSwitcher.vue";

const spaces = [
  { id: "provoz", name: "Provoz", icon: "workflow", color: "green" },
  { id: "vyvoj", name: "Vývoj", icon: "code-xml", color: "blue" },
  { id: "lide", name: "Lidé", icon: "compass", color: "amber" },
];

let switcher: VueWrapper | undefined;
afterEach(() => {
  switcher?.unmount();
  switcher = undefined;
  document.body.replaceChildren();
});

// The list is portalled out of the component, so it is read from the document.
const panel = () => document.querySelector<HTMLElement>('[data-slot="popover-content"]');
const listed = () => [...panel()!.querySelectorAll('[role="radio"]')].map((item) => item.getAttribute("data-value"));
const radio = (id: string) => panel()!.querySelector<HTMLElement>(`[role="radio"][data-value="${id}"]`)!;
const searchField = () => panel()!.querySelector<HTMLInputElement>('input[type="search"]')!;
const press = (id: string, key: string) => radio(id).dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
async function search(text: string) {
  searchField().value = text;
  searchField().dispatchEvent(new Event("input"));
  await flushPromises();
}
async function toggle() {
  await switcher!.get("[data-space-switcher]").trigger("click");
  await flushPromises();
}
async function open() {
  switcher = mount(SpaceSwitcher, { props: { current: spaces[0]!, spaces }, attachTo: document.body });
  await toggle();
  return switcher;
}

describe("SpaceSwitcher", () => {
  it("lists the managed spaces with the open one marked and narrows them by the search", async () => {
    const view = await open();
    const chip = view.get("button[data-space-switcher]");
    expect(chip.text()).toBe("Provoz");
    expect(chip.attributes("title")).toBe("Provoz");
    expect(listed()).toEqual(["provoz", "vyvoj", "lide"]);
    expect(radio("provoz").getAttribute("data-state")).toBe("checked");
    expect(radio("provoz").textContent).toContain("Otevřeno");
    expect(radio("vyvoj").textContent).not.toContain("Otevřeno");
    expect(searchField().placeholder).toBe("Hledat prostor");
    expect(searchField().getAttribute("aria-label")).toBe("Nastavení jiného prostoru: hledat");
    await search("vyv");
    expect(listed()).toEqual(["vyvoj"]);
  });

  it("says what the chip does and names its list, with the keys that work in it", async () => {
    const view = await open();
    const chip = view.get("button[data-space-switcher]");
    // The accessible name keeps what is visible in it, so the chip is still found by the space's name.
    expect(chip.attributes("aria-label")).toBe("Prostor Provoz – přepnout prostor");
    expect(chip.text()).toBe("Provoz");
    expect(panel()!.getAttribute("role")).toBe("dialog");
    expect(panel()!.getAttribute("aria-label")).toBe("Přepnout prostor");
    expect(panel()!.querySelector(".space-switcher-help")!.textContent).toBe("Šipkami vyberete, klávesou Enter otevřete.");
  });

  it("opens the space a pointer click picks and closes; the open space only closes", async () => {
    const view = await open();
    radio("vyvoj").dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
    radio("vyvoj").click();
    await flushPromises();
    expect(view.emitted("switch")).toEqual([["vyvoj"]]);
    expect(panel()).toBeNull();

    await toggle();
    radio("provoz").dispatchEvent(new MouseEvent("pointerdown", { bubbles: true }));
    radio("provoz").click();
    await flushPromises();
    expect(view.emitted("switch")).toHaveLength(1);
    expect(panel()).toBeNull();
  });

  it("only marks the space an arrow key lands on and opens it on Enter", async () => {
    const view = await open();
    // reka's RadioGroupItem clicks the item an arrow key moves the focus to; that click must not navigate.
    radio("provoz").focus();
    press("provoz", "ArrowDown");
    await vi.waitFor(() => expect(radio("vyvoj").getAttribute("data-state")).toBe("checked"));
    expect(view.emitted("switch")).toBeUndefined();
    expect(panel()).not.toBeNull();

    press("vyvoj", "Enter");
    await flushPromises();
    expect(view.emitted("switch")).toEqual([["vyvoj"]]);
    expect(panel()).toBeNull();

    // The shell may refuse the move (unsaved changes): the list then starts from the open space again.
    await toggle();
    expect(radio("provoz").getAttribute("data-state")).toBe("checked");
  });

  it("opens the item Enter is pressed on when the search has hidden the marked space", async () => {
    const view = await open();
    await search("vyv");
    // Tab from the search field lands on the only match without marking it.
    radio("vyvoj").focus();
    press("vyvoj", "Enter");
    await flushPromises();
    expect(view.emitted("switch")).toEqual([["vyvoj"]]);
  });

  it("is a plain label with one managed space and nothing with none", () => {
    const one = mount(SpaceSwitcher, { props: { current: spaces[0]!, spaces: [spaces[0]!] } });
    const label = one.get("[data-space-switcher]");
    expect(label.element.tagName).toBe("DIV");
    expect(label.text()).toBe("Provoz");
    expect(label.attributes("title")).toBe("Provoz");
    const none = mount(SpaceSwitcher, { props: { current: spaces[0]!, spaces: [] } });
    expect(none.html()).toBe("<!--v-if-->");
  });
});
