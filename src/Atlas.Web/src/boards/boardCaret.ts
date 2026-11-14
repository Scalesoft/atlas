import type { Editor } from "@tiptap/core";
import { TextSelection } from "@tiptap/pm/state";

/** Where a person aimed the editing of a board text or block (a double-click: that word; a click into a selected card: there). */
export interface EditPoint { readonly x: number; readonly y: number; readonly word?: boolean }

const wordCharacter = /[\p{L}\p{N}_]/u;

/**
 * The caret of an editing that starts: at the point aimed at, the word under it for a
 * double-click, else at the end of the text (`block`: of the document's first block, the board block's own, not the paragraph the
 * editor keeps after it). A caret at the end of a link does not extend the link: what is typed next is plain text.
 */
export function placeCaret(editor: Editor, at?: EditPoint, { block = false }: { block?: boolean } = {}): boolean {
  let found: { pos: number } | null | undefined;
  // A view without layout (detached, jsdom) has no place under a point: the caret goes to the end.
  try { found = at && editor.view.posAtCoords({ left: at.x, top: at.y }); } catch { found = undefined; }
  if (!found || !at) {
    const end = block && editor.state.doc.firstChild ? editor.state.doc.firstChild.nodeSize - 1 : editor.state.doc.content.size;
    const placed = editor.chain().command(({ tr }) => { tr.setSelection(TextSelection.near(tr.doc.resolve(end), -1)); return true })
      .focus(undefined, { scrollIntoView: false }).run();
    const { $from, empty } = editor.state.selection;
    const link = editor.schema.marks.link;
    if (placed && empty && link && link.isInSet($from.marks())) editor.view.dispatch(editor.state.tr.setStoredMarks($from.marks().filter(mark => mark.type !== link)));
    return placed;
  }
  let from = found.pos, to = found.pos;
  if (at.word) {
    const $pos = editor.state.doc.resolve(found.pos);
    // One character per inline leaf (a chip, an emoji): offsets in the text are offsets in the parent.
    const text = $pos.parent.textBetween(0, $pos.parent.content.size, undefined, "￼");
    let start = $pos.parentOffset, end = start;
    while (start > 0 && wordCharacter.test(text[start - 1]!)) start--;
    while (end < text.length && wordCharacter.test(text[end]!)) end++;
    from = $pos.start() + start;
    to = $pos.start() + end;
  }
  return editor.chain().setTextSelection({ from, to }).focus(undefined, { scrollIntoView: false }).run();
}
