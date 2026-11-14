// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createSecretsPlugin, type SecretOperationContext, type SecretDiscovery } from './index'
import { createMarkdownEditorController, migrateMarkdownValue, type MarkdownRichValue } from '../../core'
// The secrets plugin loads concealment lazily when a reference renders; loading it up front keeps that import from finishing
// after the test environment is torn down (an unhandled EnvironmentTeardownError on a slow runner).
import '../../concealment'

// Representative persisted documents per schema version. They are plain JSON so a host (for example
// Atlas server guards) can reuse the same shapes; see ./README.md "Migration from v1/v2".
const fixtures = import.meta.glob<MarkdownRichValue>('./fixtures/secrets-v*.json', { eager: true, import: 'default' })
const fixture = (version: 1 | 2 | 3 | 4): MarkdownRichValue => structuredClone(fixtures[`./fixtures/secrets-v${version}.json`]!)

const id = 'd847b3af-4a87-4e97-aab8-a74a4cf48c89'
const otherId = 'ccff88d6-437d-4665-83a8-4c179ad8a523'
const v4Defaults = { field: 'password', fields: null, showCopy: true, showDetails: false, autoHideSeconds: 60 } as const

const disposers: (() => void)[] = []
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null) })
afterEach(() => { disposers.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); vi.restoreAllMocks() })
const services = () => ({
  discover: async (_context: SecretOperationContext): Promise<SecretDiscovery> => 'hidden',
  requestAccess: async () => {}, reveal: async () => ({ values: {} }), copy: async () => {}, subscribeRevocation: () => () => {},
})
const plugins = () => [createSecretsPlugin({ services: services() })]
/** Load through the real editor controller and view: migration, schema validation and, once the
 * document is parsed into the editor, schema defaults for every absent attribute. */
function load(value: unknown) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: plugins(), source: { kind: 'local', value: value as MarkdownRichValue } })
  disposers.push(() => controller.destroy())
  const element = document.createElement('div'); document.body.append(element)
  controller.attachView(element, { ariaLabel: 'Secrets migration' })
  return JSON.parse(JSON.stringify(controller.getProjection().rich)) as MarkdownRichValue
}
type Json = { type?: string; attrs?: Record<string, unknown>; content?: Json[] }
const references = (value: MarkdownRichValue) => {
  const found: { type: string; attrs: Record<string, unknown> }[] = []
  const walk = (node: Json) => {
    if (node.type === 'secretReference' || node.type === 'secretInlineReference') found.push({ type: node.type, attrs: node.attrs ?? {} })
    node.content?.forEach(walk)
  }
  walk(value.document as Json)
  return found
}
const withSecrets = (version: unknown, content: Json[]) => ({ format: 'atlas-rich', version: 1, plugins: [{ id: 'secrets', version }], document: { type: 'doc', content } })

describe('secrets schema migration fixtures', () => {
  it('declares one representative document per schema version', () => {
    expect(Object.keys(fixtures).sort()).toEqual(['./fixtures/secrets-v1.json', './fixtures/secrets-v2.json', './fixtures/secrets-v3.json', './fixtures/secrets-v4.json'])
    for (const version of [1, 2, 3, 4] as const) expect(fixture(version).plugins).toEqual([{ id: 'secrets', version }])
  })

  it.each([1, 2, 3] as const)('migrates v%i metadata to v4 without rewriting the document tree', version => {
    const original = fixture(version)
    const migrated = migrateMarkdownValue(original, 'rich', plugins())
    expect(migrated.plugins).toEqual([{ id: 'secrets', version: 4 }])
    // Both steps are identity migrations: defaults are supplied by the schema when the tree is parsed.
    expect(migrated.document).toEqual(original.document)
    expect(original.plugins).toEqual([{ id: 'secrets', version }])
  })

  it('loads a v1 bare-UUID block reference with the v4 defaults', () => {
    const loaded = load(fixture(1))
    expect(loaded.plugins).toEqual([{ id: 'secrets', version: 4 }])
    expect(references(loaded)).toEqual([{ type: 'secretReference', attrs: { secretId: id, ...v4Defaults } }])
  })

  it('loads v2 block and inline references with the v4 defaults and keeps their presentation', () => {
    const loaded = load(fixture(2))
    expect(loaded.plugins).toEqual([{ id: 'secrets', version: 4 }])
    // Since v4 inline references carry the field list too; older ones keep showing their single field.
    expect(references(loaded)).toEqual([
      { type: 'secretReference', attrs: { secretId: id, field: 'username', fields: null, showCopy: false, showDetails: true, autoHideSeconds: 60 } },
      { type: 'secretInlineReference', attrs: { secretId: otherId, ...v4Defaults } },
      { type: 'secretInlineReference', attrs: { secretId: otherId, field: 'url', fields: null, showCopy: true, showDetails: false, autoHideSeconds: 60 } },
    ])
  })

  it('loads a v3 document as v4 without changing its references', () => {
    const original = fixture(3)
    const loaded = load(original)
    expect(loaded.plugins).toEqual([{ id: 'secrets', version: 4 }])
    expect(references(loaded).map(item => item.attrs.fields)).toEqual([['title', 'username', 'password', 'url'], null, null])
    expect(references(loaded).map(item => item.attrs.autoHideSeconds)).toEqual([null, 3600, 5])
  })

  it('round-trips a v4 document unchanged through migration and the editor', () => {
    const original = fixture(4)
    expect(migrateMarkdownValue(original, 'rich', plugins())).toEqual(original)
    const loaded = load(original)
    expect(loaded.plugins).toEqual(original.plugins)
    // Secret attributes come back exactly; core nodes may add their own defaults (paragraph textAlign).
    expect(references(loaded)).toEqual(references(original))
    expect(load(loaded)).toEqual(loaded)
  })

  it('keeps a loaded field list as persisted; only commands store it canonically', () => {
    const loaded = load(withSecrets(4, [
      { type: 'secretReference', attrs: { secretId: id, fields: ['url', 'username'] } },
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: otherId, fields: ['password', 'username'] } }] },
    ]))
    expect(references(loaded).map(item => item.attrs.fields)).toEqual([['url', 'username'], ['password', 'username']])
  })
})

describe('secrets schema version handling', () => {
  const block = [{ type: 'secretReference', attrs: { secretId: id } }]

  it('rejects data newer than the installed schema', () => {
    expect(() => load(withSecrets(5, block))).toThrow(/newer than the installed schema/)
  })

  it.each([0, -1, 1.5, '2'])('rejects version %j that has no declared migration path', version => {
    expect(() => load(withSecrets(version, block))).toThrow(/Missing migration for plugin secrets/)
  })

  it('rejects a secrets requirement without a version', () => {
    const value = { ...withSecrets(4, block), plugins: [{ id: 'secrets' }] }
    expect(() => load(value)).toThrow(/Invalid document plugin requirement/)
  })

  it('rejects secret nodes without plugin metadata instead of guessing a version', () => {
    const { plugins: _plugins, ...unversioned } = withSecrets(1, block)
    expect(() => load(unversioned)).toThrow(/Document plugin metadata is required for secrets/)
  })

  it('rejects a document requiring secrets when the plugin is not registered', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', plugins: [], source: { kind: 'local', value: fixture(4) } })).toThrow(/missing plugin: secrets/)
  })

  it('does not gate attributes by the declared version; older documents are validated by the v4 rules', () => {
    // Identity migrations: a v1-labelled document using v4 attributes loads as v4.
    const lenient = load(withSecrets(1, [
      { type: 'secretReference', attrs: { secretId: id, fields: ['password'], autoHideSeconds: 5 } },
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: otherId, fields: ['username'], autoHideSeconds: null } }] },
    ]))
    expect(lenient.plugins).toEqual([{ id: 'secrets', version: 4 }])
    expect(references(lenient).map(item => item.attrs.autoHideSeconds)).toEqual([5, null])
    const invalid: Json[] = [
      { type: 'secretReference', attrs: {} },
      { type: 'secretReference', attrs: { secretId: id.toUpperCase() } },
      { type: 'secretReference', attrs: { secretId: id, field: 'ciphertext' } },
      { type: 'secretReference', attrs: { secretId: id, fields: [] } },
      { type: 'secretReference', attrs: { secretId: id, fields: ['url', 'url'] } },
      { type: 'secretReference', attrs: { secretId: id, autoHideSeconds: 4 } },
      { type: 'secretReference', attrs: { secretId: id, autoHideSeconds: 3601 } },
      { type: 'secretReference', attrs: { secretId: id, autoHideSeconds: 60.5 } },
      { type: 'secretReference', attrs: { secretId: id, autoHideSeconds: '60' } },
      { type: 'secretReference', attrs: { secretId: id, value: 'LEAK' } },
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: id, fields: [] } }] },
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: id, fields: ['ciphertext'] } }] },
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: id, password: 'LEAK' } }] },
    ]
    for (const version of [1, 2, 3, 4]) for (const node of invalid) expect(() => load(withSecrets(version, [node])), JSON.stringify(node)).toThrow()
  })
})
