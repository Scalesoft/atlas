// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { createMarkdownKit } from '../../createMarkdownKit'
import { dispatchCommand, queryCommand } from '../../core/commands'

let editor: Editor | undefined
afterEach(() => { editor?.destroy(); document.body.replaceChildren() })

it('reports the active background for uniform cells and no active color for a mixed selection', () => {
  editor = new Editor({ extensions: createMarkdownKit({ profile: 'rich' }).extensions,
    content: '| One | Two |\n| --- | --- |\n| Alpha | Beta |' })
  const map = TableMap.get(editor.state.doc.firstChild!)
  const tr = editor.state.tr
  tr.setSelection(new CellSelection(tr.doc.resolve(map.map[0] + 1), tr.doc.resolve(map.map[1] + 1)))
  editor.view.dispatch(tr)
  expect(queryCommand(editor, 'rich', { type: 'set-cell-background', color: null }).active).toBe(true)
  dispatchCommand(editor, 'rich', { type: 'set-cell-background', color: 'green' })
  expect(queryCommand(editor, 'rich', { type: 'set-cell-background', color: 'green' }).active).toBe(true)
  editor.view.dispatch(editor.state.tr.setNodeAttribute(map.map[0] + 1, 'backgroundColor', 'red'))
  for (const color of [null, 'green', 'red']) {
    expect(queryCommand(editor, 'rich', { type: 'set-cell-background', color }).active).toBe(false)
  }
})

it('clears a rectangular selection atomically, keeps cell attributes and undoes every cell together', () => {
  editor = new Editor({ extensions: createMarkdownKit({ profile: 'rich' }).extensions,
    content: '| One | Two |\n| --- | --- |\n| Alpha | Beta |' })
  const table = editor.state.doc.firstChild!
  const map = TableMap.get(table)
  const tr = editor.state.tr
  for (const pos of map.map) tr.setNodeAttribute(pos + 1, 'backgroundColor', 'green')
  tr.setSelection(new CellSelection(tr.doc.resolve(map.map[0] + 1), tr.doc.resolve(map.map.at(-1)! + 1)))
  editor.view.dispatch(tr)
  const before = editor.getJSON()
  const command = { type: 'clear-cell-content' } as const
  expect(queryCommand(editor, 'rich', command).enabled).toBe(true)
  expect(editor.getJSON()).toEqual(before)
  expect(dispatchCommand(editor, 'rich', command)).toBe(true)
  editor.state.doc.firstChild!.forEach(row => row.forEach(cell => {
    expect(cell.textContent).toBe('')
    expect(cell.attrs.backgroundColor).toBe('green')
    expect(cell.childCount).toBe(1)
  }))
  editor.commands.undo()
  expect(editor.getJSON()).toEqual(before)
})

it('disables clearing outside a table and in readonly mode', () => {
  editor = new Editor({ extensions: createMarkdownKit({ profile: 'markdown-safe' }).extensions, content: 'Paragraph' })
  expect(queryCommand(editor, 'markdown-safe', { type: 'clear-cell-content' }).enabled).toBe(false)
  editor.commands.setContent('| One |\n| --- |\n| Two |')
  editor.commands.setTextSelection(3)
  editor.setEditable(false)
  expect(dispatchCommand(editor, 'markdown-safe', { type: 'clear-cell-content' })).toBe(false)
})
