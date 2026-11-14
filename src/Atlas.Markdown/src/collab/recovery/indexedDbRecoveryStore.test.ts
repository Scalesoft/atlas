import { afterEach, describe, expect, it, vi } from 'vitest'
import { cloneRecoveryCheckpoint, createIndexedDbRecoveryStore, normalizeRecoveryScope, summarizeRecoveryCheckpoint } from './indexedDbRecoveryStore'
import { RecoveryStoreError, type RecoveryCheckpoint } from './types'

const scope = { serverId: 'https://server.example/hub', tenantId: 'tenant', accountId: 'account', documentId: 'document' }
const branchId = 'AABBCCDD-0000-4000-8000-001122334455'
function checkpoint(): RecoveryCheckpoint {
  return { formatVersion: 1, scope: { ...scope }, branchId, revision: 1, capturedAt: 1_788_626_000_000,
    schema: { version: 1, profile: 'rich', plugins: [{ id: 'custom', version: 2 }] }, fragment: 'default',
    observedSequence: 0, documentRevision: 0, connectionGeneration: 0, epoch: null, replayPolicy: 'manual-only',
    yjsState: new Uint8Array([1, 2, 3]), pendingUpdates: [{ id: 'pending', update: new Uint8Array([4, 5]), updateCount: 2, attempted: true }] }
}
afterEach(() => vi.unstubAllGlobals())

describe('recovery checkpoint data ownership and metadata', () => {
  it('owns binary subarrays, scope and normalized schema independently from caller mutation', () => {
    const value = checkpoint(), backing = new Uint8Array([9, 1, 2, 3, 9])
    const input = { ...value, yjsState: backing.subarray(1, 4) }
    const copy = cloneRecoveryCheckpoint(input)
    backing.fill(8); value.pendingUpdates[0]!.update.fill(7)
    Object.assign(value.scope, { accountId: 'another' })
    Object.assign(value.schema!.plugins[0]!, { version: 99 })
    expect(copy.yjsState).toEqual(new Uint8Array([1, 2, 3]))
    expect(copy.yjsState.buffer.byteLength).toBe(3)
    expect(copy.pendingUpdates[0]!.update).toEqual(new Uint8Array([4, 5]))
    expect(copy.scope.accountId).toBe('account')
    expect(copy.schema!.plugins[0]!.version).toBe(2)
    expect(copy.branchId).toBe(branchId.toLowerCase())
  })

  it('accepts schema null, empty binary data and no pending items without claiming Yjs validity', () => {
    const copy = cloneRecoveryCheckpoint({ ...checkpoint(), schema: null, yjsState: new Uint8Array(), pendingUpdates: [] })
    expect(copy.schema).toBeNull()
    expect(copy.pendingUpdates).toEqual([])
    expect(summarizeRecoveryCheckpoint(copy).pendingCount).toBe(0)
  })

  it('reports only summary metadata and charges UTF8 metadata as well as binary data', () => {
    const copy = cloneRecoveryCheckpoint(checkpoint()), summary = summarizeRecoveryCheckpoint(copy)
    expect(Object.keys(summary).sort()).toEqual(['branchId', 'byteLength', 'capturedAt', 'pendingCount', 'revision', 'scope'])
    expect(summary.pendingCount).toBe(2)
    expect(summary.byteLength).toBeGreaterThan(5)
    const larger = summarizeRecoveryCheckpoint(cloneRecoveryCheckpoint({ ...checkpoint(), yjsState: new Uint8Array(13) }))
    expect(larger.byteLength - summary.byteLength).toBe(10)
    const unicode = summarizeRecoveryCheckpoint(cloneRecoveryCheckpoint({ ...checkpoint(), fragment: 'žžžžžžž' }))
    expect(unicode.byteLength - summary.byteLength).toBe(7)
  })

  it.each(['serverId', 'tenantId', 'accountId', 'documentId'])('requires exact valid scope field %s', field => {
    for (const value of [undefined, null, '', ' ', ' leading', 'trailing ', '\u0000hidden', 'a'.repeat(1025), 1]) {
      expect(() => normalizeRecoveryScope({ ...scope, [field]: value })).toThrow(RecoveryStoreError)
    }
  })

  it('preserves delimiter-bearing scope identities instead of flattening them', () => {
    expect(normalizeRecoveryScope({ ...scope, tenantId: 'a|b', accountId: 'c' })).not.toEqual(
      normalizeRecoveryScope({ ...scope, tenantId: 'a', accountId: 'b|c' }))
  })

  it.each(['revision', 'capturedAt', 'observedSequence', 'documentRevision', 'connectionGeneration'])('rejects malformed %s metadata', field => {
    for (const value of [undefined, null, '1', true, -1, 0.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
      expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), [field]: value })).toThrow(RecoveryStoreError)
    }
  })

  it.each([
    ['revision', 0], ['branchId', 'not-a-uuid'], ['branchId', '00000000-0000-0000-0000-000000000000x'],
    ['formatVersion', 2], ['formatVersion', '1'], ['epoch', 'server-epoch'], ['epoch', undefined],
    ['replayPolicy', 'automatic'], ['fragment', ''], ['fragment', 'x'.repeat(257)],
    ['schema', undefined], ['schema', { version: 9, profile: 'rich', plugins: [] }],
    ['yjsState', [1, 2]], ['yjsState', new ArrayBuffer(4)], ['yjsState', new Uint16Array([1])],
    ['pendingUpdates', null], ['pendingUpdates', {}],
  ])('rejects invalid checkpoint %s=%s', (field, value) => {
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), [field as string]: value })).toThrow(RecoveryStoreError)
  })

  it.each([
    ['id', ''], ['id', ' pending'], ['id', 1], ['update', [1]], ['update', null],
    ['updateCount', 0], ['updateCount', -1], ['updateCount', 1.2], ['updateCount', '1'],
    ['updateCount', Number.MAX_SAFE_INTEGER + 1], ['attempted', 0], ['attempted', 'false'],
  ])('rejects malformed pending %s=%s', (field, value) => {
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), pendingUpdates: [{ ...checkpoint().pendingUpdates[0], [field as string]: value }] })).toThrow(RecoveryStoreError)
  })

  it('rejects duplicate pending ids and unsafe aggregate counts', () => {
    const pending = checkpoint().pendingUpdates[0]!
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), pendingUpdates: [pending, pending] })).toThrow(RecoveryStoreError)
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), pendingUpdates: [
      { ...pending, updateCount: Number.MAX_SAFE_INTEGER }, { ...pending, id: 'other' },
    ] })).toThrow(RecoveryStoreError)
  })

  it('rejects accessors at checkpoint, scope, schema and array boundaries without invoking them', () => {
    const getter = vi.fn(() => { throw new Error('must never be invoked') })
    const root = Object.defineProperty(checkpoint(), 'revision', { enumerable: true, get: getter })
    const accessorScope = Object.defineProperty({ ...scope }, 'accountId', { enumerable: true, get: getter })
    const schema = Object.defineProperty({}, 'version', { enumerable: true, get: getter })
    const pending = Object.defineProperty([checkpoint().pendingUpdates[0]], '0', { enumerable: true, get: getter })
    for (const value of [root, { ...checkpoint(), scope: accessorScope }, { ...checkpoint(), schema }, { ...checkpoint(), pendingUpdates: pending }]) {
      expect(() => cloneRecoveryCheckpoint(value)).toThrow()
    }
    expect(getter).not.toHaveBeenCalled()
  })

  it('rejects unknown fields, sparse arrays and array custom properties', () => {
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), token: 'secret' })).toThrow(RecoveryStoreError)
    expect(() => normalizeRecoveryScope({ ...scope, token: 'secret' })).toThrow(RecoveryStoreError)
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), pendingUpdates: new Array(1) })).toThrow(RecoveryStoreError)
    const array = Object.assign([...checkpoint().pendingUpdates], { extra: 1 })
    expect(() => cloneRecoveryCheckpoint({ ...checkpoint(), pendingUpdates: array })).toThrow(RecoveryStoreError)
  })
})

describe('recovery factory and opening lifecycle', () => {
  it('opens lazily and reports unavailable storage only when used', async () => {
    vi.stubGlobal('indexedDB', undefined)
    const store = createIndexedDbRecoveryStore()
    await expect(store.list(scope)).rejects.toMatchObject({ code: 'unavailable' })
    store.close()
    await expect(store.list(scope)).rejects.toMatchObject({ code: 'closed' })
  })

  it.each(['maxCheckpointBytes', 'maxTotalBytes'])('rejects invalid %s options synchronously', field => {
    for (const value of [0, -1, 0.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
      expect(() => createIndexedDbRecoveryStore({ [field]: value })).toThrow(RecoveryStoreError)
    }
  })

  it('rejects invalid input before attempting to open storage', async () => {
    const open = vi.fn()
    vi.stubGlobal('indexedDB', { open })
    const store = createIndexedDbRecoveryStore()
    await expect(store.write({ ...checkpoint(), revision: 0 })).rejects.toMatchObject({ code: 'invalid' })
    expect(open).not.toHaveBeenCalled()
  })

  it('rejects checkpoint budget before attempting to open storage', async () => {
    const open = vi.fn()
    vi.stubGlobal('indexedDB', { open })
    const store = createIndexedDbRecoveryStore({ maxCheckpointBytes: 1 })
    await expect(store.write(checkpoint())).rejects.toMatchObject({ code: 'quota' })
    expect(open).not.toHaveBeenCalled()
  })

  it.each(['close', 'blocked'])('settles %s during pending open and closes any late connection', async action => {
    const opened = { close: vi.fn() }
    const request = { result: opened } as unknown as IDBOpenDBRequest
    vi.stubGlobal('indexedDB', { open: vi.fn(() => request) })
    const store = createIndexedDbRecoveryStore(), operation = store.list(scope)
    const rejected = expect(operation).rejects.toMatchObject({ code: action === 'close' ? 'closed' : 'blocked' })
    if (action === 'close') store.close()
    else request.onblocked!.call(request, {} as IDBVersionChangeEvent)
    await rejected
    request.onsuccess!.call(request, {} as Event)
    expect(opened.close).toHaveBeenCalledOnce()
  })
})
