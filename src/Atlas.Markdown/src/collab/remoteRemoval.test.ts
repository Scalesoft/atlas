// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import * as Y from 'yjs'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import { useCollab } from './useCollab'
import { markdownRemoteRemovalEvent, type MarkdownRemoteRemovalDetail } from '../core/inputErrors'
import { CollaborationRemoteRemoval } from './remoteRemoval'

const editors: Editor[] = []
const docs: Y.Doc[] = []
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  docs.splice(0).forEach(doc => doc.destroy())
})

/** Who is present, as the provider's awareness tells it (client id → state). */
const people = new Map<number, Record<string, unknown>>()
afterEach(() => people.clear())

function replica(snapshot?: Uint8Array) {
  const doc = new Y.Doc()
  docs.push(doc)
  if (snapshot) Y.applyUpdate(doc, snapshot)
  const collaboration = useCollab({ documentId: 'removal-test', user: { id: String(doc.clientID), name: 'Test' }, ydoc: doc })
  const editor = new Editor({ element: document.body.appendChild(document.createElement('div')), extensions: [StarterKit.configure({ undoRedo: false }),
    ...collaboration.extensions.map(extension => extension.name === CollaborationRemoteRemoval.name ? CollaborationRemoteRemoval.configure({ awareness: () => ({ getStates: () => people }) }) : extension)] })
  editors.push(editor)
  const notices: MarkdownRemoteRemovalDetail[] = []
  editor.view.dom.addEventListener(markdownRemoteRemovalEvent, event => notices.push((event as CustomEvent<MarkdownRemoteRemovalDetail>).detail))
  return { doc, editor, notices }
}
function pair(html = '<p>Before</p><p>Editable block</p><p>After</p>') {
  const alice = replica()
  alice.editor.commands.setContent(html)
  yUndoPluginKey.getState(alice.editor.state)!.undoManager.clear()
  const bob = replica(Y.encodeStateAsUpdate(alice.doc))
  return { alice, bob }
}
/** Bob's offline edits reach Alice: only one way, so what Alice typed meanwhile is still hers alone. */
const deliver = (from: ReturnType<typeof replica>, to: ReturnType<typeof replica>) => Y.applyUpdate(to.doc, Y.encodeStateAsUpdate(from.doc, Y.encodeStateVector(to.doc)))
const second = (editor: Editor) => ({ from: editor.state.doc.child(0).nodeSize, to: editor.state.doc.child(0).nodeSize + editor.state.doc.child(1).nodeSize })

describe('a colleague removes the block I am typing in (J10.6, VS5)', () => {
  it('keeps my unsent text, names nobody it cannot tell, and „Vrátit“ brings the block back as one undo step', () => {
    const { alice, bob } = pair()
    // Alice types in the second block; Bob, who has not received it, deletes that block.
    alice.editor.commands.insertContentAt(second(alice.editor).to - 1, ' + mine')
    bob.editor.commands.deleteRange(second(bob.editor))
    deliver(bob, alice)
    // Yjs drops text inserted into a deleted element: without the notice it would be gone silently.
    expect(alice.editor.getText()).toBe('Before\n\nAfter')
    expect(alice.notices).toHaveLength(1)
    expect(alice.notices[0]!.message).toBe('Někdo odebral(a) blok, ve kterém jste psal(a).')

    expect(alice.notices[0]!.restore()).toBe(true)
    expect(alice.editor.getText()).toBe('Before\n\nEditable block + mine\n\nAfter')
    expect(alice.editor.state.selection.$head.parent.textContent).toBe('Editable block + mine')
    // The restore is Alice's own change and reaches Bob like any other.
    deliver(alice, bob)
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
    expect(alice.editor.commands.undo()).toBe(true)
    expect(alice.editor.getText()).toBe('Before\n\nAfter')
  })

  it('brings a removed list item back into its list, after the item before it', () => {
    const { alice, bob } = pair('<ul><li><p>One</p></li><li><p>Two</p></li><li><p>Three</p></li></ul>')
    const itemText = (editor: Editor, text: string) => { let at = -1; editor.state.doc.descendants((node, pos) => { if (at < 0 && node.isTextblock && node.textContent === text) at = pos + 1 }); return at }
    alice.editor.commands.insertContentAt(itemText(alice.editor, 'Two') + 3, ' and more')
    const two = itemText(bob.editor, 'Two')
    bob.editor.commands.deleteRange({ from: two - 2, to: two + 'Two'.length + 2 })
    deliver(bob, alice)
    expect(alice.editor.getHTML()).toBe('<ul><li><p>One</p></li><li><p>Three</p></li></ul><p></p>')
    expect(alice.notices).toHaveLength(1)
    alice.notices[0]!.restore()
    expect(alice.editor.getHTML()).toBe('<ul><li><p>One</p></li><li><p>Two and more</p></li><li><p>Three</p></li></ul><p></p>')
  })

  it('names the colleague from presence when they are the only one there', () => {
    const { alice, bob } = pair()
    people.set(alice.doc.clientID, { user: { name: 'Alice' } }).set(bob.doc.clientID, { user: { name: 'Jana Nováková' } })
    alice.editor.commands.setTextSelection(second(alice.editor).from + 3)
    alice.editor.commands.insertContent('x')
    bob.editor.commands.deleteRange(second(bob.editor))
    deliver(bob, alice)
    expect(alice.notices.map(notice => notice.message)).toEqual(['Jana Nováková odebral(a) blok, ve kterém jste psal(a).'])
  })

  it('stays quiet for a removal elsewhere, an empty block, and my own deletion', () => {
    const { alice, bob } = pair('<p>Before</p><p></p><p>After</p>')
    // Caret in the empty block, which Bob removes: nothing to lose.
    alice.editor.commands.setTextSelection(second(alice.editor).from + 1)
    bob.editor.commands.deleteRange(second(bob.editor))
    deliver(bob, alice)
    // Bob removes the first block while Alice types in the last one.
    alice.editor.commands.insertContentAt(alice.editor.state.doc.content.size - 1, '!')
    bob.editor.commands.deleteRange({ from: 0, to: bob.editor.state.doc.child(0).nodeSize })
    deliver(bob, alice)
    alice.editor.commands.deleteRange({ from: 0, to: alice.editor.state.doc.child(0).nodeSize })
    expect(alice.notices).toHaveLength(0)
  })
})
