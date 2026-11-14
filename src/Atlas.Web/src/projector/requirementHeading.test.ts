import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import type { Editor } from "@tiptap/core";
import { DOMParser as PMDOMParser, Fragment, Slice } from "@tiptap/pm/model";
import { TextSelection } from "@tiptap/pm/state";
import { closeHistory, undoDepth } from "@tiptap/pm/history";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import { mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { createMarkdownEditorController, pragueNow, loadMarkdownDialect, markdownBlockKeyboardControls, openMarkdownIntentions, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { projectMarkdownAssistSelection } from "@atlas/markdown/assist";
import { atlasContentPlugins } from "../documentWidgets";
import { createHeadingFoldPlugin } from "../editor/headingFoldPlugin";
import { foldAll, isFolded, resetFoldStateForTests } from "../editor/foldState";
import { pageRequirements } from "./requirementNode";
import { touchesOutline } from "./requirementOverview";
import { headingLevelAt } from "./requirementCommands";
import { endOfRequirement } from "./fillMissingPrerequisites";
import { factsOfText, headingFacts, readFacts, requirementSections } from "./requirementHeading";
import { addHeadingDependency, headingIntentions, insertRequirementHeading, openAwaited, openDependencyPicker, openStateMenu, recordHeading, renameHeadingId, setHeadingDeadline, setHeadingParty, setHeadingState, sizeTints, toggleDone } from "./requirementHeadingView";
import type { ProjectorHost } from "./requirementsPlugin";
import { readFileSync } from "node:fs";
const headingCss = readFileSync("src/projector/requirementHeading.css", "utf8");
const contentCss = readFileSync("../Atlas.Markdown/src/styles/content.css", "utf8");

/* Requirement headings (shape 2026-11-04): the facts read from the text, and the editor's rules over them. */

type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string; marks?: Json[] };
const text = (value: string): Json => ({ type: "text", text: value });
const p = (value: string, attrs?: Record<string, unknown>): Json => ({ type: "paragraph", ...(attrs ? { attrs } : {}), ...(value ? { content: [text(value)] } : {}) });
const link = (headingId: string, title: string, pageId = "0b5f3c1e-0000-4000-8000-0000000000a1"): Json => ({ type: "pageReference", attrs: { id: pageId, title, url: `#/pages/${pageId}?heading=${headingId}` } });
const deps = (...links: Json[]): Json => ({ type: "paragraph", attrs: { dependencies: true }, content: links.flatMap((item, index) => index ? [text(", "), item] : [item]) });
const heading = (level: number, headingId: string, title: string, attrs: Record<string, unknown> = {}): Json => ({ type: "heading", attrs: { level, headingId, ...attrs }, content: [text(title)] });
const page = (...content: Json[]) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "heading-anchors", version: 1 }, { id: "atlas-requirements", version: 4 }, { id: "pages", version: 1 }, { id: "contacts", version: 2 }],
  document: { type: "doc", content } }) as MarkdownRichValue;

const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => "0b5f3c1e-0000-4000-8000-0000000000a1", isCatalog: () => false, open: vi.fn(),
  api: { catalog: async () => ({ definitions: [], warnings: [] }), definitions: async () => ({ definitions: [], warnings: [] }), setCatalogPage: async (pageId: string, isCatalog: boolean) => ({ pageId, isCatalog }),
    createDeployment: async () => ({ id: "x", title: "x" }) } };

const sample = () => page(
  heading(2, "identita", "Identita občana"),
  heading(3, "nia-cert", "Certifikát NIA", { state: "waiting_for_input", direction: "inbound", system: "NIA", party: "IT nemocnice" }), p("do 21. 7. 2026"), p("Poznámka."),
  heading(3, "uzis", "Napojení na ÚZIS", { state: "in_progress" }), deps(link("nia-cert", "Certifikát NIA"), link("infra-vpn", "VPN", "0b5f3c1e-0000-4000-8000-0000000000b2")), p("do 15. 11. 2026"),
  heading(2, "provoz", "Provoz"), p("Text."));

let made: { editor: Editor; destroy(): void; undo(): void; redo(): void } | undefined;
async function open(value: MarkdownRichValue, writable = true, projector = host) {
  let editor!: Editor;
  const capture: MarkdownPlugin = { id: "heading-test-capture", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const controller = createMarkdownEditorController({ profile: "rich", source: { kind: "local", value }, editable: writable,
    plugins: [...atlasContentPlugins({ kind: "edit", projector, canWrite: () => writable }), createHeadingFoldPlugin(() => "0b5f3c1e-0000-4000-8000-0000000000a1"), capture] });
  const element = document.body.appendChild(document.createElement("div"));
  controller.attachView(element, { ariaLabel: "Stránka" });
  made = { editor, destroy: () => { controller.destroy(); element.remove(); }, undo: () => controller.dispatch({ type: "undo" }), redo: () => controller.dispatch({ type: "redo" }) };
  await new Promise(resolve => setTimeout(resolve, 0));
  return editor;
}
/** The page editor as the app mounts it (MarkdownEditor: KeyboardFirst's move and duplicate keys, the slash menu). */
async function openVue(value: MarkdownRichValue) {
  let editor!: Editor;
  const capture: MarkdownPlugin = { id: "heading-test-capture", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
  const model = ref(value), plugins = [...atlasContentPlugins({ kind: "edit", projector: host, canWrite: () => true }), createHeadingFoldPlugin(() => "0b5f3c1e-0000-4000-8000-0000000000a1"), capture];
  const wrapper = mount(defineComponent({ setup: () => () => h(MarkdownEditor, { profile: "rich", modelValue: model.value,
    "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never, plugins, ariaLabel: "Stránka" }) }), { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(".ProseMirror")).toBeTruthy());
  await new Promise(resolve => setTimeout(resolve));
  made = { editor, destroy: () => wrapper.unmount(), undo: () => editor.commands.undo(), redo: () => editor.commands.redo() };
  return editor;
}
beforeAll(() => loadMarkdownDialect());
afterEach(() => { made?.destroy(); made = undefined; document.body.replaceChildren(); localStorage.clear(); vi.unstubAllGlobals(); });

const json = (editor: Editor) => editor.getJSON() as Json;
const types = (editor: Editor) => json(editor).content!.map(node => `${node.type}${node.attrs?.state ? `:${node.attrs.state}` : ""}${node.attrs?.dependencies ? ":deps" : ""}`);
const caretIn = (editor: Editor, match: (text: string) => boolean, at: "start" | "end" | number = "end") => {
  editor.state.doc.forEach((node, pos) => {
    if (!match(node.textContent)) return;
    const offset = at === "start" ? 0 : at === "end" ? node.content.size : at;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, pos + 1 + offset)));
  });
};
const press = (editor: Editor, key: string, modifiers: KeyboardEventInit = {}) =>
  editor.view.someProp("handleKeyDown", handler => handler(editor.view, new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true, ...modifiers })));

describe("reading requirement headings", () => {
  it("lists the sections in order, each up to the next heading of any level, with its dependency and facts lines", async () => {
    const editor = await open(sample());
    const sections = requirementSections(editor.state.doc);
    expect(sections.map(section => section.id)).toEqual(["nia-cert", "uzis"]);
    const [nia, uzis] = sections;
    expect(nia!.depsPos).toBeUndefined();
    expect(editor.state.doc.nodeAt(nia!.factsPos!)!.textContent).toBe("do 21. 7. 2026");
    expect(editor.state.doc.nodeAt(nia!.end)!.attrs.headingId).toBe("uzis");
    expect(editor.state.doc.nodeAt(uzis!.depsPos!)!.attrs.dependencies).toBe(true);
    expect(editor.state.doc.nodeAt(uzis!.end)!.attrs.headingId).toBe("provoz");
    expect(nia!.facts).toMatchObject({ id: "nia-cert", level: 3, state: "waiting_for_input", direction: "inbound", system: "NIA", title: "Certifikát NIA", party: "IT nemocnice", deadline: "2026-07-21" });
    expect(uzis!.facts.dependencies).toEqual([{ pageId: "0b5f3c1e-0000-4000-8000-0000000000a1", headingId: "nia-cert", title: "Certifikát NIA" }, { pageId: "0b5f3c1e-0000-4000-8000-0000000000b2", headingId: "infra-vpn", title: "VPN" }]);
    expect(uzis!.facts).toMatchObject({ party: "", deadline: "2026-11-15" });
  });

  it("is a requirement only with a state; a plain heading is not one", async () => {
    const editor = await open(page(heading(2, "a", "A"), p("od X")));
    expect(requirementSections(editor.state.doc)).toEqual([]);
  });

  it("caches the facts per heading node and its lines, so an edit elsewhere reads nothing again", async () => {
    const editor = await open(sample());
    const before = requirementSections(editor.state.doc)[0]!.facts;
    caretIn(editor, value => value === "Text.");
    editor.commands.insertContent("x");
    expect(requirementSections(editor.state.doc)[0]!.facts).toBe(before);
    const node = editor.state.doc.nodeAt(requirementSections(editor.state.doc)[0]!.headingPos)!;
    expect(headingFacts(node)).not.toBe(before);
  });

  it("reads the party and its contact from the heading's attributes and the deadline from a facts line that holds only it", async () => {
    const editor = await open(page(heading(3, "a", "A", { state: "planned", party: "Jana Nováková", partyContactId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1" }), p("do 3.2.2026")));
    const section = requirementSections(editor.state.doc)[0]!;
    expect(readFacts(editor.state.doc.nodeAt(section.factsPos!))).toEqual({ deadline: "2026-02-03", completed: "" });
    expect(section.facts).toMatchObject({ party: "Jana Nováková", partyContactId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1", deadline: "2026-02-03" });
  });

  it("a legacy „od X · do Y“ paragraph is an ordinary note: no facts, no party, nothing lost (shape §6)", async () => {
    const editor = await open(page(heading(3, "a", "A", { state: "planned" }), p("od Eva · do 5. 11. 2026"), p("pro Honza"), p("Poznámka.")));
    const section = requirementSections(editor.state.doc)[0]!;
    expect(section.factsPos).toBeUndefined();
    expect(section.facts).toMatchObject({ party: "", partyContactId: "", deadline: "" });
    expect(editor.state.doc.textContent).toContain("od Eva · do 5. 11. 2026");
  });

  it("gives pageRequirements one requirement per heading: key = id, requires as ids, id@page for another page, never nested", async () => {
    const editor = await open(sample());
    const items = pageRequirements(editor.state.doc, "0b5f3c1e-0000-4000-8000-0000000000a1");
    expect(items.map(item => [item.id, item.attrs.key, item.attrs.requires, item.attrs.date, item.attrs.party, item.ancestors])).toEqual([
      ["nia-cert", "nia-cert", "", "2026-07-21", "IT nemocnice", []], ["uzis", "uzis", "nia-cert,infra-vpn@0b5f3c1e-0000-4000-8000-0000000000b2", "2026-11-15", "", []]]);
    // Without the page id a link counts as this page's when its id is a requirement heading here.
    expect(pageRequirements(editor.state.doc).map(item => item.attrs.requires)).toEqual(["", "nia-cert,infra-vpn@0b5f3c1e-0000-4000-8000-0000000000b2"]);
  });

  it("round-trips the attributes through HTML", async () => {
    const editor = await open(sample());
    const element = document.createElement("div");
    element.innerHTML = editor.getHTML();
    // The blocks and their attributes (a page reference parses back as a link: the pages plugin's own HTML rule, not this one's).
    const blocks = (doc: import("@tiptap/pm/model").Node) => doc.content.content.map(node => ({ type: node.type.name, attrs: node.attrs }));
    expect(blocks(PMDOMParser.fromSchema(editor.schema).parse(element))).toEqual(blocks(editor.state.doc));
  });
});

describe("the marks in the heading line", () => {
  it("draws a checkbox before the title and the kind, service, „čeká na N“ and state chips after it (the state last, the pill at the line end), as real buttons", async () => {
    const editor = await open(sample());
    const uzis = editor.view.dom.querySelector<HTMLElement>('[data-heading-id="uzis"]')!;
    expect(uzis.querySelector("input.rq-check")!.getAttribute("aria-label")).toBe("Hotovo: Napojení na ÚZIS");
    expect([...uzis.querySelectorAll<HTMLElement>("button.rq-chip")].map(chip => [chip.dataset.rqChip, chip.getAttribute("aria-label")]))
      .toEqual([["kind", "Druh: úkol"], ["ready", "Čeká na 1 prerekvizitu"], ["state", "Stav: rozpracováno"]]);
    expect(editor.state.doc.textContent).not.toContain("rozpracováno");
  });

  it("keeps 3.5.2's labels: the kind with its party („od IT nemocnice“), „čeká na blokované: …“ and „čeká na N prerekvizity“", async () => {
    const editor = await open(page(heading(3, "nia-cert", "Certifikát NIA", { state: "blocked", direction: "inbound", party: "IT nemocnice" }),
      heading(3, "uzis", "Napojení na ÚZIS", { state: "planned" }), deps(link("nia-cert", "Certifikát NIA")), p("Poznámka.")));
    const chip = (id: string, name: string) => editor.view.dom.querySelector<HTMLElement>(`[data-heading-id="${id}"] [data-rq-chip="${name}"]`);
    // The party is the heading's attribute; the chip shows it and names it in its label.
    expect([chip("nia-cert", "kind")!.textContent, chip("nia-cert", "kind")!.getAttribute("aria-label")]).toEqual(["od IT nemocnice", "Druh: vstup, od IT nemocnice"]);
    expect(chip("nia-cert", "kind")!.querySelector(".rq-chip-value")!.textContent).toBe("IT nemocnice");
    expect(chip("uzis", "blocked-by")!.textContent).toBe("čeká na blokované: Certifikát NIA");
    expect(chip("uzis", "ready")!.textContent).toBe("čeká na 1 prerekvizitu");
    expect(chip("uzis", "kind")!.textContent).toBe("úkol");
  });

describe("the section card (a requirement's section ends at the next heading of any level, as the Přehled reads it)", () => {
  /** Which headings carry a card, and the blocked reasons their lines show („Blokuje: …“ chips). */
  const cards = (editor: Editor) => [...editor.view.dom.querySelectorAll<HTMLElement>(":scope > * > .rq-tint")].map(card => card.parentElement!.dataset.headingId);
  const reasons = (editor: Editor) => [...editor.view.dom.querySelectorAll<HTMLElement>(":scope > * > .rq-chips > [data-rq-chip=reason]")].map(element => element.querySelector(".rq-chip-value")!.textContent);

  it("gives a card to in-progress, waiting and blocked requirements only; a nested blocked one under a planned one has its own", async () => {
    const editor = await open(page(heading(2, "planned", "Neřešené", { state: "planned" }), p("Text."), heading(3, "nested", "Vnořené blokované", { state: "blocked" }), p("Proč."),
      heading(2, "blocked", "Blokované", { state: "blocked" }), p("Důvod."), heading(3, "done", "Hotové pod ním", { state: "done" }), p("Hotovo."),
      heading(3, "plain", "Obyčejný pododdíl"), p("Pod ním."), heading(2, "wait", "Čeká", { state: "waiting_for_input" }), heading(2, "work", "Rozpracované", { state: "in_progress" })));
    expect(cards(editor)).toEqual(["nested", "blocked", "wait", "work"]);
    // A done requirement under a blocked one is no part of its section: no reason is read past the next heading.
    expect(reasons(editor)).toEqual(["Proč.", "Důvod."]);
  });

  it("keeps the card a title is typed into: no card moved, so neither the view nor the sizes are redone per key", async () => {
    const editor = await open(page(heading(2, "a", "Blokované", { state: "blocked" }), p("Text."), heading(2, "b", "Rozpracované", { state: "in_progress" })));
    const card = editor.view.dom.querySelector(".rq-tint");
    caretIn(editor, value => value === "Blokované");
    editor.view.dispatch(editor.state.tr.insertText(" teď"));
    expect(editor.view.dom.querySelector(".rq-tint")).toBe(card);
    // Re-stated to done: its card goes.
    editor.view.dispatch(editor.state.tr.setNodeAttribute(0, "state", "done"));
    expect(cards(editor)).toEqual(["b"]);
  });

  it("measures a card from its heading to the last block that shows, stopping at any heading (a plain one included)", async () => {
    const editor = await open(page(heading(2, "a", "Blokované", { state: "blocked" }), p("Jedna."), p("Dvě."), heading(3, "plain", "Obyčejný pododdíl"), p("Pod ním.")));
    const at = (element: Element, top: number, height: number, shown = true) => Object.defineProperties(element, {
      offsetTop: { value: top, configurable: true }, offsetHeight: { value: height, configurable: true }, offsetParent: { value: shown ? editor.view.dom : null, configurable: true } });
    const [title, one, two, plain, under] = [...editor.view.dom.children];
    at(title!, 100, 24); at(one!, 132, 20); at(two!, 160, 20); at(plain!, 200, 24); at(under!, 230, 20);
    sizeTints(editor.view.dom);
    const card = title!.querySelector<HTMLElement>(".rq-tint")!;
    expect(card.style.getPropertyValue("--rq-section")).toBe("80px");
    // Folded (the Detail slider's S): the blocks are hidden, the card is the title's line.
    at(one!, 0, 0, false); at(two!, 0, 0, false);
    sizeTints(editor.view.dom);
    expect(card.style.getPropertyValue("--rq-section")).toBe("24px");
  });

  it("keeps „Blokuje:“ on the Přehled's note while typing: a reason typed into an empty note gets it, a deleted one gives it on", async () => {
    const editor = await open(page(heading(2, "a", "Blokované", { state: "blocked" }), p("do 1. 12. 2026"), p(""), p("Další poznámka."), heading(2, "b", "Jiné"), p("Text.")));
    expect(reasons(editor)).toEqual(["Další poznámka."]);
    caretIn(editor, value => value === "", "start");
    editor.commands.insertContent("Chybí certifikát");
    expect(reasons(editor)).toEqual(["Chybí certifikát"]);
    editor.commands.setTextSelection({ from: editor.state.selection.from - "Chybí certifikát".length, to: editor.state.selection.from });
    editor.commands.deleteSelection();
    expect(reasons(editor)).toEqual(["Další poznámka."]);
  });

  it("shows the reason as a chip of the heading line, never a converted page's „Kde uloženo: …“; none without a reason note", async () => {
    const editor = await open(page(heading(2, "a", "S důvodem", { state: "blocked" }), p("Kde uloženo: Keepass."), p("Chybí certifikát."),
      heading(2, "b", "Bez důvodu", { state: "blocked" }), p("Kde uloženo: Keepass, složka SMS.")));
    expect(reasons(editor)).toEqual(["Chybí certifikát."]);
    const chip = editor.view.dom.querySelector<HTMLElement>('[data-heading-id="a"] [data-rq-chip=reason]')!;
    // Text, not a control: no Tab stop, nothing on the grip's keyboard path.
    expect([chip.tagName, chip.textContent, chip.dataset.tone]).toEqual(["SPAN", "Blokuje: Chybí certifikát.", "blocked"]);
    expect(editor.view.dom.querySelector('[data-heading-id="b"] [data-rq-chip=reason]')).toBeNull();
    // The paragraphs stay plain text, no „Blokuje:“ drawn before them.
    expect(editor.view.dom.querySelector("[data-rq-reason]")).toBeNull();
  });

  it("prefers the PM's newest „Blokováno: …“ record over the note, and follows a record added by „Blokuje…“", async () => {
    const list = (...lines: string[]): Json => ({ type: "bulletList", content: lines.map(line => ({ type: "listItem", content: [p(line)] })) });
    const editor = await open(page(heading(2, "a", "Obojí", { state: "blocked" }), p("Poznámka pod nadpisem."), list("2. 11. 2026 — Blokováno: chybí prostup", "1. 11. 2026 — Blokováno: staré"),
      heading(2, "b", "Jen poznámka", { state: "blocked" }), p("Chybí certifikát.")));
    expect(reasons(editor)).toEqual(["chybí prostup", "Chybí certifikát."]);
    // The note stays as it is under the heading.
    expect(editor.view.dom.querySelector('[data-heading-id="a"]')!.nextElementSibling!.textContent).toBe("Poznámka pod nadpisem.");
    recordHeading(editor, "b", "blocked", "čeká se na firewall");
    expect(reasons(editor)).toEqual(["chybí prostup", "čeká se na firewall"]);
  });
});


  it("marks the boxes when the right goes: the checkbox inert and no Tab stop, a value chip opens a read-only card", async () => {
    const editor = await open(sample());
    const nia = editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"]')!;
    const drawn = [nia.querySelector(".rq-check"), nia.querySelector(".rq-chips")];
    editor.setEditable(false);
    // The same widgets, marked in the editor's update: a lock toggle redraws no requirement (perf guard: 150 of them cost 2.8 s at 4× CPU).
    expect([nia.querySelector(".rq-check")!.getAttribute("aria-disabled"), (nia.querySelector(".rq-check") as HTMLElement).tabIndex]).toEqual(["true", -1]);
    expect(nia.querySelector(".rq-check")).toBe(drawn[0]);
    expect(nia.querySelector(".rq-chips")).toBe(drawn[1]);
    expect([...nia.querySelectorAll<HTMLElement>(".rq-chip")].map(chip => chip.getAttribute("aria-disabled"))).toEqual([null, null, null]);
    // A reader tabs to the heading's first chip (the chips open the card); the arrows walk the others and the chevron (one stop a heading).
    expect([...nia.querySelectorAll<HTMLElement>(".rq-chips > button")].map(chip => chip.tabIndex)).toEqual([0, -1, -1]);
    expect(nia.querySelector<HTMLElement>(".atlas-heading-fold")?.tabIndex ?? -1).toBe(-1);
    nia.querySelector<HTMLElement>("[data-rq-chip=kind]")!.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true }));
    expect([...nia.querySelectorAll<HTMLElement>(".rq-chips > button")].map(chip => chip.tabIndex)).toEqual([-1, 0, -1]);
    const json = JSON.stringify(editor.getJSON());
    nia.querySelector<HTMLElement>(".rq-check")!.click();
    nia.querySelector<HTMLElement>("[data-rq-chip=state]")!.click();
    const card = await vi.waitFor(() => { const found = document.querySelector<HTMLElement>(".md-context-popover"); expect(found).not.toBeNull(); return found!; });
    expect([...card.querySelectorAll(".md-context-menu-item")].map(row => row.textContent)).toEqual(["čeká na vstupStav", "vstup · IT nemocniceDruh", "NIASlužba", "21. 7. 2026Termín"]);
    (card.querySelector(".md-context-menu-item") as HTMLElement).click();
    expect(JSON.stringify(editor.getJSON())).toBe(json);
    // The right back: the checkbox is a control again. A writer's in-text controls are no Tab stops (Esc, Tab leaves the editor, F5):
    // Alt+F10, then Tab reaches them.
    editor.setEditable(true);
    expect([nia.querySelector(".rq-check")!.hasAttribute("aria-disabled"), (nia.querySelector(".rq-check") as HTMLElement).tabIndex]).toEqual([false, -1]);
    expect(nia.querySelector(".rq-chips")).toBe(drawn[1]);
    expect([...nia.querySelectorAll<HTMLElement>(".rq-chips > button")].map(chip => chip.tabIndex)).toEqual([-1, -1, -1]);
  });

  it("a click on the check before the text had the focus puts the caret at the title's end, so Ctrl+Z undoes it (F3)", async () => {
    const editor = await open(sample());
    // The page just opened: the title field holds the focus.
    const title = document.body.appendChild(document.createElement("textarea"));
    title.focus();
    const check = editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"] .rq-check')!;
    check.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true }));
    check.click();
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("done");
    expect(document.activeElement).toBe(editor.view.dom);
    const section = requirementSections(editor.state.doc)[0]!;
    expect(editor.state.selection.head).toBe(section.headingPos + editor.state.doc.nodeAt(section.headingPos)!.nodeSize - 1);
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "z", ctrlKey: true, bubbles: true, cancelable: true }));
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("waiting_for_input");
    title.remove();
  });

  it("a click on the state chip before the text had the focus does the same: caret at the title's end, the editor focused (F3)", async () => {
    const editor = await open(sample());
    const title = document.body.appendChild(document.createElement("textarea"));
    title.focus();
    editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"] [data-rq-chip="state"]')!.click();
    const section = requirementSections(editor.state.doc)[0]!;
    expect(editor.state.selection.head).toBe(section.headingPos + editor.state.doc.nodeAt(section.headingPos)!.nodeSize - 1);
    await vi.waitFor(() => expect((document.activeElement as HTMLElement | null)?.dataset.stateOption).toBe("waiting_for_input"));
    title.remove();
  });

  it("checks hotovo and unchecks back to the state it had, one undo step each", async () => {
    const editor = await open(sample());
    editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"] .rq-check')!.click();
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("done");
    toggleDone(editor, "nia-cert");
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("waiting_for_input");
    made!.undo();
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("done");
    // Nothing remembered (another session): neřešeno.
    made!.undo();
    toggleDone(editor, "uzis", true); toggleDone(editor, "uzis");
    expect(requirementSections(editor.state.doc)[1]!.facts.state).toBe("in_progress");
  });
});

describe("who changed a requirement (requirementLinks.ts)", () => {
  it("is asked of the server only on a page that has requirements", async () => {
    const pageRequirements = vi.fn(async () => ({ blocks: [] }));
    const asking: ProjectorHost = { ...host, api: { ...host.api, pageRequirements } };
    await open(page(heading(2, "uvod", "Úvod"), p("Jen text.")), true, asking);
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(pageRequirements).not.toHaveBeenCalled();
    made!.destroy(); made = undefined;
    await open(sample(), true, asking);
    await vi.waitFor(() => expect(pageRequirements).toHaveBeenCalledOnce());
  });
});

describe("the day a requirement was checked off", () => {
  it("is written into the facts line beside the deadline when it goes to done, and goes with it; one undo step each", async () => {
    const editor = await open(sample());
    const today = pragueNow().date, section = (at: number) => requirementSections(editor.state.doc)[at]!;
    const line = (at: number) => section(at).factsPos === undefined ? undefined : editor.state.doc.nodeAt(section(at).factsPos!)?.textContent;
    const shown = `${Number(today.slice(8))}. ${Number(today.slice(5, 7))}. ${today.slice(0, 4)}`;
    // With a deadline: both parts, the deadline kept.
    expect(line(1)).toBe("do 15. 11. 2026");
    toggleDone(editor, "uzis", true);
    expect(line(1)).toBe(`do 15. 11. 2026 · hotovo ${shown}`);
    expect(section(1).facts).toMatchObject({ deadline: "2026-11-15", completed: today });
    made!.undo();
    expect(line(1)).toBe("do 15. 11. 2026");
    made!.redo();
    toggleDone(editor, "uzis");
    expect(line(1)).toBe("do 15. 11. 2026");
    // Without one: a line of its own, gone again when unchecked; the state chip does the same.
    setHeadingDeadline(editor, "nia-cert", "");
    expect(line(0)).toBeUndefined();
    setHeadingState(editor, "nia-cert", "done");
    expect(line(0)).toBe(`hotovo ${shown}`);
    setHeadingState(editor, "nia-cert", "blocked");
    setHeadingDeadline(editor, "nia-cert", "");
    expect(line(0)).toBeUndefined();
  });

  it("reads the facts line grammar: either part or both, no more", () => {
    expect(factsOfText("do 15. 11. 2026 · hotovo 5. 11. 2026")).toEqual({ deadline: "2026-11-15", completed: "2026-11-05" });
    expect(factsOfText("hotovo 5. 11. 2026")).toEqual({ deadline: "", completed: "2026-11-05" });
    for (const text of ["hotovo 31. 2. 2026", "do 1. 1. 2026 · do 2. 1. 2026", "hotovo 5. 11. 2026 · poznámka", "od IT · hotovo 5. 11. 2026"]) expect(factsOfText(text)).toBeNull();
  });
});

describe("writing requirement headings", () => {
  it("writes the party as attributes only and the deadline into the facts line, making it when missing", async () => {
    const editor = await open(sample());
    const line = () => editor.state.doc.nodeAt(requirementSections(editor.state.doc)[1]!.factsPos!)?.textContent;
    setHeadingParty(editor, "uzis", "Jana Nováková", "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1");
    expect(requirementSections(editor.state.doc)[1]!.facts).toMatchObject({ party: "Jana Nováková", partyContactId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1", deadline: "2026-11-15" });
    expect(line()).toBe("do 15. 11. 2026");
    made!.undo();
    expect(requirementSections(editor.state.doc)[1]!.facts).toMatchObject({ party: "", partyContactId: "" });
    made!.redo();
    expect(requirementSections(editor.state.doc)[1]!.facts.party).toBe("Jana Nováková");
    setHeadingDeadline(editor, "uzis", "");
    expect(requirementSections(editor.state.doc)[1]!.factsPos).toBeUndefined();
    expect(requirementSections(editor.state.doc)[1]!.facts.party).toBe("Jana Nováková");
    setHeadingParty(editor, "uzis", "");
    expect(requirementSections(editor.state.doc)[1]!.facts).toMatchObject({ party: "", partyContactId: "" });
    setHeadingDeadline(editor, "uzis", "2026-12-01");
    const section = requirementSections(editor.state.doc)[1]!;
    // The facts line comes after the dependency line.
    expect(section.factsPos!).toBeGreaterThan(section.depsPos!);
    expect(editor.state.doc.nodeAt(section.factsPos!)!.textContent).toBe("do 1. 12. 2026");
  });

  it("adds a dependency as a section link, making the line under the heading when missing", async () => {
    const editor = await open(sample());
    expect(addHeadingDependency(editor, "nia-cert", { headingId: "uzis", title: "Napojení na ÚZIS" })).toBe(true);
    const section = requirementSections(editor.state.doc)[0]!;
    expect(section.depsPos).toBe(section.headingPos + editor.state.doc.nodeAt(section.headingPos)!.nodeSize);
    expect(section.facts.dependencies).toEqual([{ pageId: "0b5f3c1e-0000-4000-8000-0000000000a1", headingId: "uzis", title: "Napojení na ÚZIS" }]);
    addHeadingDependency(editor, "uzis", { headingId: "nia-cert", title: "x" });
    expect(requirementSections(editor.state.doc)[1]!.facts.dependencies).toHaveLength(2);
  });

  it("renames the ID: the heading keeps the former one as an alias, the page's dependency lines follow, one undo step", async () => {
    const editor = await open(sample());
    const before = JSON.stringify(editor.getJSON());
    renameHeadingId(editor, "nia-cert", "nia-podpis");
    const [nia, uzis] = requirementSections(editor.state.doc);
    expect(nia!.id).toBe("nia-podpis");
    expect(editor.state.doc.nodeAt(nia!.headingPos)!.attrs.aliases).toBe("nia-cert");
    expect(uzis!.facts.dependencies.map(dependency => dependency.headingId)).toEqual(["nia-podpis", "infra-vpn"]);
    expect(() => renameHeadingId(editor, "nia-podpis", "uzis")).toThrow("už na stránce má jiný nadpis");
    expect(() => renameHeadingId(editor, "nia-podpis", "ne platné")).toThrow();
    made!.undo();
    expect(JSON.stringify(editor.getJSON())).toBe(before);
  });

  it("removes a dependency line the step emptied, with the caret in it too (the caret to the title's end), never for a colleague's change", async () => {
    const editor = await open(sample());
    const deps = () => requirementSections(editor.state.doc)[1]!.depsPos;
    const removeLinks = (remote: boolean) => {
      const at = deps()!, line = editor.state.doc.nodeAt(at)!, tr = editor.state.tr.delete(at + 1, at + line.nodeSize - 1);
      editor.view.dispatch(remote ? tr.setMeta(ySyncPluginKey, { isChangeOrigin: true }) : tr);
    };
    removeLinks(true);
    expect(types(editor)).toContain("paragraph:deps");
    made!.destroy(); await open(sample());
    const fresh = made!.editor;
    caretIn(fresh, value => value.startsWith("Poznámka"));
    const at = requirementSections(fresh.state.doc)[1]!.depsPos!, line = fresh.state.doc.nodeAt(at)!;
    fresh.view.dispatch(fresh.state.tr.delete(at + 1, at + line.nodeSize - 1));
    expect(types(fresh)).not.toContain("paragraph:deps");
    // With the caret inside, the line goes too and the caret to the title's end: an empty paragraph there would hide the facts line.
    made!.destroy(); await open(sample());
    const third = made!.editor, at3 = requirementSections(third.state.doc)[1]!.depsPos!, line3 = third.state.doc.nodeAt(at3)!;
    const tr = third.state.tr.delete(at3 + 1, at3 + line3.nodeSize - 1);
    third.view.dispatch(tr.setSelection(TextSelection.create(tr.doc, at3 + 1)));
    expect(types(third).slice(4, 6)).toEqual(["heading:in_progress", "paragraph"]);
    expect(third.state.selection.$head.parent.textContent).toBe("Napojení na ÚZIS");
    expect(requirementSections(third.state.doc)[1]!.facts).toMatchObject({ deadline: "2026-11-15", dependencies: [] });
  });

  it("Backspace on the only link keeps the facts line read (review 2026-11-04 P1-1: an empty paragraph held its place)", async () => {
    const editor = await open(page(heading(3, "a", "A", { state: "planned" }), heading(3, "b", "B", { state: "planned", party: "Eva" }), deps(link("a", "A")), p("do 15. 11. 2026"), p("Poznámka.")));
    const original = JSON.stringify(json(editor)), at = requirementSections(editor.state.doc)[1]!.depsPos!;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at + 2)));
    press(editor, "Backspace") || editor.commands.deleteRange({ from: at + 1, to: at + 2 });
    expect(requirementSections(editor.state.doc)[1]!.facts).toMatchObject({ party: "Eva", deadline: "2026-11-15", dependencies: [] });
    made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
  });

  it("Enter at the end of the dependency line or the start of the facts line (the editor's own): the lines go back to their places, the facts stay read (P1-1)", async () => {
    const editor = await open(sample()), uzis = () => requirementSections(editor.state.doc)[1]!, original = JSON.stringify(json(editor));
    const at = uzis().depsPos!;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at + editor.state.doc.nodeAt(at)!.nodeSize - 1)));
    expect(press(editor, "Enter")).toBe(true);
    expect(uzis().facts).toMatchObject({ deadline: "2026-11-15" });
    expect(types(editor).slice(4, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph"]);
    expect(editor.state.selection.$head.index(0)).toBe(7);
    made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
    // At the facts line's start the empty line the split made goes after it; the caret stays in the facts line.
    caretIn(editor, value => value === "do 15. 11. 2026", "start");
    expect(press(editor, "Enter")).toBe(true);
    expect(uzis().facts).toMatchObject({ deadline: "2026-11-15" });
    expect(types(editor).slice(4, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph"]);
    expect(editor.state.selection.$head.parent.textContent).toBe("do 15. 11. 2026");
    // The same at the start of the dependency line.
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, uzis().depsPos! + 1)));
    expect(press(editor, "Enter")).toBe(true);
    expect(uzis().facts).toMatchObject({ deadline: "2026-11-15", dependencies: [{ headingId: "nia-cert" }, { headingId: "infra-vpn" }] });
  });

  it("typing in the dependency line writes a note after the requirement's lines; the line keeps links only (P2-5)", async () => {
    const editor = await open(sample());
    const at = requirementSections(editor.state.doc)[1]!.depsPos!;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at + 2)));
    // Typed as the editor gets it: the text goes in, the rules move it to a note after the lines and the caret with it.
    for (const char of "xy") {
      const { from, to } = editor.state.selection;
      if (!editor.view.someProp("handleTextInput", handler => handler(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to));
    }
    expect(editor.state.doc.nodeAt(requirementSections(editor.state.doc)[1]!.depsPos!)!.textContent.replace(/[, ]/g, "")).toBe("");
    expect(types(editor).slice(4, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph"]);
    expect(editor.state.selection.$head.parent.textContent).toBe("xy");
    expect(requirementSections(editor.state.doc)[1]!.facts).toMatchObject({ deadline: "2026-11-15" });
  });

  it("Backspace on the only link, typing elsewhere and undo twice give the document back exactly (the link never lands in another line)", async () => {
    const editor = await open(page(heading(3, "nia-cert", "Certifikát NIA", { state: "planned" }), heading(3, "uzis", "ÚZIS", { state: "planned" }),
      deps(link("nia-cert", "Certifikát NIA")), p("do 15. 11. 2026"), p("Poznámka.")));
    const original = JSON.stringify(json(editor));
    const at = requirementSections(editor.state.doc)[1]!.depsPos!;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at + 2)));
    editor.commands.deleteRange({ from: at + 1, to: at + 2 });
    expect(types(editor)).not.toContain("paragraph:deps");
    caretIn(editor, value => value === "Poznámka.");
    editor.commands.insertContent("x");
    const edited = JSON.stringify(json(editor));
    made!.undo(); made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
    made!.redo(); made!.redo();
    expect(JSON.stringify(json(editor))).toBe(edited);
    made!.undo(); made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
    const facts = json(editor).content!.find(node => node.content?.[0]?.text === "do 15. 11. 2026")!;
    expect(facts.content!.map(node => node.type)).toEqual(["text"]);
  });

  it("Enter at the end of a requirement heading puts the caret in a new paragraph after its dependency and facts lines", async () => {
    const editor = await open(sample());
    caretIn(editor, value => value === "Napojení na ÚZIS");
    expect(press(editor, "Enter")).toBe(true);
    expect(types(editor).slice(4, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph"]);
    expect(editor.state.selection.$head.parent.textContent).toBe("");
    expect(editor.state.selection.$head.index(0)).toBe(7);
  });

  it("Enter in the middle splits it as a heading does: the second half is a plain heading with its own id, the lines stay with the first", async () => {
    const editor = await open(sample());
    caretIn(editor, value => value === "Certifikát NIA", 10);
    press(editor, "Enter");
    const content = json(editor).content!;
    // The facts line goes back under the first half; the note follows the second half, as text under a heading does.
    expect(content.slice(1, 5).map(node => node.content?.map(child => child.text).join(""))).toEqual(["Certifikát", "do 21. 7. 2026", " NIA", "Poznámka."]);
    expect(content[1]).toMatchObject({ type: "heading", attrs: { state: "waiting_for_input", system: "NIA", headingId: "nia-cert" } });
    expect(content[3]).toMatchObject({ type: "heading", attrs: { state: null, system: "", direction: "internal" } });
    expect(content[3]!.attrs!.headingId).not.toBe("nia-cert");
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ party: "IT nemocnice", deadline: "2026-07-21" });
    expect(editor.state.selection.$head.parent.textContent).toBe(" NIA");
    // With a dependency line: it stays under the first half.
    caretIn(editor, value => value === "Napojení na ÚZIS", 7);
    press(editor, "Enter");
    expect(types(editor).slice(5, 9)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "heading"]);
    expect(requirementSections(editor.state.doc)[1]!.facts.dependencies).toHaveLength(2);
    made!.undo();
    expect(types(editor).slice(5, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph"]);
  });

  it("„Změnit na odstavec“ drops the state in one step", async () => {
    const editor = await open(sample());
    caretIn(editor, value => value === "Certifikát NIA");
    editor.commands.setNode("paragraph");
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["uzis"]);
    made!.undo();
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["nia-cert", "uzis"]);
  });

  it("inserts a requirement (neřešeno) one level under the plain heading above, as a sibling under a requirement", async () => {
    const editor = await open(page(heading(2, "a", "Oddíl"), p("")));
    caretIn(editor, value => value === "");
    editor.chain().command(({ tr }) => insertRequirementHeading(tr)).run();
    expect(json(editor).content![1]).toMatchObject({ type: "heading", attrs: { level: 3, state: "planned" } });
    editor.chain().command(({ tr }) => insertRequirementHeading(tr)).run();
    expect(json(editor).content![2]).toMatchObject({ type: "heading", attrs: { level: 3, state: "planned" } });
  });
});

const links = (editor: Editor, section: number) => {
  const found: Record<string, unknown>[] = [];
  editor.state.doc.nodeAt(requirementSections(editor.state.doc)[section]!.depsPos!)!.forEach(node => { if (node.type.name === "pageReference") found.push(node.attrs); });
  return found;
};
const remoteHost = (readable: boolean): ProjectorHost => ({ ...host, api: { ...host.api, prerequisites: async refs => refs.map(reference => ({ reference, pageId: reference.split("@")[1]!,
  key: reference.split("@")[0]!, readable, ...(readable ? { title: "VPN koncentrátor", state: "in_progress", pageTitle: "Infrastruktura" } : {}) })) } });

describe("dependency links", () => {
  it("shows the target heading's live title (the stored one stays in the document) and opens the section on this page", async () => {
    const editor = await open(page(heading(3, "nia-cert", "Certifikát NIA", { state: "planned" }), heading(3, "uzis", "ÚZIS", { state: "planned" }), deps(link("nia-cert", "Starý název"))));
    const chip = editor.view.dom.querySelector<HTMLAnchorElement>("[data-page-reference]")!;
    expect(chip.textContent).toBe("Certifikát NIA");
    caretIn(editor, value => value === "Certifikát NIA");
    editor.commands.insertContent(" v2");
    expect(chip.textContent).toBe("Certifikát NIA v2");
    expect(links(editor, 1)[0]).toMatchObject({ title: "Starý název", url: "#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=nia-cert" });
    editor.setEditable(false);
    chip.click();
    expect(document.activeElement).toBe(editor.view.dom.querySelector('[data-heading-id="nia-cert"]'));
  });

  it("shows another page's title once the server read it, keeps its section address, and counts it in „čeká na N“ only when readable", async () => {
    const editor = await open(sample(), true, remoteHost(true));
    const uzis = () => editor.view.dom.querySelector<HTMLElement>('[data-heading-id="uzis"]')!;
    await vi.waitFor(() => expect(uzis().querySelector('[data-rq-chip="ready"]')!.getAttribute("aria-label")).toBe("Čeká na 2 prerekvizity"));
    const other = editor.view.dom.querySelectorAll<HTMLAnchorElement>("[data-page-reference]")[1]!;
    expect(other.querySelector(".md-chip-label")!.textContent).toBe("VPN koncentrátor");
    expect(other.querySelector(".md-chip-detail")!.textContent).toBe("Infrastruktura");
    expect(other.getAttribute("aria-label")).toBe("VPN koncentrátor, Infrastruktura");
    expect(other.getAttribute("href")).toBe("#/pages/0b5f3c1e-0000-4000-8000-0000000000b2?heading=infra-vpn");
    made!.destroy();
    const unknown = await open(sample(), true, remoteHost(false));
    await new Promise(resolve => setTimeout(resolve, 10));
    expect(unknown.view.dom.querySelector('[data-heading-id="uzis"] [data-rq-chip="ready"]')!.getAttribute("aria-label")).toBe("Čeká na 1 prerekvizitu");
    expect(unknown.view.dom.querySelectorAll("[data-page-reference]")[1]!.querySelector(".md-chip-label")!.textContent).toBe("VPN");
  });

  it("greys a link to a requirement of this page that is gone, named by its stored title, never by its bare id", async () => {
    const editor = await open(page(heading(3, "uzis", "ÚZIS", { state: "planned" }), deps(link("portal.server-hw", "portal.server-hw"), link("vpn", "VPN"))));
    const [byId, byTitle] = editor.view.dom.querySelectorAll<HTMLAnchorElement>("[data-page-reference]");
    expect(byId!.textContent).toBe("Chybějící požadavek");
    expect(byId!.dataset.chipState).toBe("muted");
    expect(byTitle!.textContent).toBe("VPN");
    expect(byTitle!.title).toContain("na stránce už není");
    expect(pageRequirements(editor.state.doc, host.pageId())[0]!.attrs.requires).toBe("portal.server-hw,vpn");
  });

  it("„+ závislost“ offers another page's requirements once typed (the server's search) and links the section there", async () => {
    const searchRequirements = vi.fn(async () => [{ pageId: "0b5f3c1e-0000-4000-8000-0000000000b3", pageTitle: "Infrastruktura", requirement: { blockId: "dns", key: "infra.dns", title: "DNS záznamy", state: "done" } }]);
    const editor = await open(sample(), true, { ...host, api: { ...host.api, searchRequirements } });
    openDependencyPicker(editor, "nia-cert");
    const input = await vi.waitFor(() => document.querySelector<HTMLInputElement>('input[aria-label="Závisí na"]')!);
    input.value = "dns"; input.dispatchEvent(new Event("input"));
    await vi.waitFor(() => expect(document.body.textContent).toContain("Jiná stránka · Infrastruktura"));
    input.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(searchRequirements).toHaveBeenCalledWith("space-1", "dns", expect.any(AbortSignal), "0b5f3c1e-0000-4000-8000-0000000000a1");
    expect(requirementSections(editor.state.doc)[0]!.facts.dependencies).toEqual([{ pageId: "0b5f3c1e-0000-4000-8000-0000000000b3", headingId: "infra.dns", title: "DNS záznamy" }]);
  });
});

/**
 * A paste as the editor runs it: the heading anchors' paste flag, every transformPasted in order, the slice inserted at the end; a move
 * (a drop within the page: no flag) takes the original `from`–`to` out in the same step.
 */
function paste(editor: Editor, slice: Slice, move?: { from: number; to: number }) {
  if (!move) editor.state.plugins.find(plugin => plugin.props.transformPasted && plugin.props.handleDOMEvents?.paste && plugin.props.handleDOMEvents?.drop)!
    .props.handleDOMEvents!.paste!.call(undefined as never, editor.view, new Event("paste") as ClipboardEvent);
  editor.view.someProp("transformPasted", transform => { slice = transform(slice, editor.view, false); return false; });
  const tr = editor.state.tr.insert(editor.state.doc.content.size, slice.content);
  editor.view.dispatch(move ? tr.delete(move.from, move.to) : tr);
}

describe("copies", () => {
  const linked = () => page(heading(3, "nia-cert", "Certifikát NIA", { state: "planned", aliases: "nia-old" }),
    { type: "paragraph", content: [{ type: "text", text: "viz NIA", marks: [{ type: "link", attrs: { href: "#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=nia-cert" } }] }] },
    heading(3, "uzis", "ÚZIS", { state: "planned" }), deps(link("nia-old", "Certifikát NIA"), link("infra-vpn", "VPN", "0b5f3c1e-0000-4000-8000-0000000000b2")), heading(2, "konec", "Konec"));

  it("gives pasted headings fresh ids, clears their aliases and rewrites the links between them; links outside stay", async () => {
    const editor = await open(linked());
    const [nia, uzis] = requirementSections(editor.state.doc);
    paste(editor, editor.state.doc.slice(nia!.pos, uzis!.end));
    const [, , copyNia, copyUzis] = requirementSections(editor.state.doc);
    expect([copyNia!.id, copyUzis!.id].every(id => id && !["nia-cert", "uzis"].includes(id))).toBe(true);
    expect(editor.state.doc.nodeAt(copyNia!.headingPos)!.attrs.aliases).toBe("");
    expect(links(editor, 3)).toEqual([{ id: "0b5f3c1e-0000-4000-8000-0000000000a1", title: "Certifikát NIA", url: `#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=${copyNia!.id}` }, { id: "0b5f3c1e-0000-4000-8000-0000000000b2", title: "VPN", url: "#/pages/0b5f3c1e-0000-4000-8000-0000000000b2?heading=infra-vpn" }]);
    expect(JSON.stringify(json(editor))).toContain(`"href":"#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=${copyNia!.id}"`);
    // The originals are untouched.
    expect(links(editor, 1).map(item => item.url)).toEqual(["#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=nia-old", "#/pages/0b5f3c1e-0000-4000-8000-0000000000b2?heading=infra-vpn"]);
    expect(editor.state.doc.nodeAt(nia!.headingPos)!.attrs).toMatchObject({ headingId: "nia-cert", aliases: "nia-old" });
  });

  it("retargets a copy from another page to this page", async () => {
    const editor = await open(linked());
    const schema = editor.schema, from = (value: Json) => schema.nodeFromJSON(value);
    paste(editor, new Slice(Fragment.fromArray([from(heading(3, "a", "A", { state: "planned" })), from(heading(3, "b", "B", { state: "planned" })), from(deps(link("a", "A", "0b5f3c1e-0000-4000-8000-0000000000b9"), link("x", "X", "0b5f3c1e-0000-4000-8000-0000000000b9")))]), 0, 0));
    const [, , a] = requirementSections(editor.state.doc);
    expect(links(editor, 3)).toEqual([{ id: "0b5f3c1e-0000-4000-8000-0000000000a1", title: "A", url: `#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=${a!.id}` }, { id: "0b5f3c1e-0000-4000-8000-0000000000b9", title: "X", url: "#/pages/0b5f3c1e-0000-4000-8000-0000000000b9?heading=x" }]);
  });

  it("keeps a link to a third page's heading of the same id as a copied one (third review P3-2)", async () => {
    const source = "0b5f3c1e-0000-4000-8000-0000000000b9", third = "0b5f3c1e-0000-4000-8000-0000000000c7";
    // Copied on its own page's editor (the copy remembers the page), pasted into this one.
    const origin = await open(linked(), true, { ...host, pageId: () => source }), closeOrigin = made!.destroy;
    const editor = await open(linked());
    const schema = editor.schema, from = (value: Json) => schema.nodeFromJSON(value);
    const slice = new Slice(Fragment.fromArray([from(heading(3, "a", "A", { state: "planned" })), from(heading(3, "b", "B", { state: "planned" })),
      from(deps(link("a", "A", source), link("a", "A jinde", third)))]), 0, 0);
    origin.view.someProp("transformCopied", transform => { transform(slice, origin.view); });
    closeOrigin();
    paste(editor, slice);
    const [, , a] = requirementSections(editor.state.doc);
    expect(links(editor, 3).map(item => item.url)).toEqual([`#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=${a!.id}`, `#/pages/${third}?heading=a`]);
  });

  it("duplicates a requirement heading (Alt+Shift+↓, KeyboardFirst's copy of the block) with a fresh id, the original keeps its own", async () => {
    const editor = await open(linked());
    const { headingPos } = requirementSections(editor.state.doc)[1]!, node = editor.state.doc.nodeAt(headingPos)!;
    editor.view.dispatch(editor.state.tr.insert(headingPos + node.nodeSize, node.copy(node.content)));
    const ids = requirementSections(editor.state.doc).map(section => section.id);
    expect(ids).toHaveLength(3);
    expect(ids.slice(0, 2)).toEqual(["nia-cert", "uzis"]);
    expect(new Set(ids).size).toBe(3);
  });

  it("keeps the ids of a move (a drop within the page, no paste)", async () => {
    const editor = await open(linked());
    const [nia, uzis] = requirementSections(editor.state.doc);
    paste(editor, editor.state.doc.slice(nia!.pos, uzis!.end), { from: nia!.pos, to: uzis!.end });
    expect(json(editor).content!.map(node => node.attrs?.headingId).filter(Boolean)).toEqual(["konec", "nia-cert", "uzis"]);
    expect(links(editor, 1).map(item => item.url)).toEqual(["#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=nia-old", "#/pages/0b5f3c1e-0000-4000-8000-0000000000b2?heading=infra-vpn"]);
  });

  it("pastes a copied section as whole blocks: the title never joins the paragraph at the caret, a second paste does not nest in its last line", async () => {
    // jsdom has no ClipboardEvent; ProseMirror's pasteHTML only names its type.
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const editor = await open(page(p("S pozdravem")));
    editor.commands.setTextSelection(1 + "S pozdravem".length);
    const section = `<h3 data-heading-id="vpn" data-state="done" data-pm-slice="1 1 []">VPN přístup</h3><p>Přístup funguje.</p>`;
    expect(editor.view.pasteHTML(section)).toBe(true);
    expect(editor.view.pasteHTML(section)).toBe(true);
    expect(json(editor).content!.filter(node => node.content?.length).map(node => `${node.type}:${node.content![0]!.text}`))
      .toEqual(["paragraph:S pozdravem", "heading:VPN přístup", "paragraph:Přístup funguje.", "heading:VPN přístup", "paragraph:Přístup funguje."]);
    expect(requirementSections(editor.state.doc)).toHaveLength(2);
  });
});

describe("dotted ids", () => {
  it("renames to portal.vpn: the dependency line follows and reads it back", async () => {
    const editor = await open(sample());
    expect(renameHeadingId(editor, "nia-cert", "portal.vpn")).toBe(true);
    expect(requirementSections(editor.state.doc)[1]!.facts.dependencies[0]).toEqual({ pageId: "0b5f3c1e-0000-4000-8000-0000000000a1", headingId: "portal.vpn", title: "Certifikát NIA" });
    expect(pageRequirements(editor.state.doc, "0b5f3c1e-0000-4000-8000-0000000000a1")[1]!.attrs.requires).toBe("portal.vpn,infra-vpn@0b5f3c1e-0000-4000-8000-0000000000b2");
  });
});

describe("whole requirements under the keys (review 2026-11-04)", () => {
  const two = () => page(heading(3, "a", "Alfa", { state: "planned" }), p("Poznámka A."), heading(3, "b", "Beta", { state: "in_progress", party: "Eva" }),
    deps(link("a", "Alfa")), p("do 5. 11. 2026"), p("Poznámka B."), heading(2, "konec", "Konec"), p("Text."));
  const place = (editor: Editor, text: string, end = false) => {
    let at = -1;
    editor.state.doc.forEach((node, pos) => { if (at < 0 && node.textContent === text) at = end ? pos + node.nodeSize - 1 : pos + 1; });
    return at;
  };
  const select = (editor: Editor, from: number, to: number) => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, from, to)));
  const snapshot = (editor: Editor) => JSON.parse(JSON.stringify(json(editor))) as Json;

  it("Delete over a selection from one requirement's title start to the next one's note end takes both whole; Undo and Redo restore exactly", async () => {
    const editor = await open(two()), original = snapshot(editor);
    select(editor, place(editor, "Alfa"), place(editor, "Poznámka B.", true));
    press(editor, "Delete");
    expect(requirementSections(editor.state.doc)).toEqual([]);
    expect(types(editor).slice(0, 2)).toEqual(["paragraph", "heading"]);
    expect(json(editor).content![0]!.content).toBeUndefined();
    const edited = snapshot(editor);
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
    made!.redo();
    expect(snapshot(editor)).toEqual(edited);
  });

  it("a selection that ends at the next requirement's title start keeps that requirement whole (its state, its lines)", async () => {
    const editor = await open(two()), original = snapshot(editor);
    select(editor, place(editor, "Alfa"), place(editor, "Beta"));
    press(editor, "Backspace");
    expect(types(editor).slice(0, 5)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph", "heading"]);
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ id: "b", title: "Beta", party: "Eva", deadline: "2026-11-05" });
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("a selection that ends at the next requirement's title end takes that requirement too: no requirement without a title stays (fuzz seed 21528144)", async () => {
    const editor = await open(two()), original = snapshot(editor);
    select(editor, place(editor, "Alfa"), place(editor, "Beta", true));
    press(editor, "Delete");
    expect(requirementSections(editor.state.doc)).toEqual([]);
    expect(types(editor).slice(0, 5)).toEqual(["paragraph", "paragraph", "paragraph", "paragraph", "heading"]);
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("Backspace at a requirement title's start does what it does in a heading (J4.3): text first, then a join; its lines are notes, never the requirement above's", async () => {
    const editor = await open(two()), original = snapshot(editor);
    editor.commands.setTextSelection(place(editor, "Beta"));
    expect(press(editor, "Backspace")).toBe(true);
    // The heading is text: the state goes, the dependency line is a note of links, the facts line a note.
    expect(types(editor).slice(0, 7)).toEqual(["heading:planned", "paragraph", "paragraph", "paragraph", "paragraph", "paragraph", "heading"]);
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["a"]);
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ dependencies: [], party: "", deadline: "" });
    // The next Backspace joins the text into the paragraph above.
    press(editor, "Backspace") || editor.commands.joinBackward();
    expect(json(editor).content![1]!.content![0]!.text).toBe("Poznámka A.Beta");
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ dependencies: [], party: "", deadline: "" });
    made!.undo(); made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("under a requirement heading, Backspace makes the title text and a join never gives its lines to the requirement above; Delete at the title above does not join", async () => {
    const editor = await open(page(heading(3, "a", "Alfa", { state: "planned" }), heading(3, "b", "Beta", { state: "planned" }), deps(link("a", "Alfa")), p("do 5. 11. 2026")));
    const original = snapshot(editor), alfa = () => requirementSections(editor.state.doc).find(section => section.id === "a")!.facts;
    editor.commands.setTextSelection(place(editor, "Beta"));
    expect(press(editor, "Backspace")).toBe(true);
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["a"]);
    expect(alfa()).toMatchObject({ dependencies: [], deadline: "" });
    press(editor, "Backspace") || editor.commands.joinBackward();
    expect(alfa()).toMatchObject({ title: "AlfaBeta", dependencies: [], deadline: "" });
    made!.undo(); made!.undo();
    expect(snapshot(editor)).toEqual(original);
    // Delete at the end of the title above does not join a requirement with lines into it.
    editor.commands.setTextSelection(place(editor, "Alfa", true));
    expect(press(editor, "Delete")).toBe(true);
    expect(snapshot(editor)).toEqual(original);
  });

  it("a block put above the dependency line goes below the lines (heal); a heading in a quote or a list is plain", async () => {
    const editor = await open(two()), facts = requirementSections(editor.state.doc)[1]!.facts;
    const { depsPos } = requirementSections(editor.state.doc)[1]!;
    editor.view.dispatch(editor.state.tr.insert(depsPos!, editor.schema.nodes.paragraph!.create(null, editor.schema.text("Vložený řádek"))));
    expect(types(editor).slice(0, 8)).toEqual(["heading:planned", "paragraph", "heading:in_progress", "paragraph:deps", "paragraph", "paragraph", "paragraph", "heading"]);
    expect(json(editor).content![5]!.content![0]!.text).toBe("Vložený řádek");
    expect(requirementSections(editor.state.doc)[1]!.facts).toEqual(facts);
    const schema = editor.schema, quote = schema.nodes.blockquote!.create(null, [schema.nodeFromJSON(heading(3, "q", "Citát", { state: "planned", system: "NIA", party: "Eva", partyContactId: "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1" })),
      schema.nodes.paragraph!.create({ dependencies: true }, schema.nodeFromJSON(link("a", "Alfa")))]);
    editor.view.dispatch(editor.state.tr.insert(editor.state.doc.content.size, quote));
    const last = json(editor).content!.find(node => node.type === "blockquote")!;
    expect(last.content![0]!.attrs).toMatchObject({ headingId: "q", state: null, system: "", party: "", partyContactId: "" });
    expect(last.content![1]!.attrs?.dependencies).toBe(false);
  });

  it("the checkbox asks „Ještě chybí N — označit přesto?“ before checking a requirement that waits; unchecking never asks", async () => {
    const editor = await open(sample());
    editor.view.dom.querySelector<HTMLElement>('[data-heading-id="uzis"] .rq-check')!.click();
    expect(requirementSections(editor.state.doc)[1]!.facts.state).toBe("in_progress");
    const confirm = await vi.waitFor(() => { const hint = document.querySelector("[data-requirement-done-hint]")!; expect(hint.textContent).toContain("Ještě chybí 1 prerekvizita — označit přesto?"); return hint.querySelector("button")!; });
    await vi.waitFor(() => expect(document.activeElement).toBe(confirm));
    confirm.click();
    expect(requirementSections(editor.state.doc)[1]!.facts.state).toBe("done");
    toggleDone(editor, "uzis");
    expect(requirementSections(editor.state.doc)[1]!.facts.state).toBe("in_progress");
  });

  it("the state menu focuses the current state", async () => {
    const editor = await open(sample());
    openStateMenu(editor, "uzis");
    await vi.waitFor(() => expect((document.activeElement as HTMLElement | null)?.dataset.stateOption).toBe("in_progress"));
  });

  it("`from` takes only its pattern (the page id and a heading id)", async () => {
    const editor = await open(sample());
    const validate = (editor.schema.nodes.heading!.spec.attrs!.from as { validate: (value: unknown) => void }).validate;
    expect(() => validate("0b5f3c1e-0000-4000-8000-0000000000a1#portal.vpn")).not.toThrow();
    expect(() => validate("cokoli")).toThrow();
  });
});

describe("second review (2026-11-04)", () => {
  const sel = (editor: Editor, from: number, to: number) => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, from, to)));
  const textAt = (editor: Editor, text: string, offset: number) => {
    let at = -1;
    editor.state.doc.forEach((node, pos) => { if (at < 0 && node.textContent === text) at = pos + 1 + offset; });
    return at;
  };
  const clean = (editor: Editor) => JSON.parse(JSON.stringify(json(editor))) as Json;

  it("P1-4: a delete from mid-title to mid-title joins the titles as a heading does; the first keeps its identity and takes the second one's lines, never a link to itself", async () => {
    const editor = await open(sample()), original = clean(editor);
    sel(editor, textAt(editor, "Certifikát NIA", 5), textAt(editor, "Napojení na ÚZIS", 8));
    press(editor, "Delete");
    const sections = requirementSections(editor.state.doc);
    expect(sections).toHaveLength(1);
    expect(sections[0]!.facts).toMatchObject({ id: "nia-cert", title: "Certi na ÚZIS", state: "waiting_for_input", party: "IT nemocnice", deadline: "2026-11-15" });
    // Sixth review P2-2: the merged requirement's link to itself goes with its „, “ (the index stored it as a cycle).
    expect(sections[0]!.facts.dependencies.map(dependency => dependency.headingId)).toEqual(["infra-vpn"]);
    expect(json(editor).content![2]!.content!.map(node => node.type)).toEqual(["pageReference"]);
    const edited = clean(editor);
    made!.undo();
    expect(clean(editor)).toEqual(original);
    made!.redo();
    expect(clean(editor)).toEqual(edited);
  });

  it("P1-4: a delete from mid-title into the next requirement's dependency line leaves the line's rest a note of links, never part of the title", async () => {
    const editor = await open(sample()), original = clean(editor);
    const line = requirementSections(editor.state.doc)[1]!.depsPos!;
    sel(editor, textAt(editor, "Certifikát NIA", 5), line + 2);
    press(editor, "Delete");
    const [nia] = requirementSections(editor.state.doc);
    expect(nia!.facts).toMatchObject({ title: "Certi", dependencies: [], party: "IT nemocnice" });
    expect(types(editor)).not.toContain("paragraph:deps");
    expect(JSON.stringify(json(editor))).toContain("0b5f3c1e-0000-4000-8000-0000000000b2");
    made!.undo();
    expect(clean(editor)).toEqual(original);
  });

  it("P2-5: a paste of text into the dependency line goes after the lines; text a step puts into the line moves to a note", async () => {
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const editor = await open(sample()), uzis = () => requirementSections(editor.state.doc)[1]!;
    editor.commands.setTextSelection(uzis().depsPos! + 2);
    expect(editor.view.pasteHTML("<p>Text A</p><p>Text B</p>")).toBe(true);
    expect(types(editor).slice(4, 9)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph", "paragraph"]);
    expect(json(editor).content!.slice(7, 9).map(node => node.content![0]!.text)).toEqual(["Text A", "Text B"]);
    expect(uzis().facts).toMatchObject({ deadline: "2026-11-15" });
    expect(uzis().facts.dependencies).toHaveLength(2);
    // Text in the line by any other step (a paste's open end, a script): the links stay, the text goes after the facts line.
    editor.view.dispatch(editor.state.tr.insertText("navíc", uzis().depsPos! + 2));
    expect(editor.state.doc.nodeAt(uzis().depsPos!)!.textContent).toBe(", ");
    expect(json(editor).content![7]!.content![0]!.text).toMatch(/^navíc/);
    expect(uzis().facts).toMatchObject({ deadline: "2026-11-15" });
    expect(uzis().facts.dependencies).toHaveLength(2);
  });

  it("P1-2: a section link pastes back from HTML as the link node (before the link mark's a[href])", async () => {
    const editor = await open(sample());
    const element = document.createElement("div");
    element.innerHTML = `<p data-dependencies=""><a data-page-reference="0b5f3c1e-0000-4000-8000-0000000000a1" data-page-title="X" href="#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=x">X</a></p>`;
    const parsed = PMDOMParser.fromSchema(editor.schema).parse(element);
    expect(parsed.firstChild!.attrs.dependencies).toBe(true);
    expect(parsed.firstChild!.firstChild!.type.name).toBe("pageReference");
  });

  it("P1-2: copy and paste through the clipboard's HTML: a copy with fresh ids, its dependency links still links; cut and paste keeps the id", async () => {
    // jsdom has no ClipboardEvent; ProseMirror's pasteHTML only names its type.
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const editor = await open(sample()), { view } = editor;
    const uzis = () => requirementSections(editor.state.doc).filter(section => section.facts.title === "Napojení na ÚZIS");
    const html = (from: number, to: number) => { const box = document.createElement("div"); box.append(view.serializeForClipboard(editor.state.doc.slice(from, to)).dom); return box.innerHTML; };
    const { pos, end } = uzis()[0]!;
    const copied = html(pos, end);
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    expect(view.pasteHTML(copied)).toBe(true);
    const [original, copy] = uzis();
    expect(copy!.id).not.toBe("uzis");
    expect(original!.id).toBe("uzis");
    expect(copy!.facts.dependencies.map(dependency => dependency.headingId)).toEqual(["nia-cert", "infra-vpn"]);
    expect(copy!.facts.deadline).toBe("2026-11-15");
    // Cut (ProseMirror's own cut step) and paste elsewhere: a move.
    const before = requirementSections(editor.state.doc).find(section => section.id === "uzis")!;
    const moved = html(before.pos, before.end);
    sel(editor, before.pos + 1, before.end - 1);
    view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, before.pos, before.end)).deleteSelection().setMeta("uiEvent", "cut"));
    expect(requirementSections(editor.state.doc).some(section => section.id === "uzis")).toBe(false);
    editor.commands.setTextSelection(1);
    expect(view.pasteHTML(moved)).toBe(true);
    const back = requirementSections(editor.state.doc).find(section => section.id === "uzis");
    expect(back?.facts).toMatchObject({ title: "Napojení na ÚZIS", deadline: "2026-11-15" });
    expect(back!.facts.dependencies.map(dependency => dependency.headingId)).toEqual(["nia-cert", "infra-vpn"]);
  });
});

describe("the party attributes in a copy (shape §6)", () => {
  it("copy and paste of a heading keeps its party and contact; a heading made plain loses them", async () => {
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const contact = "6f1c1d3e-8a4b-4c55-9d1e-0000000000c1";
    const editor = await open(page(heading(3, "a", "Alfa", { state: "planned", party: "Eva", partyContactId: contact }), p("do 5. 11. 2026"), heading(3, "z", "Konec", { state: "planned" })));
    const { view } = editor, { pos, end } = requirementSections(editor.state.doc)[0]!;
    const box = document.createElement("div");
    box.append(view.serializeForClipboard(editor.state.doc.slice(pos, end)).dom);
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    expect(view.pasteHTML(box.innerHTML)).toBe(true);
    const copies = requirementSections(editor.state.doc).filter(section => section.facts.title === "Alfa");
    expect(copies).toHaveLength(2);
    expect(copies.map(section => [section.facts.party, section.facts.partyContactId])).toEqual([["Eva", contact], ["Eva", contact]]);
  });
});

describe("third review (2026-11-04)", () => {
  const snapshot = (editor: Editor) => JSON.parse(JSON.stringify(json(editor))) as Json;
  const read = (editor: Editor) => requirementSections(editor.state.doc).map(section => [section.id, section.facts.dependencies.length, section.facts.party, section.facts.deadline]);
  const ctrlShift = (editor: Editor, key: string) => press(editor, key, { ctrlKey: true, shiftKey: true });
  const altShift = (editor: Editor, key: string) => press(editor, key, { altKey: true, shiftKey: true });

  it("P2-1: Ctrl+Shift+↑/↓ never moves a dependency or facts line from its place, nor a block up over one; a requirement heading moves its section", async () => {
    const editor = await openVue(sample()), original = snapshot(editor), readings = read(editor);
    caretIn(editor, value => value === "do 15. 11. 2026", 3);
    expect(ctrlShift(editor, "ArrowUp")).toBe(true);
    expect(ctrlShift(editor, "ArrowDown")).toBe(true);
    caretIn(editor, value => value === "", 0);
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, requirementSections(editor.state.doc)[1]!.depsPos! + 1)));
    expect(ctrlShift(editor, "ArrowUp")).toBe(true);
    // A note under the facts line does not go up over it.
    caretIn(editor, value => value === "Poznámka.", 2);
    expect(ctrlShift(editor, "ArrowUp")).toBe(true);
    expect(snapshot(editor)).toEqual(original);
    expect(read(editor)).toEqual(readings);
    // The heading takes its section along: the order changes, every requirement reads the same.
    caretIn(editor, value => value === "Napojení na ÚZIS", 3);
    ctrlShift(editor, "ArrowUp");
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["uzis", "nia-cert"]);
    expect(read(editor).sort()).toEqual([...readings].sort());
  });

  it("P2-1: Alt+Shift+↓/↑ is KeyboardFirst's duplicate; a requirement keeps its lines (a copied dependency line is a note of links, a copied heading a new requirement with its whole section)", async () => {
    const editor = await openVue(sample()), original = snapshot(editor), readings = read(editor);
    for (const key of ["ArrowDown", "ArrowUp"]) {
      editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, requirementSections(editor.state.doc)[1]!.depsPos! + 1)));
      expect(altShift(editor, key)).toBe(true);
      expect(read(editor)).toEqual(readings);
      expect(types(editor).filter(type => type === "paragraph:deps")).toHaveLength(1);
      made!.undo();
      expect(snapshot(editor)).toEqual(original);
    }
    caretIn(editor, value => value === "Napojení na ÚZIS", 3);
    expect(altShift(editor, "ArrowDown")).toBe(true);
    const sections = requirementSections(editor.state.doc);
    expect(sections.map(section => [section.facts.title, section.facts.dependencies.length, section.facts.deadline]))
      .toEqual([["Certifikát NIA", 0, "2026-07-21"], ["Napojení na ÚZIS", 2, "2026-11-15"], ["Napojení na ÚZIS", 2, "2026-11-15"]]);
    expect(sections[1]!.id).toBe("uzis");
    expect(sections[2]!.id).not.toBe("uzis");
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("P2-2: the facts line does not join into its title, Delete at the title's end or Backspace at the line's start, with or without a dependency line", async () => {
    const editor = await open(page(heading(3, "a", "A", { state: "planned", party: "Eva" }), p("do 5. 11. 2026"), p("Poznámka."),
      heading(3, "b", "B", { state: "planned" }), deps(link("a", "A")), p("do 6. 11. 2026"), p("Text.")));
    const original = snapshot(editor), readings = read(editor);
    caretIn(editor, value => value === "A");
    expect(press(editor, "Delete")).toBe(true);
    expect(editor.state.selection.$head.parent.textContent).toBe("do 5. 11. 2026");
    expect(press(editor, "Backspace")).toBe(true);
    expect(editor.state.selection.$head.parent.textContent).toBe("A");
    caretIn(editor, value => value === "do 6. 11. 2026", "start");
    expect(press(editor, "Backspace")).toBe(true);
    caretIn(editor, value => value === "B");
    expect(press(editor, "Delete")).toBe(true);
    expect(snapshot(editor)).toEqual(original);
    expect(read(editor)).toEqual(readings);
  });

  it("P3-3: pasted HTML with an attribute value the rule refuses keeps the rest of the paste (the value drops)", async () => {
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const editor = await open(sample());
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    expect(editor.view.pasteHTML(`<p>Úvod vložení</p><h3 data-state="bogus" data-direction="sideways">Vložený nadpis</h3><h3 data-state="planned">Vložený požadavek</h3><p>Vložený text</p>`)).toBe(true);
    const text = editor.state.doc.textContent;
    expect(text).toContain("Vložený nadpis");
    expect(text).toContain("Vložený text");
    expect(requirementSections(editor.state.doc).map(section => section.facts.title)).toEqual(["Certifikát NIA", "Napojení na ÚZIS", "Vložený požadavek"]);
  });

  it("a paste that brings a requirement heading without its title brings a plain heading (fuzz seed 20356132)", async () => {
    vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
    const editor = await open(sample());
    editor.commands.setTextSelection(editor.state.doc.content.size - 1);
    editor.view.pasteHTML(`<p>Před</p><h3 data-state="planned" data-heading-id="prazdny"></h3><p data-dependencies=""><a data-page-reference="0b5f3c1e-0000-4000-8000-0000000000a1" href="#/pages/0b5f3c1e-0000-4000-8000-0000000000a1?heading=uzis">ÚZIS</a></p><p>Po</p>`);
    expect(requirementSections(editor.state.doc).map(section => section.facts.title)).toEqual(["Certifikát NIA", "Napojení na ÚZIS"]);
    expect(json(editor).content!.filter(node => node.attrs?.dependencies)).toHaveLength(1);
  });

  it("P3-1: Enter in an empty requirement title makes it a paragraph: no requirement without a title", async () => {
    const editor = await open(page(heading(2, "o", "Oddíl"), p("")));
    caretIn(editor, value => value === "");
    editor.chain().command(({ tr }) => insertRequirementHeading(tr)).run();
    expect(requirementSections(editor.state.doc)).toHaveLength(1);
    press(editor, "Enter");
    expect(requirementSections(editor.state.doc)).toEqual([]);
    expect(editor.state.selection.$head.parent.type.name).toBe("paragraph");
  });
});

describe("fourth review (2026-11-04)", () => {
  // A table's chartRef is the charts plugin's (given on the first change), not the requirement's: left out of the comparison.
  const snapshot = (editor: Editor) => JSON.parse(JSON.stringify(json(editor), (key, value: unknown) => key === "chartRef" ? undefined : value)) as Json;
  const textEnd = (editor: Editor, text: string) => { let at = -1; editor.state.doc.descendants((node, pos) => { if (at < 0 && node.isTextblock && node.textContent === text) at = pos + node.nodeSize - 1; return at < 0; }); return at; };
  const ending = { list: { type: "bulletList", content: [{ type: "listItem", content: [p("28. 10. 2026 — Test prošel")] }] }, table: { type: "table", content: [{ type: "tableRow", content: [{ type: "tableCell", content: [p("Test prošel")] }] }] },
    quote: { type: "blockquote", content: [p("Test prošel")] } } satisfies Record<string, Json>;

  it.each(Object.entries(ending))("P2-1: Delete from a requirement title's start to the end of its section's last %s takes the requirement whole; undo exact", async (_name, last) => {
    const editor = await open(page(heading(3, "a", "A", { state: "planned" }), heading(3, "uzis", "Napojení na ÚZIS", { state: "in_progress" }), deps(link("a", "A")),
      p("do 15. 11. 2026"), last, heading(3, "c", "C", { state: "planned" }), deps(link("uzis", "Napojení na ÚZIS")), p("Text.")));
    const original = snapshot(editor), end = textEnd(editor, last === ending.list ? "28. 10. 2026 — Test prošel" : "Test prošel");
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, textEnd(editor, "Napojení na ÚZIS") - "Napojení na ÚZIS".length, end)));
    press(editor, "Delete");
    expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["a", "c"]);
    expect(editor.view.dom.querySelector('[data-heading-id="c"] [data-rq-chip="ready"]')).toBeNull();
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  describe("P2-2: a paste into a requirement title or its facts line keeps the lines in place", () => {
    const uzis = (editor: Editor) => requirementSections(editor.state.doc).find(section => section.id === "uzis")!.facts;
    const paste = async (at: (editor: Editor) => number, html: string) => {
      vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
      const editor = await open(sample()), original = snapshot(editor);
      editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at(editor))));
      expect(editor.view.pasteHTML(html)).toBe(true);
      return { editor, original };
    };
    const check = (editor: Editor, original: Json) => {
      expect(uzis(editor)).toMatchObject({ deadline: "2026-11-15" });
      expect(uzis(editor).dependencies.map(dependency => dependency.headingId)).toEqual(["nia-cert", "infra-vpn"]);
      expect(requirementSections(editor.state.doc).map(section => section.id)).toEqual(["nia-cert", "uzis"]);
      made!.undo();
      expect(snapshot(editor)).toEqual(original);
    };

    it("two paragraphs at the title's end go after the facts line", async () => {
      const { editor, original } = await paste(editor => textEnd(editor, "Napojení na ÚZIS"), "<p>Jedna</p><p>Dvě</p>");
      // A heading's own paste (the first paragraph into the title, the next one after it); heal() puts the lines back under the title.
      expect(types(editor).slice(4, 9)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "paragraph", "heading"]);
      expect(uzis(editor).title).toBe("Napojení na ÚZISJedna");
      expect(json(editor).content![7]!.content![0]!.text).toBe("Dvě");
      check(editor, original);
    });

    it("a table mid-title: the first half keeps the requirement, the second half is a plain heading after the section", async () => {
      const { editor, original } = await paste(editor => textEnd(editor, "Napojení na ÚZIS") - " na ÚZIS".length, "<table><tr><td>T</td></tr></table>");
      expect(uzis(editor).title).toBe("Napojení");
      expect(json(editor).content!.find(node => node.type === "heading" && node.content?.[0]?.text === " na ÚZIS")!.attrs!.state).toBeNull();
      expect(types(editor)).toContain("table");
      check(editor, original);
    });

    it("a list at the facts line's start goes after it, the facts text stays whole", async () => {
      const { editor, original } = await paste(editor => textEnd(editor, "do 15. 11. 2026") - "do 15. 11. 2026".length, "<ul><li>Bod</li></ul>");
      expect(json(editor).content!.some(node => node.content?.[0]?.text === "do 15. 11. 2026")).toBe(true);
      expect(types(editor).slice(4, 8)).toEqual(["heading:in_progress", "paragraph:deps", "paragraph", "bulletList"]);
      check(editor, original);
    });
  });

  it("P3-1: Space on the heading's checkbox keeps the focus on it, so it toggles again", async () => {
    const editor = await open(sample());
    const box = () => editor.view.dom.querySelector<HTMLInputElement>('[data-heading-id="nia-cert"] .rq-check')!;
    box().focus();
    box().click();
    await vi.waitFor(() => expect(document.activeElement).toBe(box()));
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("done");
    box().click();
    await vi.waitFor(() => expect(document.activeElement).toBe(box()));
    expect(requirementSections(editor.state.doc)[0]!.facts.state).toBe("waiting_for_input");
  });
});

describe("input over a range across blocks (G1, fifth review)", () => {
  const doc = () => page(heading(2, "o", "Oddíl 41"), p("Úvod oddílu."), heading(3, "a", "Alfa", { state: "planned", party: "Eva" }), p("do 5. 11. 2026"), p("Poznámka A."),
    { type: "bulletList", content: [{ type: "listItem", content: [p("Bod jedna")] }, { type: "listItem", content: [p("Bod dva")] }] }, heading(3, "b", "Beta", { state: "planned" }), p("Text."));
  const startOf = (editor: Editor, text: string) => { let at = -1; editor.state.doc.descendants((node, pos) => { if (at < 0 && node.isTextblock && node.textContent === text) at = pos + 1; return at < 0; }); return at; };
  const textblocks = (editor: Editor) => { const found: { pos: number; size: number }[] = []; editor.state.doc.descendants((node, pos) => { if (node.isTextblock) found.push({ pos, size: node.content.size }); return true; }); return found; };
  const ids = (editor: Editor) => requirementSections(editor.state.doc).map(section => section.id);
  // A person selects later than history's 500 ms grouping after the load's own transactions: undo must reach exactly the state before.
  const select = (editor: Editor, from: number, to: number) => editor.view.dispatch(closeHistory(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, from, to))));

  it.each(["Oddíl 41", "Úvod oddílu.", "Alfa", "Bod jedna", "Poznámka A."])(
    "Enter from the start of „%s“ to the start and the middle of the block two on: the range goes, then the caret's Enter; no error, no new requirement, one undo", async start => {
      for (const end of ["start", "middle"] as const) {
        const editor = await openVue(doc()), original = JSON.stringify(json(editor)), blocks = textblocks(editor), before = ids(editor);
        const from = startOf(editor, start), index = blocks.findIndex(block => block.pos + 1 === from), target = blocks[index + 2]!;
        select(editor, from, target.pos + 1 + (end === "start" ? 0 : Math.floor(target.size / 2)));
        expect(press(editor, "Enter")).toBe(true);
        expect(editor.state.selection.empty).toBe(true);
        expect(ids(editor).every(id => before.includes(id))).toBe(true);
        expect(requirementSections(editor.state.doc).every(section => section.facts.title)).toBe(true);
        expect(undoDepth(editor.state)).toBe(1);
        made!.undo();
        expect(JSON.stringify(json(editor))).toBe(original);
        made!.destroy(); made = undefined;
      }
    });

  it("Enter from mid-title to mid-facts line: the second half is a plain heading, no new requirement (fifth review P2-2)", async () => {
    const editor = await openVue(sample()), original = JSON.stringify(json(editor));
    select(editor, startOf(editor, "Certifikát NIA") + 4, startOf(editor, "do 21. 7. 2026") + 3);
    expect(press(editor, "Enter")).toBe(true);
    expect(ids(editor)).toEqual(["nia-cert", "uzis"]);
    const second = json(editor).content!.find(node => node.type === "heading" && node.content?.[0]?.text?.startsWith("21."));
    expect(second?.attrs).toMatchObject({ state: null, system: "", direction: "internal", party: "", partyContactId: "" });
    made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
  });

  it("Enter from mid-title into the next title leaves no requirement without a title (fifth review P2-2)", async () => {
    const editor = await openVue(sample());
    select(editor, startOf(editor, "Certifikát NIA") + 4, startOf(editor, "Napojení na ÚZIS") + 8);
    expect(press(editor, "Enter")).toBe(true);
    expect(requirementSections(editor.state.doc).every(section => section.facts.title)).toBe(true);
  });

  it("a range inside one requirement title: deleted, then Enter splits as a heading does (the second half a plain heading)", async () => {
    const editor = await openVue(doc());
    const from = startOf(editor, "Alfa");
    select(editor, from + 1, from + 2);
    expect(press(editor, "Enter")).toBe(true);
    expect(requirementSections(editor.state.doc).find(section => section.id === "a")!.facts).toMatchObject({ title: "A", party: "Eva", deadline: "2026-11-05" });
    expect(json(editor).content!.some(node => node.type === "heading" && node.content?.[0]?.text === "fa" && node.attrs!.state === null)).toBe(true);
  });

  const commit = (editor: Editor, data: string) => {
    const event = new InputEvent("beforeinput", { inputType: "insertText", data, bubbles: true, cancelable: true });
    editor.view.dom.dispatchEvent(event);
    return event.defaultPrevented;
  };

  it("text an IME commits over a range across blocks with chips (a beforeinput the browser would apply): exactly the text, no chip label; one undo", async () => {
    const editor = await openVue(sample()), original = JSON.stringify(json(editor)), before = editor.state.doc.textContent;
    const from = startOf(editor, "Poznámka.") + 3, to = startOf(editor, "Napojení na ÚZIS") + 3;
    select(editor, from, to);
    expect(commit(editor, "x")).toBe(true);
    expect(editor.state.doc.textContent).toBe(before.slice(0, before.indexOf("Poznámka.") + 3) + "x" + before.slice(before.indexOf("Napojení na ÚZIS") + 3));
    made!.undo();
    expect(JSON.stringify(json(editor))).toBe(original);
  });

  it("a composition starting over a range across blocks deletes the range first (the IME then composes at a caret)", async () => {
    const editor = await openVue(sample()), before = editor.state.doc.textContent;
    select(editor, startOf(editor, "Poznámka.") + 3, startOf(editor, "Napojení na ÚZIS") + 3);
    editor.view.dom.dispatchEvent(new CompositionEvent("compositionstart", { bubbles: true }));
    expect(editor.state.selection.empty).toBe(true);
    expect(editor.state.doc.textContent).toBe(before.slice(0, before.indexOf("Poznámka.") + 3) + before.slice(before.indexOf("Napojení na ÚZIS") + 3));
  });

  it.each([["from a title's start to the next title's middle", "Certifikát NIA", 0, "Napojení na ÚZIS", 8], ["from a note's middle to a title's start", "Poznámka.", 3, "Napojení na ÚZIS", 0],
    ["from a title's start to the next facts line's middle", "Certifikát NIA", 0, "do 15. 11. 2026", 3]] as const)(
    "typing over a range %s is a Backspace over it, then the text (fifth review P3)", async (_name, start, startAt, end, endAt) => {
      const outcome = async (type: (editor: Editor) => void) => {
        const editor = await openVue(sample());
        select(editor, startOf(editor, start) + startAt, startOf(editor, end) + endAt);
        type(editor);
        const result = JSON.stringify(json(editor));
        made!.destroy(); made = undefined;
        return result;
      };
      const typed = await outcome(editor => commit(editor, "x"));
      expect(typed).toBe(await outcome(editor => { press(editor, "Backspace"); editor.view.dispatch(editor.state.tr.insertText("x")); }));
    });
});

describe("the heading's pickers (one table: kind, service, deadline, ID, blocked; the PM's actions)", () => {
  it("Ctrl+. lists the pickers and the PM's actions; the kind menu, the deadline field (its error, then a date) and B on the state chip work", async () => {
    const editor = await open(sample());
    caretIn(editor, value => value === "Napojení na ÚZIS", 3);
    const intentions = headingIntentions(editor);
    expect(intentions.map(item => [item.label, item.description ?? ""])).toEqual([["Stav…", ""], ["Druh…", ""], ["Služba…", ""], ["Čekáme na…", ""], ["Termín…", ""], ["+ závislost", ""],
      ["Komentář", expect.stringMatching(/Alt.*M$/)], ["ID…", "uzis"], ["Vyzvali jsme", "V na čipu stavu"], ["Urgovali jsme", "U na čipu stavu"], ["Dodáno", "H na čipu stavu"], ["Blokuje…", "B na čipu stavu"]]);
    editor.view.dom.querySelector<HTMLElement>('[data-rq-id="uzis"] [data-rq-chip="kind"]')!.click();
    const kinds = await vi.waitFor(() => { const rows = [...document.querySelectorAll<HTMLButtonElement>('[role="menuitemradio"]')]; expect(rows.length).toBeGreaterThan(1); return rows; });
    const other = kinds.find(row => row.getAttribute("aria-checked") === "false")!, label = other.textContent;
    other.click();
    expect(editor.view.dom.querySelector('[data-rq-id="uzis"] [data-rq-chip="kind"]')!.textContent).toBe(label);
    intentions.find(item => item.label === "Termín…")!.run();
    const form = await vi.waitFor(() => document.querySelector<HTMLFormElement>(".md-module-form-popover form")!);
    const input = form.querySelector("input")!;
    input.value = "zítra snad";
    form.requestSubmit();
    expect(form.querySelector('[role="alert"]')!.textContent).toContain("Termín zadejte");
    input.value = "10. 9. 2026";
    form.requestSubmit();
    expect(requirementSections(editor.state.doc)[1]!.facts.deadline).toBe("2026-09-10");
    editor.view.dom.querySelector<HTMLElement>('[data-rq-id="uzis"] [data-rq-chip="state"]')!.dispatchEvent(new KeyboardEvent("keydown", { key: "b", bubbles: true }));
    await vi.waitFor(() => expect(document.querySelector(".md-module-form-popover label")?.textContent).toContain("Blokováno"));
  });
});

describe("where „Doplnit prerekvizity“ puts a requirement no catalog heading places (regression review P3)", () => {
  it("after the section of the requirement that needs it, beside it at its level", async () => {
    const editor = await open(sample()), sections = requirementSections(editor.state.doc);
    const end = endOfRequirement(editor.state.doc, "nia-cert");
    expect(end).toBe(sections[1]!.headingPos);
    expect(headingLevelAt(editor.state.doc, end)).toBe(3);
    expect(endOfRequirement(editor.state.doc, "neni")).toBe(-1);
  });
});

describe("the automatic overview's work per change (regression review P3)", () => {
  it("looks at the outline again only for a step that touches a heading, an overview or a legacy block, not for typing in a paragraph", async () => {
    const editor = await open(sample()), at = (text: string) => { let pos = -1; editor.state.doc.forEach((node, offset) => { if (pos < 0 && node.textContent === text) pos = offset; }); return pos; };
    expect(touchesOutline(editor.state.tr.insertText("x", at("Poznámka.") + 2))).toBe(false);
    expect(touchesOutline(editor.state.tr.insertText("x", at("Certifikát NIA") + 2))).toBe(true);
    expect(touchesOutline(editor.state.tr.setNodeAttribute(at("Napojení na ÚZIS"), "state", "done"))).toBe(true);
    expect(touchesOutline(editor.state.tr.insert(at("Poznámka."), editor.schema.nodes.heading!.create({ level: 3 })))).toBe(true);
    expect(touchesOutline(editor.state.tr.delete(at("Provoz"), at("Provoz") + editor.state.doc.nodeAt(at("Provoz"))!.nodeSize))).toBe(true);
  });
});

describe("sixth review", () => {
  const snapshot = (editor: Editor) => JSON.parse(JSON.stringify(json(editor))) as Json;
  const texts = (editor: Editor) => json(editor).content!.map(node => (node.content ?? []).map(child => child.text ?? child.type).join(""));
  const lineAt = (editor: Editor, text: string) => { let at = -1; editor.state.doc.forEach((node, pos) => { if (at < 0 && node.textContent === text) at = pos; }); return at; };

  it("P2-1: the facts line's text deleted (Home, Shift+End, Delete): a facts-like note below stays a note where it was; undo exact", async () => {
    const editor = await open(page(heading(3, "vpn", "VPN přístup", { state: "done" }), p("do 1. 12. 2026"), p("Přístup funguje od 29. 9. 2026."), p("do 2. 12. 2026"), p("Konec.")));
    const original = snapshot(editor), at = lineAt(editor, "do 1. 12. 2026");
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at + 1, at + 1 + "do 1. 12. 2026".length)));
    expect(press(editor, "Delete")).toBe(true);
    expect(texts(editor)).toEqual(["VPN přístup", "", "Přístup funguje od 29. 9. 2026.", "do 2. 12. 2026", "Konec."]);
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ party: "", deadline: "" });
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("P2-1: the facts line typed over into a note („do 1. 12. 2026“ → „Volala 1. 12. 2026“): the facts-like notes below stay notes in their order; undo exact", async () => {
    const editor = await open(page(heading(3, "a", "Alfa", { state: "planned" }), p("do 1. 12. 2026"), p("pro Honza"), p("do 2. 12. 2026"), p("Konec.")));
    const original = snapshot(editor), at = lineAt(editor, "do 1. 12. 2026");
    editor.view.dispatch(editor.state.tr.insertText("Volala", at + 1, at + 3));
    expect(texts(editor)).toEqual(["Alfa", "Volala 1. 12. 2026", "pro Honza", "do 2. 12. 2026", "Konec."]);
    expect(requirementSections(editor.state.doc)[0]!.facts).toMatchObject({ party: "", deadline: "" });
    made!.undo();
    expect(snapshot(editor)).toEqual(original);
  });

  it("P3-1: Enter in the middle of a requirement title: the plain second half takes no aliases", async () => {
    const editor = await open(page(heading(3, "a", "Alfa beta", { state: "planned", aliases: "stare-a" }), p("od Eva"), p("Konec.")));
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 1 + "Alfa".length)));
    expect(press(editor, "Enter")).toBe(true);
    const halves = json(editor).content!.filter(node => node.type === "heading");
    expect(halves.map(node => [node.attrs!.headingId === "a", node.attrs!.aliases, node.attrs!.state])).toEqual([[true, "stare-a", "planned"], [false, "", null]]);
  });

  it("P3-2: Enter in the middle of a dependency line: the note it leaves starts with its link, not with „, “", async () => {
    const editor = await open(page(heading(3, "a", "Alfa", { state: "planned" }), heading(3, "b", "Beta", { state: "planned" }), deps(link("a", "Alfa"), link("c", "Gama")),
      p("do 5. 11. 2026"), heading(3, "c", "Gama", { state: "planned" }), p("Konec.")));
    const line = requirementSections(editor.state.doc)[1]!.depsPos!;
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, line + 2)));
    expect(press(editor, "Enter")).toBe(true);
    const note = json(editor).content!.find(node => node.type === "paragraph" && !node.attrs?.dependencies && node.content?.some(child => child.type === "pageReference"));
    expect(note!.content!.map(child => child.type)).toEqual(["pageReference"]);
    expect(requirementSections(editor.state.doc)[1]!.facts.dependencies.map(dependency => dependency.headingId)).toEqual(["a"]);
  });
});

describe("fifth review: a paste with a heading, Delete at a list's end", () => {
  const snapshot = (editor: Editor) => JSON.parse(JSON.stringify(json(editor))) as Json;
  const read = (editor: Editor) => requirementSections(editor.state.doc).map(section => [section.id, section.facts.title, section.facts.dependencies.length, section.facts.party, section.facts.deadline]);
  const offsetIn = (editor: Editor, text: string, offset: number) => { let at = -1; editor.state.doc.forEach((node, pos) => { if (at < 0 && node.textContent === text) at = pos + 1 + offset; }); return at; };

  it.each([["the title", (editor: Editor) => offsetIn(editor, "Napojení na ÚZIS", 4)], ["the dependency line", (editor: Editor) => requirementSections(editor.state.doc)[1]!.depsPos! + 2],
    ["the facts line", (editor: Editor) => offsetIn(editor, "do 15. 11. 2026", 3)]] as const)(
    "a slice with a heading pasted into a requirement's %s goes after its notes; its lines read the same; undo exact (P2-3)", async (_name, at) => {
      vi.stubGlobal("ClipboardEvent", class extends Event { readonly clipboardData = null; });
      const editor = await open(sample()), original = snapshot(editor), readings = read(editor);
      editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, at(editor))));
      expect(editor.view.pasteHTML("<h3>Kapitola</h3><p>Text kapitoly</p>")).toBe(true);
      expect(read(editor)).toEqual(readings);
      expect(json(editor).content!.map(node => node.content?.[0]?.text ?? "").slice(6, 10)).toEqual(["do 15. 11. 2026", "Kapitola", "Text kapitoly", "Provoz"]);
      made!.undo();
      expect(snapshot(editor)).toEqual(original);
    });

  it("Delete at the very end of a list before a requirement heading moves the caret into the title; nothing joins (P3)", async () => {
    const editor = await open(page(p("Úvod"), { type: "bulletList", content: [{ type: "listItem", content: [p("Bod")] }] }, heading(3, "a", "Alfa", { state: "planned" }),
      deps(link("b", "Beta")), p("do 5. 11. 2026"), heading(3, "b", "Beta", { state: "planned" }), p("Text.")));
    const original = snapshot(editor);
    let end = -1;
    editor.state.doc.descendants((node, pos) => { if (node.isTextblock && node.textContent === "Bod") end = pos + node.nodeSize - 1; return end < 0; });
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, end)));
    expect(press(editor, "Delete")).toBe(true);
    expect(snapshot(editor)).toEqual(original);
    expect(editor.state.selection.$head.parent.textContent).toBe("Alfa");
  });
});

describe("readiness (G1 review): a link to a plain heading is „neznámé“", () => {
  it("counts the same at mount, after a keystroke elsewhere and after undo; lists the plain heading as „neznámé“; no „Hotovo“ question for it", async () => {
    const editor = await open(page(heading(2, "modul", "Modul"), heading(3, "a", "A", { state: "planned" }), deps(link("modul", "Modul"), link("b", "B")),
      heading(3, "b", "B", { state: "in_progress" }), heading(3, "c", "C", { state: "planned" }), deps(link("modul", "Modul")), p("Text.")));
    const chips = () => ["a", "c"].map(id => editor.view.dom.querySelector(`[data-heading-id="${id}"] [data-rq-chip="ready"]`)?.getAttribute("aria-label") ?? "");
    const atMount = chips();
    expect(atMount).toEqual(["Čeká na 1 prerekvizitu", ""]);
    caretIn(editor, value => value === "Text.");
    editor.commands.insertContent("x");
    expect(chips()).toEqual(atMount);
    made!.undo();
    expect(chips()).toEqual(atMount);
    // The list shows both, the plain heading as „neznámé“.
    openAwaited(editor, "a");
    const rows = await vi.waitFor(() => { const found = [...document.querySelectorAll(".atlas-requirement-popover .md-context-menu-item")]; expect(found).toHaveLength(2); return found; });
    expect(rows.map(row => row.textContent)).toEqual(["Modulneznámé", "Brozpracováno"]);
    // „Hotovo“ on C (it waits for the plain heading only) asks nothing.
    toggleDone(editor, "c");
    expect(requirementSections(editor.state.doc).find(section => section.id === "c")!.facts.state).toBe("done");
  });
});

describe("AI rewrite (second review P1-3)", () => {
  it("keeps a requirement heading and its dependency line whole: they are opaque in the projection, the notes are rewritten", async () => {
    const editor = await open(sample());
    const projection = projectMarkdownAssistSelection(editor, { from: 0, to: editor.state.doc.content.size })!;
    expect(projection.text).not.toContain("Certifikát NIA");
    expect(projection.text).toContain("Identita občana");
    const restored = editor.schema.nodeFromJSON(projection.restore(projection.text.replace("Poznámka.", "Upravená poznámka.")));
    projection.dispose();
    expect(requirementSections(restored).map(section => [section.id, section.facts.state, section.facts.dependencies.length, section.facts.deadline]))
      .toEqual([["nia-cert", "waiting_for_input", 0, "2026-07-21"], ["uzis", "in_progress", 2, "2026-11-15"]]);
    expect(restored.textContent).toContain("Upravená poznámka.");
  });
});

describe("a slash block lands at the caret", () => {
  it("inserts the information box where /box was typed, below an overview, through the page's own slash menu", async () => {
    const editor = await openVue(page({ type: "overview", attrs: { kind: "requirements" } }, heading(2, "a", "Úvod", { state: "planned" }), p("Poznámka"), heading(2, "b", "Druhý"), p("")));
    editor.commands.focus("end");
    editor.commands.insertContent("/box");
    const item = await vi.waitFor(() => { const found = [...document.querySelectorAll<HTMLElement>("[data-testid=slash-menu] [role=option]")].find(row => row.textContent?.includes("Informační box")); expect(found).toBeTruthy(); return found!; });
    item.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true })); item.click();
    await vi.waitFor(() => expect(json(editor).content!.some(node => node.type === "callout")).toBe(true));
    expect(json(editor).content!.slice(0, 5).map(node => node.type)).toEqual(["overview", "heading", "paragraph", "heading", "callout"]);
  });
});

/** The fold key of a heading on the sample page (foldState.ts keeps folds by `page#headingId`). */
const fk = (id: string) => `${host.pageId()}#${id}`;

describe("fold", () => {
  it("folds a requirement heading as any heading: everything up to the next heading of its level or higher", async () => {
    const editor = await open(sample());
    const chevron = editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"] > .atlas-heading-fold')!;
    expect(chevron).not.toBeNull();
    chevron.click();
    await vi.waitFor(() => expect(editor.view.dom.querySelectorAll(".atlas-heading-folded")).toHaveLength(2));
    expect([...editor.view.dom.querySelectorAll(".atlas-heading-folded")].map(element => element.textContent)).toEqual(["do 21. 7. 2026", "Poznámka."]);
    // The fold is the shared fold state's (G2 foldState.ts), by the heading id.
    expect(isFolded(fk("nia-cert"))).toBe(true);
    // The chevron is drawn anew with the fold: the current one opens it.
    editor.view.dom.querySelector<HTMLElement>('[data-heading-id="nia-cert"] > .atlas-heading-fold')!.click();
    await vi.waitFor(() => expect(editor.view.dom.querySelectorAll(".atlas-heading-folded")).toHaveLength(0));
    expect(isFolded(fk("nia-cert"))).toBe(false);
  });

  it("folds and opens requirement headings by their ids through foldAll („Sbalit vše“, the Detail slider), dotted ids too", async () => {
    resetFoldStateForTests();
    const editor = await open(sample());
    renameHeadingId(editor, "nia-cert", "portal.nia");
    const ids = requirementSections(editor.state.doc).map(section => section.id), keys = ids.map(fk);
    expect(ids).toEqual(["portal.nia", "uzis"]);
    foldAll(keys, true);
    // Both sections under their headings: NIA's facts and note, ÚZIS's dependency line and facts.
    await vi.waitFor(() => expect(editor.view.dom.querySelectorAll(".atlas-heading-folded")).toHaveLength(4));
    expect([...editor.view.dom.querySelectorAll(".atlas-heading-folded")].map(element => element.textContent)).toContain("do 15. 11. 2026");
    expect(keys.map(isFolded)).toEqual([true, true]);
    foldAll(keys, false);
    await vi.waitFor(() => expect(editor.view.dom.querySelectorAll(".atlas-heading-folded")).toHaveLength(0));
  });

  it("keeps a folded requirement folded when its ID is renamed, and drops the old entry", async () => {
    resetFoldStateForTests();
    const editor = await open(sample());
    foldAll([fk("nia-cert")], true);
    renameHeadingId(editor, "nia-cert", "nia-podpis");
    expect(isFolded(fk("nia-podpis"))).toBe(true);
    expect(isFolded(fk("nia-cert"))).toBe(false);
    await vi.waitFor(() => expect(editor.view.dom.querySelectorAll(".atlas-heading-folded")).toHaveLength(2));
  });

  it("offers „Sbalit sekci“ / „Rozbalit sekci“ in the intentions of a plain heading and a requirement alike", async () => {
    resetFoldStateForTests();
    const editor = await open(sample());
    // The menu's rows come from the registered providers: the heading fold's is one of them.
    for (const [title, id] of [["Identita občana", "identita"], ["Certifikát NIA", "nia-cert"]] as const) {
      caretIn(editor, value => value === title, 2);
      openMarkdownIntentions(editor);
      const row = await vi.waitFor(() => { const found = [...document.querySelectorAll<HTMLElement>('[data-testid=intentions] [role=option]')].find(item => item.textContent?.includes("Sbalit sekci")); expect(found).toBeTruthy(); return found!; });
      row.click();
      await vi.waitFor(() => expect(isFolded(fk(id))).toBe(true));
    }
  });

  it("offers a heading's controls on the grip's keyboard path (Alt+F10, Tab) in the order they are drawn: checkbox, label chips, state, chevron", async () => {
    const editor = await open(sample());
    const controls = (title: string) => {
      let found: HTMLElement[] = [];
      editor.state.doc.forEach((node, pos) => { if (node.type.name === "heading" && node.textContent === title) found = markdownBlockKeyboardControls(editor, { pos, node }); });
      return found.map(element => element.className.includes("atlas-heading-fold") ? "fold" : element.className.includes("rq-check") ? "check" : element.dataset.rqChip);
    };
    expect(controls("Certifikát NIA")).toEqual(["check", "kind", "service", "state", "fold"]);
    expect(controls("Identita občana")).toEqual(["fold"]);
    // The DOM in the same order, so the browser's Tab after the chevron leaves the heading (not back to its first chip).
    const nia = editor.view.dom.querySelector('[data-heading-id="nia-cert"]')!;
    expect([...nia.querySelectorAll<HTMLElement>("input, button")].map(element => element.className.includes("atlas-heading-fold") ? "fold" : element.className.includes("rq-check") ? "check" : element.dataset.rqChip))
      .toEqual(["check", "kind", "service", "state", "fold"]);
  });

  it("dims a done requirement's title: the heading carries data-state and the rule outweighs the editor's heading colour", async () => {
    const editor = await open(page(heading(2, "a", "Hotovo", { state: "done" }), heading(2, "b", "Rozděláno", { state: "in_progress" })));
    const style = document.head.appendChild(document.createElement("style"));
    // The theme's variable (palettes.css): jsdom resolves var(), and with neither --text-dim nor --muted set the colour is the parent's.
    style.textContent = `:root { --text-dim: rgb(1, 2, 3); }\n${contentCss}\n${headingCss}`;
    const color = (id: string) => getComputedStyle(editor.view.dom.querySelector(`[data-heading-id="${id}"]`)!).color;
    expect(editor.view.dom.querySelector('[data-heading-id="a"]')!.getAttribute("data-state")).toBe("done");
    // The page's markup: .md-editor > ... > .editor-body > .ProseMirror.
    document.body.classList.add("md-editor"); editor.view.dom.parentElement!.classList.add("editor-body");
    expect(color("a")).not.toBe(color("b"));
    expect(color("a")).toBe("rgb(1, 2, 3)");
    style.remove(); document.body.classList.remove("md-editor");
    // Netýká se is dimmed the same way, never struck through; the round checkbox outweighs the task list's square one by its selector
    // (both classes), whatever order the bundle loads the sheets in.
    expect(headingCss).not.toContain("line-through");
    expect(headingCss).toMatch(/:is\(\[data-state="done"\], \[data-state="not_applicable"\]\) \{ color: var\(--text-dim/);
    expect(headingCss).toMatch(/\.md-editor \.editor-body input\.md-task-check\.rq-check \{[^}]*border-radius: 50%/);
    expect(contentCss).toMatch(/\.md-editor \.editor-body input\.md-task-check \{/);
  });

  it("marks the chips that open a menu, expanded while that is open", async () => {
    const editor = await open(sample());
    const chip = () => editor.view.dom.querySelector<HTMLElement>('[data-rq-id="uzis"] [data-rq-chip="kind"]')!;
    expect(chip().getAttribute("aria-haspopup")).toBe("menu");
    expect(chip().getAttribute("aria-expanded")).toBe("false");
    chip().click();
    await vi.waitFor(() => expect(chip().getAttribute("aria-expanded")).toBe("true"));
    document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    await vi.waitFor(() => expect(chip().getAttribute("aria-expanded")).toBe("false"));
    // Another popover anchored on the editor (a slash menu, a picker at the caret) must not mark the chip that was open before.
    editor.view.dom.setAttribute("aria-expanded", "true");
    await new Promise(resolve => setTimeout(resolve, 0));
    expect(chip().getAttribute("aria-expanded")).toBe("false");
    editor.view.dom.setAttribute("aria-expanded", "false");
  });
});
