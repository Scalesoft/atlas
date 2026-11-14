import type { MarkdownDocumentSchema } from '../../core/types'
import { clonePlainData } from '../../core/plainData'
import { normalizeDocumentSchema } from '../documentSchema'
import { normalizeRecoveryScope } from './indexedDbRecoveryStore'
import { moveLegacyDatabase } from './moveLegacyDatabase'
import { RecoveryStoreError, type RecoveryScope } from './types'

export interface JournalUpdate { readonly updateId: string; readonly sha256: string; readonly update: Uint8Array }
export interface JournalRecord {
  readonly version: 2; readonly scope: RecoveryScope; readonly branchId: string
  readonly epoch: string; readonly schema: MarkdownDocumentSchema; readonly fragment: string
  readonly revision: number; readonly owner: string; readonly sequence: number
  readonly snapshot: Uint8Array; readonly pending: readonly JournalUpdate[]
}
export interface DurableJournalStore {
  read(scope: RecoveryScope, branchId: string): Promise<JournalRecord | null>
  list(scope: RecoveryScope): Promise<JournalRecord[]>
  /** Revision zero creates only. Every subsequent write compares both revision and owner. */
  write(record: JournalRecord, expectedRevision: number, expectedOwner: string): Promise<void>
  /** Explicit removal leaves an identity tombstone, preventing an old tab from resurrecting it. */
  discard(scope: RecoveryScope, branchId: string, expectedRevision: number, expectedOwner: string): Promise<void>
  close(): void
}
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const fail = (message: string): never => { throw new RecoveryStoreError('invalid', message) }
function id(value: unknown): string { return typeof value === 'string' && uuid.test(value) ? value.toLowerCase() : fail('Journal identity must be a UUID.') }
function integer(value: unknown): number { return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : fail('Invalid journal counter.') }
function bytes(value: unknown): Uint8Array {
  if (!(value instanceof Uint8Array) || value.byteLength > 16 * 1024 * 1024) fail('Invalid journal bytes.')
  return Uint8Array.from(value as Uint8Array)
}
function data(value: unknown, fields: readonly string[]): Record<string, any> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) fail('Journal must be a plain data object.')
  const prototype = Object.getPrototypeOf(value)
  if (prototype !== null && Object.getPrototypeOf(prototype) !== null) fail('Journal must be a plain data object.')
  const result: Record<string, unknown> = Object.create(null)
  for (const name of Reflect.ownKeys(value as object)) {
    const descriptor = Object.getOwnPropertyDescriptor(value, name)
    if (typeof name !== 'string' || !fields.includes(name) || !descriptor?.enumerable || !('value' in descriptor)) fail('Journal must contain only supported data fields.')
    result[name as string] = descriptor!.value
  }
  if (fields.some(name => !Object.hasOwn(result, name))) fail('Journal field is missing.')
  return result
}
/** Owned immutable metadata and owned binary buffers, captured before asynchronous storage. */
export function cloneJournal(input: JournalRecord): JournalRecord {
  const value = data(input, ['version', 'scope', 'branchId', 'epoch', 'schema', 'fragment', 'revision', 'owner', 'sequence', 'snapshot', 'pending'])
  if (value.version !== 2) fail('Unsupported journal version.')
  if (!Array.isArray(value.pending) || value.pending.length > 4096) fail('Invalid journal pending queue.')
  if (Reflect.ownKeys(value.pending).length !== value.pending.length + 1) fail('Journal queue must be dense data.')
  let totalBytes = value.snapshot instanceof Uint8Array ? value.snapshot.byteLength : 0
  const copyBytes = (value: unknown) => {
    if (value instanceof Uint8Array) totalBytes += value.byteLength
    if (totalBytes > 64 * 1024 * 1024) fail('Journal binary data exceeds its aggregate budget.')
    return bytes(value)
  }
  const seen = new Set<string>()
  const pending = Array.from({ length: value.pending.length }, (_, index) => {
    const descriptor = Object.getOwnPropertyDescriptor(value.pending, String(index))
    if (!descriptor?.enumerable || !('value' in descriptor)) fail('Journal queue must contain data entries.')
    const entry = data(descriptor!.value, ['updateId', 'sha256', 'update'])
    const updateId = id(entry.updateId)
    if (seen.has(updateId) || typeof entry.sha256 !== 'string' || !/^[0-9a-f]{64}$/.test(entry.sha256)) fail('Invalid journal update identity or hash.')
    seen.add(updateId)
    return Object.freeze({ updateId, sha256: entry.sha256, update: copyBytes(entry.update) })
  })
  if (typeof value.fragment !== 'string' || !value.fragment.trim() || value.fragment.length > 256) fail('Invalid journal fragment.')
  const revision = integer(value.revision)
  if (!revision) fail('Journal revision must be positive.')
  return Object.freeze({ version: 2, scope: normalizeRecoveryScope(value.scope), branchId: id(value.branchId),
    epoch: id(value.epoch), owner: id(value.owner), schema: normalizeDocumentSchema(clonePlainData(value.schema)),
    fragment: value.fragment, revision, sequence: integer(value.sequence), snapshot: bytes(value.snapshot), pending: Object.freeze(pending) })
}
const scopeKey = (scope: RecoveryScope) => JSON.stringify(normalizeRecoveryScope(scope))
const key = (scope: RecoveryScope, branch: string) => JSON.stringify([scopeKey(scope), id(branch)])
export async function journalSha256(update: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', Uint8Array.from(update))
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
}

/** Independent v2 database; opening this store never upgrades or clears manual checkpoints. */
export function createIndexedDbDurableJournal(options: { databaseName?: string; maxTotalBytes?: number } = {}): DurableJournalStore {
  const limit = options.maxTotalBytes ?? 64 * 1024 * 1024
  if (!Number.isSafeInteger(limit) || limit < 1024) throw new RangeError('Invalid journal storage budget.')
  let closed = false, database: IDBDatabase | undefined, opening: Promise<IDBDatabase> | undefined
  let rejectOpen: ((error: unknown) => void) | undefined
  const transactions = new Set<IDBTransaction>()
  const close = () => {
    closed = true
    rejectOpen?.(new RecoveryStoreError('closed', 'Journal store is closed.')); rejectOpen = undefined
    for (const tx of transactions) try { tx.abort() } catch { /* Already committed. */ }
    database?.close()
  }
  const transaction = (db: IDBDatabase, names: string | string[], mode: IDBTransactionMode): IDBTransaction => {
    if (closed) throw new RecoveryStoreError('closed', 'Journal store is closed.')
    const tx = db.transaction(names, mode, mode === 'readwrite' ? { durability: 'strict' } : undefined)
    transactions.add(tx)
    const remove = () => transactions.delete(tx)
    tx.addEventListener('complete', remove, { once: true }); tx.addEventListener('abort', remove, { once: true })
    return tx
  }
  const open = (): Promise<IDBDatabase> => {
    if (closed) return Promise.reject(new RecoveryStoreError('closed', 'Journal store is closed.'))
    return opening ??= new Promise((resolve, reject) => {
      rejectOpen = reject
      if (typeof indexedDB === 'undefined') { reject(new RecoveryStoreError('unavailable', 'IndexedDB is unavailable.')); return }
      let abandoned = false
      const request = indexedDB.open(options.databaseName ?? 'atlas-markdown-journal-v2', 1)
      request.onupgradeneeded = () => { const store = request.result.createObjectStore('branches', { keyPath: 'key' }); store.createIndex('scope', 'scope'); request.result.createObjectStore('usage').put(0, 'bytes') }
      request.onerror = () => reject(new RecoveryStoreError('storage', 'Journal open failed.', { cause: request.error }))
      request.onblocked = () => { abandoned = true; reject(new RecoveryStoreError('blocked', 'Journal open is blocked by another tab.')) }
      request.onsuccess = () => {
        if (closed || abandoned) { request.result.close(); reject(new RecoveryStoreError('closed', 'Journal store closed during open.')); return }
        const opened = database = request.result; rejectOpen = undefined
        database.onversionchange = close
        // A failed move keeps the legacy database for the next open; the store works meanwhile.
        resolve(options.databaseName === undefined ? moveLegacyDatabase(indexedDB, 'scalesoft-markdown-journal-v2', opened).then(() => opened, () => opened) : opened)
      }
    })
  }
  const read = async (scope: RecoveryScope, branch?: string): Promise<JournalRecord[]> => {
    const selection = scopeKey(scope), selectionKey = branch === undefined ? undefined : key(scope, branch)
    const db = await open()
    return new Promise((resolve, reject) => {
      const tx = transaction(db, 'branches', 'readonly'), store = tx.objectStore('branches')
      const request = selectionKey === undefined ? store.index('scope').getAll(selection) : store.get(selectionKey)
      let result: JournalRecord[] = [], error: unknown
      request.onsuccess = () => {
        try {
          const rows = selectionKey === undefined ? request.result : request.result ? [request.result] : []
          result = rows.filter((row: { tombstone?: boolean }) => row.tombstone !== true).map((row: { key: string; scope: string; record: JournalRecord }) => {
            const record = cloneJournal(row.record)
            if (row.scope !== selection || row.key !== key(record.scope, record.branchId) || scopeKey(record.scope) !== selection) throw new Error('Journal scope mismatch.')
            return record
          })
        } catch (cause) { error = new RecoveryStoreError('corrupt', 'Invalid stored journal.', { cause }); tx.abort() }
      }
      tx.oncomplete = () => resolve(result)
      tx.onabort = tx.onerror = () => reject(error ?? new RecoveryStoreError(closed ? 'closed' : 'storage', 'Journal read failed.', { cause: tx.error }))
    })
  }
  return {
    read: async (scope, branch) => (await read(scope, branch))[0] ?? null,
    list: scope => read(scope),
    async write(input, expectedRevision, expectedOwner) {
      const record = cloneJournal(input), revision = integer(expectedRevision), owner = id(expectedOwner)
      if (record.revision !== revision + 1) fail('Journal revisions must advance exactly once.')
      const recordKey = key(record.scope, record.branchId), selection = scopeKey(record.scope)
      const metadata = { ...record, snapshot: null, pending: record.pending.map(({ update: _bytes, ...entry }) => entry) }
      const size = record.snapshot.byteLength + record.pending.reduce((sum, item) => sum + item.update.byteLength, 0) + new TextEncoder().encode(JSON.stringify(metadata)).byteLength
      if (size > limit) throw new RecoveryStoreError('quota', 'Journal storage budget exceeded.')
      const db = await open()
      return new Promise<void>((resolve, reject) => {
        const tx = transaction(db, ['branches', 'usage'], 'readwrite')
        const branches = tx.objectStore('branches'), usage = tx.objectStore('usage')
        let failure: unknown, previous: any, total: number | undefined, ready = 0
        const commit = () => {
          if (++ready !== 2) return
          try {
            if (previous?.tombstone) throw new RecoveryStoreError('discarded', 'This journal branch was explicitly discarded.')
            if ((previous?.record.revision ?? 0) !== revision || (previous && previous.record.owner !== owner)) throw new RecoveryStoreError('conflict', 'Another session owns or changed this journal branch.')
            if (previous) {
              const existing = cloneJournal(previous.record)
              if (previous.key !== recordKey || previous.scope !== selection || key(existing.scope, existing.branchId) !== recordKey) throw new RecoveryStoreError('corrupt', 'Journal identity mismatch.')
              if (existing.epoch !== record.epoch || existing.fragment !== record.fragment || JSON.stringify(existing.schema) !== JSON.stringify(record.schema) || existing.sequence > record.sequence) throw new RecoveryStoreError('conflict', 'Journal binding and server sequence cannot be replaced or moved backwards.')
            }
            if (!Number.isSafeInteger(total) || total! < 0 || (previous && (!Number.isSafeInteger(previous.size) || previous.size < 0))) throw new RecoveryStoreError('corrupt', 'Journal usage is invalid.')
            const nextTotal = total! - (previous?.size ?? 0) + size
            if (!Number.isSafeInteger(nextTotal) || nextTotal < 0) throw new RecoveryStoreError('corrupt', 'Journal usage is inconsistent.')
            if (nextTotal > limit) throw new RecoveryStoreError('quota', 'Journal storage budget exceeded.')
            branches.put({ key: recordKey, scope: selection, record, size }); usage.put(nextTotal, 'bytes')
          } catch (error) { failure = error; tx.abort() }
        }
        const old = branches.get(recordKey), used = usage.get('bytes')
        old.onsuccess = () => { previous = old.result; commit() }
        used.onsuccess = () => { total = used.result; commit() }
        tx.oncomplete = () => resolve()
        tx.onabort = tx.onerror = () => reject(failure ?? new RecoveryStoreError(closed ? 'closed' : tx.error?.name === 'QuotaExceededError' ? 'quota' : 'storage', 'Journal transaction failed.', { cause: tx.error }))
      })
    },
    async discard(scope, branch, expectedRevision, expectedOwner) {
      const recordKey = key(scope, branch), selection = scopeKey(scope), revision = integer(expectedRevision), owner = id(expectedOwner)
      const db = await open()
      return new Promise<void>((resolve, reject) => {
        const tx = transaction(db, ['branches', 'usage'], 'readwrite'), branches = tx.objectStore('branches'), usage = tx.objectStore('usage')
        let failure: unknown, previous: any, total: any, ready = 0
        const finish = () => {
          if (++ready !== 2) return
          try {
            if (!previous || previous.tombstone || previous.record.revision !== revision || previous.record.owner !== owner) throw new RecoveryStoreError('conflict', 'The selected journal changed before discard.')
            const record = cloneJournal(previous.record)
            if (key(record.scope, record.branchId) !== recordKey || previous.scope !== selection) throw new RecoveryStoreError('corrupt', 'Journal identity mismatch.')
            const tombstone = { key: recordKey, scope: selection, record: null, tombstone: true }
            const size = new TextEncoder().encode(JSON.stringify(tombstone)).byteLength
            if (!Number.isSafeInteger(total) || !Number.isSafeInteger(previous.size) || previous.size < 0 || total < previous.size) throw new RecoveryStoreError('corrupt', 'Journal usage is inconsistent.')
            const nextTotal = total - previous.size + size
            if (nextTotal > limit) throw new RecoveryStoreError('quota', 'Journal storage budget exceeded.')
            branches.put({ ...tombstone, size }); usage.put(nextTotal, 'bytes')
          } catch (error) { failure = error; tx.abort() }
        }
        const old = branches.get(recordKey), used = usage.get('bytes')
        old.onsuccess = () => { previous = old.result; finish() }; used.onsuccess = () => { total = used.result; finish() }
        tx.oncomplete = () => resolve()
        tx.onabort = tx.onerror = () => reject(failure ?? new RecoveryStoreError(closed ? 'closed' : 'storage', 'Journal discard failed.', { cause: tx.error }))
      })
    },
    close,
  }
}
