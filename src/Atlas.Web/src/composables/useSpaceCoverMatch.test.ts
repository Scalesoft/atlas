// @vitest-environment node
import { effectScope, nextTick, ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SpaceCoverMatchInput } from "../spaceCover";
import { useSpaceCoverMatch } from "./useSpaceCoverMatch";

type Answer = { catalogKey: string | null };
const named = (name: string): SpaceCoverMatchInput => ({ name, description: "", icon: "book-open" });

describe("useSpaceCoverMatch", () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  // The preview follows typing: one request per pause, never an older answer over a newer one, and never an error.
  it("asks once for the last of quick inputs, ignores a superseded answer, gives no answer on a failure and no drawing on a blank name", async () => {
    const pending: { resolve: (answer: Answer) => void; reject: (reason: Error) => void }[] = [];
    const match = vi.fn((_input: SpaceCoverMatchInput, _signal: AbortSignal) => new Promise<Answer>((resolve, reject) => { pending.push({ resolve, reject }); }));
    const input = ref<SpaceCoverMatchInput>();
    const scope = effectScope();
    const key = scope.run(() => useSpaceCoverMatch(input, match))!;
    expect(key.value).toBeUndefined();

    input.value = named("Zál"); await nextTick();
    await vi.advanceTimersByTimeAsync(200);
    input.value = named("Zálohy"); await nextTick();
    await vi.advanceTimersByTimeAsync(300);
    expect(match).toHaveBeenCalledTimes(1);
    expect(match.mock.calls[0]![0]).toEqual(named("Zálohy"));

    // The name changes before the answer comes: the older request is aborted and its late answer does not show.
    input.value = named("Databáze"); await nextTick();
    await vi.advanceTimersByTimeAsync(300);
    expect(match.mock.calls[0]![1].aborted).toBe(true);
    pending[0]!.resolve({ catalogKey: "it-backup" }); await vi.advanceTimersByTimeAsync(0);
    expect(key.value).toBeUndefined();
    pending[1]!.resolve({ catalogKey: "it-database" }); await vi.advanceTimersByTimeAsync(0);
    expect(key.value).toBe("it-database");

    // A failed request is no answer (not "no drawing"), and the next one is asked as usual.
    input.value = named("Tým Kobra"); await nextTick();
    await vi.advanceTimersByTimeAsync(300);
    pending[2]!.reject(new Error("offline")); await vi.advanceTimersByTimeAsync(0);
    expect(key.value).toBeUndefined();
    input.value = named("Porady"); await nextTick();
    await vi.advanceTimersByTimeAsync(300);
    pending[3]!.resolve({ catalogKey: "ops-meetings" }); await vi.advanceTimersByTimeAsync(0);
    expect(key.value).toBe("ops-meetings");

    input.value = named("  "); await nextTick();
    expect(key.value).toBeNull();
    await vi.advanceTimersByTimeAsync(300);
    expect(match).toHaveBeenCalledTimes(4);

    // The owner goes away while a request is waiting for its pause: it is never sent.
    input.value = named("Zálohy"); await nextTick();
    scope.stop();
    await vi.advanceTimersByTimeAsync(300);
    expect(match).toHaveBeenCalledTimes(4);
  });
});
