import { afterEach, expect, it, vi } from "vitest";
import { playNodeMotion } from "./boardAnimations";

afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const box = (x: number, y = 0) => ({ x, y, width: 200, height: 120 });
function node() {
  const wrapper = document.createElement("div"), el = document.createElement("div");
  wrapper.append(el);
  const played: { frames: Keyframe[]; animation: { playState: string; cancel: () => void } }[] = [];
  wrapper.animate = ((frames: Keyframe[]) => {
    const animation = { playState: "running", cancel: vi.fn(() => { animation.playState = "idle"; }), finished: Promise.resolve() };
    played.push({ frames, animation });
    return animation;
  }) as unknown as Element["animate"];
  return { el, wrapper, played };
}

it("glides to the planned place even when the node's own position is still the old one (COL2-2)", () => {
  const { el, played } = node();
  playNodeMotion(el, { kind: "glide", order: 0, key: 1, from: box(0), to: box(240, 30) }, { x: 0, y: 0 });
  expect(played[0]!.frames.map(frame => frame.transform)).toEqual(["translate(0px,0px)", "translate(240px,30px)"]);
});

it("starts a move that arrives mid-glide where the card is drawn, and drops the glide it replaces", () => {
  const { el, wrapper, played } = node();
  playNodeMotion(el, { kind: "glide", order: 0, key: 1, from: box(0), to: box(240) }, { x: 0, y: 0 });
  // Halfway along: the animated transform puts the card at 120.
  vi.stubGlobal("DOMMatrixReadOnly", class { m41 = 120; m42 = 0; isIdentity = false; });
  vi.spyOn(window, "getComputedStyle").mockImplementation(target => ({ transform: target === wrapper ? "matrix(1, 0, 0, 1, 120, 0)" : "none", getPropertyValue: () => "" }) as unknown as CSSStyleDeclaration);
  playNodeMotion(el, { kind: "glide", order: 0, key: 2, from: box(240), to: box(400) }, { x: 240, y: 0 });
  expect(played[0]!.animation.cancel).toHaveBeenCalled();
  expect(played[1]!.frames.map(frame => frame.transform)).toEqual(["translate(120px,0px)", "translate(400px,0px)"]);
  // Once it has come to rest, the next one starts from the stored place.
  played[1]!.animation.playState = "finished";
  playNodeMotion(el, { kind: "glide", order: 0, key: 3, from: box(400), to: box(500) }, { x: 400, y: 0 });
  expect(played[2]!.frames[0]!.transform).toBe("translate(400px,0px)");
});

it("reads a board's motion tokens once for all the items a change plays, and again once the page is idle", () => {
  const idle: (() => void)[] = [];
  vi.stubGlobal("requestIdleCallback", (callback: () => void) => idle.push(callback));
  const read = vi.fn((name: string) => (name === "--atlas-motion-base" ? "200ms" : ""));
  vi.spyOn(window, "getComputedStyle").mockImplementation(() => ({ getPropertyValue: read, transform: "none" }) as unknown as CSSStyleDeclaration);
  const canvas = document.createElement("div");
  canvas.className = "board-canvas";
  const nodes = [node(), node()];
  const durations: number[] = [];
  for (const { el, wrapper } of nodes) {
    canvas.append(wrapper);
    el.animate = ((_frames: Keyframe[], options: KeyframeAnimationOptions) => { durations.push(Number(options.duration)); return null; }) as unknown as Element["animate"];
    playNodeMotion(el, { kind: "fade", order: 0, key: 1 }, { x: 0, y: 0 });
  }
  expect(durations).toEqual([200, 200]);
  const reads = () => read.mock.calls.filter(([name]) => name === "--atlas-motion-base").length;
  expect(reads()).toBe(1);
  idle.forEach(callback => callback());
  expect(reads()).toBe(2);
});
