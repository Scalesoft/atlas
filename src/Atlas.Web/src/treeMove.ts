import type { PageSummary } from "./api";
/**
 * A node of the space tree: a page, a board (module Boards) or a reference node (a placement of any object, not a copy),
 * the last two leaves among the pages (`GET /api/spaces/{id}/tree`).
 */
export type TreeNodeType = "page" | "board" | "reference";
export type TreePage = PageSummary & {
  canEdit?: boolean; type?: TreeNodeType;
  /** A reference node's target, its type (null when unreadable) and the place inside it (a heading or block id). */
  targetId?: string | null; targetTypeId?: string | null; anchor?: string | null;
  /** False for a reference whose target the reader may not read: a lock without a title. */
  readable?: boolean;
  /** A page's own board (its deployment board), for an editor of the page: the row's quiet mark opens it. */
  attachedBoardId?: string | null;
  /** „Řadit abecedně“: the page's direct children are shown by title; a hand move among them ends it. */
  childrenByTitle?: boolean;
};
export const isBoardNode = (node: { type?: string }) => node.type === "board";
export const isReferenceNode = (node: { type?: string }) => node.type === "reference";
/** A board and a reference node are leaves: nothing goes inside them. */
export const isLeafNode = (node: { type?: string }) => isBoardNode(node) || isReferenceNode(node);
export const isPageNode = (node: { type?: string }) => !node.type || node.type === "page";
export type DropPosition = "before" | "inside" | "after";
/** `sourceId`: set by a keyboard move (no drag has named the source before the drop). */
export interface TreeDrop { targetId: string | null; position: DropPosition; sourceId?: string }
export interface TreeMove { parentId: string | null; beforePageId: string | null }

/** Only known, editable nodes participate; server authorization and CAS remain authoritative. */
export function resolveTreeMove(pages: TreePage[], sourceId: string, drop: TreeDrop): TreeMove | undefined {
  const source = pages.find(page => page.id === sourceId);
  if (!source?.canEdit) return;
  const target = drop.targetId ? pages.find(page => page.id === drop.targetId) : undefined;
  if (drop.targetId && (!target?.canEdit || target.id === sourceId)) return;
  // Boards and reference nodes are leaves (interaction catalog, decision 3): nothing goes inside one.
  if (target && drop.position === "inside" && isLeafNode(target)) return;
  const parentId = target ? drop.position === "inside" ? target.id : target.parentId : null;
  const byId = new Map(pages.map(page => [page.id, page]));
  const seen = new Set<string>();
  let ancestor = parentId;
  while (ancestor) {
    if (ancestor === sourceId || seen.has(ancestor)) return;
    seen.add(ancestor);
    const item = byId.get(ancestor);
    if (!item) return;
    ancestor = item.parentId;
  }
  const siblings = pages.filter(page => page.parentId === parentId && page.id !== sourceId);
  const targetIndex = target ? siblings.findIndex(page => page.id === target.id) : -1;
  const beforePageId = !target || drop.position === "inside" ? null
    : drop.position === "before" ? target.id : siblings[targetIndex + 1]?.id ?? null;
  if (source.parentId === parentId) {
    const currentSiblings = pages.filter(page => page.parentId === parentId);
    if ((currentSiblings[currentSiblings.findIndex(page => page.id === sourceId) + 1]?.id ?? null) === beforePageId) return;
  }
  return { parentId, beforePageId };
}

/**
 * Where a node dropped into the tree from outside goes (a reference node, spec 2026-10-27 §5.4): the same gate as a move, as if a
 * new editable node came from nowhere, so the thirds rule, leaves and editable targets apply alike.
 */
export function resolveTreeInsert(pages: TreePage[], drop: TreeDrop): TreeMove | undefined {
  const incoming = "\u0000incoming";
  return resolveTreeMove([...pages, { id: incoming, spaceId: "", parentId: incoming, title: "", version: 0, isPublished: true, canEdit: true }], incoming, drop);
}

/** Where a node stands among its siblings (the next one is the one it goes before): what a move or a removal names to be put back. */
export function treePlaceOf(pages: readonly TreePage[], id: string): TreeMove {
  const at = pages.findIndex(item => item.id === id), node = pages[at];
  return { parentId: node?.parentId ?? null, beforePageId: node ? pages.slice(at + 1).find(item => item.parentId === node.parentId)?.id ?? null : null };
}

export function pageAncestors(pages: PageSummary[], id: string): PageSummary[] {
  const byId = new Map(pages.map(page => [page.id, page]));
  const result: PageSummary[] = [], seen = new Set([id]);
  let parent = byId.get(id)?.parentId;
  while (parent && !seen.has(parent)) {
    const page = byId.get(parent); if (!page) break;
    result.unshift(page); seen.add(parent); parent = page.parentId;
  }
  return result;
}

/**
 * The pages a reader can actually reach right now, depth first, parents before
 * their children and nothing under a collapsed node. This is the order arrow
 * keys move through, so it has to match what the recursive component renders --
 * every instance receives the whole flat list, so both derive it the same way.
 */
export function visibleTreeOrder(pages: TreePage[], collapsedIds: ReadonlySet<string>): TreePage[] {
  const children = new Map<string | null, TreePage[]>();
  const known = new Set(pages.map(page => page.id));
  for (const page of pages) {
    // A slice can arrive without an ancestor (a space the reader cannot see).
    // Such a page is not rendered, so it must not appear in the order either.
    const parentId = page.parentId !== null && !known.has(page.parentId) ? undefined : page.parentId;
    if (parentId === undefined) continue;
    const siblings = children.get(parentId) ?? [];
    siblings.push(page);
    children.set(parentId, siblings);
  }
  const order: TreePage[] = [];
  const walk = (parentId: string | null) => {
    for (const page of children.get(parentId) ?? []) {
      order.push(page);
      if (!collapsedIds.has(page.id)) walk(page.id);
    }
  };
  walk(null);
  return order;
}
