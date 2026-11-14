// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { attachRecovery, createMarkdownRecovery, type MarkdownRecoveryOptions, type RecoveryCapturePort } from './session'
import type { RecoveryCheckpoint, RecoveryScope, RecoveryStore, RecoverySummary } from './types'

const scope: RecoveryScope = { serverId: 'server', tenantId: 'tenant', accountId: 'account', documentId: 'doc' }
const ports: RecoveryCapturePort[] = []
afterEach(() => { ports.splice(0).forEach(port => port.detach()); vi.useRealTimers() })
function deferred() {
  let resolve!: () => void, reject!: (reason: Error) => void
  const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
class MemoryStore implements RecoveryStore {
  writes: RecoveryCheckpoint[] = []
  waits: Promise<void>[] = []
  saved: RecoveryCheckpoint | null = null
  async write(checkpoint: RecoveryCheckpoint) {
    this.writes.push(checkpoint)
    if (this.waits.length) await this.waits.shift()
    this.saved = checkpoint
  }
  async read() { return this.saved }
  async list(): Promise<RecoverySummary[]> { return [] }
  async discard() { this.saved = null }
  close() {}
}
function setup(options: Partial<MarkdownRecoveryOptions> = {}) {
  const store = new MemoryStore()
  const recovery = createMarkdownRecovery({ store, scope, ...options })
  let revision = 0
  const capture = vi.fn(() => ({ schema: null, fragment: 'default', observedSequence: revision,
    documentRevision: revision, connectionGeneration: 0, yjsState: new Uint8Array([0, 0]), pendingUpdates: [] }))
  const port = attachRecovery(recovery, 'doc', capture); ports.push(port)
  return { store, recovery, port, capture, edit: () => { revision++; port.markDirty() } }
}

describe('manual recovery checkpoint scheduling', () => {
  it('does not capture or write merely by creating a handle', async () => {
    const store = new MemoryStore(), recovery = createMarkdownRecovery({ store, scope })
    expect(recovery.getState().status).toBe('idle')
    expect(store.writes).toHaveLength(0)
    await expect(recovery.checkpoint()).rejects.toThrow('not attached')
  })
  it('captures only after the entire synchronous editing callback has finished', async () => {
    const run = setup(); const checkpoint = run.recovery.checkpoint()
    expect(run.capture).not.toHaveBeenCalled()
    run.edit()
    await checkpoint
    expect(run.store.saved?.documentRevision).toBe(1)
    expect(run.store.saved?.replayPolicy).toBe('manual-only')
    expect(run.store.saved?.epoch).toBeNull()
    expect(run.recovery.getState()).toMatchObject({ status: 'recorded', dirty: false })
  })
  it('coalesces concurrent checkpoint callers into one complete write', async () => {
    const run = setup()
    const summaries = await Promise.all(Array.from({ length: 50 }, () => run.recovery.checkpoint()))
    expect(run.store.writes).toHaveLength(1)
    expect(new Set(summaries.map(summary => summary.revision)).size).toBe(1)
  })
  it('keeps periodic edits cheap until the debounce expires', async () => {
    vi.useFakeTimers(); const run = setup()
    for (let index = 0; index < 50; index++) run.edit()
    expect(run.capture).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(999)
    expect(run.capture).not.toHaveBeenCalled()
    await vi.advanceTimersByTimeAsync(1)
    expect(run.store.writes).toHaveLength(1)
    expect(run.store.saved?.documentRevision).toBe(50)
  })
  it('reaches max wait while continuous edits keep moving the debounce', async () => {
    vi.useFakeTimers(); const run = setup({ checkpointDelayMs: 100, maxWaitMs: 500 })
    for (let index = 0; index < 5; index++) { run.edit(); await vi.advanceTimersByTimeAsync(90) }
    expect(run.store.writes).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(50)
    expect(run.store.writes).toHaveLength(1)
  })
  it('does not report recorded until storage has completed', async () => {
    const run = setup(), gate = deferred(); run.store.waits.push(gate.promise)
    const checkpoint = run.recovery.checkpoint()
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(1))
    expect(run.recovery.getState()).toMatchObject({ status: 'recording', dirty: true, lastRecorded: null })
    gate.resolve(); await checkpoint
    expect(run.recovery.getState()).toMatchObject({ status: 'recorded', dirty: false })
  })
  it('a newer checkpoint request waits for its own revision after a slow previous write', async () => {
    const run = setup(), first = deferred(), second = deferred()
    run.store.waits.push(first.promise, second.promise)
    const oldCheckpoint = run.recovery.checkpoint()
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(1))
    run.edit(); let done = false
    const newCheckpoint = run.recovery.checkpoint().then(result => { done = true; return result })
    first.resolve(); await oldCheckpoint
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(2))
    expect(done).toBe(false)
    expect(run.recovery.getState().dirty).toBe(true)
    second.resolve(); const summary = await newCheckpoint
    expect(summary.revision).toBe(2)
    expect(run.store.saved?.documentRevision).toBe(1)
    expect(run.recovery.getState().dirty).toBe(false)
  })
  it('preserves the last good checkpoint when the next storage write fails and supports retry', async () => {
    const run = setup(); const first = await run.recovery.checkpoint()
    const gate = deferred(); run.store.waits.push(gate.promise); run.edit()
    const failed = run.recovery.checkpoint()
    const rejection = expect(failed).rejects.toThrow('quota')
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(2))
    gate.reject(new Error('quota')); await rejection
    expect(run.recovery.getState()).toMatchObject({ status: 'degraded', dirty: true, lastRecorded: first })
    const retry = await run.recovery.checkpoint()
    expect(retry.revision).toBe(3)
    expect(run.recovery.getState()).toMatchObject({ status: 'recorded', error: null, dirty: false })
  })
  it('handles capture failure without a storage call and can retry', async () => {
    const run = setup(); run.capture.mockImplementationOnce(() => { throw new Error('capture failed') })
    await expect(run.recovery.checkpoint()).rejects.toThrow('capture failed')
    expect(run.store.writes).toHaveLength(0)
    expect(run.recovery.getState().status).toBe('degraded')
    await run.recovery.checkpoint()
    expect(run.store.writes).toHaveLength(1)
  })
  it('never captures a destroyed source from a pending microtask', async () => {
    const run = setup(); const checkpoint = run.recovery.checkpoint(); run.port.detach()
    await expect(checkpoint).rejects.toThrow('not attached')
    expect(run.capture).not.toHaveBeenCalled()
    expect(run.recovery.getState().status).toBe('closed')
  })
  it('allows already detached bytes to finish persistence after provider teardown', async () => {
    const run = setup(), gate = deferred(); run.store.waits.push(gate.promise)
    const checkpoint = run.recovery.checkpoint()
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(1))
    run.port.detach(); run.capture.mockImplementation(() => { throw new Error('destroyed Y.Doc') })
    gate.resolve(); await checkpoint
    expect(run.recovery.getState()).toMatchObject({ status: 'closed', dirty: false })
    expect(run.capture).toHaveBeenCalledTimes(1)
  })
  it('does not falsely fulfill newer work after closing during an older write', async () => {
    const run = setup(), gate = deferred(); run.store.waits.push(gate.promise)
    const first = run.recovery.checkpoint()
    await vi.waitFor(() => expect(run.store.writes).toHaveLength(1))
    run.edit(); const later = run.recovery.checkpoint(); const rejection = expect(later).rejects.toThrow('not attached')
    run.port.detach(); gate.resolve(); await first; await rejection
    expect(run.recovery.getState().dirty).toBe(true)
  })
  it('clears timers on teardown and never takes another snapshot', async () => {
    vi.useFakeTimers(); const run = setup(); run.port.detach()
    await vi.advanceTimersByTimeAsync(60_000)
    expect(run.capture).not.toHaveBeenCalled()
    expect(vi.getTimerCount()).toBe(0)
  })
  it('keeps callback exceptions observational and supports unsubscribe', async () => {
    const run = setup(), listener = vi.fn()
    run.recovery.subscribe(() => { throw new Error('observer') })
    const stop = run.recovery.subscribe(listener); stop(); const before = listener.mock.calls.length
    await run.recovery.checkpoint()
    expect(run.recovery.getState().status).toBe('recorded')
    expect(listener).toHaveBeenCalledTimes(before)
  })
  it('reuses a recorded checkpoint without serializing unchanged content again', async () => {
    const run = setup(); const first = await run.recovery.checkpoint()
    const second = await run.recovery.checkpoint()
    expect(second).toEqual(first); expect(run.capture).toHaveBeenCalledTimes(1)
  })
  it('gives simultaneous sessions independent branches even for the same document', async () => {
    const first = setup(), second = setup()
    await Promise.all([first.recovery.checkpoint(), second.recovery.checkpoint()])
    expect(first.store.saved?.branchId).not.toBe(second.store.saved?.branchId)
  })
  it('rejects an already used handle, even after its original provider closes', () => {
    const run = setup(); run.port.detach()
    expect(() => attachRecovery(run.recovery, 'doc', run.capture)).toThrow('fresh handle')
  })
  it('rejects another document and forged handles before capturing', () => {
    const run = setup()
    expect(() => attachRecovery(run.recovery, 'other', run.capture)).toThrow()
    expect(() => attachRecovery({ ...run.recovery }, 'doc', run.capture)).toThrow()
    expect(run.capture).not.toHaveBeenCalled()
  })
  it.each([
    { checkpointDelayMs: 0 }, { checkpointDelayMs: -1 }, { checkpointDelayMs: 1.5 },
    { checkpointDelayMs: NaN }, { checkpointDelayMs: 60_001 }, { maxWaitMs: 999 },
    { maxWaitMs: Infinity }, { maxWaitMs: 60_001 },
  ])('rejects unsafe timing configuration %j', options => {
    expect(() => createMarkdownRecovery({ store: new MemoryStore(), scope, ...options })).toThrow()
  })
})
