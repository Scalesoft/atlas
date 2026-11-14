import { cloneMarkdownDraftRecord, draftBranch, draftInteger, draftKey, draftScopeKey } from './data'
import { MarkdownDraftError, type MarkdownDraftRecord, type MarkdownDraftScope, type MarkdownDraftStore } from './types'
import { moveLegacyDatabase } from '../collab/recovery/moveLegacyDatabase'

/** Atomic periodic snapshots, independent of both collaboration journal databases. */
export function createIndexedDbMarkdownDraftStore(options: { databaseName?: string; maxTotalBytes?: number } = {}): MarkdownDraftStore {
  const limit = options.maxTotalBytes ?? 64 * 1024 * 1024
  if (!Number.isSafeInteger(limit) || limit < 1024) throw new MarkdownDraftError('invalid', 'Invalid draft byte budget.')
  const name = options.databaseName ?? 'atlas-markdown-drafts'
  if (typeof name !== 'string' || !name.trim()) throw new MarkdownDraftError('invalid', 'Invalid draft database name.')
  let closed = false, database: IDBDatabase | undefined, opening: Promise<IDBDatabase> | undefined
  let rejectOpening: ((error: unknown) => void) | undefined
  const transactions = new Set<IDBTransaction>()
  const close = () => {
    closed = true; rejectOpening?.(new MarkdownDraftError('closed', 'Draft store is closed.')); rejectOpening = undefined
    for (const tx of transactions) try { tx.abort() } catch { /* Already complete. */ }
    database?.close()
  }
  const open = () => {
    if (closed) return Promise.reject(new MarkdownDraftError('closed', 'Draft store is closed.'))
    return opening ??= new Promise<IDBDatabase>((resolve, reject) => {
      rejectOpening = reject
      if (typeof indexedDB === 'undefined') { reject(new MarkdownDraftError('unavailable', 'IndexedDB is unavailable.')); return }
      let abandoned = false
      let request: IDBOpenDBRequest
      try { request = indexedDB.open(name, 1) } catch (cause) { reject(new MarkdownDraftError('storage', 'Draft database open failed.', { cause })); return }
      request.onupgradeneeded = () => {
        const records = request.result.createObjectStore('drafts', { keyPath: 'key' }); records.createIndex('scope', 'scope')
        request.result.createObjectStore('usage').put(0, 'bytes')
      }
      request.onerror = () => reject(new MarkdownDraftError('storage', 'Draft database open failed.', { cause: request.error }))
      request.onblocked = () => { abandoned = true; reject(new MarkdownDraftError('blocked', 'Draft database open is blocked.')) }
      request.onsuccess = () => {
        if (closed || abandoned) { request.result.close(); return }
        const opened = database = request.result; rejectOpening = undefined; database.onversionchange = close
        // Unsaved drafts of the legacy database survive; a failed move keeps it for the next open.
        resolve(options.databaseName === undefined ? moveLegacyDatabase(indexedDB, 'scalesoft-markdown-drafts', opened).then(() => opened, () => opened) : opened)
      }
    })
  }
  const transact = (db: IDBDatabase, mode: IDBTransactionMode) => {
    if (closed) throw new MarkdownDraftError('closed', 'Draft store is closed.')
    try {
      const tx = db.transaction(mode === 'readwrite' ? ['drafts', 'usage'] : ['drafts'], mode, mode === 'readwrite' ? { durability: 'strict' } : undefined)
      transactions.add(tx)
      tx.addEventListener('complete', () => transactions.delete(tx), { once: true })
      tx.addEventListener('abort', () => transactions.delete(tx), { once: true })
      return tx
    } catch (cause) { throw new MarkdownDraftError('storage', 'Draft transaction could not start.', { cause }) }
  }
  const read = async (scope: MarkdownDraftScope, branch?: string): Promise<MarkdownDraftRecord[]> => {
    const selection = draftScopeKey(scope), selectionKey = branch === undefined ? undefined : draftKey(scope, branch)
    const db = await open()
    return new Promise((resolve, reject) => {
      const tx = transact(db, 'readonly'), store = tx.objectStore('drafts')
      const request = selectionKey === undefined ? store.index('scope').getAll(selection) : store.get(selectionKey)
      let result: MarkdownDraftRecord[] = [], failure: unknown
      request.onsuccess = () => {
        try {
          const rows = selectionKey === undefined ? request.result : request.result ? [request.result] : []
          result = rows.filter((row: any) => row.tombstone !== true).map((row: any) => {
            const record = cloneMarkdownDraftRecord(row.record)
            if (row.key !== draftKey(record.scope, record.branchId) || row.scope !== selection || draftScopeKey(record.scope) !== selection) throw new Error('Draft scope mismatch.')
            return record
          })
        } catch (cause) { failure = new MarkdownDraftError('corrupt', 'Invalid stored draft.', { cause }); tx.abort() }
      }
      tx.oncomplete = () => resolve(result)
      tx.onabort = tx.onerror = () => reject(failure ?? new MarkdownDraftError('storage', 'Draft read failed.', { cause: tx.error }))
    })
  }
  const mutate = async (selection: string, key: string, expected: number, record: MarkdownDraftRecord | null) => {
    const row = record ? { key, scope: selection, record } : { key, scope: selection, tombstone: true }
    const size = new TextEncoder().encode(JSON.stringify(row)).byteLength
    if (size > limit) throw new MarkdownDraftError('quota', 'Draft byte budget exceeded.')
    const db = await open()
    return new Promise<void>((resolve, reject) => {
      const tx = transact(db, 'readwrite'), store = tx.objectStore('drafts'), usage = tx.objectStore('usage')
      let previous: any, total: unknown, ready = 0, failure: unknown
      const commit = () => {
        if (++ready !== 2) return
        try {
          if (previous?.tombstone) throw new MarkdownDraftError('discarded', 'This draft branch was discarded.')
          if ((previous?.record?.revision ?? 0) !== expected || (!record && !previous)) throw new MarkdownDraftError('conflict', 'The selected draft changed.')
          if (previous) {
            const old = cloneMarkdownDraftRecord(previous.record)
            if (previous.key !== key || previous.scope !== selection || draftKey(old.scope, old.branchId) !== key) throw new MarkdownDraftError('corrupt', 'Draft identity mismatch.')
          }
          if (typeof total !== 'number' || !Number.isSafeInteger(total) || total < 0 || (previous && (!Number.isSafeInteger(previous.size) || previous.size < 0 || previous.size > total))) throw new MarkdownDraftError('corrupt', 'Draft usage metadata is invalid.')
          const next = total - (previous?.size ?? 0) + size
          if (!Number.isSafeInteger(next) || next > limit) throw new MarkdownDraftError('quota', 'Draft byte budget exceeded.')
          store.put({ ...row, size }); usage.put(next, 'bytes')
        } catch (error) { failure = error; tx.abort() }
      }
      const old = store.get(key), used = usage.get('bytes')
      old.onsuccess = () => { previous = old.result; commit() }; used.onsuccess = () => { total = used.result; commit() }
      tx.oncomplete = () => resolve()
      tx.onabort = tx.onerror = () => reject(failure ?? new MarkdownDraftError(tx.error?.name === 'QuotaExceededError' ? 'quota' : 'storage', 'Draft commit failed.', { cause: tx.error }))
    })
  }
  return {
    read: async (scope, branch) => (await read(scope, branch))[0] ?? null,
    list: scope => read(scope),
    async write(input, expectedRevision) {
      const record = cloneMarkdownDraftRecord(input), expected = draftInteger(expectedRevision)
      if (record.revision !== expected + 1) throw new MarkdownDraftError('invalid', 'Draft revisions must advance exactly once.')
      return mutate(draftScopeKey(record.scope), draftKey(record.scope, record.branchId), expected, record)
    },
    async discard(scope, branch, expectedRevision) {
      return mutate(draftScopeKey(scope), draftKey(scope, draftBranch(branch)), draftInteger(expectedRevision, 1), null)
    },
    close,
  }
}
