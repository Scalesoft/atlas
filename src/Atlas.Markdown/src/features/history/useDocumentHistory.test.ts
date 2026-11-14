import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { effectScope, nextTick, shallowRef } from 'vue'
import { useDocumentHistory } from './useDocumentHistory'
import type { MarkdownHistoryEntry, MarkdownHistoryPage, MarkdownHistorySnapshot, MarkdownHistorySource } from './types'
const value = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [] } } as const
const entry = (id: string, second = 0): MarkdownHistoryEntry => ({ id, timestamp: new Date(100000 + second * 1000).toISOString(), author: 'A', label: 'Edit', kind: 'edit' })
const snap = (id: string): MarkdownHistorySnapshot => ({ entry: entry(id), approved: value })
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (reason: unknown) => void; const promise = new Promise<T>((a, b) => { resolve = a; reject = b }); return { promise, resolve, reject } }
const scopes: ReturnType<typeof effectScope>[] = []
function mount(api?: MarkdownHistorySource) {
  const source = shallowRef(api ?? { page: vi.fn(async () => ({ entries: [entry('a', 2), entry('b', 1), entry('c')] })), snapshot: vi.fn(async ({ id }) => snap(id)) })
  const scope = effectScope(); scopes.push(scope)
  return { state: scope.run(() => useDocumentHistory(source))!, source, scope }
}
beforeEach(() => vi.useFakeTimers())
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); vi.useRealTimers() })
async function settle() { await nextTick(); await vi.advanceTimersByTimeAsync(100) }

it('loads metadata first and only the selected full snapshot', async () => {
  const { state, source } = mount(); await settle()
  expect(state.selectedId.value).toBe('a'); expect(state.snapshot.value?.entry.id).toBe('a')
  expect(source.value.snapshot).toHaveBeenCalledTimes(1)
})
it('debounces rapid scrubbing instead of fetching each pixel', async () => {
  const { state, source } = mount(); await nextTick()
  for (const id of ['b', 'a', 'b', 'c']) { state.selectedId.value = id; await nextTick(); await vi.advanceTimersByTimeAsync(10) }
  await settle(); expect(source.value.snapshot).toHaveBeenCalledTimes(1); expect(state.snapshot.value?.entry.id).toBe('c')
})
it('ignores an old snapshot which arrives after a newer selection', async () => {
  const old = deferred<MarkdownHistorySnapshot>(), api = { page: vi.fn(async () => ({ entries: [entry('a', 2), entry('b')] })), snapshot: vi.fn(({ id }: { id: string }) => id === 'a' ? old.promise : Promise.resolve(snap(id))) }
  const { state } = mount(api); await settle(); state.selectedId.value = 'b'; await settle()
  old.resolve(snap('a')); await settle(); expect(state.snapshot.value?.entry.id).toBe('b')
})
it('clears the old preview immediately while another version is loading', async () => {
  const { state } = mount(); await settle(); state.selectedId.value = 'b'; await nextTick()
  expect(state.snapshot.value).toBeUndefined(); expect(state.loadingSnapshot.value).toBe(true)
})
it('source change aborts old requests and isolates private cache entries', async () => {
  const pending = deferred<MarkdownHistoryPage>(); let signal!: AbortSignal
  const { state, source } = mount({ page: ({ signal: next }) => { signal = next; return pending.promise }, snapshot: vi.fn() })
  source.value = { page: async () => ({ entries: [entry('new')] }), snapshot: async () => snap('new') }; await settle()
  expect(signal.aborted).toBe(true); pending.resolve({ entries: [entry('private')] }); await settle()
  expect(state.entries.value.map(entry => entry.id)).toEqual(['new'])
})
it('unmount aborts IO and cancels delayed snapshot reads', async () => {
  const { source, scope } = mount(); await nextTick(); scope.stop(); await settle(); expect(source.value.snapshot).not.toHaveBeenCalled()
})
it('snapshot error can be retried without reloading metadata', async () => {
  const api = { page: vi.fn(async () => ({ entries: [entry('a')] })), snapshot: vi.fn(async () => snap('a')).mockRejectedValueOnce(new Error('offline')) }
  const { state } = mount(api); await settle(); expect(state.error.value).toBe('offline')
  await state.retry(); expect(state.snapshot.value?.entry.id).toBe('a'); expect(api.page).toHaveBeenCalledTimes(1)
})
it('older-page retry retains selected checkpoint and retries the same cursor', async () => {
  const api = { page: vi.fn<MarkdownHistorySource['page']>().mockResolvedValueOnce({ entries: [entry('a', 2)], nextCursor: 'cursor' }).mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce({ entries: [entry('b')] }), snapshot: vi.fn(async ({ id }: { id: string }) => snap(id)) }
  const { state } = mount(api); await settle(); await state.load(true); await state.retry()
  expect(api.page.mock.calls[2]![0].cursor).toBe('cursor'); expect(state.selectedId.value).toBe('a'); expect(state.entries.value).toHaveLength(2)
})
it.each([
  { entries: [entry('b')], nextCursor: 'cursor' },
  { entries: [], nextCursor: 'another' },
  { entries: [entry('bad', 100)] },
  { entries: [{ ...entry('bad'), timestamp: 'invalid' }] },
])('rejects invalid next pages without corrupting current metadata: %j', async page => {
  const api = { page: vi.fn<MarkdownHistorySource['page']>().mockResolvedValueOnce({ entries: [entry('a', 2)], nextCursor: 'cursor' }).mockResolvedValueOnce(page), snapshot: async () => snap('a') }
  const { state } = mount(api); await settle(); await state.load(true)
  expect(state.error.value).not.toBe(''); expect(state.entries.value.map(entry => entry.id)).toEqual(['a'])
})
it('overlapping page boundary deduplicates IDs without losing equal-timestamp events', async () => {
  const api = { page: vi.fn<MarkdownHistorySource['page']>().mockResolvedValueOnce({ entries: [entry('a')], nextCursor: 'cursor' }).mockResolvedValueOnce({ entries: [entry('a'), entry('b')] }), snapshot: async () => snap('a') }
  const { state } = mount(api); await settle(); await state.load(true); expect(state.entries.value.map(entry => entry.id)).toEqual(['a', 'b'])
})
it('returning to a visited checkpoint uses cache', async () => {
  const { state, source } = mount(); await settle(); state.selectedId.value = 'b'; await settle(); state.selectedId.value = 'a'; await settle()
  expect(source.value.snapshot).toHaveBeenCalledTimes(2)
})
