import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { defineComponent, h, nextTick, ref } from "vue";
import { mount } from "@vue/test-utils";
import { useStageHeight } from "./stageHeight";

// jsdom has no layout, no Web Animations and no ResizeObserver: the stage's height, the entering child's height and the
// animations are given and recorded, the clock and the frames are driven by hand.
let stageHeight = 0;
let enteringHeight = 0;
let observed: (() => void) | undefined;
const animations: { frames: Keyframe[]; options: KeyframeAnimationOptions; cancelled: boolean; timing: OptionalEffectTiming[] }[] = [];

function host() {
  let hooks!: ReturnType<typeof useStageHeight>;
  const wrapper = mount(defineComponent({
    setup() {
      const stage = ref<HTMLElement>();
      hooks = useStageHeight(stage);
      return () => h("div", { ref: stage, class: "stage" }, [h("div", { class: "entering" })]);
    },
  }), { attachTo: document.body });
  const stage = wrapper.get(".stage").element as HTMLElement;
  const entering = wrapper.get(".entering").element as HTMLElement;
  stage.getBoundingClientRect = () => ({ height: stageHeight } as DOMRect);
  Object.defineProperty(entering, "offsetHeight", { get: () => enteringHeight });
  return { wrapper, hooks, entering };
}
const lastAnimation = () => animations.at(-1)!;
const frame = (ms: number) => { vi.advanceTimersByTime(ms); };

beforeEach(() => {
  vi.useFakeTimers({ toFake: ["requestAnimationFrame", "cancelAnimationFrame", "performance", "setTimeout", "clearTimeout"] });
  animations.length = 0;
  observed = undefined;
  globalThis.ResizeObserver = class { constructor(callback: () => void) { observed = callback; } observe() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.animate = function (frames: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) {
    const record = { frames: frames as Keyframe[], options: options as KeyframeAnimationOptions, cancelled: false, timing: [] as OptionalEffectTiming[] };
    animations.push(record);
    return { finished: Promise.resolve(), addEventListener() {}, cancel() { record.cancelled = true; }, effect: { updateTiming(timing: OptionalEffectTiming) { record.timing.push(timing); } } } as unknown as Animation;
  };
});
afterEach(() => { vi.useRealTimers(); delete (Element.prototype as { animate?: unknown }).animate; delete (globalThis as { ResizeObserver?: unknown }).ResizeObserver; document.body.innerHTML = ""; });

it("eases to a taller part at once, and holds a shorter one until the old part has faded", async () => {
  const { hooks, entering } = host();
  stageHeight = 300; enteringHeight = 500;
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  expect(lastAnimation().frames).toEqual([{ height: "300px", overflow: "clip" }, { height: "500px", overflow: "clip" }]);
  expect(lastAnimation().options.delay).toBe(0);
  frame(200);
  expect(lastAnimation().timing).toEqual([]);

  // Shorter: the old part is still fading out in the same cell, so the animation waits (its first frame is held) and
  // starts when the fade is over (120 ms, `--atlas-motion-fast`).
  stageHeight = 1469; enteringHeight = 370;
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  expect(lastAnimation().options.fill).toBe("backwards");
  expect(lastAnimation().frames[0]).toEqual({ height: "1469px", overflow: "clip" });
  expect(lastAnimation().options.delay).toBe(480);
  frame(100);
  expect(lastAnimation().timing).toEqual([]);
  frame(20);
  expect(lastAnimation().timing).toEqual([{ delay: 120 }]);
});

it("waits for a part that is still loading, up to four times as long, because its placeholder says nothing about its real height", async () => {
  const { hooks, entering } = host();
  stageHeight = 1469; enteringHeight = 370;
  entering.innerHTML = '<section aria-busy="true"></section>';
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  const held = lastAnimation();
  frame(200);
  expect(held.timing).toEqual([]);
  // It has loaded without changing its height: the shrink starts now.
  entering.innerHTML = '<section aria-busy="false"></section>';
  frame(40);
  expect(held.timing).toEqual([{ delay: 240 }]);

  // Still loading at the end of the wait: it shrinks to the placeholder then.
  entering.innerHTML = '<section aria-busy="true"></section>';
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  const stuck = lastAnimation();
  frame(600);
  expect(stuck.timing).toEqual([{ delay: 480 }]);
});


it("keeps a held shrink held when the placeholder changes its height, and starts it once the hold is over", async () => {
  const { hooks, entering } = host();
  stageHeight = 1469; enteringHeight = 304;
  entering.innerHTML = '<section aria-busy="true"></section>';
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  const first = lastAnimation();
  frame(50);
  // The skeleton shows its rows: 66 px taller. The shrink is re-aimed and still waits for what is left of its hold.
  enteringHeight = 370;
  observed!();
  const second = lastAnimation();
  expect(first.cancelled).toBe(true);
  expect(second.frames[1]).toEqual({ height: "370px", overflow: "clip" });
  expect(second.options.delay).toBe(430);
  frame(100);
  expect(second.timing).toEqual([]);
  // The part has loaded without a change of height: the shrink starts at the next look (every 40 ms), not at the end of the wait.
  entering.innerHTML = '<section aria-busy="false"></section>';
  frame(40);
  expect(second.timing).toEqual([{ delay: 160 }]);
  // After the release a change of height re-aims without waiting.
  enteringHeight = 1409;
  observed!();
  expect(lastAnimation().options.delay).toBe(0);
});

it("does not re-aim for the first notice of the observer, only when the part's height really changed", async () => {
  const { hooks, entering } = host();
  stageHeight = 1469; enteringHeight = 370;
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  const started = animations.length;
  // The observer reports the size it has just been given: nothing changed, the held shrink is left alone.
  observed!();
  expect(animations).toHaveLength(started);
  expect(lastAnimation().cancelled).toBe(false);
  // The list replaces the skeleton while the cell still holds its old height: the same animation is re-aimed at the real height.
  enteringHeight = 1409;
  observed!();
  expect(animations).toHaveLength(started + 1);
  expect(lastAnimation().frames).toEqual([{ height: "1469px", overflow: "clip" }, { height: "1409px", overflow: "clip" }]);
  expect(animations[started - 1]!.cancelled).toBe(true);
});

it("continues at the speed it has when it is re-aimed in the same direction, and turns smoothly when it has to turn back", async () => {
  const { hooks, entering } = host();
  stageHeight = 300; enteringHeight = 1000;
  hooks.onBeforeLeave(); hooks.onEnter(entering);
  // The cell is growing at 1.25 px/ms (20 px in a 16 ms frame).
  stageHeight = 400; frame(16);
  stageHeight = 420; frame(16);
  enteringHeight = 1200;
  observed!();
  const same = lastAnimation().options.easing as string;
  // A cubic-bezier whose first control point has the slope that matches the speed: 1.25 px/ms over 200 ms and 780 px is
  // a slope of 0.32 (y of the point 0.22 × 0.32 = 0.07), not the 4.5 of a restart at full speed.
  expect(same).toMatch(/^cubic-bezier\(0\.22, 0\.0[5-9]\d*, 0\.36, 1\)$/);

  stageHeight = 600; frame(16);
  stageHeight = 620; frame(16);
  enteringHeight = 200;
  observed!();
  // Against the direction it was going: it starts from rest on the curve that eases both ends.
  expect(lastAnimation().options.easing).toBe("cubic-bezier(.65, 0, .35, 1)");
});
