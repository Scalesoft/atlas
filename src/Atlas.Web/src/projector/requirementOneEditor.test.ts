import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import type { Editor } from "@tiptap/core";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import { createDocumentWidgets } from "../documentWidgets";
import { useModules } from "../composables/useModules";
import type { ProjectorHost } from "./requirementsPlugin";
import { defaultView, setViewPrefs } from "./requirementViewMode";

/*
 * The page is ONE editor, `editable=false` for readers. These tests drive a read only editor
 * (no static render) through the behaviour the plan lists; what needs the core or a real browser is `it.todo`, numbered by the plan's
 * section, and is the work list of phase 2.
 */

type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const uuid = (n: number) => `6f1c1d3e-8a4b-4c55-9d1e-${String(n).padStart(12, "0")}`;
const p = (text: string): Json => ({ type: "paragraph", content: [{ type: "text", text }] });
const block = (n: number, title: string, attrs: Record<string, unknown> = {}, ...rest: Json[]): Json =>
  ({ type: "requirement", attrs: { id: uuid(n), key: `k${n}`, ...attrs }, content: [p(title), ...rest] });
const overview: Json = { type: "overview", attrs: { id: "5d1b8a3e-2c47-4e9f-8a60-7b3c9d2e1f40", kind: "requirements" } };
const page = (...content: Json[]) => ({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 2 }], document: { type: "doc", content } }) as MarkdownRichValue;

const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => "page-1", isCatalog: () => false, open: vi.fn(),
  api: { catalog: async () => ({ definitions: [], warnings: [] }), definitions: async () => ({ definitions: [], warnings: [] }), setCatalogPage: async (pageId: string, isCatalog: boolean) => ({ pageId, isCatalog }),
    createDeployment: async () => ({ id: "x", title: "x" }) } };
let wrapper: VueWrapper | undefined;
let editor!: Editor;
const probe: MarkdownPlugin = { id: "one-editor-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };

/** The page as a reader gets it: the editor, not editable. */
async function read(value: MarkdownRichValue) {
  const model = ref(value);
  wrapper = mount(defineComponent({ setup: () => () => h(MarkdownEditor, { profile: "rich", modelValue: model.value, ariaLabel: "Stránka", readonly: true,
    plugins: [...createDocumentWidgets({ kind: "edit", projector: host, canWrite: () => false }), probe] } as never) }), { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(".ProseMirror")).toBeTruthy());
}
/** A change as a colleague's provider delivers it. */
const remote = (change: (tr: ReturnType<Editor["state"]["tr"]["setNodeAttribute"]>) => void) => {
  const tr = editor.state.tr;
  change(tr);
  editor.view.dispatch(tr.setMeta(ySyncPluginKey, { isChangeOrigin: true }).setMeta("addToHistory", false));
};
const posOf = (n: number) => { let found = -1; editor.state.doc.descendants((node, pos) => { if (node.attrs.id === uuid(n)) found = pos; }); return found; };
const key = (target: Element, name: string) => target.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true }));

beforeAll(async () => {
  vi.stubGlobal("fetch", vi.fn(async () => Response.json({ modules: [{ id: "projector", version: 1 }, { id: "boards", version: 1 }] })));
  await useModules().start();
});
afterAll(() => { useModules().stop(); vi.unstubAllGlobals(); });
afterEach(() => { wrapper?.unmount(); wrapper = undefined; editor = undefined as never; document.body.replaceChildren(); setViewPrefs("page-1", defaultView); });

describe("a read only editor draws the blocks (plan §2.1, §8.1)", () => {
  it("is not editable: Enter, Backspace and Delete in a title change nothing (plan §2.3)", async () => {
    await read(page(block(1, "SSL"), block(2, "VPN")));
    const before = JSON.stringify(editor.getJSON());
    for (const name of ["Enter", "Backspace", "Delete", "Tab"]) key(editor.view.dom, name);
    expect(JSON.stringify(editor.getJSON())).toBe(before);
    expect(editor.isEditable).toBe(false);
  });

});

describe("a colleague's change re-filters and re-sorts a reader's page at once (plan §2.6, §4)", () => {
  it("recounts the overview card from the new document", async () => {
    // The number counts up with a timer-driven tween (RequirementSummaryCard `shown`); reduced motion shows the real number at once, so the test does not depend on timers under load.
    document.body.setAttribute("data-atlas-reduced-motion", "");
    try {
      await read(page(overview, block(1, "A", { state: "planned" }), block(2, "B", { state: "planned" })));
      const percent = () => document.querySelector("[data-summary-percent]")?.textContent;
      await vi.waitFor(() => expect(percent()).toBe("0 %"));
      remote(tr => tr.setNodeAttribute(posOf(1), "state", "done"));
      await vi.waitFor(() => expect(percent()).toBe("50 %"));
    } finally { document.body.removeAttribute("data-atlas-reduced-motion"); }
  });
});

describe("Přehled as the overview NodeView (plan §4)", () => {
  it("is drawn by the editor for a reader, with no write action (menu, fill)", async () => {
    await read(page(overview, block(1, "A", { state: "planned", requires: "missing" })));
    await vi.waitFor(() => expect(document.querySelector("[data-summary-percent]")).not.toBeNull());
    expect(document.querySelector("[data-requirement-summary-menu], [data-overview-action='fill-prerequisites']")).toBeNull();
  });

  it("does no work over all blocks when only a note changes (no per-keystroke recompute)", async () => {
    await read(page(overview, block(1, "A", {}, p("poznámka")), block(2, "B"), block(3, "C")));
    await vi.waitFor(() => expect(document.querySelector("[data-summary-percent]")).not.toBeNull());
    const before = document.querySelector("[data-summary-percent]")!.closest("[data-overview-kind]")!.innerHTML;
    const note = posOf(1) + 1 + editor.state.doc.nodeAt(posOf(1))!.firstChild!.nodeSize + 2;
    editor.view.dispatch(editor.state.tr.insertText("x", note).setMeta(ySyncPluginKey, { isChangeOrigin: true }));
    expect(document.querySelector("[data-summary-percent]")!.closest("[data-overview-kind]")!.innerHTML).toBe(before);
  });
});

describe("phase 2, to be written with the core (plan §7)", () => {
  it.todo("§2.1 a press on a chip in the title never moves the caret (writer) and places none (reader)");
  it.todo("§2.1 a drag-select in a note does not open the detail");
  it.todo("§2.3 Enter at the end of a title adds a sibling, in an empty title leaves the list, Backspace in an empty title removes the block (one undo step each)");
  it.todo("§2.4 copy of a block selection carries slice, HTML and Markdown; paste regenerates ids and dedupes keys");
  it.todo("§2.5 undo of a checked block restores it in one step and closes an open detail");
  it.todo("§2.6 a remote change of a field that is being edited keeps the local draft and shows the hint on blur");
  it.todo("§2.6 a remote removal of the block under an open detail closes it with a toast");
  it.todo("§2.6 losing the right turns an open detail into the read only one without resizing it");
  it.todo("§4 a press in the Přehled card never changes the editor selection; Esc returns to the previous caret — browser spec");
  it.todo("§4 every control of the card is reached by Tab in reading order; arrows on the slider change the level and announce it — browser spec");
  it.todo("§4 the level slider keeps the first visible row's top (±1 px) across all levels — browser spec");
  it.todo("§4 a keystroke in a title recomputes one block's entry, not the index (counting spy on indexRequirements)");
  it.todo("§5 a tap on the card zone opens the detail on touch; no hover-only affordance — browser spec");
  it.todo("§6 level switch and filter ≤100 ms at 1× and ≤300 ms at 4× on the largest page — browser spec");
});
