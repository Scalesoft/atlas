import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import MotionSystemExample from "./MotionSystemExample.vue";

// The foundation tiles (motion unification plan §5 M0) play the real primitives and motion.css rules.
afterEach(() => {
  vi.useRealTimers();
  document.body.replaceChildren();
  delete (Element.prototype as Partial<Element>).animate;
});

it("plays the search unfold, the menu and the toast of the foundation from the shared motion system", async () => {
  vi.useFakeTimers();
  const animate = vi.fn((_frames: Keyframe[], _options?: KeyframeAnimationOptions) => ({ finished: Promise.resolve(), cancel: vi.fn() }) as unknown as Animation);
  Element.prototype.animate = animate as unknown as Element["animate"];
  const example = mount(MotionSystemExample, { attachTo: document.body });
  await vi.advanceTimersByTimeAsync(9 * 240 + 6 * 240 + 210);
  await flushPromises();

  expect(example.findAll("[data-tile]").map((tile) => tile.attributes("data-tile")).slice(9)).toEqual(["search", "page", "menu", "toast", "work", "defer", "wave", "loading", "rise-from", "pulse"]);
  // The search's whole-page view: the panel takes the page layout and its clip opens from the dialog's box (morphSearchPanel).
  expect(example.find(".ai-ex-page-panel").attributes("data-search-view")).toBe("page");
  expect(animate.mock.calls.some(([frames]) => String(frames[0]?.clipPath ?? "").startsWith("inset(") && frames.at(-1)?.clipPath === "inset(0px)")).toBe(true);
  // The search panel unfolded with a clip from its field (unfoldFrom) and is visible.
  const panel = example.find<HTMLElement>(".ai-ex-search-panel");
  expect(panel.element.hidden).toBe(false);
  expect(animate.mock.calls.some(([frames]) => frames[0]?.opacity === 0.7)).toBe(true);
  // The menu is a Reka-like surface, so motion.css plays atlas-menu-in on it; the toast uses atlas-toast-in.
  expect(example.find('[data-slot="dropdown-menu-content"][data-state="open"]').exists()).toBe(true);
  expect(example.find(".ai-ex-toast.is-in").exists()).toBe(true);
  // Loops of work use the shared classes that reduced motion stops.
  expect(example.find(".atlas-spinner").exists()).toBe(true);
  expect(example.find(".atlas-pulse").exists()).toBe(true);
  example.unmount();
});
