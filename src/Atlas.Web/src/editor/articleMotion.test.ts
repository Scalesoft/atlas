import { afterEach, describe, expect, it, vi } from "vitest";
import { articleArrives } from "./articleMotion";

afterEach(() => { vi.restoreAllMocks(); Reflect.deleteProperty(HTMLElement.prototype, "animate"); });

describe("articleArrives", () => {
  it("lets the page rise and fade in on the slow token, and only fades under reduced motion", () => {
    const calls: Keyframe[][] = [];
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: vi.fn((frames: Keyframe[]) => { calls.push(frames); return { finished: Promise.resolve(), cancel() {} }; }) });
    const elements = [document.createElement("div"), document.createElement("section")];
    expect(articleArrives(elements)).toHaveLength(2);
    // The rise takes the whole slow token; the fade is done earlier on the way, so reading never waits for the movement.
    expect(calls[0]).toEqual([{ opacity: 0.65, transform: "translateY(8px)" }, { opacity: 1, offset: 0.6 }, { opacity: 1, transform: "none" }]);
    calls.length = 0;
    vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: query.includes("reduce"), media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
    articleArrives(elements);
    for (const frame of calls.flat()) expect(frame).not.toHaveProperty("transform");
  });
});
