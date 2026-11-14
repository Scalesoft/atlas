import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { beforeAll, describe, expect, it } from "vitest";
import type { Schema } from "@tiptap/pm/model";
import { createMarkdownEditorController, loadMarkdownDialect, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { atlasContentPlugins } from "../documentWidgets";
import { headingRequirements, moveLegacyParty } from "./requirementHeading";

/*
 * The web's reading of requirement headings answers every case of tests/fixtures/content/requirement-headings.json with the rows the server
 * reads (RequirementHeadingsParityTests: RequirementHeadings.cs and the SQL function). The old blocks' rows are the server's alone here.
 */

type Json = { type: string; text?: string; attrs?: Record<string, unknown>; content?: Json[] };
interface Row { position: number; blockId: string; key: string; title: string; direction: string; state: string; date: string; party: string; where: string; requires: string;
  parent: string | null; partyContactId: string }
const fixture = JSON.parse(readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), "../../../../tests/fixtures/content/requirement-headings.json"), "utf8")) as
  { cases: { name: string; pageId: string | null; legacy?: Json[]; content: Json[]; rows: Row[] }[] };

// The page's schema as an editor builds it (every Atlas content plugin), without an editor's transactions (ids, trailing paragraphs).
let schema!: Schema;
beforeAll(async () => {
  await loadMarkdownDialect();
  const capture: MarkdownPlugin = { id: "parity-capture", apiVersion: 1, create: () => ({ onCreate: context => { schema = context.editor.schema; } }) };
  const controller = createMarkdownEditorController({ profile: "rich", editable: false, plugins: [...atlasContentPlugins({ kind: "edit", canWrite: () => false }), capture],
    source: { kind: "local", value: { format: "atlas-rich", version: 1, plugins: [], document: { type: "doc", content: [{ type: "paragraph" }] } } as MarkdownRichValue } });
  const element = document.body.appendChild(document.createElement("div"));
  controller.attachView(element, { ariaLabel: "Stránka" });
  await new Promise(resolve => setTimeout(resolve, 0));
  controller.destroy();
  element.remove();
});

// A null attribute the server reads as its default (`direction: null` is internal) is left out, so the node takes its default.
// The editor's YouTrack node also needs the issue's id, title and url, which the server's reading does not look at.
const issue = { id: "2-42", title: "Úkol", url: "https://youtrack.example/issue/PX2-42" };
const withoutNulls = (node: Json): Json => ({ ...node,
  ...(node.attrs ? { attrs: { ...(node.type === "youtrackIssue" ? issue : {}), ...Object.fromEntries(Object.entries(node.attrs).filter(([name, value]) => value !== null || name === "state")) } } : {}),
  ...(node.content ? { content: node.content.map(withoutNulls) } : {}) });
const legacyIds = (content: Json[]): string[] => content.flatMap(node => [...(node.type === "requirement" ? [String(node.attrs?.id ?? "")] : []), ...legacyIds(node.content ?? [])]);

describe("requirement headings read as the server reads them (tests/fixtures/content/requirement-headings.json)", () => {
  it.each(fixture.cases.map(entry => [entry.name, entry] as const))("%s", (_name, entry) => {
    // The old blocks are left out (their rows are the server's; their ids are not the editor's form): the headings read the same without them.
    const doc = schema.nodeFromJSON({ type: "doc", content: entry.content.filter(node => node.type !== "requirement").map(withoutNulls) });
    const legacy = new Set(legacyIds(entry.content));
    const expected = entry.rows.filter(row => !legacy.has(row.blockId)).map(row => [row.blockId, row.key, row.title, row.direction, row.state, row.date, row.party, row.requires, row.parent, row.partyContactId]);
    const read = headingRequirements(doc, entry.pageId ?? undefined).map(item => [item.id, item.attrs.key, item.title, item.attrs.direction, item.attrs.state, item.attrs.date, item.attrs.party,
      // The index's form: `pageId#headingId` for another page (the web keeps `headingId@pageId`).
      item.attrs.requires.split(",").filter(Boolean).map(reference => reference.includes("@") ? `${reference.split("@")[1]}#${reference.split("@")[0]}` : reference).join(","),
      null, item.attrs.partyContactId]);
    expect(read).toEqual(expected);
  });
});

describe("the 3 → 4 move gives the fixture's content (RequirementHeadings.MoveParty, the plugin's migration)", () => {
  it.each(fixture.cases.filter(entry => entry.legacy).map(entry => [entry.name, entry] as const))("%s", (_name, entry) => {
    const moved = moveLegacyParty({ type: "doc", content: entry.legacy! });
    expect(moved.content).toEqual(entry.content);
    // Moved once: a second move changes nothing.
    expect(moveLegacyParty(moved)).toEqual(moved);
  });
});

/** The document an editor holds after opening `content` declared at atlas-requirements `version`. */
async function opened(content: Json[], version: number) {
  const controller = createMarkdownEditorController({ profile: "rich", editable: false, plugins: atlasContentPlugins({ kind: "edit", canWrite: () => false }),
    source: { kind: "local", value: { format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version }, { id: "heading-anchors", version: 2 }, { id: "pages", version: 1 }, { id: "contacts", version: 2 }],
      document: { type: "doc", content: content.map(withoutNulls) } } as MarkdownRichValue } });
  const element = document.body.appendChild(document.createElement("div"));
  controller.attachView(element, { ariaLabel: "Stránka" });
  await new Promise(resolve => setTimeout(resolve, 0));
  const value = controller.getValue() as MarkdownRichValue;
  controller.destroy();
  element.remove();
  return value.document.content as Json[];
}

it("an editor opening a version-3 document moves its party (the plugin's 3 → 4 migration)", async () => {
  const entry = fixture.cases.find(item => item.name.startsWith("a requirement heading with its dependency line"))!;
  const heading = (await opened(entry.legacy!, 3)).find(node => node.attrs?.headingId === "portal.ssl")!;
  expect([heading.attrs!.party, heading.attrs!.partyContactId]).toEqual(["Jan Novák", "0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b"]);
});

it("an editor opening a version-4 document moves nothing: an od/pro line there is a note", async () => {
  const entry = fixture.cases.find(item => item.name.startsWith("a requirement heading with its dependency line"))!;
  const content = await opened(entry.legacy!, 4);
  expect(content.find(node => node.attrs?.headingId === "portal.ssl")!.attrs!.party ?? "").toBe("");
  expect(content.filter(node => node.type === "paragraph").map(node => (node.content ?? []).map(child => child.text ?? "").join("")))
    .toContain(entry.legacy!.filter(node => node.type === "paragraph").map(node => (node.content ?? []).map(child => child.text ?? "").join("")).find(text => text.startsWith("od "))!);
});
