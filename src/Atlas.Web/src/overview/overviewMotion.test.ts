import { describe, expect, it, vi } from "vitest";
import { playLevelChange } from "./overviewMotion";

describe("playLevelChange", () => {
  it("translates a component that moved under a zoomed board by the screen's distance in its own pixels", () => {
    const root = document.body.appendChild(document.createElement("div"));
    const item = root.appendChild(document.createElement("div"));
    item.setAttribute("data-overview-item", "");
    // The board shows its canvas at half size: 200 CSS px wide on 100 screen px.
    Object.defineProperty(item, "offsetWidth", { value: 200 });
    const rects = [new DOMRect(0, 100, 100, 20), new DOMRect(0, 50, 100, 20)];
    vi.spyOn(item, "getBoundingClientRect").mockImplementation(() => rects.shift() ?? new DOMRect(0, 50, 100, 20));
    const animate = vi.fn(() => ({ finished: Promise.resolve() }) as unknown as Animation);
    item.animate = animate;
    playLevelChange(root, () => {});
    expect((animate.mock.calls[0] as unknown as [Keyframe[]])[0][0]!.transform).toBe("translate(0px, 100px) scale(1, 1)");
    root.remove();
  });

  // 3.6 perf: a read after an animation had started restyled the whole page, once per row.
  it("measures every component before it starts the first animation", () => {
    const root = document.body.appendChild(document.createElement("div")), steps: string[] = [];
    for (let i = 0; i < 3; i++) {
      const item = root.appendChild(document.createElement("div"));
      item.setAttribute("data-overview-item", "");
      let top = 100 + i * 20;
      vi.spyOn(item, "getBoundingClientRect").mockImplementation(() => { steps.push("read"); return new DOMRect(0, top, 100, 20); });
      item.animate = vi.fn(() => { steps.push("animate"); return { finished: Promise.resolve() } as unknown as Animation; });
      Object.assign(item, { move: () => { top += 30; } });
    }
    playLevelChange(root, () => { for (const item of root.children) (item as unknown as { move(): void }).move(); steps.push("change"); });
    expect(steps.filter(step => step === "animate")).toHaveLength(3);
    expect(steps.lastIndexOf("read")).toBeLessThan(steps.indexOf("animate"));
    root.remove();
  });
});
