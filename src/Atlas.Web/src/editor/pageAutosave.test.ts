import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createCheckpointScheduler } from "./pageAutosave";

describe("createCheckpointScheduler", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it("saves 1.5 s after the last change", async () => {
    const save = vi.fn(async () => {});
    const scheduler = createCheckpointScheduler(save);
    scheduler.changed();
    await vi.advanceTimersByTimeAsync(1000);
    scheduler.changed();
    await vi.advanceTimersByTimeAsync(1400);
    expect(save).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(100);
    expect(save).toHaveBeenCalledTimes(1);
  });

  it("saves at the latest 10 s after the first change while typing goes on", async () => {
    const save = vi.fn(async () => {});
    const scheduler = createCheckpointScheduler(save);
    for (let elapsed = 0; elapsed < 10_000; elapsed += 1000) { scheduler.changed(); await vi.advanceTimersByTimeAsync(1000); }
    expect(save).toHaveBeenCalledTimes(1);
  });

  // A checkpoint serialises the whole page: a timed one never lands in the middle of a drag (3.6 perf), it goes once the gesture ends.
  it("holds a timed save while a pointer gesture or a drag goes on, and saves after it", async () => {
    const save = vi.fn(async () => {});
    const scheduler = createCheckpointScheduler(save);
    window.dispatchEvent(new MouseEvent("pointerdown", { buttons: 1 }));
    scheduler.changed();
    await vi.advanceTimersByTimeAsync(2000);
    window.dispatchEvent(new Event("dragstart"));
    window.dispatchEvent(new Event("pointercancel"));
    await vi.advanceTimersByTimeAsync(12_000);
    expect(save).not.toHaveBeenCalled();
    window.dispatchEvent(new Event("drop"));
    window.dispatchEvent(new Event("dragend"));
    // Not in the drop's own frames: it goes once the drop has settled.
    await vi.advanceTimersByTimeAsync(0);
    expect(save).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(600);
    expect(save).toHaveBeenCalledTimes(1);
    // A flush (blur, leaving) never waits for a gesture.
    window.dispatchEvent(new MouseEvent("pointerdown", { buttons: 1 }));
    scheduler.changed();
    await scheduler.flush();
    expect(save).toHaveBeenCalledTimes(2);
    window.dispatchEvent(new Event("pointerup"));
  });

  it("keeps one request in flight and answers flushes meanwhile with one more save after it", async () => {
    let release!: () => void;
    const save = vi.fn(() => new Promise<void>(resolve => { release = resolve; }));
    const scheduler = createCheckpointScheduler(save);
    const first = scheduler.flush();
    void scheduler.flush();
    const third = scheduler.flush();
    expect(third).toBe(first);
    expect(save).toHaveBeenCalledTimes(1);
    release();
    await vi.advanceTimersByTimeAsync(0);
    expect(save).toHaveBeenCalledTimes(2);
    release();
    await first;
    expect(scheduler.busy).toBe(false);
  });

  it("flush goes at once and cancels the timers; cancel drops what waits", async () => {
    const save = vi.fn(async () => {});
    const scheduler = createCheckpointScheduler(save);
    scheduler.changed();
    await scheduler.flush();
    await vi.advanceTimersByTimeAsync(20_000);
    expect(save).toHaveBeenCalledTimes(1);
    scheduler.changed();
    scheduler.cancel();
    await vi.advanceTimersByTimeAsync(20_000);
    expect(save).toHaveBeenCalledTimes(1);
  });

  it("rejects a flush with the failure and lets the next change try again", async () => {
    const save = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValue(undefined);
    const scheduler = createCheckpointScheduler(save);
    await expect(scheduler.flush()).rejects.toThrow("offline");
    scheduler.changed();
    await vi.advanceTimersByTimeAsync(1500);
    expect(save).toHaveBeenCalledTimes(2);
  });

  it("tries a retryable failure again by itself with backoff, without waiting for a keystroke", async () => {
    const pending = new Error("collaboration_pending");
    const save = vi.fn().mockRejectedValueOnce(pending).mockRejectedValueOnce(pending).mockResolvedValue(undefined);
    const scheduler = createCheckpointScheduler(save, { retry: error => error === pending });
    await expect(scheduler.flush()).rejects.toBe(pending);
    expect(scheduler.retries).toBe(1);
    await vi.advanceTimersByTimeAsync(1000);
    expect(save).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1999);
    expect(save).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(save).toHaveBeenCalledTimes(3);
    expect(scheduler.retries).toBe(0);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(save).toHaveBeenCalledTimes(3);
  });

  // Campaign 2026-11-05 F4: while colleagues typed, every remote change re-armed the 1.5 s save and the pending answers never backed off.
  it("tells a timed save from a flush, and changes during a retry's backoff wait for it", async () => {
    const pending = new Error("collaboration_pending");
    const save = vi.fn(async (_timed: boolean) => { throw pending; });
    const scheduler = createCheckpointScheduler(save, { retry: error => error === pending });
    scheduler.changed();
    await vi.advanceTimersByTimeAsync(1500);
    expect(save.mock.calls).toEqual([[true]]);
    // Colleagues keep typing: the retry still goes 1 s, then 2 s later, not 1.5 s after every change.
    for (let elapsed = 0; elapsed < 3000; elapsed += 250) { scheduler.changed(); await vi.advanceTimersByTimeAsync(250); }
    expect(save).toHaveBeenCalledTimes(3);
    await expect(scheduler.flush()).rejects.toBe(pending);
    expect(save.mock.calls.at(-1)).toEqual([false]);
  });

  it("does not retry other failures, and cancel drops a waiting retry", async () => {
    const pending = new Error("pending");
    const save = vi.fn().mockRejectedValueOnce(new Error("conflict")).mockRejectedValue(pending);
    const scheduler = createCheckpointScheduler(save, { retry: error => error === pending });
    await expect(scheduler.flush()).rejects.toThrow("conflict");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(save).toHaveBeenCalledTimes(1);
    await expect(scheduler.flush()).rejects.toBe(pending);
    scheduler.cancel();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(save).toHaveBeenCalledTimes(2);
  });
});
