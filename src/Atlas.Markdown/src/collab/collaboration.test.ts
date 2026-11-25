// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor, type AnyExtension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import * as Y from 'yjs'
import { ySyncPluginKey } from '@tiptap/y-tiptap'
import { KeepingCollaboration } from './collaboration'

// Guard of KeepingCollaboration's shortcut (collaboration.ts): it switches off y-tiptap's private `binding._forceRerender`, which the
// sync plugin's view calls each time the plugin views are recreated. If y-tiptap renames it or stops calling it, the first test fails
// here instead of the shortcut silently doing nothing (or skipping a render that is still needed).
const editors: Editor[] = []
afterEach(() => editors.splice(0).forEach(editor => editor.destroy()))

function open(collaboration: AnyExtension) {
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), collaboration.configure({ document: new Y.Doc() })] })
  editors.push(editor)
  editor.commands.setContent('<p>One</p><p>Two</p>')
  return editor
}
const later = () => new Plugin({ key: new PluginKey(`later-${Math.random()}`) })

it("y-tiptap's binding still has the private _forceRerender, and a recreated sync view still calls it", () => {
  const editor = open(Collaboration)
  const binding = ySyncPluginKey.getState(editor.state)!.binding
  expect(typeof binding._forceRerender, 'ProsemirrorBinding._forceRerender is gone: revisit KeepingCollaboration').toBe('function')
  const rerender = binding._forceRerender.bind(binding)
  let calls = 0
  binding._forceRerender = () => { calls++; rerender() }
  editor.registerPlugin(later())
  expect(calls, 'the sync view no longer calls _forceRerender: revisit KeepingCollaboration').toBe(1)
})

it('KeepingCollaboration skips that rerender: a plugin registered later keeps the document and its DOM', () => {
  const editor = open(KeepingCollaboration)
  const doc = editor.state.doc, first = editor.view.dom.firstChild
  editor.registerPlugin(later())
  expect(editor.state.doc).toBe(doc)
  expect(editor.view.dom.firstChild).toBe(first)
  expect(Object.hasOwn(ySyncPluginKey.getState(editor.state)!.binding, '_forceRerender')).toBe(false)
})
