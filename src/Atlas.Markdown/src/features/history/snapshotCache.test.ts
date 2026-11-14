import { expect, it, vi } from 'vitest'
import { createHistorySnapshotCache } from './snapshotCache'
import type { MarkdownHistorySnapshot, MarkdownHistorySource } from './types'
const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [] } } as const
function snapshot(id: string): MarkdownHistorySnapshot { return { entry: { id, author: 'Anna', label: 'Edit', kind: 'edit', timestamp: '2026-01-01T00:00:00Z' }, approved: value } }
function source() { return { page: vi.fn(), snapshot: vi.fn(async ({ id }: { id: string; signal: AbortSignal }) => snapshot(id)) } satisfies MarkdownHistorySource }

it('deduplicates in-flight reads and caches immutable checkpoints', async () => {
  const api = source(), cache = createHistorySnapshotCache(api)
  const a = cache.get('one'), b = cache.get('one')
  expect(a).toBe(b); await a; await cache.get('one'); expect(api.snapshot).toHaveBeenCalledTimes(1)
})
it('evicts the least recently used value at capacity', async () => {
  const api = source(), cache = createHistorySnapshotCache(api, 2)
  await cache.get('a'); await cache.get('b'); await cache.get('a'); await cache.get('c'); await cache.get('b')
  expect(api.snapshot.mock.calls.map(([request]) => request.id)).toEqual(['a', 'b', 'c', 'b'])
})
it('does not cache failures and permits retry', async () => {
  const api = source(); api.snapshot.mockRejectedValueOnce(new Error('offline'))
  const cache = createHistorySnapshotCache(api)
  await expect(cache.get('a')).rejects.toThrow('offline'); await expect(cache.get('a')).resolves.toMatchObject({ entry: { id: 'a' } })
})
it('rejects a response for the wrong checkpoint', async () => {
  const api = source(); api.snapshot.mockResolvedValueOnce(snapshot('other'))
  await expect(createHistorySnapshotCache(api).get('requested')).rejects.toThrow('different checkpoint')
})
it('aborts outstanding requests on disposal and never reuses a disposed cache', async () => {
  const api = source(); let finish!: (snapshot: MarkdownHistorySnapshot) => void
  api.snapshot.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
  const cache = createHistorySnapshotCache(api); const read = cache.get('a'); await Promise.resolve()
  const signal = api.snapshot.mock.calls[0]![0].signal; cache.dispose(); expect(signal.aborted).toBe(true)
  finish(snapshot('a')); await read; await expect(cache.get('a')).rejects.toThrow('disposed')
})
it('does not share cached content across hosts or permission contexts', async () => {
  const first = source(), second = source(); await createHistorySnapshotCache(first).get('a'); await createHistorySnapshotCache(second).get('a')
  expect(first.snapshot).toHaveBeenCalledTimes(1); expect(second.snapshot).toHaveBeenCalledTimes(1)
})
it.each([0, -1, .5])('rejects invalid cache capacity %s', capacity => {
  expect(() => createHistorySnapshotCache(source(), capacity)).toThrow(RangeError)
})
