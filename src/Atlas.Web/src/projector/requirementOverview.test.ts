// @vitest-environment node
import { describe, expect, it } from "vitest";
import type { PageRequirement } from "./requirementModel";
import { Schema } from "@tiptap/pm/model";
import { EditorState } from "@tiptap/pm/state";
import { blockedItems, blockKinds, daysSince, notAskedByParty, outlineOfJson, overviewSections, reshapesSections, sectionName, showsAutoOverview, stateEntries, stateSegments, touchesOutline, waitingByParty, type OutlineEntry } from "./requirementOverview";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";

const item = (id: string, attrs: Partial<RequirementAttributes> = {}, ancestors: string[] = []): PageRequirement =>
  ({ id, title: id.toUpperCase(), attrs: { ...requirementDefaults, ...attrs }, ancestors });
const heading = (title: string, level: number): OutlineEntry => ({ heading: title, level });
const block = (id: string): OutlineEntry => ({ block: id });

type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const p = (text: string): Json => ({ type: "paragraph", content: [{ type: "text", text }] });
const h = (level: number, text: string): Json => ({ type: "heading", attrs: { level }, content: [{ type: "text", text }] });
/** A requirement heading: a heading with a state, its section the blocks after it. */
const req = (id: string, attrs: Record<string, unknown> = {}, title = id.toUpperCase()): Json => ({ type: "heading", attrs: { level: 3, headingId: id, state: "planned", ...attrs }, content: [{ type: "text", text: title }] });
const deps = (text: string): Json => ({ ...p(text), attrs: { dependencies: true } });

/** The page's shape for a transaction: headings with a state, paragraphs with the dependency flag. */
const shapeSchema = new Schema({ nodes: {
  doc: { content: "block+" }, text: { group: "inline" },
  paragraph: { group: "block", content: "inline*", attrs: { dependencies: { default: false } } },
  heading: { group: "block", content: "inline*", attrs: { level: { default: 1 }, headingId: { default: null }, state: { default: null } } },
} });

describe("the deployment overview", () => {
  it("splits a state by the blocks' own words for it: the plain ones first, then each word, in the bar's order; what does not apply last, with no share", () => {
    const entries = stateEntries([item("a", { state: "planned" }), item("b", { state: "in_progress", stateLabel: "test" }), item("c", { state: "done" }),
      item("d", { state: "in_progress" }), item("e", { state: "in_progress", stateLabel: " test " }), item("f", { state: "not_applicable" })]);
    expect(entries.map(({ state, label, count, share }) => [state, label, count, share])).toEqual([
      ["done", "", 1, 0.2], ["in_progress", "", 1, 0.2], ["in_progress", "test", 2, 0.4], ["planned", "", 1, 0.2], ["not_applicable", "", 1, 0]]);
  });

  it("reads a page's outline: plain headings and requirement headings in order, a summary, and the first note of a blocked requirement", () => {
    const outline = outlineOfJson({ type: "doc", content: [
      p("Úvod"), h(2, "Portál"), h(3, "Core"),
      // The first note is neither the dependency line nor the facts line („do d. M. yyyy“ only; a legacy „od X“ line is a note).
      req("a", { state: "blocked" }), deps("Závislosti: B"), p("do 15. 11. 2026"), p(""), p("Čeká se na smlouvu."), p("Druhý odstavec"),
      req("b"), p("Poznámka nezablokovaného"),
      { type: "blockquote", content: [h(3, "Citát"), req("q", { state: "blocked" })] }, h(3, "NIA"),
      req("d", { state: "blocked" }), h(4, "Hned další nadpis"), p("Pod ním, ne pod D"),
      // A converted page's lines („Kde uloženo: …“, shape §5) are no reason: the note after them is, and without one there is none.
      req("e", { state: "blocked" }), p("Kde uloženo: Keepass."), p("Chybí certifikát."), req("f", { state: "blocked" }), p("Kde uloženo: Keepass."),
      // The PM's „Blokuje…“ record wins over the note, the newest of them (3.5.2's rule); a list line without it is no reason.
      req("g", { state: "blocked" }), p("Poznámka."), { type: "bulletList", content: ["2. 11. 2026 — Urgováno: ÚZIS", "1. 11. 2026 — Blokováno: chybí prostup", "3. 11. 2026 10:00 — Blokováno: čeká se na firewall", "28. 10. 2026 — Blokováno: staré"]
        .map(text => ({ type: "listItem", content: [p(text)] })) },
      { type: "overview", attrs: { id: "s", kind: "requirements" } },
    ] });
    // A heading inside a quote starts no section of the page.
    expect(outline.entries).toEqual([heading("Portál", 2), heading("Core", 3), block("a"), block("b"), heading("NIA", 3), block("d"), heading("Hned další nadpis", 4), block("e"), block("f"), block("g")]);
    expect(outline.blocks).toBe(6);
    expect(outline.summary).toBe(true);
    expect([...outline.notes]).toEqual([["a", "Čeká se na smlouvu."], ["e", "Chybí certifikát."], ["g", "čeká se na firewall"]]);
    expect(outlineOfJson({ type: "doc", content: [req("a", {})] }).summary).toBe(false);
  });

  it("tells a transaction that reshapes the sections from typing, which walks nothing", () => {
    const doc = shapeSchema.nodeFromJSON({ type: "doc", content: [h(2, "Portál"), req("a", { state: "blocked" }), p("Čeká se na smlouvu."), req("b"), p("Poznámka")] });
    const state = EditorState.create({ doc }), at = (index: number) => { let pos = 0; for (let i = 0; i < index; i++) pos += doc.child(i).nodeSize; return pos; };
    const typed = (index: number) => state.tr.insertText("x", at(index) + 2);
    // Typing in a note or a heading's text keeps the sections; a state, a split or a removed block reshapes them.
    expect([typed(4), typed(1)].map(reshapesSections)).toEqual([false, false]);
    expect(reshapesSections(state.tr.setNodeAttribute(at(3), "state", "done"))).toBe(true);
    expect(reshapesSections(state.tr.split(at(4) + 2))).toBe(true);
    expect(reshapesSections(state.tr.delete(at(4), at(5)))).toBe(true);
    // The outline also reads headings' text and a blocked requirement's notes: those touch it, a note of another one does not.
    expect([typed(0), typed(1), typed(2), typed(4)].map(touchesOutline)).toEqual([true, true, true, false]);
  });

  it("groups blocks into sections by the nearest heading, nested blocks with their parent, and names each by its heading path", () => {
    const items = [item("intro"), item("a", { state: "done" }), item("a1", { state: "done" }, ["a"]), item("a2", { state: "blocked" }, ["a"]),
      item("b", { state: "waiting_for_input" }), item("c", { state: "not_applicable" }), item("d", { state: "done" }), item("e", { state: "in_progress" })];
    const entries = [block("intro"), heading("Portál pacienta", 2), heading("Core", 3), block("a"), heading("NIA", 3), block("b"), block("c"),
      heading("Prázdný", 3), heading("MPI", 2), heading("Core", 3), block("d"), heading("Odkazy", 2), block("e")];
    const sections = overviewSections(items, entries);
    expect(sections.map(section => [section.title, section.context, section.items.map(entry => entry.id), section.done, section.total])).toEqual([
      ["", "", ["intro"], 0, 1],
      ["Core", "Portál pacienta", ["a", "a1", "a2"], 2, 3],
      ["NIA", "Portál pacienta", ["b", "c"], 0, 1],
      ["Core", "MPI", ["d"], 1, 1],
      ["Odkazy", "", ["e"], 0, 1],
    ]);
    expect(sections.map(section => sectionName(section.path))).toEqual(["", "Portál pacienta › Core", "Portál pacienta › NIA", "MPI › Core", "Odkazy"]);
    expect(new Set(sections.map(section => section.key)).size).toBe(sections.length);
    // A page without headings has one section: nothing to break down.
    expect(overviewSections(items, items.filter(entry => !entry.ancestors.length).map(entry => block(entry.id)))).toHaveLength(1);
  });

  it("waits only on blocks waiting for input, grouped by party in page order; a planned input is not waited on", () => {
    const items = [item("a", { state: "waiting_for_input", party: "IT" }), item("b", { direction: "inbound", party: "IT" }), item("c", { state: "waiting_for_input" }),
      item("d", { state: "waiting_for_input", party: "Jana" }), item("e", { state: "waiting_for_input", party: "IT" }, ["d"])];
    expect(waitingByParty(items).map(group => [group.party, group.items.map(entry => entry.id)])).toEqual([["IT", ["a", "e"]], ["Bez určení", ["c"]], ["Jana", ["d"]]]);
  });

  it("counts the blocks that apply by kind: inputs, modules (with nested blocks), handovers, the rest of our work", () => {
    const items = [item("m"), item("v1", { direction: "inbound" }, ["m"]), item("v2", { direction: "inbound", state: "done" }, ["m"]),
      item("v3", { direction: "inbound", state: "not_applicable" }, ["m"]), item("o", { direction: "outbound" }), item("t1"), item("t2")];
    expect(blockKinds(items)).toBe("2 vstupy · 1 modul · 1 předání · 2 úkoly");
    expect(blockKinds(items.slice(0, 3))).toBe("2 vstupy · 1 modul");
    expect(blockKinds([])).toBe("");
  });

  it("lists blocked blocks with their first note", () => {
    const items = [item("a", { state: "blocked" }), item("b"), item("c", { state: "blocked" })];
    expect(blockedItems(items, new Map([["c", "Chybí smlouva"]])).map(entry => [entry.item.id, entry.note])).toEqual([["a", ""], ["c", "Chybí smlouva"]]);
  });

  it("splits the bar by state in a calm order, done first, leaving out what does not apply", () => {
    const counts = { planned: 2, waiting_for_input: 1, in_progress: 0, done: 3, blocked: 1, not_applicable: 4 };
    expect(stateSegments(counts).map(segment => [segment.state, segment.count, Math.round(segment.share * 1000) / 10])).toEqual([
      ["done", 3, 42.9], ["waiting_for_input", 1, 14.3], ["blocked", 1, 14.3], ["planned", 2, 28.6]]);
  });

  it("shows the automatic overview on a page of requirements without a summary, never beside one, and not for a single block", () => {
    expect(showsAutoOverview({ summary: false, blocks: 2 })).toBe(true);
    expect(showsAutoOverview({ summary: true, blocks: 2 })).toBe(false);
    // Requirement headings count, plain ones do not.
    expect(showsAutoOverview(outlineOfJson({ type: "doc", content: [h(2, "Modul"), req("a", {}), p("A"), req("a1", {})] }))).toBe(true);
    expect(showsAutoOverview(outlineOfJson({ type: "doc", content: [h(2, "Modul"), req("a", {}), h(3, "Jiný")] }))).toBe(false);
  });

  it("lists the inputs nobody was asked for yet by party, and counts the days of waiting", () => {
    const items = [item("a", { direction: "inbound", party: "IT" }), item("b", { direction: "inbound", state: "waiting_for_input", party: "IT" }),
      item("c", { direction: "internal" }), item("d", { direction: "inbound" }), item("e", { direction: "inbound", party: " IT " })];
    expect(notAskedByParty(items).map(group => [group.party, group.items.map(entry => entry.id)])).toEqual([["IT", ["a", "e"]], ["Bez určení", ["d"]]]);
    expect(daysSince("2026-07-21", "2026-11-01")).toBe(103);
    expect(daysSince("", "2026-11-01")).toBeUndefined();
    expect(daysSince("2026-11-02", "2026-11-01")).toBeUndefined();
  });
});
