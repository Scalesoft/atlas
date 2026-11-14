import type { JSONContent } from '@tiptap/core'
import type { Node, Schema } from '@tiptap/pm/model'

type AttributeSpecs = Readonly<Record<string, { default?: unknown }>> | undefined

/**
 * A document's JSON as Atlas stores and sends it: an attribute of a node or mark at its plain schema default is left out, which
 * nodeFromJSON fills in again. ProseMirror's toJSON writes every attribute, which on a large table page was over half the JSON.
 * The server stores the same form (RichDocument.WithoutDefaultAttributes; parity fixture tests/fixtures/default-attributes.json).
 */
export function storedDocumentJson(document: Node): JSONContent {
  return withoutDefaultAttributes(document.toJSON() as JSONContent, document.type.schema)
}

export function withoutDefaultAttributes(node: JSONContent, schema: Schema): JSONContent {
  const result: JSONContent = { ...node }
  dropDefaults(result, schema.nodes[node.type ?? '']?.spec.attrs)
  if (node.marks) result.marks = node.marks.map(mark => dropDefaults({ ...mark }, schema.marks[mark.type]?.spec.attrs))
  if (node.content) result.content = node.content.map(child => withoutDefaultAttributes(child, schema))
  return result
}

function dropDefaults<T extends { attrs?: Record<string, unknown> }>(item: T, specs: AttributeSpecs): T {
  if (!item.attrs || !specs) return item
  const kept = Object.entries(item.attrs).filter(([name, value]) => {
    const spec = specs[name]
    return !(spec && Object.hasOwn(spec, 'default') && sameValue(value, spec.default))
  })
  if (kept.length === Object.keys(item.attrs).length) return item
  if (kept.length) item.attrs = Object.fromEntries(kept)
  else delete item.attrs
  return item
}

// Only a plain value (null, a boolean, number or string): a default object (a chart's data) stays, its readers take it as written.
const sameValue = (value: unknown, fallback: unknown) => value === fallback && (fallback === null || typeof fallback !== 'object')
