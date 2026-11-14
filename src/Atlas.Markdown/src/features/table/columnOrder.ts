import type { Editor } from '@tiptap/core'
import { Fragment, type Node } from '@tiptap/pm/model'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { canMoveBlocks } from '../drag/movePolicy'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'

export function columnOrder(width: number, from: number, to: number, count = 1): number[] {
  const order = Array.from({ length: width }, (_, index) => index)
  order.splice(to, 0, ...order.splice(from, count))
  return order
}

/** Reorder existing cell nodes, preserving every attribute and every row's identity.
 * A merged cell may move as a unit, but a single-column move must never split it. */
export function reorderedTable(table: Node, from: number, to: number, count = 1): Node | null {
  if (table.type.name !== 'table') return null
  const map = TableMap.get(table)
  if (map.problems?.length || !Number.isInteger(from) || !Number.isInteger(to)
    || !Number.isInteger(count) || count < 1 || from < 0 || to < 0
    || from + count > map.width || to + count > map.width || from === to) return null
  const order = columnOrder(map.width, from, to, count)
  const rank: number[] = []
  order.forEach((column, index) => { rank[column] = index })
  const rows: Node[] = []
  let valid = true
  table.forEach((row, rowOffset) => {
    const cells: Array<{ cell: Node; rank: number }> = []
    row.forEach((cell, cellOffset) => {
      const rect = map.findCell(rowOffset + cellOffset + 1)
      if (rect.left < from + count && rect.right > from && (rect.left < from || rect.right > from + count)) valid = false
      for (let col = rect.left + 1; col < rect.right; col++) {
        if (rank[col] !== rank[col - 1] + 1) valid = false
      }
      cells.push({ cell, rank: rank[rect.left] })
    })
    rows.push(row.copy(Fragment.fromArray(cells.sort((a, b) => a.rank - b.rank).map(item => item.cell))))
  })
  return valid ? table.copy(Fragment.fromArray(rows)) : null
}

export function moveColumn(editor: Editor, tablePos: number, from: number, to: number, count = 1): boolean {
  if (!canMoveBlocks(editor)) return false
  const table = editor.state.doc.nodeAt(tablePos)
  const next = table && reorderedTable(table, from, to, count)
  if (!table || !next) return false
  const tr = closeMarkdownHistory(editor.state.tr).replaceWith(tablePos, tablePos + table.nodeSize, next)
  const map = TableMap.get(next)
  tr.setSelection(CellSelection.colSelection(tr.doc.resolve(tablePos + 1 + map.map[to]), tr.doc.resolve(tablePos + 1 + map.map[to + count - 1])))
  dispatchMarkdownHistoryOperation(editor, tr.scrollIntoView())
  return true
}
