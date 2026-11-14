import type { Viewport } from "./boardViewport";

/**
 * Where a person left a board: its view and its selection. Kept in the tab (session storage) so that the way back from a page card's
 * page, or from anywhere else in Atlas, finds the board as it was instead of fitted again with nothing selected (BD2-3).
 */
export interface BoardPlace { viewport: Viewport; selected: string[] }

const keyOf = (boardId: string) => `atlas.board-place.${boardId.toLowerCase()}`;
const finite = (value: unknown): value is number => typeof value === "number" && Number.isFinite(value);

export function saveBoardPlace(boardId: string, place: BoardPlace | null, storage: Storage = sessionStorage) {
  try {
    if (place) storage.setItem(keyOf(boardId), JSON.stringify(place)); else storage.removeItem(keyOf(boardId));
  } catch { /* storage refused: the board then opens fitted, as before */ }
}

/** The place saved for the board, or none; what is not a view at a sane zoom is none. */
export function readBoardPlace(boardId: string, storage: Storage = sessionStorage): BoardPlace | undefined {
  try {
    const raw = storage.getItem(keyOf(boardId));
    if (!raw) return undefined;
    const { viewport, selected } = JSON.parse(raw) as Partial<BoardPlace>;
    if (!viewport || !finite(viewport.x) || !finite(viewport.y) || !finite(viewport.zoom) || viewport.zoom < 0.05 || viewport.zoom > 4) return undefined;
    return { viewport: { x: viewport.x, y: viewport.y, zoom: viewport.zoom }, selected: Array.isArray(selected) ? selected.filter((id): id is string => typeof id === "string").slice(0, 500) : [] };
  } catch { return undefined; }
}
