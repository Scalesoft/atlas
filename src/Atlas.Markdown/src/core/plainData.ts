/** Clone JSON-shaped input, including framework proxies that structuredClone rejects. */
export function clonePlainData<T>(value: T, visiting = new WeakSet<object>()): T {
  if (value === null || typeof value === 'string' || typeof value === 'boolean') return value
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) throw new TypeError('Editor values must contain finite JSON numbers.')
    return value
  }
  if (typeof value !== 'object') throw new TypeError('Editor values must contain JSON data only.')
  if (visiting.has(value)) throw new TypeError('Editor values must be acyclic JSON data.')
  if (!Array.isArray(value)) {
    const prototype = Object.getPrototypeOf(value)
    if (prototype !== null && Object.getPrototypeOf(prototype) !== null) throw new TypeError('Editor values must contain plain JSON objects.')
  }
  visiting.add(value)
  try {
    if (Array.isArray(value)) {
      const keys = Reflect.ownKeys(value)
      const itemKeys = keys.filter(key => key !== 'length')
      for (const key of itemKeys) {
        if (typeof key !== 'string' || !/^(0|[1-9]\d*)$/.test(key) || Number(key) >= value.length) {
          throw new TypeError('Editor values must contain JSON array items only.')
        }
      }
      if (itemKeys.length !== value.length) throw new TypeError('Editor values must not contain sparse JSON arrays.')
      const result = new Array(value.length)
      for (const key of itemKeys as string[]) {
        const descriptor = Object.getOwnPropertyDescriptor(value, key)
        if (!descriptor?.enumerable || !('value' in descriptor)) {
          throw new TypeError('Editor values must contain JSON array data fields only.')
        }
        result[Number(key)] = clonePlainData(descriptor.value, visiting)
      }
      return result as T
    }
    const result: Record<string, unknown> = Object.create(null) as Record<string, unknown>
    for (const key of Reflect.ownKeys(value)) {
      if (typeof key !== 'string') throw new TypeError('Editor values must not contain symbol keys.')
      const descriptor = Object.getOwnPropertyDescriptor(value, key)
      if (!descriptor?.enumerable) throw new TypeError('Editor values must contain enumerable JSON fields only.')
      if (!('value' in descriptor)) throw new TypeError('Editor values must contain JSON data fields only.')
      result[key] = clonePlainData(descriptor.value, visiting)
    }
    return result as T
  } finally {
    visiting.delete(value)
  }
}

/** Deep-freeze already validated plain data without invoking getters. */
export function deepFreezePlainData<T>(value: T, visited = new WeakSet<object>()): T {
  if (value === null || typeof value !== 'object' || visited.has(value)) return value
  visited.add(value)
  for (const key of Reflect.ownKeys(value)) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key)
    if (descriptor && 'value' in descriptor) deepFreezePlainData(descriptor.value, visited)
  }
  return Object.freeze(value)
}
