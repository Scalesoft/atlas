import { repairLegacyValue, type MarkdownRichValue } from "@atlas/markdown/core";
import { readDocument } from "./document";
import { compareHistoryText } from "./historyDiff";
import { kindOfNode } from "./overview/overviewKinds";

/** One top-level node of a stored rich document (paragraph, heading, table, module…). */
export type HistoryBlock = { readonly type?: string; readonly [key: string]: unknown };
export type HistoryBlockSegment =
  | { kind: "same"; blocks: HistoryBlock[] }
  | { kind: "changed"; removed: HistoryBlock[]; added: HistoryBlock[] };
export interface HistoryBlockDiff {
  segments: HistoryBlockSegment[];
  /** Number of changed segments (the ↑/↓ stops). */
  changes: number;
  /** The comparison was too large for a block alignment: one replaced span between the common ends. */
  coarse: boolean;
  plugins: MarkdownRichValue["plugins"];
  /** Both revisions are readable rich documents (otherwise only the Markdown comparison is exact). */
  readable: boolean;
  /** Blocks that moved unchanged: the removed block at the old place and the added one at the new (Přesunuto there, nothing here). */
  moved: ReadonlySet<HistoryBlock>;
}

/** Top-level blocks of a stored revision, read as the editor opens it (the content repair: a history revision of before 3.6 holds its
 * disclosure's title in an attribute, the converted revision after it as a node, and the block is not changed); unreadable data compares
 * as empty, the source diff still shows it. */
export function historyBlocks(contentJson: string | null | undefined): { blocks: HistoryBlock[]; plugins: MarkdownRichValue["plugins"]; readable: boolean } {
  if (!contentJson) return { blocks: [], plugins: undefined, readable: false };
  try {
    const value = repairLegacyValue(readDocument(contentJson));
    const content = (value.document as { content?: unknown }).content;
    return { blocks: Array.isArray(content) ? (content as HistoryBlock[]) : [], plugins: value.plugins, readable: true };
  } catch {
    return { blocks: [], plugins: undefined, readable: false };
  }
}

/**
 * A node as the reader sees it, for comparing: a revision stored by an older editor differs from the same text saved by today's in
 * ways nobody can see, and those are no edit. Left out: an attribute that is empty (null, "") against one that is missing, an empty
 * `attrs` or `marks`, the order of keys, a heading's identity (`headingId`, `aliases`: the anchor, generated on the first save), and
 * the two defaults the requirement headings of 3.6 write into every heading and paragraph (`direction: "internal"`,
 * `dependencies: false`). Everything else stays: a state, a size, an alignment, a link's address, a chip's target.
 * shortcut: the defaults are listed here, not read from the editor schema (it is not loaded with the history); add one when a new
 * global attribute with a non-empty default appears.
 */
function comparable(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(comparable);
  if (!value || typeof value !== "object") return value;
  const node = value as Record<string, unknown>, heading = node.type === "heading", result: Record<string, unknown> = {};
  for (const key of Object.keys(node).sort()) {
    let item = node[key];
    if (key === "attrs" && item && typeof item === "object" && !Array.isArray(item)) {
      const kept = Object.entries(item).filter(([name, attribute]) => attribute != null && attribute !== ""
        && !(heading && (name === "headingId" || name === "aliases" || (name === "direction" && attribute === "internal")))
        && !(name === "dependencies" && attribute === false));
      if (!kept.length) continue;
      item = Object.fromEntries(kept.sort(([a], [b]) => (a < b ? -1 : 1)));
    } else if (key === "marks" && Array.isArray(item) && !item.length) continue;
    else item = comparable(item);
    result[key] = item;
  }
  return result;
}

/** What makes two blocks the same: what the reader sees of them (`comparable`), but an overview by its kind alone: its numbers are
 * computed from the page, so a revision stored with another id or the 3.3 summary block is the same overview, not a removed one. */
function blockKey(block: HistoryBlock) {
  const kind = kindOfNode(block.type, block.attrs as Record<string, unknown> | undefined);
  return kind === undefined ? JSON.stringify(comparable(block)) : `overview:${kind}`;
}

/**
 * Aligns two revisions by whole top-level blocks, so the change view can render each block as the
 * reader sees it (tables, callouts, diagrams) instead of its Markdown. Blocks are compared by what
 * the reader sees of their stored JSON (`comparable`).
 */
export function compareHistoryBlocks(before: string | null, after: string): HistoryBlockDiff {
  // A first revision compares with nothing: every block is new.
  const old = before === null ? { blocks: [], plugins: undefined, readable: true } : historyBlocks(before), next = historyBlocks(after);
  const a = old.blocks, b = next.blocks;
  const ka = a.map(blockKey), kb = b.map(blockKey);
  let start = 0, end = 0;
  while (start < a.length && start < b.length && ka[start] === kb[start]) start++;
  while (end < a.length - start && end < b.length - start && ka[a.length - 1 - end] === kb[b.length - 1 - end]) end++;
  const segments: HistoryBlockSegment[] = [];
  const same = (blocks: HistoryBlock[]) => {
    if (!blocks.length) return;
    const last = segments.at(-1);
    if (last?.kind === "same") last.blocks.push(...blocks);
    else segments.push({ kind: "same", blocks: [...blocks] });
  };
  const changed = (removed: HistoryBlock[], added: HistoryBlock[]) => {
    if (!removed.length && !added.length) return;
    const last = segments.at(-1);
    if (last?.kind === "changed") { last.removed.push(...removed); last.added.push(...added); }
    else segments.push({ kind: "changed", removed: [...removed], added: [...added] });
  };
  same(a.slice(0, start));
  const left = a.slice(start, a.length - end), right = b.slice(start, b.length - end);
  const lk = ka.slice(start, a.length - end), rk = kb.slice(start, b.length - end);
  // Pages have hundreds of blocks, not thousands; beyond this the middle is one replaced span.
  const coarse = (left.length + 1) * (right.length + 1) > 1_000_000;
  if (coarse) changed(left, right);
  else {
    const width = right.length + 1;
    const lengths = new Uint32Array((left.length + 1) * width);
    for (let i = left.length - 1; i >= 0; i--)
      for (let j = right.length - 1; j >= 0; j--)
        lengths[i * width + j] = lk[i] === rk[j] ? lengths[(i + 1) * width + j + 1]! + 1 : Math.max(lengths[(i + 1) * width + j]!, lengths[i * width + j + 1]!);
    let i = 0, j = 0;
    while (i < left.length || j < right.length) {
      if (i < left.length && j < right.length && lk[i] === rk[j]) { same([left[i++]!]); j++; }
      else if (i < left.length && (j === right.length || lengths[(i + 1) * width + j]! >= lengths[i * width + j + 1]!)) changed([left[i++]!], []);
      else changed([], [right[j++]!]);
    }
  }
  same(end ? a.slice(a.length - end) : []);
  const moved = coarse ? new Set<HistoryBlock>() : movedBlocks(segments);
  // A span that only lost blocks which moved elsewhere has nothing to show: the block reads as moved where it went.
  const changes = segments.filter((segment) => segment.kind === "changed" && !onlyMovedAway(segment, moved)).length;
  return { segments, changes, coarse, plugins: next.plugins ?? old.plugins, readable: old.readable && next.readable, moved };
}

/** Removed and added blocks with the same key, paired once each in document order (a coarse span is not paired: its kept blocks are in it). */
function movedBlocks(segments: HistoryBlockSegment[]) {
  const removed = new Map<string, HistoryBlock[]>(), moved = new Set<HistoryBlock>();
  for (const segment of segments)
    if (segment.kind === "changed")
      for (const block of segment.removed) {
        const key = blockKey(block);
        if (!isBlankBlock(block)) removed.set(key, [...(removed.get(key) ?? []), block]);
      }
  for (const segment of segments)
    if (segment.kind === "changed")
      for (const block of segment.added) {
        const from = isBlankBlock(block) ? undefined : removed.get(blockKey(block))?.shift();
        if (from) { moved.add(from); moved.add(block); }
      }
  return moved;
}
export function onlyMovedAway(segment: HistoryBlockSegment, moved: ReadonlySet<HistoryBlock>) {
  return segment.kind === "changed" && !segment.added.length && segment.removed.every((block) => moved.has(block));
}

/** A node without its marks, adjacent texts joined: its structure, attributes and text. */
function unmarked(node: unknown): string {
  const value = node as { type?: string; text?: string; attrs?: unknown; content?: unknown[] };
  return value.type === "text" ? value.text ?? "" : `<${value.type} ${JSON.stringify(value.attrs ?? {})}>${(value.content ?? []).map(unmarked).join("")}</>`;
}
/** Two versions of a block that differ only in formatting (bold, italic, a link): the same once their marks are left out. */
export function formattingOnly(a: HistoryBlock, b: HistoryBlock) {
  return unmarked(a) === unmarked(b);
}

/** Character ranges of the text that a modified block lost (in `before`) and gained (in `after`). */
export function changedTextRanges(before: string, after: string) {
  const removed: [number, number][] = [], added: [number, number][] = [];
  let oldAt = 0, newAt = 0;
  for (const part of compareHistoryText(before, after).parts) {
    const length = part.text.length;
    // Whitespace-only changes carry no meaning for a reader and would highlight empty gaps.
    const visible = part.text.trim().length > 0;
    if (part.kind === "same") { oldAt += length; newAt += length; }
    else if (part.kind === "removed") { if (visible) removed.push([oldAt, oldAt + length]); oldAt += length; }
    else { if (visible) added.push([newAt, newAt + length]); newAt += length; }
  }
  return { removed, added };
}

/** An empty paragraph is spacing, not content: it neither anchors context nor forms a change of its own. */
export function isBlankBlock(block: HistoryBlock) {
  if (block.type !== "paragraph") return false;
  const content = block.content as { type?: string; text?: string }[] | undefined;
  return !content?.length || content.every((node) => node.type === "hardBreak" || (node.type === "text" && !node.text?.trim()));
}

function blockText(node: unknown): string {
  if (!node || typeof node !== "object") return "";
  const value = node as { text?: unknown; content?: unknown };
  return (typeof value.text === "string" ? value.text : "") + (Array.isArray(value.content) ? value.content.map(blockText).join(" ") : "");
}
function words(block: HistoryBlock) {
  return new Set(blockText(block).toLocaleLowerCase("cs").match(/[\p{L}\p{N}_]+/gu) ?? []);
}
/** Dice coefficient of the blocks' word sets: 1 for the same words, 0 for none shared. */
function similarity(a: Set<string>, b: Set<string>) {
  if (!a.size && !b.size) return 1;
  let shared = 0;
  for (const word of a) if (b.has(word)) shared++;
  return (2 * shared) / (a.size + b.size);
}

export type HistoryBlockChange =
  | { kind: "added" | "removed"; block: HistoryBlock }
  | { kind: "modified"; before: HistoryBlock; after: HistoryBlock };

// Below this share of common words two blocks read as a replacement, not an edit.
const EDIT_SIMILARITY = 0.4;

/**
 * Orders one changed span for reading: an edited block (same type, mostly the same words) becomes a
 * before/after pair, everything else is added or removed, all in document order. Pairs are the
 * order-preserving alignment with the highest total similarity, so a deleted paragraph next to an
 * edited one is not mistaken for its earlier wording.
 */
export function alignChangedBlocks(removed: HistoryBlock[], added: HistoryBlock[]): HistoryBlockChange[] {
  const n = removed.length, m = added.length;
  const pairs: [number, number][] = [];
  if (n && m && n * m <= 10_000) {
    const a = removed.map(words), b = added.map(words);
    const score = (i: number, j: number) => {
      if (removed[i]!.type !== added[j]!.type) return 0;
      const value = similarity(a[i]!, b[j]!);
      return value >= EDIT_SIMILARITY ? value : 0;
    };
    const width = m + 1;
    const best = new Float64Array((n + 1) * width);
    for (let i = n - 1; i >= 0; i--)
      for (let j = m - 1; j >= 0; j--) {
        const pair = score(i, j);
        best[i * width + j] = Math.max(pair ? pair + best[(i + 1) * width + j + 1]! : 0, best[(i + 1) * width + j]!, best[i * width + j + 1]!);
      }
    for (let i = 0, j = 0; i < n && j < m;) {
      const pair = score(i, j);
      if (pair && best[i * width + j] === pair + best[(i + 1) * width + j + 1]!) { pairs.push([i, j]); i++; j++; }
      else if (best[i * width + j] === best[(i + 1) * width + j]) i++;
      else j++;
    }
  }
  const result: HistoryBlockChange[] = [];
  let i = 0, j = 0;
  for (const [pi, pj] of [...pairs, [n, m] as [number, number]]) {
    while (i < pi) result.push({ kind: "removed", block: removed[i++]! });
    while (j < pj) result.push({ kind: "added", block: added[j++]! });
    if (pi < n && pj < m) { result.push({ kind: "modified", before: removed[i++]!, after: added[j++]! }); }
  }
  return result;
}
