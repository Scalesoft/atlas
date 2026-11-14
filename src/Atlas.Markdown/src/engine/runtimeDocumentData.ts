import type { Node } from '@tiptap/pm/model'

/** Validate live PM JSON fields without allocating a full JSON document.
 * Only recursively frozen plain values are cached. Mutable attrs, including nested
 * plugin data, are checked on every visit; live editor attributes are never frozen.
 */
export function createRuntimeDocumentDataValidator(): (document: Node) => void {
  const immutable = new WeakSet<object>()
  const visiting = new WeakSet<object>()
  function plain(value: unknown): boolean {
    if (value === null || typeof value === 'string' || typeof value === 'boolean') return true
    if (typeof value === 'number') {
      if (!Number.isFinite(value)) throw new TypeError('Editor values must contain finite JSON numbers.')
      return true
    }
    if (!value || typeof value !== 'object') throw new TypeError('Editor values must contain JSON data only.')
    if (immutable.has(value)) return true
    if (visiting.has(value)) throw new TypeError('Editor values must be acyclic JSON data.')
    const array = Array.isArray(value)
    if (!array) {
      const prototype = Object.getPrototypeOf(value)
      if (prototype !== null && Object.getPrototypeOf(prototype) !== null) throw new TypeError('Editor values must contain plain JSON objects.')
    }
    visiting.add(value)
    try {
      let frozen = Object.isFrozen(value), count = 0
      for (const key of Reflect.ownKeys(value)) {
        if (array && key === 'length') continue
        if (typeof key !== 'string' || (array && (!/^(0|[1-9]\d*)$/.test(key) || Number(key) >= value.length))) {
          throw new TypeError('Editor values must contain JSON fields only.')
        }
        const descriptor = Object.getOwnPropertyDescriptor(value, key)
        if (!descriptor?.enumerable || !('value' in descriptor)) throw new TypeError('Editor values must contain JSON data fields only.')
        if (!plain(descriptor.value)) frozen = false
        count++
      }
      if (array && count !== value.length) throw new TypeError('Editor values must not contain sparse JSON arrays.')
      if (frozen) immutable.add(value)
      return frozen
    } finally { visiting.delete(value) }
  }
  return document => {
    const validate = (node: Node) => {
      plain(node.attrs)
      for (const mark of node.marks) plain(mark.attrs)
      if (node.isText && typeof node.text !== 'string') throw new TypeError('Editor text must be a string.')
    }
    validate(document)
    document.descendants(node => { validate(node) })
  }
}
