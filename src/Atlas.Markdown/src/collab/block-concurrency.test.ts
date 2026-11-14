// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor, Extension } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import * as Y from 'yjs'
import { moveBlock, prepareBlockDrag } from '../features/drag/blockCommands'
import { commitBlockDrop } from '../features/drag/blockDrop'
import { createDragDecorationPlugins, SOURCE_DECO_PLUGIN_KEY, type SetSourceMeta } from '../features/drag/decorations'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import { NodeSelection } from '@tiptap/pm/state'
import { useCollab } from './useCollab'
import { PasteInputGuard } from '../engine/pasteInputGuard'

// The grip's drop path reads the drag-source decoration, which the editor surface installs in the app.
const DragDecorations = Extension.create({ name: 'testDragDecorations', addProseMirrorPlugins: () => createDragDecorationPlugins() })

const editors: Editor[] = []
const docs: Y.Doc[] = []
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  docs.splice(0).forEach(doc => doc.destroy())
  vi.restoreAllMocks()
})

function replica(snapshot?: Uint8Array) {
  const doc = new Y.Doc()
  docs.push(doc)
  if (snapshot) Y.applyUpdate(doc, snapshot)
  const collaboration = useCollab({ documentId: 'block-test', user: { id: String(doc.clientID), name: 'Test' }, ydoc: doc })
  const editor = new Editor({ extensions: [StarterKit.configure({ undoRedo: false }), PasteInputGuard, DragDecorations, ...collaboration.extensions] })
  editors.push(editor)
  return { doc, editor }
}
function pair() {
  const alice = replica()
  alice.editor.commands.setContent('<p>Before</p><p>Editable block</p><p>After</p>')
  yUndoPluginKey.getState(alice.editor.state)!.undoManager.clear()
  const bob = replica(Y.encodeStateAsUpdate(alice.doc))
  return { alice, bob }
}
function exchange(a: ReturnType<typeof replica>, b: ReturnType<typeof replica>) {
  const fromA = Y.encodeStateAsUpdate(a.doc, Y.encodeStateVector(b.doc))
  const fromB = Y.encodeStateAsUpdate(b.doc, Y.encodeStateVector(a.doc))
  Y.applyUpdate(a.doc, fromB)
  Y.applyUpdate(b.doc, fromA)
}
const secondBlock = (editor: Editor) => editor.state.doc.firstChild!.nodeSize

// Decision 2026-09-08: a collaborative move is the same delete+insert edit as cut/paste.
// These tests document the accepted behaviour, including its one known loss scenario.
describe('collaborative block moves are ordinary edits', () => {
  it('moves a block under the shared binding and converges on independent replicas', () => {
    const { alice, bob } = pair()
    expect(moveBlock(alice.editor, secondBlock(alice.editor), 1)).not.toBeNull()
    exchange(alice, bob)
    expect(alice.editor.getText()).toBe('Before\n\nAfter\n\nEditable block')
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('prepares a grip drag and commits a drop under the shared binding', () => {
    const { alice, bob } = pair()
    const from = secondBlock(alice.editor)
    const range = prepareBlockDrag(alice.editor, from)
    expect(range).toEqual({ from, to: from + alice.editor.state.doc.child(1).nodeSize })
    // What startDragSourceMarker publishes once the native drag begins.
    alice.editor.view.dispatch(alice.editor.state.tr.setMeta(SOURCE_DECO_PLUGIN_KEY, { type: 'set-source', ...range! } satisfies SetSourceMeta))
    expect(commitBlockDrop(alice.editor, 0)).toBeTruthy()
    exchange(alice, bob)
    expect(alice.editor.getText()).toBe('Editable block\n\nBefore\n\nAfter')
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('keeps concurrent edits made in other blocks', () => {
    const { alice, bob } = pair()
    moveBlock(alice.editor, secondBlock(alice.editor), 1)
    bob.editor.commands.insertContentAt(1, 'BOB ')
    exchange(alice, bob)
    expect(alice.editor.getText()).toBe('BOB Before\n\nAfter\n\nEditable block')
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('keeps edits made in the moved block after the move has been delivered', () => {
    const { alice, bob } = pair()
    const moved = moveBlock(alice.editor, secondBlock(alice.editor), 1)!
    exchange(alice, bob)
    bob.editor.commands.insertContentAt(moved + 1, 'BOB ')
    exchange(alice, bob)
    expect(alice.editor.getText()).toBe('Before\n\nAfter\n\nBOB Editable block')
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('documents the accepted trade-off: an unmerged edit inside the moved block does not follow the copy', () => {
    const { alice, bob } = pair()
    moveBlock(alice.editor, secondBlock(alice.editor), 1)
    bob.editor.commands.insertContentAt(secondBlock(bob.editor) + 1, 'BOB ')
    exchange(alice, bob)
    // Same outcome as Ctrl+X / Ctrl+V. Replicas converge; the offline text is not retained.
    expect(alice.editor.getJSON()).toEqual(bob.editor.getJSON())
    expect(alice.editor.getText()).toBe('Before\n\nAfter\n\nEditable block')
  })

  it('never duplicates a block when a move races a concurrent delete', () => {
    const { alice, bob } = pair()
    moveBlock(alice.editor, secondBlock(alice.editor), 1)
    bob.editor.commands.deleteRange({ from: secondBlock(bob.editor), to: secondBlock(bob.editor) + bob.editor.state.doc.child(1).nodeSize })
    exchange(alice, bob)
    expect(alice.editor.getJSON()).toEqual(bob.editor.getJSON())
    expect(alice.editor.getText().split('Editable block').length - 1).toBeLessThanOrEqual(1)
  })

  it('undo of a move restores the original order on every replica', () => {
    const { alice, bob } = pair()
    moveBlock(alice.editor, secondBlock(alice.editor), 1)
    exchange(alice, bob)
    expect(alice.editor.commands.undo()).toBe(true)
    exchange(alice, bob)
    expect(alice.editor.getText()).toBe('Before\n\nEditable block\n\nAfter')
    expect(bob.editor.getJSON()).toEqual(alice.editor.getJSON())
  })

  it('documents the undo wart: undoing a move after someone typed into the moved block strands their text', () => {
    const { alice, bob } = pair()
    const moved = moveBlock(alice.editor, secondBlock(alice.editor), 1)!
    exchange(alice, bob)
    bob.editor.commands.insertContentAt(moved + 1, 'BOB ')
    exchange(alice, bob)
    expect(alice.editor.commands.undo()).toBe(true)
    exchange(alice, bob)
    // Nothing is lost, but the other replica's text no longer sits inside the restored block.
    expect(alice.editor.getJSON()).toEqual(bob.editor.getJSON())
    expect(alice.editor.getText()).toBe('Before\n\nEditable block\n\nAfter\n\nBOB ')
  })

  it('no longer registers a collaboration drag guard', () => {
    const { alice } = pair()
    expect(alice.editor.extensionManager.extensions.some(extension => extension.name === 'markdownCollaborationDragGuard')).toBe(false)
  })

  it('lets a native ProseMirror move drop through instead of cancelling it', () => {
    const { alice } = pair()
    const { editor } = alice
    const from = editor.state.doc.firstChild!.nodeSize
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, from)))
    editor.view.dragging = { slice: editor.state.selection.content(), move: true }
    vi.spyOn(editor.view, 'posAtCoords').mockReturnValue({ pos: 1, inside: 0 })
    const event = new Event('drop', { bubbles: true, cancelable: true }) as DragEvent
    Object.defineProperty(event, 'dataTransfer', { value: { getData: () => '', files: [], types: [] } })
    editor.view.dispatchEvent(event)
    // No plugin of ours claims the drop any more, so ProseMirror performs the ordinary move.
    expect(editor.getText()).toBe('Editable block\n\nBefore\n\nAfter')
  })

  it('allows copy and external drop handlers to continue', () => {
    const { alice } = pair()
    const event = new Event('drop', { cancelable: true }) as DragEvent
    const slice = alice.editor.state.doc.slice(0, alice.editor.state.doc.firstChild!.nodeSize)
    const handled = alice.editor.view.someProp('handleDrop', fn => fn(alice.editor.view, event, slice, false))
    expect(handled).toBeFalsy()
    expect(event.defaultPrevented).toBe(false)
  })
})

describe('independent collaborative replicas: convergence of ordinary edits', () => {
  it('isolates rapid paste history while preserving another replica’s edits', () => {
    vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null })
    try {
      const { alice, bob } = pair()
      alice.editor.commands.setTextSelection(7)
      alice.editor.commands.insertContent(' typed')
      alice.editor.view.pasteText(' first')
      alice.editor.view.pasteText(' second')
      alice.editor.commands.insertContent(' after')
      bob.editor.commands.insertContentAt(bob.editor.state.doc.content.size - 1, ' BOB')
      exchange(alice, bob)
      for (const expected of ['Before typed first second', 'Before typed first', 'Before typed', 'Before']) {
        expect(alice.editor.commands.undo()).toBe(true)
        exchange(alice, bob)
        expect(alice.editor.state.doc.firstChild?.textContent).toBe(expected)
        expect(alice.editor.getText()).toContain('After BOB')
        expect(alice.editor.getJSON()).toEqual(bob.editor.getJSON())
      }
    } finally { vi.unstubAllGlobals() }
  })

  it.each(['bob-first', 'charlie-first'])('retains offline text and formatting with reordered, duplicated delivery (%s)', delivery => {
    const { alice, bob } = pair()
    const charlie = replica(Y.encodeStateAsUpdate(alice.doc))
    const from = alice.editor.state.doc.firstChild!.nodeSize
    bob.editor.commands.insertContentAt(from + 1, 'BOB ')
    charlie.editor.commands.setTextSelection({ from: from + 1, to: from + 1 + 'Editable block'.length })
    charlie.editor.commands.toggleBold()
    const updates = [Y.encodeStateAsUpdate(bob.doc), Y.encodeStateAsUpdate(charlie.doc)]
    if (delivery === 'charlie-first') updates.reverse()
    for (const peer of [alice, bob, charlie]) {
      for (const update of [...updates, ...updates]) Y.applyUpdate(peer.doc, update)
      expect(peer.editor.getText()).toBe('Before\n\nBOB Editable block\n\nAfter')
      expect(peer.editor.getJSON()).toEqual(alice.editor.getJSON())
      const edited = peer.editor.state.doc.child(1)
      expect(edited.content.content.some(node => node.text?.includes('Editable block') && node.marks.some(mark => mark.type.name === 'bold'))).toBe(true)
    }
    const reopened = replica(Y.encodeStateAsUpdate(alice.doc))
    expect(reopened.editor.getJSON()).toEqual(alice.editor.getJSON())
  })
})

// Collaboration campaign 2026-11-05: three writers in one paragraph over a lossy mesh, and a move racing typing and a delete.
describe('three writers in one paragraph: random interleaving, reordered and duplicated delivery, one peer offline', () => {
  // Every character is unique, so "nothing lost" is exact: the paragraph holds every inserted character no one deleted, once.
  let next = 0
  const unique = (n: number) => Array.from({ length: n }, () => String.fromCodePoint(0x4e00 + next++)).join('')

  function mesh(seed: number) {
    let state = seed
    next = 0 // unique within a session (CJK range, clear of surrogates)
    const random = (n: number) => { state = (state * 16807) % 2147483647; return state % n }
    const base = unique(8)
    const first = replica()
    first.doc.clientID = seed * 10 // fixed ids: concurrent inserts order by client id, so a session replays exactly
    first.editor.commands.setContent(`<p>${base}</p>`)
    const peers = [first, replica(Y.encodeStateAsUpdate(first.doc)), replica(Y.encodeStateAsUpdate(first.doc))]
    peers.forEach((peer, i) => { peer.doc.clientID = seed * 10 + i + 1 })
    const wire: { from: number, to: number, update: Uint8Array }[] = []
    peers.forEach((peer, from) => peer.doc.on('update', (update: Uint8Array, origin: unknown) => {
      if (origin !== 'wire') for (const to of [0, 1, 2]) if (to !== from) wire.push({ from, to, update })
    }))
    return { random, peers, wire, inserted: new Set(base), deleted: new Set<string>() }
  }

  it('converges on 200 seeded sessions without losing or duplicating a character', () => {
    for (let session = 0; session < 200; session++) {
      const { random, peers, wire, inserted, deleted } = mesh(session + 1)
      let offline = -1
      const online = (peer: number) => peer !== offline
      const deliver = () => {
        const ready = wire.map((m, i) => [m, i] as const).filter(([m]) => online(m.from) && online(m.to))
        if (!ready.length) return
        const [message, index] = ready[random(ready.length)]
        Y.applyUpdate(peers[message.to].doc, message.update, 'wire')
        if (random(4) !== 0) wire.splice(index, 1) // otherwise it is delivered again later
      }
      for (let step = 0; step < 40; step++) {
        if (offline < 0 && random(12) === 0) offline = random(3)
        else if (offline >= 0 && random(8) === 0) offline = -1
        const { editor } = peers[random(3)]
        const text = editor.state.doc.firstChild!.textContent
        const at = 1 + random(text.length + 1)
        if (random(3) === 0 && text.length > 0) {
          const from = random(text.length), to = Math.min(text.length, from + 1 + random(3))
          for (const character of text.slice(from, to)) deleted.add(character)
          editor.view.dispatch(editor.state.tr.delete(1 + from, 1 + to))
        } else {
          const typed = unique(1 + random(3))
          for (const character of typed) inserted.add(character)
          editor.view.dispatch(editor.state.tr.insertText(typed, at))
        }
        for (let n = random(4); n > 0; n--) deliver()
      }
      // Back online: everything still on the wire arrives, in its shuffled order, some of it twice.
      offline = -1
      while (wire.length) deliver()

      const expected = [...inserted].filter(character => !deleted.has(character)).sort().join('')
      for (const peer of peers) {
        expect(peer.editor.getJSON(), `session ${session}`).toEqual(peers[0].editor.getJSON())
        expect(peer.editor.state.doc.childCount, `session ${session}`).toBe(1)
        expect([...peer.editor.state.doc.firstChild!.textContent].sort().join(''), `session ${session}`).toBe(expected)
      }
      editors.splice(0).forEach(editor => editor.destroy())
      docs.splice(0).forEach(doc => doc.destroy())
    }
  }, 30_000) // ~4 s alone, ~9 s when the whole package runs in parallel

  // Upstream yjs 13.6.33, found by this fuzz (seed 2172): two updates held back for missing dependencies merge into one pending set whose
  // missing clock is the later gap's, so the first stays pending after its own dependency arrived, until that gap closes. Not reachable
  // through the provider, which applies remote updates in server sequence (causal) order; a peer-to-peer delivery would hit it.
  it('documents the upstream yjs gap: an update whose dependency arrived late stays pending behind another one', () => {
    const base = new Y.Doc()
    base.clientID = 1
    base.getText('t').insert(0, 'abcdefghij')
    const writer = new Y.Doc()
    writer.clientID = 22
    Y.applyUpdate(writer, Y.encodeStateAsUpdate(base))
    const own: Uint8Array[] = []
    writer.on('update', (update: Uint8Array, _origin: unknown, _doc: Y.Doc, transaction: Y.Transaction) => { if (transaction.local) own.push(update) })
    writer.getText('t').insert(8, 'x')
    writer.getText('t').insert(7, 'yzw')
    const other = new Y.Doc()
    other.clientID = 23
    Y.applyUpdate(other, Y.encodeStateAsUpdate(writer))
    other.getText('t').insert(8, 'Q') // between y and z: depends on the writer's second update
    const dependent = Y.encodeStateAsUpdate(other, Y.encodeStateVector(writer))
    Y.applyUpdate(writer, dependent)
    for (let i = 0; i < 7; i++) writer.getText('t').insert(0, String(i))
    writer.getText('t').insert(writer.getText('t').toString().indexOf('Q') + 1, 'R') // after Q: depends on the other client
    const [first, second, ...rest] = own
    const gap = rest.slice(0, -1), last = rest.at(-1)!

    const receiver = new Y.Doc()
    for (const update of [Y.encodeStateAsUpdate(base), dependent, last, first, second]) Y.applyUpdate(receiver, update)
    expect(receiver.getText('t').toString()).toBe('abcdefgyzwhxij') // Q's dependencies are all here, yet Q is still pending
    expect(receiver.store.pendingStructs).not.toBeNull()
    for (const update of gap) Y.applyUpdate(receiver, update)
    expect(receiver.getText('t').toString()).toBe(writer.getText('t').toString())
    ;[base, writer, other, receiver].forEach(doc => doc.destroy())
  })
})

describe('a block move racing typing and a delete, then undo on every replica', () => {
  function trio() {
    const { alice, bob } = pair()
    const charlie = replica(Y.encodeStateAsUpdate(alice.doc))
    return { alice, bob, charlie }
  }
  const sync = (all: ReturnType<typeof replica>[]) => { for (const a of all) for (const b of all) if (a !== b) exchange(a, b) }
  const blockAt = (editor: Editor, index: number) => { let pos = 0; for (let i = 0; i < index; i++) pos += editor.state.doc.child(i).nodeSize; return pos }

  it.each([
    ['alice', 'bob', 'charlie'], ['charlie', 'bob', 'alice'], ['bob', 'charlie', 'alice'],
  ] as const)('keeps every replica equal and restores the original page after undo in the order %s, %s, %s', (...order) => {
    const replicas = trio()
    const { alice, bob, charlie } = replicas
    const all = [alice, bob, charlie]
    moveBlock(alice.editor, secondBlock(alice.editor), 1) // Editable block → end
    bob.editor.commands.insertContentAt(1, 'BOB ') // types into Before
    const after = blockAt(charlie.editor, 2)
    charlie.editor.commands.deleteRange({ from: after, to: after + charlie.editor.state.doc.child(2).nodeSize }) // deletes After
    sync(all)
    expect(alice.editor.getText()).toBe('BOB Before\n\nEditable block')
    for (const r of all) expect(r.editor.getJSON()).toEqual(alice.editor.getJSON())

    for (const name of order) {
      expect(replicas[name].editor.commands.undo(), name).toBe(true)
      sync(all)
      for (const r of all) expect(r.editor.getJSON()).toEqual(alice.editor.getJSON())
    }
    expect(alice.editor.getText()).toBe('Before\n\nEditable block\n\nAfter')
  })
})
