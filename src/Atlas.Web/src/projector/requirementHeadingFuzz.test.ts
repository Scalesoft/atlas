import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import type { Editor } from "@tiptap/core";
import type { Node as PMNode } from "@tiptap/pm/model";
import { NodeSelection, TextSelection } from "@tiptap/pm/state";
import { Mapping } from "@tiptap/pm/transform";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import { loadMarkdownDialect, parseMarkdownContent, serializeMarkdownContent, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { atlasContentPlugins } from "../documentWidgets";
import { createHeadingFoldPlugin } from "../editor/headingFoldPlugin";
import { isDependencyLine, isRequirementHeading, requirementSections } from "./requirementHeading";
import type { ProjectorHost } from "./requirementsPlugin";

/*
 * Random edits over requirement sections (reviews 2026-11-04): seeded sequences of selections from and to any offset (mid-title, mid-note)
 * with Delete/Backspace, Backspace, Delete, Enter, typing, a paste and a move (cut and paste) through the clipboard's HTML, a colleague's
 * typing, undo and redo. After every step the shape holds (check), a requirement no change touched reads as before and the page's
 * Markdown parses back; at the end undoing everything gives the original document exactly and redoing everything the edited one.
 * A failure prints its seed: FUZZ_SEED=<seed> reruns that sequence alone. The texts have no dots: a join of „C.“ and „Delta“ is a web address to
 * the editor's autolink, whose mark an undo does not take back (the Link extension's own, any paragraphs).
 */

type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const PAGE = "0b5f3c1e-0000-4000-8000-0000000000a1", OTHER = "0b5f3c1e-0000-4000-8000-0000000000b2";
const text = (value: string): Json => ({ type: "text", text: value });
const p = (value: string): Json => ({ type: "paragraph", content: [text(value)] });
const link = (headingId: string, title: string, pageId = PAGE): Json => ({ type: "pageReference", attrs: { id: pageId, title, url: `#/pages/${pageId}?heading=${headingId}` } });
const deps = (...links: Json[]): Json => ({ type: "paragraph", attrs: { dependencies: true }, content: links.flatMap((item, index) => index ? [text(", "), item] : [item]) });
const heading = (level: number, headingId: string, title: string, attrs: Record<string, unknown> = {}): Json => ({ type: "heading", attrs: { level, headingId, ...attrs }, content: [text(title)] });
const page = (...content: Json[]) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }, { id: "pages", version: 1 }],
  document: { type: "doc", content } }) as MarkdownRichValue;
const base = () => page(
  heading(2, "oddil", "Oddíl"), p("Úvod"),
  heading(3, "a", "Alfa", { state: "planned" }), p("od Eva · do 5. 11. 2026"), p("Poznámka A"),
  heading(3, "b", "Beta", { state: "in_progress", direction: "inbound" }), deps(link("a", "Alfa"), link("vpn", "VPN", OTHER)), p("do 1. 12. 2026"), p("Poznámka B"),
  heading(3, "c", "Gama", { state: "waiting_for_input" }), deps(link("b", "Beta")), p("Poznámka C"),
  heading(3, "d", "Delta", { state: "done" }),
  heading(2, "konec", "Konec"), p("Text"));

const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => PAGE, isCatalog: () => false, open: vi.fn(),
  api: { catalog: async () => ({ definitions: [], warnings: [] }), definitions: async () => ({ definitions: [], warnings: [] }), setCatalogPage: async (pageId: string, isCatalog: boolean) => ({ pageId, isCatalog }),
    createDeployment: async () => ({ id: "x", title: "x" }) } };

let made: { editor: Editor; destroy(): void } | undefined;
/** The page editor as the app mounts it (MarkdownEditor: KeyboardFirst's move and paragraph keys run through their real commands). */
async function open(value: MarkdownRichValue) {
  let editor!: Editor;
  const capture: MarkdownPlugin = { id: "fuzz-capture", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const model = ref(value), plugins = [...atlasContentPlugins({ kind: "edit", projector: host, canWrite: () => true }), createHeadingFoldPlugin(() => PAGE), capture];
  const wrapper = mount(defineComponent({ setup: () => () => h(MarkdownEditor, { profile: "rich", modelValue: model.value,
    "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never, plugins, ariaLabel: "Stránka" }) }), { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(".ProseMirror")).toBeTruthy());
  await new Promise(resolve => setTimeout(resolve));
  made = { editor, destroy: () => wrapper.unmount() };
  return editor;
}
beforeAll(() => loadMarkdownDialect());
afterEach(() => { made?.destroy(); made = undefined; document.body.replaceChildren(); localStorage.clear(); });

/** mulberry32: a small seeded generator, the same sequence for the same seed. */
function random(seed: number) {
  let state = seed >>> 0;
  const next = () => { state = (state + 0x6d2b79f5) >>> 0; let t = state; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
  return { next, int: (below: number) => Math.floor(next() * below), pick: <T,>(items: readonly T[]) => items[Math.floor(next() * items.length)]! };
}

const json = (editor: Editor) => JSON.parse(JSON.stringify(editor.getJSON())) as Json;
const press = (editor: Editor, key: string, modifiers: KeyboardEventInit = {}) =>
  editor.view.someProp("handleKeyDown", handler => handler(editor.view, new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...modifiers })));
const linkKey = (node: PMNode) => `${String(node.attrs.url)}|${String(node.attrs.title)}`;
const isSeparator = (node: PMNode) => node.type.name === "pageReference" || (node.isText && /^[\s,]*$/.test(node.text!));
/** The dependency links in dependency lines now, by link. */
function linesOfLinks(doc: PMNode) {
  const found = new Set<string>();
  doc.descendants(node => { if (isDependencyLine(node)) node.forEach(child => { if (child.type.name === "pageReference") found.add(linkKey(child)); }); return !node.isTextblock; });
  return found;
}

/** Requirement headings without a title. */
const titleless = (doc: PMNode) => { let count = 0; doc.forEach(node => { if (isRequirementHeading(node) && !node.content.size) count++; }); return count; };
/** What the server reads of each requirement (the index row's facts), by id. */
const readings = (doc: PMNode) => new Map(requirementSections(doc).map(section => [section.id, JSON.stringify([section.facts.title, section.facts.state, section.facts.party,
  section.facts.deadline, section.facts.dependencies.map(dependency => `${dependency.pageId}#${dependency.headingId}`)])]));

interface Before { links: ReadonlySet<string>; ghosts: number; readings: Map<string, string>; sections: Map<string, [number, number]> }
/**
 * The shape after every step. `before` is what was there: the links in dependency lines (a link a step made a mention is then ordinary
 * content), the titleless requirements and what the server read of each requirement; `changed` the ranges the step changed, in the new
 * document. A requirement whose section no change touched reads exactly as before (its party, deadline, dependencies never move to
 * another requirement or vanish). After a paste (`pasted`) a link may be anywhere, and twice: its links are not followed.
 */
function check(editor: Editor, before: Before, kind: string, changed: readonly [number, number][], pasted = false, remoteText = false, removed: readonly [number, number][] = []) {
  const { doc } = editor.state, problems: string[] = [], seen = new Map<string, number>();
  // A requirement without a title comes only from deleting its title's characters one by one (or the history bringing one back).
  if (titleless(doc) > before.ghosts && !/^(Backspace at [1-9]|undo|redo)/.test(kind)) problems.push("a requirement without a title");
  let previous: PMNode | null = null;
  doc.forEach(node => {
    if (isDependencyLine(node) && !isRequirementHeading(previous)) problems.push("a dependency line not directly under a requirement heading");
    // A colleague's text in the line is theirs to fix (this editor never rewrites a remote change); the readers take its links only.
    if (isDependencyLine(node) && !remoteText) node.forEach(child => { if (!isSeparator(child)) problems.push(`a dependency line holding ${child.type.name} „${child.textContent}“`); });
    previous = node;
  });
  doc.descendants((node, _pos, parent) => {
    if (parent !== doc && (isDependencyLine(node) || isRequirementHeading(node))) problems.push(`a nested ${node.type.name} with requirement attributes`);
    if (node.type.name === "pageReference" && !pasted) {
      seen.set(linkKey(node), (seen.get(linkKey(node)) ?? 0) + 1);
      // A dependency link never lands in a line of other text (the facts line, a note, a title); a paste puts what it carries anywhere.
      if (!remoteText && before.links.has(linkKey(node)) && parent && parent.content.content.some(child => !isSeparator(child))) problems.push(`the link ${linkKey(node)} in ${parent.type.name} „${parent.textContent}“`);
    }
    return true;
  });
  for (const [key, times] of seen) if (times > 1) problems.push(`the link ${key} ${times} times`);
  const now = readings(doc);
  for (const section of requirementSections(doc)) {
    const was = before.readings.get(section.id);
    // Exempt only a section the step changed, in the new document (a range inside it, a deletion point within it; a change at its edge
    // is the neighbour's) or in the old one (what the step replaced lay inside it).
    const inside = (ranges: readonly [number, number][], pos: number, end: number) => ranges.some(([from, to]) => from === to ? pos < from && from <= end : from < end && to > pos);
    const old = before.sections.get(section.id);
    if (was === undefined || inside(changed, section.pos, section.end) || (old && inside(removed, old[0], old[1]))) continue;
    if (now.get(section.id) !== was) problems.push(`requirement ${section.id} untouched but read ${now.get(section.id)} (was ${was})`);
  }
  try { parseMarkdownContent(editor, serializeMarkdownContent(editor, doc)); } catch (error) { problems.push(`Markdown: ${(error as Error).message}`); }
  return problems;
}

const textblocks = (doc: PMNode) => { const found: { pos: number; node: PMNode }[] = []; doc.forEach((node, pos) => { if (node.isTextblock) found.push({ pos, node }); }); return found; };
/** The clipboard's HTML of a range, as ProseMirror writes it on copy. */
const clipboard = (editor: Editor, from: number, to: number) => { const box = document.createElement("div"); box.append(editor.view.serializeForClipboard(editor.state.doc.slice(from, to)).dom); return box.innerHTML; };

/** One random step; its name for the failure's trail, „remote“ when it came from a colleague (no undo of it). */
function step(editor: Editor, rng: ReturnType<typeof random>): string {
  const { doc } = editor.state, blocks = textblocks(doc), caret = (pos: number) => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, pos)));
  const at = rng.pick(blocks), offset = rng.int(at.node.content.size + 1), kind = rng.int(100), label = (block: { node: PMNode }) => `„${block.node.textContent.slice(0, 14)}“`;
  // A range from a line (a requirement title half the time) at any offset to a later line at any offset.
  const range = () => {
    const headings = blocks.filter(block => isRequirementHeading(block.node)), from = headings.length && rng.next() < 0.5 ? rng.pick(headings) : at;
    const later = blocks.filter(block => block.pos > from.pos), to = later.length ? rng.pick(later) : from;
    const start = from.pos + 1 + rng.int(from.node.content.size + 1), end = to.pos + 1 + rng.int(to.node.content.size + 1);
    return { from, to, start: Math.min(start, end), end: Math.max(start, end) };
  };
  if (kind < 16) {
    const { from, to, start, end } = range(), key = rng.pick(["Delete", "Backspace", "Enter", "IME"] as const);
    // Enter over a range from a line's start (G1 review: tiptap's splitBlock threw on it).
    const anchor = key === "Enter" ? from.pos + 1 : start;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(doc, anchor, Math.max(anchor, end))));
    if (key !== "IME") press(editor, key);
    else {
      // Text an IME or a dead key commits over the range (fifth review P1): the editor's beforeinput, else the browser's own insert.
      const input = new InputEvent("beforeinput", { inputType: "insertText", data: "q", bubbles: true, cancelable: true });
      editor.view.dom.dispatchEvent(input);
      if (!input.defaultPrevented) editor.view.dispatch(editor.state.tr.insertText("q"));
    }
    return `select ${label(from)}@${anchor}…${label(to)}@${end} ${key}`;
  }
  const sections = requirementSections(doc);
  if (kind < 24 && sections.length && rng.next() < 0.4) {
    // Blocks pasted into a requirement title (start, middle, end) or at its facts line's start (fourth review P2-2).
    const section = rng.pick(sections), title = doc.nodeAt(section.headingPos)!, facts = section.factsPos !== undefined && rng.next() < 0.4;
    caret(facts ? section.factsPos! + 1 : section.headingPos + 1 + rng.int(title.content.size + 1));
    editor.view.pasteHTML(rng.pick(["<p>Jedna</p><p>Dvě</p>", "<ul><li>Bod</li></ul>", "<table><tr><td>T</td></tr></table>", "<p>Jen text</p>", "<h3>Kapitola</h3><p>Text kapitoly</p>"]));
    return `paste blocks into ${facts ? "the facts line of" : "the title of"} ${section.id}`;
  }
  if (kind < 24) {
    // Copy a range and paste it at a caret, through the clipboard's HTML (ProseMirror's parser, the paste rules).
    const { start, end } = range(), html = clipboard(editor, start, end), target = rng.pick(textblocks(editor.state.doc));
    caret(target.pos + 1 + rng.int(target.node.content.size + 1));
    editor.view.pasteHTML(html);
    return `paste ${start}…${end} at ${label(target)}`;
  }
  if (kind < 30) {
    // Cut a requirement's section (ProseMirror's cut step) and paste it before another top-level line: a move.
    if (sections.length) {
      const section = rng.pick(sections), html = clipboard(editor, section.pos, section.end);
      // The whole section selected (a lone heading as a node, else from its title's start to its last line's end), cut as ProseMirror cuts.
      const whole = doc.nodeAt(section.pos)!.nodeSize === section.end - section.pos ? NodeSelection.create(doc, section.pos) : TextSelection.create(doc, section.pos + 1, section.end - 1);
      editor.view.dispatch(editor.state.tr.setSelection(whole).deleteSelection().setMeta("uiEvent", "cut"));
      const target = rng.pick(textblocks(editor.state.doc));
      caret(target.pos + 1 + target.node.content.size);
      editor.view.pasteHTML(html);
      return `move ${section.id} after ${label(target)}`;
    }
  }
  if (kind < 34) {
    // A colleague's typing (the collaboration binding's step), not in this editor's history; in a dependency line too.
    editor.view.dispatch(editor.state.tr.insertText("r", at.pos + 1 + offset).setMeta(ySyncPluginKey, { isChangeOrigin: true }).setMeta("addToHistory", false));
    return `remote type at ${offset} of ${label(at)}${isDependencyLine(at.node) ? " (dependency line)" : ""}`;
  }
  if (kind < 40) {
    // Ctrl+Shift+↑/↓ (KeyboardFirst's move) with the caret in any line.
    caret(at.pos + 1 + offset);
    const key = rng.next() < 0.5 ? "ArrowUp" : "ArrowDown";
    press(editor, key, { ctrlKey: true, shiftKey: true });
    return `move ${key === "ArrowUp" ? "up" : "down"} ${label(at)}`;
  }
  if (kind < 43) {
    // Ctrl+Shift+0 (KeyboardFirst's paragraph) on a heading: a requirement heading drops its state.
    const headings = blocks.filter(block => block.node.type.name === "heading");
    if (headings.length) {
      const target = rng.pick(headings);
      caret(target.pos + 1);
      press(editor, "0", { ctrlKey: true, shiftKey: true });
      return `paragraph ${label(target)}`;
    }
  }
  if (kind < 46) { caret(at.pos + 1); press(editor, "Backspace"); return `Backspace at the start of ${label(at)}`; }
  if (kind < 54) { caret(at.pos + at.node.nodeSize - 1); press(editor, "Delete"); return `Delete at the end of ${label(at)}`; }
  if (kind < 62) { caret(at.pos + 1 + offset); press(editor, "Backspace"); return `Backspace at ${offset} of ${label(at)}`; }
  if (kind < 74) { caret(at.pos + 1 + offset); press(editor, "Enter"); return `Enter at ${offset} of ${label(at)}`; }
  if (kind < 86) {
    // Typing as the editor gets it (input rules first); in a dependency line it becomes a note after the lines.
    caret(at.pos + 1 + offset);
    const { from, to } = editor.state.selection;
    if (!editor.view.someProp("handleTextInput", handler => handler(editor.view, from, to, "x", () => editor.state.tr.insertText("x", from, to)))) editor.view.dispatch(editor.state.tr.insertText("x", from, to));
    return `type at ${offset} of ${label(at)}`;
  }
  if (kind < 95) { editor.commands.undo(); return "undo"; }
  editor.commands.redo();
  return "redo";
}

// 700 steps in about 15 s, most of it the Markdown round trip of every state; FUZZ_SEQUENCES=<n> explores more.
const SEQUENCES = Number(process.env.FUZZ_SEQUENCES) || 50, STEPS = 14;
// The seeds that found a bug run every time (titleless requirements, a paste into a dependency line, an untouched reading).
const found = [21528144, 20356132, 21741957, 20728325, 20340294, 20292780, 20554107, 20569945, 20269023];
const seeds = process.env.FUZZ_SEED ? [Number(process.env.FUZZ_SEED)] : [...found, ...Array.from({ length: SEQUENCES }, (_, index) => 20261104 + index * 7919)];

describe("random edits over requirement sections keep the shape and the history", () => {
  it(`${seeds.length} seeded sequences of ${STEPS} steps`, async () => {
    // jsdom has no ClipboardEvent; ProseMirror's pasteHTML only names its type.
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    for (const seed of seeds) {
      // A fresh editor per sequence: its history starts at the original.
      made?.destroy();
      const editor = await open(base()), start = json(editor), rng = random(seed), trail: string[] = [];
      // The ranges each step changed, in the document after it (its transactions and the appended ones).
      let maps: Mapping[] = [];
      editor.on("transaction", ({ transaction, appendedTransactions }) => { for (const item of [transaction, ...appendedTransactions]) if (item.docChanged) maps.push(item.mapping); });
      for (let index = 0; index < STEPS; index++) {
        const before: Before = { links: linesOfLinks(editor.state.doc), ghosts: titleless(editor.state.doc), readings: readings(editor.state.doc),
          sections: new Map(requirementSections(editor.state.doc).map(section => [section.id, [section.pos, section.end] as [number, number]])) };
        maps = [];
        trail.push(step(editor, rng));
        const mapping = new Mapping(maps.flatMap(map => map.maps)), changed: [number, number][] = [];
        // …and what each replaced, in the document before the step.
        const removed: [number, number][] = [];
        mapping.maps.forEach((map, at) => map.forEach((oldFrom, oldTo, from, to) => {
          const after = mapping.slice(at + 1), back = mapping.slice(0, at).invert();
          changed.push([after.map(from, -1), after.map(to, 1)]);
          removed.push([back.map(oldFrom, -1), back.map(oldTo, 1)]);
        }));
        if (process.env.FUZZ_DEBUG) console.log(trail.at(-1), JSON.stringify(json(editor).content!.map(node => [node.type, node.attrs?.state ?? (node.attrs?.dependencies ? "deps" : ""),
          (node.content ?? []).map(child => child.text ?? `[${child.type}:${String(child.attrs?.title ?? "")}]`).join("")])));
        const problems = check(editor, before, trail.at(-1)!, changed, trail.some(name => /^(paste|move [^ud])/.test(name)), trail.some(name => name.endsWith("(dependency line)")), removed);
        if (problems.length) throw new Error(`seed ${seed}, after ${trail.join(" → ")}: ${problems.join("; ")}`);
      }
      // What the history holds at its newest (a sequence may end with an undo), then all the way back and forward again. A colleague's
      // change is not in it, so after one the way back is checked for the shape only.
      const remote = trail.some(name => name.startsWith("remote"));
      for (let guard = 0; guard < 200 && editor.can().redo(); guard++) editor.commands.redo();
      const edited = json(editor);
      for (let guard = 0; guard < 200 && editor.can().undo(); guard++) editor.commands.undo();
      if (!remote) expect(json(editor), `seed ${seed}: undo everything after ${trail.join(" → ")}`).toEqual(start);
      for (let guard = 0; guard < 200 && editor.can().redo(); guard++) editor.commands.redo();
      if (!remote) expect(json(editor), `seed ${seed}: redo everything after ${trail.join(" → ")}`).toEqual(edited);
      const shape = check(editor, { links: new Set(), ghosts: Infinity, readings: new Map(), sections: new Map() }, "history", [], true, remote);
      if (shape.length) throw new Error(`seed ${seed}, undo and redo of everything after ${trail.join(" → ")}: ${shape.join("; ")}`);
    }
  }, SEQUENCES * 3_000);
});
