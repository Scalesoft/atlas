import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { installScrollMemory } from "./scrollMemory";

/** A window with just what the module touches: listeners, a hash, a scroll position and a page height that can grow. */
function fakeWindow() {
  const listeners = new Map<string, Set<() => void>>();
  const win = {
    location: { hash: "#/pages/a" }, scrollY: 0, innerHeight: 800, document: { documentElement: { scrollHeight: 5000 } },
    performance: { now: () => Date.now() },
    addEventListener: (type: string, fn: () => void) => { (listeners.get(type) ?? listeners.set(type, new Set()).get(type)!).add(fn); },
    removeEventListener: (type: string, fn: () => void) => { listeners.get(type)?.delete(fn); },
    requestAnimationFrame: (fn: () => void) => setTimeout(fn, 16) as unknown as number,
    cancelAnimationFrame: (id: number) => clearTimeout(id),
    scrollTo: ({ top }: { top: number }) => { win.scrollY = top; fire("scroll"); },
  };
  const fire = (type: string) => listeners.get(type)?.forEach(fn => fn());
  return { win: win as unknown as Window & typeof win, fire };
}

describe("scroll memory", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it("returns to the remembered position of an address on history navigation, once the page is tall enough", () => {
    const { win, fire } = fakeWindow();
    installScrollMemory(win);
    win.scrollY = 2500; fire("scroll");
    win.location.hash = "#/pages/b"; win.scrollY = 0; fire("scroll"); // a new address starts at the top
    win.location.hash = "#/pages/a"; win.document.documentElement.scrollHeight = 900; fire("popstate"); // Back: the page is still short
    vi.advanceTimersByTime(300);
    expect(win.scrollY).toBe(0);
    win.document.documentElement.scrollHeight = 5000; // the content arrived
    vi.advanceTimersByTime(300);
    expect(win.scrollY).toBe(2500);
  });

  it("keeps the place of an address when its page shrinks under the reader and the browser clamps the scroll", () => {
    const { win, fire } = fakeWindow();
    installScrollMemory(win);
    win.scrollY = 2500; fire("scroll");
    win.document.documentElement.scrollHeight = 900; win.scrollY = 100; fire("scroll"); // the arrival's skeleton, still under the old address
    win.location.hash = "#/pages/b"; win.scrollY = 0; fire("scroll");
    win.location.hash = "#/pages/a"; win.document.documentElement.scrollHeight = 5000; fire("popstate");
    vi.advanceTimersByTime(300);
    expect(win.scrollY).toBe(2500);
  });

  it("lets go when the reader scrolls by themselves, and starts a new address at the top", () => {
    const { win, fire } = fakeWindow();
    installScrollMemory(win);
    win.scrollY = 2500; fire("scroll");
    win.location.hash = "#/pages/a2"; fire("popstate"); // never seen: nothing to restore
    vi.advanceTimersByTime(300);
    expect(win.scrollY).toBe(2500);
    win.location.hash = "#/pages/a"; win.scrollY = 0; win.document.documentElement.scrollHeight = 900; fire("popstate");
    fire("wheel"); // the reader took over
    win.document.documentElement.scrollHeight = 5000;
    vi.advanceTimersByTime(1000);
    expect(win.scrollY).toBe(0);
  });
});
