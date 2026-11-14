// @vitest-environment node
import { afterEach, expect, it, vi } from "vitest";
import { createDebouncedLookup } from "./debouncedLookup";

afterEach(() => { vi.useRealTimers(); });

it("waits for the pause, answers only the newest text, asks at once with delay 0, and reports a failure to onError when given", async () => {
  vi.useFakeTimers();
  const found: string[] = [], errors: unknown[] = [];
  let fail = false;
  const lookup = createDebouncedLookup<string>({
    find: async text => { if (fail) throw new Error("down"); return text; },
    onResult: value => found.push(value), empty: () => "", delayMs: 100, minLength: 0, onError: failure => errors.push(failure),
  });
  lookup.update("a");
  lookup.update("ab");
  await vi.advanceTimersByTimeAsync(100);
  expect(found).toEqual(["ab"]);
  lookup.update("abc", 0);
  await Promise.resolve();
  expect(found).toEqual(["ab", "abc"]);
  fail = true;
  lookup.update("x", 0);
  await Promise.resolve(); await Promise.resolve();
  expect(found).toEqual(["ab", "abc"]);
  expect(errors).toHaveLength(1);
});

it("answers empty() for a failure without onError, as before", async () => {
  const found: string[] = [];
  const lookup = createDebouncedLookup<string>({ find: async () => { throw new Error("down"); }, onResult: value => found.push(value), empty: () => "-", delayMs: 0, minLength: 0 });
  lookup.update("a", 0);
  await Promise.resolve(); await Promise.resolve();
  expect(found).toEqual(["-"]);
});
