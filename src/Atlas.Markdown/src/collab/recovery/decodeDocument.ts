import type { JSONContent } from '@tiptap/core'
import { yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { createUpdateCoverage } from '../provider/updateCoverage'
import type { RecoveryCheckpoint } from './types'

export interface RecoveryDecodeLimits { readonly maxNodes: number; readonly maxDepth: number }
class RecoveryDecodeLimitError extends RangeError {}

/**
 * Decode an already owned checkpoint in isolation. No schema repair, pending replay or live apply.
 * Coverage checks clock/deletion inclusion only, not payload equality or snapshot authority.
 * Limits count the document root, XML nodes/text runs and JSON attribute values; root depth is zero.
 */
export function decodeRecoveryDocument(checkpoint: Pick<RecoveryCheckpoint, 'yjsState' | 'pendingUpdates' | 'fragment'>, limits: RecoveryDecodeLimits): JSONContent {
  if (!Number.isSafeInteger(limits.maxNodes) || limits.maxNodes < 1
    || !Number.isSafeInteger(limits.maxDepth) || limits.maxDepth < 1) {
    throw new RangeError('Recovery decode limits must be positive safe integers.')
  }
  const doc = new Y.Doc({ gc: false })
  try {
    const coverage = createUpdateCoverage(checkpoint.yjsState)
    for (const pending of checkpoint.pendingUpdates) {
      if (coverage.hasMissing(pending.update)) throw new TypeError('Recovery state does not contain all pending update clocks and deletions.')
    }
    Y.applyUpdate(doc, checkpoint.yjsState)
    if (doc.store.pendingStructs !== null || doc.store.pendingDs !== null) {
      throw new TypeError('Recovery state has unresolved Yjs dependencies.')
    }
    if (!doc.share.has(checkpoint.fragment)) throw new TypeError('Recovery state is missing its document fragment.')
    // Top-level Yjs types arrive as AbstractType: the binary format does not retain their
    // constructor. Interpret only the named, existing root, then validate its actual content.
    const fragment = doc.getXmlFragment(checkpoint.fragment)
    const otherRoots = new Set([...doc.share].filter(([name]) => name !== checkpoint.fragment).map(([, root]) => root))
    for (const structs of doc.store.clients.values()) {
      for (const item of structs) {
        if (!(item instanceof Y.Item) || item.deleted) continue
        if (item.parent instanceof Y.AbstractType && otherRoots.has(item.parent)) {
          throw new TypeError('Recovery state contains another nonempty root outside the document fragment.')
        }
        if (item.parent === fragment && item.parentSub !== null) {
          throw new TypeError('Recovery document fragment contains map attributes.')
        }
      }
    }
    preflight(fragment, limits)
    // The newer PM-node converter repairs invalid nodes by deleting them from Yjs. This raw
    // serializer preserves unknown nodes/marks/attributes for the caller's strict schema check.
    return yXmlFragmentToProsemirrorJSON(fragment)
  } catch (cause) {
    if (cause instanceof TypeError || cause instanceof RecoveryDecodeLimitError) throw cause
    throw new TypeError('Recovery state is not a supported complete Yjs document.', { cause })
  } finally { doc.destroy() }
}

function preflight(fragment: Y.XmlFragment, limits: RecoveryDecodeLimits): void {
  let count = 1
  const visit = (depth: number) => {
    if (depth > limits.maxDepth || ++count > limits.maxNodes) throw new RecoveryDecodeLimitError('Recovery document exceeds its node or depth budget.')
  }
  const json = (value: unknown, depth: number) => {
    const stack: Array<{ value: unknown; depth: number }> = [{ value, depth }]
    const seen = new WeakSet<object>()
    while (stack.length) {
      const next = stack.pop()!
      visit(next.depth)
      const entry = next.value
      if (entry === null || typeof entry === 'string' || typeof entry === 'boolean') continue
      if (typeof entry === 'number' && Number.isFinite(entry)) continue
      if (!entry || typeof entry !== 'object') throw new TypeError('Recovery attributes must contain finite JSON data.')
      if (seen.has(entry)) throw new TypeError('Recovery attributes must contain acyclic JSON data.')
      seen.add(entry)
      if (!Array.isArray(entry)) {
        const prototype = Object.getPrototypeOf(entry)
        if (prototype !== null && Object.getPrototypeOf(prototype) !== null) throw new TypeError('Recovery attributes must contain plain JSON data.')
      }
      for (const key of Reflect.ownKeys(entry)) {
        if (Array.isArray(entry) && key === 'length') continue
        const descriptor = Object.getOwnPropertyDescriptor(entry, key)
        if (typeof key !== 'string' || !descriptor?.enumerable || !('value' in descriptor)) throw new TypeError('Recovery attributes must contain JSON data properties.')
        stack.push({ value: descriptor.value, depth: next.depth + 1 })
      }
    }
  }
  const stack: Array<{ value: unknown; depth: number }> = fragment.toArray().map(value => ({ value, depth: 1 }))
  while (stack.length) {
    const { value, depth } = stack.pop()!
    visit(depth)
    if (value instanceof Y.XmlElement) {
      const attributes = value.getAttributes()
      if (Object.keys(attributes).length) json(attributes, depth + 1)
      for (const child of value.toArray()) stack.push({ value: child, depth: depth + 1 })
    } else if (value instanceof Y.XmlText && depth > 1) {
      if (Object.keys(value.getAttributes()).length) throw new TypeError('Recovery text contains unsupported block attributes.')
      const delta: Array<{ insert?: unknown; attributes?: unknown }> = value.toDelta()
      for (let index = 0; index < delta.length; index++) {
        if (index > 0) visit(depth)
        const item = delta[index]
        if (typeof item.insert !== 'string') throw new TypeError('Recovery document contains an unsupported text embed.')
        if (item.attributes !== undefined) json(item.attributes, depth + 1)
      }
    } else throw new TypeError('Recovery document contains an unsupported XML type or root content.')
  }
}
