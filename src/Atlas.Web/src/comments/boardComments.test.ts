// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { BoardViewItem } from "../boards/boardSession";
import { commentedObjectsOf, discussionOf } from "./boardComments";

const item = (id: string, kind: BoardViewItem["kind"], extra: Partial<BoardViewItem> = {}): BoardViewItem =>
  ({ id, kind, x: 0, y: 0, width: 200, height: 120, z: 1, ...extra });

describe("the discussion of a board's detail", () => {
  it("is the board's for the board itself and for its own items (anchored at the item), the object's for a card of one", () => {
    expect(discussionOf(null, "b1")).toEqual({ objectId: "b1", anchor: null, ofBoard: true });
    expect(discussionOf(item("n1", "note"), "b1")).toEqual({ objectId: "b1", anchor: "n1", ofBoard: true });
    expect(discussionOf(item("p1", "page", { targetResourceId: "page-1" }), "b1")).toEqual({ objectId: "page-1", anchor: null, ofBoard: false });
    expect(discussionOf(item("o1", "object", { targetResourceId: "contact-1" }), "b1")).toEqual({ objectId: "contact-1", anchor: null, ofBoard: false });
  });

  it("asks the counts of the board and of the objects its live cards show, each once and at most a hundred", () => {
    const cards = Array.from({ length: 120 }, (_, index) => item(`c${index}`, "page", { targetResourceId: `page-${index % 110}` }));
    const ids = commentedObjectsOf("b1", [item("gone", "page", { targetResourceId: "trashed", deleted: true }), item("n", "note"), ...cards]);
    expect(ids[0]).toBe("b1");
    expect(ids).toHaveLength(100);
    expect(new Set(ids).size).toBe(100);
    expect(ids).not.toContain("trashed");
  });
});
