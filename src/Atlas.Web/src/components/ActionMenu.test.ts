import { afterEach, describe, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { h } from "vue";
import ActionMenu from "./ActionMenu.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

const items = [{ id: "rect", label: "Obdélník", icon: "square" }, { id: "ellipse", label: "Elipsa", icon: "circle" }, { id: "off", label: "Nedostupné", disabled: true }];
function show(props: Record<string, unknown> = {}) {
  wrapper = mount(ActionMenu, { attachTo: document.body, props: { items, label: "Tvar", itemAttr: "data-board-shape", ...props },
    slots: { default: () => h("button", { type: "button", "data-trigger": "" }, "Tvar") } });
}
const menu = () => document.querySelector<HTMLElement>("[role=menu]");
const rows = () => [...document.querySelectorAll<HTMLElement>("[role=menu] [role^=menuitem]")];

describe("ActionMenu", () => {
  it("opens as the page editor's context menu, rows with their icons, the current choice checked", async () => {
    show({ open: true, value: "ellipse" });
    await flushPromises();
    expect(menu()!.classList).toContain("md-context-popover");
    expect(menu()!.classList).toContain("md-context-menu");
    expect(menu()!.getAttribute("aria-label")).toBe("Tvar");
    expect(rows().map(row => row.textContent)).toEqual(["Obdélník", "Elipsa", "Nedostupné"]);
    expect(rows().every(row => row.classList.contains("md-context-menu-item"))).toBe(true);
    expect(rows()[0]!.querySelector("svg")).not.toBeNull();
    expect(rows().map(row => row.getAttribute("aria-checked"))).toEqual(["false", "true", "false"]);
    expect(rows()[0]!.getAttribute("data-board-shape")).toBe("rect");
    // U12: the current choice is checked visibly too, as in the editor's menus.
    expect(rows().map(row => !!row.querySelector("[data-board-menu-check]"))).toEqual([false, true, false]);
  });

  it("reports the chosen row and closes; a disabled row does nothing; without a value the rows are plain items", async () => {
    show({ open: true, "onUpdate:open": (open: boolean) => wrapper!.setProps({ open }) });
    await flushPromises();
    expect(rows()[0]!.getAttribute("role")).toBe("menuitem");
    rows()[2]!.click();
    rows()[1]!.click();
    await flushPromises();
    expect(wrapper!.emitted("select")).toEqual([["ellipse"]]);
    expect(menu()).toBeNull();
  });
});
