import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "./index";

let wrapper: VueWrapper;
let style: HTMLStyleElement;
beforeEach(() => {
  // jsdom does not run CSS animations. Give Presence the same entering/exiting
  // animation names as a browser, leaving the closing content mounted.
  style = document.createElement("style");
  style.textContent = '[data-slot="tooltip-content"] { animation-name: tooltip-enter; } [data-slot="tooltip-content"][data-state="closed"] { animation-name: tooltip-exit; }';
  document.head.append(style);
});
afterEach(() => {
  wrapper?.unmount();
  document.body.innerHTML = "";
  style.remove();
});
function renderTooltips() {
  wrapper = mount(defineComponent({
    render: () => h(TooltipProvider, {}, () => ["first", "second"].map(name =>
      h(Tooltip, {}, () => [
        h(TooltipTrigger, { "data-test-trigger": name }, () => name),
        h(TooltipContent, { "data-test-content": name }, () => name + " help"),
      ]))),
  }), {
    attachTo: document.body,
    global: { stubs: { Tooltip: false, TooltipTrigger: false, TooltipContent: false, TooltipProvider: false } },
  });
  return wrapper;
}
describe("tooltip opening during an exit animation", () => {
  it("reopens its own closing content after a rapid keyboard refocus", async () => {
    const first = renderTooltips().get('[data-test-trigger="first"]');
    await first.trigger("focus");
    await flushPromises();
    const content = document.querySelector('[data-test-content="first"]')!;
    content.dispatchEvent(new Event("animationstart"));

    await first.trigger("blur");
    await flushPromises();
    expect(content.isConnected).toBe(true);
    expect(content.getAttribute("data-state")).toBe("closed");

    await first.trigger("focus");
    await flushPromises();
    expect(first.attributes("data-state")).toBe("instant-open");
    expect(content.getAttribute("data-state")).toBe("instant-open");

    await first.trigger("blur");
    await flushPromises();
    expect(content.isConnected).toBe(true);
    const ended = new Event("animationend");
    Object.defineProperty(ended, "animationName", { value: "tooltip-exit" });
    content.dispatchEvent(ended);
    await flushPromises();
    expect(content.isConnected).toBe(false);
  });

  it("still closes a different tooltip when the second one opens", async () => {
    renderTooltips();
    const first = wrapper.get('[data-test-trigger="first"]');
    const second = wrapper.get('[data-test-trigger="second"]');
    await first.trigger("focus");
    await flushPromises();
    await second.trigger("focus");
    await flushPromises();

    expect(first.attributes("data-state")).toBe("closed");
    expect(second.attributes("data-state")).toBe("instant-open");
    expect(document.querySelector('[data-test-content="second"]')?.textContent).toContain("second help");
  });
});
