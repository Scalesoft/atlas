import type { Editor } from '@tiptap/core'

/** Moves are ordinary delete+insert edits, identical to cut/paste, in every binding (decision 2026-09-08).
 * See docs/collaborative-block-moves-and-review.md for the accepted concurrency trade-offs. */
export function canMoveBlocks(editor: Editor): boolean {
  return !editor.isDestroyed && editor.isEditable
}
