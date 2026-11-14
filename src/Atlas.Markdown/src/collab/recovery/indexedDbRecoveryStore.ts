import { clonePlainData } from '../../core/plainData'
import { normalizeDocumentSchema } from '../documentSchema'
import { moveLegacyDatabase } from './moveLegacyDatabase'
import { RecoveryStoreError, type RecoveryCheckpoint, type RecoveryPendingUpdate, type RecoveryScope, type RecoveryStore, type RecoverySummary } from './types'

const STORE = 'checkpoints'
const USAGE_STORE = 'usage'
const USAGE_KEY = 'totalBytes'
const SCOPE_INDEX = 'scope'
const DEFAULT_DATABASE = 'atlas-markdown-recovery'
const LEGACY_DATABASE = 'scalesoft-markdown-recovery'
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export interface IndexedDbRecoveryStoreOptions {
  readonly databaseName?: string
  readonly maxCheckpointBytes?: number
  readonly maxTotalBytes?: number
}

function invalid(message: string): never { throw new RecoveryStoreError('invalid', message) }
function record(value: unknown, fields: readonly string[], label: string): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) invalid(`${label} must be an object.`)
  const prototype = Object.getPrototypeOf(value)
  if (prototype !== null && Object.getPrototypeOf(prototype) !== null) invalid(`${label} must be a plain object.`)
  const result: Record<string, unknown> = Object.create(null)
  for (const key of Reflect.ownKeys(value)) {
    if (typeof key !== 'string' || !fields.includes(key)) invalid(`${label} contains an unsupported field.`)
    const descriptor = Object.getOwnPropertyDescriptor(value, key)
    if (!descriptor?.enumerable || !('value' in descriptor)) invalid(`${label} must contain data properties only.`)
    result[key] = descriptor.value
  }
  for (const field of fields) if (!Object.hasOwn(result, field)) invalid(`${label}.${field} is required.`)
  return result
}
function text(value: unknown, label: string, maxLength = 1024): string {
  if (typeof value !== 'string' || !value || value.trim() !== value || value.length > maxLength || /[\u0000-\u001f\u007f]/.test(value)) {
    invalid(`${label} must be a nonempty bounded identifier without control characters.`)
  }
  return value
}
function integer(value: unknown, label: string, minimum = 0): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < minimum) invalid(`${label} must be a safe integer at least ${minimum}.`)
  return value
}
function branch(value: unknown): string {
  if (typeof value !== 'string' || !UUID.test(value)) invalid('Recovery branchId must be a UUID.')
  return value.toLowerCase()
}
function bytes(value: unknown, label: string): Uint8Array {
  if (!(value instanceof Uint8Array)) invalid(`${label} must be a Uint8Array.`)
  try { const copy = new Uint8Array(value.byteLength); copy.set(value); return copy }
  catch (cause) { throw new RecoveryStoreError('invalid', `${label} could not be copied.`, { cause }) }
}

export function normalizeRecoveryScope(value: unknown): RecoveryScope {
  const raw = record(value, ['serverId', 'tenantId', 'accountId', 'documentId'], 'Recovery scope')
  return Object.freeze({ serverId: text(raw.serverId, 'serverId'), tenantId: text(raw.tenantId, 'tenantId'),
    accountId: text(raw.accountId, 'accountId'), documentId: text(raw.documentId, 'documentId') })
}

/** Capture owned data synchronously. This does not validate or authorize Yjs content. */
export function cloneRecoveryCheckpoint(value: unknown): RecoveryCheckpoint {
  const raw = record(value, ['formatVersion', 'scope', 'branchId', 'revision', 'capturedAt', 'schema', 'fragment',
    'observedSequence', 'documentRevision', 'connectionGeneration', 'epoch', 'replayPolicy', 'yjsState', 'pendingUpdates'], 'Recovery checkpoint')
  if (raw.formatVersion !== 1 || raw.epoch !== null || raw.replayPolicy !== 'manual-only') invalid('Unsupported recovery format, epoch or replay policy.')
  const scope = normalizeRecoveryScope(raw.scope), branchId = branch(raw.branchId)
  const revision = integer(raw.revision, 'revision', 1), capturedAt = integer(raw.capturedAt, 'capturedAt')
  let schema: RecoveryCheckpoint['schema']
  try { schema = raw.schema === null ? null : normalizeDocumentSchema(clonePlainData(raw.schema)) }
  catch (cause) { throw new RecoveryStoreError('invalid', 'Invalid recovery document schema.', { cause }) }
  if (!Array.isArray(raw.pendingUpdates)) invalid('pendingUpdates must be an array.')
  const pendingUpdates: RecoveryPendingUpdate[] = [], ids = new Set<string>()
  const keys = Reflect.ownKeys(raw.pendingUpdates)
  if (keys.length !== raw.pendingUpdates.length + 1) invalid('pendingUpdates must contain only dense array items.')
  for (let index = 0; index < raw.pendingUpdates.length; index++) {
    const descriptor = Object.getOwnPropertyDescriptor(raw.pendingUpdates, String(index))
    if (!descriptor?.enumerable || !('value' in descriptor)) invalid('pendingUpdates must contain data items only.')
    const item = record(descriptor.value, ['id', 'update', 'updateCount', 'attempted'], 'Pending update')
    const id = text(item.id, 'Pending update id', 200)
    if (ids.has(id)) invalid('Pending update ids must be unique.')
    ids.add(id)
    if (typeof item.attempted !== 'boolean') invalid('Pending attempted must be a boolean.')
    pendingUpdates.push(Object.freeze({ id, update: bytes(item.update, 'Pending update bytes'),
      updateCount: integer(item.updateCount, 'updateCount', 1), attempted: item.attempted }))
  }
  integer(pendingUpdates.reduce((sum, item) => sum + item.updateCount, 0), 'Pending update count')
  return Object.freeze({ formatVersion: 1, scope, branchId, revision, capturedAt, schema,
    fragment: text(raw.fragment, 'fragment', 256), observedSequence: integer(raw.observedSequence, 'observedSequence'),
    documentRevision: integer(raw.documentRevision, 'documentRevision'), connectionGeneration: integer(raw.connectionGeneration, 'connectionGeneration'),
    epoch: null, replayPolicy: 'manual-only', yjsState: bytes(raw.yjsState, 'Yjs state'), pendingUpdates: Object.freeze(pendingUpdates) })
}

export function summarizeRecoveryCheckpoint(checkpoint: RecoveryCheckpoint): RecoverySummary {
  const { yjsState, pendingUpdates, ...metadata } = checkpoint
  const binaryBytes = yjsState.byteLength + pendingUpdates.reduce((sum, item) => sum + item.update.byteLength, 0)
  const metadataBytes = new TextEncoder().encode(JSON.stringify({ ...metadata,
    pendingUpdates: pendingUpdates.map(({ update: _update, ...item }) => item) })).byteLength
  return { scope: normalizeRecoveryScope(checkpoint.scope), branchId: checkpoint.branchId,
    revision: checkpoint.revision, capturedAt: checkpoint.capturedAt,
    pendingCount: pendingUpdates.reduce((sum, item) => sum + item.updateCount, 0), byteLength: binaryBytes + metadataBytes }
}

const scopeKey = (scope: RecoveryScope) => JSON.stringify([scope.serverId, scope.tenantId, scope.accountId, scope.documentId])
const recordKey = (scope: RecoveryScope, branchId: string) => JSON.stringify([scopeKey(scope), branchId])
interface Tombstone { scope: RecoveryScope; branchId: string; revision: number }
interface StoredRow { key: string; scopeKey: string; checkpoint: RecoveryCheckpoint | null; tombstone: Tombstone | null }
function checkpointRow(checkpoint: RecoveryCheckpoint): StoredRow {
  return { key: recordKey(checkpoint.scope, checkpoint.branchId), scopeKey: scopeKey(checkpoint.scope), checkpoint, tombstone: null }
}
function tombstoneRow(scope: RecoveryScope, branchId: string, revision: number): StoredRow {
  return { key: recordKey(scope, branchId), scopeKey: scopeKey(scope), checkpoint: null, tombstone: { scope, branchId, revision } }
}
function readRow(value: unknown): StoredRow {
  try {
    const raw = record(value, ['key', 'scopeKey', 'checkpoint', 'tombstone'], 'Stored recovery record')
    let row: StoredRow
    if (raw.checkpoint !== null && raw.tombstone === null) row = checkpointRow(cloneRecoveryCheckpoint(raw.checkpoint))
    else if (raw.checkpoint === null && raw.tombstone !== null) {
      const deleted = record(raw.tombstone, ['scope', 'branchId', 'revision'], 'Recovery tombstone')
      row = tombstoneRow(normalizeRecoveryScope(deleted.scope), branch(deleted.branchId), integer(deleted.revision, 'Tombstone revision', 1))
    } else invalid('Stored recovery record must be a checkpoint or tombstone.')
    if (raw.key !== row.key || raw.scopeKey !== row.scopeKey) invalid('Stored recovery record identity does not match its content.')
    return row
  } catch (cause) { throw new RecoveryStoreError('corrupt', 'Stored recovery data is invalid; it was not overwritten.', { cause }) }
}
function rowBytes(row: StoredRow): number {
  return row.checkpoint ? summarizeRecoveryCheckpoint(row.checkpoint).byteLength
    : new TextEncoder().encode(JSON.stringify(row.tombstone)).byteLength
}
function storageError(error: unknown): RecoveryStoreError {
  if (error instanceof RecoveryStoreError) return error
  return new RecoveryStoreError(typeof DOMException !== 'undefined' && error instanceof DOMException && error.name === 'QuotaExceededError' ? 'quota' : 'storage',
    'Recovery storage transaction failed; no success was acknowledged.', { cause: error })
}

/** Both requests run in the same transaction; no payload scan is needed for accounting. */
function loadAccounting(store: IDBObjectStore, usage: IDBObjectStore, key: string,
  done: (existing: StoredRow | null, totalBytes: number) => void, fail: (error: unknown) => void): void {
  const existing = store.get(key), total = usage.get(USAGE_KEY)
  let completed = 0
  const loaded = () => {
    if (++completed !== 2) return
    try {
      if (typeof total.result !== 'number' || !Number.isSafeInteger(total.result) || total.result < 0) {
        throw new RecoveryStoreError('corrupt', 'Recovery storage accounting is missing or invalid; existing copies were retained.')
      }
      const row = existing.result === undefined ? null : readRow(existing.result)
      if (row && (row.key !== key || rowBytes(row) > total.result)) {
        throw new RecoveryStoreError('corrupt', 'Recovery storage accounting does not cover the requested branch.')
      }
      done(row, total.result)
    } catch (error) { fail(error) }
  }
  existing.onsuccess = loaded
  total.onsuccess = loaded
}

/** Native IndexedDB only. No provider attachment, eviction, replay or automatic restore. */
export function createIndexedDbRecoveryStore(options: IndexedDbRecoveryStoreOptions = {}): RecoveryStore {
  const databaseName = text(options.databaseName ?? DEFAULT_DATABASE, 'databaseName', 256)
  const maxCheckpointBytes = integer(options.maxCheckpointBytes ?? 16 * 1024 * 1024, 'maxCheckpointBytes', 1)
  const maxTotalBytes = integer(options.maxTotalBytes ?? 64 * 1024 * 1024, 'maxTotalBytes', 1)
  let closed = false, database: IDBDatabase | undefined, opening: Promise<IDBDatabase> | undefined
  let rejectOpening: ((error: RecoveryStoreError) => void) | undefined
  const transactions = new Set<IDBTransaction>()

  function close() {
    if (closed) return
    closed = true
    rejectOpening?.(new RecoveryStoreError('closed', 'Recovery store closed before opening completed.'))
    for (const transaction of transactions) { try { transaction.abort() } catch { /* already complete */ } }
    database?.close()
  }
  function open(): Promise<IDBDatabase> {
    if (closed) return Promise.reject(new RecoveryStoreError('closed', 'Recovery store is closed.'))
    if (opening) return opening
    opening = new Promise<IDBDatabase>((resolve, reject) => {
      let settled = false, request: IDBOpenDBRequest
      const fail = (error: RecoveryStoreError) => { if (!settled) { settled = true; reject(error) } }
      rejectOpening = fail
      try {
        if (!globalThis.indexedDB) { fail(new RecoveryStoreError('unavailable', 'IndexedDB recovery storage is unavailable.')); return }
        request = globalThis.indexedDB.open(databaseName, 1)
      } catch (cause) { fail(new RecoveryStoreError('unavailable', 'IndexedDB recovery storage could not be opened.', { cause })); return }
      request.onblocked = () => fail(new RecoveryStoreError('blocked', 'Another connection is blocking recovery storage opening.'))
      request.onerror = () => fail(closed ? new RecoveryStoreError('closed', 'Recovery store is closed.') : storageError(request.error))
      request.onupgradeneeded = () => {
        if (settled || closed) { request.transaction?.abort(); return }
        try {
          const store = request.result.createObjectStore(STORE, { keyPath: 'key' })
          store.createIndex(SCOPE_INDEX, 'scopeKey', { unique: false })
          request.result.createObjectStore(USAGE_STORE).put(0, USAGE_KEY)
        } catch (cause) { fail(storageError(cause)); request.transaction?.abort() }
      }
      request.onsuccess = () => {
        const opened = request.result
        if (settled || closed) { opened.close(); fail(new RecoveryStoreError('closed', 'Recovery store is closed.')); return }
        if (!opened.objectStoreNames.contains(STORE) || !opened.objectStoreNames.contains(USAGE_STORE)) {
          opened.close(); fail(new RecoveryStoreError('corrupt', 'Recovery database is missing its checkpoint or accounting store.')); return
        }
        database = opened
        database.onversionchange = close
        settled = true; rejectOpening = undefined
        // A failed move keeps the legacy database for the next open; the store works meanwhile.
        resolve(options.databaseName === undefined ? moveLegacyDatabase(globalThis.indexedDB, LEGACY_DATABASE, opened).then(() => opened, () => opened) : opened)
      }
    })
    return opening
  }

  async function transact<T>(mode: IDBTransactionMode,
    work: (store: IDBObjectStore, result: (value: T) => void, fail: (error: unknown) => void, usage: IDBObjectStore) => void): Promise<T> {
    const db = await open()
    if (closed) throw new RecoveryStoreError('closed', 'Recovery store is closed.')
    return new Promise<T>((resolve, reject) => {
      let transaction: IDBTransaction, value: T, failure: unknown
      try { transaction = db.transaction([STORE, USAGE_STORE], mode, mode === 'readwrite' ? { durability: 'strict' } : undefined) }
      catch (cause) { reject(storageError(cause)); return }
      transactions.add(transaction)
      transaction.oncomplete = () => { transactions.delete(transaction); resolve(value) }
      transaction.onabort = () => {
        transactions.delete(transaction)
        reject(failure ? storageError(failure) : closed ? new RecoveryStoreError('closed', 'Recovery store closed before the transaction committed.') : storageError(transaction.error))
      }
      const fail = (error: unknown) => {
        failure = error
        try { transaction.abort() } catch { /* completion/abort handler owns settlement */ }
      }
      try { work(transaction.objectStore(STORE), next => { value = next }, fail, transaction.objectStore(USAGE_STORE)) }
      catch (error) { fail(error) }
    })
  }

  return {
    async write(value) {
      const checkpoint = cloneRecoveryCheckpoint(value), summary = summarizeRecoveryCheckpoint(checkpoint)
      if (summary.byteLength > maxCheckpointBytes) throw new RecoveryStoreError('quota', 'Recovery checkpoint exceeds its byte budget.')
      const incoming = checkpointRow(checkpoint)
      return transact<void>('readwrite', (store, result, fail, usage) => {
        loadAccounting(store, usage, incoming.key, (existing, usedBytes) => {
          if (existing?.tombstone) throw new RecoveryStoreError('discarded', 'This recovery branch was discarded and cannot be written again.')
          if (existing?.checkpoint && checkpoint.revision <= existing.checkpoint.revision) throw new RecoveryStoreError('conflict', 'A newer or identical recovery revision already exists.')
          const total = usedBytes - (existing ? rowBytes(existing) : 0) + summary.byteLength
          if (!Number.isSafeInteger(total) || total > maxTotalBytes) throw new RecoveryStoreError('quota', 'Recovery storage byte budget would be exceeded; existing copies were retained.')
          store.put(incoming); usage.put(total, USAGE_KEY); result(undefined)
        }, fail)
      })
    },
    async read(value, id) {
      const scope = normalizeRecoveryScope(value), branchId = branch(id), key = recordKey(scope, branchId)
      return transact<RecoveryCheckpoint | null>('readonly', (store, result, fail) => {
        const request = store.get(key)
        request.onsuccess = () => {
          try {
            const row = request.result === undefined ? null : readRow(request.result)
            if (row && row.key !== key) throw new RecoveryStoreError('corrupt', 'Recovery lookup returned a different scope or branch.')
            result(row?.checkpoint ?? null)
          } catch (error) { fail(error) }
        }
      })
    },
    async list(value) {
      const scope = normalizeRecoveryScope(value), key = scopeKey(scope)
      return transact<RecoverySummary[]>('readonly', (store, result, fail) => {
        const request = store.index(SCOPE_INDEX).getAll(key)
        request.onsuccess = () => {
          try {
            const rows = (request.result as unknown[]).map(readRow)
            if (rows.some(row => row.scopeKey !== key)) throw new RecoveryStoreError('corrupt', 'Recovery list contains another scope.')
            result(rows.flatMap(row => row.checkpoint ? [summarizeRecoveryCheckpoint(row.checkpoint)] : [])
              .sort((a, b) => b.capturedAt - a.capturedAt || a.branchId.localeCompare(b.branchId)))
          } catch (error) { fail(error) }
        }
      })
    },
    async discard(value, id, expected) {
      const scope = normalizeRecoveryScope(value), branchId = branch(id), revision = integer(expected, 'expectedRevision', 1)
      const key = recordKey(scope, branchId)
      return transact<void>('readwrite', (store, result, fail, usage) => {
        loadAccounting(store, usage, key, (row, usedBytes) => {
          if (!row || row.key !== key || (row.checkpoint?.revision ?? row.tombstone?.revision) !== revision) {
            throw new RecoveryStoreError('conflict', 'Recovery revision changed or is missing; discard was not applied.')
          }
          if (!row.tombstone) {
            const tombstone = tombstoneRow(scope, branchId, revision)
            const total = usedBytes - rowBytes(row) + rowBytes(tombstone)
            if (!Number.isSafeInteger(total) || total > maxTotalBytes) throw new RecoveryStoreError('quota', 'Recovery tombstone would exceed the storage byte budget.')
            store.put(tombstone); usage.put(total, USAGE_KEY)
          }
          result(undefined)
        }, fail)
      })
    },
    close,
  }
}
