// @vitest-environment node
import { effectScope, ref } from "vue";
import { describe, expect, it, vi } from "vitest";
import { usePageCommentCount } from "./pageCommentCount";

function setup(counts: number[]) {
  const answers = [...counts];
  const api = { counts: vi.fn(async (ids: readonly string[]) => ({ [ids[0]!.toUpperCase()]: answers.shift() ?? 0 })) };
  let listener: { changed?(change: unknown, version: number): void } = {};
  const close = vi.fn(), stop = vi.fn();
  const views = { open: vi.fn(() => ({ watch: (_id: string, next: typeof listener) => { listener = next; return stop; }, close })) } as never;
  const page = ref<string | undefined>("p1");
  const scope = effectScope();
  const result = scope.run(() => usePageCommentCount(() => page.value, api, views))!;
  return { api, views, page, scope, result, notify: (change: unknown) => listener.changed?.(change, 2), close, stop };
}
const settle = () => new Promise(resolve => setTimeout(resolve));

describe("usePageCommentCount", () => {
  it("reads the page's count (the key of any case), then again at a comments notice, and says fresh only once it grew", async () => {
    const s = setup([2, 2, 3]);
    await settle();
    expect(s.result.count.value).toBe(2);
    expect(s.result.fresh.value).toBe(false);
    s.notify({ kind: "comments" });
    await settle();
    expect(s.result.fresh.value).toBe(false);
    s.notify(null);
    await settle();
    expect(s.result.count.value).toBe(3);
    expect(s.result.fresh.value).toBe(true);
    s.result.seen();
    expect(s.result.fresh.value).toBe(false);
  });

  it("ignores other notices, reads nothing without a page, and lets go of the session when the page goes", async () => {
    const s = setup([1]);
    await settle();
    s.notify({ kind: "text" });
    await settle();
    expect(s.api.counts).toHaveBeenCalledTimes(1);
    s.page.value = undefined;
    await settle();
    expect(s.stop).toHaveBeenCalled();
    expect(s.close).toHaveBeenCalled();
    expect(s.result.count.value).toBe(0);
    s.scope.stop();
  });
});
