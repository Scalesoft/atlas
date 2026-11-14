export interface TreeExpansionState {
  /** Spaces whose page tree the sidebar shows expanded. */
  openedSpaces: Set<string>;
  collapsedPages: Set<string>;
}

type ReadStorage = Pick<Storage, "getItem">;
type WriteStorage = Pick<Storage, "setItem">;
type TreeItem = { id: string; parentId?: string | null };

// v1 remembered a single `lastSpaceId` (the sidebar showed one space); v2 remembers every expanded space.
const VERSION = 2;
const OPENED_SPACE_LIMIT = 200;
const COLLAPSED_PAGE_LIMIT = 500;
const VALID_ID = /^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$/;

export function treeExpansionKey(userId: string, version = VERSION) {
  return `scaleatlas:tree-expansion:v${version}:${encodeURIComponent(userId)}`;
}

function emptyState(): TreeExpansionState {
  return { openedSpaces: new Set(), collapsedPages: new Set() };
}

function validIds(value: unknown, limit: number) {
  const result = new Set<string>();
  if (!Array.isArray(value)) return result;
  for (const candidate of value) {
    if (typeof candidate === "string" && VALID_ID.test(candidate)) result.add(candidate);
    if (result.size === limit) break;
  }
  return result;
}

function parse(raw: string): Record<string, unknown> | undefined {
  if (raw.length > 100_000) return undefined;
  const parsed: unknown = JSON.parse(raw);
  return parsed && typeof parsed === "object" ? parsed as Record<string, unknown> : undefined;
}

export function readTreeExpansion(storage: ReadStorage | undefined, userId: string): TreeExpansionState {
  if (!storage || !userId) return emptyState();
  try {
    const current = storage.getItem(treeExpansionKey(userId));
    if (current !== null) {
      const value = parse(current);
      if (value?.version !== VERSION) return emptyState();
      return {
        openedSpaces: validIds(value.openedSpaces, OPENED_SPACE_LIMIT),
        collapsedPages: validIds(value.collapsedPages, COLLAPSED_PAGE_LIMIT),
      };
    }
    // An account that last used the single-space sidebar keeps that space open and its collapsed branches.
    const previous = storage.getItem(treeExpansionKey(userId, 1));
    const value = previous === null ? undefined : parse(previous);
    if (value?.version !== 1) return emptyState();
    return {
      openedSpaces: validIds([value.lastSpaceId], 1),
      collapsedPages: validIds(value.collapsedPages, COLLAPSED_PAGE_LIMIT),
    };
  } catch {
    return emptyState();
  }
}

export function writeTreeExpansion(storage: WriteStorage | undefined, userId: string, state: TreeExpansionState) {
  if (!storage || !userId) return;
  try {
    storage.setItem(treeExpansionKey(userId), JSON.stringify({
      version: VERSION,
      openedSpaces: [...validIds([...state.openedSpaces], OPENED_SPACE_LIMIT)],
      collapsedPages: [...validIds([...state.collapsedPages], COLLAPSED_PAGE_LIMIT)],
    }));
  } catch {
    // Browser preferences are optional; navigation remains usable when storage is unavailable.
  }
}

export function revealTreeAncestors(pages: readonly TreeItem[], pageId: string, collapsedPages: ReadonlySet<string>) {
  const next = new Set(collapsedPages);
  const byId = new Map(pages.map(page => [page.id, page]));
  const seen = new Set([pageId]);
  let parentId = byId.get(pageId)?.parentId;
  while (parentId && !seen.has(parentId)) {
    next.delete(parentId);
    seen.add(parentId);
    parentId = byId.get(parentId)?.parentId;
  }
  return next;
}
