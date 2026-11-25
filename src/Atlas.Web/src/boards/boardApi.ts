import { api, post } from "../api";
import { appearanceColors } from "../appearance";
import { liveObjectOf, liveObjects } from "../objects/liveObjects";
import type { ObjectData } from "../objects/objectApi";

/**
 * The board module's HTTP contract, extended by the
 * interaction design: more kinds, typed `props`, a wider palette and richer targets.
 * The client codes to the extension already; a kind or prop it does not know is carried along and drawn as a plain card.
 */
export const boardItemKinds = ["note", "text", "shape", "frame", "group", "page", "block", "object", "connector", "links"] as const;
export type BoardItemKind = (typeof boardItemKinds)[number];
/** Spec §4 order (the palette shows it so). The server of the prototype knows only yellow, blue, green, pink, purple and gray. */
export const boardColors = ["yellow", "orange", "red", "pink", "purple", "blue", "teal", "green", "gray", "white", "black"] as const;
export type BoardPaletteColor = (typeof boardColors)[number];
/** A board colour, or for an editor component's background also a colour of the appearance palette of spaces (`blue-4`). */
export type BoardColor = BoardPaletteColor | `${string}-${1 | 2 | 3 | 4 | 5}`;
/** A colour the server takes: one of the board's or of the appearance palette (never its legacy names). */
export const isBoardColor = (value: unknown): value is BoardColor =>
  typeof value === "string" && (boardColors.includes(value as BoardPaletteColor) || appearanceColors.some(color => color.id === value));

export type BoardSide = "top" | "right" | "bottom" | "left";
export type BoardFontSize = "s" | "m" | "l" | "xl";
export type BoardAlign = "left" | "center" | "right";
export type BoardShape = "rect" | "rounded" | "ellipse" | "diamond";
export type BoardRoute = "elbow" | "curved" | "straight";
export type BoardThickness = "s" | "m" | "l";
/** The contact schema of the editor's contact component (a contact is a `block` of the editor's contact plugin now). */
export interface BoardContact {
  name: string; title?: string; organization?: string; department?: string; emails?: string[]; phones?: string[];
  website?: string; address?: string; directoryId?: string;
}
/** `props` by kind (spec §4). Every key is optional here: an item of an older server has none, and the defaults fill them. */
export interface BoardProps {
  fontSize?: "auto" | BoardFontSize;
  align?: BoardAlign;
  bold?: boolean;
  shape?: BoardShape;
  /** A page card: "card" (title and excerpt) or "preview" (the rendered page), and one section by its heading anchor; "product" (the
   * product heading's anchor) or "service" (the slug of the service's name): the deployment's part drawn live from the page's
   * requirements (projector/DeploymentBoardCard.vue). */
  display?: "card" | "preview" | "product" | "service";
  anchor?: string;
  /** A `block`: one node of the editor (type, attributes, content) as in a page, rendered by its plugin. */
  block?: BoardBlockNode;
  fromSide?: BoardSide;
  toSide?: BoardSide;
  route?: BoardRoute;
  arrowStart?: "none" | "arrow";
  arrowEnd?: "none" | "arrow";
  dash?: "solid" | "dashed";
  /** A connector's state (objects spec §5.3): it colours the line; none is a plain annotation. */
  state?: BoardEdgeState;
  /** A connector's quick style: a flow along its arrow's direction, and its line's thickness (none: the plain 2 px line). */
  animated?: boolean;
  thickness?: BoardThickness;
  /** An object card (objects spec §5.1): the object it shows, fixed when placed, and its view. */
  objectId?: string;
  typeId?: string;
  view?: "card" | "compact";
  /** A group shown as one node, its members hidden and their connectors merged (§5.4). */
  collapsed?: boolean;
  /** A list derived from the relations (`links`, P15): the people, what we wait for, or where `objectId` is used; `objectId` narrows
   * people and waiting to one page instead of the board's space. */
  show?: BoardLinksShow;
}
export const boardLinksShows = ["people", "waiting", "usage"] as const;
export type BoardLinksShow = (typeof boardLinksShows)[number];
export const boardEdgeStates = ["waiting", "done", "blocked"] as const;
export type BoardEdgeState = (typeof boardEdgeStates)[number];
export interface BoardBlockNode { type: string; attrs?: Record<string, unknown>; content?: unknown[] }
/** A partial `set` of props: a key with `null` removes an optional key (spec §4). */
export type BoardPropsPatch = { [K in keyof BoardProps]?: BoardProps[K] | null };

/** A card's target as the reader may see it: `available: false` and no title for a page or object that is gone or not theirs. A page
 * card's (`atlas.page`) has its space and an excerpt; an object card's (the object's type) its version, its data and whether the
 * reader may change it (through the object path). */
export interface BoardTarget {
  resourceId: string; type: string; title: string | null; available: boolean;
  spaceTitle?: string | null; excerpt?: string | null;
  version?: number; canEdit?: boolean; data?: ObjectData | null;
  trashed?: boolean; spaceId?: string; canRestore?: boolean;
}
/** The board's own settings (spec §3.7, §4), shared by everyone on it; a Writer changes them. */
export interface BoardTimeline { origin: string; pxPerDay: number }
export interface BoardSettings {
  background: "dots" | "lines" | "none" | "timeline";
  snapToGrid: boolean;
  gridSize: 8 | 16 | 24 | 32 | 48;
  timeline?: BoardTimeline;
}
export const defaultBoardSettings: BoardSettings = { background: "dots", snapToGrid: false, gridSize: 24 };
export const gridSizes = [8, 16, 24, 32, 48] as const;

export interface BoardItemDto {
  id: string; kind: BoardItemKind; x: number; y: number; width: number; height: number; z: number;
  text?: string | null; color?: BoardColor | null; from?: string | null; to?: string | null; version: number;
  props?: BoardProps | null;
  target?: BoardTarget | null;
  /** The group or frame the item belongs to (explicit membership). */
  parent?: string | null;
}
/** `ownerPageId`: the page whose own board it is (a deployment board), outside the space tree; absent for a board of the tree. */
export interface BoardDto { id: string; spaceId: string; title: string; sequence: number; canEdit: boolean; items: BoardItemDto[]; settings?: Partial<BoardSettings> | null; ownerPageId?: string | null }
export interface BoardSummary { id: string; title: string; updatedAt: string; itemCount: number }

export interface NewBoardItem {
  id: string; kind: BoardItemKind; x: number; y: number; width: number; height: number; z: number;
  text?: string; color?: BoardColor; targetResourceId?: string; from?: string; to?: string; props?: BoardProps;
}
export interface Geometry { x: number; y: number; width: number; height: number }
export type BoardChange =
  | { type: "create"; item: NewBoardItem }
  | ({ type: "move"; itemId: string } & Geometry)
  /** `color: ""` removes the colour; `from`/`to` move a connector's ends to other items (it keeps its id). */
  | { type: "set"; itemId: string; text?: string; color?: BoardColor | ""; props?: BoardPropsPatch; from?: string; to?: string }
  | { type: "reorder"; itemId: string; z: number }
  | { type: "delete"; itemId: string }
  | { type: "restore"; itemId: string }
  /** Into a group or frame, or out of it without `parentId`. */
  | { type: "parent"; itemId: string; parentId?: string }
  | { type: "rename"; title: string }
  | { type: "settings"; settings: Partial<BoardSettings> };
/**
 * An edit of a card's object made on the board (a contact's fields, P6): the object path writes it (liveObjects), never a board
 * batch, and the board's undo holds it as one step among the board's own. `before` and `after` are the fields it changed (null: none).
 * Only the canvas, the model and the session know it; the server never sees one.
 */
export interface ObjectEdit { type: "object"; itemId: string; objectId: string; before: Record<string, unknown>; after: Record<string, unknown> }
/** What a gesture on the board makes: changes of the board, and edits of its cards' objects. */
export type BoardEdit = BoardChange | ObjectEdit;
export const isObjectEdit = (edit: BoardEdit): edit is ObjectEdit => edit.type === "object";
/** What the server takes in one batch (prototype spec §5). */
export const maxBatchChanges = 200;

/** One confirmed batch, from `operations?after=` or the hub's `BoardChanged`. */
export interface BoardBatch { sequence: number; operationId: string; authorId?: string; changes: BoardChange[] }
export interface BoardAck { sequence: number; duplicate: boolean }

const board = (boardId: string) => `/boards/${encodeURIComponent(boardId)}`;
export const boardApi = {
  // The spec answers an array, the server draft `{ boards }`; either is read until the two agree.
  list: async (spaceId: string) => {
    const body = await api<BoardSummary[] | { boards: BoardSummary[] }>(`/spaces/${encodeURIComponent(spaceId)}/boards`);
    return Array.isArray(body) ? body : body.boards;
  },
  /** `parentPageId`: the page the board goes under in the space tree; none puts it at the end of the root. With `purpose`
   *  (`projector.board`) it is that page's own board instead, no node of the tree. */
  create: (spaceId: string, title: string, parentPageId?: string | null, purpose?: string) =>
    post<BoardDto>(`/spaces/${encodeURIComponent(spaceId)}/boards`, { title, ...(parentPageId ? { parentPageId } : {}), ...(purpose ? { purpose } : {}) }),
  /** The board; its object cards' targets go to the tab's live objects (liveObjects), which the cards read. */
  get: async (boardId: string) => {
    const dto = await api<BoardDto>(board(boardId));
    for (const item of dto.items) if (item.kind === "object") { const object = liveObjectOf(item.target); if (object) liveObjects.seed(object); }
    return dto;
  },
  remove: (boardId: string) => api<void>(board(boardId), { method: "DELETE" }),
  submit: async (boardId: string, operationId: string, changes: readonly BoardChange[]) => {
    const body = JSON.stringify({ operationId, changes }), path = `${board(boardId)}/operations`;
    // A batch sent as the page is hidden (a text saved before a reload, U3-1) outlives the page; browsers allow 64 KiB of such,
    // in bytes and over every keepalive request in flight (W4-1). One the browser refuses goes once more as an ordinary request.
    const keepalive = document.visibilityState === "hidden" && new Blob([body]).size < 60_000;
    try { return await api<BoardAck>(path, { method: "POST", body, keepalive }); } catch (failure) {
      if (!keepalive || !(failure instanceof TypeError)) throw failure;
      return api<BoardAck>(path, { method: "POST", body });
    }
  },
  /** The items taken off the board that a Writer can restore (newest first). */
  removed: (boardId: string) => api<{ items: { id: string; kind: BoardItemKind; text: string | null }[] }>(`${board(boardId)}/removed`),
  after: (boardId: string, sequence: number) => api<{ sequence: number; batches: BoardBatch[] }>(`${board(boardId)}/operations?after=${sequence}`),
};
export type BoardApi = typeof boardApi;
