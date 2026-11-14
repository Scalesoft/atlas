import { clonePlainData, deepFreezePlainData } from '../../core/plainData'
import type { JSONContent } from '@tiptap/core'

/** Invalid server metadata must fail closed rather than enter a transport retry loop. */
export class ProtocolError extends Error {
  constructor(message: string, options?: ErrorOptions) {
    super(`[PROTOCOL_ERROR] ${message}`, options)
    this.name = 'ProtocolError'
  }
}

/** Return a safe integer server sequence; update acknowledgements start at one. */
export function validateServerSequence(value: unknown, allowZero = false): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < (allowZero ? 0 : 1)) {
    throw new ProtocolError(`Server sequence must be a safe integer ${allowZero ? 'at least zero' : 'greater than zero'}.`)
  }
  return value
}

/** SignalR JSON accepts either .NET property casing on join responses. */
export interface JoinResultRaw {
  snapshot?: string | null; Snapshot?: string | null
  updates?: string[]; Updates?: string[]
  seq?: number; Seq?: number
  seedMarkdown?: string | null; SeedMarkdown?: string | null
  seedEditorJson?: JSONContent | null; SeedEditorJson?: JSONContent | null
  isSnapshotter?: boolean; IsSnapshotter?: boolean
  canWrite?: boolean; CanWrite?: boolean
  schema?: unknown; Schema?: unknown
  snapshotEncoding?: string | null; SnapshotEncoding?: string | null
  seed?: string | null; Seed?: string | null
  projectionsDerived?: boolean; ProjectionsDerived?: boolean
}

export interface JoinResult {
  snapshot: string | null
  updates: readonly string[]
  seq: number
  seedMarkdown: string | null
  seedEditorJson: JSONContent | null
  isSnapshotter: boolean
  canWrite: boolean
  schema: unknown
  /** 'gzip' when the server compressed `snapshot` (the connection asked for it, see transport.ts); null when raw. */
  snapshotEncoding: 'gzip' | null
  /** The seed gzip-compressed (base64 of `{"markdown","editorJson"}`, see `normalizeSeed`) instead of `seedMarkdown`/`seedEditorJson`. */
  seed: string | null
  /** The server derives the snapshot's projections: push the state alone (`PushSnapshotGzip`). */
  projectionsDerived: boolean
}

const missing = Symbol('missing join field')

function aliases(result: Record<string, unknown>, camel: string, pascal: string): unknown {
  const hasCamel = Object.hasOwn(result, camel), hasPascal = Object.hasOwn(result, pascal)
  if (hasCamel && hasPascal && !equalJson(result[camel], result[pascal])) {
    throw new ProtocolError(`Join response contains conflicting ${camel}/${pascal} aliases.`)
  }
  return hasCamel ? result[camel] : hasPascal ? result[pascal] : missing
}

// Inputs have already been copied as acyclic JSON data, so comparison cannot invoke accessors.
function equalJson(left: unknown, right: unknown): boolean {
  if (Object.is(left, right)) return true
  if (!left || !right || typeof left !== 'object' || typeof right !== 'object') return false
  if (Array.isArray(left) !== Array.isArray(right)) return false
  const a = left as Record<string, unknown>, b = right as Record<string, unknown>
  const keys = Object.keys(a)
  return keys.length === Object.keys(b).length && keys.every(key => Object.hasOwn(b, key) && equalJson(a[key], b[key]))
}

function nullableText(value: unknown, field: string): string | null {
  if (value === missing || value === null) return null
  if (typeof value !== 'string') throw new ProtocolError(`Join ${field} must be a string or null.`)
  return value
}

function snapshotEncoding(value: unknown): 'gzip' | null {
  if (value === missing || value === null) return null
  if (value !== 'gzip') throw new ProtocolError('Join snapshotEncoding must be gzip or null.')
  return value
}

function optionalBoolean(value: unknown, fallback: boolean, field: string): boolean {
  if (value === missing) return fallback
  if (typeof value !== 'boolean') throw new ProtocolError(`Join ${field} must be a boolean.`)
  return value
}

export function normalizeJoin(result: unknown): JoinResult {
  if (!result || typeof result !== 'object' || Array.isArray(result)) throw new ProtocolError('Join response must be an object.')
  let raw: Record<string, unknown>
  try { raw = clonePlainData(result) as Record<string, unknown> }
  catch (cause) { throw new ProtocolError('Join response must contain JSON data only.', { cause }) }
  // A missing head is not an empty document: never turn a malformed reply into sequence zero.
  const seq = validateServerSequence(aliases(raw, 'seq', 'Seq'), true)
  const updates = aliases(raw, 'updates', 'Updates')
  if (updates !== missing && (!Array.isArray(updates) || !updates.every(item => typeof item === 'string'))) {
    throw new ProtocolError('Join updates must be an array of strings.')
  }
  const schema = aliases(raw, 'schema', 'Schema')
  if (schema !== missing && schema !== null && (typeof schema !== 'object' || Array.isArray(schema))) {
    throw new ProtocolError('Join schema must be an object or null.')
  }
  const isSnapshotter = optionalBoolean(aliases(raw, 'isSnapshotter', 'IsSnapshotter'), false, 'isSnapshotter')
  return Object.freeze({
    snapshot: nullableText(aliases(raw, 'snapshot', 'Snapshot'), 'snapshot'),
    updates: Object.freeze(updates === missing ? [] : (updates as string[]).slice()),
    seq,
    seedMarkdown: nullableText(aliases(raw, 'seedMarkdown', 'SeedMarkdown'), 'seedMarkdown'),
    seedEditorJson: richSeed(aliases(raw, 'seedEditorJson', 'SeedEditorJson')),
    isSnapshotter,
    // An older server only proves write permission by electing this peer as snapshotter.
    // All other missing permission values remain fail-closed.
    canWrite: optionalBoolean(aliases(raw, 'canWrite', 'CanWrite'), isSnapshotter, 'canWrite'),
    schema: schema === missing ? null : deepFreezePlainData(schema),
    snapshotEncoding: snapshotEncoding(aliases(raw, 'snapshotEncoding', 'SnapshotEncoding')),
    seed: nullableText(aliases(raw, 'seed', 'Seed'), 'seed'),
    projectionsDerived: optionalBoolean(aliases(raw, 'projectionsDerived', 'ProjectionsDerived'), false, 'projectionsDerived'),
  })
}

function richSeed(value: unknown): JSONContent | null {
  if (value === missing || value === null) return null
  if (typeof value !== 'object' || Array.isArray(value) || (value as JSONContent).type !== 'doc') {
    throw new ProtocolError('Join seedEditorJson must be a document object or null.')
  }
  // The seed's node attributes become Yjs attributes, and Yjs stores only ordinary objects: the
  // null-prototype objects of clonePlainData make seeding throw "Unexpected content type" for any
  // block with an object attribute (diagram, math, OpenAPI). The validated JSON round-trips losslessly.
  return deepFreezePlainData(JSON.parse(JSON.stringify(value))) as JSONContent
}

/** The inflated compressed seed (`JoinResult.seed`), validated like the plain one. */
export function normalizeSeed(json: string): { seedMarkdown: string | null; seedEditorJson: JSONContent | null } {
  let raw: unknown
  try { raw = JSON.parse(json) } catch (cause) { throw new ProtocolError('Join seed must be JSON.', { cause }) }
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) throw new ProtocolError('Join seed must be an object.')
  const seed = raw as Record<string, unknown>
  return { seedMarkdown: nullableText(seed.markdown ?? null, 'seed markdown'), seedEditorJson: richSeed(seed.editorJson ?? null) }
}

// The JSON hub protocol represents .NET byte[] as base64, not JSON Uint8Array objects.
export function u8ToB64(value: Uint8Array): string {
  let text = ''
  const chunk = 0x8000
  for (let index = 0; index < value.length; index += chunk) {
    text += String.fromCharCode(...value.subarray(index, index + chunk))
  }
  return btoa(text)
}

export function b64ToU8(value: string): Uint8Array {
  const text = atob(value)
  const bytes = new Uint8Array(text.length)
  for (let index = 0; index < text.length; index++) bytes[index] = text.charCodeAt(index)
  return bytes
}

// Yjs encodes a no-op update in two bytes ([0,0]); longer updates carry real structs.
export const EMPTY_UPDATE_LEN = 2

/** True when a hub error signals the document exceeded its server-side size cap. */
export function isDocumentFull(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error ?? '')
  return /size limit exceeded/i.test(message)
}

export function shouldRetryWithNewUpdateId(error: unknown): boolean {
  return /\[RETRY_WITH_NEW_ID\]/i.test(error instanceof Error ? error.message : String(error ?? ''))
}

export function isUpdateIdCollision(error: unknown): boolean {
  return /\[UPDATE_ID_COLLISION\]/i.test(error instanceof Error ? error.message : String(error ?? ''))
}

/** The relay content guard refused the payload; resending the same local change can never succeed. */
export function isInvalidContentError(error: unknown): boolean {
  return /\[INVALID_CONTENT\]/i.test(error instanceof Error ? error.message : String(error ?? ''))
}

/** The host replaced the document (a new generation): this session can never store an update again and must join the current one. */
export function isSupersededError(error: unknown): boolean {
  return /\[SUPERSEDED\]/i.test(error instanceof Error ? error.message : String(error ?? ''))
}

export function isReadOnlyError(error: unknown): boolean {
  return /\[READ_ONLY\]/i.test(error instanceof Error ? error.message : String(error ?? ''))
}

export function newUpdateId(): string {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID()
  const bytes = new Uint8Array(16)
  if (globalThis.crypto?.getRandomValues) globalThis.crypto.getRandomValues(bytes)
  else for (let index = 0; index < bytes.length; index++) bytes[index] = Math.floor(Math.random() * 256)
  bytes[6] = (bytes[6] & 0x0f) | 0x40
  bytes[8] = (bytes[8] & 0x3f) | 0x80
  const hex = Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

export async function sha256Hex(value: Uint8Array): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', value.slice().buffer)
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
}
