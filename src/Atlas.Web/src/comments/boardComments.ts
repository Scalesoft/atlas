import type { BoardViewItem } from "../boards/boardSession";
import type { InjectionKey } from "vue";

/** The server's limit of one counts request. */
export const maxCountedObjects = 100;

/** Whose discussion a board's detail shows: an object card's object, else the board's (anchored at its own item). */
export interface BoardDiscussion { objectId: string; anchor: string | null; ofBoard: boolean }

export function discussionOf(item: BoardViewItem | null, boardId: string): BoardDiscussion {
  if (item && (item.kind === "page" || item.kind === "object") && item.targetResourceId) return { objectId: item.targetResourceId, anchor: null, ofBoard: false };
  return { objectId: boardId, anchor: item?.id ?? null, ofBoard: true };
}

/** The objects whose comment counts a board shows (its own first, then its live cards' objects), once each, at most a request's worth. */
export function commentedObjectsOf(boardId: string, items: readonly BoardViewItem[]): string[] {
  const ids = new Set([boardId]);
  for (const item of items) {
    if (ids.size >= maxCountedObjects) break;
    if (!item.deleted && (item.kind === "page" || item.kind === "object") && item.targetResourceId) ids.add(item.targetResourceId);
  }
  return [...ids];
}

/** The quiet badges of a board's cards: the count of an object's live comments (0 without any or unknown). Provided by BoardView. */
export const commentCountsKey: InjectionKey<(objectId: string) => number> = Symbol("commentCounts");
