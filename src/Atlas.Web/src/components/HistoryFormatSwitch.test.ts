import { afterEach, expect, it } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import HistoryFormatSwitch from "./HistoryFormatSwitch.vue";
import { useHistoryFormat } from "../historyFormat";

afterEach(() => { useHistoryFormat().choose("rendered"); localStorage.clear(); document.body.innerHTML = ""; });

it("gives Náhled | Markdown one thumb that is placed first and then follows the choice", async () => {
  // jsdom has no layout: Náhled sits at 3 px (56 px wide), Markdown at 61 px (75 px wide).
  const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
  const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
  const markdown = (element: HTMLElement) => element.textContent === "Markdown";
  Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return markdown(this) ? 61 : 3; } });
  Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return this.tagName !== "BUTTON" ? 0 : markdown(this) ? 75 : 56; } });
  const frame = () => new Promise<void>(done => requestAnimationFrame(() => done()));
  try {
    const wrapper = mount(HistoryFormatSwitch, { attachTo: document.body });
    await flushPromises(); await frame(); await frame(); await flushPromises();
    const track = wrapper.get(".history-format");
    // Placed without motion first, then ready for the glide.
    expect(track.attributes("data-segmented")).toBeDefined();
    expect(track.attributes("style")).toContain("--thumb-x: 3px");
    expect(track.attributes("style")).toContain("--thumb-w: 56px");
    expect(track.attributes("data-thumb")).toBe("ready");
    await wrapper.findAll("button")[1]!.trigger("click");
    await flushPromises(); await frame();
    expect(track.attributes("style")).toContain("--thumb-x: 61px");
    expect(track.attributes("style")).toContain("--thumb-w: 75px");
    // The choice itself, its roles and its labels are those of before.
    expect(wrapper.findAll("button").map(button => [button.text(), button.attributes("aria-pressed")])).toEqual([["Náhled", "false"], ["Markdown", "true"]]);
    expect(track.attributes("role")).toBe("group");
    wrapper.unmount();
  } finally {
    if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
    if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
  }
});
