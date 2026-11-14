// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { TableKit } from '@tiptap/extension-table'
import TextAlign from '@tiptap/extension-text-align'
import Collaboration from '@tiptap/extension-collaboration'
import { TextSelection } from '@tiptap/pm/state'
import { CollaborationRemoteSelection } from './remoteSelection'
import crashes from './fixtures/y-tiptap-selection-crash.json'

// Recorded by yjsGuardFuzz.test.ts: three real editors edited concurrently, and one of them threw while it received a remote
// update ("Position N out of range" in y-tiptap's restoreRelativeSelection), which the provider turns into a fatal error. Each
// record is that editor's Yjs state and caret before the update, and the update. Plain Tiptap reproduces it, so it is
// @tiptap/y-tiptap 3.0.9 (the latest release), not our extensions.
const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()) })
const bytes = (base64: string) => Uint8Array.from(atob(base64), character => character.charCodeAt(0))

function receive(crash: (typeof crashes)[number], workaround: boolean) {
  const doc = new Y.Doc()
  Y.applyUpdate(doc, bytes(crash.before))
  const editor = new Editor({ extensions: [
    StarterKit.configure({ undoRedo: false }), TableKit, TextAlign.configure({ types: ['heading', 'paragraph'] }),
    Collaboration.configure({ document: doc, field: 'default' }), ...workaround ? [CollaborationRemoteSelection] : [],
  ] })
  editors.push(editor)
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, crash.selection.anchor, crash.selection.head)))
  return { editor, apply: () => Y.applyUpdate(doc, bytes(crash.update), 'remote') }
}

it.each(crashes.map(crash => [crash.cause, crash] as const))('reproduces the upstream crash (%s)', (_cause, crash) => {
  // When this stops throwing, y-tiptap fixed it and CollaborationRemoteSelection can go.
  expect(receive(crash, false).apply).toThrow(/out of range/)
})

it.each(crashes.map(crash => [crash.cause, crash] as const))('keeps the caret in range across the transactions of one remote change (%s)', (_cause, crash) => {
  const { editor, apply } = receive(crash, true)
  expect(apply).not.toThrow()
  expect(editor.state.selection.head).toBeLessThanOrEqual(editor.state.doc.content.size)
})
