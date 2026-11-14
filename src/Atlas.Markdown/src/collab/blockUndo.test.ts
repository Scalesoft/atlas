// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import { Editor, type JSONContent } from '@tiptap/core'
import Collaboration from '@tiptap/extension-collaboration'
import { TextSelection } from '@tiptap/pm/state'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import { CollaborationHistoryBoundary } from './historyBoundary'
import { CollaborationTableRepair } from './tableRepair'
import { createMarkdownKit } from '../createMarkdownKit'
import { KeyboardFirst } from '../extensions/KeyboardFirst'
import { blockActions } from '../features/intentions/turnInto'
import type { MarkdownIntention } from '../features/intentions/intentions'

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })

/** The real kit with the collaborative history, as useCollab gives it (J4.4, J9.4). */
function mount(content: unknown, caret: (editor: Editor) => number) {
  const element = document.createElement('div'); document.body.appendChild(element)
  const editor = new Editor({ element, extensions: [...createMarkdownKit({ profile: 'rich', collaborative: true }).extensions, KeyboardFirst,
    Collaboration.configure({ document: new Y.Doc() }), CollaborationHistoryBoundary, CollaborationTableRepair] })
  editors.push(editor)
  editor.commands.setContent(content as never)
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, caret(editor))))
  yUndoPluginKey.getState(editor.state)!.undoManager.clear()
  return editor
}
const key = (editor: Editor, init: KeyboardEventInit) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, cancelable: true, ...init }))
// The binding writes to Yjs on the view update; a step ends after it.
const settle = () => new Promise(resolve => setTimeout(resolve, 10))
const blocks = (editor: Editor) => (editor.getJSON() as JSONContent).content!.map(node => node.content?.[0]?.text)

it.each([
  ['Alt+Shift+ArrowDown', (editor: Editor) => key(editor, { key: 'ArrowDown', altKey: true, shiftKey: true })],
  ['the block menu row', (editor: Editor) => (blockActions({ editor, block: true, from: 2, to: 2, empty: true, text: '', signal: new AbortController().signal }) as readonly MarkdownIntention[])
    .find(item => item.id === 'core.block.duplicate')!.run()],
])('duplicating a block (%s) is one undo step, and redo brings it back', async (_, run) => {
  const editor = mount('alpha\n\nbeta', () => 2)
  run(editor); await settle()
  expect(blocks(editor)).toEqual(['alpha', 'alpha', 'beta'])
  editor.commands.undo(); await settle()
  expect(blocks(editor)).toEqual(['alpha', 'beta'])
  editor.commands.redo(); await settle()
  expect(blocks(editor)).toEqual(['alpha', 'alpha', 'beta'])
})

it('undoing a row Tab added in the last cell puts the caret back in that cell, redo in the new row (J9.4)', async () => {
  const editor = mount('| a | b |\n|---|---|\n| c | d |\n\nafter', instance => {
    let at = 0
    instance.state.doc.descendants((node, pos) => { if (node.text === 'd') at = pos + 1 })
    return at
  })
  const before = editor.state.selection.from
  key(editor, { key: 'Tab' }); await settle()
  const added = editor.state.selection.from
  expect(added).toBeGreaterThan(before)
  editor.commands.undo(); await settle()
  expect(editor.state.selection.from).toBe(before)
  expect(editor.state.selection.$from.parent.textContent).toBe('d')
  editor.commands.redo(); await settle()
  expect(editor.state.selection.from).toBe(added)
})

it('undoing Enter in a timed item puts the caret back at the end of that item, redo in the new one (J9.4)', async () => {
  const text = '3. 11. 2026 17:19 — first'
  const editor = mount({ type: 'doc', content: [{ type: 'timedList', content: [{ type: 'timedItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] }] }] },
    instance => instance.state.doc.firstChild!.nodeSize - 3)
  const before = editor.state.selection.from
  key(editor, { key: 'Enter' }); await settle()
  const added = editor.state.selection.from
  expect(editor.state.doc.firstChild!.childCount).toBe(2)
  editor.commands.undo(); await settle()
  expect(editor.state.doc.firstChild!.childCount).toBe(1)
  expect(editor.state.selection.from).toBe(before)
  editor.commands.redo(); await settle()
  expect(editor.state.selection.from).toBe(added)
})
