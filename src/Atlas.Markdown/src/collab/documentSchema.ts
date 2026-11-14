import type { MarkdownDocumentSchema } from '../core/types'

export class DocumentSchemaError extends Error {
  constructor(message: string) { super(`[SCHEMA_MISMATCH] ${message}`); this.name = 'DocumentSchemaError' }
}

/** Also accepts SignalR's PascalCase JSON representation. Never infers a missing profile. */
export function normalizeDocumentSchema(value: unknown): MarkdownDocumentSchema {
  if (!value || typeof value !== 'object') throw new DocumentSchemaError('The server did not confirm a document schema.')
  const raw = value as Record<string, unknown>
  const version = raw.version ?? raw.Version
  const profile = raw.profile ?? raw.Profile
  const entries = raw.plugins ?? raw.Plugins
  // Revisions 1–7 are recognized and preserved as declared; recognizing one does not make it compatible with another.
  if ((version !== 1 && version !== 2 && version !== 3 && version !== 4 && version !== 5 && version !== 6 && version !== 7) || (profile !== 'rich' && profile !== 'markdown-safe') || !Array.isArray(entries) || entries.length > 128)
    throw new DocumentSchemaError('Unsupported document schema.')
  const ids = new Set<string>()
  const plugins = entries.map((entry: unknown) => {
    if (!entry || typeof entry !== 'object') throw new DocumentSchemaError('Invalid content plugin.')
    const rawPlugin = entry as Record<string, unknown>
    const id = rawPlugin.id ?? rawPlugin.Id
    const pluginVersion = rawPlugin.version ?? rawPlugin.Version
    if (typeof id !== 'string' || !id.trim() || id.length > 200 || typeof pluginVersion !== 'number'
      || !Number.isInteger(pluginVersion) || pluginVersion < 1 || pluginVersion > 2147483647 || ids.has(id))
      throw new DocumentSchemaError('Invalid or duplicate content plugin identity or version.')
    ids.add(id)
    return Object.freeze({ id, version: pluginVersion })
  }).sort((left, right) => left.id < right.id ? -1 : left.id > right.id ? 1 : 0)
  return Object.freeze({ version, profile, plugins: Object.freeze(plugins) })
}

export function confirmDocumentSchema(requested: MarkdownDocumentSchema | undefined, received: unknown): void {
  if (!requested && received == null) return // explicitly unversioned legacy session
  if (!requested || received == null) throw new DocumentSchemaError('Legacy and versioned documents require an explicit migration.')
  if (JSON.stringify(normalizeDocumentSchema(received)) !== JSON.stringify(normalizeDocumentSchema(requested)))
    throw new DocumentSchemaError('The document requires a different built-in schema revision, profile or content plugin schema.')
}

export function isSchemaError(error: unknown): boolean {
  const message = error instanceof Error ? error.message : String(error)
  return /\[SCHEMA_[A-Z_]+\]|unknown hub method.*JoinDocumentWithSchema|JoinDocumentWithSchema.*(?:does not exist|not found)/i.test(message)
}
