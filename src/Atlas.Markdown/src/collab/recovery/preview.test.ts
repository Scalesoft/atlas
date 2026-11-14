// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Node } from '@tiptap/core'
import * as Y from 'yjs'
import { prepareMarkdownRecovery, RecoveryPreviewError } from '../../collaboration/signalr'
import type { MarkdownDocumentSchema, MarkdownRichValue } from '../../core/types'
import type { MarkdownPlugin } from '../../core/plugins/types'
import * as editorFactory from '../../engine/editorFactory'
import type { RecoveryCheckpoint, RecoveryStore } from './types'

type Options = Parameters<typeof prepareMarkdownRecovery>[0]
type JsonNode = { type: string; text?: string; attrs?: Record<string, unknown>; marks?: Array<{ type: string; attrs?: Record<string, unknown> }>; content?: JsonNode[] }
const scope = { serverId: 'server', tenantId: 'tenant', accountId: 'account', documentId: 'document' }
const branchId = '11111111-2222-4333-8444-555555555555'
const richSchema: MarkdownDocumentSchema = { version: 7, profile: 'rich', plugins: [] }
const safeSchema: MarkdownDocumentSchema = { version: 1, profile: 'markdown-safe', plugins: [] }
const paragraph = (text: string): JsonNode => ({ type: 'paragraph', content: [{ type: 'text', text }] })

function encode(content: JsonNode[], fragment = 'default'): Uint8Array {
  const doc = new Y.Doc()
  function node(value: JsonNode): Y.XmlElement | Y.XmlText {
    if (value.type === 'text') {
      const text = new Y.XmlText()
      text.applyDelta([{ insert: value.text ?? '', attributes: Object.fromEntries((value.marks ?? []).map(mark => [mark.type, mark.attrs ?? {}])) }])
      return text
    }
    const element = new Y.XmlElement(value.type)
    for (const [key, attribute] of Object.entries(value.attrs ?? {})) element.setAttribute(key, attribute as string)
    element.insert(0, (value.content ?? []).map(node))
    return element
  }
  doc.getXmlFragment(fragment).insert(0, content.map(node))
  const update = Y.encodeStateAsUpdate(doc)
  doc.destroy()
  return update
}
function checkpoint(content: JsonNode[] = [paragraph('Saved locally')], schema = richSchema): RecoveryCheckpoint {
  return { formatVersion: 1, scope: { ...scope }, branchId, revision: 4, capturedAt: 1_788_620_400_000,
    schema: { ...schema, plugins: schema.plugins.map(item => ({ ...item })) }, fragment: 'default',
    observedSequence: 2, documentRevision: 3, connectionGeneration: 1, epoch: null, replayPolicy: 'manual-only',
    yjsState: encode(content), pendingUpdates: [] }
}
function fixture(value: RecoveryCheckpoint | null = checkpoint()) {
  const store = { read: vi.fn(async () => value), write: vi.fn(), list: vi.fn(), discard: vi.fn(), close: vi.fn() } satisfies RecoveryStore
  const options: Options = { store, scope: { ...scope }, branchId, revision: 4,
    schema: { ...richSchema, plugins: [] }, fragment: 'default' }
  return { store, options }
}
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
function pluginProbe() {
  const abort = vi.fn(), onCreate = vi.fn(), onAttach = vi.fn(), onRender = vi.fn(), migrate = vi.fn(value => value)
  const signals: AbortSignal[] = []
  const create = vi.fn(({ signal }: { signal: AbortSignal }) => {
    signals.push(signal); signal.addEventListener('abort', abort)
    return { onCreate, onAttach, onRender, markdownLossy: () => true,
      extensions: [Node.create({ name: 'recoveryCard', group: 'block', atom: true,
        addAttributes: () => ({ label: { default: 'Card' } }), renderHTML: () => ['div'],
        renderText: ({ node }) => node.attrs.label,
        addStorage: () => ({ markdown: { serialize: (state: { write(value: string): void }, node: { attrs: { label: string } }) => state.write(node.attrs.label) } }),
      })] }
  })
  const plugin: MarkdownPlugin = { id: 'test.recovery-card', apiVersion: 1, schemaVersion: 2,
    profiles: ['rich'], migrations: [{ from: 1, to: 2, migrate }], create }
  const schema: MarkdownDocumentSchema = { ...richSchema, plugins: [{ id: plugin.id, version: 2 }] }
  return { plugin, schema, create, signals, abort, onCreate, onAttach, onRender, migrate }
}
afterEach(() => { document.body.replaceChildren(); vi.restoreAllMocks() })

describe('public manual recovery preview selection boundary', () => {
  it('reads one exact branch without writing, discarding, closing or mounting anything', async () => {
    const { store, options } = fixture()
    const result = await prepareMarkdownRecovery(options)
    expect(store.read).toHaveBeenCalledExactlyOnceWith(scope, branchId)
    expect(store.write).not.toHaveBeenCalled(); expect(store.list).not.toHaveBeenCalled()
    expect(store.discard).not.toHaveBeenCalled(); expect(store.close).not.toHaveBeenCalled()
    expect(document.body.childElementCount).toBe(0)
    expect(result.source).toMatchObject({ scope, branchId, revision: 4, pendingCount: 0 })
    expect(result.schema).toEqual(richSchema)
    expect(result.fragment).toBe('default')
    expect(result.projection.text).toBe('Saved locally')
    expect(result.value).toEqual({ ...result.projection.rich, document: { type: 'doc', content: [paragraph('Saved locally')] } })
  })

  it('reports missing branches without plugin initialization', async () => {
    const { options } = fixture(null), plugin = pluginProbe()
    await expect(prepareMarkdownRecovery({ ...options, plugins: [plugin.plugin] })).rejects.toMatchObject({ code: 'missing' })
    expect(plugin.create).not.toHaveBeenCalled()
  })

  it.each(['serverId', 'tenantId', 'accountId', 'documentId'] as const)('rejects a custom store returning another %s', async field => {
    const value = checkpoint(), plugin = pluginProbe()
    Object.assign(value.scope, { [field]: 'another' })
    const { options } = fixture(value)
    await expect(prepareMarkdownRecovery({ ...options, plugins: [plugin.plugin] })).rejects.toMatchObject({ code: 'scope' })
    expect(plugin.create).not.toHaveBeenCalled()
  })

  it.each([3, 5])('rejects revision %s instead of previewing a different selected version', async revision => {
    const { options } = fixture({ ...checkpoint(), revision })
    await expect(prepareMarkdownRecovery(options)).rejects.toMatchObject({ code: 'stale' })
  })

  it('rejects a different returned branch', async () => {
    const { options } = fixture({ ...checkpoint(), branchId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee' })
    await expect(prepareMarkdownRecovery(options)).rejects.toMatchObject({ code: 'scope' })
  })

  it.each([null, safeSchema, { ...richSchema, version: 2 }, { ...richSchema, plugins: [{ id: 'missing', version: 1 }] }])('rejects incompatible checkpoint schema %s before plugins', async schema => {
    const { options } = fixture({ ...checkpoint(), schema: schema as MarkdownDocumentSchema | null }), plugin = pluginProbe()
    await expect(prepareMarkdownRecovery({ ...options, plugins: [plugin.plugin] })).rejects.toMatchObject({ code: 'schema' })
    expect(plugin.create).not.toHaveBeenCalled()
  })

  it('rejects a fragment mismatch before plugins', async () => {
    const { options } = fixture({ ...checkpoint(), fragment: 'other' }), plugin = pluginProbe()
    await expect(prepareMarkdownRecovery({ ...options, plugins: [plugin.plugin] })).rejects.toMatchObject({ code: 'schema' })
    expect(plugin.create).not.toHaveBeenCalled()
  })

  it('maps custom store failures to a public storage error', async () => {
    const { store, options } = fixture()
    store.read.mockRejectedValue(new Error('database stopped'))
    const operation = prepareMarkdownRecovery(options)
    await expect(operation).rejects.toBeInstanceOf(RecoveryPreviewError)
    await expect(operation).rejects.toMatchObject({ code: 'storage' })
  })

  it('maps malformed custom store checkpoints to content errors', async () => {
    const { options } = fixture({ ...checkpoint(), yjsState: [1, 2] } as unknown as RecoveryCheckpoint)
    await expect(prepareMarkdownRecovery(options)).rejects.toMatchObject({ code: 'content' })
  })

  it('captures selected options and plugin array before waiting for storage', async () => {
    const pending = deferred<RecoveryCheckpoint | null>(), { store, options } = fixture()
    const plugins: MarkdownPlugin[] = []
    const requestOptions = { ...options, plugins }
    store.read.mockReturnValue(pending.promise)
    const operation = prepareMarkdownRecovery(requestOptions)
    Object.assign(requestOptions.scope, { accountId: 'mutated' })
    Object.assign(requestOptions.schema, { profile: 'markdown-safe', version: 1 })
    Object.assign(requestOptions, { revision: 9, branchId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', fragment: 'mutated' })
    plugins.push(pluginProbe().plugin)
    pending.resolve(checkpoint())
    const result = await operation
    expect(result.source).toMatchObject({ scope, branchId, revision: 4 })
    expect(result.schema).toEqual(richSchema)
    expect(result.fragment).toBe('default')
  })
})

describe('preview cancellation and ownership', () => {
  it('captures the store result before a later observer of that same fulfilled promise mutates it', async () => {
    const value = checkpoint(), response = Promise.resolve(value), { store, options } = fixture(value)
    store.read.mockReturnValue(response)
    const operation = prepareMarkdownRecovery(options)
    // Registered after preview's read handler, but before its outer await continuation.
    const mutate = response.then(() => Object.assign(value, { yjsState: encode([paragraph('Changed after store completion')]) }))
    const result = await operation
    await mutate
    expect(result.projection.text).toBe('Saved locally')
    expect(result.source).toMatchObject({ scope, branchId, revision: 4 })
  })

  it('rejects an already aborted request without reading storage', async () => {
    const { store, options } = fixture(), controller = new AbortController()
    controller.abort()
    await expect(prepareMarkdownRecovery({ ...options, signal: controller.signal })).rejects.toMatchObject({ code: 'aborted' })
    expect(store.read).not.toHaveBeenCalled()
  })

  it.each(['resolve', 'reject'] as const)('promptly aborts a hanging read and ignores its late %s', async outcome => {
    const pending = deferred<RecoveryCheckpoint | null>(), { store, options } = fixture()
    const controller = new AbortController(), plugin = pluginProbe()
    store.read.mockReturnValue(pending.promise)
    const operation = prepareMarkdownRecovery({ ...options, signal: controller.signal, plugins: [plugin.plugin] })
    controller.abort()
    await expect(operation).rejects.toMatchObject({ code: 'aborted' })
    if (outcome === 'resolve') pending.resolve(checkpoint())
    else pending.reject(new Error('late failure'))
    await Promise.resolve(); await Promise.resolve()
    expect(plugin.create).not.toHaveBeenCalled()
    expect(store.write).not.toHaveBeenCalled()
  })

  it('owns checkpoint data before invoking plugin code supplied by the host', async () => {
    const probe = pluginProbe(), value = checkpoint([{ type: 'recoveryCard', attrs: { label: 'Owned card' } }], probe.schema)
    const bytes = value.yjsState.slice(), create = probe.plugin.create
    const plugin = { ...probe.plugin, create: (context: Parameters<MarkdownPlugin['create']>[0]) => {
      value.yjsState.fill(0); Object.assign(value.scope, { accountId: 'changed' }); return create(context)
    } }
    const { options } = fixture(value)
    const result = await prepareMarkdownRecovery({ ...options, schema: probe.schema, plugins: [plugin] })
    expect(result.projection.text).toBe('Owned card')
    expect(result.source.scope.accountId).toBe('account')
    expect(bytes.some(byte => byte !== 0)).toBe(true)
  })

  it('does not mutate caller bytes and deeply freezes returned value, projection and schema', async () => {
    const value = checkpoint(), original = value.yjsState.slice(), { options } = fixture(value)
    const result = await prepareMarkdownRecovery(options)
    expect(value.yjsState).toEqual(original)
    function assertFrozen(item: unknown): void {
      if (!item || typeof item !== 'object') return
      expect(Object.isFrozen(item)).toBe(true)
      Object.values(item).forEach(assertFrozen)
    }
    assertFrozen(result)
    expect(() => Object.assign(result.source.scope, { accountId: 'wrong' })).toThrow()
    expect(value.scope.accountId).toBe('account')
  })

  it('rejects pending data missing from the full state instead of merging it into a preview', async () => {
    const value = checkpoint()
    const withPending = { ...value, pendingUpdates: [{ id: 'pending-only', update: encode([paragraph('Must not merge')]), updateCount: 1, attempted: true }] }
    const { options, store } = fixture(withPending)
    await expect(prepareMarkdownRecovery(options)).rejects.toMatchObject({ code: 'content' })
    expect(store.write).not.toHaveBeenCalled()
  })
})

describe('preview document fidelity and exact schema', () => {
  it('preserves Unicode, bold and underline while reporting markdown lossiness', async () => {
    const content: JsonNode[] = [{ type: 'paragraph', content: [{ type: 'text', text: 'Žluťoučký 👩🏽‍💻 café', marks: [{ type: 'bold' }, { type: 'underline' }] }] }]
    const result = await prepareMarkdownRecovery(fixture(checkpoint(content)).options)
    expect(result.projection.text).toBe('Žluťoučký 👩🏽‍💻 café')
    expect(result.projection.markdown).toContain('Žluťoučký 👩🏽‍💻 café')
    expect(result.projection.markdownLossy).toBe(true)
    const rich = result.value as MarkdownRichValue
    expect(rich.document.content).toEqual(expect.arrayContaining([expect.objectContaining({ content: [expect.objectContaining({
      text: 'Žluťoučký 👩🏽‍💻 café', marks: expect.arrayContaining([{ type: 'bold' }, { type: 'underline' }]),
    })] })]))
  })

  it('keeps merged rich table cells and their lossy projection', async () => {
    const content: JsonNode[] = [{ type: 'table', content: [
      { type: 'tableRow', content: [{ type: 'tableHeader', attrs: { colspan: 2 }, content: [paragraph('Merged title')] }] },
      { type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('Left')] }, { type: 'tableCell', content: [paragraph('Right')] }] },
    ] }]
    const result = await prepareMarkdownRecovery(fixture(checkpoint(content)).options)
    expect(result.projection.markdownLossy).toBe(true)
    expect(result.projection.text).toContain('Merged title')
    expect(JSON.stringify(result.value)).toContain('"colspan":2')
    expect(result.projection.markdown).toContain('Left')
    expect(result.projection.markdown).toContain('Right')
  })

  it('returns the markdown-safe string under exactly the version 1 schema', async () => {
    const { options } = fixture(checkpoint([paragraph('Safe **literal** český text')], safeSchema))
    const result = await prepareMarkdownRecovery({ ...options, schema: safeSchema })
    expect(typeof result.value).toBe('string')
    expect(result.value).toBe(result.projection.markdown)
    expect(result.projection.text).toBe('Safe **literal** český text')
    expect(result.projection.markdownLossy).toBe(false)
    expect(result.schema).toEqual(safeSchema)
  })

  it.each(([
    [{ type: 'unknownNode' }],
    [{ type: 'paragraph', attrs: { surprise: 'do not discard' }, content: [{ type: 'text', text: 'Preserve' }] }],
    [{ type: 'paragraph', attrs: { textAlign: 'invalid' }, content: [{ type: 'text', text: 'Preserve' }] }],
    [{ type: 'paragraph', content: [{ type: 'text', text: 'Unknown mark', marks: [{ type: 'unknownMark' }] }] }],
  ] as JsonNode[][]).map(content => [content] as const))('rejects unknown or invalid document data instead of normalizing it away (%j)', async content => {
    await expect(prepareMarkdownRecovery(fixture(checkpoint(content)).options)).rejects.toMatchObject({ code: 'content' })
  })

  it('rejects rich-only underline under markdown-safe instead of losing it', async () => {
    const value = checkpoint([{ type: 'paragraph', content: [{ type: 'text', text: 'Marked', marks: [{ type: 'underline' }] }] }], safeSchema)
    await expect(prepareMarkdownRecovery({ ...fixture(value).options, schema: safeSchema })).rejects.toMatchObject({ code: 'content' })
  })

  it.each([{ version: 1, profile: 'rich', plugins: [] }, { version: 2, profile: 'markdown-safe', plugins: [] }] as MarkdownDocumentSchema[])('rejects mutually matching but unsupported runtime schema %j', async schema => {
    await expect(prepareMarkdownRecovery({ ...fixture(checkpoint([paragraph('Text')], schema)).options, schema })).rejects.toMatchObject({ code: 'schema' })
  })
})

describe('preview plugin isolation and cleanup', () => {
  it('rejects schema attribute loss during editor construction without normalizing the expected document', async () => {
    let constructing = false
    const plugin: MarkdownPlugin = { id: 'test.dynamic-attributes', apiVersion: 1, schemaVersion: 1, profiles: ['rich'],
      create: () => ({ markdownLossy: () => true, extensions: [Node.create({
        name: 'dynamicRecoveryCard', group: 'block', atom: true,
        addAttributes: () => constructing ? { replacement: { default: 'Replacement' } } : { label: { default: 'Original' } },
        renderHTML: () => ['div'], renderText: () => 'Card',
        addStorage: () => ({ markdown: { serialize: (state: { write(value: string): void }) => state.write('Card') } }),
      })] }) }
    const schema: MarkdownDocumentSchema = { ...richSchema, plugins: [{ id: plugin.id, version: 1 }] }
    const value = checkpoint([{ type: 'dynamicRecoveryCard', attrs: { label: 'Must not disappear' } }], schema)
    const create = editorFactory.createTiptapEditor
    vi.spyOn(editorFactory, 'createTiptapEditor').mockImplementationOnce(options => {
      constructing = true
      return create(options) // Real Editor builds a second schema from the dynamic extension.
    })
    await expect(prepareMarkdownRecovery({ ...fixture(value).options, schema, plugins: [plugin] })).rejects.toMatchObject({
      code: 'content', cause: expect.objectContaining({ message: 'Recovery engine initialization changed the selected content.' }),
    })
  })

  it('aborts the registry lifetime when a Tiptap constructor hook throws before returning its editor', async () => {
    const probe = pluginProbe(), value = checkpoint([{ type: 'recoveryCard' }], probe.schema), before = value.yjsState.slice()
    const plugin = { ...probe.plugin, create: (context: Parameters<MarkdownPlugin['create']>[0]) => {
      const instance = probe.plugin.create(context)
      return { ...instance, extensions: instance.extensions!.map(extension => extension.extend({
        onBeforeCreate() { throw new Error('Extension constructor failed') },
      })) }
    } }
    await expect(prepareMarkdownRecovery({ ...fixture(value).options, schema: probe.schema, plugins: [plugin] })).rejects.toMatchObject({
      code: 'content', cause: expect.objectContaining({ message: 'Extension constructor failed' }),
    })
    expect(probe.abort).toHaveBeenCalledOnce()
    expect(probe.signals.every(signal => signal.aborted)).toBe(true)
    expect(value.yjsState).toEqual(before)
    expect(probe.onCreate).not.toHaveBeenCalled(); expect(probe.onAttach).not.toHaveBeenCalled(); expect(probe.onRender).not.toHaveBeenCalled()
  })

  it('uses the exact content plugin version, never migrates and aborts its lifetime after success', async () => {
    const probe = pluginProbe(), value = checkpoint([{ type: 'recoveryCard', attrs: { label: 'Plugin card' } }], probe.schema)
    const result = await prepareMarkdownRecovery({ ...fixture(value).options, schema: probe.schema, plugins: [probe.plugin] })
    expect(result.projection.text).toBe('Plugin card')
    expect(probe.create).toHaveBeenCalledOnce()
    expect(probe.migrate).not.toHaveBeenCalled()
    expect(probe.onCreate).not.toHaveBeenCalled(); expect(probe.onAttach).not.toHaveBeenCalled(); expect(probe.onRender).not.toHaveBeenCalled()
    expect(probe.abort).toHaveBeenCalledOnce(); expect(probe.signals.every(signal => signal.aborted)).toBe(true)
  })

  it.each(['missing', 'older', 'newer', 'extra'] as const)('rejects %s runtime content plugins without migration', async kind => {
    const probe = pluginProbe()
    const schema = kind === 'extra' ? richSchema : kind === 'newer'
      ? { ...probe.schema, plugins: [{ id: probe.plugin.id, version: 1 }] } : probe.schema
    const plugins = kind === 'missing' ? [] : [{ ...probe.plugin, ...(kind === 'older' ? { schemaVersion: 1, migrations: [] } : {}) }]
    await expect(prepareMarkdownRecovery({ ...fixture(checkpoint([paragraph('Text')], schema)).options, schema, plugins })).rejects.toMatchObject({ code: 'schema' })
    expect(probe.migrate).not.toHaveBeenCalled()
    expect(probe.signals.every(signal => signal.aborted)).toBe(true)
  })

  it('aborts initialized plugin resources after invalid content', async () => {
    const probe = pluginProbe()
    await expect(prepareMarkdownRecovery({ ...fixture(checkpoint([{ type: 'unknownNode' }], probe.schema)).options,
      schema: probe.schema, plugins: [probe.plugin] })).rejects.toMatchObject({ code: 'content' })
    expect(probe.abort).toHaveBeenCalledOnce()
    expect(probe.onCreate).not.toHaveBeenCalled(); expect(probe.onAttach).not.toHaveBeenCalled(); expect(probe.onRender).not.toHaveBeenCalled()
  })

  it('observes cancellation triggered during plugin initialization and cleans up', async () => {
    const controller = new AbortController(), probe = pluginProbe(), original = probe.plugin.create
    const plugin = { ...probe.plugin, create: (context: Parameters<MarkdownPlugin['create']>[0]) => {
      const instance = original(context); controller.abort(); return instance
    } }
    await expect(prepareMarkdownRecovery({ ...fixture(checkpoint([paragraph('Text')], probe.schema)).options,
      schema: probe.schema, plugins: [plugin], signal: controller.signal })).rejects.toMatchObject({ code: 'aborted' })
    expect(probe.abort).toHaveBeenCalledOnce()
  })

  it.each(['throw', 'abort'] as const)('cleans up when a projection serializer causes %s', async kind => {
    const controller = new AbortController(), probe = pluginProbe()
    const plugin = { ...probe.plugin, create: (context: Parameters<MarkdownPlugin['create']>[0]) => {
      const instance = probe.plugin.create(context)
      return { ...instance, markdownLossy: () => {
        if (kind === 'throw') throw new Error('projection failed')
        controller.abort(); return true
      } }
    } }
    await expect(prepareMarkdownRecovery({ ...fixture(checkpoint([{ type: 'recoveryCard' }], probe.schema)).options,
      schema: probe.schema, plugins: [plugin], signal: controller.signal })).rejects.toMatchObject({ code: kind === 'throw' ? 'content' : 'aborted' })
    expect(probe.abort).toHaveBeenCalledOnce()
  })
})

describe('preview resource and configuration limits', () => {
  it.each(['maxCheckpointBytes', 'maxNodes', 'maxDepth'] as const)('rejects invalid %s before store access', async field => {
    for (const value of [0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER]) {
      const { options, store } = fixture()
      await expect(prepareMarkdownRecovery({ ...options, [field]: value })).rejects.toMatchObject({ code: 'limit' })
      expect(store.read).not.toHaveBeenCalled()
    }
  })

  it.each([{ maxCheckpointBytes: 1 }, { maxNodes: 1 }, { maxDepth: 1 }])('reports actual input limit exhaustion %j', async limits => {
    await expect(prepareMarkdownRecovery({ ...fixture().options, ...limits })).rejects.toMatchObject({ code: 'limit' })
  })

  it.each([{ branchId: '' }, { branchId: 'not-a-uuid' }, { revision: 0 }, { revision: 1.5 }, { revision: NaN }])('rejects invalid selection %j before store access', async invalid => {
    const { options, store } = fixture()
    await expect(prepareMarkdownRecovery({ ...options, ...invalid })).rejects.toMatchObject({ code: 'stale' })
    expect(store.read).not.toHaveBeenCalled()
  })

  it('reports nonstring runtime branch metadata as stale rather than an untyped exception', async () => {
    const { options, store } = fixture()
    await expect(prepareMarkdownRecovery({ ...options, branchId: 42 } as unknown as Options)).rejects.toMatchObject({ code: 'stale' })
    expect(store.read).not.toHaveBeenCalled()
  })

  it('rejects invalid host scope before store access', async () => {
    const { options, store } = fixture()
    await expect(prepareMarkdownRecovery({ ...options, scope: { ...scope, accountId: '' } })).rejects.toMatchObject({ code: 'scope' })
    expect(store.read).not.toHaveBeenCalled()
  })
})
