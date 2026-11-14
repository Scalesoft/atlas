import { afterEach, describe, expect, it, vi } from "vitest";
import { createArrivalTracker } from "./searchArrival";

const rows = () => {
  const make = (semantic: boolean) => { const el = document.createElement("a"); el.animate = vi.fn(() => ({ finished: Promise.resolve(), cancel() {} }) as unknown as Animation); if (semantic) el.setAttribute("data-semantic", ""); document.body.append(el); return el; };
  return { a: make(false), s: make(true) };
};
afterEach(() => { document.body.innerHTML = ""; vi.unstubAllGlobals(); });

describe("createArrivalTracker", () => {
  it("animates a row once, with a glow on meaning-only rows", () => {
    const { a, s } = rows();
    const tracker = createArrivalTracker();
    tracker.settle([["a", a], ["s", s]], true);
    expect((a.animate as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(1);
    expect((s.animate as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(2);
    tracker.settle([["a", a], ["s", s]], true);
    expect((a.animate as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(1);
  });
  it("rises full-pass rows 8 px, 200 ms, 40 ms apart whether or not AI is on, and leaves no CSS animation delay behind", () => {
    const { a, s } = rows();
    createArrivalTracker().settle([["a", a], ["s", s]], true);
    const calls = [a, s].map(el => (el.animate as ReturnType<typeof vi.fn>).mock.calls[0]!);
    expect((calls[0]![0] as Keyframe[])[0]).toMatchObject({ opacity: 0, transform: "translateY(8px)" });
    expect(calls.map(call => (call[1] as KeyframeAnimationOptions).duration)).toEqual([200, 200]);
    expect(calls.map(call => (call[1] as KeyframeAnimationOptions).delay)).toEqual([0, 40]);
    expect(a.style.getPropertyValue("--search-result-delay")).toBe("");
  });
  it("staggers only the first eight rows of a long batch; the rest rise with the eighth", () => {
    const els = Array.from({ length: 14 }, () => { const el = document.createElement("a"); el.animate = vi.fn(() => ({ finished: Promise.resolve(), cancel() {} }) as unknown as Animation); return el; });
    createArrivalTracker().settle(els.map((el, index) => [`r${index}`, el] as [string, Element]), true);
    const delays = els.map(el => ((el.animate as ReturnType<typeof vi.fn>).mock.calls[0]![1] as KeyframeAnimationOptions).delay);
    expect(delays).toEqual([0, 40, 80, 120, 160, 200, 240, 280, 280, 280, 280, 280, 280, 280]);
  });
  it("only records rows of the prefix preview, so the full pass does not replay them", () => {
    const { a, s } = rows();
    const tracker = createArrivalTracker();
    tracker.settle([["a", a]], false);
    tracker.settle([["a", a], ["s", s]], true);
    expect((a.animate as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(0);
    expect((s.animate as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(0);
  });
  it("under reduced motion the glow is a background fade and no row moves", () => {
    vi.stubGlobal("matchMedia", () => ({ matches: true }));
    const { a, s } = rows();
    createArrivalTracker().settle([["a", a], ["s", s]], true);
    const frames = (s.animate as ReturnType<typeof vi.fn>).mock.calls.flatMap(call => call[0] as Keyframe[]);
    expect(frames.some(f => "transform" in f || "boxShadow" in f)).toBe(false);
    expect(frames.some(f => "backgroundColor" in f)).toBe(true);
    const options = (a.animate as ReturnType<typeof vi.fn>).mock.calls.map(call => call[1] as KeyframeAnimationOptions);
    expect(options.every(o => Number(o.duration) <= 160)).toBe(true);
  });
});
