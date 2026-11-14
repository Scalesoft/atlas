import type { Editor, JSONContent } from '@tiptap/core'
import type { MarkdownDocumentSchema, MarkdownProjection, MarkdownValue } from '../../core/types'
import { RICH_FORMAT } from '../../core/types'
import type { MarkdownPlugin } from '../../core/plugins/types'
import { MarkdownPluginRegistry } from '../../core/plugins/registry'
import { clonePlainData } from '../../core/plainData'
import { MarkdownDocumentCodec } from '../../engine/documentCodec'
import { createTiptapEditor } from '../../engine/editorFactory'
import { destroyEditor } from '../../engine/editorLifecycle'
import { confirmDocumentSchema } from '../documentSchema'
import { builtinSchemaRevision } from '../../core/documentConstraints'
import { RecoveryPreviewError } from './preview'

function sameJson(left: unknown, right: unknown): boolean {
  if (left === right) return true
  if (!left || !right || typeof left !== 'object' || typeof right !== 'object'
    || Array.isArray(left) !== Array.isArray(right)) return false
  const keys = Object.keys(left)
  return keys.length === Object.keys(right).length && keys.every(key => Object.hasOwn(right, key)
    && sameJson((left as Record<string, unknown>)[key], (right as Record<string, unknown>)[key]))
}

/** Lazy, unmounted browser engine. No registry create/attach/render hooks or collaboration. */
export function projectRecoveryDocument(document: JSONContent, schema: MarkdownDocumentSchema,
  plugins: readonly MarkdownPlugin[], checkAbort: () => void): { value: MarkdownValue; projection: MarkdownProjection } {
  let registry: MarkdownPluginRegistry | undefined, editor: Editor | null = null
  let result: { value: MarkdownValue; projection: MarkdownProjection } | undefined, failure: unknown
  const cleanupErrors: unknown[] = []
  try {
    checkAbort()
    let codec: MarkdownDocumentCodec<typeof schema.profile>
    try {
      registry = new MarkdownPluginRegistry(plugins, schema.profile)
      checkAbort()
      codec = new MarkdownDocumentCodec(schema.profile, registry, { profile: schema.profile })
      confirmDocumentSchema(schema, { version: builtinSchemaRevision(schema.profile), profile: schema.profile, plugins: registry.schemaPlugins })
    } catch (cause) {
      if (cause instanceof RecoveryPreviewError) throw cause
      throw new RecoveryPreviewError('schema', 'The installed recovery engine does not match the selected schema.', { cause })
    }
    checkAbort()
    // Strict validation precedes nodeFromJSON and editor initialization. No migrations,
    // schema repair converter or Markdown round-trip may silently remove input fields.
    const parsed = codec.parseRichDocument({ format: RICH_FORMAT, version: 1, document, plugins: schema.plugins })
    const expected = clonePlainData(parsed.toJSON())
    checkAbort()
    editor = createTiptapEditor({ element: null, editable: false, extensions: codec.extensions, content: parsed.toJSON() })
    codec.validateParsedDocument(editor.state.doc)
    // Node.eq requires identical NodeTypes, but reparsing the expected JSON with the new
    // schema could itself discard attributes. Compare the original validated JSON directly.
    if (!sameJson(expected, editor.state.doc.toJSON())) throw new TypeError('Recovery engine initialization changed the selected content.')
    checkAbort()
    result = { value: codec.getValue(editor, editor.state.doc), projection: codec.project(editor, editor.state.doc) }
    checkAbort()
  } catch (cause) { failure = cause }
  cleanupErrors.push(...destroyEditor(editor))
  try { registry?.destroy() } catch (cause) { cleanupErrors.push(cause) }
  if (failure !== undefined || cleanupErrors.length) {
    const cause = cleanupErrors.length ? new AggregateError([...(failure === undefined ? [] : [failure]), ...cleanupErrors], 'Recovery projection cleanup failed.') : failure
    if (failure instanceof RecoveryPreviewError && !cleanupErrors.length) throw failure
    throw new RecoveryPreviewError('content', 'The recovery content could not be projected without changes.', { cause })
  }
  return result!
}
