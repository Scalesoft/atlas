import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import type { Editor } from "@tiptap/core";
import { NodeSelection } from "@tiptap/pm/state";
import { createMarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownEditor } from "@atlas/markdown/vue";
import { createYouTrackPlugin } from "@atlas/markdown/plugins/youtrack";
import { createDocumentWidgets } from "../documentWidgets";
import type { ProjectorHost } from "../projector/requirementsPlugin";
import { runInteractionContract, type InteractionDescriptor, type ProbeResult } from "./contractRunner";
import deviations from "./deviations/editorHost.json";

/*
 * The interaction contract of the blocks only Atlas adds to the page editor (area A; the editor package's own blocks and chips are
 * in src/Atlas.Markdown/src/contracts): Atlas's real widgets (createDocumentWidgets) in a real editor. A node type Atlas adds that
 * has no descriptor here fails the registration test.
 */

const toaster = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() }));
vi.mock("../composables/useToast", () => ({ useToast: () => toaster }));
document.elementFromPoint ??= () => null;
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList; };
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect(); };

const host: ProjectorHost = { spaceId: () => "space-1", pageId: () => "page-1", open: vi.fn(), people: vi.fn(async () => []) as never,
  api: { catalog: vi.fn(), definitions: vi.fn(), setCatalogPage: vi.fn(), createDeployment: vi.fn(), waiting: vi.fn(async () => ({ parties: [] })) } as unknown as ProjectorHost["api"] };
const widgets = () => [...createDocumentWidgets({ kind: "edit", projector: host }), createYouTrackPlugin({ client: { search: async () => [] } })];

interface HostDescriptor extends InteractionDescriptor {
  readonly type: string;
  readonly node: object;
  /** Where a click starts the editing, the field, the value typed and the attribute it lands in. */
  readonly target: string;
  readonly field: string;
  readonly value: string;
  readonly read: (attrs: Record<string, unknown>) => unknown;
}

// The requirement block was the one Atlas block edited in place; a requirement is a heading now (its pickers are the heading's).
export const editorHostDescriptors: readonly HostDescriptor[] = [];

let wrapper: VueWrapper | undefined;
let editor!: Editor;
const probePlugin: MarkdownPlugin = { id: "contract-probe", apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor; } }) };
afterEach(() => { wrapper?.unmount(); wrapper = undefined; editor = undefined as never; document.body.replaceChildren(); vi.clearAllMocks(); });

interface Mounted { outside: HTMLButtonElement; position: () => number; dom: () => HTMLElement; value: () => unknown }
async function open(descriptor: HostDescriptor): Promise<Mounted> {
  const model = ref<MarkdownRichValue>({ format: "atlas-rich", version: 1, plugins: [{ id: "atlas-requirements", version: 1 }],
    document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Před" }] }, descriptor.node, { type: "paragraph", content: [{ type: "text", text: "Po" }] }] } } as MarkdownRichValue);
  const plugins = [...widgets(), probePlugin];
  wrapper = mount(defineComponent({ setup: () => () => h(MarkdownEditor, { profile: "rich", modelValue: model.value,
    "onUpdate:modelValue": ((next: MarkdownRichValue) => { model.value = next; }) as never, plugins, ariaLabel: "Stránka" }) }), { attachTo: document.body });
  await vi.waitFor(() => expect(editor && !editor.isDestroyed && document.querySelector(descriptor.target)).toBeTruthy(), { timeout: 15_000 });
  await settle();
  const outside = document.createElement("button"); outside.textContent = "Jinde"; document.body.append(outside);
  const position = () => { let found = -1; editor.state.doc.descendants((node, pos) => { if (found < 0 && node.type.name === descriptor.type) found = pos; return found < 0; }); return found; };
  return { outside, position, dom: () => editor.view.nodeDOM(position()) as HTMLElement, value: () => descriptor.read(editor.state.doc.nodeAt(position())!.attrs) };
}

const settle = async () => { for (let index = 0; index < 5; index++) { await Promise.resolve(); await new Promise(resolve => setTimeout(resolve, 0)); } };
const visible = (element: Element | null) => !!element && element.isConnected && !(element as HTMLElement).closest("[hidden]");
const editing = () => [...document.querySelectorAll(".md-context-popover, .md-suggestion-popup")].some(visible);
async function until(condition: () => boolean, timeout = 800) {
  try { await vi.waitFor(() => { if (!condition()) throw new Error("not yet"); }, { timeout, interval: 20 }); return true; } catch { return false; }
}
const state = (mounted: Mounted) => {
  const active = document.activeElement as HTMLElement | null;
  return `otevřeno: ${[...document.querySelectorAll(".md-context-popover, .md-suggestion-popup")].filter(visible).map(element => element.getAttribute("aria-label") ?? element.className).join(",") || "nic"}; fokus: ${active === document.body ? "body" : active === editor.view.dom ? "editor" : mounted.dom().contains(active) ? "v prvku" : active?.tagName}; výběr: ${editor.state.selection.constructor.name}`;
};
function key(target: Element, name: string, init: KeyboardEventInit = {}) {
  const down = new KeyboardEvent("keydown", { key: name, bubbles: true, cancelable: true, ...init });
  target.dispatchEvent(down);
  // A browser submits a form on a plain Enter in its text field (implicit submission); jsdom does not.
  if (name === "Enter" && !down.defaultPrevented && !init.ctrlKey && !init.metaKey && target instanceof HTMLInputElement && target.form) target.form.requestSubmit();
}
const keyboard = () => document.activeElement && document.activeElement !== document.body ? document.activeElement : editor.view.dom;
function click(target: Element) {
  const init = { bubbles: true, cancelable: true, button: 0 };
  target.dispatchEvent(new PointerEvent("pointerdown", init)); target.dispatchEvent(new MouseEvent("mousedown", init));
  if (target instanceof HTMLElement && target.matches("button, input, a[href], [tabindex]")) target.focus();
  target.dispatchEvent(new PointerEvent("pointerup", init)); target.dispatchEvent(new MouseEvent("mouseup", init)); target.dispatchEvent(new MouseEvent("click", init));
}
const select = (mounted: Mounted) => { editor.view.focus(); editor.commands.setNodeSelection(mounted.position()); };
const selected = (mounted: Mounted) => editor.state.selection instanceof NodeSelection && editor.state.selection.from === mounted.position();
async function start(descriptor: HostDescriptor, mounted: Mounted) { click(mounted.dom().querySelector(descriptor.target)!); return until(editing); }
async function edit(descriptor: HostDescriptor, mounted: Mounted): Promise<ProbeResult> {
  if (!await start(descriptor, mounted)) return `klik úpravu neotevřel (${state(mounted)})`;
  const field = [...document.querySelectorAll<HTMLInputElement>(descriptor.field)].find(visible);
  if (!field) return `pole ${descriptor.field} se neukázalo (${state(mounted)})`;
  field.focus(); field.value = descriptor.value; field.dispatchEvent(new Event("input", { bubbles: true }));
  await settle();
  return true;
}

let current: { descriptor: HostDescriptor; mounted: Mounted } | undefined;
const withEditor = (probe: (descriptor: HostDescriptor, mounted: Mounted) => Promise<ProbeResult>) => async (descriptor: HostDescriptor) => {
  const mounted = await open(descriptor); current = { descriptor, mounted };
  return probe(descriptor, mounted);
};

runInteractionContract<HostDescriptor>({ surface: "page editor, Atlas blocks", descriptors: editorHostDescriptors, deviations, timeout: 60_000, probes: {
  "P1.start": withEditor(async (descriptor, mounted) => await start(descriptor, mounted) || `klik úpravu neotevřel (${state(mounted)})`),
  "P17.sync": withEditor(async (descriptor, mounted) => {
    click(mounted.dom().querySelector(descriptor.target)!);
    return editing() ? true : await until(editing) ? "úprava se ukázala až po čekání" : "úprava se neotevřela";
  }),
  "P1.enter": withEditor(async (_descriptor, mounted) => {
    const missing: string[] = [];
    for (const name of ["Enter", "F2"]) {
      select(mounted); await settle();
      key(editor.view.dom, name);
      if (!await until(editing)) missing.push(`${name}: ${state(mounted)}`);
      key(keyboard(), "Escape"); await settle();
    }
    return missing.length ? `neotevře úpravu – ${missing.join(" | ")}` : true;
  }),
  "P2.menu": withEditor(async (_descriptor, mounted) => {
    const menu = () => [...document.querySelectorAll(".md-suggestion-popup, [role=menu], [role=listbox]")].find(visible);
    // Every entry point of the one menu (P2), as the board probe asks: Ctrl+Space, Shift+F10 and the right button on the element.
    const gestures: [string, () => void][] = [
      ["Ctrl+mezerník", () => key(editor.view.dom, " ", { code: "Space", ctrlKey: true })],
      ["Shift+F10", () => key(editor.view.dom, "F10", { shiftKey: true })],
      ["pravé tlačítko", () => mounted.dom().dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2 }))],
    ];
    for (const [name, open] of gestures) {
      select(mounted); await settle();
      open();
      if (!await until(() => !!menu()?.querySelector("[role=option], [role=menuitem]"))) return `${name} nabídku akcí neotevřel (${state(mounted)})`;
      if (!menu()!.querySelector("[aria-selected=true]")) return `${name}: první řádek není předvybraný`;
      key(keyboard(), "Escape");
      if (!await until(() => !menu())) return `${name}: Esc nabídku nezavřel`;
    }
    return true;
  }),
  "P3.esc": withEditor(async (descriptor, mounted) => {
    const before = mounted.value(), result = await edit(descriptor, mounted);
    if (result !== true) return result;
    key(keyboard(), "Escape");
    if (!await until(() => !editing())) return `Esc pole nezavřel (${state(mounted)})`;
    return mounted.value() === before || `Esc koncept nezahodil (${JSON.stringify(mounted.value())})`;
  }),
  "P4.outside": withEditor(async (descriptor, mounted) => {
    const before = mounted.value(), result = await edit(descriptor, mounted);
    if (result !== true) return result;
    click(mounted.outside); await settle();
    return await until(() => mounted.value() !== before) || `klik mimo hodnotu neuložil (${state(mounted)})`;
  }),
  "P4.blur": withEditor(async (descriptor, mounted) => {
    const before = mounted.value(), result = await edit(descriptor, mounted);
    if (result !== true) return result;
    mounted.outside.focus(); await settle();
    return await until(() => mounted.value() !== before) || `odchod fokusu hodnotu neuložil (${state(mounted)})`;
  }),
  "P6.undo": withEditor(async (descriptor, mounted) => {
    const before = mounted.value(), result = await edit(descriptor, mounted);
    if (result !== true) return result;
    key(keyboard(), "Enter");
    if (!await until(() => mounted.value() !== before && !editing())) return `Enter hodnotu neuložil a pole nezavřel (${state(mounted)})`;
    key(keyboard(), "z", { ctrlKey: true });
    return await until(() => mounted.value() === before) || `Ctrl+Z (${state(mounted)}) změnu nevrátil`;
  }),
  "P7.focus": withEditor(async (descriptor, mounted) => {
    const result = await edit(descriptor, mounted);
    if (result !== true) return result;
    key(keyboard(), "Enter");
    await until(() => !editing()); await settle();
    const active = document.activeElement;
    if (!active || active === document.body) return "klávesnice skončila na <body>";
    if (mounted.dom().contains(active) || selected(mounted)) return true;
    return `klávesnice není na upraveném prvku (${state(mounted)})`;
  }),
  "P12.selected": withEditor(async (_descriptor, mounted) => {
    select(mounted);
    const marked = () => [mounted.dom(), ...mounted.dom().querySelectorAll("*")].some(element => element.classList.contains("md-component-selected") || element.classList.contains("ProseMirror-selectednode"));
    return await until(marked) || `vybraný blok nemá sdílenou třídu (třídy: ${mounted.dom().className})`;
  }),
} });

describe("interaction contract: page editor, Atlas blocks: registration", () => {
  it("has a descriptor for every node type Atlas adds to the editor package's", () => {
    const nodes = (plugins: MarkdownPlugin[]) => {
      const controller = createMarkdownEditorController({ profile: "rich", plugins, source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph" }] } } as never } });
      const element = document.createElement("div"); document.body.append(element); controller.attachView(element, { ariaLabel: "Editor" });
      let names: string[] = [];
      const probe: MarkdownPlugin = { id: "schema-probe", apiVersion: 1, create: () => ({ onCreate: context => { names = Object.keys(context.editor.schema.nodes); } }) };
      controller.destroy();
      const again = createMarkdownEditorController({ profile: "rich", plugins: [...plugins, probe], source: { kind: "local", value: { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph" }] } } as never } });
      again.attachView(element, { ariaLabel: "Editor" }); again.destroy();
      return names;
    };
    const atlas = nodes(widgets()), editorOnly = new Set(nodes([createYouTrackPlugin({ client: { search: async () => [] } })]));
    // Atlas's widgets wrap the editor package's plugins; what is left is Atlas's own (the editor package's are in its own contract).
    const packageNodes = new Set(["contactCard", "contactInline", "mathBlock", "mathInline", "tableOfContents", "chart", "diagram", "attachment", "openapi", "secretReference", "secretInlineReference", "concealedContent", "pageReference", "emoji"]);
    const own = atlas.filter(name => !editorOnly.has(name) && !packageNodes.has(name));
    const covered = new Set(editorHostDescriptors.map(descriptor => descriptor.type));
    // An overview is generated from the page's components and not edited in place (overview/overviewNode.ts); requirementSummary is its 3.3 name.
    const notEdited: Record<string, string> = { overview: "přehled se skládá z komponent stránky, sám se neupravuje", requirementSummary: "starý název přehledu požadavků",
      requirement: "starý blok požadavku: jen uložené dokumenty a historie, server ho při otevření převede na nadpis" };
    expect(own.filter(name => !covered.has(name) && !(name in notEdited)), "add a descriptor to editorHostDescriptors").toEqual([]);
    void current;
  });
});
