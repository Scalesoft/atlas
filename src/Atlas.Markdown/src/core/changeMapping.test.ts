// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import type { Transaction } from '@tiptap/pm/state'
import * as Y from 'yjs'
import { useCollab } from '../collab/useCollab'
import { markdownChangeMapping } from './changeMapping'
import { isRemoteTransaction } from './recognition/gate'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()) })

function replica(snapshot?: Uint8Array) {
  const doc = new Y.Doc()
  if (snapshot) Y.applyUpdate(doc, snapshot)
  const collaboration = useCollab({ documentId: 'change-mapping', user: { id: String(doc.clientID), name: 'Test' }, ydoc: doc })
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), ...collaboration.extensions] })
  editors.push(editor)
  const remote: Transaction[] = []
  editor.on('transaction', ({ transaction }) => { if (isRemoteTransaction(transaction)) remote.push(transaction) })
  return { doc, editor, remote }
}
/** The position right after `text`. */
function after(editor: Editor, text: string) {
  let at = -1
  editor.state.doc.descendants((node, pos) => { if (at < 0 && node.text?.includes(text)) at = pos + node.text.indexOf(text) + text.length })
  return at
}

it('maps a position inside a block a collaborator typed into, which y-tiptap replaces whole', () => {
  const alice = replica()
  alice.editor.commands.setContent('<p>One</p><ul><li><p>First item</p></li><li><p>Second item</p></li></ul><p>Two</p>')
  const bob = replica(Y.encodeStateAsUpdate(alice.doc))
  // Bob's target: inside the second item, after „Second“.
  const target = after(bob.editor, 'Second')
  // Alice types into the first item of the same list.
  alice.editor.commands.insertContentAt(after(alice.editor, 'First'), '!')
  Y.applyUpdate(bob.doc, Y.encodeStateAsUpdate(alice.doc, Y.encodeStateVector(bob.doc)))
  const transaction = bob.remote.at(-1)!
  expect(transaction.mapping.mapResult(target, 1).deleted).toBe(true)
  const mapped = markdownChangeMapping(transaction).mapResult(target, 1)
  expect(mapped.deleted).toBe(false)
  expect(bob.editor.state.doc.textBetween(mapped.pos - 6, mapped.pos)).toBe('Second')
})

it('leaves local transactions as they are', () => {
  const { editor } = replica()
  editor.commands.setContent('<p>One</p>')
  const transaction = editor.state.tr.insertText('x', 2)
  expect(markdownChangeMapping(transaction)).toBe(transaction.mapping)
})
