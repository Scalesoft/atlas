import { foldText } from "./lib/foldText";

/** A title as the server compares it (SearchText.Normalize): no case, no diacritics, punctuation as single spaces. */
const searchable = (text: string) => foldText(text).replace(/[^\p{L}\p{N}]+/gu, " ").trim();

/** 0 the whole title, 1 the start of the title, 2 neither. */
export function titleMatch(title: string, query: string): number {
  const folded = searchable(title), wanted = searchable(query);
  if (!wanted) return 2;
  return folded === wanted ? 0 : folded.startsWith(wanted) ? 1 : 2;
}

/**
 * UX SE3-3: the omnisearch's groups (Stránky, Nadpisy, Tabule, …) by their best title match, and each group's items likewise, so
 * the item whose whole title (or else its start) is the query is the preselected first one whatever its kind; Enter opens it.
 * UX SE5-1: a group with `rankFloor` (the headings) never outranks a better group on its own whole-title match: a heading called „Server“ is
 * no more the page the reader means than „Serverová strana“, and 49 of them must not push the page titles below the fold.
 * A `rankFloor` of 3 (YouTrack) is below any title match, even none (2): such a group is always last, after the spaces too.
 * Stable: without such a match the groups and the server's order stay as they are.
 */
export function orderSearchGroups<G extends { items: readonly { title: string }[]; rankFloor?: number }>(groups: readonly G[], query: string): G[] {
  const ranked = groups.map((group, position) => {
    const items = group.items.map((item, index) => ({ item, index, rank: titleMatch(item.title, query) }))
      .sort((a, b) => a.rank - b.rank || a.index - b.index);
    return { group: { ...group, items: items.map(entry => entry.item) } as G, position, rank: Math.max(group.rankFloor ?? 0, items[0]?.rank ?? 2) };
  });
  return ranked.sort((a, b) => a.rank - b.rank || a.position - b.position).map(entry => entry.group);
}
