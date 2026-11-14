// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import type { CommentApi, CommentPage, ObjectComment } from "./commentApi";
import { cleanCommentText, createCommentThread, isCommentsChange, mergeComments } from "./commentThread";

let clock = Date.parse("2026-10-26T10:00:00Z");
function comment(id: string, overrides: Partial<ObjectComment> = {}): ObjectComment {
  return { id, authorId: "a1", authorName: "Jana", body: `Text ${id}`, anchor: null, createdAt: new Date(clock++).toISOString(), editedAt: null,
    deleted: false, canEdit: true, canDelete: true, ...overrides };
}
const page = (items: ObjectComment[], hasMore = false, count = items.length, canPost = true): CommentPage => ({ items, hasMore, count, canPost });
function fakeApi(pages: Record<string, CommentPage>): CommentApi & { calls: string[] } {
  const calls: string[] = [];
  return {
    calls,
    page: vi.fn(async (_id: string, before?: string) => { calls.push(before ?? "newest"); return pages[before ?? "newest"]!; }),
    post: vi.fn(async (_id: string, body: string, anchor?: string | null) => comment("new", { body, anchor: anchor ?? null })),
    edit: vi.fn(async (_id: string, commentId: string, body: string) => comment(commentId, { body, editedAt: "2026-10-26T11:00:00Z" })),
    remove: vi.fn(async () => undefined),
    counts: vi.fn(async () => ({})),
  };
}

describe("mergeComments", () => {
  it("keeps one of each comment, the newer read winning, newest first by time and then id", () => {
    const a = comment("a", { createdAt: "2026-10-26T09:00:00Z" });
    const b = comment("b", { createdAt: "2026-10-26T09:00:00Z" });
    const c = comment("c", { createdAt: "2026-10-26T10:00:00Z" });
    expect(mergeComments([a, b], [c, { ...a, body: "Upraveno" }]).map(x => `${x.id}:${x.body}`)).toEqual(["c:Text c", "b:Text b", "a:Upraveno"]);
    // A whole second is written without a fraction: still older than a tenth after it.
    const whole = comment("w", { createdAt: "2026-10-26T11:00:00Z" }), tenth = comment("t", { createdAt: "2026-10-26T11:00:00.1Z" });
    expect(mergeComments([whole], [tenth]).map(x => x.id)).toEqual(["t", "w"]);
  });
});

describe("isCommentsChange", () => {
  it("tells a comment notice from any other change of the object", () => {
    expect(isCommentsChange({ kind: "comments", count: 2, latestId: "x" })).toBe(true);
    expect(isCommentsChange({ kind: "working" })).toBe(false);
    expect(isCommentsChange(null)).toBe(false);
  });
});

describe("cleanCommentText", () => {
  it("keeps what the server takes: tabs as spaces, no control or bidi characters, at most 10 000 characters, no trailing space", () => {
    expect(cleanCommentText("a\tb‮\r\nc  \n")).toBe("a    b\nc");
    expect(cleanCommentText("x".repeat(10_050))).toHaveLength(10_000);
  });
});

describe("createCommentThread", () => {
  it("loads the newest page, then older ones by the last comment, and says when there are no more", async () => {
    const newest = Array.from({ length: 20 }, (_, i) => comment(`n${i}`)).reverse();
    const older = [comment("o1"), comment("o0")].map((x, i) => ({ ...x, createdAt: `2026-10-01T0${i}:00:00Z` })).reverse();
    const api = fakeApi({ newest: page(newest, true, 22), n0: page(older) });
    const thread = createCommentThread("obj", api);
    await thread.load();
    expect([thread.items.value.length, thread.hasMore.value, thread.count.value, thread.status.value]).toEqual([20, true, 22, "ready"]);
    await thread.older();
    expect(api.calls).toEqual(["newest", "n0"]);
    expect(thread.items.value.map(x => x.id).slice(-2)).toEqual(["o0", "o1"]);
    expect(thread.hasMore.value).toBe(false);
  });

  it("posts at the top, edits in place and leaves a placeholder for a deleted comment", async () => {
    const first = comment("first");
    const api = fakeApi({ newest: page([first]) });
    // The server answers an edit with the comment's own time.
    api.edit = vi.fn(async (_id: string, _commentId: string, body: string) => ({ ...first, body, editedAt: "2026-10-26T11:00:00Z" }));
    const thread = createCommentThread("obj", api);
    await thread.load();
    await thread.post("Ahoj **všem**", "uvod");
    expect(api.post).toHaveBeenCalledWith("obj", "Ahoj **všem**", "uvod");
    expect(thread.items.value.map(x => x.id)).toEqual(["new", "first"]);
    expect(thread.count.value).toBe(2);
    await thread.edit("first", "Opraveno");
    expect(thread.items.value[1]).toMatchObject({ id: "first", body: "Opraveno", editedAt: "2026-10-26T11:00:00Z" });
    await thread.remove("first");
    expect(thread.items.value[1]).toMatchObject({ id: "first", deleted: true, body: "", canEdit: false, canDelete: false });
    expect(thread.count.value).toBe(1);
  });

  it("reads the newest page again on a notice and keeps the older comments it already shows", async () => {
    const old = comment("old");
    const api = fakeApi({ newest: page([old]) });
    const thread = createCommentThread("obj", api);
    await thread.load();
    const fresh = comment("fresh", { authorName: "Petr" });
    api.page = vi.fn(async () => page([fresh, old], false, 2));
    await thread.refresh();
    expect(thread.items.value.map(x => x.id)).toEqual(["fresh", "old"]);
    expect(thread.count.value).toBe(2);
  });

  it("starts over from the newest page when more arrived than one page shows", async () => {
    const old = comment("old");
    const api = fakeApi({ newest: page([old]) });
    const thread = createCommentThread("obj", api);
    await thread.load();
    const burst = Array.from({ length: 20 }, (_, i) => comment(`b${i}`)).reverse();
    api.page = vi.fn(async () => page(burst, true, 21));
    await thread.refresh();
    expect(thread.items.value).toHaveLength(20);
    expect(thread.items.value.some(x => x.id === "old")).toBe(false);
    expect(thread.hasMore.value).toBe(true);
  });

  it("tells a missing or unreadable object from a failure", async () => {
    const { ApiError } = await import("../api");
    const api = fakeApi({});
    api.page = vi.fn(async () => { throw new ApiError(404, "Objekt nebyl nalezen."); });
    const thread = createCommentThread("obj", api);
    await thread.load();
    expect(thread.status.value).toBe("missing");
    api.page = vi.fn(async () => { throw new Error("Spojení selhalo"); });
    await thread.load();
    expect([thread.status.value, thread.error.value]).toEqual(["failed", "Spojení selhalo"]);
  });
});
