// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { runTableAxisCommand, tableAxisRange } from './commands'
import { createMarkdownKit } from '../../createMarkdownKit'
import { moveColumn, reorderedTable } from './columnOrder'

let editor: Editor
afterEach(() => { editor?.destroy(); document.body.replaceChildren() })
function mount() {
  editor = new Editor({ extensions: createMarkdownKit({ profile: 'rich' }).extensions,
    content: '| A | B | C |\n| --- | --- | --- |\n| **Alpha** | Beta | Gamma |' })
  return editor.state.doc.firstChild!
}

it('moves original nodes with widths, backgrounds and marks, selecting the new column', () => {
  mount()
  const tr = editor.state.tr
  tr.doc.descendants((node, pos) => {
    if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') {
      tr.setNodeAttribute(pos, 'backgroundColor', 'green')
      tr.setNodeAttribute(pos, 'colwidth', [144])
    }
  })
  editor.view.dispatch(tr)
  const before = editor.state.doc.firstChild!
  const original = editor.getJSON()
  expect(moveColumn(editor, 0, 0, 2)).toBe(true)
  const moved = editor.state.doc.firstChild!
  moved.forEach((row, _, index) => expect(row.child(2).eq(before.child(index).child(0))).toBe(true))
  expect(TableMap.get(moved).problems).toBeNull()
  editor.commands.undo()
  expect(editor.getJSON()).toEqual(original)
})

it('rejects invalid moves and cannot cut through a horizontally merged cell', () => {
  const table = mount()
  expect(reorderedTable(table, -1, 0)).toBeNull()
  expect(reorderedTable(table, 0, 3)).toBeNull()
  expect(reorderedTable(table, 1, 1)).toBeNull()
  // Use a valid merged row shape rather than relying on table-repair side effects.
  const json = table.toJSON()
  const row = json.content![1]
  row.content![0].attrs = { ...row.content![0].attrs, colspan: 2 }
  row.content!.splice(1, 1)
  const merged = editor.schema.nodeFromJSON(json)
  expect(reorderedTable(merged, 0, 2)).toBeNull()
  expect(reorderedTable(merged, 2, 1)).toBeNull()
  expect(reorderedTable(merged, 2, 0)).not.toBeNull()
})

it('keeps vertical spans intact and refuses edits in readonly', () => {
  const table = mount()
  const json = table.toJSON()
  json.content![0].content![0].attrs = { ...json.content![0].content![0].attrs, rowspan: 2 }
  json.content![1].content!.splice(0, 1)
  const result = reorderedTable(editor.schema.nodeFromJSON(json), 0, 2)!
  expect(TableMap.get(result).problems).toBeNull()
  expect(result.firstChild!.lastChild!.attrs.rowspan).toBe(2)
  editor.setEditable(false)
  expect(moveColumn(editor, 0, 0, 2)).toBe(false)
})

it('Shift selection moves two columns as one range, preserves order and independent undo steps', () => {
  mount()
  runTableAxisCommand(editor, 0, 'column', 0, 'select')
  runTableAxisCommand(editor, 0, 'column', 1, 'select', true)
  const before = editor.getJSON()
  expect(tableAxisRange(editor.state, 0, 'column', 1)).toEqual({ from: 0, count: 2 })
  expect(moveColumn(editor, 0, 0, 1, 2)).toBe(true)
  expect(editor.state.doc.firstChild!.firstChild!.textContent).toBe('CAB')
  expect(tableAxisRange(editor.state, 0, 'column', 2)).toEqual({ from: 1, count: 2 })
  expect((editor.state.selection as CellSelection).isColSelection()).toBe(true)
  expect(moveColumn(editor, 0, 1, 0, 2)).toBe(true)
  editor.commands.undo()
  expect(editor.state.doc.firstChild!.firstChild!.textContent).toBe('CAB')
  editor.commands.undo()
  expect(editor.getJSON()).toEqual(before)
})

it('a selected column group carries an entire merged cell but cannot cut it', () => {
  const table = mount().toJSON()
  table.content![1].content![0].attrs = { ...table.content![1].content![0].attrs, colspan: 2 }
  table.content![1].content!.splice(1, 1)
  const merged = editor.schema.nodeFromJSON(table)
  expect(reorderedTable(merged, 0, 1, 2)).not.toBeNull()
  expect(TableMap.get(reorderedTable(merged, 0, 1, 2)!).problems).toBeNull()
  expect(reorderedTable(merged, 1, 0, 2)).toBeNull()
})
