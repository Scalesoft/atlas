// @vitest-environment node
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createSimilarPagesWatcher, titleDrift, type SimilarPage } from "./similarPages";

const page = (id: string, extra: Partial<SimilarPage> = {}): SimilarPage => ({ id, spaceId: "s", spaceName: "Docs", title: `Page ${id}`, isDraft: false, matchedBy: "title", headingTitle: null, headingId: null, score: 0.8, ...extra });
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => vi.useFakeTimers());
afterEach(() => vi.useRealTimers());

describe("similar pages watcher", () => {
  it("waits 400 ms and 8 characters, aborts the previous request and ignores a late answer", async () => {
    const calls: { title: string; signal: AbortSignal; answer: ReturnType<typeof deferred<SimilarPage[]>> }[] = [];
    const results: SimilarPage[][] = [];
    const watcher = createSimilarPagesWatcher({
      find: (title, signal) => { const answer = deferred<SimilarPage[]>(); calls.push({ title, signal, answer }); return answer.promise; },
      onResult: items => results.push(items),
    });

    watcher.update("vpn");
    watcher.update("  vpn mob  ");
    await vi.advanceTimersByTimeAsync(1000);
    expect(calls).toEqual([]);

    watcher.update("vpn mobil");
    await vi.advanceTimersByTimeAsync(399);
    expect(calls).toEqual([]);
    watcher.update("vpn mobilní");
    await vi.advanceTimersByTimeAsync(399);
    expect(calls).toEqual([]);
    await vi.advanceTimersByTimeAsync(1);
    expect(calls.map(call => call.title)).toEqual(["vpn mobilní"]);

    watcher.update("vpn mobilní přístup");
    expect(calls[0]!.signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(400);
    expect(calls.map(call => call.title)).toEqual(["vpn mobilní", "vpn mobilní přístup"]);

    calls[0]!.answer.resolve([page("late")]);
    await vi.advanceTimersByTimeAsync(0);
    expect(results.flat()).toEqual([]);
    calls[1]!.answer.resolve([page("fresh")]);
    await vi.advanceTimersByTimeAsync(0);
    expect(results.flat()).toEqual([page("fresh")]);
  });

  it("clears the result for a short title, on a failed lookup and after cancel", async () => {
    const results: SimilarPage[][] = [];
    const find = vi.fn<(title: string, signal: AbortSignal) => Promise<SimilarPage[]>>().mockRejectedValue(new Error("down"));
    const watcher = createSimilarPagesWatcher({ find, onResult: items => results.push(items) });

    watcher.update("short");
    expect(results).toEqual([[]]);
    watcher.update("a long enough title");
    await vi.advanceTimersByTimeAsync(400);
    expect(find).toHaveBeenCalledTimes(1);
    expect(results).toEqual([[], []]);

    find.mockResolvedValue([page("1")]);
    watcher.update("another long title");
    watcher.cancel();
    await vi.advanceTimersByTimeAsync(1000);
    expect(find).toHaveBeenCalledTimes(1);
    expect(results).toHaveLength(2);
  });

  it("measures how far a title moved from the one that was dismissed", () => {
    expect(titleDrift("Připojení na VPN", "Připojení na VPN")).toBe(0);
    expect(titleDrift("Připojení na VPN", "Připojení na VPN z")).toBe(2);
    expect(titleDrift("Připojení na VPN", "Zálohování databáze")).toBeGreaterThan(3);
  });
});
