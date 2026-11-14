import { afterEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, withDirectives } from "vue";
import { mount, type VueWrapper } from "@vue/test-utils";
import { revealInRow, vScrollEdges } from "./scrollEdges";

// jsdom has no layout: a row 375 px wide of six 120 px tabs, scrolled by `scrollLeft`.
function row(scrollLeft = 0) {
  const element = document.createElement("div");
  Object.defineProperties(element, { clientWidth: { value: 375 }, scrollWidth: { value: 720 }, scrollLeft: { value: scrollLeft, writable: true } });
  const tabs = Array.from({ length: 6 }, (_, index) => {
    const tab = document.createElement("button");
    Object.defineProperties(tab, { offsetLeft: { value: index * 120 }, offsetWidth: { value: 120 } });
    element.append(tab);
    return tab;
  });
  element.scrollTo = vi.fn() as typeof element.scrollTo;
  return { element, tabs };
}

// W2-8: at 375 px the space's tabs scroll; the chosen one is brought into the row, never by scrolling the page.
describe("revealInRow", () => {
  it("scrolls the row just enough to show a tab past its end, and leaves a shown one", () => {
    const { element, tabs } = row();
    revealInRow(element, tabs[4]!);
    expect(element.scrollTo).toHaveBeenCalledWith({ left: 600 + 16 - 375, behavior: expect.any(String) });
    vi.mocked(element.scrollTo).mockClear();
    revealInRow(element, tabs[1]!);
    expect(element.scrollTo).not.toHaveBeenCalled();
  });
  it("scrolls back to a tab before its start", () => {
    const { element, tabs } = row(400);
    revealInRow(element, tabs[1]!);
    expect(element.scrollTo).toHaveBeenCalledWith({ left: 120 - 16, behavior: expect.any(String) });
  });
});

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

describe("v-scroll-edges:x", () => {
  it("marks the start and the end where more of a horizontal row waits", async () => {
    const Row = defineComponent({ setup: () => () => withDirectives(h("div", { class: "row" }), [[vScrollEdges, undefined, "x"]]) });
    wrapper = mount(Row, { attachTo: document.body });
    const element = wrapper.element as HTMLElement;
    Object.defineProperties(element, { clientWidth: { value: 375 }, scrollWidth: { value: 720 }, scrollLeft: { value: 0, writable: true } });
    element.dispatchEvent(new Event("scroll"));
    await new Promise(resolve => requestAnimationFrame(resolve));
    expect(element.hasAttribute("data-more-after")).toBe(true);
    expect(element.hasAttribute("data-more-before")).toBe(false);
    element.scrollLeft = 345;
    element.dispatchEvent(new Event("scroll"));
    await new Promise(resolve => requestAnimationFrame(resolve));
    expect(element.hasAttribute("data-more-before")).toBe(true);
    expect(element.hasAttribute("data-more-after")).toBe(false);
  });
});
