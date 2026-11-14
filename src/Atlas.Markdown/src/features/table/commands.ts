import type { Editor } from '@tiptap/core'
import type { EditorState, Transaction } from '@tiptap/pm/state'
import type { Node } from '@tiptap/pm/model'
import type { EditorView } from '@tiptap/pm/view'
import { CellSelection, TableMap } from '@tiptap/pm/tables'

export type TableAxisCommand = 'delete' | 'insertBefore' | 'insertAfter' | 'select'
export type TableAxis = 'row' | 'column'

/** Only a complete axis selection owns a group move; ordinary cell text never does. */
export function tableAxisRange(state: EditorState, tablePos: number, axis: TableAxis, index: number) {
  const selected = selectedTableAxisRange(state, tablePos, axis)
  return selected && index >= selected.from && index < selected.from + selected.count ? selected : { from: index, count: 1 }
}

export function selectedTableAxisRange(state: EditorState, tablePos: number, axis: TableAxis): { from: number; count: number } | null {
  const selection = state.selection
  if (selection instanceof CellSelection && selection.$anchorCell.start(-1) === tablePos + 1
    && (axis === 'row' ? selection.isRowSelection() : selection.isColSelection())) {
    const map = TableMap.get(selection.$anchorCell.node(-1))
    const rect = map.rectBetween(selection.$anchorCell.pos - tablePos - 1, selection.$headCell.pos - tablePos - 1)
    const from = axis === 'row' ? rect.top : rect.left
    const end = axis === 'row' ? rect.bottom : rect.right
    return { from, count: end - from }
  }
  return null
}

/** Add width normalization to the structural edit's existing transaction. */
export function resetTableColumnWidths(tr: Transaction, tablePos: number): void {
  const table = tr.doc.nodeAt(tablePos)
  if (table?.type.name !== 'table') return
  table.descendants((node, pos) => {
    if ((node.type.name === 'tableCell' || node.type.name === 'tableHeader') && node.attrs.colwidth) {
      tr.setNodeAttribute(tablePos + 1 + pos, 'colwidth', null)
    }
  })
}

export function resetSelectedTableColumnWidths(tr: Transaction): void {
  const { $from } = tr.selection
  for (let depth = $from.depth; depth > 0; depth--) {
    if ($from.node(depth).type.name === 'table') {
      resetTableColumnWidths(tr, $from.before(depth))
      return
    }
  }
}

/**
 * Selection and structural operation share a transaction, so focus cannot race them.
 * Merged cells follow ProseMirror's whole-span row/column selection semantics.
 */
export function runTableAxisCommand(editor: Editor, tablePos: number, axis: TableAxis, index: number, command: TableAxisCommand, extend = false): boolean {
  if (!editor.isEditable || editor.isDestroyed) return false
  const table = editor.state.doc.nodeAt(tablePos)
  if (table?.type.name !== 'table') return false
  const map = TableMap.get(table)
  if (index < 0 || index >= (axis === 'column' ? map.width : map.height)) return false
  const cellPos = tablePos + 1 + map.map[axis === 'column' ? index : index * map.width]
  const selection = editor.state.selection
  const extending = extend && selection instanceof CellSelection && selection.$anchorCell.start(-1) === tablePos + 1
    && (axis === 'row' ? selection.isRowSelection() : selection.isColSelection())
  const group = tableAxisRange(editor.state, tablePos, axis, index)
  return editor.chain()
    .command(({ tr, dispatch }) => {
      if (dispatch) {
        const $cell = tr.doc.resolve(cellPos)
        if (command !== 'select' && group.count > 1) return true
        const $anchor = extending ? selection.$anchorCell : $cell
        tr.setSelection(axis === 'column' ? CellSelection.colSelection($anchor, $cell) : CellSelection.rowSelection($anchor, $cell))
      }
      return true
    })
    .command(({ tr, dispatch, commands }) => {
      if (command === 'select') return true
      const operations = axis === 'column'
        ? { delete: commands.deleteColumn, insertBefore: commands.addColumnBefore, insertAfter: commands.addColumnAfter }
        : { delete: commands.deleteRow, insertBefore: commands.addRowBefore, insertAfter: commands.addRowAfter }
      const success = operations[command]()
      if (success && dispatch && axis === 'column') resetTableColumnWidths(tr, tablePos)
      return success
    })
    .run()
}

export function findTablePosition(doc: Node, table: HTMLTableElement, view: EditorView): number {
  let position = -1
  doc.descendants((node, pos) => {
    if (position >= 0) return false
    if (node.type.name !== 'table') return true
    const dom = view.nodeDOM(pos)
    if (dom instanceof HTMLElement && (dom === table || dom.contains(table))) {
      position = pos
      return false
    }
    return true
  })
  return position
}
