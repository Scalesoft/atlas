import type { JSONContent } from '@tiptap/core'
import type { Node, Schema } from '@tiptap/pm/model'
import type { MarkdownProfile, MarkdownRichValue, MarkdownValue } from '../core/types'
import { isRichFormat } from '../core/types'
import type { MarkdownSchemaPlugin } from '../core/plugins/types'
import { validateBuiltinDocument } from './builtinDocumentValidation'
import { clonePlainData } from '../core/plainData'

/** Validate an already parsed input before installing it in a live or static view.
 * This is deliberately separate from transaction filtering: table plugins may
 * repair intermediate transaction geometry before producing their final state.
 */
export function validateParsedDocument(document: Node): Node {
  if (document.type !== document.type.schema.topNodeType) throw new TypeError('Document must have a doc root.')
  clonePlainData(document.toJSON())
  document.check()
  validateBuiltinDocument(document)
  return document
}

export function validateValue<P extends MarkdownProfile>(profile: P, value: unknown, schema: Schema, plugins: readonly MarkdownSchemaPlugin[], schemaOwners: ReadonlyMap<string, MarkdownSchemaPlugin>): asserts value is MarkdownValue<P> {
  if (profile === 'markdown-safe') {
    if (typeof value !== 'string') throw new TypeError('Markdown-safe value must be a string.')
    return
  }
  if (!value || typeof value !== 'object') throw new TypeError('Rich value must be a document envelope.')
  const envelope = value as Partial<MarkdownRichValue>
  if (!isRichFormat(envelope.format) || envelope.version !== 1) throw new TypeError('Unsupported rich document format or version.')
  if (Object.keys(envelope).some(key => !['format', 'version', 'plugins', 'document'].includes(key))) {
    throw new TypeError('Unsupported rich document field.')
  }
  if (envelope.plugins !== undefined) {
    if (!Array.isArray(envelope.plugins)) throw new TypeError('Invalid document plugin requirements.')
    const seen = new Set<string>()
    for (const requirement of envelope.plugins) {
      if (!requirement || typeof requirement.id !== 'string' || !Number.isSafeInteger(requirement.version) || requirement.version < 1
        || Object.keys(requirement).some(key => !['id', 'version'].includes(key)) || seen.has(requirement.id)) {
        throw new TypeError('Invalid document plugin requirement.')
      }
      seen.add(requirement.id)
      const installed = plugins.find(plugin => plugin.id === requirement.id)
      if (!installed) throw new TypeError(`Document requires missing plugin: ${requirement.id}`)
      if (installed.version !== requirement.version) throw new TypeError(`Incompatible schema version for plugin ${requirement.id}; migrate the document before editing.`)
    }
  }
  validateRichNode(envelope.document as JSONContent, schema)
  validatePluginMetadata(envelope.document as JSONContent, envelope.plugins ?? [], schemaOwners)
  const document = schema.nodeFromJSON(envelope.document)
  if (document.type !== schema.topNodeType) throw new TypeError('Rich document must have a doc root.')
  validateParsedDocument(document)
}

function validatePluginMetadata(node: JSONContent, requirements: readonly MarkdownSchemaPlugin[], owners: ReadonlyMap<string, MarkdownSchemaPlugin>, mark = false): void {
  const key = `${mark ? 'marks' : 'nodes'}:${node.type}`
  for (const field of [key, ...Object.keys(node.attrs ?? {}).map(attribute => `${key}:${attribute}`)]) {
    const owner = owners.get(field)
    if (owner && !requirements.some(requirement => requirement.id === owner.id && requirement.version === owner.version)) {
      throw new TypeError(`Document plugin metadata is required for ${owner.id}; migrate unversioned plugin content before editing.`)
    }
  }
  node.content?.forEach(child => validatePluginMetadata(child, requirements, owners))
  node.marks?.forEach(child => validatePluginMetadata(child, requirements, owners, true))
}

function validateRichNode(node: JSONContent, schema: Schema, mark = false): void {
  if (!node || typeof node !== 'object' || Array.isArray(node)) throw new TypeError('Invalid rich node.')
  const type = mark ? schema.marks[node.type ?? ''] : schema.nodes[node.type ?? '']
  if (!type) throw new TypeError('Unsupported rich node or mark.')
  const fields = mark ? ['type', 'attrs'] : node.type === 'text' ? ['type', 'text', 'marks'] : ['type', 'attrs', 'content', 'marks']
  if (Object.keys(node).some(key => !fields.includes(key))) throw new TypeError('Unsupported rich node field.')
  if (node.attrs && (typeof node.attrs !== 'object' || Array.isArray(node.attrs)
    || Object.keys(node.attrs).some(key => !Object.hasOwn(type.spec.attrs ?? {}, key)))) throw new TypeError('Unsupported rich node attribute.')
  if (node.content !== undefined && !Array.isArray(node.content)) throw new TypeError('Invalid rich content.')
  if (node.marks !== undefined && !Array.isArray(node.marks)) throw new TypeError('Invalid rich marks.')
  node.content?.forEach(child => validateRichNode(child, schema))
  node.marks?.forEach(child => validateRichNode(child, schema, true))
}
