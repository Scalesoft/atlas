import { describe, expect, it } from 'vitest'
import { moveLegacyDatabase } from './moveLegacyDatabase'

// The package has no IndexedDB implementation for tests: this stand-in covers exactly the calls the move makes
// (open without a version, cursors, put, deleteDatabase), each request answering asynchronously like the browser.
type Store = { keyPath: string | null; rows: Map<IDBValidKey, unknown> }
function fakeIndexedDb(databases: Map<string, Map<string, Store>>) {
  const later = (callback: () => void) => queueMicrotask(callback)
  const database = (stores: Map<string, Store>) => ({
    objectStoreNames: Object.assign([...stores.keys()], { contains: (name: string) => stores.has(name) }),
    close() {},
    transaction(names: string[]) {
      const tx = { error: null, oncomplete: null as null | (() => void), onerror: null, onabort: null, objectStore(name: string) {
        const store = stores.get(name)!
        return {
          keyPath: store.keyPath,
          put(value: Record<string, unknown>, key?: IDBValidKey) { store.rows.set(key ?? value[store.keyPath!] as IDBValidKey, structuredClone(value)) },
          openCursor() {
            const entries = [...store.rows], request = { result: null as unknown, onsuccess: null as null | (() => void) }
            const step = (index: number) => later(() => {
              const entry = entries[index]
              request.result = entry ? { primaryKey: entry[0], value: structuredClone(entry[1]), continue: () => step(index + 1) } : null
              request.onsuccess?.()
            })
            step(0)
            return request
          },
        }
      } }
      void names
      setTimeout(() => tx.oncomplete?.())
      return tx
    },
  })
  return {
    open(name: string) {
      const request = { result: null as unknown, error: null as null | { name: string }, transaction: null as null | { abort(): void },
        onupgradeneeded: null as null | (() => void), onsuccess: null as null | (() => void), onerror: null as null | ((event: { preventDefault(): void }) => void) }
      later(() => {
        const stores = databases.get(name)
        if (stores) { request.result = database(stores); request.onsuccess?.(); return }
        let aborted = false
        request.transaction = { abort: () => { aborted = true } }
        request.onupgradeneeded?.()
        if (!aborted) throw new Error('The move must not create the legacy database.')
        request.error = { name: 'AbortError' }; request.onerror?.({ preventDefault() {} })
      })
      return request
    },
    deleteDatabase(name: string) { databases.delete(name) },
    database,
  }
}

describe('moveLegacyDatabase', () => {
  it('moves the records of the legacy database once and deletes it; without one it creates nothing', async () => {
    const draft = { key: '["scope","branch"]', scope: 'scope', record: { snapshot: 'unsaved' } }
    const target = new Map<string, Store>([['drafts', { keyPath: 'key', rows: new Map() }], ['usage', { keyPath: null, rows: new Map([['bytes', 0]]) }]])
    const databases = new Map([
      ['scalesoft-markdown-drafts', new Map<string, Store>([['drafts', { keyPath: 'key', rows: new Map([[draft.key, draft]]) }], ['usage', { keyPath: null, rows: new Map([['bytes', 120]]) }]])],
      ['atlas-markdown-drafts', target],
    ])
    const factory = fakeIndexedDb(databases)
    const opened = factory.database(target) as unknown as IDBDatabase

    await moveLegacyDatabase(factory as unknown as IDBFactory, 'scalesoft-markdown-drafts', opened)
    expect(target.get('drafts')!.rows.get(draft.key)).toEqual(draft)
    expect(target.get('usage')!.rows.get('bytes')).toBe(120)
    expect(databases.has('scalesoft-markdown-drafts')).toBe(false)

    await moveLegacyDatabase(factory as unknown as IDBFactory, 'scalesoft-markdown-drafts', opened)
    expect([...databases.keys()]).toEqual(['atlas-markdown-drafts'])
    expect(target.get('drafts')!.rows.size).toBe(1)
  })
})
