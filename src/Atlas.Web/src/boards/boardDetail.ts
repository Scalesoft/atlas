import type { BoardEdgeState } from "./boardApi";
import type { BoardViewItem } from "./boardSession";
import t from "../i18n/boards";

/**
 * What the object detail of a card knows from its board (objects spec §6): its relations (the connectors at it, Vazby). Where an object is used, across boards, pages and the tree, is the relations endpoint (components/objects/ObjectRelations.vue).
 */
export interface BoardRelation { id: string; direction: "out" | "in"; other: BoardViewItem; label: string; state: BoardEdgeState | undefined }

/** The live connectors at the item, in board order, with the item at their other end. */
export function relationsOf(items: readonly BoardViewItem[], itemId: string): BoardRelation[] {
  const byId = new Map(items.map(item => [item.id, item]));
  return items.flatMap(item => {
    if (item.kind !== "connector" || item.deleted || (item.from !== itemId && item.to !== itemId)) return [];
    const direction = item.from === itemId ? "out" : "in";
    const other = byId.get(direction === "out" ? item.to! : item.from!);
    return other && !other.deleted ? [{ id: item.id, direction, other, label: item.text ?? "", state: item.props?.state }] : [];
  });
}

/** The tabs of the detail in the dock (BoardObjectDetail). */
export type BoardDetailTab = "overview" | "description" | "relations" | "usage" | "comments" | "history";
const tab = (id: BoardDetailTab) => ({ id, get label() { return t.detail.tabs[id]; } });
export const boardDetailTabs: { id: BoardDetailTab; label: string }[] = (["overview", "description", "relations", "usage", "comments", "history"] as const).map(tab);
