import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { Editor, type AnyExtension } from "@tiptap/core";
import StarterKit from "@tiptap/starter-kit";
import Collaboration from "@tiptap/extension-collaboration";
import type { Node as PMNode } from "@tiptap/pm/model";
import { TextSelection } from "@tiptap/pm/state";
import { yUndoPluginKey } from "@tiptap/y-tiptap";
import * as Y from "yjs";
import { loadMarkdownDialect, type MarkdownPlugin } from "@atlas/markdown/core";
import { createHeadingAnchorsPlugin } from "@atlas/markdown/plugins/heading-anchors";
import { createPagesPlugin } from "@atlas/markdown/plugins/pages";
import { isDependencyLine, isRequirementHeading, requirementSections } from "./requirementHeading";
import { createRequirementsPlugin } from "./requirementsPlugin";

/*
 * Two real replicas over Yjs (no server: the updates are exchanged by hand, as block-concurrency.test.ts in @atlas/markdown does): the
 * editors carry the page's heading, page-link and requirement extensions and the Collaboration binding with its own undo (y-undo, each
 * client undoes its own changes). Client A deletes across requirements while client B edits a dependency line, then both undo.
 */

type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const PAGE = "0b5f3c1e-0000-4000-8000-0000000000a1";
const text = (value: string): Json => ({ type: "text", text: value });
const p = (value: string): Json => ({ type: "paragraph", content: [text(value)] });
const link = (headingId: string, title: string): Json => ({ type: "pageReference", attrs: { id: PAGE, title, url: `#/pages/${PAGE}?heading=${headingId}` } });
const heading = (headingId: string, title: string, attrs: Record<string, unknown> = {}): Json => ({ type: "heading", attrs: { level: 3, headingId, ...attrs }, content: [text(title)] });
const content = (): Json[] => [
  heading("a", "Alfa", { state: "planned" }), p("od Eva · do 5. 11. 2026"), p("Poznámka A"),
  heading("b", "Beta", { state: "in_progress" }), { type: "paragraph", attrs: { dependencies: true }, content: [link("a", "Alfa"), text(", "), link("c", "Gama")] }, p("do 1. 12. 2026"), p("Poznámka B"),
  heading("c", "Gama", { state: "planned" }), { type: "paragraph", attrs: { dependencies: true }, content: [link("b", "Beta")] }, p("Poznámka C"),
  { type: "heading", attrs: { level: 2, headingId: "konec" }, content: [text("Konec")] }, p("Text")];

const extensionsOf = (plugin: MarkdownPlugin) => [...(plugin.create({ profile: "rich", signal: new AbortController().signal }).extensions ?? [])] as AnyExtension[];
const editors: Editor[] = [], docs: Y.Doc[] = [];
beforeAll(() => loadMarkdownDialect());
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); docs.splice(0).forEach(doc => doc.destroy()); document.body.replaceChildren(); });

function replica(snapshot?: Uint8Array) {
  const doc = new Y.Doc();
  docs.push(doc);
  if (snapshot) Y.applyUpdate(doc, snapshot);
  const editor = new Editor({ element: document.body.appendChild(document.createElement("div")), extensions: [StarterKit.configure({ undoRedo: false }),
    ...extensionsOf(createHeadingAnchorsPlugin()), ...extensionsOf(createPagesPlugin()), ...extensionsOf(createRequirementsPlugin({ offered: () => false })),
    Collaboration.configure({ document: doc })] });
  editors.push(editor);
  return { doc, editor };
}
function pair(start = content()) {
  const alice = replica();
  alice.editor.commands.setContent({ type: "doc", content: start });
  yUndoPluginKey.getState(alice.editor.state)!.undoManager.clear();
  return { alice, bob: replica(Y.encodeStateAsUpdate(alice.doc)) };
}
function exchange(a: { doc: Y.Doc }, b: { doc: Y.Doc }) {
  const fromA = Y.encodeStateAsUpdate(a.doc, Y.encodeStateVector(b.doc)), fromB = Y.encodeStateAsUpdate(b.doc, Y.encodeStateVector(a.doc));
  Y.applyUpdate(a.doc, fromB);
  Y.applyUpdate(b.doc, fromA);
}
const press = (editor: Editor, key: string) => editor.view.someProp("handleKeyDown", handler => handler(editor.view, new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true })));
const place = (editor: Editor, value: string, end = false) => {
  let at = -1;
  editor.state.doc.forEach((node, pos) => { if (at < 0 && node.textContent === value) at = end ? pos + node.nodeSize - 1 : pos + 1; });
  return at;
};

/** What no replica may ever hold: a link in a heading or a line of text, a dependency line holding text or not directly under its requirement
 * heading, a link twice, requirement attributes nested. */
function corrupted(doc: PMNode) {
  const problems: string[] = [], seen = new Set<string>();
  doc.descendants((node, _pos, parent) => {
    if (parent !== doc && (isDependencyLine(node) || isRequirementHeading(node))) problems.push(`nested ${node.type.name}`);
    if (isDependencyLine(node)) node.forEach(child => { if (child.type.name !== "pageReference" && !/^[\s,]*$/.test(child.textContent)) problems.push(`text „${child.textContent}“ in a dependency line`); });
    if (node.type.name === "pageReference") {
      const key = String(node.attrs.url);
      if (seen.has(key)) problems.push(`the link ${key} twice`);
      seen.add(key);
      if (parent?.content.content.some(child => child.type.name !== "pageReference" && !/^[\s,]*$/.test(child.textContent))) problems.push(`the link ${key} in „${parent.textContent}“`);
    }
    return true;
  });
  let previous: PMNode | null = null;
  doc.forEach(node => { if (isDependencyLine(node) && !isRequirementHeading(previous)) problems.push("a dependency line not directly under a requirement heading"); previous = node; });
  return problems;
}

describe("two clients over Yjs: a delete across requirements and an edit of a dependency line, then both undo", () => {
  /** The start of the n-th dependency line's content. */
  const line = (editor: Editor, n: number) => {
    const found: number[] = [];
    editor.state.doc.forEach((node, pos) => { if (isDependencyLine(node)) found.push(pos + 1); });
    return found[n]!;
  };
  it.each([
    // Beta's line (in A's deleted range): its first link, deleted as Backspace after it deletes it.
    ["B removes a link of a dependency line A deletes", (editor: Editor) => { const at = line(editor, 0); editor.view.dispatch(editor.state.tr.delete(at, at + 1)); }],
    // Gama's line (outside it): its only link, so the line is a paragraph again.
    ["B removes the only link of a dependency line A keeps", (editor: Editor) => { const at = line(editor, 1); editor.view.dispatch(editor.state.tr.delete(at, at + 1)); }],
    ["B adds a link to a dependency line A deletes", (editor: Editor) => {
      const at = line(editor, 0) + editor.state.doc.resolve(line(editor, 0)).parent.content.size;
      editor.view.dispatch(editor.state.tr.insert(at, [editor.schema.text(", "), editor.schema.nodeFromJSON(link("konec", "Konec"))]));
    }],
  ] as const)("%s", (_name, bobEdits) => {
    const { alice, bob } = pair();
    const original = alice.editor.getJSON();
    // A selects from the start of „Alfa“ to the end of „Poznámka B“ and deletes; B edits Beta's dependency line meanwhile.
    alice.editor.view.dispatch(alice.editor.state.tr.setSelection(TextSelection.create(alice.editor.state.doc, place(alice.editor, "Alfa"), place(alice.editor, "Poznámka B", true))));
    press(alice.editor, "Delete");
    expect(requirementSections(alice.editor.state.doc).map(section => section.id)).toEqual(["c"]);
    bobEdits(bob.editor);
    expect(bob.editor.getJSON()).not.toEqual(original);
    for (const { editor } of [alice, bob]) expect(corrupted(editor.state.doc), "before the exchange").toEqual([]);
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(corrupted(alice.editor.state.doc), "after the exchange").toEqual([]);
    expect(requirementSections(alice.editor.state.doc).map(section => section.id)).toEqual(["c"]);
    alice.editor.commands.undo();
    bob.editor.commands.undo();
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(corrupted(alice.editor.state.doc), "after both undid").toEqual([]);
    // Both undid everything they did: the document as it was, and its requirements read as they were.
    expect(alice.editor.getJSON()).toEqual(original);
    expect(requirementSections(alice.editor.state.doc).map(section => [section.id, section.facts.dependencies.map(dependency => dependency.headingId)]))
      .toEqual([["a", []], ["b", ["a", "c"]], ["c", ["b"]]]);
  });

  it("A deletes from mid-title to mid-title (a heading's join: the first takes the second's lines) while B removes a link of that line: both converge, then undo", () => {
    const { alice, bob } = pair();
    const original = alice.editor.getJSON(), titleAt = (editor: Editor, title: string, offset: number) => place(editor, title) + offset;
    alice.editor.view.dispatch(alice.editor.state.tr.setSelection(TextSelection.create(alice.editor.state.doc, titleAt(alice.editor, "Alfa", 2), titleAt(alice.editor, "Beta", 2))));
    press(alice.editor, "Delete");
    const at = line(bob.editor, 0);
    bob.editor.view.dispatch(bob.editor.state.tr.delete(at, at + 1));
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(corrupted(alice.editor.state.doc), "after the exchange").toEqual([]);
    expect(requirementSections(alice.editor.state.doc).map(section => [section.id, section.facts.title, section.facts.dependencies.map(dependency => dependency.headingId), section.facts.deadline]))
      .toEqual([["a", "Alta", ["c"], "2026-12-01"], ["c", "Gama", ["b"], ""]]);
    alice.editor.commands.undo();
    bob.editor.commands.undo();
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(alice.editor.getJSON()).toEqual(original);
  });

  it("A deletes the facts line's text while B types in a facts-like note below: the note stays where it is, once; both converge, then undo (sixth review P2-1)", () => {
    const { alice, bob } = pair([heading("vpn", "VPN přístup", { state: "done" }), p("od IT nemocnice"), p("Přístup funguje od 29. 9. 2026."), p("od Honza"), p("Text")]);
    const original = alice.editor.getJSON(), lines = (editor: Editor) => { const found: string[] = []; editor.state.doc.forEach(node => found.push(node.textContent)); return found; };
    alice.editor.view.dispatch(alice.editor.state.tr.setSelection(TextSelection.create(alice.editor.state.doc, place(alice.editor, "od IT nemocnice"), place(alice.editor, "od IT nemocnice", true))));
    press(alice.editor, "Delete");
    bob.editor.view.dispatch(bob.editor.state.tr.insertText(" a Jana", place(bob.editor, "od Honza", true)));
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(lines(alice.editor)).toEqual(["VPN přístup", "", "Přístup funguje od 29. 9. 2026.", "od Honza a Jana", "Text"]);
    alice.editor.commands.undo();
    bob.editor.commands.undo();
    exchange(alice, bob);
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON());
    expect(alice.editor.getJSON()).toEqual(original);
  });
});
