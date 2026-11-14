import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createSuggestionWatcher, parsePlacement, parseTemplate, PLACEMENT_MIN_LENGTH, type PlacementSuggestion } from "./creationSuggestions";

const suggestion = (id: string): PlacementSuggestion => ({ parentId: id, parentTitle: id, path: [{ id, title: id }] });
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("creation suggestion watcher", () => {
  it("waits 400 ms and 8 characters and ignores a late answer", async () => {
    const calls: { title: string; signal: AbortSignal; answer: ReturnType<typeof deferred<PlacementSuggestion | null>> }[] = [];
    const results: (PlacementSuggestion | null)[] = [];
    const watcher = createSuggestionWatcher<PlacementSuggestion>({
      find: (title, signal) => { const answer = deferred<PlacementSuggestion | null>(); calls.push({ title, signal, answer }); return answer.promise; },
      onResult: value => results.push(value),
      minLength: PLACEMENT_MIN_LENGTH,
    });

    watcher.update("vpn mob");
    watcher.update("  vpn mob  ");
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toEqual([]);

    watcher.update("vpn mobil");
    await vi.advanceTimersByTimeAsync(399);
    expect(calls).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(calls.map(call => call.title)).toEqual(["vpn mobil"]);

    // A newer title aborts the one in flight; its answer, however late, is never shown.
    watcher.update("vpn mobil heslo");
    expect(calls[0]!.signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(400);
    calls[1]!.answer.resolve(suggestion("new"));
    await vi.advanceTimersByTimeAsync(0);
    calls[0]!.answer.resolve(suggestion("old"));
    await vi.advanceTimersByTimeAsync(0);
    expect(results.filter(Boolean)).toEqual([suggestion("new")]);

    // 204 is nothing, and cancel() silences what is pending.
    watcher.update("vpn mobil heslo2");
    await vi.advanceTimersByTimeAsync(400);
    calls[2]!.answer.resolve(null);
    await vi.advanceTimersByTimeAsync(0);
    expect(results.at(-1)).toBeNull();
    watcher.update("vpn mobil heslo3");
    watcher.cancel();
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toHaveLength(3);
  });
  it("answers nothing, not an error, for a failed lookup", async () => {
    const results: unknown[] = [];
    const watcher = createSuggestionWatcher({ find: () => Promise.reject(new Error("503")), onResult: value => results.push(value) });
    watcher.update("vpn mobil");
    await vi.advanceTimersByTimeAsync(400);
    expect(results.at(-1)).toBeNull();
  });
});

describe("reading the answers", () => {
  it("accepts the documented shapes and treats anything else as nothing", () => {
    expect(parsePlacement({ parentId: null, parentTitle: null, path: [] })).toEqual({ parentId: null, parentTitle: null, path: [] });
    expect(parsePlacement({ parentId: "a", parentTitle: "A", path: [{ id: "a", title: "A" }, { nope: 1 }] })?.path).toEqual([{ id: "a", title: "A" }]);
    expect(parsePlacement(undefined)).toBeNull();
    expect(parsePlacement({ parentId: 5 })).toBeNull();
    expect(parseTemplate({ templateId: "t", title: "T", matchedBy: "meaning" })).toEqual({ templateId: "t", title: "T", matchedBy: "meaning" });
    expect(parseTemplate({ title: "T" })).toBeNull();
  });
});
