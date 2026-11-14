// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor, Extension } from '@tiptap/core'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { createMarkdownKit } from '../../createMarkdownKit'
import { dispatchCommand, queryCommand } from '../../core/commands'
import { moveRow, reorderedRows, sortedRows } from './rowOrder'
import { runTableAxisCommand, tableAxisRange } from './commands'

let editor: Editor
afterEach(() => editor?.destroy())
function mount(values = ['10', '-2', '2,5', '', '2', '2'], collaborative = false) {
  editor = new Editor({ extensions: [...createMarkdownKit({ profile: 'rich' }).extensions,
    ...(collaborative ? [Extension.create({ name: 'collaboration' })] : [])],
  content: '| Key | Identity |\n| --- | --- |\n' + values.map((value, i) => `| ${value} | **row${i}** |`).join('\n') })
  return editor.state.doc.firstChild!
}
function selectColumn(column = 0) {
  const map = TableMap.get(editor.state.doc.firstChild!)
  editor.view.dispatch(editor.state.tr.setSelection(CellSelection.colSelection(editor.state.doc.resolve(1 + map.map[column]))))
}
const asc = { type: 'sort-table', direction: 'ascending' } as const
const desc = { type: 'sort-table', direction: 'descending' } as const

it('Shift selection preserves two rows and their selection through repeated moves and undo', () => {
  mount(['A', 'B', 'C', 'D'])
  runTableAxisCommand(editor, 0, 'row', 1, 'select')
  runTableAxisCommand(editor, 0, 'row', 2, 'select', true)
  const before = editor.getJSON()
  expect(tableAxisRange(editor.state, 0, 'row', 2)).toEqual({ from: 1, count: 2 })
  expect(moveRow(editor, 0, 1, 3, 2)).toBe(true)
  const values = () => Array.from({ length: 4 }, (_, i) => editor.state.doc.firstChild!.child(i + 1).firstChild!.textContent)
  expect(values()).toEqual(['C', 'D', 'A', 'B'])
  expect(tableAxisRange(editor.state, 0, 'row', 4)).toEqual({ from: 3, count: 2 })
  expect(moveRow(editor, 0, 3, 2, 2)).toBe(true)
  editor.commands.undo()
  expect(values()).toEqual(['C', 'D', 'A', 'B'])
  editor.commands.undo()
  expect(editor.getJSON()).toEqual(before)
})

it('a complete vertical merge can travel with its row group, but never split', () => {
  const json = mount(['A', 'B', 'C']).toJSON()
  json.content![1].content![0].attrs = { ...json.content![1].content![0].attrs, rowspan: 2 }
  json.content![2].content!.splice(0, 1)
  const merged = editor.schema.nodeFromJSON(json)
  expect(reorderedRows(merged, 1, 2, 2)).not.toBeNull()
  expect(TableMap.get(reorderedRows(merged, 1, 2, 2)!).problems).toBeNull()
  expect(reorderedRows(merged, 2, 1, 2)).toBeNull()
})

it('moves complete original rows with marks and attributes, selects the destination and undoes once', () => {
  mount()
  let pos = 1
  editor.state.doc.firstChild!.forEach((row, offset) => { if (row.textContent.includes('row0')) pos = offset + 1 })
  editor.view.dispatch(editor.state.tr.setNodeAttribute(pos, 'height', 80))
  const before = editor.state.doc
  expect(moveRow(editor, 0, 1, 4)).toBe(true)
  expect(editor.state.doc.firstChild!.child(4).eq(before.firstChild!.child(1))).toBe(true)
  expect((editor.state.selection as CellSelection).isRowSelection()).toBe(true)
  expect(TableMap.get(editor.state.doc.firstChild!).problems).toBeNull()
  editor.commands.undo()
  expect(editor.state.doc.eq(before)).toBe(true)
  editor.commands.redo()
  expect(editor.state.doc.firstChild!.child(4).eq(before.firstChild!.child(1))).toBe(true)
})

it.each([[-1, 1], [1, -1], [0, 2], [2, 0], [1, 99], [99, 1], [1, 1], [1.2, 2]])('rejects unsafe row move %s → %s', (from, to) => {
  expect(reorderedRows(mount(), from, to)).toBeNull()
})

it('sorts numbers naturally, keeps ties stable and empties last in either direction', () => {
  const table = mount()
  const ascending = sortedRows(table, 0, 'ascending')!
  const descending = sortedRows(table, 0, 'descending')!
  const identities = (node: typeof table) => Array.from({ length: node.childCount - 1 }, (_, i) => node.child(i + 1).child(1).textContent)
  expect(identities(ascending)).toEqual(['row1', 'row4', 'row5', 'row2', 'row0', 'row3'])
  expect(identities(descending)).toEqual(['row0', 'row2', 'row4', 'row5', 'row1', 'row3'])
  expect(ascending.child(0)).toBe(table.child(0))
  expect(sortedRows(ascending, 0, 'ascending')).toBeNull()
})

it('sorts Czech words and natural alphanumeric identifiers', () => {
  const table = mount(['Žába', 'Čaj', 'Alice', 'item10', 'item2'])
  const next = sortedRows(table, 0, 'ascending')!
  expect(Array.from({ length: 5 }, (_, i) => next.child(i + 1).child(0).textContent)).toEqual(['Alice', 'Čaj', 'item2', 'item10', 'Žába'])
})

it('query is side-effect free; dispatch keeps column selection and sort is one undo step', () => {
  mount(); selectColumn()
  const before = editor.state.doc
  expect(queryCommand(editor, 'rich', asc).enabled).toBe(true)
  expect(editor.state.doc).toBe(before)
  expect(dispatchCommand(editor, 'rich', asc)).toBe(true)
  expect((editor.state.selection as CellSelection).isColSelection()).toBe(true)
  expect(queryCommand(editor, 'rich', asc).enabled).toBe(false)
  expect(queryCommand(editor, 'rich', desc).enabled).toBe(true)
  editor.commands.undo()
  expect(editor.state.doc.eq(before)).toBe(true)
})

it('requires exactly one selected column, refuses readonly and allows shared-fragment moves as ordinary edits', () => {
  mount()
  expect(queryCommand(editor, 'rich', asc).enabled).toBe(false)
  const map = TableMap.get(editor.state.doc.firstChild!)
  editor.view.dispatch(editor.state.tr.setSelection(CellSelection.colSelection(editor.state.doc.resolve(1 + map.map[0]), editor.state.doc.resolve(1 + map.map[1]))))
  expect(queryCommand(editor, 'rich', asc).enabled).toBe(false)
  selectColumn(); editor.setEditable(false)
  expect(moveRow(editor, 0, 1, 2)).toBe(false)
  expect(dispatchCommand(editor, 'rich', asc)).toBe(false)
  // Decision 2026-09-08: the shared Yjs binding no longer blocks structural table edits.
  editor.destroy(); mount(undefined, true); selectColumn()
  expect(moveRow(editor, 0, 1, 2)).toBe(true)
  selectColumn()
  expect(dispatchCommand(editor, 'rich', asc)).toBe(true)
})

it('rejects vertical spans without changing data but allows horizontal spans in row moves', () => {
  const table = mount()
  const json = structuredClone(table.toJSON())
  json.content![1].content![0].attrs.rowspan = 2
  json.content![2].content!.splice(0, 1)
  const vertical = editor.schema.nodeFromJSON(json)
  expect(TableMap.get(vertical).problems).toBeNull()
  expect(reorderedRows(vertical, 1, 3)).toBeNull()
  expect(sortedRows(vertical, 0, 'ascending')).toBeNull()
  const horizontal = structuredClone(table.toJSON())
  horizontal.content![1].content![0].attrs.colspan = 2
  horizontal.content![1].content!.splice(1, 1)
  const moved = reorderedRows(editor.schema.nodeFromJSON(horizontal), 1, 3)!
  expect(moved).not.toBeNull()
  expect(TableMap.get(moved).problems).toBeNull()
})

it('sorts headerless tables and protects every leading header row', () => {
  const table = mount(['2', '1'])
  const json = structuredClone(table.toJSON())
  json.content!.shift()
  const next = sortedRows(editor.schema.nodeFromJSON(json), 0, 'ascending')!
  expect(next.firstChild!.firstChild!.textContent).toBe('1')
  const headers = structuredClone(table.toJSON())
  headers.content!.splice(1, 0, headers.content![0])
  const multiple = editor.schema.nodeFromJSON(headers)
  expect(reorderedRows(multiple, 1, 2)).toBeNull()
  expect(sortedRows(multiple, 0, 'ascending')!.child(1)).toBe(multiple.child(1))
})
