import { describe, expect, it } from "vitest";
import { alignChangedBlocks, changedTextRanges, compareHistoryBlocks, formattingOnly } from "./historyBlocks";

const paragraph = (text: string) => ({ type: "paragraph", content: [{ type: "text", text }] });
const stored = (...content: object[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } });

describe("history block comparison", () => {
  it("keeps unchanged blocks and isolates the edited, added and removed ones", () => {
    const before = stored(paragraph("Úvod"), paragraph("Restartujte službu."), paragraph("Konec"), paragraph("Smazat"));
    const after = stored(paragraph("Úvod"), paragraph("Restartujte službu ručně."), paragraph("Nový odstavec"), paragraph("Konec"));
    const diff = compareHistoryBlocks(before, after);
    expect(diff.readable).toBe(true);
    expect(diff.segments.map((segment) => segment.kind)).toEqual(["same", "changed", "same", "changed"]);
    expect(diff.changes).toBe(2);
    const [, edited, , removed] = diff.segments;
    expect(edited).toMatchObject({ removed: [paragraph("Restartujte službu.")], added: [paragraph("Restartujte službu ručně."), paragraph("Nový odstavec")] });
    expect(removed).toMatchObject({ removed: [paragraph("Smazat")], added: [] });
  });

  it("keeps a disclosure stored before 3.6 (its title an attribute) unchanged against the converted revision (its title a node)", () => {
    const before = stored({ type: "disclosure", attrs: { title: "Podrobnosti" }, content: [paragraph("Skryté")] }, paragraph("Konec"));
    const after = stored({ type: "disclosure", attrs: {}, content: [{ type: "disclosureTitle", content: [{ type: "text", text: "Podrobnosti" }] }, paragraph("Skryté")] }, paragraph("Konec 2"));
    const diff = compareHistoryBlocks(before, after);
    expect(diff.segments.map((segment) => segment.kind)).toEqual(["same", "changed"]);
    expect(diff.changes).toBe(1);
  });

  it("does not call a block edited when only what nobody sees differs, and keeps every visible change of the same text", () => {
    // Stored before 4.0 and saved again by today's editor: the heading's identity, the requirement defaults, empty attributes, key order.
    const before = stored(
      { type: "heading", attrs: { level: 2 }, content: [{ type: "text", text: "Postup" }] },
      paragraph("Beze změny"),
      { type: "paragraph", content: [{ type: "text", text: "Odkaz", marks: [{ type: "link", attrs: { href: "https://a.example" } }] }] });
    const after = stored(
      { content: [{ type: "text", text: "Postup", marks: [] }], type: "heading", attrs: { headingId: "h-1", aliases: "", level: 2, state: null, stateLabel: "", direction: "internal", system: "", party: "", partyContactId: "", from: "" } },
      { type: "paragraph", attrs: { dependencies: false }, content: [{ type: "text", text: "Beze změny" }] },
      { type: "paragraph", attrs: { dependencies: false }, content: [{ type: "text", text: "Odkaz", marks: [{ type: "link", attrs: { href: "https://a.example", target: null } }] }] });
    const diff = compareHistoryBlocks(before, after);
    expect(diff.changes).toBe(0);
    expect(diff.segments.map((segment) => segment.kind)).toEqual(["same"]);

    const heading = (attrs: object) => ({ type: "heading", attrs: { level: 2, headingId: "h-1", ...attrs }, content: [{ type: "text", text: "Dodat certifikát" }] });
    const image = (width: number) => ({ type: "image", attrs: { src: "/a.png", width } });
    const linked = (href: string) => ({ type: "paragraph", content: [{ type: "text", text: "Odkaz", marks: [{ type: "link", attrs: { href } }] }] });
    const chip = (headingId: string) => ({ type: "paragraph", content: [{ type: "sectionLink", attrs: { pageId: "p-1", headingId } }] });
    for (const [was, is] of [
      [heading({ state: "open" }), heading({ state: "done" })],
      [heading({ state: "open", direction: "internal" }), heading({ state: "open", direction: "incoming" })],
      [heading({}), heading({ level: 3 })],
      [image(200), image(400)],
      [{ type: "paragraph", attrs: { textAlign: "left" }, content: [{ type: "text", text: "A" }] }, { type: "paragraph", attrs: { textAlign: "center" }, content: [{ type: "text", text: "A" }] }],
      [linked("https://a.example"), linked("https://b.example")],
      // Another node's headingId is its target, not an identity.
      [chip("h-1"), chip("h-2")],
    ]) expect(compareHistoryBlocks(stored(paragraph("Úvod"), was), stored(paragraph("Úvod"), is)).changes, JSON.stringify(is)).toBe(1);
  });

  it("keeps a computed overview unchanged whatever id or shape it was stored with, not removed and added again", () => {
    const requirement = (state: string) => ({ type: "requirement", attrs: { id: "r1", state }, content: [paragraph("Dodatek")] });
    const before = stored({ type: "requirementSummary", attrs: { id: "s1" } }, requirement("open"));
    const after = stored({ type: "overview", attrs: { kind: "requirements", id: "s1" } }, requirement("done"));
    const diff = compareHistoryBlocks(before, after);
    expect(diff.segments.map((segment) => segment.kind)).toEqual(["same", "changed"]);
    expect(diff.segments[1]).toMatchObject({ removed: [requirement("open")], added: [requirement("done")] });
    // Another kind of overview is another block.
    expect(compareHistoryBlocks(stored({ type: "overview", attrs: { kind: "contacts" } }), stored({ type: "overview", attrs: { kind: "requirements" } })).changes).toBe(1);
  });

  it("reads a block that moved unchanged as moved, once, and a change of marks alone as formatting", () => {
    const table = { type: "table", content: [{ type: "tableRow", content: [{ type: "tableCell", content: [paragraph("Server")] }] }] };
    const diff = compareHistoryBlocks(stored(table, paragraph("A"), paragraph("B")), stored(paragraph("A"), paragraph("B"), table));
    expect(diff.moved.size).toBe(2);
    // The old place only lost the moved table: one change, where it went.
    expect(diff.changes).toBe(1);
    const bold = { type: "paragraph", content: [{ type: "text", text: "A " }, { type: "text", text: "tučně", marks: [{ type: "bold" }] }] };
    expect(formattingOnly(paragraph("A tučně"), bold)).toBe(true);
    expect(formattingOnly(paragraph("A tučně"), paragraph("A jinak"))).toBe(false);
  });

  it("treats a first revision as entirely new and unreadable data as not renderable", () => {
    expect(compareHistoryBlocks(null, stored(paragraph("A"))).segments).toEqual([{ kind: "changed", removed: [], added: [paragraph("A")] }]);
    expect(compareHistoryBlocks("historical-2", stored(paragraph("A"))).readable).toBe(false);
  });

  it("maps word changes to character ranges of each side, ignoring whitespace-only changes", () => {
    expect(changedTextRanges("Restartujte službu.", "Restartujte službu ručně.")).toEqual({ removed: [], added: [[18, 24]] });
    const { removed, added } = changedTextRanges("Volejte Janu", "Volejte Petra");
    expect(removed.map(([from, to]) => "Volejte Janu".slice(from, to))).toEqual(["Janu"]);
    expect(added.map(([from, to]) => "Volejte Petra".slice(from, to))).toEqual(["Petra"]);
  });
});

describe("aligning a changed span", () => {
  it("pairs an edited block with its earlier wording, not with a deleted neighbour", () => {
    const removed = [paragraph("Starý postup: restartujte všechny služby ručně."), paragraph("Po nasazení zkontrolujte dostupnost služby.")];
    const added = [paragraph("Po nasazení zkontrolujte dostupnost služby a chybové logy."), paragraph("Výsledek zapište do deníku.")];
    expect(alignChangedBlocks(removed, added).map((change) => change.kind)).toEqual(["removed", "modified", "added"]);
  });
  it("keeps blocks of another type or with few shared words as a replacement", () => {
    expect(alignChangedBlocks([paragraph("Kontakt: Jana")], [{ type: "heading", content: [{ type: "text", text: "Kontakt: Jana" }] }]).map((change) => change.kind)).toEqual(["removed", "added"]);
    expect(alignChangedBlocks([paragraph("Zálohy běží v noci")], [paragraph("Monitoring hlásí výpadky")]).map((change) => change.kind)).toEqual(["removed", "added"]);
  });
});
