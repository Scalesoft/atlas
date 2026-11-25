import { describe, expect, it } from "vitest";
import { atlasObjectDragType, atlasPageDragType, atlasPageSectionDragType, carriesReference, maxPasteItems, objectPayload, pageSectionPayload, readObjectPayload, readObjectsPayload, readReferenceDrop, startObjectDrag } from "./boardDrop";
import { dropAccepts, placedContacts, readDrop, scanDrop, type PasteContext } from "./boardPaste";

const id = "3f2b8c1e-5a4d-4e6f-9a1b-2c3d4e5f6a7b";
const other = "a1b2c3d4-0000-4000-8000-000000000001";
const atlas: PasteContext = { origin: "https://atlas.example", youtrackBaseUrls: ["https://yt.example/"] };

/** A DataTransfer as a browser gives it while dropping: the types it carries and what each holds. */
function transfer(data: Record<string, string>) {
  return { types: Object.keys(data), getData: (type: string) => data[type] ?? "" };
}

describe("readDrop: a page dragged from the page tree", () => {
  it("makes a page card from the id the tree puts in its drag payload", () => {
    expect(readDrop(transfer({ [atlasPageDragType]: id }), atlas)).toEqual([{ kind: "page", resourceId: id }]);
  });

  it("is the payload the page tree writes", () => {
    expect(atlasPageDragType).toBe("application/x-atlas-page");
  });

  it("takes several ids, in lower case and once each, and ignores what is no page id", () => {
    expect(readDrop(transfer({ [atlasPageDragType]: `${id.toUpperCase()}\n${other}\n${id}\nnot-an-id` }), atlas)).toEqual([
      { kind: "page", resourceId: id },
      { kind: "page", resourceId: other },
    ]);
  });

  it("prefers the payload to the link a browser adds to the same drag", () => {
    const data = transfer({ [atlasPageDragType]: id, "text/uri-list": `https://atlas.example/#/pages/${other}`, "text/plain": `https://atlas.example/#/pages/${other}` });
    expect(readDrop(data, atlas)).toEqual([{ kind: "page", resourceId: id }]);
  });

  it("falls back to the link when the payload holds no usable id", () => {
    const data = transfer({ [atlasPageDragType]: "junk", "text/uri-list": `https://atlas.example/#/pages/${other}` });
    expect(readDrop(data, atlas)).toEqual([{ kind: "page", resourceId: other }]);
  });
});

describe("readDrop: a section of a page", () => {
  it("makes a card of one section from the payload of a heading handle or an outline item", () => {
    expect(atlasPageSectionDragType).toBe("application/x-atlas-page-section");
    expect(readDrop(transfer({ [atlasPageSectionDragType]: pageSectionPayload(id.toUpperCase(), "uvod-1") }), atlas)).toEqual([{ kind: "page", resourceId: id, anchor: "uvod-1" }]);
  });

  it("prefers the section to the page and the link that come with the same drag", () => {
    const data = transfer({
      [atlasPageSectionDragType]: pageSectionPayload(id, "uvod"), [atlasPageDragType]: other, "text/uri-list": `https://atlas.example/#/pages/${other}`,
    });
    expect(readDrop(data, atlas)).toEqual([{ kind: "page", resourceId: id, anchor: "uvod" }]);
  });

  it.each([
    ["no JSON", "uvod"],
    ["no page id", JSON.stringify({ resourceId: "x", anchor: "uvod" })],
    ["no anchor", JSON.stringify({ resourceId: id })],
    ["an empty anchor", JSON.stringify({ resourceId: id, anchor: "" })],
    ["an anchor that is too long", JSON.stringify({ resourceId: id, anchor: "x".repeat(201) })],
    ["an anchor with a control character", JSON.stringify({ resourceId: id, anchor: "a\u0007b" })],
    ["a number", "42"],
    ["null", "null"],
  ])("falls back to what else the drag carries when the section payload is %s", (_, payload) => {
    const data = transfer({ [atlasPageSectionDragType]: payload, [atlasPageDragType]: other });
    expect(readDrop(data, atlas)).toEqual([{ kind: "page", resourceId: other }]);
  });
});

describe("readDrop: links and text", () => {
  it("reads a dragged link (a search result, an address) as a pasted one, once although two types carry it", () => {
    const url = `https://atlas.example/#/pages/${id}`;
    expect(readDrop(transfer({ "text/uri-list": url, "text/plain": url }), atlas)).toEqual([{ kind: "page", resourceId: id }]);
    expect(readDrop(transfer({ "text/uri-list": "https://yt.example/issue/ATL-4", "text/plain": "https://yt.example/issue/ATL-4" }), atlas)).toEqual([{ kind: "youtrack", issueId: "ATL-4" }]);
  });

  it("skips the comment lines of a URI list and takes every link of it", () => {
    const list = `# dragged from the search\r\nhttps://atlas.example/#/pages/${id}\r\n# another\r\nhttps://yt.example/issue/ATL-5\r\n`;
    expect(readDrop(transfer({ "text/uri-list": list }), atlas)).toEqual([{ kind: "page", resourceId: id }, { kind: "youtrack", issueId: "ATL-5" }]);
  });

  it("makes a note of a link that is no page or issue of ours", () => {
    expect(readDrop(transfer({ "text/uri-list": "https://example.org/clanek" }), atlas)).toEqual([{ kind: "note", text: "https://example.org/clanek" }]);
  });

  it("turns dragged text into notes, a line each", () => {
    expect(readDrop(transfer({ "text/plain": "Příliš žluťoučký kůň\nATL-7" }), atlas)).toEqual([
      { kind: "note", text: "Příliš žluťoučký kůň" },
      { kind: "youtrack", issueId: "ATL-7" },
    ]);
  });

  it("cleans dragged text as pasted text: cells of a spreadsheet row are words, refused characters are gone", () => {
    const bidi = String.fromCharCode(0x202e);
    expect(readDrop(transfer({ "text/plain": `A\t\tB${bidi}C\u0000` }), atlas)).toEqual([{ kind: "note", text: "A BC" }]);
  });

  it("makes nothing of an empty drag or of files alone (no image yet)", () => {
    expect(readDrop(transfer({}), atlas)).toEqual([]);
    expect(readDrop(transfer({ Files: "" }), atlas)).toEqual([]);
    expect(readDrop(transfer({ "text/plain": "  \n " }), atlas)).toEqual([]);
  });

  it("stops at 100 and counts what it left out", () => {
    const text = Array.from({ length: 105 }, (_, index) => `Poznámka ${index}`).join("\n");
    expect(readDrop(transfer({ "text/plain": text }), atlas)).toHaveLength(maxPasteItems);
    expect(scanDrop(transfer({ "text/plain": text }), atlas).omitted).toBe(5);
    const ids = Array.from({ length: 103 }, (_, index) => `00000000-0000-4000-8000-${String(index).padStart(12, "0")}`).join("\n");
    const pages = scanDrop(transfer({ [atlasPageDragType]: ids }), atlas);
    expect(pages.items).toHaveLength(maxPasteItems);
    expect(pages.omitted).toBe(3);
  });
});

describe("dropAccepts", () => {
  it("accepts what readDrop can read, by type alone (the data is hidden until the drop)", () => {
    expect(dropAccepts([atlasPageDragType])).toBe(true);
    expect(dropAccepts([atlasPageSectionDragType])).toBe(true);
    expect(dropAccepts(["text/uri-list", "text/plain"])).toBe(true);
    expect(dropAccepts(["text/plain"])).toBe(true);
  });

  it("refuses files, html alone and nothing", () => {
    expect(dropAccepts(["Files"])).toBe(false);
    expect(dropAccepts(["text/html"])).toBe(false);
    expect(dropAccepts([])).toBe(false);
  });

  it("takes the DOMStringList-like object a drag event carries", () => {
    expect(dropAccepts({ length: 1, 0: "text/plain" } as unknown as readonly string[])).toBe(true);
  });
});

describe("readObjectPayload: an object dragged out of a page or a list", () => {
  it("reads what objectPayload writes, id in lower case", () => {
    expect(readObjectPayload(objectPayload({ id: id.toUpperCase(), typeId: "atlas.contact", title: "Jan", anchor: "req-1" })))
      .toEqual({ id, typeId: "atlas.contact", title: "Jan", anchor: "req-1" });
  });

  it("refuses junk, a bad id, a bad type and a bad anchor", () => {
    expect(readObjectPayload("{")).toBeUndefined();
    expect(readObjectPayload(objectPayload({ id: "nope", typeId: "atlas.board" }))).toBeUndefined();
    expect(readObjectPayload(objectPayload({ id, typeId: "Atlas Board" }))).toBeUndefined();
    expect(readObjectPayload(JSON.stringify({ id, typeId: "atlas.board", anchor: "" }))).toBeUndefined();
  });
});

describe("readReferenceDrop: what the tree places as a reference node", () => {
  it("reads an object first, then a section, then a page id", () => {
    expect(readReferenceDrop(transfer({ [atlasObjectDragType]: objectPayload({ id, typeId: "atlas.contact", title: "Jan" }), [atlasPageDragType]: other })))
      .toEqual({ targetId: id, typeId: "atlas.contact", title: "Jan" });
    expect(readReferenceDrop(transfer({ [atlasPageSectionDragType]: pageSectionPayload(id, "uvod"), [atlasPageDragType]: other })))
      .toEqual({ targetId: id, typeId: "atlas.page", anchor: "uvod" });
    expect(readReferenceDrop(transfer({ [atlasPageDragType]: other.toUpperCase() }))).toEqual({ targetId: other, typeId: "atlas.page" });
  });

  it("accepts only its own types and nothing malformed", () => {
    expect(carriesReference([atlasObjectDragType])).toBe(true);
    expect(carriesReference(["text/plain", "text/uri-list"])).toBe(false);
    expect(readReferenceDrop(transfer({ [atlasObjectDragType]: "{", "text/plain": id }))).toBeUndefined();
  });
});

describe("startObjectDrag: the one writer of a dragged object", () => {
  function written() {
    const data = new Map<string, string>();
    return { data, event: { dataTransfer: { setData: (type: string, value: string) => data.set(type, value), effectAllowed: "" } } as unknown as DragEvent };
  }

  it("carries the object for the tree and a board, and a page also as a page", () => {
    const { data, event } = written();
    startObjectDrag(event, { id, typeId: "atlas.page", title: "Úvod" });
    expect(readObjectPayload(data.get(atlasObjectDragType)!)).toEqual({ id, typeId: "atlas.page", title: "Úvod" });
    expect(data.get(atlasPageDragType)).toBe(id);
    expect(data.get("text/plain")).toBe("Úvod");
    expect(event.dataTransfer!.effectAllowed).toBe("copyMove");
  });

  it("leaves the page payload to pages", () => {
    const { data, event } = written();
    startObjectDrag(event, { id, typeId: "atlas.contact" });
    expect(data.has(atlasPageDragType)).toBe(false);
    expect(data.has(atlasObjectDragType)).toBe(true);
  });

  it("writes the object alone and the effect it is asked for when bare (a drag inside the editor must not paste a name)", () => {
    const { data, event } = written();
    startObjectDrag(event, { id, typeId: "atlas.page", title: "Úvod" }, { effect: "copy", bare: true });
    expect([...data.keys()]).toEqual([atlasObjectDragType]);
    expect(event.dataTransfer!.effectAllowed).toBe("copy");
  });
});

describe("the page ids of the tree's payload", () => {
  it("serve the drop of a board and the tree's reference node alike: lower case, once each", () => {
    const payload = transfer({ [atlasPageDragType]: `${id.toUpperCase()} ${id} ${other}` });
    expect(readReferenceDrop(payload)).toEqual({ targetId: id, typeId: "atlas.page" });
    expect(readDrop(payload, atlas)).toEqual([{ kind: "page", resourceId: id }, { kind: "page", resourceId: other }]);
  });
});

describe("readDrop: an object dragged onto a board", () => {
  it("makes a live object card of a contact or a board, before the name its drag also carries", () => {
    expect(readDrop(transfer({ [atlasObjectDragType]: objectPayload({ id, typeId: "atlas.contact", title: "Jan" }), "text/plain": "Jan" }), atlas))
      .toEqual([{ kind: "object", objectId: id, typeId: "atlas.contact" }]);
    expect(readDrop(transfer({ [atlasObjectDragType]: objectPayload({ id: other, typeId: "atlas.board" }) }), atlas))
      .toEqual([{ kind: "object", objectId: other, typeId: "atlas.board" }]);
  });

  it("makes a page a page card, at its heading or block", () => {
    expect(readDrop(transfer({ [atlasObjectDragType]: objectPayload({ id, typeId: "atlas.page", anchor: "uvod" }) }), atlas))
      .toEqual([{ kind: "page", resourceId: id, anchor: "uvod" }]);
  });

  it("is accepted by its type alone, as a bare drag out of the editor carries nothing else", () => {
    expect(dropAccepts([atlasObjectDragType])).toBe(true);
  });

  it("falls back to the other payloads when malformed", () => {
    expect(readDrop(transfer({ [atlasObjectDragType]: "{", [atlasPageDragType]: other }), atlas)).toEqual([{ kind: "page", resourceId: other }]);
  });
});

// Spec 2026-10-30 §8: several contacts dragged at once make several cards; one the board shows already is placed and only said.
describe("several objects in one drag", () => {
  it("writes them as one payload, reads each once, and the tree takes the first", () => {
    const data = new Map<string, string>();
    const event = { dataTransfer: { setData: (type: string, value: string) => data.set(type, value), effectAllowed: "" } } as unknown as DragEvent;
    startObjectDrag(event, [{ id, typeId: "atlas.contact", title: "Jan" }, { id: other, typeId: "atlas.contact", title: "Eva" }, { id, typeId: "atlas.contact", title: "Jan" }]);
    expect(data.get("text/plain")).toBe("Jan\nEva\nJan");
    expect(readObjectsPayload(data.get(atlasObjectDragType)!)).toHaveLength(3);
    const dropped = transfer(Object.fromEntries(data));
    expect(readDrop(dropped, atlas)).toEqual([{ kind: "object", objectId: id, typeId: "atlas.contact" }, { kind: "object", objectId: other, typeId: "atlas.contact" }]);
    expect(readReferenceDrop(dropped)).toEqual({ targetId: id, typeId: "atlas.contact", title: "Jan" });
    expect(readObjectsPayload(JSON.stringify([{ id, typeId: "atlas.contact" }, { id: "nope", typeId: "atlas.contact" }]))).toEqual([{ id, typeId: "atlas.contact" }]);
  });

  it("names the contacts a board shows already, and no other object", () => {
    const items = [{ kind: "object", props: { objectId: id.toUpperCase(), typeId: "atlas.contact" } }, { kind: "note" }];
    expect(placedContacts([{ kind: "object", objectId: id, typeId: "atlas.contact" }, { kind: "object", objectId: other, typeId: "atlas.contact" }], items)).toEqual([id]);
    expect(placedContacts([{ kind: "object", objectId: id, typeId: "atlas.note" }], items)).toEqual([]);
  });
});
