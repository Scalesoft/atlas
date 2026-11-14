import { afterEach, describe, expect, it, vi } from "vitest";
import { revealSettled } from "./revealSettled";

afterEach(() => { vi.useRealTimers(); document.body.replaceChildren(); });

/** A block whose place on screen the test moves, as a page laying out above it does. */
function block() {
  const element = document.createElement("div");
  let top = 0;
  element.getBoundingClientRect = () => ({ top, bottom: top + 80, left: 0, right: 0, width: 0, height: 80, x: 0, y: top, toJSON: () => ({}) });
  element.scrollIntoView = vi.fn(() => { top = window.innerHeight / 2 - 40; });
  return { element, moveTo: (next: number) => { top = next; } };
}

describe("revealSettled (U4-2)", () => {
  it("waits for the block, centres it, and centres it again while the page above it still lays out, then lets go", async () => {
    vi.useFakeTimers({ toFake: ["requestAnimationFrame", "cancelAnimationFrame"] });
    const { element, moveTo } = block();
    let rendered = false;
    const found = vi.fn();
    revealSettled(() => (rendered ? element : null), found);
    vi.advanceTimersByTime(16 * 3);
    expect(found).not.toHaveBeenCalled();
    rendered = true;
    vi.advanceTimersByTime(16);
    expect(found).toHaveBeenCalledOnce();
    expect(element.scrollIntoView).toHaveBeenCalledTimes(1);
    // The blocks above it took their real size: it was pushed far down.
    moveTo(1900);
    vi.advanceTimersByTime(16);
    expect(element.scrollIntoView).toHaveBeenCalledTimes(2);
    vi.advanceTimersByTime(16 * 200);
    moveTo(4000);
    vi.advanceTimersByTime(16 * 5);
    expect(element.scrollIntoView).toHaveBeenCalledTimes(2);
    expect(found).toHaveBeenCalledOnce();
  });

  it("stops at once when the reader scrolls", () => {
    vi.useFakeTimers({ toFake: ["requestAnimationFrame", "cancelAnimationFrame"] });
    const { element, moveTo } = block();
    revealSettled(() => element, () => {});
    window.dispatchEvent(new WheelEvent("wheel"));
    moveTo(1900);
    vi.advanceTimersByTime(16 * 10);
    expect(element.scrollIntoView).toHaveBeenCalledTimes(1);
  });
});
