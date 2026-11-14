import { afterEach, describe, expect, it } from "vitest";
import { enableAutoUnmount, mount } from "@vue/test-utils";
import { defineComponent, h, nextTick } from "vue";
import SettingsNav from "./SettingsNav.vue";
import type { SettingsNavGroup } from "./types";

enableAutoUnmount(afterEach);

const groups: SettingsNavGroup[] = [
  { id: "a", items: [{ path: "one", label: "Jedna", icon: "layout-dashboard" }, { path: "two", label: "Dvě", icon: "layout-dashboard" }] },
  { id: "b", label: "Další", items: [{ path: "three", label: "Tři", icon: "layout-dashboard" }, { path: "four", label: "Čtyři", icon: "layout-dashboard" }] },
];

/**
 * A rail that scrolls (the nav inside a box with overflow-y) with the geometry the test dictates: jsdom has no layout, so the
 * rects and the scroll position are stood in for on the prototypes while the test runs.
 */
function geometry(rects: { box: [number, number]; rows: Record<string, [number, number]> }) {
  const state = { scrollTop: 0 };
  const rect = (top: number, bottom: number) => ({ top, bottom, left: 0, right: 200, width: 200, height: bottom - top, x: 0, y: top, toJSON: () => ({}) }) as DOMRect;
  const originalRect = HTMLElement.prototype.getBoundingClientRect;
  const originalTop = Object.getOwnPropertyDescriptor(Element.prototype, "scrollTop");
  HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
    if (this.classList.contains("rail")) return rect(...rects.box);
    const row = rects.rows[this.getAttribute("data-path") ?? ""];
    return row ? rect(...row) : rect(0, 0);
  };
  Object.defineProperty(Element.prototype, "scrollTop", { get: () => state.scrollTop, set: (value: number) => { state.scrollTop = value; }, configurable: true });
  return {
    state,
    restore() {
      HTMLElement.prototype.getBoundingClientRect = originalRect;
      if (originalTop) Object.defineProperty(Element.prototype, "scrollTop", originalTop);
      else delete (Element.prototype as unknown as Record<string, unknown>).scrollTop;
    },
  };
}
const Rail = defineComponent({
  props: { current: { type: String, required: true } },
  setup: (props) => () => h("div", { class: "rail", style: "overflow-y: auto" }, [h(SettingsNav, { groups, label: "Sekce", currentPath: props.current })]),
});
const rects = { box: [100, 300] as [number, number], rows: { one: [110, 140] as [number, number], two: [150, 180] as [number, number], three: [400, 430] as [number, number], four: [500, 530] as [number, number] } };

describe("SettingsNav keeps the current section in view", () => {
  it("scrolls the rail on arrival so that a current row below its visible part comes into view, without taking focus", async () => {
    const { state, restore } = geometry(rects);
    const focused = document.activeElement;
    try {
      mount(Rail, { props: { current: "four" }, attachTo: document.body });
      await nextTick();
      await nextTick();
      expect(state.scrollTop).toBe(230);
      expect(document.activeElement).toBe(focused);
    } finally {
      restore();
    }
  });

  it("leaves the rail alone when the current row is already visible", async () => {
    const { state, restore } = geometry(rects);
    try {
      mount(Rail, { props: { current: "one" }, attachTo: document.body });
      await nextTick();
      await nextTick();
      expect(state.scrollTop).toBe(0);
    } finally {
      restore();
    }
  });

  it("follows a change of the current section, but not the reader's own scrolling", async () => {
    const { state, restore } = geometry(rects);
    try {
      const wrapper = mount(Rail, { props: { current: "one" }, attachTo: document.body });
      await nextTick();
      await wrapper.setProps({ current: "three" });
      await nextTick();
      expect(state.scrollTop).toBe(130);
      state.scrollTop = 20;
      await wrapper.setProps({ current: "three" });
      await nextTick();
      expect(state.scrollTop).toBe(20);
    } finally {
      restore();
    }
  });
});
