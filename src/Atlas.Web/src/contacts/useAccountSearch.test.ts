import { describe, expect, it, vi } from "vitest";
import { effectScope, nextTick } from "vue";
import { flushPromises } from "@vue/test-utils";
import { useAccountSearch } from "./useAccountSearch";

const person = (id: string) => ({ id, name: `Osoba ${id}`, username: id });

describe("useAccountSearch", () => {
  it("offers only the accounts every page shares, reads at once on start and only the newest answer lands", async () => {
    vi.useFakeTimers();
    const people = vi.fn(async (pageId: string, text: string) => (text === "old" ? [person("x")] : pageId === "p1" ? [person("a"), person("b")] : [person("b")]));
    const scope = effectScope();
    const search = scope.run(() => useAccountSearch(() => ["p1", "p2"], people))!;
    search.start();
    await flushPromises();
    expect(search.options.value.map((option) => option.value)).toEqual(["b"]);
    search.query.value = "old";
    await nextTick();
    search.query.value = "b";
    await nextTick();
    await vi.advanceTimersByTimeAsync(200);
    await flushPromises();
    expect(people.mock.calls.some(([, text]) => text === "old")).toBe(false);
    expect(search.options.value.map((option) => option.value)).toEqual(["b"]);
    scope.stop();
    vi.useRealTimers();
  });
});
