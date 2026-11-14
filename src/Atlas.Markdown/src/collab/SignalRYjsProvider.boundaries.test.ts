// @vitest-environment jsdom
import * as Y from 'yjs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SignalRYjsProvider, type MarkdownSyncConnection } from './SignalRYjsProvider'

const base64 = (bytes: Uint8Array) => btoa(String.fromCharCode(...bytes))
const encoded = (doc: Y.Doc) => base64(Y.encodeStateAsUpdate(doc))
const documents: Y.Doc[] = []
const providers: SignalRYjsProvider[] = []
function document(text = '') {
  const doc = new Y.Doc(); documents.push(doc)
  if (text) doc.getText('content').insert(0, text)
  return doc
}
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>(done => { resolve = done })
  return { promise, resolve }
}

class BoundaryConnection implements MarkdownSyncConnection {
  calls: Array<{ method: string; args: unknown[] }> = []
  join: unknown = { snapshot: 'AAA=', updates: [], seq: 0, seedMarkdown: null, isSnapshotter: false, canWrite: true }
  acks: unknown[] = []
  private handlers = new Map<string, Array<(...args: any[]) => void>>()
  private reconnecting: Array<() => void> = []
  private reconnected: Array<() => void> = []
  async start() {}
  async stop() {}
  async send() {}
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args })
    if (method === 'JoinDocument' || method === 'JoinDocumentWithSchema') return this.join as T
    if (method === 'SendUpdate') return (this.acks.length ? this.acks.shift() : 1) as T
    return undefined as T
  }
  on(method: string, handler: (...args: any[]) => void) {
    this.handlers.set(method, [...this.handlers.get(method) ?? [], handler])
  }
  onReconnecting(handler: () => void) { this.reconnecting.push(handler) }
  onReconnected(handler: () => void) { this.reconnected.push(handler) }
  onClose() {}
  reconnect() { this.reconnecting.forEach(handler => handler()); this.reconnected.forEach(handler => handler()) }
  emit(method: string, ...args: unknown[]) { this.handlers.get(method)?.forEach(handler => handler(...args)) }
  count(method: string) { return this.calls.filter(call => call.method === method).length }
}

function setup(connection = new BoundaryConnection(), local = document()) {
  const fatal = vi.fn(), pending = vi.fn(), synced = vi.fn()
  const provider = new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc: local,
    onFatalError: fatal, onPendingUpdates: pending, onSynced: synced }, connection)
  providers.push(provider)
  return { provider, connection, local, fatal, pending, synced }
}
function joinState(remote: Y.Doc, canWrite: boolean, seq = 0) {
  return { snapshot: encoded(remote), updates: [], seq, seedMarkdown: null, isSnapshotter: false, canWrite }
}

afterEach(() => {
  providers.splice(0).forEach(provider => provider.destroy())
  documents.splice(0).forEach(doc => doc.destroy())
})

it('keeps an unavailable server offline throughout retries until the document is joined', async () => {
  vi.useFakeTimers()
  try {
    const connection = new BoundaryConnection()
    const retry = deferred<void>()
    const start = vi.spyOn(connection, 'start')
      .mockRejectedValueOnce(new Error('Unavailable'))
      .mockRejectedValueOnce(new Error('Unavailable'))
      .mockReturnValueOnce(retry.promise)
    const status = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc: document(), onStatus: status }, connection)
    providers.push(provider)
    const started = provider.start()
    await vi.advanceTimersByTimeAsync(0)
    expect(status).toHaveBeenLastCalledWith('offline')
    status.mockClear()
    await vi.advanceTimersByTimeAsync(1000)
    expect(start).toHaveBeenCalledTimes(3)
    expect(status.mock.calls.every(([value]) => value === 'offline')).toBe(true)
    expect(connection.count('JoinDocument')).toBe(0)
    retry.resolve()
    await started
    expect(connection.count('JoinDocument')).toBe(1)
    expect(status).toHaveBeenLastCalledWith('connected')
  } finally {
    vi.useRealTimers()
  }
})

describe('provider deletion coverage', () => {
  it('joins a clean reader with historical deletions without an append or false read-only failure', async () => {
    const remote = document('abc'); remote.getText('content').delete(1, 1)
    const run = setup(); run.connection.join = joinState(remote, false)
    await run.provider.start()
    expect(run.local.getText('content').toString()).toBe('ac')
    expect(run.fatal).not.toHaveBeenCalled()
    expect(run.synced).toHaveBeenLastCalledWith(true)
    expect(run.connection.count('SendUpdate')).toBe(0)
  })

  it('does not append historical deletions again when a writer rejoins', async () => {
    const remote = document('abc'); remote.getText('content').delete(1, 1)
    const run = setup(); run.connection.join = joinState(remote, true)
    await run.provider.start()
    run.connection.reconnect()
    await vi.waitFor(() => expect(run.connection.count('JoinDocument')).toBe(2))
    await vi.waitFor(() => expect(run.synced).toHaveBeenLastCalledWith(true))
    expect(run.connection.count('SendUpdate')).toBe(0)
    expect(run.fatal).not.toHaveBeenCalled()
  })

  it('retains a truly local deletion when a read-only join cannot merge it', async () => {
    const remote = document('abc'), local = document()
    Y.applyUpdate(local, Y.encodeStateAsUpdate(remote)); local.getText('content').delete(1, 1)
    const run = setup(new BoundaryConnection(), local); run.connection.join = joinState(remote, false)
    await run.provider.start()
    expect(run.fatal).toHaveBeenCalledOnce()
    expect(run.local.getText('content').toString()).toBe('ac')
    expect(run.connection.count('SendUpdate')).toBe(0)
    expect(run.synced).not.toHaveBeenLastCalledWith(true)
  })

  it.each([true, false])('reconciles an uncertain deletion using actual server coverage (covered=%s)', async covered => {
    const remote = document('abc'), acknowledgement = deferred<number>()
    const run = setup(); run.connection.join = joinState(remote, true)
    await run.provider.start()
    run.connection.acks.push(acknowledgement.promise)
    run.local.getText('content').delete(1, 1)
    await vi.waitFor(() => expect(run.connection.count('SendUpdate')).toBe(1))
    expect(run.pending).toHaveBeenLastCalledWith(1)
    if (covered) Y.applyUpdate(remote, Y.encodeStateAsUpdate(run.local))
    run.connection.join = joinState(remote, true, covered ? 1 : 0)
    run.connection.reconnect()
    acknowledgement.resolve(1) // old generation ACK must not discharge the pending entry
    await vi.waitFor(() => expect(run.connection.count('JoinDocument')).toBe(2))
    await vi.waitFor(() => expect(run.pending).toHaveBeenLastCalledWith(0))
    await vi.waitFor(() => expect(run.synced).toHaveBeenLastCalledWith(true))
    expect(run.connection.count('SendUpdate')).toBe(covered ? 1 : 2)
    expect(run.local.getText('content').toString()).toBe('ac')
    expect(run.fatal).not.toHaveBeenCalled()
  })
})

describe('provider untrusted wire boundary', () => {
  it.each([
    ['NaN', NaN], ['string', '1'], ['undefined', undefined], ['null', null],
    ['negative', -1], ['zero', 0], ['fraction', 1.5], ['unsafe', Number.MAX_SAFE_INTEGER + 1],
  ] as const)('rejects an invalid %s acknowledgement without discarding pending edits', async (_name, ack) => {
    const run = setup(); await run.provider.start()
    run.connection.acks.push(ack)
    run.local.getText('content').insert(0, 'retain this')
    await vi.waitFor(() => expect(run.fatal).toHaveBeenCalledOnce())
    expect(run.pending).toHaveBeenLastCalledWith(1)
    expect(run.synced).not.toHaveBeenLastCalledWith(true)
    expect(run.local.getText('content').toString()).toBe('retain this')
    expect(run.connection.count('SendUpdate')).toBe(1)
  })

  it.each([
    ['negative sequence', { seq: -1 }], ['string sequence', { seq: '0' }],
    ['unsafe sequence', { seq: Number.MAX_SAFE_INTEGER + 1 }], ['string write flag', { canWrite: 'false' }],
    ['numeric snapshotter flag', { isSnapshotter: 1 }], ['nonarray tail', { updates: {} }],
    ['null tail element', { updates: [null], seq: 1 }], ['object seed', { seedMarkdown: {} }],
  ] as const)('rejects invalid join metadata (%s) before touching the shared document', async (_name, patch) => {
    const remote = document('remote'), run = setup(new BoundaryConnection(), document('local'))
    const before = Y.encodeStateAsUpdate(run.local)
    run.connection.join = { ...joinState(remote, true), ...patch }
    await run.provider.start()
    expect(run.fatal).toHaveBeenCalledOnce()
    expect(Y.encodeStateAsUpdate(run.local)).toEqual(before)
    expect(run.connection.count('SendUpdate')).toBe(0)
  })

  it('does not apply a valid join prefix when a later update is malformed', async () => {
    const remote = document('remote'), updateDoc = document('tail')
    const run = setup(new BoundaryConnection(), document('local')), before = Y.encodeStateAsUpdate(run.local)
    run.connection.join = { ...joinState(remote, true, 2), updates: [encoded(updateDoc), 'not base64!'] }
    await run.provider.start()
    expect(run.fatal).toHaveBeenCalledOnce()
    expect(Y.encodeStateAsUpdate(run.local)).toEqual(before)
    expect(run.connection.count('SendUpdate')).toBe(0)
  })

  it('does not partially apply a live update whose delete-set encoding is truncated', async () => {
    const run = setup(); await run.provider.start()
    const remote = document('must not leak'), bytes = Y.encodeStateAsUpdate(remote)
    const before = Y.encodeStateAsUpdate(run.local)
    // An insert-only update ends in an empty delete set. Yjs integrates decoded structs
    // before it tries to read that last byte, so applyUpdate alone is not an atomic validator.
    run.connection.emit('ReceiveUpdate', 'doc', base64(bytes.subarray(0, bytes.length - 1)), 1)
    expect(run.fatal).toHaveBeenCalledOnce()
    expect(Y.encodeStateAsUpdate(run.local)).toEqual(before)
    expect(run.connection.count('SendUpdate')).toBe(0)
  })
})
