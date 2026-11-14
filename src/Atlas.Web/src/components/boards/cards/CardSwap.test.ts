import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import CardSwap from "./CardSwap.vue";

interface Started { target: Element; frames: Keyframe[]; options: KeyframeAnimationOptions; finish: () => void }
let started: Started[];
let wrapper: VueWrapper | undefined;
let host: HTMLElement;

beforeEach(() => {
  vi.useFakeTimers();
  started = [];
  host = document.createElement("div");
  document.body.append(host);
  Element.prototype.animate = function (this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) {
    let finish!: () => void;
    const finished = new Promise<void>(resolve => { finish = resolve; });
    started.push({ target: this, frames, options, finish });
    return { finished, cancel: vi.fn() } as unknown as Animation;
  } as unknown as Element["animate"];
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  host.remove();
  delete (Element.prototype as Partial<Element>).animate;
  vi.useRealTimers();
});

const slots = { default: "<p data-ready>Obsah</p>", locked: "<p data-locked>Zámek</p>", pending: "<p data-pending>Načítání</p>" };
function show(state: "pending" | "locked" | "ready", attachTo: HTMLElement = host) {
  // The real Transition: its hooks are what animates.
  wrapper = mount(CardSwap, { props: { state }, slots, attachTo, global: { stubs: { transition: false } } });
  return wrapper;
}
const has = (selector: string) => wrapper!.find(selector).exists();

it("shows no placeholder for a card that is still resolving until --atlas-motion-defer has passed", async () => {
  show("pending");
  expect(wrapper!.get(".bcard-swap").attributes("aria-busy")).toBe("true");
  await vi.advanceTimersByTimeAsync(149);
  expect(has("[data-pending]")).toBe(false);
  await vi.advanceTimersByTimeAsync(1);
  expect(has("[data-pending]")).toBe(true);
  expect(has("[data-board-pending]")).toBe(true);
});

it("never flashes a placeholder for an answer that comes within the defer: the content arrives, the placeholder does not", async () => {
  show("pending");
  await vi.advanceTimersByTimeAsync(100);
  await wrapper!.setProps({ state: "ready" });
  expect(has("[data-ready]")).toBe(true);
  await vi.advanceTimersByTimeAsync(1000);
  expect(has("[data-pending]")).toBe(false);
  expect(wrapper!.get(".bcard-swap").attributes("aria-busy")).toBeUndefined();
  // The content fades in; there was nothing to cross-fade with.
  expect(started).toHaveLength(1);
  expect(started[0]!.frames).toEqual([{ opacity: 0 }, { opacity: 1 }]);
});

it("stands at once when the content is there from the first render: no animation at all", async () => {
  show("ready");
  await vi.advanceTimersByTimeAsync(1000);
  expect(has("[data-ready]")).toBe(true);
  expect(started).toHaveLength(0);
});

it("cross-fades from the placeholder to the content: both layers share the cell while one fades out and the other in", async () => {
  show("pending");
  await vi.advanceTimersByTimeAsync(150);
  expect(started).toHaveLength(1);
  expect(started[0]!.frames).toEqual([{ opacity: 0 }, { opacity: 1 }]);
  started[0]!.finish();

  await wrapper!.setProps({ state: "ready" });
  await nextTick();
  expect(has("[data-pending]")).toBe(true);
  expect(has("[data-ready]")).toBe(true);
  const [placeholder, content] = started.slice(1);
  expect(placeholder!.frames).toEqual([{ opacity: 1 }, { opacity: 0 }]);
  expect(placeholder!.options).toMatchObject({ duration: 200, fill: "forwards" });
  expect(placeholder!.target.hasAttribute("inert")).toBe(true);
  expect(content!.frames).toEqual([{ opacity: 0 }, { opacity: 1 }]);
  expect(content!.options).toMatchObject({ duration: 200, fill: "backwards" });
  // Both layers occupy the same grid cell, so the card never changes size meanwhile.
  expect(wrapper!.findAll(".bcard-layer")).toHaveLength(2);

  placeholder!.finish();
  content!.finish();
  await vi.advanceTimersByTimeAsync(0);
  await nextTick();
  expect(has("[data-pending]")).toBe(false);
  expect(has("[data-ready]")).toBe(true);
});

it("fades a locked placeholder in the same way", async () => {
  show("pending");
  await vi.advanceTimersByTimeAsync(150);
  await wrapper!.setProps({ state: "locked" });
  await nextTick();
  expect(has("[data-locked]")).toBe(true);
  expect(started.map(item => item.frames[0])).toEqual([{ opacity: 0 }, { opacity: 1 }, { opacity: 0 }]);
});

it("keeps to a fade of at most 160 ms, linear, under reduced motion", async () => {
  host.setAttribute("data-atlas-reduced-motion", "");
  show("pending");
  await vi.advanceTimersByTimeAsync(150);
  await wrapper!.setProps({ state: "ready" });
  await nextTick();
  for (const item of started) {
    expect(Number(item.options.duration)).toBeLessThanOrEqual(160);
    expect(item.options.easing).toBe("linear");
    expect(item.frames.every(frame => Object.keys(frame).every(key => key === "opacity"))).toBe(true);
  }
});

it("leaves no timer behind when the card goes away while it waits", async () => {
  show("pending");
  wrapper!.unmount();
  wrapper = undefined;
  expect(vi.getTimerCount()).toBe(0);
});

it("does without Web Animations: the layers swap at once", async () => {
  delete (Element.prototype as Partial<Element>).animate;
  show("pending");
  await vi.advanceTimersByTimeAsync(150);
  await wrapper!.setProps({ state: "ready" });
  await nextTick();
  expect(has("[data-pending]")).toBe(false);
  expect(has("[data-ready]")).toBe(true);
});
