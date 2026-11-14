import { type Component } from "vue";
import { lazyComponent } from "../lazyComponent";
import t from "../i18n/boards";

/**
 * The card of each object type a board shows (objects spec §2.1 „Pohledy“, F1 plan §0: a type's views are registered on the web):
 * the component that draws a live object card (`kind: "object"`) by its `typeId`, with the type's Czech name and icon for the
 * placeholder and the locked card. A new type adds its entry here; a type without one is drawn as its title.
 * Each card takes `{ object: LiveObject, editing: boolean, compact: boolean, host: BoardBlockHost }` and emits `done` when its
 * editing ends.
 */
export interface ObjectCardView { label: string; icon: string; card: Component }
export const objectCardViews: Readonly<Record<string, ObjectCardView>> = {
  "atlas.contact": { get label() { return t.canvas.contact; }, icon: "user", card: lazyComponent(() => import("../components/boards/cards/ContactObjectCard.vue")) },
};

/** A card of another board (dropped from the tree, Obsah or a relation): it has no view of its own and opens that board. */
export const isBoardCard = (item: { kind: string; targetResourceId?: string | null; props?: unknown }) =>
  item.kind === "object" && !!item.targetResourceId && (item.props as { typeId?: unknown } | undefined)?.typeId === "atlas.board";
