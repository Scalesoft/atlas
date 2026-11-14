import type { Node as PMNode } from '@tiptap/pm/model'
import type { EditorState, Transaction } from '@tiptap/pm/state'

/** What ends a typed word: a space or a punctuation mark. */
export const RECOGNITION_ENDS = /[\s.,;:!?)\]}“”"'»…–—]/u
/** The most text before the caret a keystroke reads (P17). */
export const RECOGNITION_WINDOW = 200

/** The window of a keystroke: the block's text before the caret, at most RECOGNITION_WINDOW characters, starting after a space. */
export function recognitionWindow(text: string, offset: number): { start: number; text: string } {
  let start = Math.max(0, offset - RECOGNITION_WINDOW)
  if (start) { const space = text.slice(start, offset).search(/\s/u); start = space < 0 ? offset : start + space }
  return { start, text: text.slice(start, offset) }
}

/** Whether someone else made the change (a collaborator's edit or undo, applied by y-tiptap): nothing is recognised in it. */
export const isRemoteTransaction = (transaction: Transaction): boolean =>
  (transaction.getMeta('y-sync$') as { isChangeOrigin?: boolean } | undefined)?.isChangeOrigin === true

/** Marks whose text is not prose (code, a link's label). */
const SKIPPED_MARKS = new Set(['code', 'link'])
/** Blocks whose words count: prose, not code nor a heading (a heading names a section, not the people in it). */
export const isProseBlock = (node: PMNode): boolean => node.isTextblock && !node.type.spec.code && node.type.name !== 'heading'
/** A block's text with every position kept: inline nodes, code and links are U+FFFC, so no name spans them. */
export function proseText(block: PMNode): string {
  let text = ''
  block.forEach(child => { text += child.isText && !child.marks.some(mark => SKIPPED_MARKS.has(mark.type.name)) ? child.text! : '￼'.repeat(child.nodeSize) })
  return text
}

/**
 * The gate of a keystroke (audit-engine §4.1–2), O(1) until it passes: a local, typed (no composition, paste nor drop) single character
 * at an empty caret that ends a word, in prose. Then the window: the block's text before the caret, at most RECOGNITION_WINDOW long,
 * `start` its document position. Never the rest of the document.
 */
export function typedTrigger(transactions: readonly Transaction[], old: EditorState, state: EditorState): { start: number; caret: number; text: string } | null {
  if (!transactions.some(tr => tr.docChanged)) return null
  if (transactions.some(tr => isRemoteTransaction(tr) || tr.getMeta('composition') || tr.getMeta('uiEvent') === 'paste' || tr.getMeta('uiEvent') === 'drop')) return null
  const { selection } = state
  if (!selection.empty || !old.selection.empty || selection.from !== old.selection.from + 1 || state.doc.content.size !== old.doc.content.size + 1) return null
  const $caret = selection.$from
  if (!isProseBlock($caret.parent) || !RECOGNITION_ENDS.test(state.doc.textBetween(selection.from - 1, selection.from, '￼', '￼'))) return null
  const window = recognitionWindow(proseText($caret.parent), $caret.parentOffset)
  return { start: $caret.start() + window.start, caret: selection.from, text: window.text }
}
