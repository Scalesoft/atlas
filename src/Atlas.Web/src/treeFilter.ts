import { foldText } from "./lib/foldText";
import { plainTitle } from "./objects/relationsApi";
import { visibleTreeOrder, type TreePage } from "./treeMove";

/** The tree filtered by a query, all on the loaded tree. */
export interface TreeFilter {
  /** The matching nodes in the order the tree shows them, whatever is folded: the first is preselected, Enter opens it. */
  matches: TreePage[];
  /** Nodes that neither match nor lead to a match: their rows fold away. */
  hidden: ReadonlySet<string>;
  /** The folded branches with the filter's way through them unfolded (what the reader folded comes back when it closes). */
  collapsed: ReadonlySet<string>;
}

/** Matches by the title, accent- and case-insensitively (foldText), and shows each match with its ancestors. Null for an empty query. */
export function filterTree(pages: readonly TreePage[], query: string, collapsed: ReadonlySet<string>): TreeFilter | null {
  const needle = foldText(query.trim());
  if (!needle) return null;
  const byId = new Map(pages.map(page => [page.id, page]));
  const matches = visibleTreeOrder([...pages], new Set()).filter(page => page.readable !== false && foldText(plainTitle(page.title)).includes(needle));
  const shown = new Set<string>(), ways = new Set<string>();
  for (const match of matches) {
    shown.add(match.id);
    for (let parent = match.parentId; parent && !ways.has(parent); parent = byId.get(parent)?.parentId ?? null) { ways.add(parent); shown.add(parent); }
  }
  return {
    matches,
    hidden: new Set(pages.filter(page => !shown.has(page.id)).map(page => page.id)),
    collapsed: new Set([...collapsed].filter(id => !ways.has(id))),
  };
}
