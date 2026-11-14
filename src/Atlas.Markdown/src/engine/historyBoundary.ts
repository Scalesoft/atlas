import { Plugin, type Transaction } from '@tiptap/pm/state'
import { ReplaceStep } from '@tiptap/pm/transform'
import { Extension, type Editor } from '@tiptap/core'
import { closeHistory } from '@tiptap/pm/history'

export const markdownHistoryBoundary = 'markdownHistoryBoundary'

/** Mark an explicit user operation for both local history and optional adapters. */
export function closeMarkdownHistory(transaction: Transaction): Transaction {
  return closeHistory(transaction).setMeta(markdownHistoryBoundary, true)
}

/** Commit a discrete UI action independently from both earlier and later typing. */
export function dispatchMarkdownHistoryOperation(editor: Editor, transaction: Transaction): void {
  editor.view.dispatch(closeMarkdownHistory(transaction))
  // closeHistory marks the beginning of an event; a separate empty transaction
  // closes its end too. The marker also tells collaborative history adapters to
  // stop capturing, without importing their implementation into the core.
  if (!editor.isDestroyed) editor.view.dispatch(closeMarkdownHistory(editor.state.tr).setMeta('addToHistory', false))
}

/** A letter or digit followed by whitespace or sentence punctuation: the end of a word (a second space or ". " ends nothing new). */
const wordEnd = /[\p{L}\p{N}][\s.,;:!?…]$/u

/** Whether a change is typed text that ends a word: one inserted run of text, not a paste, drop or remote change. */
export function endsWord(transaction: Transaction): boolean {
  const step = transaction.steps.length === 1 ? transaction.steps[0] : undefined
  if (!(step instanceof ReplaceStep) || step.slice.openStart || !step.slice.content.firstChild?.isText) return false
  if (transaction.getMeta('uiEvent') || transaction.getMeta('y-sync$')) return false
  const $end = transaction.doc.resolve(step.from + step.slice.size)
  return wordEnd.test($end.parent.textBetween(Math.max(0, $end.parentOffset - 2), $end.parentOffset, undefined, '￼'))
}

/**
 * Typing ends an undo step at the end of a word, so Ctrl+Z removes the last word rather than a time slice; the 500 ms grouping
 * stays the fallback inside a word. Closing through the shared marker ends the step in local history and in Yjs alike.
 */
export const WordHistoryBoundary = Extension.create({
  name: 'markdownWordHistoryBoundary',
  // Last: a change another plugin appends to the typed space (autolink) still joins its step.
  priority: 0,
  addProseMirrorPlugins: () => [new Plugin({
    appendTransaction: (transactions, _old, state) => transactions.some(endsWord) ? closeMarkdownHistory(state.tr).setMeta('addToHistory', false) : null,
  })],
})
