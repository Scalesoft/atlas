import { Extension } from '@tiptap/core'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'

/**
 * Backspace right after a chip and Delete right before it select the chip first; the next press deletes it (the editor's own
 * delete of a selected node, one undo step). A chip is never lost to a single stray key. `names` are the inline atom node types.
 */
export function markdownChipDeleteKeys(id: string, names: readonly string[]): Extension {
  const select = (direction: -1 | 1) => ({ editor }: { editor: import('@tiptap/core').Editor }) => {
    const { selection } = editor.state
    if (!editor.isEditable || !selection.empty) return false
    const node = direction < 0 ? selection.$from.nodeBefore : selection.$from.nodeAfter
    if (!node || !names.includes(node.type.name)) return false
    const at = direction < 0 ? selection.from - node.nodeSize : selection.from
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, at)).scrollIntoView())
    return true
  }
  return Extension.create({ name: `${id}ChipDeleteKeys`, addKeyboardShortcuts: () => ({ Backspace: select(-1), Delete: select(1) }) })
}

/**
 * The keys of a focused chip that is the editor's selected node (the focus sits on its button, so the editor never sees them):
 * ←/→ put the caret beside the chip, Backspace/Delete remove it, one undo step. Returns the detach. Readers have no caret and do nothing.
 */
export function bindMarkdownChipKeys(button: HTMLElement, editor: import('@tiptap/core').Editor, getPos: () => number | undefined): () => void {
  const keydown = (event: KeyboardEvent) => {
    if (!editor.isEditable || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
    const position = getPos(), node = position === undefined ? null : editor.state.doc.nodeAt(position)
    if (position === undefined || !node || !(editor.state.selection instanceof NodeSelection) || editor.state.selection.from !== position) return
    const { tr } = editor.state
    if (event.key === 'Backspace' || event.key === 'Delete') tr.delete(position, position + node.nodeSize)
    else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') tr.setSelection(TextSelection.near(tr.doc.resolve(event.key === 'ArrowLeft' ? position : position + node.nodeSize), event.key === 'ArrowLeft' ? -1 : 1))
    else return
    event.preventDefault(); event.stopPropagation()
    editor.view.dispatch(tr.scrollIntoView()); editor.view.focus()
  }
  button.addEventListener('keydown', keydown)
  return () => button.removeEventListener('keydown', keydown)
}
