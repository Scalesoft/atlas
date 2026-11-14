import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import type { Editor } from "@tiptap/core";
import { NodeSelection } from "@tiptap/pm/state";
import { createMarkdownEditorController, loadMarkdownDialect, type MarkdownEditorController, type MarkdownPlugin } from "@atlas/markdown/core";
import { createMathPlugin } from "@atlas/markdown/plugins/math";
import type { BoardViewItem } from "../boards/boardSession";
import BoardCanvas from "../components/boards/BoardCanvas.vue";
import ReadonlySelectionActions from "../components/ai/ReadonlySelectionActions.vue";
import { aiIntentLabels, readingIntents } from "../assist/aiIntents";
import { loadEditorAssist } from "../assist/loadAssist";
import { projectReadonlySelection } from "../assist/readonlyProjection";
import type { AtlasAssistContext } from "../assist/types";
import { askAssistant } from "../assistant/assistantEntries";
import { startBoardAi } from "../assistant/boardAi";
import { useAssistant } from "../assistant/useAssistant";
import { hasBoardIntentions } from "../boards/boardIntentions";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import { noAiCapabilities, type AiCapabilities } from "../ai/capabilities";
import { runInteractionContract, type InteractionDescriptor, type Probe, type ProbeResult } from "./contractRunner";
import type { InteractionDeviation } from "./interaction";
import deviations from "./deviations/ai.json";

/*
 * The interaction contract of AI (area D, P11 in docs/interaction-rules.md; deviations in deviations/ai.json): one set of intents over
 * what one points at, by mode, hidden when AI cannot answer, and a change as a proposal of one undo step. Every surface runs its real
 * code: the page editor with the assist module as App.vue loads it (loadEditorAssist → atlasAssist → writing.ts) and its „Co s tím jde
 * dělat“ on Ctrl+Space; the read page's selection bar over the real projection; the real BoardCanvas with the board rows of
 * assistant/boardAi.ts. Replaced are only the AI transport: the capabilities the server answers (fetchAiCapabilities), the writing
 * endpoint (a stub that returns a changed text) and the hand-over to the assistant panel (askAssistant).
 */
const server = vi.hoisted(() => ({ capabilities: {} as Record<string, boolean> }));
vi.mock("../ai/capabilities", async original => { const actual = await original<typeof import("../ai/capabilities")>(); return { ...actual, fetchAiCapabilities: async () => actual.parseAiCapabilities(server.capabilities) }; });
vi.mock("../assistant/assistantEntries", async original => ({ ...await original<typeof import("../assistant/assistantEntries")>(), askAssistant: vi.fn(async () => true) }));
vi.mock("../composables/useToast", () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() }) }));
document.elementFromPoint ??= () => null;
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList; };
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect(); };

const everything: AiCapabilities = { ...noAiCapabilities(), assistant: true, editorTransform: true, editorOutline: true, editorIntent: true };
async function setAi(on: boolean) { server.capabilities = on ? { ...everything } : {}; await useAiCapabilities().refresh(); }

const reading = readingIntents.map(intent => aiIntentLabels[intent]);
/** What a changing intent shows in a menu (Přepsat: its ways, Přeložit, Doplnit). */
const writing = ["Zkrátit", "Přeložit do angličtiny", "Doplnit"];

interface AiDescriptor extends InteractionDescriptor {
  readonly mode: "reading" | "editing";
  /** Opens the surface with AI on or off and answers the rows it shows: AI rows (label + target) and the others. */
  readonly open: (ai: boolean) => Promise<{ ai: { label: string; target: string }[]; other: string[] }>;
  /** The intent labels the target must offer. */
  readonly expected: readonly string[];
  /** Words the AI rows' target must say („Sekce „Úvod““, „Stránka“). */
  readonly target?: string;
  /** Runs a changing intent through to its proposal. */
  readonly propose?: () => Promise<ProbeResult>;
}

// --- The page editor --------------------------------------------------------------------------------------------------------
const transform = vi.fn(async (request: { text: string }) => ({ text: request.text.replace(/odstavec/gu, "text"), provenance: { location: "local", provider: "openai", model: "stub" } }));
const controllers: MarkdownEditorController[] = [];
let editor!: Editor;
const t = (text: string) => ({ type: "text", text });
const defaultContent = () => [{ type: "heading", attrs: { level: 2 }, content: [t("Úvod")] }, { type: "paragraph", content: [t("První odstavec stránky.")] },
  { type: "mathBlock", attrs: { data: { version: 1, expression: "x^2" } } }];
/** A page longer than the one limit: one section of ordinary paragraphs. */
const longContent = () => [{ type: "heading", attrs: { level: 2 }, content: [t("Úvod")] }, ...Array.from({ length: 60 }, () => ({ type: "paragraph", content: [t("Odstavec dlouhé stránky o provozu a nasazení.")] }))];
async function openEditor(select: (editor: Editor) => void, content = defaultContent()) {
  const factory = await loadEditorAssist({ features: { editorAssist: false } }, { writingAllowed: true });
  const capabilities = () => useAiCapabilities().capabilities.value;
  // The context DocumentEditor.vue gives the adapter.
  const context: AtlasAssistContext = {
    pageId: () => "page-1",
    writing: { transformAllowed: () => capabilities().editorTransform, outlineAllowed: () => capabilities().editorOutline, intentAllowed: () => capabilities().editorIntent,
      title: () => "Příručka", onApplied: vi.fn(), api: { transform, outline: vi.fn(), intents: vi.fn(async () => ({ suggestions: [] })) } as never },
    ask: { available: () => useAssistant().canAnswer.value, run: (intent, text, target) => askAssistant(intent, text, target) },
    contacts: { search: async () => [], get: async () => null }, pages: { search: async () => [], resolve: async () => null } as never,
    resolveIssue: async () => null, hasSecrets: async () => false, pickSecret: async () => null, notify: vi.fn(), warn: vi.fn(), reportInteraction: vi.fn(),
  };
  const capture: MarkdownPlugin = { id: "capture", apiVersion: 1, create: () => ({ onCreate: created => { editor = created.editor; } }) };
  const plugins = [createMathPlugin(), factory!(context), capture];
  const document_ = { type: "doc", content };
  const controller = createMarkdownEditorController({ profile: "rich", plugins, source: { kind: "local", value: { format: "atlas-rich", version: 1, plugins: [{ id: "math", version: 1 }], document: document_ } } as never });
  controllers.push(controller);
  const host = document.createElement("div"); document.body.append(host); controller.attachView(host, { ariaLabel: "Stránka" });
  editor.view.focus(); select(editor); await settle();
  return controller;
}
const settle = async () => { for (let index = 0; index < 5; index++) { await Promise.resolve(); await new Promise(resolve => setTimeout(resolve, 0)); } await nextTick(); };
const at = (type: string, text?: string) => { let found = -1; editor.state.doc.descendants((node, pos) => { if (found < 0 && node.type.name === type && (text === undefined || node.textContent === text)) found = pos; return found < 0; }); return found; };
const visible = (element: Element | null) => !!element && element.isConnected && !(element as HTMLElement).closest("[hidden]");
/** The rows of the open „Co s tím jde dělat“, split into AI rows (their description starts with „AI“) and the others. */
function menuRows() {
  const rows = [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].filter(visible);
  const parts = rows.map(row => ({ label: row.querySelector(".t")?.textContent ?? "", target: row.querySelector(".d")?.textContent ?? "" }));
  return { ai: parts.filter(row => row.target.startsWith("AI")), other: parts.filter(row => !row.target.startsWith("AI")).map(row => row.label) };
}
async function editorMenu(select: (editor: Editor) => void, ai: boolean, content?: ReturnType<typeof defaultContent>) {
  await setAi(ai);
  await openEditor(select, content);
  editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: " ", code: "Space", ctrlKey: true, bubbles: true, cancelable: true }));
  await settle();
  await vi.waitFor(() => { if (!document.querySelector("[data-testid=intentions]")) throw new Error("no menu"); }, { timeout: 2000 });
  await settle();
  return menuRows();
}
/** Picks „Zkrátit“ for the target, then Použít, then Ctrl+Z: a proposal of one undo step. */
async function proposeIn(select: (editor: Editor) => void, target: string): Promise<ProbeResult> {
  await editorMenu(select, true);
  const row = [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].find(row => row.querySelector(".t")?.textContent === "Zkrátit" && row.querySelector(".d")?.textContent?.includes(target));
  if (!row) return `„Zkrátit“ for ${target} is not in the menu`;
  const before = editor.state.doc.toJSON();
  row.click();
  const buttons = () => [...document.querySelectorAll<HTMLButtonElement>(".md-assist-proposal button")].map(button => button.textContent);
  try { await vi.waitFor(() => { if (!buttons().includes("Použít")) throw new Error("no proposal"); }, { timeout: 2000 }); } catch { return `no proposal with Použít (${buttons().join(", ") || "nic"})`; }
  if (!buttons().includes("Zahodit")) return `the proposal offers ${buttons().join(" / ")}, not Použít / Zahodit`;
  if (JSON.stringify(editor.state.doc.toJSON()) !== JSON.stringify(before)) return "the document changed before Použít";
  [...document.querySelectorAll<HTMLButtonElement>(".md-assist-proposal button")].find(button => button.textContent === "Použít")!.click();
  try { await vi.waitFor(() => { if (!editor.state.doc.textContent.includes("text stránky")) throw new Error("not applied"); }, { timeout: 2000 }); } catch { return "Použít did not write the proposal"; }
  editor.view.dom.dispatchEvent(new KeyboardEvent("keydown", { key: "z", keyCode: 90, ctrlKey: true, bubbles: true, cancelable: true }));
  await settle();
  return JSON.stringify(editor.state.doc.toJSON()) === JSON.stringify(before) || `one Ctrl+Z did not return the text before (${editor.state.doc.textContent})`;
}
const inParagraph = (selection: boolean) => (target: Editor) => {
  const start = at("paragraph") + 1;
  if (selection) target.commands.setTextSelection({ from: start, to: start + "První odstavec".length });
  else target.commands.setTextSelection(start + 3);
};
const inHeading = (target: Editor) => { target.commands.setTextSelection(at("heading") + 2); };
const onMath = (target: Editor) => { target.view.dispatch(target.state.tr.setSelection(NodeSelection.create(target.state.doc, at("mathBlock")))); };

// --- The read page's selection bar ------------------------------------------------------------------------------------------
let bar: VueWrapper | undefined;
async function readingBar(ai: boolean, inside: "paragraph" | "heading" | "element") {
  await setAi(ai);
  const root = document.createElement("div"); root.className = "ProseMirror"; document.body.append(root);
  root.innerHTML = '<h2 data-heading-id="h1">Úvod</h2><p>První odstavec stránky.</p><div data-md-module="math" data-expression="x^2">x²</div><h2 data-heading-id="h2">Další</h2><p>Mimo.</p>';
  const range = document.createRange();
  if (inside === "element") range.selectNode(root.querySelector("[data-md-module]")!);
  else { const node = root.querySelector(inside === "heading" ? "h2" : "p")!.firstChild!; range.setStart(node, 0); range.setEnd(node, 3); }
  const projected = projectReadonlySelection(root, range)!;
  // App.vue: the bar is enabled while reading and its AI part follows `assistant.canAnswer`.
  bar = mount(ReadonlySelectionActions, { attachTo: document.body, props: { enabled: true, explainEnabled: useAssistant().canAnswer.value,
    selection: { ...projected, anchorRect: new DOMRect(0, 0, 10, 10), sectionHref: "#/pages/page-1?heading=h1" } } });
  await flushPromises();
  const buttons = [...document.querySelectorAll<HTMLElement>("[data-readonly-actions] button")];
  const offered = buttons.filter(button => button.matches("[data-readonly-ask], [data-readonly-explain], [data-readonly-summarize]"));
  return { ai: offered.map(button => ({ label: button.textContent!.trim(), target: button.title })), other: buttons.filter(button => !offered.includes(button)).map(button => button.textContent!.trim()) };
}

// --- The board ---------------------------------------------------------------------------------------------------------------
let board: VueWrapper | undefined;
const note = (text: string, id = "n", parent?: string): BoardViewItem => ({ id, kind: "note", x: 0, y: 0, z: 1, width: 240, height: 160, text, color: "yellow", ...(parent && { parent }) });
const firstNote = () => note("Schůzka s dodavatelem ve čtvrtek");
/** A frame „Rizika“ holding two notes: its AI target is the frame with its members (P11), not its title alone. */
const riskFrame = (): BoardViewItem[] => [{ id: "f", kind: "frame", x: 0, y: 0, z: 0, width: 600, height: 400, text: "Rizika" },
  { ...note("Termín dodávky se posouvá", "a", "f"), x: 20, y: 40 }, { ...note("Rozpočet je na hraně", "b", "f"), x: 300, y: 40 }];
/** Two notes joined by a connector, and a page card: a marquee or Ctrl+A takes the textless ones too (P11, P14). */
const mixedBoard = (): BoardViewItem[] => [note("Termín dodávky se posouvá", "a"), { ...note("Rozpočet je na hraně", "b"), x: 300 },
  { id: "c", kind: "connector", x: 0, y: 0, width: 8, height: 8, z: 0, from: "a", to: "b" },
  { id: "p", kind: "page", x: 600, y: 0, width: 280, height: 140, z: 1, targetResourceId: "page-1", target: { title: "Příručka", available: true } }];
/** A planning board of 30 notes: together over the one limit (1 600 characters). */
const longBoard = (): BoardViewItem[] => Array.from({ length: 30 }, (_, index) =>
  ({ ...note(`Poznámka ${index + 1} z plánovací porady o provozu a nasazení nové verze`, `n${index}`), x: (index % 6) * 260, y: Math.floor(index / 6) * 180 }));
const ctrlSpace = (target: Element) => target.dispatchEvent(new KeyboardEvent("keydown", { key: " ", code: "Space", keyCode: 32, ctrlKey: true, bubbles: true, cancelable: true }));
const menuOpen = () => { if (!document.querySelector("[data-testid=intentions]")) throw new Error("no menu"); };
/** Mounts the real BoardCanvas with `items`, the first selected by a click (D1) unless `select` is false; AI rows come from assistant/boardAi.ts. */
async function openBoard(ai: boolean, canEdit: boolean, items: BoardViewItem[] = [firstNote()], select = true) {
  await setAi(ai);
  startBoardAi(); await nextTick();
  vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => {})));
  board = mount(BoardCanvas, { attachTo: document.body, props: { items, label: "Plán", canEdit } });
  vi.spyOn(board.get("[data-board-surface]").element, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 1200, 800));
  const item = items[0]!;
  const element = () => (board!.element as HTMLElement).querySelector<HTMLElement>(`.vue-flow__node[data-id="${item.id}"]`);
  await vi.waitFor(() => expect(element()).not.toBeNull());
  await settle();
  if (select) { element()!.focus(); element()!.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true })); await settle(); }
  return { element: element()!, item };
}
/** Ctrl+Space on the selected first item or, with `whole`, on the board with nothing selected. */
async function boardMenu(ai: boolean, canEdit: boolean, items: BoardViewItem[] = [firstNote()], whole = false) {
  const { element, item } = await openBoard(ai, canEdit, items, !whole);
  const surface = board!.get("[data-board-surface]").element as HTMLElement;
  if (whole) surface.focus();
  ctrlSpace(whole ? surface : element);
  // A reader's note without AI has no rows: the menu then shows only its empty text. The board itself without rows opens no menu.
  try { await vi.waitFor(menuOpen, { timeout: whole && !ai ? 300 : 3000 }); } catch (error) { if (!whole) throw error; }
  await settle();
  return { ...menuRows(), registered: hasBoardIntentions({ items: whole ? [] : [item], board: items, canEdit, signal: new AbortController().signal }) };
}
/** Ctrl+A selects everything on the board, then Ctrl+Space opens the menu over that selection. */
async function boardAllMenu(ai: boolean, items: BoardViewItem[]) {
  await openBoard(ai, true, items, false);
  const surface = board!.get("[data-board-surface]").element as HTMLElement;
  surface.focus();
  surface.dispatchEvent(new KeyboardEvent("keydown", { key: "a", code: "KeyA", ctrlKey: true, bubbles: true, cancelable: true }));
  await settle();
  ctrlSpace(surface);
  await vi.waitFor(menuOpen, { timeout: 3000 });
  await settle();
  return menuRows();
}
/** Double-click edits the note (D1); Ctrl+Space over a selected word opens the text editor's own menu, as in a page. */
async function boardTextMenu(ai: boolean) {
  const { item } = await openBoard(ai, true);
  const node = () => (board!.element as HTMLElement).querySelector<HTMLElement>(`.vue-flow__node[data-id="${item.id}"]`)!;
  const field = () => node().querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror[contenteditable=true]");
  node().dispatchEvent(new MouseEvent("dblclick", { bubbles: true, cancelable: true, detail: 2 }));
  await vi.waitFor(() => expect(field()?.editor).toBeDefined(), { timeout: 10_000 }); // the text editor's chunk loads on first use
  await settle();
  field()!.editor!.commands.setTextSelection({ from: 1, to: 1 + "Schůzka".length });
  ctrlSpace(field()!);
  await vi.waitFor(menuOpen, { timeout: 3000 });
  await settle();
  return menuRows();
}

const descriptors: readonly AiDescriptor[] = [
  { id: "editor.selection", label: "Výběr textu (úpravy)", mode: "editing", saves: "apply", checks: ["P11.offered", "P11.hidden", "P11.proposal"],
    open: ai => editorMenu(inParagraph(true), ai), expected: [...reading, ...writing], propose: () => proposeIn(inParagraph(true), "AI"),
    touch: "Při 375 px s dotykem ověřit: výběr prstem ukáže lištu s „AI“, nabídka i návrh se vejdou na šířku." },
  { id: "editor.section", label: "Nadpis a jeho sekce (úpravy)", mode: "editing", saves: "apply", checks: ["P11.offered", "P11.hidden", "P11.proposal"],
    open: ai => editorMenu(inHeading, ai), expected: [...reading, ...writing], target: "Sekce „Úvod“", propose: () => proposeIn(inHeading, "Sekce „Úvod“"),
    touch: "Při 375 px s dotykem ověřit: kurzor v nadpisu a Ctrl+mezerník (nebo nabídka bloku) nabídne záměry nad sekcí." },
  { id: "editor.block", label: "Odstavec u kurzoru (úpravy)", mode: "editing", saves: "apply", checks: ["P11.offered", "P11.hidden", "P11.proposal"],
    open: ai => editorMenu(inParagraph(false), ai), expected: [...reading, ...writing], target: "Odstavec", propose: () => proposeIn(inParagraph(false), "Odstavec"),
    touch: "Při 375 px s dotykem ověřit: návrh odstavce se vejde na šířku a Použít / Zahodit jsou cíle aspoň 44 px." },
  // An element is opaque to a rewrite (its data is not prose): it gets the reading intents, with its own Markdown (a formula, a chart's data).
  { id: "editor.element", label: "Prvek (vzorec)", mode: "editing", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => editorMenu(onMath, ai), expected: reading, target: "Vzorec",
    touch: "Při 375 px s dotykem ověřit: klepnutí vybere prvek, nabídka akcí má Zeptat se / Vysvětlit / Shrnout." },
  { id: "editor.page", label: "Celá stránka (úpravy)", mode: "editing", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => editorMenu(inParagraph(false), ai), expected: reading, target: "Stránka",
    touch: "Při 375 px s dotykem ověřit: řádky nad stránkou jsou v nabídce akcí pod řádky bloku." },
  { id: "reading.selection", label: "Výběr textu (čtení)", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => readingBar(ai, "paragraph"), expected: reading,
    touch: "Při 375 px s dotykem ověřit: výběr prstem ukáže lištu nad výběrem, tlačítka se zalomí, nic nepřetéká." },
  { id: "reading.section", label: "Nadpis a jeho sekce (čtení)", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => readingBar(ai, "heading"), expected: reading, target: "sekci „Úvod“",
    touch: "Při 375 px s dotykem ověřit: výběr v nadpisu nabídne Shrnout sekci." },
  // Reading intents over an element of a read page (a formula, a chart): the read page is static HTML whose elements stay opaque.
  { id: "reading.element", label: "Prvek (čtení)", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => readingBar(ai, "element"), expected: reading,
    touch: "Při 375 px s dotykem ověřit: klepnutí na prvek při čtení nabídne Zeptat se / Vysvětlit / Shrnout." },
  // Over the one limit the text cannot be sent: the section and page intents vanish on a long page (the heading alone stays).
  { id: "editor.long", label: "Dlouhá sekce a stránka (úpravy)", mode: "editing", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => editorMenu(inHeading, ai, longContent()), expected: reading, target: "Sekce „Úvod“",
    touch: "Při 375 px s dotykem ověřit: na dlouhé stránce nabídka akcí v nadpisu nabídne záměry nad sekcí i stránkou." },
  { id: "editor.links", label: "Navrhnout odkazy a kontakty (úpravy)", mode: "editing", saves: "apply", checks: ["P11.offered", "P11.hidden"],
    open: ai => editorMenu(inParagraph(false), ai), expected: [aiIntentLabels.links],
    touch: "Při 375 px s dotykem ověřit: řádek Navrhnout odkazy a kontakty je v nabídce akcí bloku." },
  { id: "board.item", label: "Prvek tabule (úpravy)", mode: "editing", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardMenu(ai, true), expected: [...reading, ...writing], target: "Prvek tabule",
    touch: "Při 375 px s dotykem ověřit: dlouhý stisk nebo nabídka akcí na prvku má řádky AI." },
  { id: "board.reader", label: "Prvek tabule (čtení)", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardMenu(ai, false), expected: reading, target: "Prvek tabule",
    touch: "Při 375 px s dotykem ověřit: čtenář tabule má na prvku s textem jen Zeptat se / Vysvětlit / Shrnout." },
  // A frame or a group is what it holds: its title and its members' texts go to the assistant.
  { id: "board.frame", label: "Rám s prvky", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardMenu(ai, true, riskFrame()), expected: reading, target: "Prvek tabule",
    touch: "Při 375 px s dotykem ověřit: nabídka akcí na rámu nabídne Shrnout nad jeho obsahem." },
  // Nothing selected: the board itself (P11 „tabule“).
  { id: "board.whole", label: "Celá tabule", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardMenu(ai, true, riskFrame(), true), expected: reading, target: "Tabule",
    touch: "Při 375 px s dotykem ověřit: bez výběru nabídne nabídka akcí tabule Zeptat se / Vysvětlit / Shrnout nad celou tabulí." },
  // A selection with textless items (a connector, a card): they are skipped, the notes' text goes (as a page's opaque elements, P14).
  { id: "board.mixed", label: "Výběr s konektorem a kartou", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardAllMenu(ai, mixedBoard()), expected: reading, target: "prvky tabule",
    touch: "Při 375 px s dotykem ověřit: výběr tažením přes poznámky i šipku mezi nimi nabídne v nabídce akcí Zeptat se / Vysvětlit / Shrnout." },
  // The whole board over the one limit: as editor.long, the text cannot be sent (no silent cut).
  { id: "board.long", label: "Dlouhá tabule", mode: "reading", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardMenu(ai, true, longBoard(), true), expected: reading, target: "Tabule",
    touch: "Při 375 px s dotykem ověřit: na tabuli s mnoha poznámkami nabídne nabídka akcí bez výběru záměry nad celou tabulí." },
  // A board text being edited: its editor's own „Co s tím jde dělat“ over the selection, as in a page (P14).
  { id: "board.textEditing", label: "Text tabule při úpravě", mode: "editing", saves: "none", checks: ["P11.offered", "P11.hidden"],
    open: ai => boardTextMenu(ai), expected: [...reading, ...writing], target: "Prvek tabule",
    touch: "Při 375 px s dotykem ověřit: výběr prstem v upravovaném textu tabule nabídne v nabídce akcí řádky AI." },
];

const probes: Partial<Record<"P11.offered" | "P11.hidden" | "P11.proposal", Probe<AiDescriptor>>> = {
  "P11.offered": async d => {
    const { ai } = await d.open(true);
    const labels = ai.filter(row => !d.target || row.target.includes(d.target)).map(row => row.label);
    const missing = d.expected.filter(label => !labels.includes(label));
    if (missing.length) return `missing ${missing.join(", ")} (shown: ${ai.map(row => `${row.label} [${row.target}]`).join(", ") || "nic"})`;
    const changing = ai.filter(row => !reading.includes(row.label)).map(row => row.label);
    if (d.mode === "reading" && changing.length) return `reading offers intents that change content: ${changing.join(", ")}`;
    if (d.id === "board.frame") {
      [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].find(row => row.querySelector(".t")?.textContent === aiIntentLabels.summarize)?.click();
      await settle();
      const sent = String(vi.mocked(askAssistant).mock.calls.at(-1)?.[1] ?? "");
      if (!/Rizika[\s\S]*Termín dodávky[\s\S]*Rozpočet/u.test(sent)) return `Shrnout over the frame takes „${sent}“, not the frame with its notes`;
    }
    if (d.id === "reading.section") {
      document.querySelector<HTMLElement>("[data-readonly-summarize]")!.click();
      const sent = (bar!.emitted("summarize")?.[0]?.[0] as string | undefined) ?? "";
      if (!/Úvod[\s\S]*První odstavec/u.test(sent) || sent.includes("Mimo")) return `Shrnout takes „${sent}“, not the section`;
    }
    return true;
  },
  "P11.hidden": async d => {
    const { ai, other, ...rest } = await d.open(false) as Awaited<ReturnType<AiDescriptor["open"]>> & { registered?: boolean };
    if (ai.length) return `AI rows without AI: ${ai.map(row => row.label).join(", ")}`;
    if ("registered" in rest && rest.registered) return "without AI a board still has a module's rows (the right button would not keep the browser's menu)";
    if (d.id.startsWith("reading") && !other.length) return "the bar vanished with its non-AI actions";
    if ((d.id === "editor.element" || d.id === "board.item") && !other.length) return "the element's own actions vanished";
    return true;
  },
  "P11.proposal": async d => d.propose!(),
};

beforeAll(() => loadMarkdownDialect());
afterEach(async () => {
  controllers.splice(0).forEach(controller => controller.destroy());
  bar?.unmount(); bar = undefined; board?.unmount(); board = undefined;
  vi.unstubAllGlobals();
  document.body.replaceChildren();
  transform.mockClear(); vi.mocked(askAssistant).mockClear();
  await setAi(false);
});

describe("interaction contract: AI, the hand-over", () => {
  it("hands a reading intent of the editor to the assistant with the target's own Markdown (a formula's source)", async () => {
    await editorMenu(onMath, true);
    const row = [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].find(row => row.querySelector(".t")?.textContent === "Vysvětlit")!;
    row.click(); await settle();
    expect(askAssistant).toHaveBeenCalledWith("explain", expect.stringContaining("x^2"), "Vzorec");
  });
  it("offers the reading intents over a selection in a board text being edited (P11, P14), with that text", async () => {
    const { ai } = await boardTextMenu(true);
    expect(ai.map(row => row.label)).toEqual(expect.arrayContaining(reading));
    [...document.querySelectorAll<HTMLElement>("[data-testid=intentions] [role=option]")].find(row => row.querySelector(".t")?.textContent === aiIntentLabels.explain)!.click();
    await settle();
    expect(askAssistant).toHaveBeenCalledWith("explain", "Schůzka", "Prvek tabule");
  });
  it("keeps the browser's menu on a reader's board item the AI has no rows for (P2), and opens the board's on one it has", async () => {
    const rightClick = (element: HTMLElement) => { const event = new MouseEvent("contextmenu", { bubbles: true, cancelable: true }); element.dispatchEvent(event); return event.defaultPrevented; };
    const empty = await openBoard(true, false, [note("")]);
    expect(rightClick(empty.element)).toBe(false);
    board!.unmount(); board = undefined; document.body.replaceChildren();
    const text = await openBoard(true, false);
    expect(rightClick(text.element)).toBe(true);
  });
});

runInteractionContract({ surface: "AI", descriptors, deviations: deviations as InteractionDeviation[], probes, timeout: 60_000 });
