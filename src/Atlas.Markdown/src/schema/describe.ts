import type { AnyExtension } from '@tiptap/core'
import type { MarkType, NodeType } from '@tiptap/pm/model'
import { MAX_TABLE_CELLS, MAX_TABLE_SPAN, builtinSchemaRevision } from '../core/documentConstraints'
import type { MarkdownProfile } from '../core/kit'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin } from '../core/plugins/types'
import type { JsonValue } from '../core/types'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import { MarkdownDocumentCodec } from '../engine/documentCodec'
import { builtinAttributeConstraints, builtinStructuredValidators } from './builtin'
import { markdownValueConstraintOf, type MarkdownConstraintEnforcement, type MarkdownStructuredValidator, type MarkdownValueConstraint } from './constraints'

export interface MarkdownSchemaAttribute {
  readonly name: string
  /** The plugin that contributes the attribute to a node or mark it does not own (heading-anchors' headingId). */
  readonly owner?: string
  /** Absent: the attribute has no default and is required. A default is itself validated against the constraint. */
  readonly default?: JsonValue
  readonly required?: true
  readonly constraint: MarkdownValueConstraint
  readonly enforcement: MarkdownConstraintEnforcement
}
export interface MarkdownSchemaNode {
  readonly name: string
  /** The plugin whose document requirement (`plugins` of the envelope) a document using the node must declare. */
  readonly owner?: string
  readonly text?: true
  readonly inline: boolean
  readonly groups: readonly string[]
  /** ProseMirror content expression; empty for a leaf. */
  readonly content: string
  /** Marks the node allows on its content (its children's marks). */
  readonly marks: readonly string[]
  readonly attrs: readonly MarkdownSchemaAttribute[]
}
export interface MarkdownSchemaMark {
  readonly name: string
  readonly owner?: string
  /** Marks this mark cannot share a node with (itself included unless it may repeat). */
  readonly excludes: readonly string[]
  readonly attrs: readonly MarkdownSchemaAttribute[]
}
/** A kind of entity and the inline atom that shows it (defineMarkdownEntityKind). */
export interface MarkdownSchemaEntity {
  readonly kind: string
  /** The plugin that declares the kind and owns its node. */
  readonly owner: string
  readonly inline: string
}
export interface MarkdownSchemaRecognizer {
  readonly id: string
  readonly kind: string
  /** The name of the pattern in `patterns` the text matches. */
  readonly pattern: string
}
export interface MarkdownSchemaPluginEntry {
  readonly id: string
  readonly version: number
  /** Older stored versions the editor migrates to `version` when it opens a document. */
  readonly migratesFrom: readonly number[]
}
/**
 * The document schema of an editor configuration as data: everything a server needs to decide, without running the
 * editor, whether the editor opens a stored rich document (Atlas.Markdown.Sync's MarkdownDocumentValidator reads
 * it). Marks are listed in rank order.
 */
export interface MarkdownSchemaManifest {
  readonly format: 'atlas-markdown-schema'
  readonly version: 1
  readonly profile: MarkdownProfile
  /** The built-in content schema revision (MarkdownDocumentSchema.version). */
  readonly schemaRevision: number
  readonly topNode: string
  readonly plugins: readonly MarkdownSchemaPluginEntry[]
  readonly nodes: readonly MarkdownSchemaNode[]
  readonly marks: readonly MarkdownSchemaMark[]
  /** Every structured validator the constraints name, with its description. */
  readonly validators: Readonly<Record<string, MarkdownStructuredValidator>>
  /** Every kind of entity an inline atom of the configuration shows, by kind. */
  readonly entities: readonly MarkdownSchemaEntity[]
  readonly recognizers: readonly MarkdownSchemaRecognizer[]
  /** Regular expression sources the recognizers name, read the same by JavaScript (flag `u`) and .NET. */
  readonly patterns: Readonly<Record<string, string>>
  readonly limits: { readonly maxTableCells: number; readonly maxTableSpan: number }
}
export interface DescribeMarkdownSchemaOptions {
  readonly profile?: MarkdownProfile
  readonly resourcePolicy?: MarkdownResourcePolicy
  readonly codeBlock?: AnyExtension
  /** Descriptions of structured validators that third-party plugins name; the built-in ones are always known. */
  readonly validators?: Readonly<Record<string, MarkdownStructuredValidator>>
}

const sortById = <T extends { id: string }>(items: T[]) => items.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))

/**
 * Describe the document schema of `plugins`, configured exactly as a host configures its editor (same plugin
 * factories and options, profile, resource policy and code block). Headless: no DOM or view is created.
 */
export function describeMarkdownSchema(plugins: readonly MarkdownPlugin[], options: DescribeMarkdownSchemaOptions = {}): MarkdownSchemaManifest {
  const profile = options.profile ?? 'rich'
  const registry = new MarkdownPluginRegistry(plugins, profile)
  try {
    const codec = new MarkdownDocumentCodec(profile, registry, { profile, resourcePolicy: options.resourcePolicy, codeBlock: options.codeBlock })
    const schema = codec.schema
    const owners = new Map([...codec.schemaOwners].map(([field, plugin]) => [field, plugin.id]))
    const validators = new Map<string, MarkdownStructuredValidator>()
    const known = { ...builtinStructuredValidators, ...options.validators }
    const attributes = (kind: 'nodes' | 'marks', type: NodeType | MarkType): MarkdownSchemaAttribute[] =>
      Object.entries(type.spec.attrs ?? {}).map(([name, spec]) => {
        const key = `${kind}:${type.name}:${name}`
        const declared = markdownValueConstraintOf(spec.validate)
        const builtin = builtinAttributeConstraints[key]
        if (declared && builtin) throw new TypeError(`Attribute ${key} declares its constraint twice.`)
        const constraint = declared ?? builtin?.constraint ?? { kind: 'unconstrained' as const }
        if (constraint.kind === 'structured') {
          const description = known[constraint.validator]
          if (description) validators.set(constraint.validator, description)
        }
        const owner = owners.get(key)
        return {
          name,
          ...(owner && owner !== owners.get(`${kind}:${type.name}`) ? { owner } : {}),
          ...(Object.hasOwn(spec, 'default') ? { default: spec.default as JsonValue } : { required: true as const }),
          constraint,
          enforcement: builtin?.enforcement ?? 'editor',
        }
      })
    const marks = Object.values(schema.marks)
    // A kind whose node the profile leaves out (emoji in markdown-safe) is not part of this configuration.
    const kinds = plugins.flatMap(plugin => (plugin.entities ?? []).filter(kind => schema.nodes[kind.inline]).map(kind => ({ kind, owner: plugin.id })))
    for (const { kind, owner } of kinds) {
      const node = schema.nodes[kind.inline]!
      if (owners.get(`nodes:${kind.inline}`) !== owner || !node.isInline || !node.isAtom) throw new TypeError(`Entity kind ${kind.kind} names ${kind.inline}, no inline atom of plugin ${owner}.`)
    }
    // The manifest and the server key them by name alone.
    const unique = <T>(label: string, items: T[], key: (item: T) => string) => {
      const keys = items.map(key)
      const duplicate = keys.find((value, index) => keys.indexOf(value) !== index)
      if (duplicate) throw new TypeError(`Duplicate ${label}: ${duplicate}`)
      return items.sort((a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0))
    }
    const migrations = new Map(plugins.map(plugin => [plugin.id, plugin]))
    return {
      format: 'atlas-markdown-schema',
      version: 1,
      profile,
      schemaRevision: builtinSchemaRevision(profile),
      topNode: schema.topNodeType.name,
      plugins: sortById(registry.schemaPlugins.map(({ id, version }) => {
        // A chain of migration steps must reach the installed version.
        const steps = new Map((migrations.get(id)?.migrations ?? []).map(step => [step.from, step.to]))
        const migratesFrom = [...steps.keys()].filter(from => {
          let at = from
          while (steps.has(at) && at < version) at = steps.get(at)!
          return at === version
        }).sort((a, b) => a - b)
        return { id, version, migratesFrom }
      })),
      nodes: Object.values(schema.nodes).map(type => ({
        name: type.name,
        ...(owners.has(`nodes:${type.name}`) ? { owner: owners.get(`nodes:${type.name}`) } : {}),
        ...(type.isText ? { text: true as const } : {}),
        inline: type.isInline,
        groups: (type.spec.group ?? '').split(' ').filter(Boolean),
        content: type.spec.content ?? '',
        marks: marks.filter(mark => type.allowsMarkType(mark)).map(mark => mark.name),
        attrs: attributes('nodes', type),
      })),
      marks: marks.map(type => ({
        name: type.name,
        ...(owners.has(`marks:${type.name}`) ? { owner: owners.get(`marks:${type.name}`) } : {}),
        excludes: marks.filter(other => type.excludes(other)).map(other => other.name),
        attrs: attributes('marks', type),
      })),
      validators: Object.fromEntries([...validators].sort(([a], [b]) => (a < b ? -1 : 1))),
      entities: unique('entity kind', kinds.map(({ kind, owner }) => ({ kind: kind.kind, owner, inline: kind.inline })), entity => entity.kind),
      recognizers: unique('recognizer', kinds.flatMap(({ kind }) => (kind.recognizers ?? []).map(recognizer => ({ id: recognizer.id, kind: kind.kind, pattern: recognizer.pattern }))), recognizer => recognizer.id),
      patterns: Object.fromEntries(unique('pattern', kinds.flatMap(({ kind }) => Object.entries(kind.patterns ?? {})), ([name]) => name)),
      limits: { maxTableCells: MAX_TABLE_CELLS, maxTableSpan: MAX_TABLE_SPAN },
    }
  } finally {
    registry.destroy()
  }
}
