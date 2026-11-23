import { afterEach, expect, it, vi } from "vitest";
import { createPointerGlide } from "./pointerGlide";

afterEach(() => { vi.restoreAllMocks(); });

it("carries a pointer smoothly between samples, keeps its speed when the next one arrives, and settles exactly", () => {
  const frames: FrameRequestCallback[] = [];
  vi.spyOn(globalThis, "requestAnimationFrame").mockImplementation(callback => frames.push(callback));
  const drawn: number[] = [];
  const element = document.createElement("div");
  const glide = createPointerGlide(() => document.body, (_element, x) => drawn.push(x));
  glide.to(element, 0, 0);
  expect(drawn).toEqual([0]);
  let now = performance.now();
  const run = (count: number) => { for (let i = 0; i < count; i++) frames.splice(0).forEach(callback => callback(now += 16)); };
  // Samples every 50 ms (three frames), 20 px apart: the path rises every frame, never jumps a whole step and never overshoots.
  for (let sample = 1; sample <= 5; sample++) {
    glide.to(element, sample * 20, 0);
    run(3);
  }
  run(60);
  const steps = drawn.slice(1).map((x, i) => x - drawn[i]!);
  expect(steps.every(step => step >= 0)).toBe(true);
  expect(Math.max(...steps)).toBeLessThan(10);
  expect(Math.max(...drawn)).toBe(100);
  expect(drawn.at(-1)).toBe(100);
  expect(frames).toHaveLength(0);
  // A snap places it at once (the page moved under it).
  glide.to(element, 300, 0, true);
  expect(drawn.at(-1)).toBe(300);
});
