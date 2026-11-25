import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import type { Editor } from "@tiptap/core";
import type { BoardBlockNode } from "../../../boards/boardApi";
import { useEditorBlockModules } from "../../../boards/editorBlocks";
import BoardBlock from "./BoardBlock.vue";

// The board loads the editor's `a-` dialect as it opens (BoardView); a block's editor writes its Markdown with it.
beforeAll(() => loadMarkdownDialect());
// Mermaid, which the inserted diagram renders with, loaded cold within the insertion case: half its time, most of 5 s under CPU load.
beforeAll(() => import("mermaid"));
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

const contact: BoardBlockNode = { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } };
const issue: BoardBlockNode = { type: "youtrackIssue", attrs: { id: "2-1", key: "ATL-1", title: "Oprava přihlášení", url: "https://yt.example/issue/ATL-1", state: "Open" } };

function show(props: Record<string, unknown>) {
  wrapper = mount(BoardBlock, { props: { block: null, ...props }, attachTo: document.body });
  return wrapper;
}
async function liveEditor() {
  await vi.waitFor(() => expect(document.querySelector<HTMLElement & { editor?: Editor }>(".board-block .ProseMirror[contenteditable=true]")?.editor).toBeDefined());
  await flushPromises();
  return document.querySelector<HTMLElement & { editor?: Editor }>(".board-block .ProseMirror")!.editor!;
}
const escape = () => document.querySelector(".board-block .ProseMirror")!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", keyCode: 27, bubbles: true, cancelable: true }));

describe("BoardBlock at rest", () => {
  it("draws the block with the editor's own plugin in its editor, read-only (one editor, no static render)", async () => {
    show({ block: contact });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Jana Nováková"));
    expect(wrapper!.find(".md-renderer").exists()).toBe(false);
    expect(wrapper!.find(".ProseMirror[contenteditable=false]").exists()).toBe(true);
    expect(wrapper!.find("[contenteditable=true]").exists()).toBe(false);
    expect(wrapper!.attributes("data-block-type")).toBe("contactCard");
  });

  it("renders an inline component as the pickers store it (a YouTrack issue) in its paragraph", async () => {
    show({ block: issue });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("ATL-1"));
    expect(wrapper!.find("[data-board-block-fallback]").exists()).toBe(false);
  });

  it("shows a node of an unknown plugin as a fallback that keeps its data, and does not edit it", async () => {
    show({ block: { type: "kanban", attrs: { columns: 3 }, content: [{ type: "text", text: "Sloupec Hotovo" }] } });
    expect(wrapper!.get("[data-board-block-fallback]").text()).toContain("Komponentu „kanban“");
    expect(wrapper!.text()).toContain("Sloupec Hotovo");
    await wrapper!.setProps({ editing: true });
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
    expect(wrapper!.emitted("commit")).toBeUndefined();
    expect(wrapper!.find(".ProseMirror[contenteditable=true]").exists()).toBe(false);
  });

  it("keeps a press on a control inside the block from the canvas", async () => {
    show({ block: { type: "paragraph", content: [{ type: "text", text: "web", marks: [{ type: "link", attrs: { href: "https://example.cz" } }] }] } });
    const canvas = vi.fn();
    wrapper!.element.parentElement!.addEventListener("pointerdown", canvas);
    await vi.waitFor(() => expect(wrapper!.find("a").exists()).toBe(true));
    wrapper!.get("a").element.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(canvas).not.toHaveBeenCalled();
    wrapper!.element.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(canvas).toHaveBeenCalledOnce();
  });
});

describe("BoardBlock editing", () => {
  it("edits the block in a small page editor with only this block, and commits the changed node on Esc", async () => {
    show({ block: contact, editing: true });
    const editor = await liveEditor();
    expect(editor.state.doc.firstChild?.type.name).toBe("contactCard");
    const changed = { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"], phones: ["+420 777 123 456"] }] };
    editor.commands.command(({ tr }) => { tr.setNodeMarkup(0, undefined, { data: changed }); return true; });
    await flushPromises();
    escape();
    expect(wrapper!.emitted("commit")).toEqual([[{ type: "contactCard", attrs: { data: changed } }]]);
  });

  it("keeps a contact added in the editor („Přidat kontakt“) in the block, which is the item's only one", async () => {
    show({ block: contact, editing: true });
    const editor = await liveEditor();
    // As a person sees it: the toolbar shown with the contact's own form opened by the editing; a popover of a hidden toolbar closes at once.
    const add = await vi.waitFor(() => {
      const button = document.querySelector<HTMLButtonElement>('.md-context-toolbar:not([hidden]) button[aria-label="Přidat kontakt"]');
      expect(button).not.toBeNull();
      expect(document.querySelector<HTMLInputElement>(".md-module-form-popover [name=name]")?.value).toBe("Jana Nováková");
      return button!;
    });
    add.click();
    const name = await vi.waitFor(() => { const input = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=name]'); expect(input?.value).toBe(""); return input!; });
    name.value = "Eva Malá"; name.form!.dispatchEvent(new Event("submit", { cancelable: true }));
    await vi.waitFor(() => expect((editor.state.doc.firstChild?.attrs.data as { contacts: unknown[] }).contacts).toHaveLength(2));
    expect(document.querySelector(".md-module-form-popover")).toBeNull();
    escape();
    const [[committed, others]] = wrapper!.emitted("commit") as [BoardBlockNode, BoardBlockNode[]?][];
    expect(committed).toMatchObject({ type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková" }, { name: "Eva Malá" }] } } });
    expect(others).toBeUndefined();
  });

  it("cancels when nothing changed, and removes the block deleted in the editor", async () => {
    show({ block: contact, editing: true });
    await liveEditor();
    escape();
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
    wrapper!.unmount();

    show({ block: contact, editing: true });
    const editor = await liveEditor();
    editor.commands.command(({ tr }) => { tr.delete(0, tr.doc.firstChild!.nodeSize); return true; });
    await flushPromises();
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    expect(wrapper!.emitted("remove")).toHaveLength(1);
  });

  it("stores an edited inline component back as the bare node", async () => {
    show({ block: issue, editing: true });
    const editor = await liveEditor();
    let position = -1;
    editor.state.doc.descendants((node, at) => { if (node.type.name === "youtrackIssue") position = at; });
    editor.commands.command(({ tr }) => { tr.setNodeMarkup(position, undefined, { ...issue.attrs, state: "Done" }); return true; });
    await flushPromises();
    escape();
    expect(wrapper!.emitted("commit")).toEqual([[{ type: "youtrackIssue", attrs: { ...issue.attrs, state: "Done" } }]]);
  });

  it("inserts a module with the plugin's own insertion and commits what it made, or cancels when it made nothing", async () => {
    const modules = useEditorBlockModules();
    show({ insert: modules.find(module => module.id === "diagrams.insert"), editing: true });
    const editor = await liveEditor();
    await vi.waitFor(() => expect(editor.state.doc.firstChild?.type.name).toBe("diagram"));
    escape();
    expect(wrapper!.emitted("commit")?.[0]?.[0]).toMatchObject({ type: "diagram", attrs: { data: { version: 1 } } });
    wrapper!.unmount();

    const search = vi.fn(async () => []);
    show({ insert: modules.find(module => module.id === "youtrack.search"), editing: true, host: { youTrack: { search } } });
    await liveEditor();
    escape();
    expect(wrapper!.emitted("cancel")).toHaveLength(1);
  });
});

// U3-10: „Požadavek z katalogu“ inserts by the catalog's own panel, which needs the editor, and a pick brings what it requires.
it("inserts by a module's own panel and ends with every block the panel inserted", async () => {
  const requirementOf = (key: string, title: string) => ({ type: "requirement", attrs: { key }, content: [{ type: "paragraph", content: [{ type: "text", text: title }] }] });
  let opened: Editor | undefined;
  const module = { id: "requirements.catalog", label: "Požadavek z katalogu", icon: "book-open", keywords: [], open: (editor: Editor) => { opened = editor; } };
  show({ insert: module, editing: true });
  await vi.waitFor(() => expect(opened).toBeDefined());
  opened!.commands.insertContent([requirementOf("portal", "Portál"), requirementOf("dns", "Doména")]);
  await flushPromises();
  const [[first, others]] = wrapper!.emitted("commit") as [[BoardBlockNode, BoardBlockNode[]]];
  expect(first).toMatchObject({ type: "requirement", attrs: { key: "portal" } });
  expect(others).toEqual([expect.objectContaining({ type: "requirement", attrs: expect.objectContaining({ key: "dns" }) })]);
});
