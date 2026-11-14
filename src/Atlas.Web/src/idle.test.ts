import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { whenRested } from "./idle";

describe("whenRested", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it("runs idle work only after keys and pointer have rested, and stops listening then", () => {
    const work = vi.fn();
    const remove = vi.spyOn(window, "removeEventListener");
    whenRested(work);
    window.dispatchEvent(new Event("keydown"));
    vi.advanceTimersByTime(1);
    expect(work).not.toHaveBeenCalled();
    vi.advanceTimersByTime(500);
    window.dispatchEvent(new Event("pointerdown"));
    vi.advanceTimersByTime(700);
    expect(work).not.toHaveBeenCalled();
    vi.advanceTimersByTime(200);
    expect(work).toHaveBeenCalledOnce();
    expect(remove).toHaveBeenCalledWith("keydown", expect.any(Function), { capture: true });
    remove.mockRestore();
  });
});

describe("whenRested in idle time", () => {
  afterEach(() => { Reflect.deleteProperty(window, "requestIdleCallback"); vi.restoreAllMocks(); });

  it("waits past short gaps between tasks for a free thread, until its timeout", () => {
    const idle: IdleRequestCallback[] = [];
    Object.defineProperty(window, "requestIdleCallback", { configurable: true, value: (callback: IdleRequestCallback) => idle.push(callback) });
    const work = vi.fn();
    let now = 0;
    vi.spyOn(performance, "now").mockImplementation(() => now);
    whenRested(work, 3000);
    idle.shift()!({ didTimeout: false, timeRemaining: () => 8 });
    expect(work).not.toHaveBeenCalled();
    idle.shift()!({ didTimeout: false, timeRemaining: () => 49 });
    expect(work).toHaveBeenCalledOnce();

    whenRested(work, 3000);
    now = 3500;
    idle.shift()!({ didTimeout: false, timeRemaining: () => 3 });
    expect(work).toHaveBeenCalledTimes(2);
  });
});
