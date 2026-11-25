// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { TableKit } from '@tiptap/extension-table'
import TextAlign from '@tiptap/extension-text-align'
import Collaboration from '@tiptap/extension-collaboration'
import { TextSelection } from '@tiptap/pm/state'
import crashes from './fixtures/y-tiptap-selection-crash.json'

// Recorded by yjsGuardFuzz.test.ts: three real editors edited concurrently, and one of them threw while it received a remote
// update ("Position N out of range" in y-tiptap's restoreRelativeSelection), which the provider turns into a fatal error. Each
// record is that editor's Yjs state and caret before the update, and the update. Plain Tiptap reproduced it with
// @tiptap/y-tiptap 3.0.9, whose CollaborationRemoteSelection workaround Atlas carried until 3.3.1 fixed it upstream.
const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()) })
const bytes = (base64: string) => Uint8Array.from(atob(base64), character => character.charCodeAt(0))

function receive(crash: (typeof crashes)[number]) {
  const doc = new Y.Doc()
  Y.applyUpdate(doc, bytes(crash.before))
  const editor = new Editor({ extensions: [
    StarterKit.configure({ undoRedo: false }), TableKit, TextAlign.configure({ types: ['heading', 'paragraph'] }),
    Collaboration.configure({ document: doc, field: 'default' }),
  ] })
  editors.push(editor)
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, crash.selection.anchor, crash.selection.head)))
  return { editor, apply: () => Y.applyUpdate(doc, bytes(crash.update), 'remote') }
}

it.each(crashes.map(crash => [crash.cause, crash] as const))('keeps the caret in range across the transactions of one remote change (%s)', (_cause, crash) => {
  const { editor, apply } = receive(crash)
  expect(apply).not.toThrow()
  expect(editor.state.selection.head).toBeLessThanOrEqual(editor.state.doc.content.size)
})
