import type { Editor } from '@tiptap/core'
import { TextSelection } from '@tiptap/pm/state'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'

/** The side button and between-block control open the same slash picker. */
export function insertBlockAt(editor: Editor, position: number): boolean {
  if (editor.isDestroyed || !editor.isEditable || editor.view.composing
    || position < 0 || position > editor.state.doc.content.size) return false
  const paragraph = editor.schema.nodes.paragraph
  if (!paragraph) return false
  const $position = editor.state.doc.resolve(position)
  const node = paragraph.create(null, editor.schema.text('/'))
  if (!$position.parent.canReplaceWith($position.index(), $position.index(), paragraph)) return false
  const transaction = editor.state.tr.insert(position, node)
  transaction.setSelection(TextSelection.create(transaction.doc, position + 2))
  dispatchMarkdownHistoryOperation(editor, transaction.scrollIntoView())
  editor.view.focus()
  return true
}
