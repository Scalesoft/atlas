// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import { CollaborationHistoryBoundary } from '../collab/historyBoundary'
import { endsWord, WordHistoryBoundary } from './historyBoundary'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()) })

function type(editor: Editor, text: string) {
  for (const character of text) editor.view.dispatch(editor.state.tr.insertText(character))
}

function local() {
  const editor = new Editor({ extensions: [StarterKit, WordHistoryBoundary], content: '<p></p>' })
  editors.push(editor)
  return editor
}

function collaborative() {
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document: new Y.Doc() }), CollaborationHistoryBoundary, WordHistoryBoundary] })
  editors.push(editor)
  yUndoPluginKey.getState(editor.state)!.undoManager.clear()
  return editor
}

it('reads a word end from typed whitespace or punctuation after a letter, not from a second space or a paste', () => {
  const editor = local()
  const ends = (text: string, meta?: string) => {
    const tr = editor.state.tr.insertText(text)
    if (meta) tr.setMeta('uiEvent', meta)
    return endsWord(tr)
  }
  type(editor, 'ab')
  expect(ends(' ')).toBe(true)
  expect(ends('.')).toBe(true)
  expect(ends('c')).toBe(false)
  expect(ends(' ', 'paste')).toBe(false)
  type(editor, '.')
  expect(ends(' ')).toBe(false)
})

for (const [name, create] of [['local history', local], ['Yjs history', collaborative]] as const) {
  it(`undoes typing one word at a time in ${name}, within the 500 ms the steps would otherwise share`, () => {
    const editor = create()
    type(editor, 'Jedno dvě, tři')
    expect(editor.getText()).toBe('Jedno dvě, tři')
    editor.commands.undo(); expect(editor.getText()).toBe('Jedno dvě,')
    editor.commands.undo(); expect(editor.getText()).toBe('Jedno ')
    editor.commands.undo(); expect(editor.getText()).toBe('')
  })
}
