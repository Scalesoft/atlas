import type { MarkdownDocumentSchema, MarkdownProjection, MarkdownValue } from '../../core/types'
import type { MarkdownPlugin } from '../../core/plugins/types'
import { clonePlainData, deepFreezePlainData } from '../../core/plainData'
import { confirmDocumentSchema, normalizeDocumentSchema } from '../documentSchema'
import { cloneRecoveryCheckpoint, normalizeRecoveryScope, summarizeRecoveryCheckpoint } from './indexedDbRecoveryStore'
import { decodeRecoveryDocument } from './decodeDocument'
import type { RecoveryCheckpoint, RecoveryScope, RecoveryStore, RecoverySummary } from './types'

export type RecoveryPreviewErrorCode = 'missing' | 'stale' | 'scope' | 'schema' | 'content' | 'limit' | 'aborted' | 'storage'
export class RecoveryPreviewError extends Error {
  constructor(readonly code: RecoveryPreviewErrorCode, message: string, options?: ErrorOptions) {
    super(message, options)
    this.name = 'RecoveryPreviewError'
  }
}

export interface MarkdownRecoveryPreviewOptions {
  readonly store: RecoveryStore
  /** The host must verify this account before exposing any local recovery content. */
  readonly scope: RecoveryScope
  readonly branchId: string
  /** Exact revision selected for review. A newer record requires a new selection. */
  readonly revision: number
  readonly schema: MarkdownDocumentSchema
  readonly fragment: string
  readonly plugins?: readonly MarkdownPlugin[]
  readonly signal?: AbortSignal
  readonly maxCheckpointBytes?: number
  readonly maxNodes?: number
  readonly maxDepth?: number
}

/** Isolated local export data; it is not an authoritative server version or replay permission. */
export interface MarkdownRecoveryPreview {
  readonly source: RecoverySummary
  readonly schema: MarkdownDocumentSchema
  readonly fragment: string
  readonly value: MarkdownValue
  readonly projection: MarkdownProjection
}

function fail(code: RecoveryPreviewErrorCode, message: string, cause?: unknown): never {
  throw new RecoveryPreviewError(code, message, cause === undefined ? undefined : { cause })
}
function checkAbort(signal?: AbortSignal): void {
  if (signal?.aborted) fail('aborted', 'Recovery preview was cancelled.', signal.reason)
}
function limit(value: number | undefined, fallback: number, maximum: number): number {
  const result = value ?? fallback
  if (!Number.isSafeInteger(result) || result < 1 || result > maximum) fail('limit', 'Invalid recovery preview limit.')
  return result
}
function matchingScope(left: RecoveryScope, right: RecoveryScope): boolean {
  return left.serverId === right.serverId && left.tenantId === right.tenantId
    && left.accountId === right.accountId && left.documentId === right.documentId
}

/** Bound metadata and byte copies before the normal checkpoint validator allocates owned data. */
export function preflight(value: unknown, maxBytes: number, maxNodes: number, maxDepth: number): void {
  const stack: Array<{ value: unknown; depth: number; exit?: boolean }> = [{ value, depth: 0 }]
  const active = new WeakSet<object>()
  let nodes = 0, bytes = 0
  while (stack.length) {
    const item = stack.pop()!
    if (item.exit) { active.delete(item.value as object); continue }
    if (++nodes > maxNodes || item.depth > maxDepth) fail('limit', 'Recovery metadata exceeds the preview traversal limit.')
    if (item.value instanceof Uint8Array) {
      bytes += item.value.byteLength
      if (bytes > maxBytes) fail('limit', 'Recovery bytes exceed the preview limit.')
      continue
    }
    if (!item.value || typeof item.value !== 'object') continue
    if (active.has(item.value)) fail('content', 'Recovery metadata must be acyclic.')
    active.add(item.value)
    stack.push({ ...item, exit: true })
    const keys = Reflect.ownKeys(item.value)
    if (nodes + stack.length + keys.length > maxNodes * 2) fail('limit', 'Recovery metadata exceeds the preview traversal limit.')
    for (const key of keys) {
      if (key === 'length' && Array.isArray(item.value)) continue
      const descriptor = Object.getOwnPropertyDescriptor(item.value, key)
      if (!descriptor || !('value' in descriptor)) fail('content', 'Recovery metadata must contain data fields only.')
      stack.push({ value: descriptor.value, depth: item.depth + 1 })
    }
  }
}

function readSelected(store: RecoveryStore, scope: RecoveryScope, branchId: string,
  capture: (value: unknown) => RecoveryCheckpoint | null, signal?: AbortSignal): Promise<RecoveryCheckpoint | null> {
  return new Promise((resolve, reject) => {
    let settled = false
    const aborted = () => {
      if (settled) return
      settled = true
      cleanup()
      reject(new RecoveryPreviewError('aborted', 'Recovery preview was cancelled.', { cause: signal?.reason }))
    }
    const cleanup = () => signal?.removeEventListener('abort', aborted)
    signal?.addEventListener('abort', aborted, { once: true })
    if (signal?.aborted) { aborted(); return }
    try {
      Promise.resolve(store.read(scope, branchId)).then(value => {
        if (settled) return
        settled = true
        cleanup()
        // Own the returned data in the first fulfillment callback, before another promise
        // continuation can mutate the host store's buffers. Cancelled late reads are ignored.
        try { resolve(capture(value)) } catch (cause) { reject(cause) }
      }, cause => {
        if (settled) return
        settled = true
        cleanup()
        reject(cause instanceof RecoveryPreviewError ? cause : new RecoveryPreviewError('storage', 'The selected recovery copy could not be read.', { cause }))
      })
    } catch (cause) {
      if (settled) return
      settled = true
      cleanup()
      reject(cause instanceof RecoveryPreviewError ? cause : new RecoveryPreviewError('storage', 'The selected recovery copy could not be read.', { cause }))
    }
  })
}

/**
 * Read exactly one selected branch and prepare a browser-runtime export without a live binding.
 * Never writes/discards the store, merges pending payloads, or starts a collaboration provider.
 * Cancellation prevents publication; synchronous decoding is bounded, not interruptible.
 */
export async function prepareMarkdownRecovery(options: MarkdownRecoveryPreviewOptions): Promise<MarkdownRecoveryPreview> {
  const signal = options.signal
  checkAbort(signal)
  let scope: RecoveryScope, schema: MarkdownDocumentSchema
  try { scope = normalizeRecoveryScope(options.scope) }
  catch (cause) { return fail('scope', 'Recovery preview requires a valid host scope.', cause) }
  try { schema = normalizeDocumentSchema(clonePlainData(options.schema)) }
  catch (cause) { return fail('schema', 'Recovery preview requires an explicit document schema.', cause) }
  const branchId = typeof options.branchId === 'string' ? options.branchId.toLowerCase() : '', revision = options.revision, fragment = options.fragment
  if (typeof branchId !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(branchId)
    || !Number.isSafeInteger(revision) || revision < 1) fail('stale', 'Recovery preview requires an exact branch and revision.')
  if (typeof fragment !== 'string' || !fragment || fragment.trim() !== fragment || fragment.length > 256
    || /[\u0000-\u001f\u007f]/.test(fragment)) fail('schema', 'Recovery preview requires an exact fragment.')
  const maxCheckpointBytes = limit(options.maxCheckpointBytes, 16 * 1024 * 1024, 64 * 1024 * 1024)
  const maxNodes = limit(options.maxNodes, 100_000, 1_000_000), maxDepth = limit(options.maxDepth, 100, 256)
  if (options.plugins !== undefined && !Array.isArray(options.plugins)) fail('schema', 'Recovery plugins must be a host supplied array.')
  const plugins = [...options.plugins ?? []], store = options.store
  const checkpoint = await readSelected(store, scope, branchId, raw => {
    if (raw === null) return null
    try {
      preflight(raw, maxCheckpointBytes, maxNodes, maxDepth)
      return cloneRecoveryCheckpoint(raw)
    } catch (cause) {
      if (cause instanceof RecoveryPreviewError) throw cause
      return fail('content', 'The selected recovery copy is invalid.', cause)
    }
  }, signal)
  checkAbort(signal)
  if (checkpoint === null) fail('missing', 'The selected recovery copy no longer exists in this scope.')
  if (!matchingScope(scope, checkpoint.scope) || branchId !== checkpoint.branchId) fail('scope', 'The returned recovery copy does not belong to the selected branch and scope.')
  if (revision !== checkpoint.revision) fail('stale', 'The recovery revision changed; select the current copy again.')
  if (fragment !== checkpoint.fragment || checkpoint.schema === null) fail('schema', 'The selected recovery copy has an incompatible fragment or unversioned schema.')
  try { confirmDocumentSchema(schema, checkpoint.schema) }
  catch (cause) { return fail('schema', 'The selected recovery schema does not match the host.', cause) }
  const source = summarizeRecoveryCheckpoint(checkpoint)
  if (source.byteLength > maxCheckpointBytes) fail('limit', 'The recovery checkpoint exceeds the preview byte limit.')
  let document
  try { document = decodeRecoveryDocument(checkpoint, { maxNodes, maxDepth }) }
  catch (cause) { return fail(cause instanceof RangeError ? 'limit' : 'content', 'The recovery document cannot be decoded completely and safely.', cause) }
  checkAbort(signal)
  const { projectRecoveryDocument } = await import('./previewProjection')
  checkAbort(signal)
  const result = projectRecoveryDocument(document, schema, plugins, () => checkAbort(signal))
  checkAbort(signal)
  return deepFreezePlainData({ source, schema, fragment, ...result })
}
