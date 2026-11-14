import { afterEach, describe, expect, it, vi } from "vitest";
import { morphSearchPanel } from "./searchMorph";

afterEach(() => { document.body.replaceChildren(); vi.unstubAllGlobals(); });

function panel() {
  const el = document.createElement("dialog");
  el.setAttribute("data-search-view", "dialog");
  el.style.transition = "width 1s";
  // The dialog's box at 100,64 (900 × 600), the page's the whole 1280 × 800 window.
  el.getBoundingClientRect = () => (el.getAttribute("data-search-view") === "page"
    ? new DOMRect(0, 0, 1280, 800) : new DOMRect(100, 64, 900, 600));
  const order: string[] = [];
  el.animate = vi.fn((frames: Keyframe[], options?: KeyframeAnimationOptions) => {
    order.push(`animate:${el.getAttribute("data-search-view")}`);
    return { finished: Promise.resolve(), cancel: vi.fn(), frames, options } as unknown as Animation;
  });
  document.body.append(el);
  const apply = (view: string) => async () => { order.push(`apply:${view}`); el.setAttribute("data-search-view", view); };
  return { el, order, apply, calls: () => (el.animate as ReturnType<typeof vi.fn>).mock.calls as [Keyframe[], KeyframeAnimationOptions][] };
}

describe("morphSearchPanel", () => {
  it("grows into the page at once and opens its clip from the dialog's box", async () => {
    const { el, order, apply, calls } = panel();
    await morphSearchPanel(el, "page", apply("page"));
    expect(order).toEqual(["apply:page", "animate:page"]);
    const [frames] = calls()[0]!;
    expect(frames[0]).toMatchObject({ opacity: 0.7 }); expect(frames[0]!.clipPath).toMatch(/^inset\(64px 280px 136px 100px round 0(px)?\)$/);
    expect(frames[1]).toMatchObject({ clipPath: "inset(0px)", opacity: 1 });
    expect(el.style.transition).toBe("");
  });

  it("shrinks its clip onto the dialog's box first and switches the layout when it gets there", async () => {
    const { el, order, apply, calls } = panel();
    el.setAttribute("data-search-view", "page");
    await morphSearchPanel(el, "dialog", apply("dialog"));
    expect(order).toEqual(["animate:page", "apply:dialog"]);
    const [frames, options] = calls()[0]!;
    expect(frames.at(-1)?.clipPath).toMatch(/^inset\(64px 280px 136px 100px round 0(px)?\)$/);
    expect(options.fill).toBe("forwards");
  });

  it("under reduced motion switches at once and only fades", async () => {
    vi.stubGlobal("matchMedia", () => ({ matches: true }));
    const { el, order, apply, calls } = panel();
    el.setAttribute("data-search-view", "page");
    await morphSearchPanel(el, "dialog", apply("dialog"));
    expect(order).toEqual(["apply:dialog", "animate:dialog"]);
    const [frames, options] = calls()[0]!;
    expect(frames.every(frame => !("clipPath" in frame))).toBe(true);
    expect(Number(options.duration)).toBeLessThanOrEqual(160);
  });
});
