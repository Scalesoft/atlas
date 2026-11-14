import type { Command } from '@tiptap/core'
import { CellSelection } from '@tiptap/pm/tables'
import type { EditorState } from '@tiptap/pm/state'

/** A mixed cell selection has no single active value. */
export function selectedCellsHaveAttribute(state: EditorState, attribute: string, value: unknown): boolean {
  const selection = state.selection
  if (selection instanceof CellSelection) {
    let count = 0
    let matches = true
    selection.forEachCell(cell => { count++; matches &&= (cell.attrs[attribute] ?? null) === value })
    return count > 0 && matches
  }
  for (let depth = selection.$from.depth; depth > 0; depth--) {
    const node = selection.$from.node(depth)
    if (['tableCell', 'tableHeader'].includes(node.type.name)) return (node.attrs[attribute] ?? null) === value
  }
  return false
}

/** Clear contents in one transaction while preserving cell geometry and formatting. */
export const clearCellContent: Command = ({ tr, dispatch }) => {
  const cells: Array<{ pos: number; size: number }> = []
  const selection = tr.selection
  if (selection instanceof CellSelection) {
    selection.forEachCell((cell, pos) => cells.push({ pos, size: cell.nodeSize }))
  } else {
    const { $from } = selection
    for (let depth = $from.depth; depth > 0; depth--) {
      if (['tableCell', 'tableHeader'].includes($from.node(depth).type.name)) {
        cells.push({ pos: $from.before(depth), size: $from.node(depth).nodeSize })
        break
      }
    }
  }
  if (!cells.length) return false
  if (dispatch) {
    for (const cell of cells.sort((a, b) => b.pos - a.pos)) {
      tr.replaceWith(cell.pos + 1, cell.pos + cell.size - 1, tr.doc.type.schema.nodes.paragraph.create())
    }
  }
  return true
}
