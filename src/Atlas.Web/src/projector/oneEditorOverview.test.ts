import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, nextTick, ref } from "vue";
import type { Editor } from "@tiptap/core";
import type { MarkdownPlugin, MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { NodeSelection, Selection, TextSelection, type Plugin } from "@tiptap/pm/state";
import { ySyncPluginKey } from "@tiptap/y-tiptap";
import type { DecorationSet } from "@tiptap/pm/view";
import { atlasContentPlugins } from "../documentWidgets";
import { useModules } from "../composables/useModules";
import { isFolded, resetFoldStateForTests } from "../editor/foldState";
import { setOverviewLevel } from "../overview/overviewKinds";
import type { ProjectorHost } from "./requirementsPlugin";
import { playRequirementLevel, setViewPrefs } from "./requirementViewMode";
import * as overview from "./requirementOverview";

// The requirements overview in the one editor over requirement headings:
// a heading with a state, its section the blocks up to the next heading.

vi.mock("./requirementOverview", async importOriginal => {
  const original = await importOriginal<typeof import("./requirementOverview")>();
  return { ...original, outlineOfDoc: vi.fn(original.outlineOfDoc) };
});
const toaster = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() }));
vi.mock("../composables/useToast", () => ({ useToast: () => toaster }));


type Json = { type: string; attrs?: Record<string, unknown>; content?: Json[]; text?: string };
const p = (text = ""): Json => ({ type: "paragraph", ...(text ? { content: [{ type: "text", text }] } : {}) });
const plain = (level: number, id: string, text: string): Json => ({ type: "heading", attrs: { level, headingId: id }, content: [{ type: "text", text }] });
const req = (id: string, title: string, state = "planned", level = 3): Json => ({ type: "heading", attrs: { level, headingId: id, state }, content: [{ type: "text", text: title }] });
const summary: Json = { type: "overview", attrs: { id: "6f1c1d3e-8a4b-4c55-9d1e-000000000050", kind: "requirements" } };
const page = (...content: Json[]): MarkdownRichValue =>
  ({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 4 }, { id: "heading-anchors", version: 1 }], document: { type: "doc", content } } as MarkdownRichValue);

const api = { catalog: vi.fn(async () => ({ definitions: [], warnings: [] })), definitions: vi.fn(async () => ({ definitions: [], warnings: [] })) };
const host = { spaceId: () => "space-1", pageId: () => "page-1", parentId: () => "parent-1", pageTitle: () => "KBN", api, open: vi.fn(),
  unsaved: () => false, isCatalog: () => false, setCatalog: vi.fn() } as unknown as ProjectorHost;

let wrapper: VueWrapper | undefined;
let editor!: Editor;
const probe: MarkdownPlugin = { id: "overview-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };

const readonly = ref(false);
async function open(value: MarkdownRichValue, reading = false) {
  readonly.value = reading;
  const model = ref(value), plugins = [...atlasContentPlugins({ kind: "edit", projector: host }), probe];
  const Host = defineComponent({ setup: () => () => h(MarkdownEditor, {
    profile: "rich", modelValue: model.value, "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never,
    plugins, ariaLabel: "Stránka", readonly: readonly.value,
  }) });
  wrapper = mount(Host, { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector("[data-requirement-summary-view]")).toBeTruthy());
}

/** Where the top-level block at `index` of the document starts, and its element. */
const at = (index: number) => editor.state.doc.resolve(0).posAtIndex(index);
const block = (index: number) => editor.view.nodeDOM(at(index)) as HTMLElement;
/** Whether the node view holding `target` keeps a mousedown from ProseMirror (which would select the overview whole). */
function keepsPress(target: Element) {
  let node: Node | null = target;
  while (node && !(node as { pmViewDesc?: unknown }).pmViewDesc) node = node.parentNode;
  const desc = (node as unknown as { pmViewDesc: { stopEvent(event: Event): boolean } }).pmViewDesc;
  let kept = false;
  target.addEventListener("mousedown", event => { kept = desc.stopEvent(event); event.stopPropagation(); }, { once: true });
  target.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, cancelable: true, button: 0 }));
  return kept;
}
const viewState = () => (editor.state.plugins.find(plugin => (plugin as Plugin & { key: string }).key === "requirementViewMode$")!
  .getState(editor.state) as { layout: DecorationSet });

beforeAll(async () => {
  vi.stubGlobal("fetch", vi.fn(async () => new Response(JSON.stringify({ modules: [{ id: "projector", version: 1 }] }), { headers: { "Content-Type": "application/json" } })));
  await useModules().start();
});
afterAll(() => { useModules().stop(); vi.unstubAllGlobals(); });
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined; editor = undefined as never; document.body.replaceChildren(); localStorage.clear();
  setViewPrefs("page-1", { sort: "", state: "" }); setOverviewLevel("requirements", "page-1", "m"); resetFoldStateForTests(); vi.clearAllMocks();
});

// 0 summary · 1 MPI (H2) · 2 A blocked · 3 its note · 4 B · 5 its note · 6 Server (H2) · 7 C done · 8 konec
const deployment = () => page(summary, plain(2, "mpi", "MPI"), req("a", "Portál", "blocked"), p("čeká na smlouvu"), req("b", "Brána"), p("poznámka B"),
  plain(2, "server", "Server"), req("c", "Certifikát", "done"), p("konec"));
const card = () => document.querySelector<HTMLElement>("[data-requirement-summary-view]")!;

describe("the requirements overview in the one editor", () => {
  it("keeps every press inside its card from ProseMirror: an icon in a button or the card's background never puts the caret anywhere (§4.1)", async () => {
    await open(deployment());
    expect(keepsPress(card().querySelector("[data-summary-counts] [data-go]")!)).toBe(true);
    expect(keepsPress(card().querySelector("[data-summary-counts] [data-go] i")!)).toBe(true);
    expect(keepsPress(card().querySelector("[data-summary-percent]")!)).toBe(true);
  });

  it("filters in the document: every block of a section left out is hidden, and typing in a note maps the layout (§4.3)", async () => {
    await open(deployment());
    setViewPrefs("page-1", { state: "blocked" });
    await vi.waitFor(() => expect(block(4).classList).toContain("is-filtered-out"));
    expect([1, 2, 3, 4, 5, 6, 7, 8].map(index => block(index).classList.contains("is-filtered-out"))).toEqual([false, false, false, true, true, true, true, true]);
    // Mapped decorations keep their kind (the attributes they set); a layout walked again makes new ones.
    const kinds = () => viewState().layout.find().map(decoration => (decoration as unknown as { type: unknown }).type);
    const before = kinds();
    editor.commands.insertContentAt(at(4) - 1, " s KBN");
    expect(kinds()).toHaveLength(before.length);
    kinds().forEach((kind, index) => expect(kind).toBe(before[index]));
  });

  it("does not walk the page for its outline on a keystroke (§4.5)", async () => {
    await open(deployment());
    await nextTick();
    const walks = vi.mocked(overview.outlineOfDoc).mock.calls.length;
    editor.commands.insertContentAt(at(6) - 1, "x");
    editor.commands.insertContentAt(at(6) - 1, "y");
    expect(vi.mocked(overview.outlineOfDoc).mock.calls.length).toBe(walks);
  });

  it("sorts only in the card, as a list of links; the document keeps its order (owner 2026-11-04)", async () => {
    await open(deployment());
    const order = () => [...editor.view.dom.children].slice(1).map(child => child.textContent).join("|");
    const before = order();
    setViewPrefs("page-1", { sort: "state" });
    const list = await vi.waitFor(() => { const found = document.querySelector<HTMLElement>("[data-summary-sorted]"); expect(found).toBeTruthy(); return found!; });
    expect([...list.querySelectorAll(".summary-row-title")].map(row => row.textContent)).toEqual(["Portál", "Brána", "Certifikát"]);
    expect(order()).toBe(before);
    expect(editor.view.dom.hasAttribute("data-requirement-sorted")).toBe(false);
    // A row is a link: the caret goes to the end of its heading.
    list.querySelectorAll<HTMLElement>("[data-go]")[2]!.click();
    expect(editor.state.selection.head).toBe(at(8) - 1);
  });

  it("folds the requirement headings on S and opens them all on M, single folds reset; NONE hides them (model §2)", async () => {
    await open(deployment());
    const level = (id: string) => playRequirementLevel(editor.view.dom, () => setOverviewLevel("requirements", "page-1", id));
    level("s");
    expect(["a", "b", "c", "mpi"].map(id => isFolded(`page-1#${id}`))).toEqual([true, true, true, false]);
    level("m");
    expect(["a", "b", "c"].map(id => isFolded(`page-1#${id}`))).toEqual([false, false, false]);
    level("none");
    await nextTick();
    // The requirements go with their sections; the plain headings (MPI, Server) stay.
    expect([1, 2, 3, 6, 7, 8].map(index => block(index).classList.contains("is-filtered-out"))).toEqual([false, true, true, false, true, true]);
  });

  it("re-filters a reader's page on a colleague's change of a state, a burst in one relayout (§4.3)", async () => {
    await open(deployment(), true);
    setViewPrefs("page-1", { state: "planned" });
    await vi.waitFor(() => expect(block(2).classList).toContain("is-filtered-out"));
    const hidden = () => [2, 4, 7].filter(index => block(index).classList.contains("is-filtered-out"));
    expect(hidden()).toEqual([2, 7]);
    const remote = (index: number, state: string) => {
      const tr = editor.state.tr.setNodeAttribute(at(index), "state", state);
      editor.view.dispatch(tr.setMeta(ySyncPluginKey, { isChangeOrigin: true }).setMeta("addToHistory", false));
    };
    const layouts = vi.spyOn(editor.view, "dispatch");
    remote(4, "done");
    remote(7, "planned");
    // Gathered over a motion's length, then laid out once.
    await vi.waitFor(() => expect(hidden()).toEqual([2, 4]));
    expect(layouts.mock.calls.filter(([tr]) => tr.getMeta("requirementRelayout")).length).toBe(1);
  });
  it("keeps filtering and folding after the page turns read-only (the view's watchers outlive the component that made them)", async () => {
    await open(deployment());
    readonly.value = true;
    await vi.waitFor(() => expect(editor.isEditable).toBe(false));
    setViewPrefs("page-1", { state: "blocked" });
    await vi.waitFor(() => expect(block(4).classList).toContain("is-filtered-out"));
    setOverviewLevel("requirements", "page-1", "s");
    await vi.waitFor(() => expect(isFolded("page-1#a")).toBe(true));
  });

  it("keeps the summary menu's place on a writable page until the editor turns editable (UX open blink: the level switch stays put)", async () => {
    const writable = host as ProjectorHost & { writable?: () => boolean };
    writable.writable = () => true;
    try {
      await open(deployment(), true);
      expect(document.querySelector("[data-summary-menu]")).toBeNull();
      expect(document.querySelector("[data-summary-menu-place]")).not.toBeNull();
      readonly.value = false;
      await vi.waitFor(() => expect(document.querySelector("[data-summary-menu]")).not.toBeNull());
      expect(document.querySelector("[data-summary-menu-place]")).toBeNull();
    } finally { delete writable.writable; }
  });
  it("keeps no place for a menu on a page the reader cannot write", async () => {
    await open(deployment(), true);
    expect(document.querySelector("[data-summary-menu-place]")).toBeNull();
  });

  it("says how many hidden requirements a removed range took along, with Zpět only while nothing else changed (model §3)", async () => {
    // A dependency line without links elsewhere: the rule that drops its flag appends a transaction to the removal (the notice stays).
    await open(page(summary, plain(2, "mpi", "MPI"), req("a", "Portál", "blocked"), p("čeká na smlouvu"), req("b", "Brána"), p("poznámka B"),
      plain(2, "server", "Server"), req("c", "Certifikát", "blocked"), { ...p("bez odkazu"), attrs: { dependencies: true } }, p("konec")));
    setViewPrefs("page-1", { state: "blocked" });
    await vi.waitFor(() => expect(block(4).classList).toContain("is-filtered-out"));
    const before = editor.state.doc;
    // From A's heading over the hidden B to the end of its note.
    editor.commands.deleteRange({ from: at(2), to: at(6) });
    expect(toaster.success).toHaveBeenCalledWith("Odebráno 2 požadavky, z toho 1 skrytý.", expect.objectContaining({ undo: expect.any(Function) }));
    const undo = (toaster.success.mock.calls[0]![1] as { undo(): void }).undo;
    // A later edit: Zpět no longer undoes anything (it would take that edit instead).
    const edited = editor.state.doc.lastChild!;
    editor.commands.insertContentAt(editor.state.doc.content.size - 1, "!");
    const now = editor.state.doc;
    undo();
    expect(editor.state.doc).toBe(now);
    expect(edited).not.toBe(now.lastChild);
    editor.commands.undo();
    undo();
    expect(editor.state.doc.childCount).toBeLessThan(before.childCount);
  });

  it("tells a removal by the block keys that took more than the block in view (a heading's section), with Zpět, unfiltered too", async () => {
    await open(page(summary, plain(2, "mpi", "MPI"), req("a", "Portál"), p("pa"), req("b", "Brána"), plain(2, "server", "Server"), p("konec")));
    // A page opening on its Přehled never selects it: the next block key would remove the card unseen.
    expect(Selection.atStart(editor.state.doc)).toBeInstanceOf(TextSelection);
    editor.commands.setTextSelection(at(1) + 1);
    // The key itself: Tiptap's keyboardShortcut command copies only the steps of what the key dispatched, never its meta (the block count).
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "Backspace", ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true }));
    expect(editor.state.doc.childCount).toBe(3);
    expect(toaster.success).toHaveBeenCalledWith("Odebráno 4 bloky, z toho 2 požadavky.", expect.objectContaining({ undo: expect.any(Function) }));
  });

  it("lets an arrow select the Přehled no selection of ProseMirror's own lands on (keyboard-first)", async () => {
    await open(page(summary, p("Úvod")));
    editor.commands.setTextSelection(at(1) + 1);
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowLeft", bubbles: true, cancelable: true }));
    expect(editor.state.selection).toBeInstanceOf(NodeSelection);
    expect(editor.state.selection.from).toBe(0);
  });

  it("selects the Přehled by a click on its card, not on its controls, and never for a reader (as a table or an image)", async () => {
    await open(deployment());
    card().querySelector("[data-summary-percent]")!.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
    expect(editor.state.selection).toBeInstanceOf(NodeSelection);
    expect(editor.state.selection.from).toBe(at(0));
    editor.commands.setTextSelection(at(1) + 1);
    card().querySelector<HTMLElement>("[data-summary-counts] [data-go]")!.click();
    expect(editor.state.selection).not.toBeInstanceOf(NodeSelection);
    wrapper!.unmount(); wrapper = undefined;
    await open(deployment(), true);
    card().dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
    expect(editor.state.selection).not.toBeInstanceOf(NodeSelection);
  });

  it("selects the Přehled first by Delete at the end of the text above it and Backspace at the start of the text below; the next key removes it with Zpět", async () => {
    await open(page(p("Úvod"), summary, p("Dál")));
    const key = (name: string) => editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true }));
    editor.commands.setTextSelection(at(1) - 1);
    key("Delete");
    expect(editor.state.selection).toBeInstanceOf(NodeSelection);
    expect(editor.state.selection.from).toBe(at(1));
    editor.commands.setTextSelection(at(2) + 1);
    key("Backspace");
    expect(editor.state.selection.from).toBe(at(1));
    expect(editor.state.doc.childCount).toBe(3);
    key("Delete");
    expect(editor.state.doc.childCount).toBe(2);
    expect(toaster.success).toHaveBeenCalledWith("Odebrán přehled.", expect.objectContaining({ undo: expect.any(Function) }));
    // An empty line below the card goes with the Backspace; the card is selected, not removed.
    editor.commands.undo();
    editor.commands.insertContentAt(at(2), p());
    editor.commands.setTextSelection(at(2) + 1);
    key("Backspace");
    expect(editor.state.doc.childCount).toBe(3);
    expect(editor.state.selection).toBeInstanceOf(NodeSelection);
  });

  it("goes from the selected Přehled into its first control by Enter", async () => {
    await open(deployment());
    const count = editor.state.doc.childCount;
    editor.commands.setTextSelection(at(1) + 1);
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowLeft", bubbles: true, cancelable: true }));
    expect(editor.state.selection).toBeInstanceOf(NodeSelection);
    editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(card().querySelector("button:not([disabled]), [role=slider]"));
    expect(editor.state.doc.childCount).toBe(count);
  });

  it("shows a Přehled inside a range selected across it as selected; the range's removal takes it along and Ctrl+Z brings it back", async () => {
    await open(page(p("Úvod"), summary, p("Dál")));
    editor.commands.setTextSelection({ from: 2, to: at(2) + 2 });
    expect(block(1).hasAttribute("data-in-selection")).toBe(true);
    editor.commands.deleteSelection();
    expect(document.querySelector("[data-requirement-summary-view]")).toBeNull();
    editor.commands.undo();
    expect(editor.state.doc.child(1).type.name).toBe("overview");
  });

  it("removes the Přehled by „Odebrat přehled“ in its own menu, with Zpět", async () => {
    await open(page(p("Úvod"), summary, p("Dál")));
    card().querySelector<HTMLElement>("[data-summary-menu]")!.click();
    const remove = await vi.waitFor(() => { const found = document.querySelector<HTMLElement>("[data-summary-action=remove]"); expect(found).toBeTruthy(); return found!; });
    remove.click();
    expect(editor.state.doc.childCount).toBe(2);
    expect(toaster.success).toHaveBeenCalledWith("Odebrán přehled.", expect.objectContaining({ undo: expect.any(Function) }));
  });

  it("counts at NONE too, the requirement the selection ends in included (it is pinned only for the moment)", async () => {
    await open(page(summary, p("Úvod"), plain(2, "o40", "Oddíl 40"), req("a", "A"), p("pa"), req("b", "B"), p("pb"), plain(2, "o41", "Oddíl 41"), p("konec")));
    playRequirementLevel(editor.view.dom, () => setOverviewLevel("requirements", "page-1", "none"));
    await vi.waitFor(() => expect(block(3).classList).toContain("is-filtered-out"));
    editor.commands.setTextSelection({ from: at(2) + 1, to: at(7) - 1 });
    editor.commands.deleteSelection();
    expect(toaster.success).toHaveBeenCalledWith("Odebráno 2 požadavky, z toho 2 skryté.", expect.anything());
  });

  it("keeps Zpět working across a colleague's change in between (only the reader's own edits end it)", async () => {
    await open(deployment());
    setViewPrefs("page-1", { state: "blocked" });
    await vi.waitFor(() => expect(block(4).classList).toContain("is-filtered-out"));
    const count = editor.state.doc.childCount;
    editor.commands.deleteRange({ from: at(2), to: at(6) });
    const undo = (toaster.success.mock.calls.at(-1)![1] as { undo(): void }).undo;
    const tr = editor.state.tr.insertText("!", editor.state.doc.content.size - 1);
    editor.view.dispatch(tr.setMeta(ySyncPluginKey, { isChangeOrigin: true }).setMeta("addToHistory", false));
    undo();
    expect(editor.state.doc.childCount).toBe(count);
    expect(editor.state.doc.lastChild!.textContent).toBe("konec!");
  });

  it("says so too when the removal joins what is left into a hidden requirement above (the caret lands there, pinned)", async () => {
    await open(page(summary, p("Úvod"), plain(2, "o39", "Oddíl 39"), req("z", "Z"), p("pz"), plain(2, "o40", "Oddíl 40"), req("a", "A"), p("pa"), plain(2, "o41", "Oddíl 41"), p("konec")));
    playRequirementLevel(editor.view.dom, () => setOverviewLevel("requirements", "page-1", "none"));
    await vi.waitFor(() => expect(block(3).classList).toContain("is-filtered-out"));
    // From the end of Z's note (hidden) over Oddíl 40 and A into Oddíl 41: what is left joins Z's note.
    editor.commands.setTextSelection({ from: at(5) - 1, to: at(8) + 2 });
    editor.commands.deleteSelection();
    expect(toaster.success).toHaveBeenCalledWith("Odebráno 1 požadavek, z toho 1 skrytý.", expect.anything());
  });

  it("keeps the section the caret is in through a change that the filter would hide, and lets it go once the caret leaves (model §5)", async () => {
    await open(deployment());
    setViewPrefs("page-1", { state: "blocked" });
    await vi.waitFor(() => expect(block(4).classList).toContain("is-filtered-out"));
    editor.commands.setTextSelection(at(4) - 1);
    editor.commands.command(({ tr }) => { tr.setNodeAttribute(at(2), "state", "done"); return true; });
    await new Promise(resolve => requestAnimationFrame(resolve));
    await nextTick();
    // Still shown where it was, quietly marked as not fitting the filter.
    expect(block(2).classList).not.toContain("is-filtered-out");
    expect(block(2).classList).toContain("is-pinned");
    editor.commands.setTextSelection(1);
    await vi.waitFor(() => expect(block(2).classList).toContain("is-filtered-out"));
  });
  it("goes into the card by Tab from the text above it and by Shift+Tab from the text below, out below by Tab, never looping; Esc after a jump returns (§4.2)", async () => {
    await open(page(p("Úvod"), summary, plain(2, "mpi", "MPI"), req("a", "Portál", "blocked"), req("b", "Brána")));
    const keydown = (target: Element, key: string, shiftKey = false) => target.dispatchEvent(new KeyboardEvent("keydown", { key, shiftKey, bubbles: true, cancelable: true }));
    const controls = [...card().querySelectorAll<HTMLElement>("button:not([disabled]), [role=slider]")];
    // Tab at the end of the text above the card: its first control.
    editor.commands.setTextSelection(at(1) - 1);
    editor.view.focus();
    keydown(editor.view.dom, "Tab");
    expect(document.activeElement).toBe(controls[0]);
    // Shift+Tab at the start of the heading below it (a heading's own Tab keys come after): its last control.
    editor.commands.setTextSelection(at(2) + 1);
    editor.view.focus();
    keydown(editor.view.dom, "Tab", true);
    expect(document.activeElement).toBe(controls.at(-1));
    // Tab from the last control: the caret at the start of the text below; Tab there goes on, not back into the card.
    keydown(controls.at(-1)!, "Tab");
    expect(editor.view.hasFocus()).toBe(true);
    expect(editor.state.selection.head).toBe(at(2) + 1);
    keydown(editor.view.dom, "Tab");
    expect(card().contains(document.activeElement)).toBe(false);
    // Shift+Tab from the first control: the end of the text above; Esc from any control: the caret as it was.
    controls[0]!.focus();
    keydown(controls[0]!, "Tab", true);
    expect(editor.state.selection.head).toBe(at(1) - 1);
    controls[1]!.focus();
    keydown(controls[1]!, "Escape");
    expect(editor.view.hasFocus()).toBe(true);
    expect(editor.state.selection.head).toBe(at(1) - 1);
    // The sorted list is one Tab stop; the arrows walk it.
    setViewPrefs("page-1", { sort: "state" });
    const rows = await vi.waitFor(() => { const found = [...card().querySelectorAll<HTMLElement>("[data-summary-sorted] [data-go]")]; expect(found).toHaveLength(2); return found; });
    expect(rows.map(row => row.tabIndex)).toEqual([0, -1]);
    rows[0]!.focus();
    keydown(rows[0]!, "ArrowDown");
    expect(document.activeElement).toBe(rows[1]);
    // A jump from a row the legend opened, then Esc (before the editor's own Esc): back on that control.
    card().querySelector<HTMLElement>("[data-summary-counts] [data-go]")!.click();
    const legend = await vi.waitFor(() => { const row = card().querySelector<HTMLElement>("[data-summary-state-list] [data-go]"); expect(row).not.toBeNull(); return row!; });
    legend.focus();
    legend.click();
    // goToHeading focuses through tiptap's focus command, on the next frame.
    await vi.waitFor(() => expect(editor.view.hasFocus()).toBe(true));
    // Looking around first (an arrow) keeps the way back; typing would end it.
    keydown(editor.view.dom, "ArrowLeft");
    keydown(editor.view.dom, "Escape");
    expect(document.activeElement).toBe(legend);
  });

  it("folds the requirement holding the caret too: the slider is an explicit fold (model §3)", async () => {
    await open(deployment());
    editor.commands.setTextSelection(at(4) - 1);
    playRequirementLevel(editor.view.dom, () => setOverviewLevel("requirements", "page-1", "s"));
    await vi.waitFor(() => expect(["a", "b", "c"].map(id => isFolded(`page-1#${id}`))).toEqual([true, true, true]));
    // The caret stays at the end of the folded heading, as with Alt+↑, outside the hidden body.
    expect(editor.state.selection.from).toBe(at(4) - 1);
  });
  it.todo("gives a reader (editable=false) the heading's focus, not a caret, on a jump, and no control that writes (§4.1, §4.6)");
  it.todo("keeps the anchor block within 1 px on a level switch in the one editor root (§4.4)");
  it.todo("shows a board's overview skeleton as tall as its last card (§4.7)");
});
