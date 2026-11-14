import { Editor, Extension } from "@tiptap/core";
import Collaboration from "@tiptap/extension-collaboration";
import StarterKit from "@tiptap/starter-kit";
import { afterEach, describe, expect, it } from "vitest";
import * as Y from "yjs";
import { FOLDED, foldAll, foldedBy, isFolded, sectionsAround, unfoldTo } from "./headingFold";
import { createHeadingFoldPlugin } from "./headingFoldPlugin";
import { resetFoldStateForTests } from "./foldState";

afterEach(() => {
  try { localStorage.clear(); } catch { /* none */ }
  resetFoldStateForTests();
  document.body.innerHTML = "";
});

describe("sections of a page", () => {
  it("hide everything under a folded heading up to the next one of the same or a higher level", () => {
    // h2 p h3 p h2 p: the first h2 folded hides its paragraph, the h3 and its paragraph, not the next h2.
    expect(foldedBy([2, 0, 3, 0, 2, 0], [true, false, false, false, false, false])).toEqual([-1, 0, 0, 0, -1, -1]);
    // A folded h3 inside a folded h2 is hidden with it; alone it hides only its own paragraph.
    expect(foldedBy([2, 0, 3, 0, 2, 0], [false, false, true, false, false, false])).toEqual([-1, -1, -1, 2, -1, -1]);
    expect(sectionsAround([1, 2, 0, 3, 0], 4)).toEqual([3, 1, 0]);
    expect(sectionsAround([1, 2, 0, 3, 0], 3)).toEqual([1, 0]);
  });
});

function read(html: string, page?: string) {
  const container = document.body.appendChild(document.createElement("div"));
  container.innerHTML = html;
  const controller = new AbortController();
  const cleanup = createHeadingFoldPlugin(page ? () => page : undefined).create({} as never).onRender!({ container, ownerDocument: document, signal: controller.signal, document: { type: "doc" } as never });
  return { container, cleanup: () => { controller.abort(); (cleanup as () => void)(); } };
}

describe("reading", () => {
  it("folds a section with its chevron, remembers it by the heading's id and opens it for an anchor", () => {
    const { container, cleanup } = read(`<h2 data-heading-id="a">A</h2><p>a</p><h3 data-heading-id="b">B</h3><p>b</p><h2 data-heading-id="c">C</h2>`, "p1");
    const toggles = container.querySelectorAll<HTMLButtonElement>(".atlas-heading-fold");
    // The last heading has nothing under it, so it has no chevron.
    expect(toggles).toHaveLength(2);
    expect(toggles[0]!.getAttribute("aria-label")).toBe("Sbalit sekci: A");
    // The heading is named by its text alone, not by its chevron's label too.
    expect(container.querySelector("h2")!.getAttribute("aria-label")).toBe("A");
    toggles[0]!.click();
    expect([...container.children].map(block => block.classList.contains(FOLDED))).toEqual([false, true, true, true, false]);
    expect(toggles[0]!.getAttribute("aria-expanded")).toBe("false");
    // The page and the id, never the text.
    expect(isFolded("p1#a")).toBe(true);
    expect(JSON.parse(localStorage.getItem("atlas.folds2")!)).toEqual(["p1#a"]);
    unfoldTo(container.querySelector("h3")!);
    expect(container.querySelectorAll(`.${FOLDED}`)).toHaveLength(0);
    cleanup();
    expect(container.querySelectorAll(".atlas-heading-fold")).toHaveLength(0);
    expect(container.querySelector("h2")!.hasAttribute("aria-label")).toBe(false);
  });

  it("folds the same heading id on two pages apart (a shared template)", () => {
    const html = `<h2 data-heading-id="portal.vpn">VPN</h2><p>a</p>`;
    const first = read(html, "p1"), second = read(html, "p2");
    first.container.querySelector<HTMLButtonElement>(".atlas-heading-fold")!.click();
    expect(first.container.querySelectorAll(`.${FOLDED}`)).toHaveLength(1);
    expect(second.container.querySelectorAll(`.${FOLDED}`)).toHaveLength(0);
    first.cleanup(); second.cleanup();
  });

  it("gives a heading without an id no chevron: its fold would follow its text (VS1)", () => {
    const { container, cleanup } = read(`<h2>A</h2><p>a</p>`);
    expect(container.querySelector(".atlas-heading-fold")).toBeNull();
    cleanup();
  });
});

// The heading-anchors plugin's attribute (Atlas.Markdown), as the page editor has it.
const HeadingIds = Extension.create({
  name: "testHeadingIds",
  addGlobalAttributes: () => [{ types: ["heading"], attributes: { headingId: {
    default: null, parseHTML: element => element.getAttribute("data-heading-id"), renderHTML: attrs => attrs.headingId ? { "data-heading-id": attrs.headingId } : {},
  } } }],
});
const PAGE = `<h2 data-heading-id="a">A</h2><p>a</p><h2 data-heading-id="b">B</h2><p>b</p>`;

describe("editing", () => {
  function edit(content = PAGE, extensions: Extension[] = []) {
    const element = document.body.appendChild(document.createElement("div"));
    const editor = new Editor({ element, extensions: [StarterKit, HeadingIds, ...extensions, ...createHeadingFoldPlugin().create({} as never).extensions!], content });
    const altKey = (key: string) => editor.view.someProp("handleKeyDown", handle => handle(editor.view, new KeyboardEvent("keydown", { key, altKey: true })));
    const folded = () => [...editor.view.dom.children].map(block => block.classList.contains(FOLDED));
    return { editor, altKey, folded };
  }

  it("folds with Alt+↑ on the heading and never writes into the document", () => {
    const { editor, altKey, folded } = edit();
    const before = JSON.stringify(editor.getJSON());
    const chevron = editor.view.dom.querySelector(".atlas-heading-fold");
    editor.commands.setTextSelection(2);
    expect(altKey("ArrowUp")).toBe(true);
    expect(folded()).toEqual([false, true, false, false]);
    // The same chevron says the new state: a fold of every section (the Detail slider) draws no new buttons (perf guard).
    expect(editor.view.dom.querySelector(".atlas-heading-fold")).toBe(chevron);
    expect(chevron!.getAttribute("aria-expanded")).toBe("false");
    expect(chevron!.getAttribute("aria-label")).toContain("A");
    expect(JSON.stringify(editor.getJSON())).toBe(before);
    expect(editor.view.dom.querySelector("h2")!.getAttribute("aria-label")).toBe("A");
    expect(altKey("ArrowDown")).toBe(true);
    expect(folded()).toEqual([false, false, false, false]);
    expect(chevron!.getAttribute("aria-expanded")).toBe("true");
    editor.destroy();
  });

  it("folds the section of the paragraph holding the caret with Alt+↑ and puts the caret at the end of its heading (J10.4)", () => {
    const { editor, altKey, folded } = edit();
    editor.commands.setTextSelection(5);
    expect(editor.state.selection.$head.parent.textContent).toBe("a");
    expect(altKey("ArrowUp")).toBe(true);
    expect(folded()).toEqual([false, true, false, false]);
    expect(editor.state.selection.head).toBe(2);
    editor.destroy();
  });

  it("moves the caret out of the section a chevron folds, at once (VS3)", () => {
    const { editor, folded } = edit();
    editor.commands.setTextSelection(5);
    (editor.view.dom.querySelector(".atlas-heading-fold") as HTMLButtonElement).click();
    expect(editor.state.selection.head).toBe(2);
    expect(folded()).toEqual([false, true, false, false]);
    editor.destroy();
  });

  it("opens a folded section the caret arrives in", async () => {
    const { editor, folded } = edit();
    (editor.view.dom.querySelector(".atlas-heading-fold") as HTMLButtonElement).click();
    expect(folded()).toEqual([false, true, false, false]);
    editor.commands.setTextSelection(5);
    await Promise.resolve();
    expect(folded()).toEqual([false, false, false, false]);
    editor.destroy();
  });

  it("folds a block inserted into a folded section too, and keeps the folds while typing elsewhere (VS2)", () => {
    const { editor, folded } = edit();
    foldAll(["a"], true);
    expect(folded()).toEqual([false, true, false, false]);
    // A colleague's paragraph lands in the folded section: the fold is a range, not the DOM it had.
    editor.view.dispatch(editor.state.tr.insert(editor.state.doc.child(0).nodeSize + editor.state.doc.child(1).nodeSize, editor.schema.nodes.paragraph!.create(null, editor.schema.text("new"))));
    expect(folded()).toEqual([false, true, true, false, false]);
    editor.commands.insertContentAt(editor.state.doc.content.size - 1, "typed");
    expect(folded()).toEqual([false, true, true, false, false]);
    editor.destroy();
  });

  it("gives Alt+↑ back without a heading that has an id", () => {
    const { editor, altKey } = edit(`<h2>A</h2><p>a</p>`);
    editor.commands.setTextSelection(5);
    expect(altKey("ArrowUp")).toBeFalsy();
    expect(editor.view.dom.querySelector(".atlas-heading-fold")).toBeNull();
    editor.destroy();
  });

  it("writes nothing into the shared document or its history when folding (J10.1)", () => {
    const ydoc = new Y.Doc();
    const { editor, altKey } = edit(undefined, [Collaboration.configure({ document: ydoc })]);
    editor.commands.setContent(PAGE);
    const updates: Uint8Array[] = [];
    ydoc.on("update", update => updates.push(update));
    editor.commands.setTextSelection(5);
    expect(altKey("ArrowUp")).toBe(true);
    foldAll(["b"], true);
    foldAll(["a", "b"], false);
    // The chevron with the caret in its section (the caret goes to the heading's end, F34) and back.
    editor.commands.setTextSelection(editor.state.doc.child(0).nodeSize + editor.state.doc.child(1).nodeSize + editor.state.doc.child(2).nodeSize + 1);
    const before = editor.state.doc;
    for (let i = 0; i < 2; i++) [...editor.view.dom.querySelectorAll<HTMLButtonElement>(".atlas-heading-fold")].at(-1)!.click();
    expect(editor.state.doc.eq(before)).toBe(true);
    expect(updates).toHaveLength(0);
    editor.destroy();
    ydoc.destroy();
  });

  it("gives the editor the focus on a chevron's click while the title field held it, the caret at the heading's end (F3)", () => {
    const { editor, folded } = edit();
    const title = document.body.appendChild(document.createElement("textarea"));
    title.focus();
    const toggles = () => [...editor.view.dom.querySelectorAll<HTMLButtonElement>(".atlas-heading-fold")];
    toggles()[1]!.click();
    expect(folded()).toEqual([false, false, false, true]);
    expect(document.activeElement).toBe(editor.view.dom);
    expect(editor.state.selection.$head.parent.textContent).toBe("B");
    expect(editor.state.selection.$head.parentOffset).toBe(1);
    // With the caret already in the text it stays where it is.
    editor.commands.setTextSelection(2);
    toggles()[0]!.click();
    expect(editor.state.selection.head).toBe(2);
    editor.destroy();
  });

  it("keeps the chevron out of a writer's Tab order (Esc, Tab leaves the editor; Alt+F10 reaches it) and in a reader's (F5)", () => {
    const { editor } = edit();
    const tabIndices = () => [...editor.view.dom.querySelectorAll<HTMLElement>(".atlas-heading-fold")].map(toggle => toggle.tabIndex);
    expect(tabIndices()).toEqual([-1, -1]);
    editor.setEditable(false);
    expect(tabIndices()).toEqual([0, 0]);
    // Redrawn (a fold) while read: still a reader's stop.
    foldAll(["a"], true);
    expect(tabIndices()).toEqual([0, 0]);
    editor.setEditable(true);
    expect(tabIndices()).toEqual([-1, -1]);
    editor.destroy();
  });
});
