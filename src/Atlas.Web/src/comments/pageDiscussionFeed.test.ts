// @vitest-environment node
import { effectScope, ref } from "vue";
import { describe, expect, it, vi } from "vitest";
import type { ObjectComment } from "./commentApi";
import { maxBubbles, usePageDiscussionFeed } from "./pageDiscussionFeed";

const comment = (id: string, authorId = "colleague", body = `Text **${id}**`): ObjectComment => ({
  id, authorId, authorName: authorId === "me" ? "Já" : "Petr Svoboda", body, anchor: null, createdAt: `2026-11-25T10:0${id.length}:00Z`,
  editedAt: null, deleted: false, canEdit: false, canDelete: false,
});

function setup(pages: ObjectComment[][]) {
  const answers = [...pages];
  const api = { page: vi.fn(async () => { const items = answers.shift() ?? []; return { items, hasMore: false, count: items.filter(item => !item.deleted).length, canPost: true }; }) };
  let listener: { changed?(change: unknown, version: number): void } = {};
  const close = vi.fn(), stop = vi.fn();
  const views = { open: vi.fn(() => ({ watch: (_id: string, next: typeof listener) => { listener = next; return stop; }, close })) } as never;
  const page = ref<string | undefined>("p1");
  const open = ref(false);
  const scope = effectScope();
  const feed = scope.run(() => usePageDiscussionFeed(() => page.value, { selfId: () => "ME", open: () => open.value }, api, views))!;
  return { api, page, open, scope, feed, notify: (change: unknown = { kind: "comments" }) => listener.changed?.(change, 2), close, stop };
}
const settle = () => new Promise(resolve => setTimeout(resolve));

describe("usePageDiscussionFeed", () => {
  it("only seeds what is there on the first read, then turns a colleague's new comments into bubbles and unread, oldest first", async () => {
    const s = setup([[comment("a")], [comment("bb"), comment("a")], [comment("ccc", "colleague", "Souhlas [odkaz](#/x)"), comment("bb"), comment("a")]]);
    await settle();
    expect(s.feed.count.value).toBe(1);
    expect(s.feed.bubbles.value).toEqual([]);
    s.notify();
    await settle();
    expect(s.feed.count.value).toBe(2);
    expect(s.feed.unread.value).toBe(1);
    s.notify(null);
    await settle();
    expect(s.feed.bubbles.value.map(bubble => bubble.id)).toEqual(["bb", "ccc"]);
    expect(s.feed.bubbles.value[1]).toMatchObject({ authorName: "Petr Svoboda", text: "Souhlas odkaz" });
    expect(s.feed.unread.value).toBe(2);
  });

  it("counts the reader's own comment but never shows or counts it as unread, and ignores other notices", async () => {
    const s = setup([[comment("a")], [comment("bb", "me"), comment("a")]]);
    await settle();
    s.notify({ kind: "text" });
    await settle();
    expect(s.api.page).toHaveBeenCalledTimes(1);
    s.notify();
    await settle();
    expect(s.feed.count.value).toBe(2);
    expect(s.feed.unread.value).toBe(0);
    expect(s.feed.bubbles.value).toEqual([]);
  });

  it("opening the discussion takes the bubbles away and clears the count; what arrives while it is open queues nothing", async () => {
    const s = setup([[comment("a")], [comment("bb"), comment("a")], [comment("ccc"), comment("bb"), comment("a")]]);
    await settle();
    s.notify();
    await settle();
    expect(s.feed.bubbles.value).toHaveLength(1);
    s.open.value = true;
    await settle();
    expect(s.feed.bubbles.value).toEqual([]);
    expect(s.feed.unread.value).toBe(0);
    s.notify();
    await settle();
    expect(s.feed.count.value).toBe(3);
    expect(s.feed.unread.value).toBe(0);
    expect(s.feed.bubbles.value).toEqual([]);
  });

  it("keeps a bubble counted as unread when it is dismissed, and the newest few when many arrive", async () => {
    const many = Array.from({ length: maxBubbles + 3 }, (_, index) => comment(`n${"x".repeat(index)}`));
    const s = setup([[comment("a")], [...many, comment("a")]]);
    await settle();
    s.notify();
    await settle();
    expect(s.feed.unread.value).toBe(many.length);
    expect(s.feed.bubbles.value).toHaveLength(maxBubbles);
    s.feed.dismiss(s.feed.bubbles.value[0]!.id);
    expect(s.feed.bubbles.value).toHaveLength(maxBubbles - 1);
    expect(s.feed.unread.value).toBe(many.length);
  });

  it("reads nothing without a page and lets go of the session when the page goes", async () => {
    const s = setup([[comment("a")]]);
    await settle();
    s.page.value = undefined;
    await settle();
    expect(s.stop).toHaveBeenCalled();
    expect(s.close).toHaveBeenCalled();
    expect(s.feed.count.value).toBe(0);
    s.scope.stop();
  });
});

describe("usePageDiscussionFeed: a reply being written, and late answers", () => {
  it("never pushes out a bubble whose quick reply is open", async () => {
    const s = setup([[comment("a")], [comment("bb"), comment("a")], [comment("ccc"), comment("bb"), comment("a")], [comment("dddd"), comment("ccc"), comment("bb"), comment("a")]]);
    await settle();
    s.notify();
    await settle();
    s.feed.pin("bb", true);
    s.notify();
    await settle();
    s.notify();
    await settle();
    expect(s.feed.bubbles.value.map(bubble => bubble.id)).toEqual(["bb", "dddd"]);
    s.feed.pin("bb", false);
    expect(s.feed.canPost.value).toBe(true);
  });

  it("ignores an older answer that arrives after a newer one", async () => {
    const waiting: ((count: number) => void)[] = [];
    const api = { page: vi.fn(() => new Promise<{ items: ObjectComment[]; hasMore: boolean; count: number; canPost: boolean }>(resolve => waiting.push(count => resolve({ items: [], hasMore: false, count, canPost: true })))) };
    let listener: { changed?(change: unknown, version: number): void } = {};
    const views = { open: vi.fn(() => ({ watch: (_id: string, next: typeof listener) => { listener = next; return vi.fn(); }, close: vi.fn() })) } as never;
    const feed = effectScope().run(() => usePageDiscussionFeed(() => "p1", { selfId: () => "ME", open: () => false }, api, views))!;
    listener.changed?.(null, 2);
    expect(waiting).toHaveLength(2);
    waiting[1]!(5); // the newer read answers first
    await settle();
    waiting[0]!(1); // the older one comes late
    await settle();
    expect(feed.count.value).toBe(5);
  });
});
