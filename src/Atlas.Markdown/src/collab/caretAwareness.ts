import type { Awareness } from 'y-protocols/awareness'

type Changes = { added: number[]; updated: number[]; removed: number[] }
type Listener = (changes: Changes, origin: unknown) => void

/**
 * The awareness the caret plugin (y-tiptap's yCursorPlugin) listens to: its 'change' only when a peer comes, goes, or moves its caret or
 * changes its name. A host's other fields ride the same state (Atlas.Web's mouse pointers, 20 a second per co-editor), and every 'change'
 * the plugin sees is a transaction of the whole editor that rebuilds every caret. Everything else, 'update' included, is the awareness itself.
 */
export function caretAwareness(awareness: Awareness): Awareness {
  const listeners = new Set<Listener>()
  const shown = new Map<number, string>()
  const caretOf = (clientId: number) => {
    const state = awareness.getStates().get(clientId) as { cursor?: unknown; user?: unknown } | undefined
    return JSON.stringify([state?.cursor ?? null, state?.user ?? null])
  }
  const relay = ({ added, updated, removed }: Changes, origin: unknown) => {
    added.forEach(id => shown.set(id, caretOf(id)))
    removed.forEach(id => shown.delete(id))
    const moved = updated.filter(id => {
      const caret = caretOf(id)
      if (shown.get(id) === caret) return false
      shown.set(id, caret)
      return true
    })
    if (!added.length && !removed.length && !moved.length) return
    listeners.forEach(listener => listener({ added, updated: moved, removed }, origin))
  }
  const on = (name: string, listener: Listener) => {
    if (name !== 'change') return awareness.on(name as 'update', listener)
    if (!listeners.size) {
      awareness.getStates().forEach((_, id) => shown.set(id, caretOf(id)))
      awareness.on('change', relay)
    }
    listeners.add(listener)
  }
  const off = (name: string, listener: Listener) => {
    if (name !== 'change') return awareness.off(name as 'update', listener)
    listeners.delete(listener)
    if (!listeners.size) { awareness.off('change', relay); shown.clear() }
  }
  return new Proxy(awareness, { get: (target, property) => property === 'on' ? on : property === 'off' ? off : Reflect.get(target, property) })
}
