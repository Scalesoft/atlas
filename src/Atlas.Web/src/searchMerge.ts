/**
 * Merges the list of the full search pass into the list the prefix pass already showed for the same query (omnisearch,
 * spec 7.1 point 1). The row under the cursor (the preselected first one too) holds its position: what stood above it stays above
 * it, in its new form, or as shown when the full pass left it out of its first page (both passes answer the same query, so it is
 * still a hit; the next pages skip it), and every new result goes below. Without a row under the cursor the list is the new ranking.
 */
export function mergeSearchResults<T>(shown: readonly T[], incoming: readonly T[], selectedKey: string | undefined, keyOf: (item: T) => string): T[] {
  const selectedAt = selectedKey === undefined ? -1 : shown.findIndex(item => keyOf(item) === selectedKey);
  if (selectedAt < 0) return [...incoming];
  const fresh = new Map(incoming.map(item => [keyOf(item), item] as const));
  const head = shown.slice(0, selectedAt + 1).map(item => fresh.get(keyOf(item)) ?? item);
  const placed = new Set(head.map(keyOf));
  return [...head, ...incoming.filter(item => !placed.has(keyOf(item)))];
}
