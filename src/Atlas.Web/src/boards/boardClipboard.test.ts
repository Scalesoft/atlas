// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { BoardItem } from "./boardModel";
import type { BoardViewItem } from "./boardSession";
import { copyPayload, pasteChanges, readPayload } from "./boardClipboard";

const note = (id: string, extra: Partial<BoardItem> = {}): BoardItem => ({ id, kind: "note", x: 0, y: 0, width: 200, height: 200, z: 1, text: id, color: "yellow", ...extra });
const link = (id: string, from: string, to: string): BoardItem => ({ id, kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from, to });
let counter = 0;
const newId = () => `n${++counter}`;

describe("board clipboard (interaction spec §3.2)", () => {
  it("copies the selection with the connectors between it as JSON for boards, and its text for other applications", () => {
    const page: BoardViewItem = { id: "p", kind: "page", x: 0, y: 0, width: 280, height: 140, z: 2, targetResourceId: "r1", target: { title: "Příručka", available: true } };
    const payload = copyPayload([note("a"), note("b", { x: 300 }), page, note("o"), link("c", "a", "b"), link("d", "a", "o")], ["a", "b", "p"])!;
    expect(readPayload(payload.json)!.map(item => item.id)).toEqual(["a", "b", "p", "c"]);
    expect(payload.json).not.toContain("Příručka");
    expect(payload.text).toBe("a\n\nb\n\nPříručka");
    expect(copyPayload([note("a")], [])).toBeNull();
  });

  it("D12: writes a block's or a contact card's Markdown as their plain text, which a page pastes as the component", () => {
    const block: BoardViewItem = { id: "b", kind: "block", x: 0, y: 0, width: 300, height: 120, z: 1, props: { block: { type: "contactCard" } } };
    const fence = "```a-contact\n- name: Jana\n```";
    const markdown = (item: BoardViewItem) => (item.kind === "block" ? fence : undefined);
    expect(copyPayload([note("a"), block], ["a", "b"], markdown)!.text).toBe(`a\n\n${fence}`);
    // Without Markdown (the relations graph, a block whose projection failed) the plain text stays as before.
    expect(copyPayload([note("a"), block], ["a", "b"])!.text).toBe("a");
  });

  it("pastes as new items centred where it goes, on top, with the connectors joined to the copies", () => {
    counter = 0;
    const copied = readPayload(copyPayload([note("a"), note("b", { x: 300, z: 4 }), link("c", "a", "b")], ["a", "b"])!.json)!;
    const { changes, ids } = pasteChanges(copied, { x: 1000, y: 1000 }, newId, 10);
    expect(ids).toEqual(["n1", "n2", "n3"]);
    expect(changes).toEqual([
      { type: "create", item: expect.objectContaining({ id: "n1", kind: "note", x: 750, y: 900, z: 10, text: "a" }) },
      { type: "create", item: expect.objectContaining({ id: "n2", kind: "note", x: 1050, y: 900, z: 11, text: "b" }) },
      { type: "create", item: expect.objectContaining({ id: "n3", kind: "connector", from: "n1", to: "n2" }) },
    ]);
  });

  it("reads only its own format, and from it only well-formed items, with clean text", () => {
    expect(readPayload("plain text")).toBeNull();
    expect(readPayload(JSON.stringify({ something: "else" }))).toBeNull();
    const items = readPayload(JSON.stringify({ "atlas.board": 1, items: [
      { id: "a", kind: "note", x: 0, y: 0, width: 10, height: 10, z: 1, text: "ok\u0000\ttab" },
      { id: "b", kind: "note", x: "1", y: 0, width: 10, height: 10, z: 1 },
      { id: "c", kind: "script", x: 0, y: 0, width: 10, height: 10, z: 1 },
      { id: "d", kind: "note", x: 0, y: 0, width: 10, height: 10, z: 1, color: "neon" },
      { id: "e", kind: "block", x: 0, y: 0, width: 10, height: 10, z: 1, color: "blue-4" },
      { id: "f", kind: "block", x: 0, y: 0, width: 10, height: 10, z: 1, color: "coral" },
    ] }));
    expect(items).toEqual([{ id: "a", kind: "note", x: 0, y: 0, width: 10, height: 10, z: 1, text: "ok    tab" }, { id: "d", kind: "note", x: 0, y: 0, width: 10, height: 10, z: 1 },
      { id: "e", kind: "block", x: 0, y: 0, width: 10, height: 10, z: 1, color: "blue-4" }, { id: "f", kind: "block", x: 0, y: 0, width: 10, height: 10, z: 1 }]);
  });

  it("copies a group with its members and pastes the copies inside the copied group", () => {
    counter = 0;
    const group: BoardItem = { id: "g", kind: "group", x: 0, y: 0, width: 500, height: 300, z: 0, text: "Tým" };
    const copied = readPayload(copyPayload([group, note("a", { parent: "g" }), note("o", { parent: "elsewhere" })], ["g", "a", "o"])!.json)!;
    expect(copied.map(item => item.parent)).toEqual([undefined, "g", "elsewhere"]);
    const { changes } = pasteChanges(copied, { x: 0, y: 0 }, newId, 1);
    expect(changes.filter(change => change.type === "parent")).toEqual([{ type: "parent", itemId: "n2", parentId: "n1" }]);
  });
});
