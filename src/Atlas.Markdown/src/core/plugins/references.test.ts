import { describe, expect, it } from 'vitest'
import { MarkdownPluginRegistry } from './registry'
import type { MarkdownPlugin, MarkdownReferenceProvider } from './types'

const provider: MarkdownReferenceProvider = {
  id: 'example.reference', label: 'Example', nodeType: 'exampleReference',
  read: () => null, matchUrl: () => true, resolveUrl: async () => null,
}
function plugin(id: string, references: readonly MarkdownReferenceProvider[]): MarkdownPlugin {
  return { id, apiVersion: 1, create: () => ({ references }) }
}

describe('reference provider registration', () => {
  it('copies and freezes contribution descriptors without calling host services', () => {
    const supplied = { ...provider }
    const entries = [supplied]
    const registry = new MarkdownPluginRegistry([plugin('example', entries)], 'rich')
    entries.length = 0
    supplied.label = 'Changed later'
    expect(registry.references).toHaveLength(1)
    expect(registry.references[0].label).toBe('Example')
    expect(Object.isFrozen(registry.references)).toBe(true)
    expect(Object.isFrozen(registry.references[0])).toBe(true)
    registry.destroy()
  })

  it('preserves prototype methods and their original receiver', async () => {
    class Provider implements MarkdownReferenceProvider {
      readonly id = 'class.reference'
      readonly label = 'Class'
      readonly nodeType = 'classReference'
      private url = 'https://example.com/item'
      read() { return { url: this.url, label: this.label } }
      matchUrl(url: string) { return url === this.url }
      async resolveUrl() { return { type: this.nodeType } }
    }
    const registry = new MarkdownPluginRegistry([plugin('class', [new Provider()])], 'rich')
    const saved = registry.references[0]
    expect(saved.read({ type: 'classReference' })).toEqual({ url: 'https://example.com/item', label: 'Class' })
    expect(saved.matchUrl?.('https://example.com/item')).toBe(true)
    await expect(saved.resolveUrl?.('https://example.com/item', { signal: new AbortController().signal })).resolves.toEqual({ type: 'classReference' })
    registry.destroy()
  })

  it('rejects ambiguous provider ownership and malformed contributions', () => {
    expect(() => new MarkdownPluginRegistry([plugin('one', [provider]), plugin('two', [provider])], 'rich')).toThrow(/reference/i)
    for (const invalid of [
      { ...provider, nodeType: '' },
      { ...provider, read: undefined },
      { ...provider, matchUrl: 'not a function' },
      { ...provider, resolveUrl: 'not a function' },
    ]) {
      expect(() => new MarkdownPluginRegistry([plugin('bad', [invalid as unknown as MarkdownReferenceProvider])], 'rich')).toThrow(/reference/i)
    }
  })
})

describe('plugin order', () => {
  it('sorts schema versions by ordinal id, the same in every locale and as the Sync server', () => {
    // en puts "Zeta" last, cs puts "charts" after "heading"; ordinal (StringComparer.Ordinal in Sync) does neither.
    const versioned = (id: string): MarkdownPlugin => ({ id, apiVersion: 1, schemaVersion: 1, create: () => ({}) })
    const registry = new MarkdownPluginRegistry(['heading', 'charts', 'Zeta'].map(versioned), 'rich')
    expect(registry.installedSchemaVersions.map(({ id }) => id)).toEqual(['Zeta', 'charts', 'heading'])
    registry.destroy()
  })
})
