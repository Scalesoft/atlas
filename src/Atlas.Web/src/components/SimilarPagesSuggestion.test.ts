import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SimilarPagesSuggestion from "./SimilarPagesSuggestion.vue";
import type { SimilarPage } from "../similarPages";

const item = (id: string, score: number | undefined): SimilarPage => ({ id, spaceId: "s", spaceName: "Docs", title: `Page ${id}`, isDraft: false, matchedBy: "title", headingTitle: null, headingId: null, score: score as number });
type Call = { target: Element; frames: Keyframe[]; options: KeyframeAnimationOptions };
let calls: Call[] = [];
let wrapper: VueWrapper | undefined;

beforeEach(() => {
  calls = [];
  // jsdom has no Web Animations: record what is animated, and let the unfolding of the panel finish at once.
  Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value(this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) { calls.push({ target: this, frames, options }); return { finished: Promise.resolve(), cancel() {} }; } });
  Object.defineProperty(Element.prototype, "getAnimations", { configurable: true, writable: true, value: () => [{ finished: Promise.resolve() }] });
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
  delete (Element.prototype as { animate?: unknown }).animate;
  delete (Element.prototype as { getAnimations?: unknown }).getAnimations;
});

function mountIn(items: SimilarPage[], reduced = false) {
  const host = document.createElement("div");
  if (reduced) host.setAttribute("data-atlas-reduced-motion", "");
  document.body.append(host);
  wrapper = mount(SimilarPagesSuggestion, { props: { items }, attachTo: host });
  return wrapper;
}
const scaled = () => calls.filter(call => call.frames.some(frame => frame.transform === "scaleX(0)"));

describe("similar pages panel motion", () => {
  it("unfolds the panel, then fills the meters 400 ms each, 80 ms apart, from the left", async () => {
    mountIn([item("1", 0.86), item("2", 0.48), item("3", undefined)]);
    await flushPromises();
    expect(calls[0]!.frames).toEqual([expect.objectContaining({ height: "0px", opacity: 0 }), expect.objectContaining({ opacity: 1 })]);
    expect(calls[0]!.options.duration).toBe(280);
    expect(scaled().map(call => [call.options.duration, call.options.delay])).toEqual([[400, 0], [400, 80]]);
    expect(scaled().every(call => (call.target as HTMLElement).style.transformOrigin.startsWith("0"))).toBe(true);
    // The animation holds the first frame and ends on the bar's own width: nothing stays inline.
    expect((wrapper!.get('[data-similar-page="1"] i').element as HTMLElement).style.transform).toBe("");
    expect(wrapper!.get('[data-similar-page="1"] i').attributes("style")).toContain("width: 86%");
  });
  it("fades the meters in under reduced motion instead of growing them", async () => {
    mountIn([item("1", 0.86), item("2", 0.48)], true);
    await flushPromises();
    expect(scaled()).toEqual([]);
    const fades = calls.filter(call => call.target.matches(".similar-meter > i"));
    expect(fades.map(call => call.frames)).toEqual([[{ opacity: 0 }, { opacity: 1 }], [{ opacity: 0 }, { opacity: 1 }]]);
    expect(fades.every(call => Number(call.options.duration) <= 160 && call.options.easing === "linear")).toBe(true);
    expect((wrapper!.get('[data-similar-page="1"] i').element as HTMLElement).style.opacity).toBe("");
  });
  it("fills only the meter of a row that arrives later", async () => {
    mountIn([item("1", 0.86)]);
    await flushPromises();
    calls.length = 0;
    await wrapper!.setProps({ items: [item("1", 0.86), item("2", 0.5)] });
    await flushPromises();
    const filled = scaled();
    expect(filled).toHaveLength(1);
    expect(filled[0]!.target.closest("[data-similar-page]")?.getAttribute("data-similar-page")).toBe("2");
    expect(filled[0]!.options.delay).toBe(0);
  });
});
