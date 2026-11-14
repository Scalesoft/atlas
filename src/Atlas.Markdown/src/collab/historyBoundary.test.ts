// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import { Editor, Extension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { Plugin, TextSelection } from '@tiptap/pm/state'
import { relativePositionToAbsolutePosition, ySyncPluginKey, yUndoPluginKey } from '@tiptap/y-tiptap'
import { CollaborationHistoryBoundary } from './historyBoundary'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()) })

// A plugin that answers every change with an appended transaction (as heading ids, a table repair or the trailing paragraph do).
const Appender = Extension.create({ name: 'appender', addProseMirrorPlugins: () => [new Plugin({
  appendTransaction: (transactions, _old, state) => transactions.some(tr => tr.docChanged && !tr.getMeta('appended')) ? state.tr.setMeta('appended', true) : null,
})] })

it('undoes a change with an appended transaction to the caret before it, not after it (J4.4)', async () => {
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document: new Y.Doc() }), CollaborationHistoryBoundary, Appender] })
  editors.push(editor)
  editor.commands.setContent('<p>one</p><p>two</p>')
  yUndoPluginKey.getState(editor.state)!.undoManager.stopCapturing()
  const caret = editor.state.doc.content.size - 1
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, caret)))
  const inserted = editor.schema.nodes.paragraph!.create(null, editor.schema.text('a longer first line'))
  editor.view.dispatch(editor.state.tr.insert(0, inserted))
  await Promise.resolve()
  // The step's own record, not y-tiptap's recovery heuristics: its caret is the one before the change (y-tiptap stored the end).
  const { undoManager } = yUndoPluginKey.getState(editor.state)!
  const binding = ySyncPluginKey.getState(editor.state).binding
  const stored = undoManager.undoStack.at(-1)!.meta.get(binding) as { anchor: Y.RelativePosition; absAnchor: number }
  expect(stored.absAnchor).toBe(caret)
  expect(relativePositionToAbsolutePosition(binding.doc, binding.type, stored.anchor, binding.mapping)).toBe(caret + inserted.nodeSize)
  editor.commands.undo()
  expect(editor.state.doc.textContent).toBe('onetwo')
  expect(editor.state.selection.from).toBe(caret)
})

function editor() {
  const instance = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document: new Y.Doc() }), CollaborationHistoryBoundary] })
  editors.push(instance)
  instance.commands.setContent('<p>ab</p>')
  yUndoPluginKey.getState(instance.state)!.undoManager.clear()
  return instance
}

it('starts a new undo step when the caret moves between typed characters (J4.4), not at every keystroke', () => {
  const typing = editor()
  typing.commands.insertContentAt(2, 'x'); typing.commands.insertContentAt(3, 'y')
  typing.commands.setTextSelection(1)
  typing.commands.insertContentAt(1, 'v')
  expect(typing.getText()).toBe('vaxyb')
  typing.commands.undo(); expect(typing.getText()).toBe('axyb')
  typing.commands.undo(); expect(typing.getText()).toBe('ab')
})

it('ends the step at a caret key the browser handles itself (End), before its selection reaches the editor', () => {
  const typing = editor()
  typing.commands.insertContentAt(1, 'w')
  typing.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }))
  typing.commands.insertContentAt(4, 'v')
  expect(typing.getText()).toBe('wabv')
  typing.commands.undo(); expect(typing.getText()).toBe('wab')
  typing.commands.undo(); expect(typing.getText()).toBe('ab')
})
