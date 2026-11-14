import type { MarkdownHistorySnapshot, MarkdownHistorySource } from './types'

/** Per-component cache: never shared between documents or authentication contexts. */
export function createHistorySnapshotCache(source: MarkdownHistorySource, capacity = 12) {
  if (!Number.isInteger(capacity) || capacity < 1) throw new RangeError('History cache capacity must be positive.')
  const values = new Map<string, MarkdownHistorySnapshot>()
  const pending = new Map<string, { controller: AbortController; promise: Promise<MarkdownHistorySnapshot> }>()
  let disposed = false
  function get(id: string): Promise<MarkdownHistorySnapshot> {
    if (disposed) return Promise.reject(new Error('History cache is disposed.'))
    const cached = values.get(id)
    if (cached) { values.delete(id); values.set(id, cached); return Promise.resolve(cached) }
    const existing = pending.get(id)
    if (existing) return existing.promise
    const controller = new AbortController()
    const promise = Promise.resolve().then(() => source.snapshot({ id, signal: controller.signal })).then(snapshot => {
      if (snapshot.entry.id !== id) throw new Error('History source returned a different checkpoint.')
      if (!disposed) {
        values.set(id, snapshot)
        while (values.size > capacity) values.delete(values.keys().next().value!)
      }
      return snapshot
    }).finally(() => pending.delete(id))
    pending.set(id, { controller, promise })
    return promise
  }
  return {
    get,
    dispose() { disposed = true; for (const request of pending.values()) request.controller.abort(); pending.clear(); values.clear() },
  }
}
