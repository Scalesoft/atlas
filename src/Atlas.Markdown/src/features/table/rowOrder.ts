import type { Command, Editor } from '@tiptap/core'
import { Fragment, type Node } from '@tiptap/pm/model'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { closeHistory } from '@tiptap/pm/history'
import { canMoveBlocks } from '../drag/movePolicy'
import { columnOrder } from './columnOrder'
import { markdownStrings } from '../../ui/strings'
import { closeMarkdownHistory, dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'

/** Vertical spans join rows into a unit that a single-row operation cannot reorder. */
function independentRows(table: Node): boolean {
  if (table.type.name !== 'table' || TableMap.get(table).problems?.length) return false
  let valid = true
  table.forEach(row => row.forEach(cell => { if (cell.attrs.rowspan !== 1) valid = false }))
  return valid
}

function headerCount(table: Node): number {
  let count = 0
  table.forEach((row, _, index) => {
    let header = true
    row.forEach(cell => { if (cell.type.spec.tableRole !== 'header_cell') header = false })
    if (index === count && header) count++
  })
  return count
}

export function reorderedRows(table: Node, from: number, to: number, count = 1): Node | null {
  if (table.type.name !== 'table' || TableMap.get(table).problems?.length || !Number.isInteger(from) || !Number.isInteger(to)
    || !Number.isInteger(count) || count < 1
    || from < headerCount(table) || to < headerCount(table)
    || from + count > table.childCount || to + count > table.childCount || from === to) return null
  const order = columnOrder(table.childCount, from, to, count)
  const map = TableMap.get(table)
  let valid = true
  table.forEach((row, rowOffset) => row.forEach((_cell, cellOffset) => {
    const rect = map.findCell(rowOffset + cellOffset + 1)
    if (rect.top < from + count && rect.bottom > from && (rect.top < from || rect.bottom > from + count)) valid = false
    for (let row = rect.top + 1; row < rect.bottom; row++) {
      if (order.indexOf(row) !== order.indexOf(row - 1) + 1) valid = false
    }
  }))
  return valid ? table.copy(Fragment.fromArray(order.map(index => table.child(index)))) : null
}

export function moveRow(editor: Editor, tablePos: number, from: number, to: number, count = 1): boolean {
  if (!canMoveBlocks(editor)) return false
  const table = editor.state.doc.nodeAt(tablePos)
  const next = table && reorderedRows(table, from, to, count)
  if (!table || !next) return false
  const tr = closeMarkdownHistory(editor.state.tr).replaceWith(tablePos, tablePos + table.nodeSize, next)
  const map = TableMap.get(next)
  tr.setSelection(CellSelection.rowSelection(tr.doc.resolve(tablePos + 1 + map.map[to * map.width]), tr.doc.resolve(tablePos + 1 + map.map[(to + count - 1) * map.width])))
  dispatchMarkdownHistoryOperation(editor, tr.scrollIntoView())
  return true
}

export type SortDirection = 'ascending' | 'descending'
function numeric(text: string): number | null {
  const normalized = text.replace(/[\s\u00a0\u202f]/g, '').replace(',', '.')
  return /^[+-]?\d+(?:\.\d+)?$/.test(normalized) && Number.isFinite(Number(normalized)) ? Number(normalized) : null
}

/** Stable sort: empty cells last in both directions; no guessing dates or currencies. */
export function sortedRows(table: Node, column: number, direction: SortDirection): Node | null {
  if (!independentRows(table) || !['ascending', 'descending'].includes(direction)) return null
  const map = TableMap.get(table)
  if (!Number.isInteger(column) || column < 0 || column >= map.width) return null
  const headers = headerCount(table)
  const rows = Array.from({ length: table.childCount - headers }, (_, index) => {
    const row = index + headers
    return { node: table.child(row), text: table.nodeAt(map.map[row * map.width + column])!.textContent.trim(), index }
  })
  const collator = new Intl.Collator(markdownStrings.locale, { numeric: true, sensitivity: 'base' })
  const ordered = rows.slice().sort((a, b) => {
    if (!a.text || !b.text) return Number(!a.text) - Number(!b.text) || a.index - b.index
    const x = numeric(a.text), y = numeric(b.text)
    const comparison = x !== null && y !== null ? x - y
      : x !== null ? -1 : y !== null ? 1 : collator.compare(a.text, b.text)
    return comparison * (direction === 'ascending' ? 1 : -1) || a.index - b.index
  })
  if (ordered.every((row, index) => row.index === index)) return null
  return table.copy(Fragment.fromArray([
    ...Array.from({ length: headers }, (_, index) => table.child(index)), ...ordered.map(row => row.node),
  ]))
}

/** Query and dispatch use the same transaction, including public command dry runs. */
export function sortSelectedColumn(editor: Editor, direction: SortDirection): Command {
  return ({ tr, dispatch }) => {
    if (!canMoveBlocks(editor)) return false
    const selection = tr.selection
    if (!(selection instanceof CellSelection) || !selection.isColSelection()) return false
    const table = selection.$anchorCell.node(-1)
    const tablePos = selection.$anchorCell.before(selection.$anchorCell.depth - 1)
    const map = TableMap.get(table)
    const start = selection.$anchorCell.start(-1)
    const anchor = map.findCell(selection.$anchorCell.pos - start)
    const head = map.findCell(selection.$headCell.pos - start)
    if (anchor.left !== head.left || anchor.right - anchor.left !== 1 || head.right - head.left !== 1) return false
    const next = sortedRows(table, anchor.left, direction)
    if (!next) return false
    if (dispatch) {
      closeHistory(tr).replaceWith(tablePos, tablePos + table.nodeSize, next)
      const nextMap = TableMap.get(next)
      tr.setSelection(CellSelection.colSelection(tr.doc.resolve(tablePos + 1 + nextMap.map[anchor.left])))
      tr.scrollIntoView()
    }
    return true
  }
}
