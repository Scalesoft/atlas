import type { MarkdownRichValue } from "@atlas/markdown/core";

/**
 * One section of a page for a page card that targets it: the heading with the
 * stable id `anchor` (`heading-anchors`, attribute `headingId`) and what follows it up to the next heading of the same or a higher
 * level. Sections are siblings: a heading inside another block (a callout) neither ends a section around that block nor
 * reaches out of it; its own section is sliced inside its block. Null when the page has no heading with that id (renamed sections
 * keep their id; a deleted one is gone). The page's envelope is kept, so the slice renders like the page.
 */
export function sliceSection(value: MarkdownRichValue, anchor: string): MarkdownRichValue | null {
  if (!anchor) return null;
  const content = slice((value.document as Node).content ?? [], anchor);
  return content ? { ...value, document: { type: "doc", content: content as never } } : null;
}

interface Node { type?: string; attrs?: { level?: unknown; headingId?: unknown }; content?: readonly Node[] }
// A level left out of the stored form is the default 1.
const level = (node: Node) => (node.type === "heading" ? Number(node.attrs?.level ?? 1) : undefined);

/** The section of `anchor` among `siblings` or, depth first in document order, inside one of them. */
function slice(siblings: readonly Node[], anchor: string): Node[] | null {
  for (let index = 0; index < siblings.length; index++) {
    const node = siblings[index]!;
    const own = level(node);
    if (own !== undefined && node.attrs?.headingId === anchor) {
      const end = siblings.findIndex((next, at) => at > index && (level(next) ?? Infinity) <= own);
      return siblings.slice(index, end < 0 ? undefined : end);
    }
    const inner = node.content?.length ? slice(node.content, anchor) : null;
    if (inner) return inner;
  }
  return null;
}
