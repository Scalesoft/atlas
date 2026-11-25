import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import SettingsList from "./SettingsList.vue";

enableAutoUnmount(afterEach);

// jsdom has no layout and no Web Animations: the list's height, the places of its rows and `animate` are stood in for.
// The list is as tall as the rows that are not leaving (65 px each) - and, while an easing runs and has not been cancelled,
// as tall as it is drawn (`drawn`), the way a browser reports an animated height.
const animations: { frames: Keyframe[]; cancel: ReturnType<typeof vi.fn> }[] = [];
let drawn: number | null = null;
const originals = {
  animate: (HTMLElement.prototype as { animate?: unknown }).animate,
  offsetHeight: Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetHeight"),
  offsetTop: Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetTop"),
};
beforeEach(() => {
  animations.length = 0;
  drawn = null;
  (HTMLElement.prototype as unknown as { animate: unknown }).animate = function (frames: Keyframe[]) {
    const animation = { frames, cancel: vi.fn(), addEventListener: vi.fn(), finished: Promise.resolve() };
    animations.push(animation);
    return animation;
  };
  Object.defineProperty(HTMLElement.prototype, "offsetHeight", {
    configurable: true,
    get(this: HTMLElement) {
      if (!this.classList.contains("settings-list")) return 0;
      const running = animations.some((animation) => animation.cancel.mock.calls.length === 0);
      return running && drawn !== null ? drawn : 65 * this.querySelectorAll(":scope > li:not(.st-list-leave-active)").length;
    },
  });
  Object.defineProperty(HTMLElement.prototype, "offsetTop", { configurable: true, get(this: HTMLElement) { return Number(this.dataset.top ?? 0); } });
});
afterEach(() => {
  (HTMLElement.prototype as unknown as { animate: unknown }).animate = originals.animate;
  if (originals.offsetHeight) Object.defineProperty(HTMLElement.prototype, "offsetHeight", originals.offsetHeight);
  if (originals.offsetTop) Object.defineProperty(HTMLElement.prototype, "offsetTop", originals.offsetTop);
});

const Host = defineComponent({
  props: { ids: { type: Array as () => string[], required: true }, label: { type: String, default: "Seznam" } },
  setup: (props) => () => h(SettingsList, { label: props.label }, { default: () => props.ids.map((id, index) => h("li", { key: id, class: "settings-list-row", "data-id": id, "data-top": String(index * 65) }, id)) }),
});
const mountList = (ids: string[]) => mount(Host, { props: { ids }, global: { stubs: { TransitionGroup: false } } });

describe("SettingsList", () => {
  it("eases its height only when its rows change, from the height it is drawn at to the new one", async () => {
    const wrapper = mountList(["a", "b", "c"]);
    await flushPromises();
    expect(animations).toHaveLength(0);
    await wrapper.setProps({ ids: ["a"] });
    await flushPromises();
    expect(animations).toHaveLength(1);
    expect(animations[0]!.frames.map((frame) => frame.height)).toEqual(["195px", "65px"]);
  });

  it("does not ease or cancel anything for a re-render with the same rows", async () => {
    const wrapper = mountList(["a", "b", "c"]);
    await flushPromises();
    await wrapper.setProps({ ids: ["a"] });
    await flushPromises();
    expect(animations).toHaveLength(1);
    // The same rows again (a label, a reload, a keystroke that keeps the same matches): the easing that runs is left alone.
    drawn = 130;
    await wrapper.setProps({ label: "Jiný seznam" });
    await flushPromises();
    expect(animations).toHaveLength(1);
    expect(animations[0]!.cancel).not.toHaveBeenCalled();
  });

  it("replaces a running easing when the rows change again, continuing from the height it is drawn at", async () => {
    const wrapper = mountList(["a", "b", "c"]);
    await flushPromises();
    await wrapper.setProps({ ids: ["a", "b"] });
    await flushPromises();
    expect(animations).toHaveLength(1);
    drawn = 150; // half way down from 195 to 130, as drawn
    await wrapper.setProps({ ids: [] });
    await flushPromises();
    expect(animations[0]!.cancel).toHaveBeenCalledTimes(1);
    expect(animations).toHaveLength(2);
    expect(animations[1]!.frames.map((frame) => frame.height)).toEqual(["150px", "0px"]);
  });

  it("pins a leaving row where it stood before the update and takes it out of reach", async () => {
    const wrapper = mountList(["a", "b", "c"]);
    await flushPromises();
    const rows = Object.fromEntries(wrapper.findAll("li").map((row) => [row.attributes("data-id")!, row.element as HTMLElement]));
    await wrapper.setProps({ ids: ["a"] });
    expect(rows.c!.style.top).toBe("130px");
    expect(rows.b!.style.top).toBe("65px");
    expect(rows.c!.hasAttribute("inert")).toBe(true);
    expect(rows.c!.getAttribute("aria-hidden")).toBe("true");
    expect(rows.a!.hasAttribute("inert")).toBe(false);
  });
});

describe("SettingsList keys and the row menu", () => {
  it("walks the rows with the arrows and opens a row's menu from its ⋯", async () => {
    const { default: SettingsListRow } = await import("./SettingsListRow.vue");
    const picked: string[] = [];
    const wrapper = mount(defineComponent({
      setup: () => () => h(SettingsList, { label: "Seznam" }, { default: () => ["a", "b"].map((id) => h(SettingsListRow, { key: id, title: id, menu: [{ id: "remove", label: "Odebrat", shortcut: "Delete" }], menuLabel: `Akce ${id}`, more: true, "data-id": id, onSelect: (item: string) => picked.push(`${id}:${item}`) }, { actions: () => h("button", { "data-first": id }, "Upravit") })) }),
    }), { attachTo: document.body, global: { stubs: { TransitionGroup: false } } });
    const first = wrapper.get<HTMLButtonElement>("[data-first=a]").element;
    first.focus();
    first.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(wrapper.get("[data-id=b] [data-first=b]").element);
    // Delete runs the item that names it as its shortcut, without the menu.
    document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "Delete", bubbles: true, cancelable: true }));
    expect(picked).toEqual(["b:remove"]);
    await wrapper.get("[data-id=a] [data-row-more]").trigger("click");
    await flushPromises();
    expect(document.querySelector("[data-row-action]")?.textContent).toContain("Odebrat");
  });
});
