import { shallowRef, watch, type InjectionKey, type Ref, type ShallowRef } from "vue";
import type { Geometry } from "../../../boards/boardApi";
import type { BoardBlockNode, BoardColor, BoardPropsPatch, ObjectEdit } from "../../../boards/boardApi";
import type { BoardViewItem } from "../../../boards/boardSession";
import type { BoardBlockHost } from "../../../boards/editorBlocks";
import type { Highlight, LinkMark, NodeMark } from "../../../boards/boardNeighbours";
import type { EditPoint } from "../../../boards/boardCaret";

/** The tools of the rail (spec §3.1); P, K, I and C open a picker or menu instead of staying selected. */
export type BoardTool = "select" | "hand" | "note" | "text" | "shape" | "frame" | "connector";

/** What the canvas shares with its Vue Flow nodes and edges, which Vue Flow mounts and feeds only its own props. */
export interface BoardCanvasContext {
  flowId: string;
  canEdit: Readonly<Ref<boolean>>;
  tool: Readonly<Ref<BoardTool>>;
  /** The item whose text is edited in place. */
  editing: Readonly<Ref<string | undefined>>;
  /** Where the person aimed that editing (a double-click, a click into a selected card); none for Enter, F2 and a new item. */
  editPoint: Readonly<Ref<EditPoint | undefined>>;
  /** The item a writer presses into the text of while it is the one selected: its click is about to edit it (BoardText builds the editor). */
  entering: Readonly<Ref<string | undefined>>;
  /** Zoomed out below where a component can be read: one not drawn yet shows a light preview until the board is closer. */
  overview: Readonly<Ref<boolean>>;
  /** An item's height as the board stores it, not as measured on screen. */
  storedHeight(id: string): number | undefined;
  /** The one selected item when exactly one is: only it shows resize handles. */
  single: Readonly<Ref<string | undefined>>;
  /** Shift is held: a note's resize leaves its aspect ratio. */
  shift: Readonly<Ref<boolean>>;
  /** The hovered node's relations or the selected pair's path (boardNeighbours.ts); what it does not mark dims. */
  highlight: Readonly<Ref<Highlight | null>>;
  /** The edge under the pointer, which shows its popover. */
  hoveredEdge: Readonly<Ref<string | undefined>>;
  /** What a gesture would land in now: the frame or group a drag drops into, the item a connector (by pointer or keys) goes to. */
  dropTarget: Readonly<Ref<ReadonlySet<string>>>;
  /** `single`, `editing`, the highlighting, `dropTarget` and `hoveredEdge` as an item reads them for itself ({@link perItem}). */
  per: { single(id: string): boolean; editing(id: string): boolean; mark(id: string): NodeMark | undefined; linkMark(id: string): LinkMark | undefined; dropTarget(id: string): boolean; hoveredEdge(id: string): boolean };
  startEdit(id: string): void;
  /** Saves `text` of `id` as one `set` while its edit goes on (a pause in typing, the page hidden). */
  saveText(id: string, text: string): void;
  /** Saves `text` of `id` as one `set`, unless it did not change, and ends its edit. */
  commitText(id: string, text: string): void;
  /** Ends the edit of `id` without a change. */
  endEdit(id: string): void;
  /** Whether the editor of `id` holds typed text not yet saved. */
  unsaved(id: string, holding: boolean): void;
  resizing(id: string, geometry: Geometry): void;
  resized(id: string): void;
  open(item: BoardViewItem): void;
  /** Opens an object a row of a derived list (`links`) leads to: a page as a page, anything else by its host. */
  openTarget(id: string, typeId: string): void;
  /** The host services of the editor blocks' plugins in the board's space. */
  /** None: the consumers' own inert host. */
  blockHost?: BoardBlockHost;
  /** An edited editor block so far, while its editing goes on. */
  saveBlock(id: string, node: BoardBlockNode): void;
  /** An editor block's own editing ended with a changed node. */
  commitBlock(id: string, node: BoardBlockNode): void;
  remove(id: string): void;
  /** A card's object edited in place (a contact's fields), written by the object path: one step of the board's undo (P6). */
  editedObject(id: string, edit: Pick<ObjectEdit, "objectId" | "before" | "after">): void;
  /** A colour or props of one item (a page card's whole page instead of its section). */
  restyle(id: string, patch: { props: BoardPropsPatch }): void;
  /** The colour an item shows: its own, else a requirement card's by its state, a frame's neutral grey (boardTone in BoardCanvas). */
  toneOf(item: BoardViewItem): BoardColor | undefined;
  /** Where connectors' labels and popovers go: Vue Flow's layer over the edges, found once (none until the flow is mounted). */
  labelLayer: Readonly<Ref<HTMLElement | undefined>>;
  /** The cards a connector's elbow route goes around (boardRoute.ts), with their ids; frames and open groups are none. */
  obstacles: Readonly<Ref<readonly (Geometry & { id: string })[]>>;
}
export const boardCanvasKey: InjectionKey<BoardCanvasContext> = Symbol("board-canvas");

/**
 * A board-wide state read by each item for its own id (the one selected item, the edited one, the highlighting's marks, the drop
 * target, the connector under the pointer): an item depends on its own entry only, so a change wakes the items whose entry changed.
 * Comparing the whole state with its id, every node and connector (1,400 on a large board) was checked again on each hover, selection,
 * drag over a frame and edit. Made in the canvas's setup; an entry is made when an item first reads it.
 */
export function perItem<T>(source: () => ReadonlyMap<string, T> | undefined): (id: string) => T | undefined {
  const entries = new Map<string, ShallowRef<T | undefined>>();
  let current = source();
  watch(source, next => {
    const before = current;
    current = next;
    for (const id of before?.keys() ?? []) if (!next?.has(id)) { const entry = entries.get(id); if (entry) entry.value = undefined; }
    for (const [id, value] of next ?? []) { const entry = entries.get(id); if (entry) entry.value = value; }
  }, { flush: "sync" });
  return id => {
    let entry = entries.get(id);
    if (!entry) entries.set(id, (entry = shallowRef(current?.get(id))));
    return entry.value;
  };
}
/** One id as the entries of {@link perItem}. */
export const only = (id: string | undefined): ReadonlyMap<string, true> | undefined => (id ? new Map([[id, true]]) : undefined);
