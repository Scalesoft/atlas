import { flattenExtensions, getExtensionField, sortExtensions, type AnyExtension } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import type { MarkdownSchemaPlugin } from '../core/plugins/types'

interface ExtensionContribution {
  readonly id: string
  readonly extensions: readonly AnyExtension[]
}

type AttributeGroup = {
  readonly types: readonly string[] | '*' | 'nodes' | 'marks'
  readonly attributes: Readonly<Record<string, unknown>>
}

/** Schema declarations are attributed to their declaring plugin, independent of plugin order. */
export function resolveSchemaOwnership(base: readonly AnyExtension[], contributions: readonly ExtensionContribution[]): {
  readonly pluginIds: ReadonlySet<string>
  readonly fieldOwners: ReadonlyMap<string, string>
} {
  const flattened: Array<{ extension: AnyExtension; owner?: string }> = [
    ...flattenExtensions([...base]).map(extension => ({ extension })),
    ...contributions.flatMap(contribution => flattenExtensions([...contribution.extensions])
      .map(extension => ({ extension, owner: contribution.id }))),
  ]
  const names = new Set<string>()
  for (const { extension, owner } of flattened) {
    if (names.has(extension.name)) throw new TypeError(`Duplicate content extension: ${extension.name}`)
    names.add(extension.name)
    if (owner && (getExtensionField<unknown>(extension, 'extendNodeSchema') || getExtensionField<unknown>(extension, 'extendMarkSchema'))) {
      throw new TypeError(`Plugin extension ${extension.name} cannot rewrite an existing node or mark schema.`)
    }
  }

  const resolved = sortExtensions(flattened.map(item => item.extension))
  const ownersByName = new Map(flattened.map(item => [item.extension.name, item.owner]))
  const nodeExtensions = resolved.filter(extension => extension.type === 'node')
  const markExtensions = resolved.filter(extension => extension.type === 'mark')
  const typed = [...nodeExtensions, ...markExtensions]
  const nodes = nodeExtensions.filter(extension => extension.name !== 'text').map(extension => extension.name)
  const marks = markExtensions.map(extension => extension.name)
  const typeByName = new Map(typed.map(extension => [extension.name, extension.type]))
  const declarations = new Map<string, Array<string | undefined>>()
  const declare = (key: string, owner?: string) => {
    const current = declarations.get(key) ?? []
    current.push(owner)
    declarations.set(key, current)
  }

  for (const extension of resolved) {
    const owner = ownersByName.get(extension.name)
    if (extension.type === 'node' || extension.type === 'mark') {
      declare(`${extension.type === 'node' ? 'nodes' : 'marks'}:${extension.name}`, owner)
      const addAttributes = getExtensionField<() => Readonly<Record<string, unknown>>>(extension, 'addAttributes', {
        name: extension.name, options: extension.options, storage: extension.storage,
      })
      for (const name of Object.keys(addAttributes?.() ?? {})) {
        declare(`${extension.type === 'node' ? 'nodes' : 'marks'}:${extension.name}:${name}`, owner)
      }
    }

    const addGlobalAttributes = getExtensionField<() => readonly AttributeGroup[]>(extension, 'addGlobalAttributes', {
      name: extension.name, options: extension.options, storage: extension.storage, extensions: typed,
    })
    for (const group of addGlobalAttributes?.() ?? []) {
      const targets = Array.isArray(group.types) ? group.types
        : group.types === '*' ? [...nodes, ...marks]
          : group.types === 'nodes' ? nodes : group.types === 'marks' ? marks : []
      for (const target of targets) {
        const type = typeByName.get(target)
        if (!type) continue
        for (const name of Object.keys(group.attributes)) {
          declare(`${type === 'node' ? 'nodes' : 'marks'}:${target}:${name}`, owner)
        }
      }
    }
  }

  const fieldOwners = new Map<string, string>()
  const pluginIds = new Set<string>()
  for (const [key, declaredBy] of declarations) {
    if (declaredBy.length > 1 && declaredBy.some(Boolean)) throw new TypeError(`Duplicate schema attribute contribution: ${key}`)
    const owner = declaredBy[0]
    if (!owner) continue
    fieldOwners.set(key, owner)
    pluginIds.add(owner)
  }
  return { pluginIds, fieldOwners }
}

export function usedSchemaPlugins(document: Node, owners: ReadonlyMap<string, MarkdownSchemaPlugin>): ReadonlySet<string> {
  const used = new Set<string>()
  const record = (key: string, attrs: Readonly<Record<string, unknown>>) => {
    const fields = [key, ...Object.keys(attrs).filter(name => attrs[name] !== null && attrs[name] !== undefined).map(name => `${key}:${name}`)]
    for (const field of fields) {
      const owner = owners.get(field)
      if (owner) used.add(owner.id)
    }
  }
  const visit = (node: Node) => {
    record(`nodes:${node.type.name}`, node.attrs)
    node.marks.forEach(mark => record(`marks:${mark.type.name}`, mark.attrs))
  }
  visit(document)
  document.descendants(visit)
  return used
}
