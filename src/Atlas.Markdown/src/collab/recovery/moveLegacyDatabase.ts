/**
 * Moves every record of the browser database `legacyName` (the default name of a store before 5.0) into the open
 * `target` once, then deletes the legacy database, so unsaved drafts and journals survive the rename. No-op when the
 * legacy database does not exist. Copies only the object stores `target` also has. Rejects without deleting anything
 * when reading or writing fails; the next open tries again.
 * shortcut: rows are put over the target's, so a usage counter is overwritten when both databases hold data (a downgrade
 * and upgrade again); recount usage when that case matters.
 */
export async function moveLegacyDatabase(factory: IDBFactory, legacyName: string, target: IDBDatabase): Promise<void> {
  const legacy = await new Promise<IDBDatabase | null>((resolve, reject) => {
    const request = factory.open(legacyName)
    // Opening a missing database would create it: abort the creation instead.
    request.onupgradeneeded = () => request.transaction?.abort()
    request.onsuccess = () => resolve(request.result)
    request.onerror = event => {
      if (request.error?.name !== 'AbortError') { reject(request.error); return }
      event.preventDefault(); resolve(null)
    }
  })
  if (!legacy) return
  try {
    const names = Array.from(legacy.objectStoreNames).filter(name => target.objectStoreNames.contains(name))
    if (names.length) {
      const rows = await new Promise<Array<readonly [string, IDBValidKey, unknown]>>((resolve, reject) => {
        const tx = legacy.transaction(names, 'readonly'), result: Array<readonly [string, IDBValidKey, unknown]> = []
        for (const name of names) {
          const request = tx.objectStore(name).openCursor()
          request.onsuccess = () => {
            const cursor = request.result
            if (cursor) { result.push([name, cursor.primaryKey, cursor.value]); cursor.continue() }
          }
        }
        tx.oncomplete = () => resolve(result)
        tx.onerror = tx.onabort = () => reject(tx.error)
      })
      await new Promise<void>((resolve, reject) => {
        const tx = target.transaction(names, 'readwrite')
        for (const [name, key, value] of rows) {
          const store = tx.objectStore(name)
          if (store.keyPath === null) store.put(value, key)
          else store.put(value)
        }
        tx.oncomplete = () => resolve()
        tx.onerror = tx.onabort = () => reject(tx.error)
      })
    }
  } finally { legacy.close() }
  // Not awaited: a tab of an older version still holding the database closes on `versionchange`, then the delete completes.
  factory.deleteDatabase(legacyName)
}
