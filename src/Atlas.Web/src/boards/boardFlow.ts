import type { Edge, Node } from "@vue-flow/core";
import type { BoardInsert } from "./boardBlockNodes";
import { COLLAPSED_GROUP, collapsedOwners, copiedParents, depthOf } from "./boardGroups";
import { boundsOf, geometryOf } from "./geometry";
import type { BoardBlockNode, BoardChange, BoardColor, BoardItemKind, BoardLinksShow, BoardProps, BoardPropsPatch, BoardSide, Geometry, NewBoardItem } from "./boardApi";
import type { BoardItem } from "./boardModel";
import type { BoardViewItem, PageTargetInfo } from "./boardSession";
import type { ItemMotion } from "./boardMotion";
import { cleanBoardText } from "./boardText";
import type { ConnectorStyle } from "./boardConnectorStyle";
import { parallelOffsets } from "./boardRoute";
import t from "../i18n/boards";

/**
 * The board canvas in controlled mode (interaction spec §1): Vue Flow nodes and edges are derived from the board model's
 * optimistic view plus the local state of my own gesture, and every finished gesture becomes one batch of changes here. Vue Flow
 * never owns the data. Pure functions, no Vue.
 */
export interface Point { x: number; y: number }
export interface FlowUi {
  selected: ReadonlySet<string>;
  /** Geometry under my own gesture (drag, resize, nudge) until the batch comes back as items. */
  local: ReadonlyMap<string, Geometry>;
  /** The item whose text is being edited in place. */
  editing?: string;
  /** What each item animates (boardMotion.ts, keyed so that the node replays it); a leaving item is drawn but takes no input. */
  motion?: ReadonlyMap<string, ItemMotion & { key: number }>;
  /** The connector under the pointer and the one with the keyboard focus: their ends can be dragged off like a selected one's. */
  hoveredEdge?: string;
  focusedEdge?: string;
}
/** `members`: how many items a collapsed group stands for. */
export interface BoardNodeData { item: BoardViewItem; motion?: ItemMotion & { key: number }; members?: number }
/** `merged`: the edge stands for this many connectors of hidden members of collapsed groups; `item` is the first of them. */
export interface BoardEdgeData {
  item: BoardViewItem; motion?: ItemMotion & { key: number }; route: NonNullable<BoardProps["route"]>; dash: NonNullable<BoardProps["dash"]>;
  arrowStart: NonNullable<BoardProps["arrowStart"]>; arrowEnd: NonNullable<BoardProps["arrowEnd"]>; merged?: number;
  animated?: boolean; thickness?: BoardProps["thickness"];
  /** How far it runs beside the other connectors between the same two items (boardRoute.ts parallelOffsets). */
  offset?: number;
}
export type BoardNode = Node<BoardNodeData>;
export type BoardEdge = Edge<BoardEdgeData>;

export const MIN_SIZE = 8, MAX_SIZE = 20_000, MAX_COORD = 1_000_000;
/** The smallest size a person resizes an item to (its handles, Alt with the arrows); a component that flows, its smallest width. */
export const MIN_RESIZE = 24, MIN_FLOW_WIDTH = 120;
/** A contact's card (an object card of the contact type, or the contact component) is unreadable below a name line and a role. */
export const MIN_CONTACT = { width: 160, height: 56 };
const CONNECTOR_BOX = { x: 0, y: 0, width: MIN_SIZE, height: MIN_SIZE };

/** The size a new item of a kind gets, in board units. A note is square (spec §3.1). */
export const itemSizes: Record<BoardItemKind, { width: number; height: number }> = {
  note: { width: 200, height: 200 }, text: { width: 240, height: 56 }, shape: { width: 160, height: 120 }, frame: { width: 640, height: 400 }, group: { width: 400, height: 300 },
  page: { width: 280, height: 140 }, block: { width: 320, height: 200 }, object: { width: 300, height: 168 }, connector: { width: MIN_SIZE, height: MIN_SIZE },
  links: { width: 340, height: 320 },
};
/** A block whose content is a line or two (a requirement: its title and facts) starts that small, not in a chart's box. */
const blockSizes: Partial<Record<string, { width: number; height: number }>> = {
  requirement: { width: 320, height: 88 }, chart: { width: 480, height: 320 }, diagram: { width: 420, height: 260 }, mathBlock: { width: 300, height: 120 },
  concealedContent: { width: 300, height: 150 }, callout: { width: 340, height: 96 }, codeBlock: { width: 440, height: 160 },
  table: { width: 520, height: 180 }, contactCard: { width: 340, height: 104 }, youtrackIssue: { width: 300, height: 104 },
};
/**
 * How an editor component lies on the board, without a card of its own (its item's colour is its background): `fill` draws into
 * the whole box and the person sizes it both ways; `flow` takes the box's width and is as tall as its content (the node's height is
 * measured, the stored one follows when the person resizes it). A component not named keeps the card (a requirement, an unknown one).
 */
export type BlockLayout = "fill" | "flow";
const blockLayouts: Partial<Record<string, BlockLayout>> = {
  chart: "fill", diagram: "fill", mathBlock: "fill", concealedContent: "fill",
  callout: "flow", codeBlock: "flow", table: "flow", contactCard: "flow", youtrackIssue: "flow",
};
/** The size a new editor component of a type gets on the board. */
export const blockSizeOf = (type: string | undefined) => (type ? blockSizes[type] : undefined) ?? itemSizes.block;
export const blockLayoutOf = (item: Pick<BoardItem, "kind" | "props">): BlockLayout | undefined =>
  item.kind === "block" ? blockLayouts[(item.props?.block as { type?: string } | undefined)?.type ?? ""] : undefined;
/** The smallest size an item is resized to: the general limits, a contact card's own. */
export function minSizeOf(item: Pick<BoardItem, "kind" | "props">): { width: number; height: number } {
  const contact = (item.kind === "object" && item.props?.typeId === "atlas.contact") || (item.kind === "block" && (item.props?.block as { type?: string } | undefined)?.type === "contactCard");
  return contact ? MIN_CONTACT : { width: blockLayoutOf(item) === "flow" ? MIN_FLOW_WIDTH : MIN_RESIZE, height: MIN_RESIZE };
}
const defaultColors: Partial<Record<BoardItemKind, BoardColor>> = { note: "yellow", shape: "blue", frame: "gray", group: "blue" };
/** Kinds whose text is one line: a connector's label and a group's name. */
export const isLineKind = (kind: BoardItemKind) => kind === "connector" || kind === "group";

/** The props an item of the kind has when it names none (spec §4; the migration gives connectors elbow + arrow at the end). */
export function defaultProps(kind: BoardItemKind): BoardProps {
  switch (kind) {
    case "note": return { fontSize: "auto" };
    case "text": return { fontSize: "m", align: "left" };
    case "shape": return { shape: "rect", fontSize: "m", align: "center" };
    case "page": return { display: "card" };
    case "connector": return { route: "elbow", arrowStart: "none", arrowEnd: "arrow", dash: "solid" };
    default: return {};
  }
}
/** An item's props over the defaults of its kind: an item from a server without props yet reads as its kind's defaults. */
export const propsOf = (item: BoardItem): BoardProps => ({ ...defaultProps(item.kind), ...item.props });

/** A page card's target; undefined for any other card and while the page is looked up. */
export const pageTargetOf = (item: BoardViewItem): PageTargetInfo | undefined =>
  item.kind === "page" && item.target && "title" in item.target ? item.target : undefined;

// The sections are held by reference: another language is filled into them in place when it arrives.
export const kindNames: Record<BoardItemKind, string> = t.flow.kinds;
/** What a derived list (`links`) is called by what it shows: the names of the component menu. */
export const linksNames: Readonly<Record<BoardLinksShow, string>> = t.flow.links;
/** What an editor block is, by its node (U3-11); a block of another plugin is a „Komponenta“. */
const blockNames: Readonly<Record<string, string>> = t.flow.blocks;
/** A component's name by its type (Tabulka, Graf…), „Komponenta“ for one the board has no name for. */
export const blockName = (type: string | undefined) => (type && blockNames[type]) || kindNames.block;
const plainText = (node: unknown): string => {
  if (!node || typeof node !== "object") return "";
  const { text, content } = node as { text?: unknown; content?: unknown };
  return (typeof text === "string" ? text : "") + (Array.isArray(content) ? content.map(plainText).join(" ") : "");
};
function blockLabel(block: BoardBlockNode | undefined) {
  const name = (block && blockNames[block.type]) || kindNames.block;
  const text = plainText(block).replace(/\s+/g, " ").trim().slice(0, 120);
  if (block?.type === "requirement") return `${name}: ${text || t.flow.untitled}`;
  return text && name !== kindNames.block ? `${name}: ${text}` : name;
}
/**
 * A board text's Markdown as a screen reader should say it (D5 c, BD2-9): link texts without their addresses, no emphasis marks,
 * no block markup at line starts, escapes resolved, one line.
 */
export function spokenText(markdown: string): string {
  return markdown
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/^\s*(?:>\s?)*(?:#{1,6}\s+|(?:[-+*]|\d+[.)])\s+(?:\[[ xX]\]\s+)?)?/gm, "")
    .replace(/(\*\*|__|~~|==)(?=\S)(.+?)(?<=\S)\1/g, "$2")
    .replace(/(^|[^\\*_])([*_])(?=\S)(.+?)(?<=\S)\2/g, "$1$3")
    .replace(/`([^`]*)`/g, "$1")
    .replace(/\\([\\`*_{}[\]()#+\-.!~=|>])/g, "$1")
    .replace(/\s+/g, " ")
    .trim();
}

/** The accessible name of an item (Czech, spec §3, accessibility). */
export function itemLabel(item: BoardViewItem): string {
  const kind = kindNames[item.kind];
  if (!kind) return t.flow.item;
  if (item.kind === "page") return item.target?.available === false ? t.flow.unavailablePage : `${kind}: ${pageTargetOf(item)?.title ?? t.flow.loading}`;
  if (item.kind === "block") return blockLabel(item.props?.block);
  if (item.kind === "object") return kind;
  if (item.kind === "links") return linksNames[item.props?.show ?? "people"] ?? kind;
  return `${kind}: ${spokenText(item.text ?? "") || t.flow.empty}`;
}

const shownOf = (item: BoardItem, ui: FlowUi) => ui.local.get(item.id) ?? geometryOf(item);
const isCollapsedGroup = (item: BoardItem) => item.kind === "group" && item.props?.collapsed === true;
/** Where an item is drawn: a collapsed group as its card. */
const drawnOf = (item: BoardItem, ui: FlowUi): Geometry => (isCollapsedGroup(item) ? { ...shownOf(item, ui), ...COLLAPSED_GROUP } : shownOf(item, ui));
const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));
/** Geometry as the server takes it: whole units, within ±1 000 000, sizes 8 to 20 000. */
export const fitGeometry = (g: Geometry): Geometry => ({
  x: clamp(Math.round(g.x), -MAX_COORD, MAX_COORD), y: clamp(Math.round(g.y), -MAX_COORD, MAX_COORD),
  width: clamp(Math.round(g.width), MIN_SIZE, MAX_SIZE), height: clamp(Math.round(g.height), MIN_SIZE, MAX_SIZE),
});
const sameGeometry = (a: Geometry, b: Geometry) => a.x === b.x && a.y === b.y && a.width === b.width && a.height === b.height;
const byZ = (a: BoardItem, b: BoardItem) => a.z - b.z || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
export { boundsOf };
export const topZ = (items: readonly BoardItem[]) => items.reduce((max, item) => Math.max(max, item.z), 0) + 1;

/** Frames and open groups lie below everything (spec §3.2), each container above the ones holding it; connectors lie between them
 * and the other items. A collapsed group is a card among the items. A frame or group under my gesture lies above the other ones, so
 * that it is never drawn under the frame it is dragged into (J1). */
const isUnderlay = (item: BoardItem) => item.kind === "frame" || (item.kind === "group" && !isCollapsedGroup(item));
function layers(items: readonly BoardViewItem[], local: ReadonlyMap<string, Geometry> = new Map()) {
  const byId = new Map(items.map(item => [item.id, item]));
  const cards = items.filter(item => item.kind !== "connector");
  const under = cards.filter(isUnderlay).sort((a, b) => Number(local.has(a.id)) - Number(local.has(b.id)) || depthOf(a, byId) - depthOf(b, byId) || byZ(a, b));
  return { under, ordered: [...under, ...cards.filter(item => !isUnderlay(item)).sort(byZ)] };
}

/**
 * The parts of a node that depend on its item and motion only, kept while both stay the same objects: Vue Flow hands them to the node
 * component, which then draws again only when they change (not every node on every frame of a drag).
 */
interface NodeParts { motion?: ItemMotion & { key: number }; members?: number; data: BoardNodeData; domAttributes: BoardNode["domAttributes"]; ariaLabel: string }
const nodeParts = new WeakMap<BoardViewItem, NodeParts>();
function partsOf(item: BoardViewItem, motion: (ItemMotion & { key: number }) | undefined, members: number | undefined): NodeParts {
  const known = nodeParts.get(item);
  if (known && known.motion === motion && known.members === members) return known;
  const parts: NodeParts = {
    motion, members, data: { item, ...(motion && { motion }), ...(members !== undefined && { members }) }, ariaLabel: itemLabel(item),
    domAttributes: { "data-board-item": item.id, "data-kind": item.kind } as BoardNode["domAttributes"],
  };
  nodeParts.set(item, parts);
  return parts;
}

export function toFlowNodes(items: readonly BoardViewItem[], ui: FlowUi): BoardNode[] {
  const { under, ordered } = layers(items, ui.local);
  const owners = collapsedOwners(items);
  const hidden = new Map<string, number>();
  for (const owner of owners.values()) hidden.set(owner, (hidden.get(owner) ?? 0) + 1);
  return ordered.map((item, index) => {
    const g = drawnOf(item, ui);
    const motion = ui.motion?.get(item.id);
    // A member of a collapsed group stays drawn, faded away, so that collapsing and expanding animate (BoardCanvas styles).
    const live = motion?.kind !== "leave" && !owners.has(item.id);
    const { data, domAttributes, ariaLabel } = partsOf(item, motion, isCollapsedGroup(item) ? hidden.get(item.id) ?? 0 : undefined);
    const underlay = index < under.length;
    return {
      // A component that flows is as tall as its content: Vue Flow measures it (connectors follow the measured box).
      id: item.id, type: "board", position: { x: g.x, y: g.y }, width: g.width, height: blockLayoutOf(item) === "flow" ? undefined : g.height,
      // Edges take the z of the underlay's count: above every frame and open group, below every other item.
      // What my gesture moves lies above the other cards while it runs (J1: a dragged card went under its neighbours).
      zIndex: underlay ? index : index + 1 + (ui.local.has(item.id) ? ordered.length : 0),
      selected: live && ui.selected.has(item.id),
      // Undefined: the canvas's own `nodes-draggable` and `nodes-connectable` decide (the right to write, the tool), so switching the right
      // (the lock) changes no node; only an item that cannot be dragged or connected now says so itself.
      draggable: live && ui.editing !== item.id ? undefined : false,
      connectable: live ? undefined : false,
      selectable: live,
      focusable: live,
      ariaLabel, domAttributes, data,
      // Only its title and border take the pointer: a drag inside a frame or group draws a marquee (its contents stay reachable).
      // Always named: Vue Flow keeps a node's class and style when a later node leaves them out (a group expanded again).
      class: owners.has(item.id) ? "is-collapsed-away" : underlay ? (item.kind === "frame" ? "board-frame-node" : "board-group-node") : "",
      style: underlay ? { pointerEvents: "none" } : {},
    };
  });
}

/**
 * The same value, or two plain objects (a position, a style, a node's data) with the same values. `sized`: the earlier one may also
 * carry the width and height Vue Flow writes into a node's own style object (NodeWrapper's getStyle), from the node's compared sizes.
 */
function sameValue(a: unknown, b: unknown, sized = false) {
  if (a === b) return true;
  if (!a || !b || typeof a !== "object" || typeof b !== "object" || Array.isArray(a) || Array.isArray(b)) return false;
  const before = a as Record<string, unknown>, now = b as Record<string, unknown>, keys = Object.keys(now);
  const extra = Object.keys(before).filter(key => !(key in now));
  return keys.every(key => before[key] === now[key]) && extra.every(key => sized && (key === "width" || key === "height"));
}
/**
 * The nodes (or edges) derived anew, each one that did not change the object it was before (COL2-1): Vue Flow assigns a node's
 * fields onto its graph node, and a new `position` or `style` object, even of the same values, drew every node again on every change
 * of the items (a colleague's move cost ≈ 350 ms on a board of 300 cards).
 */
export function reuseUnchanged<T extends { id: string }>(previous: readonly T[] | undefined, next: T[]): T[] {
  if (!previous?.length) return next;
  const before = new Map(previous.map(entry => [entry.id, entry]));
  return next.map(entry => {
    const old = before.get(entry.id);
    if (!old) return entry;
    const keys = Object.keys(entry) as (keyof T)[];
    return keys.length === Object.keys(old).length && keys.every(key => sameValue(old[key], entry[key], key === "style")) ? old : entry;
  });
}

/** The sides of two boxes that face each other, along the axis on which their centres lie further apart. */
export function autoSides(from: Geometry, to: Geometry): [BoardSide, BoardSide] {
  const dx = to.x + to.width / 2 - (from.x + from.width / 2), dy = to.y + to.height / 2 - (from.y + from.height / 2);
  if (Math.abs(dx) >= Math.abs(dy)) return dx >= 0 ? ["right", "left"] : ["left", "right"];
  return dy >= 0 ? ["bottom", "top"] : ["top", "bottom"];
}
/** The side of a box nearest to a point: where a connector dropped onto an item attaches. */
export function nearestSide(box: Geometry, point: Point): BoardSide {
  const distances: [BoardSide, number][] = [
    ["top", Math.abs(point.y - box.y)], ["right", Math.abs(point.x - (box.x + box.width))],
    ["bottom", Math.abs(point.y - (box.y + box.height))], ["left", Math.abs(point.x - box.x)],
  ];
  return distances.reduce((best, next) => (next[1] < best[1] ? next : best))[0];
}

/**
 * An edge per connector between drawn items. A connector with an end hidden in a collapsed group goes to that group instead: all
 * of them between the same two nodes in one direction become one merged edge with their count, those inside one group none.
 */
export function toFlowEdges(items: readonly BoardViewItem[], ui: FlowUi): BoardEdge[] {
  const { under } = layers(items);
  const owners = collapsedOwners(items);
  const ends = new Map(items.filter(item => item.kind !== "connector").map(item => [item.id, item]));
  const merged = new Map<string, { from: BoardViewItem; to: BoardViewItem; connectors: BoardViewItem[] }>();
  const edges: BoardEdge[] = items.flatMap(item => {
    const from = item.kind === "connector" ? ends.get(item.from ?? "") : undefined, to = from && ends.get(item.to ?? "");
    if (!from || !to) return [];
    const shownFrom = ends.get(owners.get(from.id) ?? from.id)!, shownTo = ends.get(owners.get(to.id) ?? to.id)!;
    if (shownFrom !== from || shownTo !== to) {
      const key = `merged:${shownFrom.id}:${shownTo.id}`;
      if (shownFrom !== shownTo) merged.set(key, { from: shownFrom, to: shownTo, connectors: [...(merged.get(key)?.connectors ?? []), item] });
      return [];
    }
    const props = propsOf(item), motion = ui.motion?.get(item.id);
    const [fromSide, toSide] = autoSides(drawnOf(from, ui), drawnOf(to, ui));
    const selected = ui.selected.has(item.id);
    return [{
      id: item.id, type: "board", source: from.id, target: to.id,
      sourceHandle: props.fromSide ?? fromSide, targetHandle: props.toSide ?? toSide,
      // The end handles (two circles each) only where a person can reach for them: on a selected, hovered or focused connector, so
      // pointing at an end and dragging it reconnects in one gesture; the idle ones of a large board hold none.
      label: item.text ?? "", zIndex: under.length, selected, updatable: selected || item.id === ui.hoveredEdge || item.id === ui.focusedEdge, selectable: ui.motion?.get(item.id)?.kind !== "leave",
      ariaLabel: connectorLabel(item, from, to),
      // Vue Flow writes its `tabIndex` as an attribute of the SVG group, which is no `tabindex`: this one lets the board focus the
      // connector (after its label is edited, P7); the board's own Tab walks the items, so it adds no tab stop.
      domAttributes: { "data-board-item": item.id, tabindex: "-1" } as BoardEdge["domAttributes"],
      data: edgeDataOf(item, motion, () => ({
        item, route: props.route!, dash: props.dash!, arrowStart: props.arrowStart!, arrowEnd: props.arrowEnd!, ...(props.animated && { animated: true }),
        ...(props.thickness && { thickness: props.thickness }), ...(motion && { motion }),
      })),
    }];
  });
  for (const [id, { from, to, connectors }] of merged) {
    const [sourceHandle, targetHandle] = autoSides(drawnOf(from, ui), drawnOf(to, ui));
    edges.push({
      id, type: "board", source: from.id, target: to.id, sourceHandle, targetHandle, label: "", zIndex: under.length, selectable: false, updatable: false,
      ariaLabel: t.flow.merged(connectors.length, itemLabel(from), itemLabel(to)),
      domAttributes: { "data-board-merged": id } as BoardEdge["domAttributes"],
      data: { item: connectors[0]!, route: "elbow", dash: "solid", arrowStart: "none", arrowEnd: "arrow", merged: connectors.length },
    });
  }
  const offsets = parallelOffsets(edges);
  return offsets.size ? edges.map(edge => (offsets.has(edge.id) ? { ...edge, data: offsetDataOf(edge.data!, offsets.get(edge.id)!) } : edge)) : edges;
}
/** A connector's spoken name, kept while it and both its ends stay the same objects (a changed item is a new one). */
const connectorLabels = new WeakMap<BoardViewItem, { from: BoardViewItem; to: BoardViewItem; label: string }>();
function connectorLabel(item: BoardViewItem, from: BoardViewItem, to: BoardViewItem): string {
  const known = connectorLabels.get(item);
  if (known && known.from === from && known.to === to) return known.label;
  const label = t.flow.connector(itemLabel(from), itemLabel(to), item.text ? spokenText(item.text) : "");
  connectorLabels.set(item, { from, to, label });
  return label;
}
/**
 * An edge's data stays the same object while its item, motion and offset do (like a node's parts): the edge component routes its
 * line from it, and a new object on every selection change rerouted all of them (UX SB4-3: select all of 450 items, 420 routes).
 */
const edgeData = new WeakMap<BoardViewItem, { motion: unknown; data: BoardEdgeData }>();
function edgeDataOf(item: BoardViewItem, motion: unknown, make: () => BoardEdgeData): BoardEdgeData {
  const known = edgeData.get(item);
  if (known && known.motion === motion) return known.data;
  const data = make();
  edgeData.set(item, { motion, data });
  return data;
}
const offsetData = new WeakMap<BoardEdgeData, { offset: number; data: BoardEdgeData }>();
function offsetDataOf(base: BoardEdgeData, offset: number): BoardEdgeData {
  const known = offsetData.get(base);
  if (known && known.offset === offset) return known.data;
  const data = { ...base, offset };
  offsetData.set(base, { offset, data });
  return data;
}

// Gestures → batches.

/** The items that ended elsewhere than they are, as one batch of moves. */
export function moveChanges(items: readonly BoardItem[], next: ReadonlyMap<string, Geometry>): BoardChange[] {
  return items.flatMap(item => {
    const target = next.get(item.id);
    if (!target || item.kind === "connector") return [];
    const fitted = fitGeometry(target);
    return sameGeometry(fitted, item) ? [] : [{ type: "move", itemId: item.id, ...fitted }];
  });
}

export interface NewItemOptions { id: string; z: number; text?: string; color?: BoardColor; props?: BoardProps; targetResourceId?: string; size?: { width: number; height: number } }
/** A new item of a kind centred on `at`, with its kind's size, colour and props unless given. */
export function createChange(kind: BoardItemKind, at: Point, options: NewItemOptions): BoardChange {
  const blockType = kind === "block" ? (options.props?.block as { type?: string } | undefined)?.type : undefined;
  const size = options.size ?? (kind === "block" ? blockSizeOf(blockType) : itemSizes[kind]);
  const color = options.color ?? defaultColors[kind];
  const item: NewBoardItem = {
    id: options.id, kind, ...fitGeometry({ x: at.x - size.width / 2, y: at.y - size.height / 2, ...size }), z: options.z,
    ...(kind !== "page" && kind !== "block" && kind !== "object" && kind !== "links" && { text: cleanBoardText(options.text ?? "", { line: isLineKind(kind) }) }),
    ...(color && { color }),
    ...(options.targetResourceId && { targetResourceId: options.targetResourceId }),
    props: { ...defaultProps(kind), ...options.props },
  };
  return { type: "create", item };
}

export interface Connection { from: string; to: string; fromSide?: BoardSide; toSide?: BoardSide }
const sides = (connection: Connection): BoardProps => ({ ...(connection.fromSide && { fromSide: connection.fromSide }), ...(connection.toSide && { toSide: connection.toSide }) });

/** A connector between two items in `style` (the last one used, boardConnectorStyle.ts); none for a loop, or when an end is a
 * connector or not on the board. */
export function connectChanges(items: readonly BoardItem[], connection: Connection & { id: string; style?: ConnectorStyle }): BoardChange[] {
  const ends = [connection.from, connection.to].map(id => items.find(item => item.id === id));
  if (connection.from === connection.to || ends.some(end => !end || end.deleted || end.kind === "connector")) return [];
  const { color, ...style } = connection.style ?? {};
  return [{ type: "create", item: {
    id: connection.id, kind: "connector", ...CONNECTOR_BOX, z: 0, from: connection.from, to: connection.to, ...(color && { color }),
    props: { ...defaultProps("connector"), ...style, ...sides(connection) },
  } }];
}

/** A connector dropped on empty board: a new note centred at the drop point and the connector to it, one batch. */
export function connectToNewNoteChanges(items: readonly BoardItem[], input: { noteId: string; connectorId: string; from: string; fromSide?: BoardSide; at: Point; z: number; style?: ConnectorStyle }): BoardChange[] {
  const created = createChange("note", input.at, { id: input.noteId, z: input.z });
  const connector = connectChanges([...items, (created as { item: BoardItem }).item], { id: input.connectorId, from: input.from, to: input.noteId, fromSide: input.fromSide, style: input.style });
  return connector.length ? [created, ...connector] : [];
}

/**
 * A connector whose end was dragged elsewhere: to another side of the same items it changes its sides; to another item it is
 * replaced in the same batch (the data has no `set` of its ends), keeping its label, colour and style.
 */
export function reconnectChanges(connector: BoardItem, next: Connection, newId: string): BoardChange[] {
  const props = propsOf(connector);
  if (next.from === connector.from && next.to === connector.to) {
    const patch: BoardPropsPatch = {};
    if ((next.fromSide ?? null) !== (props.fromSide ?? null)) patch.fromSide = next.fromSide ?? null;
    if ((next.toSide ?? null) !== (props.toSide ?? null)) patch.toSide = next.toSide ?? null;
    return Object.keys(patch).length ? [{ type: "set", itemId: connector.id, props: patch }] : [];
  }
  if (next.from === next.to) return [];
  const { fromSide: _from, toSide: _to, ...style } = props;
  return [
    { type: "delete", itemId: connector.id },
    { type: "create", item: {
      id: newId, kind: "connector", ...CONNECTOR_BOX, z: connector.z, from: next.from, to: next.to,
      ...(connector.text && { text: connector.text }), ...(connector.color && { color: connector.color }), props: { ...style, ...sides(next) },
    } },
  ];
}

export const deleteChanges = (ids: readonly string[]): BoardChange[] => ids.map(itemId => ({ type: "delete", itemId }));

/** A note turned into a page: a card of the page where the note was, in its container, with the note's connectors moved to it. */
export function noteToPageChanges(items: readonly BoardItem[], noteId: string, pageId: string, newId: () => string): BoardChange[] {
  const note = items.find(item => item.id === noteId && item.kind === "note" && !item.deleted);
  if (!note) return [];
  const id = newId();
  const moved = (end: string | undefined) => (end === noteId ? id : end!);
  return [
    { type: "delete", itemId: noteId },
    { type: "create", item: { id, kind: "page", ...geometryOf(note), z: note.z, targetResourceId: pageId, props: { display: "card" } } },
    ...(note.parent ? [{ type: "parent", itemId: id, parentId: note.parent } as const] : []),
    ...items.filter(item => item.kind === "connector" && !item.deleted && (item.from === noteId || item.to === noteId)).flatMap(connector => {
      const { fromSide, toSide } = propsOf(connector);
      return reconnectChanges(connector, { from: moved(connector.from), to: moved(connector.to), fromSide, toSide }, newId());
    }),
  ];
}

/** Copies of the selected items `offset` down and to the right, on top, with the connectors whose both ends are copied. */
export function duplicateChanges(items: readonly BoardItem[], ids: readonly string[], newId: () => string, offset = 24): { changes: BoardChange[]; ids: string[] } {
  const chosen = new Set(ids);
  const copies = new Map<string, string>();
  let z = topZ(items);
  const changes: BoardChange[] = [];
  for (const item of [...items].sort(byZ)) {
    if (!chosen.has(item.id) || item.kind === "connector" || item.deleted) continue;
    const id = newId();
    copies.set(item.id, id);
    changes.push({ type: "create", item: copyOf(item, { id, z: z++, x: item.x + offset, y: item.y + offset }) });
  }
  for (const item of items) {
    const from = copies.get(item.from ?? ""), to = copies.get(item.to ?? "");
    if (item.kind !== "connector" || !chosen.has(item.id) || !from || !to) continue;
    const id = newId();
    copies.set(item.id, id);
    changes.push({ type: "create", item: copyOf(item, { id, from, to }) });
  }
  return { changes: [...changes, ...copiedParents(items, copies)], ids: [...copies.values()] };
}
/** An item as a new one: the same content, clean text, other id and whatever `over` says; its membership is a change of its own. */
export function copyOf(item: BoardItem, over: Partial<NewBoardItem> & { id: string }): NewBoardItem {
  const { deleted: _deleted, parent: _parent, ...rest } = item;
  const copy = { ...rest, ...over } as NewBoardItem & { target?: unknown };
  delete copy.target;
  if (copy.text !== undefined) copy.text = cleanBoardText(copy.text, { line: isLineKind(item.kind) });
  // A JSON copy: the props may come through a reactive proxy, which structuredClone refuses.
  if (item.props) copy.props = JSON.parse(JSON.stringify(item.props));
  return copy;
}

/** To the front: above the top item, in the selection's own order; to the back: below the bottom one. Nothing if already there. */
export function reorderChanges(items: readonly BoardItem[], ids: readonly string[], where: "front" | "back"): BoardChange[] {
  const sorted = items.filter(item => item.kind !== "connector").sort(byZ);
  const chosen = sorted.filter(item => ids.includes(item.id));
  if (!chosen.length) return [];
  const edge = where === "front" ? sorted.slice(-chosen.length) : sorted.slice(0, chosen.length);
  if (edge.every((item, index) => item.id === chosen[index]!.id)) return [];
  const start = where === "front" ? sorted.at(-1)!.z + 1 : sorted[0]!.z - chosen.length;
  return chosen.map((item, index) => ({ type: "reorder", itemId: item.id, z: start + index }));
}

/** A colour (null: none, sent as "") and/or props for the selection, only where they differ. */
export function styleChanges(items: readonly BoardItem[], ids: readonly string[], patch: { color?: BoardColor | null; props?: BoardPropsPatch }): BoardChange[] {
  return items.flatMap(item => {
    if (!ids.includes(item.id)) return [];
    const wanted = patch.color === undefined ? undefined : patch.color ?? "";
    const color = wanted !== undefined && wanted !== (item.color ?? "") ? wanted : undefined;
    const current = propsOf(item) as Record<string, unknown>;
    const props = Object.fromEntries(Object.entries(patch.props ?? {}).filter(([key, value]) => (value ?? null) !== (current[key] ?? null))) as BoardPropsPatch;
    const change: BoardChange = { type: "set", itemId: item.id, ...(color !== undefined && { color }), ...(Object.keys(props).length && { props }) };
    return color !== undefined || Object.keys(props).length ? [change] : [];
  });
}

export type Alignment = "left" | "center" | "right" | "top" | "middle" | "bottom";
/** The selection lined up on an edge or the centre of its bounds. */
export function alignChanges(items: readonly BoardItem[], ids: readonly string[], how: Alignment): BoardChange[] {
  const chosen = items.filter(item => ids.includes(item.id) && item.kind !== "connector");
  if (chosen.length < 2) return [];
  const b = boundsOf(chosen);
  const next = new Map(chosen.map(item => {
    const x = how === "left" ? b.x : how === "center" ? b.x + (b.width - item.width) / 2 : how === "right" ? b.x + b.width - item.width : item.x;
    const y = how === "top" ? b.y : how === "middle" ? b.y + (b.height - item.height) / 2 : how === "bottom" ? b.y + b.height - item.height : item.y;
    return [item.id, { ...geometryOf(item), x, y }];
  }));
  return moveChanges(chosen, next);
}

/** The selection spread so the gaps between neighbours are equal, the outermost two staying where they are. */
export function distributeChanges(items: readonly BoardItem[], ids: readonly string[], axis: "horizontal" | "vertical"): BoardChange[] {
  const [pos, size] = axis === "horizontal" ? (["x", "width"] as const) : (["y", "height"] as const);
  const chosen = items.filter(item => ids.includes(item.id) && item.kind !== "connector").sort((a, b) => a[pos] - b[pos]);
  if (chosen.length < 3) return [];
  const start = chosen[0]![pos], end = Math.max(...chosen.map(item => item[pos] + item[size]));
  const gap = (end - start - chosen.reduce((sum, item) => sum + item[size], 0)) / (chosen.length - 1);
  let at = start;
  const next = new Map(chosen.map(item => { const g = { ...geometryOf(item), [pos]: at }; at += item[size] + gap; return [item.id, g]; }));
  return moveChanges(chosen, next);
}

/** The text an inline edit leaves, clean as the server takes it; a connector's label on one line. */
export function textChanges(item: BoardItem, text: string): BoardChange[] {
  const clean = cleanBoardText(text, { line: isLineKind(item.kind) });
  return clean === (item.text ?? "") ? [] : [{ type: "set", itemId: item.id, text: clean }];
}


/**
 * New items as one batch: a square grid (⌈√n⌉ columns) of equal cells, the largest item's size plus a gap, each item centred in
 * its cell and the grid centred on `at`, stacked on top from `z`.
 */
export function insertChanges(inserted: readonly BoardInsert[], at: Point, newId: () => string, z: number, gap = 24): { changes: BoardChange[]; ids: string[] } {
  if (!inserted.length) return { changes: [], ids: [] };
  const columns = Math.ceil(Math.sqrt(inserted.length)), rows = Math.ceil(inserted.length / columns);
  const sizes = inserted.map(item => (item.kind === "block" ? blockSizeOf(item.block.type) : itemSizes[item.kind]));
  const cell = { width: Math.max(...sizes.map(s => s.width)), height: Math.max(...sizes.map(s => s.height)) };
  const left = at.x - (columns * cell.width + (columns - 1) * gap) / 2, top = at.y - (rows * cell.height + (rows - 1) * gap) / 2;
  const ids: string[] = [];
  const changes = inserted.map((item, index) => {
    const centre = { x: left + (index % columns) * (cell.width + gap) + cell.width / 2, y: top + Math.floor(index / columns) * (cell.height + gap) + cell.height / 2 };
    const id = newId();
    ids.push(id);
    const options: NewItemOptions = { id, z: z + index };
    if (item.kind === "page") { options.targetResourceId = item.resourceId; if (item.anchor) options.props = { anchor: item.anchor }; }
    if (item.kind === "block") options.props = { block: item.block };
    if (item.kind === "object") { options.targetResourceId = item.objectId; options.props = { objectId: item.objectId, typeId: item.typeId, view: "card" }; }
    if (item.kind === "note") options.text = item.text;
    return createChange(item.kind, centre, options);
  });
  return { changes, ids };
}
