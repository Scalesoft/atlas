// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { CollaborationHistoryBoundary } from '../../collab/historyBoundary'
import { createEditorToolsPlugin, editorSearchState, findInEditor, replaceInEditor } from './index'

const editors: Editor[] = []; const documents: Y.Doc[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); documents.splice(0).forEach(document => document.destroy()) })
function replica(snapshot?: Uint8Array) {
  const document = new Y.Doc(); documents.push(document)
  if (snapshot) Y.applyUpdate(document, snapshot)
  const tools = createEditorToolsPlugin().create({ profile: 'rich', signal: new AbortController().signal })
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }),
    Collaboration.configure({ document }), CollaborationHistoryBoundary, ...tools.extensions!] })
  editors.push(editor)
  return { document, editor }
}

it('isolates replacement in collaborative undo and refreshes local search on remote changes', () => {
  const alice = replica()
  alice.editor.commands.setContent('<p>cat cat</p>')
  yUndoPluginKey.getState(alice.editor.state)!.undoManager.clear()
  const bob = replica(Y.encodeStateAsUpdate(alice.document))
  findInEditor(alice.editor, 'cat')
  bob.editor.commands.insertContentAt(1, 'remote ')
  Y.applyUpdate(alice.document, Y.encodeStateAsUpdate(bob.document))
  expect(editorSearchState(alice.editor).results).toHaveLength(2)
  expect(editorSearchState(alice.editor).results[0]?.from).toBe(8)
  replaceInEditor(alice.editor, 'dog', true)
  findInEditor(alice.editor, 'dog'); replaceInEditor(alice.editor, 'fox', true)
  alice.editor.commands.undo(); expect(alice.editor.getText()).toBe('remote dog dog')
  alice.editor.commands.undo(); expect(alice.editor.getText()).toBe('remote cat cat')
  expect(alice.editor.commands.undo()).toBe(false)
})
