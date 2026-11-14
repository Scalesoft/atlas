import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { detectSecrets } from "./rules";

/** POST /api/assist/page-order accepts at most this many characters of context. */
export const sectionContextLimit = 2_000;

const leafText = (leaf: ProseMirrorNode) => leaf.type.name === "hardBreak" ? "\n" : "";
const headingLevel = (node: ProseMirrorNode) => node.type.name === "heading" ? Number(node.attrs.level) || 1 : 0;

/**
 * E4 (plan §19): the text of the section around the caret, for ordering the `[[` list by what the author writes about.
 * The section runs from the nearest heading at or before the caret's block to the next heading of the same or a higher
 * level (without a heading before the caret: from the start to the first heading). A block with a detected password is
 * left out entirely, inline components contribute nothing, and the result keeps the blocks nearest to the caret within
 * 2 000 characters (a single longer block is cut around the caret). `null` when there is nothing to compare.
 */
export function sectionText(doc: ProseMirrorNode, head: number, limit = sectionContextLimit): string | null {
  const blocks: { node: ProseMirrorNode; offset: number }[] = [];
  doc.forEach((node, offset) => { blocks.push({ node, offset }); });
  if (!blocks.length) return null;
  let caret = blocks.findIndex(({ node, offset }) => head >= offset && head <= offset + node.nodeSize);
  if (caret < 0) caret = blocks.length - 1;

  let start = caret;
  while (start > 0 && !headingLevel(blocks[start]!.node)) start--;
  const level = headingLevel(blocks[start]!.node);
  if (!level) start = 0;
  let end = caret + 1;
  while (end < blocks.length && !(headingLevel(blocks[end]!.node) && (!level || headingLevel(blocks[end]!.node) <= level))) end++;

  const texts = blocks.slice(start, end).map(({ node, offset }, index) => ({
    at: start + index,
    offset,
    text: node.textBetween(0, node.content.size, "\n", leafText).trim(),
  })).filter(block => block.text && !detectSecrets(block.text).length);
  if (!texts.length) return null;

  // The caret's block, or the nearest kept one when it was left out.
  let middle = texts.findIndex(block => block.at >= caret);
  if (middle < 0) middle = texts.length - 1;
  const own = texts[middle]!;
  if (own.text.length > limit) {
    const at = Math.max(0, Math.min(own.text.length - limit, head - own.offset - 1 - limit / 2));
    return own.text.slice(at, at + limit);
  }
  const separator = 2;
  let size = own.text.length;
  let before = middle - 1;
  let after = middle + 1;
  let first = middle;
  let last = middle;
  for (let open = true; open;) {
    open = false;
    const previous = texts[before];
    if (previous && size + separator + previous.text.length <= limit) { size += separator + previous.text.length; first = before--; open = true; }
    else before = -1;
    const next = texts[after];
    if (next && size + separator + next.text.length <= limit) { size += separator + next.text.length; last = after++; open = true; }
    else after = texts.length;
  }
  return texts.slice(first, last + 1).map(block => block.text).join("\n\n");
}
