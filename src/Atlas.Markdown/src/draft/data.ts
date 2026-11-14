import { clonePlainData, deepFreezePlainData } from '../core/plainData'
import { normalizeDocumentSchema } from '../collab/documentSchema'
import { MarkdownDraftError, type MarkdownDraftRecord, type MarkdownDraftScope } from './types'
import { isRichFormat } from '../core/types'

export const invalid = (message: string): never => { throw new MarkdownDraftError('invalid', message) }
export function draftInteger(value: unknown, minimum = 0): number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < minimum) return invalid('Invalid draft revision or timestamp.')
  return value
}
export function draftBranch(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)) return invalid('Draft branch must be a UUID.')
  return value.toLowerCase()
}
function fields(value: Record<string, unknown>, names: readonly string[]): void {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || Object.keys(value).length !== names.length || names.some(name => !Object.hasOwn(value, name))) invalid('Unsupported draft metadata.')
}
export function normalizeMarkdownDraftScope(input: MarkdownDraftScope): MarkdownDraftScope {
  const scope = clonePlainData(input)
  fields(scope as unknown as Record<string, unknown>, ['serverId', 'tenantId', 'accountId', 'documentId'])
  for (const value of Object.values(scope)) {
    if (typeof value !== 'string' || !value.trim() || value !== value.trim() || value.length > 512 || /[\u0000-\u001f\u007f]/.test(value)) invalid('Draft scope requires explicit stable host identities.')
  }
  return deepFreezePlainData({ serverId: scope.serverId, tenantId: scope.tenantId, accountId: scope.accountId, documentId: scope.documentId })
}
/** Envelope validation only; restoring content still goes through the editor's schema validation. */
export function cloneMarkdownDraftRecord(input: MarkdownDraftRecord): MarkdownDraftRecord {
  const value = clonePlainData(input)
  fields(value as unknown as Record<string, unknown>, ['formatVersion', 'scope', 'branchId', 'revision', 'capturedAt', 'schema', 'snapshot'])
  if (value.formatVersion !== 1) invalid('Unsupported draft format.')
  fields(value.snapshot as unknown as Record<string, unknown>, ['sessionId', 'revision', 'value'])
  if (typeof value.snapshot.sessionId !== 'string' || !value.snapshot.sessionId || value.snapshot.sessionId.length > 200) invalid('Invalid draft session identity.')
  draftInteger(value.snapshot.revision)
  const schema = normalizeDocumentSchema(value.schema)
  if (schema.profile === 'markdown-safe') {
    if (typeof value.snapshot.value !== 'string') invalid('Markdown-safe draft must contain a string.')
  } else {
    const rich = value.snapshot.value
    if (!rich || typeof rich !== 'object' || !isRichFormat(rich.format) || rich.version !== 1 || !rich.document || rich.document.type !== 'doc') invalid('Rich draft must contain a document envelope.')
  }
  return deepFreezePlainData({ ...value, scope: normalizeMarkdownDraftScope(value.scope), branchId: draftBranch(value.branchId),
    revision: draftInteger(value.revision, 1), capturedAt: draftInteger(value.capturedAt), schema })
}
export const draftScopeKey = (scope: MarkdownDraftScope): string => JSON.stringify(normalizeMarkdownDraftScope(scope))
export const draftKey = (scope: MarkdownDraftScope, branch: string): string => JSON.stringify([draftScopeKey(scope), draftBranch(branch)])
