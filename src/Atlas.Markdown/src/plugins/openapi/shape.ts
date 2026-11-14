import { openapiStrings } from './strings'

export type OpenApiMode = 'download' | 'reference' | 'interactive'
export interface OpenApiData { version: 1; filename: string; source: string; uploadedBy?: string; mode?: OpenApiMode }
export interface OpenApiSummary { title: string; version: string; operations: number; schemas: number; author?: string }
export const MAX_OPENAPI_SOURCE_BYTES = 512 * 1024
export const defaultOpenApi: OpenApiData = {
  version: 1,
  filename: 'openapi.yaml',
  source: "openapi: 3.1.0\ninfo:\n  title: New API\n  version: '1.0'\npaths: {}\n",
}

const record = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value)

/**
 * The node's schema check (J8.5): the shape of the data and the size of the source, with no YAML parser. Whether the source is a valid
 * OpenAPI document is judged where it is drawn (the card shows the error), where it is entered (the source dialog, an uploaded file) and
 * on the server (the `openapi` validator), so a page whose spec does not parse still opens.
 */
export function normalizeOpenApiShape(value: unknown): OpenApiData {
  if (!record(value) || value.version !== 1 || typeof value.filename !== 'string' || !value.filename.trim() || value.filename.length > 255 || /[\\/\x00-\x1f]/.test(value.filename) || typeof value.source !== 'string') throw new TypeError(openapiStrings.invalidDataError)
  if (value.mode !== undefined && value.mode !== 'download' && value.mode !== 'reference' && value.mode !== 'interactive') throw new TypeError(openapiStrings.invalidModeError)
  if (value.uploadedBy !== undefined && (typeof value.uploadedBy !== 'string' || !value.uploadedBy.trim() || value.uploadedBy.length > 255)) throw new TypeError(openapiStrings.invalidUploaderError)
  if (new TextEncoder().encode(value.source).byteLength > MAX_OPENAPI_SOURCE_BYTES) throw new TypeError(openapiStrings.sourceTooLargeError)
  return { version: 1, filename: value.filename, source: value.source,
    ...(value.uploadedBy !== undefined ? { uploadedBy: value.uploadedBy } : {}),
    ...(value.mode !== undefined ? { mode: value.mode } : {}),
  }
}

/** The API's title and version read from the source text, without parsing it: what a stub, a copy and a projection say before the card is drawn. */
export function lightOpenApiSummary(data: OpenApiData): { title: string; version: string } {
  const json = /\.json$/i.test(data.filename)
  const info = json ? data.source : /^info:[ \t]*\r?\n((?:[ \t]+.*(?:\r?\n|$))+)/m.exec(data.source)?.[1] ?? ''
  const pick = (key: string) => (json ? new RegExp(`"${key}"\\s*:\\s*"([^"]*)"`) : new RegExp(`^[ \\t]+${key}:[ \\t]*['"]?([^'"\\r\\n]*?)['"]?[ \\t]*$`, 'm')).exec(info)?.[1]?.trim()
  return { title: pick('title') || data.filename, version: pick('version') || '' }
}
