import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { nextTick } from "vue";
import type { Editor } from "@tiptap/core";
import { boardItemKinds, type BoardChange, type BoardItemKind } from "../boards/boardApi";
import type { BoardViewItem } from "../boards/boardSession";
import BoardCanvas from "../components/boards/BoardCanvas.vue";
import { textEditorReady } from "../components/boards/text/textEditorChunk";
import { liveObjects } from "../objects/liveObjects";
import { inertBlockHost } from "../boards/editorBlocks";
import type { BoardLinksApi } from "../boards/boardLinks";
import { runInteractionContract, type InteractionDescriptor, type Probe } from "./contractRunner";
import type { InteractionCheck, InteractionDeviation } from "./interaction";
import boardDeviations from "./deviations/board.json";

/*
 * The interaction contract of the boards (area B, docs/interaction-rules.md; deviations in deviations/board.json): the real
 * BoardCanvas with the real Vue Flow, BoardTextEditor, BoardBlock and cards, one item per descriptor, driven by the gestures a
 * person makes (click, double-click, keys, a click outside). Replaced are only the toast (to see what it was asked to show), the
 * network and a contact's save (to see what it was asked to send).
 * A probe reads what the canvas emitted (`change` batches with their coalesce key, `undo`, `open*`) and drew, never a component's state.
 */
const toaster = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismissAll: vi.fn() }));
vi.mock("../composables/useToast", () => ({ useToast: () => toaster }));

type Flavour = "text" | "block" | "contact" | "card" | "scope";
interface BoardDescriptor extends InteractionDescriptor {
  /** The kind of board item it stands for: every kind of `boardItemKinds` needs a descriptor. */
  readonly kind: BoardItemKind;
  /** How the item is edited: a text in the page editor, an editor block, a directory contact's fields, its scope chosen in a
   * search list (a derived list), or not at all (a card). */
  readonly flavour: Flavour;
  /** The board with the probed item first. */
  readonly items: () => BoardViewItem[];
  /** Where a double-click goes, inside the item (its title); the item itself by default. */
  readonly grab?: string;
  /** What shows once the item is drawn (lazy cards and blocks). */
  readonly ready?: string;
}

const box = { x: 0, y: 0, z: 1 };
const note = (id: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "note", ...box, width: 240, height: 160, text: "Poznámka", color: "yellow", ...extra });
const object = (id: string, typeId: string, extra: Partial<BoardViewItem> = {}): BoardViewItem => ({ id, kind: "object", ...box, width: 340, height: 120, targetResourceId: `${id}-object`, props: { typeId, objectId: `${id}-object` }, ...extra });
const callout = { type: "callout", content: [{ type: "paragraph", content: [{ type: "text", text: "Pozor" }] }] };

const itemChecks: readonly InteractionCheck[] = ["P1.board-select", "P1.start", "P1.enter", "P2.menu", "P3.esc", "P3.esc-second", "P4.outside", "P6.undo", "P6.toast", "P7.focus", "P12.selected", "P17.sync"];
const cardChecks: readonly InteractionCheck[] = ["P1.board-select", "P1.enter", "P2.menu", "P3.esc-second", "P6.toast", "P12.selected"];
/** A derived list's reads answered at once: the space's people, nothing waiting, no uses. */
const linksApi: BoardLinksApi = {
  relations: { contents: async () => ({ items: [], summary: { types: {}, relations: { unused: 0, noIncoming: 0, broken: 0, attached: 0 } }, nextCursor: null }), relations: async () => null },
  usage: { usage: async () => null, count: async () => null },
  waiting: { waiting: async () => ({ parties: [] }), contacts: async () => new Map() },
};
const doubleTap = "Při 375 px s dotykem ověřit: tap vybere, dvojitý tap upraví, tah přesune vybraný prvek, klávesnice nezakryje upravovaný prvek.";

const descriptors: readonly BoardDescriptor[] = [
  { id: "note", label: "Poznámka", kind: "note", flavour: "text", saves: "live", touch: doubleTap, checks: itemChecks, items: () => [note("n")] },
  { id: "text", label: "Volný text", kind: "text", flavour: "text", saves: "live", touch: doubleTap, checks: itemChecks, items: () => [{ ...note("t"), kind: "text", text: "Volný text", height: 60, color: undefined }] },
  { id: "shape", label: "Tvar", kind: "shape", flavour: "text", saves: "live", touch: doubleTap, checks: itemChecks, items: () => [{ ...note("s"), kind: "shape", text: "Tvar", props: { shape: "rounded" } }] },
  { id: "frame-title", label: "Název rámu", kind: "frame", flavour: "text", saves: "live", touch: doubleTap, checks: itemChecks, grab: ".board-frame-title",
    items: () => [{ ...note("f"), kind: "frame", text: "Rám", width: 500, height: 320, z: 0, color: undefined }] },
  { id: "group-title", label: "Název skupiny", kind: "group", flavour: "text", saves: "live", touch: doubleTap, checks: itemChecks, grab: ".board-group-title",
    items: () => [{ ...note("g"), kind: "group", text: "Skupina", x: -16, y: -16, width: 272, height: 192, z: 0, color: undefined }, note("m", { parent: "g" })] },
  { id: "connector-label", label: "Popisek spojnice", kind: "connector", flavour: "text", saves: "live", touch: "Při 375 px s dotykem ověřit: tap na čáru vybere spojnici, dvojitý tap na popisku ho upraví.", checks: itemChecks,
    items: () => [{ ...note("c"), kind: "connector", text: "vede", width: 8, height: 8, z: 0, from: "a", to: "b", color: undefined }, note("a"), note("b", { x: 400 })] },
  { id: "block-callout", label: "Komponenta (informační box)", kind: "block", flavour: "block", saves: "live", touch: doubleTap, checks: itemChecks, ready: "[data-board-block]",
    items: () => [{ ...note("k"), kind: "block", width: 340, height: 100, text: undefined, color: undefined, props: { block: callout } }] },
  { id: "contact-card", label: "Karta kontaktu", kind: "object", flavour: "contact", saves: "draft", touch: "Při 375 px s dotykem ověřit: dvojitý tap zpřístupní pole karty, tap na pole ho otevře, klávesnice nezakryje kartu.", checks: itemChecks, ready: "[data-board-card=contact]",
    items: () => [object("o", "atlas.contact")] },
  { id: "page-card", label: "Karta stránky", kind: "page", flavour: "card", saves: "none", touch: "Při 375 px s dotykem ověřit: tap vybere, odkaz Otevřít se trefí prstem.", checks: [...cardChecks, "P1.start"], ready: "[data-board-card=page]",
    items: () => [{ ...note("p"), kind: "page", width: 280, height: 140, text: undefined, color: undefined, targetResourceId: "page-1", target: { title: "Příručka", available: true, canEdit: true } }] },
  { id: "board-card", label: "Karta tabule", kind: "object", flavour: "card", saves: "none", touch: "Při 375 px s dotykem ověřit: tap vybere, odkaz Otevřít se trefí prstem.", checks: cardChecks, ready: "[data-board-card=object]",
    items: () => [object("b", "atlas.board")] },
  { id: "product-card", label: "Karta produktu (nasazení)", kind: "page", flavour: "card", saves: "none", touch: "Při 375 px s dotykem ověřit: tap vybere, řádek modulu se trefí prstem a otevře stránku na nadpisu modulu.", checks: cardChecks, ready: "[data-page-preview]",
    items: () => [{ ...note("r"), kind: "page", width: 400, height: 300, text: undefined, color: undefined, targetResourceId: "page-2", props: { display: "product", anchor: "portal" } }] },
  // P15: a list derived from the relations; its editing is choosing its scope in a search list, which saves only on a pick.
  { id: "links-people", label: "Lidé projektu (seznam z vazeb)", kind: "links", flavour: "scope", saves: "none",
    touch: "Při 375 px s dotykem ověřit: tap vybere, dvojitý tap otevře výběr rozsahu nad seznamem, řádek člověka je vysoký 44 px a tap ho otevře, seznam se posouvá prstem uvnitř prvku.",
    checks: ["P1.board-select", "P1.start", "P1.enter", "P2.menu", "P3.esc-second", "P6.toast", "P7.focus", "P12.selected", "P17.sync"], ready: "[data-board-card=links]",
    items: () => [{ ...note("l"), kind: "links", width: 340, height: 320, text: undefined, color: undefined, props: { show: "people" } }] },
];

// --- The board under test ---------------------------------------------------------------------------------------------------
interface Rig { readonly wrapper: VueWrapper; readonly d: BoardDescriptor; readonly id: string }
let current: VueWrapper | undefined;
// ProseMirror takes (prevents) Enter and Esc by their key codes.
const keyCodes: Record<string, number> = { Enter: 13, Escape: 27, F2: 113, Delete: 46, z: 90, " ": 32 };
const press = (target: Element, key: string, init: KeyboardEventInit = {}) => { target.dispatchEvent(new KeyboardEvent("keydown", { key, keyCode: keyCodes[key], bubbles: true, cancelable: true, ...init })); };
const settle = async () => { await nextTick(); await flushPromises(); await nextTick(); };

async function openBoard(d: BoardDescriptor): Promise<Rig> {
  // Nothing here asks the network (a product card reads its page); a request that never answers keeps its card pending.
  vi.stubGlobal("fetch", vi.fn(() => new Promise<Response>(() => {})));
  if (d.flavour === "contact") {
    liveObjects.seed({ id: "o-object", typeId: "atlas.contact", version: 1, title: "Jana Nováková", canEdit: true, available: true, data: { name: "Jana Nováková", emails: ["jana@example.cz"] } });
    vi.spyOn(liveObjects, "edit").mockResolvedValue(undefined);
  }
  if (d.id === "board-card") liveObjects.seed({ id: "b-object", typeId: "atlas.board", version: 1, title: "Plán", canEdit: false, available: true, data: {} });
  const items = d.items();
  const blockHost = d.kind === "links" ? { ...inertBlockHost, links: { spaceId: () => "space-1", api: linksApi } } : undefined;
  current = mount(BoardCanvas, { attachTo: document.body, props: { items, label: "Plán", canEdit: true, ...(blockHost && { blockHost }) } });
  vi.spyOn(current.get("[data-board-surface]").element, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 1200, 800));
  const rig: Rig = { wrapper: current, d, id: items[0]!.id };
  await vi.waitFor(() => expect(itemEl(rig)).not.toBeNull());
  if (d.ready) await vi.waitFor(() => expect(itemEl(rig)!.querySelector(d.ready!)).not.toBeNull());
  await settle();
  return rig;
}
const canvas = (r: Rig) => r.wrapper.element as HTMLElement;
const itemEl = (r: Rig) => canvas(r).querySelector<HTMLElement>(`.vue-flow__${r.d.kind === "connector" ? "edge" : "node"}[data-id="${r.id}"]`);
/** A connector's label stands in a layer of its own, not inside its edge. */
const labelEl = (r: Rig) => canvas(r).querySelector<HTMLElement>(`[data-board-edge-label="${r.id}"]`);
/** Where editing happens: the item, for a connector its label. */
const scope = (r: Rig) => (r.d.kind === "connector" ? labelEl(r) : itemEl(r));
const owns = (r: Rig, element: Element | null) => !!element && (!!itemEl(r)?.contains(element) || !!labelEl(r)?.contains(element));
const changes = (r: Rig) => (r.wrapper.emitted("change") ?? []).map(([batch, coalesce]) => ({ batch: batch as BoardChange[], coalesce: coalesce as string | undefined }));
const sets = (r: Rig) => changes(r).flatMap(({ batch }) => batch).filter((change): change is Extract<BoardChange, { type: "set" }> => change.type === "set" && change.itemId === r.id);
const opened = (r: Rig) => ["openDetail", "openObject", "openPage"].filter(name => r.wrapper.emitted(name));
const isSelected = (r: Rig) => itemEl(r)?.classList.contains("selected") ?? false;

// A pointer focuses the item it clicks, as a browser does.
async function click(r: Rig) { const el = itemEl(r)!; el.focus(); el.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true })); await settle(); }
async function doubleClick(r: Rig) {
  const target = r.d.kind === "connector" ? labelEl(r)! : r.d.grab ? itemEl(r)!.querySelector(r.d.grab)! : itemEl(r)!;
  target.dispatchEvent(new MouseEvent("dblclick", { bubbles: true, cancelable: true, detail: 2 }));
}
const textOf = (value: unknown): string => (typeof value === "string" ? value : Array.isArray(value) ? value.map(textOf).join("") : value && typeof value === "object" ? Object.values(value).map(textOf).join("") : "");

// --- How each flavour is edited ---------------------------------------------------------------------------------------------
const proseMirror = (root: Element | null) => root?.querySelector<HTMLElement & { editor?: Editor }>(".ProseMirror[contenteditable=true]") ?? null;
const flavours: Record<Flavour, {
  /** The editing in place, started by the gesture (P1). */
  surface(r: Rig): Element | null;
  /** What Esc and a click outside close, where the person types; the same as the surface unless a card holds fields. */
  field(r: Rig): HTMLElement & { editor?: Editor } | null;
  /** Types into it, as a person does. */
  type(r: Rig): Promise<void>;
  /** What the board (the object, for a contact) holds of the edit; undefined while nothing was sent. */
  stored(r: Rig): string | undefined;
}> = {
  text: {
    surface: r => proseMirror(scope(r)),
    field: r => proseMirror(scope(r)),
    type: async r => { proseMirror(scope(r))!.editor!.chain().focus("end").insertContent(" úprava").run(); await flushPromises(); },
    stored: r => sets(r).map(change => change.text).filter(text => text !== undefined).at(-1),
  },
  block: {
    surface: r => proseMirror(scope(r)),
    field: r => proseMirror(scope(r)),
    // The caret is where the block put it (inside the callout): the document's end is the empty paragraph after it, which the board drops.
    type: async r => { proseMirror(scope(r))!.editor!.commands.insertContent(" úprava"); await flushPromises(); },
    stored: r => { const block = sets(r).map(change => change.props?.block).filter(Boolean).at(-1); return block ? textOf(block) : undefined; },
  },
  contact: {
    surface: r => scope(r)?.querySelector(".contact-card.is-editable") ?? null,
    field: r => proseMirror(scope(r)),
    type: async r => {
      scope(r)!.querySelector<HTMLElement>("[data-contact-field=name]")!.click();
      await vi.waitFor(() => expect(proseMirror(scope(r))?.editor).toBeDefined());
      proseMirror(scope(r))!.editor!.chain().focus("end").insertContent(" úprava").run();
      await flushPromises();
    },
    stored: () => { const call = vi.mocked(liveObjects.edit).mock.calls.at(-1); return call ? JSON.stringify(call[1]) : undefined; },
  },
  card: { surface: () => null, field: () => null, type: async () => {}, stored: () => undefined },
  // Typing in the search is P8's (its first Esc empties it, searchKeys): here the editing is left as it opened.
  scope: { surface: r => scope(r)?.querySelector("[data-board-links-scope]") ?? null, field: r => scope(r)?.querySelector<HTMLElement>("[data-board-links-scope] input") ?? null,
    type: async () => {}, stored: () => undefined },
};
const flavourOf = (r: Rig) => flavours[r.d.flavour];

/**
 * Starts the editing with a double-click, as a person on a board does, and waits for its surface. The item then moves the keyboard
 * into it a frame later (BoardNode `focusEditor`, into a contact card too); the keys a person presses next go where it landed.
 */
async function startEditing(r: Rig) {
  await click(r);
  await doubleClick(r);
  await vi.waitFor(() => expect(flavourOf(r).surface(r)).not.toBeNull());
  await settle();
  await vi.waitFor(() => expect(owns(r, document.activeElement) && document.activeElement !== itemEl(r)).toBe(true), { timeout: 500 }).catch(() => undefined);
}
/** What happened instead of an editing in place, for a probe's message. */
const instead = (r: Rig) => (opened(r).length ? `it opened (${opened(r).join(", ")}) instead of editing in place` : "nothing opened or started");

// --- The probes ---------------------------------------------------------------------------------------------------------------
const probes: Partial<Record<InteractionCheck, Probe<BoardDescriptor>>> = {
  "P1.board-select": async d => {
    const r = await openBoard(d);
    await click(r);
    if (!isSelected(r)) return "the first click did not select the item";
    if (flavourOf(r).surface(r)) return "the first click started editing";
    if (opened(r).length) return `the first click opened (${opened(r).join(", ")})`;
    return changes(r).length ? "the first click changed the board" : true;
  },

  "P1.start": async d => {
    const r = await openBoard(d);
    await click(r);
    await doubleClick(r);
    await settle();
    return flavourOf(r).surface(r) ? true : `a double-click: ${instead(r)}`;
  },

  "P1.enter": async d => {
    for (const key of ["Enter", "F2"]) {
      const r = await openBoard(d);
      await click(r);
      press(itemEl(r)!, key);
      await settle();
      if (opened(r).length) return `${key} opened (${opened(r).join(", ")}) instead of editing`;
      if (d.flavour !== "card" && !flavourOf(r).surface(r)) return `${key} on the selected item did not start editing`;
      current!.unmount();
      document.body.replaceChildren();
    }
    return true;
  },

  // The editor's own menu (its suggestion popup, as the editor contract recognises it), a menu or the command panel; P2 asks both gestures.
  "P2.menu": async d => {
    const menu = () => document.querySelector("[role=menu], [data-board-command-panel], .md-context-menu, .md-suggestion-popup");
    for (const gesture of ["the right button", "Ctrl+Space"]) {
      const r = await openBoard(d);
      await click(r);
      if (gesture === "Ctrl+Space") press(itemEl(r)!, " ", { ctrlKey: true, code: "Space" });
      else itemEl(r)!.dispatchEvent(new MouseEvent("contextmenu", { bubbles: true, cancelable: true, button: 2 }));
      await settle();
      if (!menu()) return `${gesture} on the selected item opens no actions menu`;
      if (!menu()!.querySelector("[aria-selected=true], [data-highlighted]")) return `the menu's first row is not preselected (${gesture})`;
      current!.unmount();
      document.body.replaceChildren();
    }
    return true;
  },

  "P3.esc": async d => {
    const r = await openBoard(d);
    await startEditing(r);
    await flavourOf(r).type(r);
    press(flavourOf(r).field(r)!, "Escape");
    await settle();
    if (flavourOf(r).field(r)) return "Esc did not close the editing";
    const stored = flavourOf(r).stored(r);
    if (d.saves === "live") return stored?.includes("úprava") ? true : "Esc closed the editing but the text typed was lost";
    return stored === undefined ? true : "Esc closed the field but kept what was typed";
  },

  "P3.esc-second": async d => {
    const r = await openBoard(d);
    if (d.flavour === "card") await click(r);
    else {
      await startEditing(r);
      for (let presses = 0; flavourOf(r).surface(r); presses++) {
        if (presses === 3) return "Esc does not close the editing";
        press(owns(r, document.activeElement) ? document.activeElement! : itemEl(r)!, "Escape");
        await settle();
      }
    }
    press(itemEl(r)!, "Escape");
    await settle();
    if (isSelected(r)) return "another Esc did not drop the selection";
    return document.activeElement === document.body ? "after Esc the keyboard is on the page body" : true;
  },

  "P4.outside": async d => {
    const r = await openBoard(d);
    await startEditing(r);
    await flavourOf(r).type(r);
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    await settle();
    if (flavourOf(r).field(r)) return "a click outside did not close the editing";
    return flavourOf(r).stored(r)?.includes("úprava") ? true : "a click outside lost what was typed";
  },

  "P6.undo": async d => {
    const r = await openBoard(d);
    await startEditing(r);
    await flavourOf(r).type(r);
    document.body.dispatchEvent(new PointerEvent("pointerdown", { bubbles: true }));
    await settle();
    const edits = changes(r);
    if (!edits.length) return "the edit is not on the board's undo stack (it goes to the object path), so Ctrl+Z asks the board for another step";
    if (new Set(edits.map(edit => edit.coalesce)).size > 1 || (edits.length > 1 && !edits[0]!.coalesce)) return "one edit made several undo steps";
    press(itemEl(r)!, "z", { ctrlKey: true });
    if (r.wrapper.emitted("undo")?.length !== 1) return "Ctrl+Z on the item did not ask for undo";
    // From the selection's context toolbar (Alt+F10) too, without going back to the item first.
    const toolbarButton = () => canvas(r).querySelector<HTMLElement>("[data-board-context-toolbar] button:not(:disabled)");
    // A contact card stays open for its fields after one closes: Esc closes the card first (P3).
    const open = flavourOf(r).surface(r);
    if (!toolbarButton() && open) { press(open, "Escape"); await settle(); }
    if (!toolbarButton()) await click(r);
    const button = toolbarButton();
    if (!button) return "the selected item shows no context toolbar";
    button.focus();
    press(button, "z", { ctrlKey: true });
    press(button, "y", { ctrlKey: true });
    if (r.wrapper.emitted("undo")?.length !== 2) return "Ctrl+Z in the selection's context toolbar did not ask for undo";
    return r.wrapper.emitted("redo")?.length === 1 ? true : "Ctrl+Y in the selection's context toolbar did not ask for redo";
  },

  "P6.toast": async d => {
    toaster.success.mockClear();
    const r = await openBoard(d);
    await click(r);
    press(itemEl(r)!, "Delete");
    await settle();
    if (!changes(r).some(({ batch }) => batch.some(change => change.type === "delete"))) return "Delete did not delete the item";
    const undo = (toaster.success.mock.calls.at(-1)?.[1] as { undo?: () => void } | undefined)?.undo;
    if (typeof undo !== "function") return "Delete showed no toast with „Zpět“";
    undo();
    return r.wrapper.emitted("undo")?.length === 1 ? true : "„Zpět“ on the toast did not ask the board to undo the deletion";
  },

  "P7.focus": async d => {
    const r = await openBoard(d);
    await startEditing(r);
    await flavourOf(r).type(r);
    press(flavourOf(r).field(r)!, "Escape");
    await settle();
    try {
      await vi.waitFor(() => expect(owns(r, document.activeElement)).toBe(true), { timeout: 400 });
      return true;
    } catch {
      return `after the editing the keyboard is on ${document.activeElement === document.body ? "the page body" : `<${document.activeElement?.tagName.toLowerCase()}> outside the item`}`;
    }
  },

  "P12.selected": async d => {
    const r = await openBoard(d);
    await click(r);
    const visual = d.kind === "connector" ? itemEl(r)?.querySelector(".board-edge") : itemEl(r)?.querySelector(".board-node");
    return visual?.classList.contains("is-selected") ? true : "the selected item has no is-selected class";
  },

  "P17.sync": async d => {
    const r = await openBoard(d);
    await click(r);
    await doubleClick(r);
    await nextTick();
    return flavourOf(r).surface(r) ? true : "the editing surface is not there when the gesture's render is done (it waits for a chunk or a request)";
  },
};

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  current?.unmount();
  current = undefined;
  document.body.replaceChildren();
  localStorage.removeItem("atlas.board.connectorStyle");
});

// The editor of a text is a chunk shared by every item: an editable board fetches and resolves it when it idles (textEditorChunk
// preheatTextEditor), so P17.sync measures the first edit of a board that idled once, with no edit before it.
beforeAll(async () => {
  await openBoard(descriptors[0]!);
  await vi.waitFor(() => expect(textEditorReady.value).toBe(true), { timeout: 45_000 });
  current?.unmount();
  current = undefined;
  document.body.replaceChildren();
}, 60_000);

describe("interaction contract: boards, registration", () => {
  it("has a descriptor for every kind of board item", () => {
    const covered = new Set(descriptors.map(descriptor => descriptor.kind));
    expect(boardItemKinds.filter(kind => !covered.has(kind))).toEqual([]);
  });
});

runInteractionContract({ surface: "boards", descriptors, deviations: boardDeviations as InteractionDeviation[], probes, timeout: 60_000 });
