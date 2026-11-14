// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import type { Transaction } from '@tiptap/pm/state'
import { ReplaceStep } from '@tiptap/pm/transform'
import * as Y from 'yjs'
import { absolutePositionToRelativePosition, yCursorPlugin, yCursorPluginKey, yXmlFragmentToProsemirrorJSON, ySyncPluginKey } from '@tiptap/y-tiptap'
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate } from 'y-protocols/awareness'
import { Extension } from '@tiptap/core'
import { useCollab } from './useCollab'

// patches/@tiptap__y-tiptap@3.0.9.patch: a remote change replaces only what changed, not the whole document.
const editors: Editor[] = []
const docs: Y.Doc[] = []
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  docs.splice(0).forEach(doc => doc.destroy())
})

function replica(snapshot?: Uint8Array) {
  const doc = new Y.Doc()
  docs.push(doc)
  if (snapshot) Y.applyUpdate(doc, snapshot)
  const collaboration = useCollab({ documentId: 'remote-apply', user: { id: String(doc.clientID), name: 'Test' }, ydoc: doc })
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), ...collaboration.extensions] })
  editors.push(editor)
  const remote: Transaction[] = []
  editor.on('transaction', ({ transaction }) => {
    if ((transaction.getMeta(ySyncPluginKey) as { isChangeOrigin?: boolean } | undefined)?.isChangeOrigin) remote.push(transaction)
  })
  return { doc, editor, remote }
}
const blocks = Array.from({ length: 20 }, (_, i) => `<p>Block ${i} text</p>`).join('')
function pair(content = blocks) {
  const alice = replica()
  alice.editor.commands.setContent(content)
  const bob = replica(Y.encodeStateAsUpdate(alice.doc))
  bob.remote.length = 0
  return { alice, bob }
}
function exchange(a: ReturnType<typeof replica>, b: ReturnType<typeof replica>) {
  const fromA = Y.encodeStateAsUpdate(a.doc, Y.encodeStateVector(b.doc))
  const fromB = Y.encodeStateAsUpdate(b.doc, Y.encodeStateVector(a.doc))
  Y.applyUpdate(a.doc, fromB)
  Y.applyUpdate(b.doc, fromA)
}
const posOfBlock = (editor: Editor, index: number) => { let pos = 0; for (let i = 0; i < index; i++) pos += editor.state.doc.child(i).nodeSize; return pos }
/** The editor's document is exactly what Yjs holds. */
const matchesYjs = (r: ReturnType<typeof replica>) => r.editor.state.schema.nodeFromJSON(yXmlFragmentToProsemirrorJSON(r.doc.getXmlFragment('default'))).eq(r.editor.state.doc)

describe('remote changes apply only the changed range', () => {
  it('replaces only the typed text and keeps every other block', () => {
    const { alice, bob } = pair()
    const before = bob.editor.state.doc
    alice.editor.commands.insertContentAt(posOfBlock(alice.editor, 10) + 3, 'X')
    exchange(alice, bob)
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
    const step = bob.remote.at(-1)!.steps[0] as ReplaceStep
    expect(bob.remote.at(-1)!.steps).toHaveLength(1)
    expect(step.to - step.from).toBeLessThan(before.child(10).nodeSize)
    for (let i = 0; i < 20; i++) if (i !== 10) expect(bob.editor.state.doc.child(i)).toBe(before.child(i))
  })

  it('keeps the untouched blocks across successive remote changes', () => {
    const { alice, bob } = pair()
    for (const index of [3, 15, 3]) {
      const before = bob.editor.state.doc
      alice.editor.commands.insertContentAt(posOfBlock(alice.editor, index) + 2, 'ab')
      exchange(alice, bob)
      for (let i = 0; i < 20; i++) if (i !== index) expect(bob.editor.state.doc.child(i)).toBe(before.child(i))
    }
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('keeps the local selection where it was', () => {
    const { alice, bob } = pair()
    const caret = posOfBlock(bob.editor, 12) + 4
    bob.editor.commands.setTextSelection(caret)
    alice.editor.commands.insertContentAt(posOfBlock(alice.editor, 2) + 1, 'ABC')
    exchange(alice, bob)
    expect(bob.editor.state.selection.from).toBe(caret + 3)
  })

  it('converges on remote block moves, splits, deletions and attribute changes', () => {
    const { alice, bob } = pair('<h2>Title</h2><p>One</p><ul><li><p>Item</p></li></ul><p>Two</p>')
    alice.editor.chain().setTextSelection(10).splitBlock().run()
    alice.editor.chain().setTextSelection(2).toggleHeading({ level: 3 }).run()
    exchange(alice, bob)
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
    alice.editor.commands.deleteRange({ from: posOfBlock(alice.editor, 1), to: posOfBlock(alice.editor, 3) })
    exchange(alice, bob)
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
    expect(matchesYjs(bob)).toBe(true)
  })

  it('converges under random concurrent edits on both sides', () => {
    let seed = 7
    const random = (n: number) => { seed = (seed * 16807) % 2147483647; return seed % n }
    const { alice, bob } = pair()
    for (let round = 0; round < 60; round++) {
      for (const side of [alice, bob]) {
        const editor = side.editor, size = editor.state.doc.content.size
        const at = 1 + random(size - 1)
        const op = random(4)
        if (op === 0) editor.commands.insertContentAt(Math.min(at, size - 1), 'xy')
        else if (op === 1) editor.commands.deleteRange({ from: at, to: Math.min(size - 1, at + 1 + random(6)) })
        else if (op === 2) editor.chain().setTextSelection(Math.min(at, size - 1)).splitBlock().run()
        else editor.chain().setTextSelection(Math.min(at, size - 1)).toggleBold().insertContent('b').run()
      }
      exchange(alice, bob)
      expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
    }
    expect(matchesYjs(alice) && matchesYjs(bob)).toBe(true)
  })

  it('keeps a collaborator cursor that arrives before the edit it points into', () => {
    const { alice, bob } = pair()
    const awareness = new Awareness(bob.doc)
    const cursors = Extension.create({ name: 'testCursors', addProseMirrorPlugins: () => [yCursorPlugin(awareness)] })
    bob.editor.registerPlugin(cursors.config.addProseMirrorPlugins!.call({} as never)[0]!)
    const remote = new Awareness(alice.doc)
    const place = (at: number) => {
      const sync = ySyncPluginKey.getState(alice.editor.state) as { type: never; binding: { mapping: never } }
      const position = absolutePositionToRelativePosition(at, sync.type, sync.binding.mapping)
      remote.setLocalState({ user: { name: 'Alice', color: '#123456' }, cursor: { anchor: position, head: position } })
      const states = new Map([[remote.clientID, remote.getLocalState()]])
      awareness.states.set(remote.clientID, states.get(remote.clientID)!)
      awareness.meta.set(remote.clientID, { clock: remote.meta.get(remote.clientID)!.clock, lastUpdated: Date.now() })
      bob.editor.view.dispatch(bob.editor.state.tr.setMeta(yCursorPluginKey, { awarenessUpdated: true }))
    }
    const cursorsAt = () => (yCursorPluginKey.getState(bob.editor.state) as { find(): { from: number; spec: { clientId?: number } }[] })
      .find().filter(decoration => decoration.spec.clientId === remote.clientID).map(decoration => decoration.from)
    const end = posOfBlock(alice.editor, 6) - 1
    place(end)
    expect(cursorsAt()).toContain(end)
    alice.editor.commands.insertContentAt(end, 'XYZ')
    place(end + 2) // inside the new text: the cursor arrives before the edit
    expect(cursorsAt()).toContain(end)
    exchange(alice, bob)
    expect(cursorsAt()).toContain(end + 2)
    remote.destroy()
    awareness.destroy()
  })

  it('keeps a click or key the browser has not reported yet when a collaborator cursor arrives', async () => {
    // 3.6 e2e collaboration-server-writes: C clicked after "Text C." and pressed End; A's cursor arrived before the selectionchange
    // event, the cursor update wrote C's old selection (the document start) back to the DOM, and C typed before "Sekce A".
    const { alice, bob } = pair()
    const awareness = new Awareness(bob.doc)
    const cursors = Extension.create({ name: 'testPendingSelection', addProseMirrorPlugins: () => [yCursorPlugin(awareness)] })
    bob.editor.registerPlugin(cursors.config.addProseMirrorPlugins!.call({} as never)[0]!)
    document.body.append(bob.editor.view.dom)
    ;(bob.editor.view.dom as HTMLElement).focus()
    bob.editor.commands.setTextSelection(1)
    // The browser moves the caret; its selectionchange event comes a task later, here only after the cursor update.
    const late = (event: Event) => event.stopImmediatePropagation()
    window.addEventListener('selectionchange', late, true)
    const caret = posOfBlock(bob.editor, 12) + 4
    const { node, offset } = bob.editor.view.domAtPos(caret)
    document.getSelection()!.collapse(node, offset)
    const remote = new Awareness(alice.doc)
    const sync = ySyncPluginKey.getState(alice.editor.state) as { type: never; binding: { mapping: never } }
    const elsewhere = absolutePositionToRelativePosition(posOfBlock(alice.editor, 3) + 2, sync.type, sync.binding.mapping)
    remote.setLocalState({ user: { name: 'Alice', color: '#123456' }, cursor: { anchor: elsewhere, head: elsewhere } })
    applyAwarenessUpdate(awareness, encodeAwarenessUpdate(remote, [remote.clientID]), 'remote')
    await new Promise(resolve => setTimeout(resolve, 50))
    window.removeEventListener('selectionchange', late, true)
    document.dispatchEvent(new Event('selectionchange'))
    expect(bob.editor.state.selection.from).toBe(caret)
    bob.editor.view.dom.remove()
    remote.destroy()
    awareness.destroy()
  })

  it('publishes the own cursor only after the first key or pointer press, not on a programmatic focus', async () => {
    const { bob } = pair()
    const awareness = new Awareness(bob.doc)
    const cursors = Extension.create({ name: 'testOwnCursor', addProseMirrorPlugins: () => [yCursorPlugin(awareness)] })
    bob.editor.registerPlugin(cursors.config.addProseMirrorPlugins!.call({} as never)[0]!)
    document.body.append(bob.editor.view.dom)
    ;(bob.editor.view.dom as HTMLElement).focus()
    bob.editor.commands.setTextSelection(6)
    expect(bob.editor.view.hasFocus()).toBe(true)
    expect(awareness.getLocalState()?.cursor ?? null).toBeNull()
    bob.editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true }))
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(awareness.getLocalState()?.cursor).toBeTruthy()
    bob.editor.view.dom.remove()
    awareness.destroy()
  })
})

