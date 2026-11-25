import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import ActionMenu from "./ActionMenu.vue";
import AppDialog from "./AppDialog.vue";
import RowActionsMenu from "./RowActionsMenu.vue";

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

  it("stays inside a native modal dialog (its top layer) and Escape closes only the menu (Uspořádat › Koš)", async () => {
    const inside = defineComponent({ setup: () => { const open = ref(true); return () => h(ActionMenu, { items, label: "Řádek", itemAttr: "data-row-action", open: open.value, "onUpdate:open": (value: boolean) => { open.value = value; } },
      () => h("button", { type: "button" }, "Řádek")); } });
    wrapper = mount(AppDialog, { attachTo: document.body, props: { title: "Uspořádat", open: true }, slots: { default: () => h(inside) } });
    await flushPromises();
    const dialog = wrapper.get("dialog").element as HTMLDialogElement;
    expect(menu()!.closest("dialog")).toBe(dialog);
    menu()!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(document.querySelector('[role="menu"][data-state="open"]')).toBeNull();
    expect(dialog.open).toBe(true);
    expect(wrapper.emitted("close")).toBeUndefined();
  });

  it("is not modal: the first click outside closes it and still acts; a route change closes it too", async () => {
    const outside = document.createElement("button");
    const act = vi.fn();
    outside.addEventListener("click", act);
    document.body.append(outside);
    show({ open: true, "onUpdate:open": (open: boolean) => wrapper!.setProps({ open }) });
    await flushPromises();
    expect(document.body.style.pointerEvents).not.toBe("none");
    outside.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    outside.click();
    await flushPromises();
    expect(act).toHaveBeenCalledOnce();
    expect(menu()).toBeNull();
    await wrapper!.setProps({ open: true });
    await flushPromises();
    window.dispatchEvent(new HashChangeEvent("hashchange"));
    await flushPromises();
    expect(menu()).toBeNull();
  });

  it("an action menu has no title (the owner's rule for action and choice menus)", async () => {
    show({ open: true, actions: true });
    await flushPromises();
    expect(menu()!.textContent).toBe("ObdélníkElipsaNedostupné");
  });
});

describe("RowActionsMenu", () => {
  it("runs the row's Delete or F2 item on that key without the menu; other keys and fields stay the row's", async () => {
    const row = defineComponent({ setup: () => () => h("li", { tabindex: 0 }, [h("input"), h(RowActionsMenu, { label: "Řádek",
      items: [{ id: "edit", label: "Upravit", shortcut: "F2" }, { id: "trash", label: "Smazat", shortcut: "Delete", destructive: true }] })]) });
    wrapper = mount(row, { attachTo: document.body });
    const li = wrapper.get("li").element;
    const press = (target: Element, key: string, init: KeyboardEventInit = {}) => target.dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...init }));
    press(li, "Delete");
    press(li, "F2");
    press(li, "Delete", { shiftKey: true });
    press(wrapper.get("input").element, "Delete");
    await flushPromises();
    expect(wrapper.findComponent(RowActionsMenu).emitted("select")).toEqual([["trash"], ["edit"]]);
    expect(menu()).toBeNull();
  });
});
