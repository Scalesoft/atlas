import { expect, it } from 'vitest'
import * as Y from 'yjs'
import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate } from 'y-protocols/awareness'
import { caretAwareness } from './caretAwareness'

// The caret plugin (y-tiptap's yCursorPlugin) turns every 'change' it hears into a transaction of the whole editor that rebuilds every
// caret. 40 co-editors with a caret each, each moving the mouse 20 times (a second of Atlas.Web's pointers): the plugin hears none of it;
// a caret that moves, a new name, an arrival and a leave it hears, once each. Everything else is the awareness itself.
it('tells the caret plugin of carets and names only, not of the pointer samples of many co-editors (perf guard)', () => {
  const awareness = new Awareness(new Y.Doc())
  const carets = caretAwareness(awareness)
  const heard: { added: number[]; updated: number[]; removed: number[] }[] = []
  let raw = 0, updates = 0
  const listener = (changes: (typeof heard)[number]) => { heard.push(changes) }
  carets.on('change', listener)
  awareness.on('change', () => { raw++ })
  carets.on('update', () => { updates++ })
  const send = (peer: Awareness) => applyAwarenessUpdate(awareness, encodeAwarenessUpdate(peer, [peer.clientID]), 'remote')
  const caret = (index: number) => ({ anchor: { type: null, tname: 'default', item: null, assoc: index }, head: { type: null, tname: 'default', item: null, assoc: index } })
  const peers = Array.from({ length: 40 }, (_, index) => {
    const peer = new Awareness(new Y.Doc())
    peer.setLocalState({ user: { id: `u${index}`, name: `Kolega ${index}`, color: '#7c3aed' }, cursor: caret(index) })
    send(peer)
    return peer
  })
  expect(heard).toHaveLength(40)
  heard.length = 0
  raw = 0

  const started = performance.now()
  for (let sample = 0; sample < 20; sample++) {
    for (const peer of peers) {
      peer.setLocalStateField('pointer', { anchor: null, x: sample, y: 4 })
      send(peer)
    }
  }
  const elapsed = performance.now() - started
  expect(raw).toBe(800)
  expect(heard).toEqual([])
  expect(updates).toBeGreaterThanOrEqual(800)
  expect(elapsed).toBeLessThan(1000)

  peers[3]!.setLocalStateField('cursor', caret(10))
  send(peers[3]!)
  peers[4]!.setLocalStateField('user', { id: 'u4', name: 'Kolegyně 4', color: '#7c3aed' })
  send(peers[4]!)
  peers[5]!.setLocalState(null)
  send(peers[5]!)
  expect(heard).toEqual([
    { added: [], updated: [peers[3]!.clientID], removed: [] },
    { added: [], updated: [peers[4]!.clientID], removed: [] },
    { added: [], updated: [], removed: [peers[5]!.clientID] },
  ])
  // Everything else reads through: states, own id, local state.
  expect(carets.getStates()).toBe(awareness.getStates())
  expect(carets.clientID).toBe(awareness.clientID)
  carets.setLocalStateField('user', { name: 'Petr' })
  expect(awareness.getLocalState()).toEqual({ user: { name: 'Petr' } })
  expect(heard).toHaveLength(4)
  carets.off('change', listener)
  peers[3]!.setLocalStateField('cursor', caret(11))
  send(peers[3]!)
  expect(heard).toHaveLength(4)
  console.info(`caretAwareness: 800 pointer samples of 40 co-editors in ${elapsed.toFixed(1)} ms; 'change' to the caret plugin: 0 (raw awareness: 800)`)
  peers.forEach(peer => peer.destroy())
})
