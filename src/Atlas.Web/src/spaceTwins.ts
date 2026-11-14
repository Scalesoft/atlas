/**
 * What tells same-named spaces apart when everything else matches (palette, Koš prostorů): the description when it is unique among
 * the twins, otherwise their order, "2. z 3". `undefined` for a space that has no twin. `key` groups the twins (name and whatever else shows).
 */
export function tellTwins<T>(items: readonly T[], key: (item: T) => string, blurb: (item: T) => string | undefined): Array<string | undefined> {
  const clip = (item: T) => blurb(item)?.trim() ?? "";
  const size = new Map<string, number>(), seen = new Map<string, number>(), blurbs = new Map<string, number>();
  for (const item of items) {
    const group = key(item);
    size.set(group, (size.get(group) ?? 0) + 1);
    blurbs.set(`${group}|${clip(item)}`, (blurbs.get(`${group}|${clip(item)}`) ?? 0) + 1);
  }
  return items.map(item => {
    const group = key(item), total = size.get(group)!, order = (seen.set(group, (seen.get(group) ?? 0) + 1), seen.get(group)!);
    if (total < 2) return undefined;
    const text = clip(item);
    return text && blurbs.get(`${group}|${text}`) === 1 ? (text.length > 32 ? `${text.slice(0, 31)}…` : text) : `${order}. z ${total}`;
  });
}
