// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { PagePrefetchMap } from "./pagePrefetchMap";

afterEach(() => vi.useRealTimers());

it("keeps no expired read and only a few recent ones, however many pages were pointed at", () => {
  vi.useFakeTimers();
  const map = new PagePrefetchMap<{ at: number }>(10_000);
  for (let i = 0; i < 200; i++) { map.set(`p${i}`, { at: Date.now() }); vi.advanceTimersByTime(1000); }
  // 200 hovers, one per second: the reads of the last 10 s are usable, capped at three.
  expect([...map.keys()]).toEqual(["p197", "p198", "p199"]);
  vi.advanceTimersByTime(60_000);
  map.set("last", { at: Date.now() });
  expect([...map.keys()]).toEqual(["last"]);
});
