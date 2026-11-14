import type { EditorView } from "@tiptap/pm/view";

/** Where the page's content starts to show: below the sticky workspace bar, else the top of the window. */
export const visibleTop = (document: Document) => Math.max(0, document.querySelector(".workspace-bar")?.getBoundingClientRect().bottom ?? 0);

/**
 * The first top-level block of an editor's document that shows, as positions between top-level blocks: `from` before it, `to` after
 * it, and whether its top shows. Read from the document's own nodes, so a widget at the top (the automatic overview) is never taken
 * for a block. DocumentEditor's focus("visible") puts the caret into it (Selection.near(from)); the catalog panel from the page menu
 * inserts at visibleInsertionPoint.
 */
export function firstVisibleBlock(view: EditorView): { from: number; to: number; topShows: boolean } | undefined {
  const top = visibleTop(view.dom.ownerDocument);
  let found: { from: number; to: number; topShows: boolean } | undefined;
  view.state.doc.forEach((node, offset) => {
    if (found) return;
    const box = (view.nodeDOM(offset) as Element | null)?.getBoundingClientRect();
    if (box && box.bottom > top) found = { from: offset, to: offset + node.nodeSize, topShows: box.top >= top };
  });
  return found;
}

/**
 * A place for new top-level blocks where the reader is (W4-2): before the first block in view, or after it when its top is scrolled
 * away. Always between top-level blocks, so what is inserted there never lands inside a requirement or a list.
 */
export function visibleInsertionPoint(view: EditorView): number | undefined {
  const block = firstVisibleBlock(view);
  return block && (block.topShows ? block.from : block.to);
}
