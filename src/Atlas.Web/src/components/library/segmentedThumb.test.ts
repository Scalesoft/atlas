import { afterAll, afterEach, describe, expect, it } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, nextTick, ref, shallowRef } from "vue";
import { bindSegmentedThumb, useSegmentedThumb } from "./segmentedThumb";

// jsdom has no layout: the chosen option sits at 3 px (A) or 79 px (B), 74 and 89 px wide, or has no box at all.
let visible = true;
const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
const isB = (element: HTMLElement) => element.dataset.option === "b";
Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return isB(this) ? 79 : 3; } });
Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return !visible ? 0 : isB(this) ? 89 : 74; } });
const frame = () => new Promise<void>((done) => requestAnimationFrame(() => done()));

const Switch = defineComponent({
  setup() {
    const track = ref<HTMLElement>();
    const choice = ref("a");
    const thumb = useSegmentedThumb(track, '[aria-pressed="true"]', choice);
    return { track, choice, thumb };
  },
  template: `<div ref="track" v-bind="thumb" class="switch">
    <button data-option="a" :aria-pressed="choice === 'a'" @click="choice = 'a'">A</button>
    <button data-option="b" :aria-pressed="choice === 'b'" @click="choice = 'b'">B</button></div>`,
});

afterEach(() => {
  visible = true;
  document.documentElement.removeAttribute("data-atlas-reduced-motion");
  document.body.innerHTML = "";
});

describe("useSegmentedThumb", () => {
  it("places one thumb on the chosen option without motion first and lets it glide afterwards", async () => {
    const wrapper = mount(Switch, { attachTo: document.body });
    await flushPromises();
    const track = wrapper.get(".switch");
    // Placed at once, gliding not yet allowed.
    expect(track.attributes("data-segmented")).toBeDefined();
    expect(track.attributes("style")).toContain("--thumb-x: 3px");
    expect(track.attributes("style")).toContain("--thumb-w: 74px");
    expect(track.attributes("data-thumb")).toBe("placed");
    await frame(); await frame(); await flushPromises();
    expect(track.attributes("data-thumb")).toBe("ready");
    wrapper.unmount();
  });

  it("makes the track the containing block of its options from the very first render, before anything is measured", () => {
    // Measured while the track is not positioned, an option's offsetLeft is relative to some ancestor: the thumb then
    // glided from there to its place (a 33 px overflow of the history dialog on a phone).
    const wrapper = mount(Switch, { attachTo: document.body });
    expect(wrapper.get(".switch").attributes("data-segmented")).toBeDefined();
    expect(wrapper.get(".switch").attributes("data-thumb")).toBeUndefined();
    wrapper.unmount();
  });

  it("follows the choice: the thumb moves to the other option and takes its width", async () => {
    const wrapper = mount(Switch, { attachTo: document.body });
    await flushPromises(); await frame(); await frame(); await flushPromises();
    await wrapper.get('[data-option="b"]').trigger("click");
    await flushPromises();
    const track = wrapper.get(".switch");
    expect(track.attributes("style")).toContain("--thumb-x: 79px");
    expect(track.attributes("style")).toContain("--thumb-w: 89px");
    expect(track.attributes("data-thumb")).toBe("ready");
    await wrapper.get('[data-option="a"]').trigger("click");
    await flushPromises();
    expect(track.attributes("style")).toContain("--thumb-x: 3px");
    wrapper.unmount();
  });

  it("does not glide in from the corner when the switch arrives without a box and gets one later", async () => {
    visible = false; // folded, hidden or fading in as a ghost: nothing to measure yet
    const wrapper = mount(Switch, { attachTo: document.body });
    await flushPromises(); await frame(); await frame(); await flushPromises();
    const track = wrapper.get(".switch");
    expect(track.attributes("data-thumb")).toBeUndefined();
    expect(track.attributes("style")).toBeUndefined();
    visible = true;
    await wrapper.get('[data-option="b"]').trigger("click");
    await flushPromises();
    // The first real box is placed without motion, and the glide is allowed only a frame later.
    expect(track.attributes("style")).toContain("--thumb-x: 79px");
    expect(track.attributes("data-thumb")).toBe("placed");
    await frame(); await frame(); await flushPromises();
    expect(track.attributes("data-thumb")).toBe("ready");
    wrapper.unmount();
  });

  it("draws no thumb under reduced motion: the chosen option fades its own chip", async () => {
    document.documentElement.setAttribute("data-atlas-reduced-motion", "");
    const wrapper = mount(Switch, { attachTo: document.body });
    await flushPromises(); await frame(); await nextTick();
    const track = wrapper.get(".switch");
    expect(track.attributes("data-segmented")).toBeUndefined();
    expect(track.attributes("style")).toBeUndefined();
    wrapper.unmount();
  });
});

describe("bindSegmentedThumb", () => {
  it("puts the same thumb on a track that another component draws, and takes it off under reduced motion", async () => {
    // The open workspace's tabs are drawn by the editor package: the element is only found, never rendered here.
    const track = document.createElement("div");
    track.innerHTML = `<button data-option="a" aria-selected="true">A</button><button data-option="b" aria-selected="false">B</button>`;
    document.body.append(track);
    const choice = ref("a");
    const Host = defineComponent({ setup() { const element = shallowRef<HTMLElement | null>(track); bindSegmentedThumb(element, '[aria-selected="true"]', choice); return {}; }, template: "<i />" });
    const wrapper = mount(Host, { attachTo: document.body });
    // The track is the containing block of its options before the first measurement.
    expect(track.hasAttribute("data-segmented")).toBe(true);
    await flushPromises(); await frame(); await frame(); await flushPromises();
    expect(track.hasAttribute("data-segmented")).toBe(true);
    expect(track.style.getPropertyValue("--thumb-x")).toBe("3px");
    expect(track.style.getPropertyValue("--thumb-w")).toBe("74px");
    expect(track.getAttribute("data-thumb")).toBe("ready");
    // The package re-renders the tabs: the thumb follows what is selected now.
    track.querySelectorAll("button").forEach((button) => button.setAttribute("aria-selected", String(button.dataset.option === "b")));
    choice.value = "b";
    await flushPromises();
    expect(track.style.getPropertyValue("--thumb-x")).toBe("79px");
    expect(track.style.getPropertyValue("--thumb-w")).toBe("89px");
    // Reduced motion: the thumb is taken off again and the selected tab keeps its own chip.
    document.documentElement.setAttribute("data-atlas-reduced-motion", "");
    choice.value = "a";
    track.querySelectorAll("button").forEach((button) => button.setAttribute("aria-selected", String(button.dataset.option === "a")));
    await flushPromises();
    expect(track.hasAttribute("data-segmented")).toBe(false);
    expect(track.style.getPropertyValue("--thumb-x")).toBe("");
    wrapper.unmount();
  });
});

afterAll(() => {
  if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
  if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
});
