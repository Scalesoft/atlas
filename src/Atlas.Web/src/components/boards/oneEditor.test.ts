import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { loadMarkdownDialect } from "@atlas/markdown/core";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, nextTick, provide, ref } from "vue";
import type { Editor } from "@tiptap/core";
import { EditorView } from "@tiptap/pm/view";
import type { EditPoint } from "../../boards/boardCaret";
import type { BoardBlockNode } from "../../boards/boardApi";
import { inertBlockHost } from "../../boards/editorBlocks";
import { drafts } from "../../lib/unsavedWork";
import BoardBlock from "./blocks/BoardBlock.vue";
import { boardCanvasKey, type BoardCanvasContext } from "./canvas/boardCanvasContext";
import BoardText from "./text/BoardText.vue";

/*
 * One editor on a board: a board's block is drawn by the
 * editor that edits it, read-only (editable=false) at rest; starting to edit flips that editor in place, nothing is swapped. A board's
 * text has no live editor until a writer enters it (owner, 4.0): its static render draws the same DOM until then.
 */

// The board loads the editor's `a-` dialect as it opens (BoardView); a block's editor writes its Markdown with it.
beforeAll(() => loadMarkdownDialect());
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); drafts.clear(); vi.restoreAllMocks(); });

type EditorElement = HTMLElement & { editor?: Editor };
/** The text's real editor (not the stand-in, which draws the same DOM without one). */
const editorOf = (root: Element) => root.querySelector<EditorElement>(".board-text-editor .ProseMirror");
/** What draws a block: the editor's root, at rest and while edited. */
const surfaceOf = (block: Element) => block.firstElementChild;

/** A note's text as BoardNode draws it, in a canvas context whose writability, edited item, aimed point and text the test switches. */
function note(initial: string, canEdit: boolean, selected = true) {
  const state = { canEdit: ref(canEdit), editing: ref<string>(), text: ref(initial), single: ref<string | undefined>(selected ? "n1" : undefined), editPoint: ref<EditPoint>() };
  const commit = vi.fn((markdown: string) => { state.text.value = markdown; });
  const ctx = {
    canEdit: state.canEdit, editing: state.editing, single: state.single, editPoint: state.editPoint, blockHost: { ...inertBlockHost, canWrite: () => state.canEdit.value },
    startEdit: (id: string) => { state.editing.value = id; }, endEdit: () => { state.editing.value = undefined; },
    unsaved: () => {}, saveText: () => {}, commitText: () => {},
  } as unknown as BoardCanvasContext;
  const Host = defineComponent({
    setup() {
      provide(boardCanvasKey, ctx);
      return () => h(BoardText, { id: "n1", editing: state.editing.value === "n1", binding: { read: () => state.text.value, commit }, text: state.text.value });
    },
  });
  wrapper = mount(Host, { attachTo: document.body });
  return { state, commit };
}
async function editorReady() {
  await vi.waitFor(() => expect(editorOf(document.body)?.editor).toBeDefined());
  await flushPromises();
  return editorOf(document.body)!;
}
/** ProseMirror instances on the page: a view (its DOM's `pmViewDesc`) or the editor the element carries. */
const instances = () => [...document.querySelectorAll<EditorElement & { pmViewDesc?: unknown }>(".ProseMirror")].filter(element => element.pmViewDesc || element.editor);

describe("one editor: a board text", () => {
  it("has no ProseMirror instance at rest or selected, and a click into it mounts the editor with the caret at the click", async () => {
    const { state } = note("Plán na pátek", true, false);
    await vi.waitFor(() => expect(document.querySelector("[data-board-markdown]")).not.toBeNull());
    expect(instances()).toHaveLength(0);
    state.single.value = "n1";
    // Longer than the idle wait that built an editor for a selected text before 4.0.
    await new Promise(resolve => setTimeout(resolve, 600));
    await flushPromises();
    expect(instances(), "selecting builds no editor").toHaveLength(0);
    // jsdom lays nothing out: the editor's view answers for the point clicked („Plán |na pátek“), as the browser's layout would.
    const at = { x: 40, y: 12 };
    vi.spyOn(EditorView.prototype, "posAtCoords").mockImplementation(coords => (coords.left === at.x && coords.top === at.y ? { pos: 6, inside: -1 } : null));
    state.editPoint.value = at;
    state.editing.value = "n1";
    const editor = await editorReady();
    await vi.waitFor(() => expect(editor.editor!.state.selection.from).toBe(6));
    expect(editor.editor!.state.selection.empty).toBe(true);
    expect(editor.getAttribute("contenteditable")).toBe("true");
    expect(document.querySelector("[data-board-markdown]"), "the stand-in left").toBeNull();
    expect(instances()).toHaveLength(1);
  });

  it("keeps a writer's editor after the editing, read-only, and the next editing flips that same editor in place", async () => {
    const { state, commit } = note("**Plán** na pátek", true);
    state.editing.value = "n1";
    const editor = await editorReady();
    editor.editor!.commands.insertContent(" B");
    editor.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", keyCode: 27, bubbles: true, cancelable: true }));
    expect(commit).toHaveBeenCalledWith("**Plán** na pátek B");
    state.editing.value = undefined;
    await nextTick();
    expect(editorOf(document.body)).toBe(editor);
    expect(editor.getAttribute("contenteditable")).toBe("false");
    state.editing.value = "n1";
    await vi.waitFor(() => expect(editor.getAttribute("contenteditable")).toBe("true"));
    expect(editorOf(document.body)).toBe(editor);
  });

  it("keeps only the last entered text's editor: entering another text gives the earlier one back to its static render", async () => {
    const editing = ref<string>();
    const ctx = { canEdit: ref(true), editing, single: ref<string>(), editPoint: ref<EditPoint>(), blockHost: inertBlockHost, endEdit: () => { editing.value = undefined; }, unsaved: () => {} } as unknown as BoardCanvasContext;
    const Host = defineComponent({
      setup() {
        provide(boardCanvasKey, ctx);
        return () => ["a", "b"].map(id => h("div", { id }, [h(BoardText, { id, editing: editing.value === id, binding: { read: () => `Text ${id}`, commit: () => {} }, text: `Text ${id}` })]));
      },
    });
    wrapper = mount(Host, { attachTo: document.body });
    const editorIn = (id: string) => document.getElementById(id)!.querySelector(".board-text-editor");
    editing.value = "a";
    await vi.waitFor(() => expect(editorIn("a")).not.toBeNull());
    editing.value = undefined;
    await nextTick();
    expect(editorIn("a"), "kept after its editing").not.toBeNull();
    editing.value = "b";
    await vi.waitFor(() => expect(editorIn("b")).not.toBeNull());
    expect(editorIn("a"), "the earlier editor went").toBeNull();
    expect(document.getElementById("a")!.querySelector("[data-board-markdown]")).not.toBeNull();
    expect(instances()).toHaveLength(1);
  });

  it("draws a reader's note with the static render only, its words selectable once it is selected", async () => {
    const { state } = note("Jen **ke čtení**", false, false);
    await vi.waitFor(() => expect(document.querySelector("[data-board-markdown]")).not.toBeNull());
    expect(document.querySelector("[data-board-markdown]")!.classList.contains("is-readable")).toBe(false);
    state.single.value = "n1";
    await nextTick();
    expect(document.querySelector("[data-board-markdown]")!.classList.contains("is-readable")).toBe(true);
    expect(instances()).toHaveLength(0);
  });

  it("takes a colleague's text into a note's static render, and into its editor once entered", async () => {
    const { state } = note("Stará verze", true);
    await vi.waitFor(() => expect(document.querySelector("[data-board-markdown]")?.textContent).toBe("Stará verze"));
    state.text.value = "Nová **verze**";
    await vi.waitFor(() => expect(document.querySelector("[data-board-markdown] strong")?.textContent).toBe("verze"));
    state.editing.value = "n1";
    const editor = await editorReady();
    state.editing.value = undefined;
    await nextTick();
    state.text.value = "Třetí verze";
    await vi.waitFor(() => expect(editor.textContent).toBe("Třetí verze"));
    expect(editorOf(document.body)).toBe(editor);
  });

  // D10: a lock or a lost right mid-edit sends nothing and loses nothing: the text waits as the draft for the next editing.
  it("keeps what was typed as the draft when the right to write goes mid-edit, and shows the stored text again", async () => {
    const { state, commit } = note("Plán", true);
    state.editing.value = "n1";
    const editor = await editorReady();
    await vi.waitFor(() => expect(editor.getAttribute("contenteditable")).toBe("true"));
    editor.editor!.commands.insertContentAt(editor.editor!.state.doc.content.size - 1, " B");
    state.canEdit.value = false;
    state.editing.value = undefined;
    await nextTick();
    await flushPromises();
    expect(commit).not.toHaveBeenCalled();
    expect(drafts.get("board-text:n1")).toBe("Plán B");
    expect(editor.textContent).toBe("Plán");
    expect(editor.getAttribute("contenteditable")).toBe("false");
    // The right back, the next editing starts from the draft.
    state.canEdit.value = true;
    state.editing.value = "n1";
    await vi.waitFor(() => expect(editor.textContent).toBe("Plán B"));
  });
});

describe("one editor: a board block", () => {
  const contact: BoardBlockNode = { type: "contactCard", attrs: { data: { version: 1, contacts: [{ name: "Jana Nováková", emails: ["jana@example.cz"] }] } } };

  it("draws a block at rest with its editor read-only, and editing flips that editor in place", async () => {
    wrapper = mount(BoardBlock, { props: { block: contact }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Jana Nováková"));
    const resting = surfaceOf(wrapper.element)!;
    expect(resting.querySelector(".ProseMirror")?.getAttribute("contenteditable")).toBe("false");
    await wrapper.setProps({ editing: true });
    await vi.waitFor(() => expect(wrapper!.find(".ProseMirror[contenteditable=true]").exists()).toBe(true));
    expect(surfaceOf(wrapper.element)).toBe(resting);
  });

  it("keeps a reader's block in its editor when the right to write comes (a lock opened), without a new element", async () => {
    wrapper = mount(BoardBlock, { props: { block: contact, readonly: true }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Jana Nováková"));
    const resting = surfaceOf(wrapper.element)!;
    expect(resting.closest(".md-renderer")).toBeNull();
    await wrapper.setProps({ readonly: false });
    await nextTick();
    await flushPromises();
    expect(surfaceOf(wrapper!.element)).toBe(resting);
  });

  it("writes nothing at rest: a block the editor normalizes as it loads is not sent back", async () => {
    wrapper = mount(BoardBlock, { props: { block: { type: "callout", content: [{ type: "paragraph", content: [{ type: "text", text: "Pozor" }] }] } }, attachTo: document.body });
    await vi.waitFor(() => expect(wrapper!.text()).toContain("Pozor"));
    await flushPromises();
    expect(wrapper.emitted("commit")).toBeUndefined();
  });
});

// What jsdom cannot show: the plan's browser checks (its chapter „Testy“), done in phase 2's passes in a real browser.
describe("one editor: in the browser", () => {
  it.todo("Enter, F2 or a double-click on a selected note puts the caret where the person aimed (double-click: that word; Enter/F2: the end) within 100 ms");
  it.todo("a second click into the text of the one selected note puts the caret there; a press that moves 4 px drags the note instead");
  it.todo("Esc ends the editing and leaves the focus on the card; Tab and the arrows still walk cards and connectors, the connector search finds them");
  it.todo("touch: a tap selects, a second tap into the text edits, „Vybírat více“ never starts an edit; a long press opens the actions");
  it.todo("a colleague moving, resizing or recolouring the note being typed in keeps the caret, the selection and the IME composition");
  it.todo("a 450-item board opens, pans and selects all within today's budgets (BD2-1, BD2-8, SB4-3) with its texts drawn by the static render");
  it.todo("a reader opens the deployment board read-only: same cards and texts, selectable and copyable, no handles, no caret, no rail");
});
