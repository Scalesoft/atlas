import { api, post, put, type Page } from "./api";
import { invalidateLexicon } from "./linking/lexicon";
import type { TreeNodeType } from "./treeMove";

export interface OrganizationTreeItem {
  id: string;
  parentId: string | null;
  /** Empty only for a reference node whose target the reader may not read (`readable` false; the server sends null). */
  title: string;
  version: number;
  sortOrder: number;
  isPublished: boolean;
  canEdit: boolean;
  /** "board" for a board of the module Boards, "reference" for a reference node (leaves, version 0); a page otherwise. */
  type?: TreeNodeType;
  targetId?: string | null;
  targetTypeId?: string | null;
  anchor?: string | null;
  readable?: boolean;
  attachedBoardId?: string | null;
  /** „Řadit abecedně“: the page's direct children are shown by title (server TitleOrder). */
  childrenByTitle?: boolean;
}
/** `rootByTitle`: „Řadit abecedně“ of the space root. */
export interface OrganizationTree { spaceId: string; version: number; items: OrganizationTreeItem[]; rootByTitle?: boolean }
export interface TrashItem {
  id: string;
  parentId: string | null;
  title: string;
  version: number;
  trashedAt: string;
  trashedById: string;
  trashedByName: string | null;
  trashBatchId: string;
  type?: TreeNodeType;
}
export interface TrashList { spaceId: string; version: number; items: TrashItem[]; hasMore: boolean }
/** `titleOrderCancelled`: the move was within a level sorted by title and ended „Řadit abecedně“ there. */
export interface OrganizationChange { version: number; affectedIds: string[]; hasSiblingTitleCollision: boolean; titleOrderCancelled?: boolean }
export interface OrganizationChanged { kind: "move" | "trash" | "untrash" | "move-space"; affectedIds: string[]; sourceSpaceId?: string; targetSpaceId?: string; titleOrderCancelled?: boolean }
const segment = encodeURIComponent;
/** A tree node's own resource: a board moves and leaves the trash at `/boards/{id}`, a reference node moves at
 * `/tree/references/{id}`, both with the page's request shapes. */
const node = (id: string, type?: TreeNodeType) => `/${type === "board" ? "boards" : type === "reference" ? "tree/references" : "pages"}/${segment(id)}`;
/** A reference node: a placement of an object in the tree, not a copy; removing it removes only the link (server Features/TreeReferences). */
export const treeReferenceApi = {
  add(spaceId: string, request: { targetId: string; anchor?: string | null; parentId: string | null; beforeId: string | null }) {
    return post<OrganizationTreeItem>(`/spaces/${segment(spaceId)}/tree/references`, request);
  },
  remove(id: string) {
    return api<void>(`/tree/references/${segment(id)}`, { method: "DELETE" });
  },
};
/** The last read of each space's tree, whoever asked (the shell's load too), for `recentTree`. */
const recentTrees = new Map<string, { at: number; tree: Promise<OrganizationTree> }>();
/** How long a read tree stands for a lookup: the shell reads it again on a page switch only after as long (App.vue TREE_FRESH_MS). */
const RECENT_TREE_MS = 30_000;
function remember(spaceId: string, tree: Promise<OrganizationTree>) {
  recentTrees.set(spaceId, { at: Date.now(), tree });
  tree.catch(() => { if (recentTrees.get(spaceId)?.tree === tree) recentTrees.delete(spaceId); });
  return tree;
}
export const organizationApi = {
  tree(spaceId: string, signal?: AbortSignal) {
    return remember(spaceId, api<OrganizationTree>(`/spaces/${segment(spaceId)}/tree`, { signal })
      // An unreadable reference node has no title (a lock in the tree); everything else reads titles as text.
      .then(tree => ({ ...tree, items: tree.items.map(item => item.title === null ? { ...item, title: "" } : item) })));
  },
  /**
   * The space's tree as last read in the shell's freshness (a read on its way counts), else read now: for a lookup that writes nothing
   * (whether the page has a board). Opening a deployment page read the whole tree three times (3.6 perf).
   */
  recentTree(spaceId: string) {
    const recent = recentTrees.get(spaceId);
    return recent && Date.now() - recent.at < RECENT_TREE_MS ? recent.tree : remember(spaceId, organizationApi.tree(spaceId));
  },
  trashList(spaceId: string, offset = 0, signal?: AbortSignal) {
    return api<TrashList>(`/spaces/${segment(spaceId)}/trash?offset=${offset}&limit=50`, { signal });
  },
  move(id: string, parentId: string | null, beforePageId: string | null, expectedTreeVersion: number, type?: TreeNodeType) {
    return post<OrganizationChange>(`${node(id, type)}/move`, { parentId, beforePageId, expectedTreeVersion });
  },
  /** „Řadit abecedně“ of the children of `parentId` (null: the space root); off keeps the order the level showed. */
  sortByTitle(spaceId: string, parentId: string | null, byTitle: boolean) {
    return post<OrganizationChange>(`/spaces/${segment(spaceId)}/tree/sort`, { parentId, byTitle });
  },
  moveSpace(id: string, request: {
    targetSpaceId: string; parentId: string | null; beforePageId: string | null; includeSubtree: boolean;
    expectedSourceTreeVersion: number; expectedTargetTreeVersion: number; expectedVersion: number; acknowledgeAccessChange: boolean;
  }) {
    return post<{ sourceSpaceId: string; targetSpaceId: string; sourceVersion: number; targetVersion: number; affectedIds: string[] }>(`/pages/${segment(id)}/move-space`, request);
  },
  trash(id: string, includeSubtree: boolean, expectedTreeVersion: number, expectedVersion: number) {
    return post<OrganizationChange>(`/pages/${segment(id)}/trash`, { includeSubtree, expectedTreeVersion, expectedVersion });
  },
  untrash(id: string, parentId: string | null, includeSubtree: boolean, expectedTreeVersion: number, type?: TreeNodeType) {
    return post<OrganizationChange>(`${node(id, type)}/untrash`, { parentId, includeSubtree: type === "board" ? false : includeSubtree, expectedTreeVersion });
  },
  rename(id: string, title: string, expectedVersion: number) {
    // The space's lexicon knows the old title until it is asked again.
    return put<Page>(`/pages/${segment(id)}/title`, { title, expectedVersion }).then(page => { invalidateLexicon(page.spaceId); return page; });
  },
};
