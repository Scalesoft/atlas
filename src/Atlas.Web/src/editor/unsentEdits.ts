import type { JSONContent } from "@tiptap/core";

/** The top-level blocks `other` changed against `base`: the base range [from, to) and what stands there now. Null when equal. */
function changeOf(base: string[], other: JSONContent[]) {
  const keys = other.map(block => JSON.stringify(block));
  let from = 0;
  while (from < base.length && from < keys.length && base[from] === keys[from]) from++;
  let tail = 0;
  while (tail < base.length - from && tail < keys.length - from && base[base.length - 1 - tail] === keys[keys.length - 1 - tail]) tail++;
  if (from === base.length - tail && from === keys.length - tail) return null;
  return { from, to: base.length - tail, blocks: other.slice(from, keys.length - tail) };
}

/**
 * A shared session that ended with edits the server never took (`[SUPERSEDED]`: the page started again from its revision): the blocks
 * they changed against the session's last acknowledged document, put into the next session's document as text. What the server changed
 * elsewhere stays; a local change over a place the server changed too follows right after it, so nothing typed is lost.
 * Null when there is nothing to put back.
 */
export function reapplyUnsent(acknowledged: JSONContent, local: JSONContent, current: JSONContent): JSONContent | null {
  const base = acknowledged.content ?? [];
  const keys = base.map(block => JSON.stringify(block));
  const mine = changeOf(keys, local.content ?? []);
  if (!mine) return null;
  const theirs = changeOf(keys, current.content ?? []);
  let content: JSONContent[];
  if (!theirs) content = [...base.slice(0, mine.from), ...mine.blocks, ...base.slice(mine.to)];
  else if (mine.to <= theirs.from || theirs.to <= mine.from) {
    const [first, second] = mine.to <= theirs.from ? [mine, theirs] : [theirs, mine];
    content = [...base.slice(0, first.from), ...first.blocks, ...base.slice(first.to, second.from), ...second.blocks, ...base.slice(second.to)];
  } else {
    if (JSON.stringify(mine.blocks) === JSON.stringify(theirs.blocks)) return null;
    const after = theirs.from + theirs.blocks.length;
    content = [...(current.content ?? []).slice(0, after), ...mine.blocks, ...(current.content ?? []).slice(after)];
  }
  return JSON.stringify(content) === JSON.stringify(current.content ?? []) ? null : { ...current, content };
}
