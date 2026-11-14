/**
 * Merges the list of the full search pass into the list the prefix pass already showed (omnisearch,
 * spec 7.1 point 1). A row the person has selected holds its position: what stood above it stays above it
 * and every new result goes below. Without a selection the list is simply the new ranking.
 */
export function mergeSearchResults<T>(shown: readonly T[], incoming: readonly T[], selectedKey: string | undefined, keyOf: (item: T) => string): T[] {
  if (!selectedKey) return [...incoming];
  const selectedAt = shown.findIndex(item => keyOf(item) === selectedKey);
  const fresh = new Map(incoming.map(item => [keyOf(item), item] as const));
  if (selectedAt < 0 || !fresh.has(selectedKey)) return [...incoming];
  // Rows up to and including the selected one keep their order (as far as they still exist), in their new form.
  const head = shown.slice(0, selectedAt + 1).map(item => fresh.get(keyOf(item))).filter((item): item is T => item !== undefined);
  const placed = new Set(head.map(keyOf));
  return [...head, ...incoming.filter(item => !placed.has(keyOf(item)))];
}
