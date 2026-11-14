import { describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { normalizeJoin, normalizeSeed, ProtocolError, validateServerSequence } from './protocol'

describe('server sequence boundary', () => {
  it.each([1, 2, Number.MAX_SAFE_INTEGER])('accepts positive safe sequence %s', sequence => {
    expect(validateServerSequence(sequence)).toBe(sequence)
    expect(validateServerSequence(sequence, true)).toBe(sequence)
  })

  it('allows zero only at explicit baseline boundaries', () => {
    expect(validateServerSequence(0, true)).toBe(0)
    expect(() => validateServerSequence(0)).toThrow(ProtocolError)
  })

  it.each([undefined, null, true, false, '', '1', '0', -1, 1.5, Number.NaN, Infinity, -Infinity,
    Number.MAX_SAFE_INTEGER + 1, 1n, {}, [], new Number(1)])('rejects a non-sequence: %s', value => {
    expect(() => validateServerSequence(value)).toThrow(ProtocolError)
    expect(() => validateServerSequence(value, true)).toThrow(ProtocolError)
  })
})

describe('join response metadata boundary', () => {
  it.each(['camel', 'pascal'])('accepts complete %s casing without changing values', casing => {
    const values = { snapshot: 'AQI=', updates: ['AwQ=', 'BQY='], seq: 12, seedMarkdown: '', seedEditorJson: null,
      isSnapshotter: false, canWrite: true, schema: { version: 1, profile: 'rich', plugins: [] }, snapshotEncoding: 'gzip',
      seed: 'H4s=', projectionsDerived: true }
    const wire = Object.fromEntries(Object.entries(values).map(([key, value]) =>
      [casing === 'pascal' ? key[0]!.toUpperCase() + key.slice(1) : key, value]))
    expect(normalizeJoin(wire)).toEqual(values)
  })

  it('defaults only omitted optional fields and remains read-only without an explicit legacy role', () => {
    expect(normalizeJoin({ seq: 0 })).toEqual({ snapshot: null, updates: [], seq: 0,
      seedMarkdown: null, seedEditorJson: null, isSnapshotter: false, canWrite: false, schema: null, snapshotEncoding: null,
      seed: null, projectionsDerived: false })
    expect(normalizeJoin({ Seq: 0, IsSnapshotter: true }).canWrite).toBe(true)
    expect(normalizeJoin({ Seq: 0, IsSnapshotter: true, CanWrite: false }).canWrite).toBe(false)
  })

  it('validates the inflated compressed seed like the plain one', () => {
    expect(normalizeSeed('{"markdown":"# A","editorJson":{"type":"doc","content":[]}}'))
      .toEqual({ seedMarkdown: '# A', seedEditorJson: { type: 'doc', content: [] } })
    expect(normalizeSeed('{"markdown":"","editorJson":null}')).toEqual({ seedMarkdown: '', seedEditorJson: null })
    expect(() => normalizeSeed('{"editorJson":{"type":"paragraph"}}')).toThrow(/document object/)
    expect(() => normalizeSeed('[]')).toThrow(/object/)
    expect(() => normalizeSeed('{')).toThrow(/JSON/)
  })

  it('retains explicit nullable fields and empty strings', () => {
    expect(normalizeJoin({ seq: 0, snapshot: '', seedMarkdown: '', schema: null })).toMatchObject({ snapshot: '', seedMarkdown: '', schema: null })
    expect(normalizeJoin({ seq: 0, snapshot: null, seedMarkdown: null })).toMatchObject({ snapshot: null, seedMarkdown: null })
  })

  it('accepts equivalent duplicate aliases including reordered schema keys', () => {
    const result = normalizeJoin({ seq: 1, Seq: 1, snapshot: null, Snapshot: null,
      updates: ['AA=='], Updates: ['AA=='], seedMarkdown: '', SeedMarkdown: '',
      isSnapshotter: false, IsSnapshotter: false, canWrite: true, CanWrite: true,
      schema: { version: 1, profile: 'rich', plugins: [] }, Schema: { plugins: [], profile: 'rich', version: 1 } })
    expect(result).toMatchObject({ seq: 1, updates: ['AA=='], canWrite: true })
  })

  it.each([
    ['seq', 'Seq', 1, 2], ['snapshot', 'Snapshot', null, 'AA=='],
    ['updates', 'Updates', ['AA=='], ['AQ==']], ['seedMarkdown', 'SeedMarkdown', '', '# Seed'],
    ['isSnapshotter', 'IsSnapshotter', false, true], ['canWrite', 'CanWrite', false, true],
    ['schema', 'Schema', { version: 1 }, { version: 2 }],
    ['seedEditorJson', 'SeedEditorJson', { type: 'doc' }, null], ['snapshotEncoding', 'SnapshotEncoding', null, 'gzip'],
  ])('rejects conflicting %s/%s aliases', (camel, pascal, left, right) => {
    expect(() => normalizeJoin({ seq: 0, [camel as string]: left, [pascal as string]: right })).toThrow(/conflicting/)
  })

  it.each([undefined, null, false, true, 0, 'reply', [], new Date(), new Map()])('rejects a non-object response: %s', value => {
    expect(() => normalizeJoin(value)).toThrow(ProtocolError)
  })

  it.each([{}, { seq: undefined }, { Seq: null }, { sequence: 0 }])('rejects a missing authoritative head: %j', value => {
    expect(() => normalizeJoin(value)).toThrow(ProtocolError)
  })

  it.each([-1, 0.5, '0', false, Number.NaN, Infinity, Number.MAX_SAFE_INTEGER + 1])('rejects invalid join head %s in both casings', seq => {
    expect(() => normalizeJoin({ seq })).toThrow(ProtocolError)
    expect(() => normalizeJoin({ Seq: seq })).toThrow(ProtocolError)
  })

  it.each([
    ['snapshot', 0], ['snapshot', false], ['snapshot', {}], ['snapshot', []], ['snapshot', undefined],
    ['seedMarkdown', 0], ['seedMarkdown', false], ['seedMarkdown', {}], ['seedMarkdown', undefined],
    ['updates', null], ['updates', undefined], ['updates', 'AA=='], ['updates', {}],
    ['updates', [null]], ['updates', [1]], ['updates', ['AA==', false]], ['updates', new Array(1)],
    ['isSnapshotter', null], ['isSnapshotter', undefined], ['isSnapshotter', 'true'], ['isSnapshotter', 1],
    ['canWrite', null], ['canWrite', undefined], ['canWrite', 'false'], ['canWrite', 0],
    ['schema', false], ['schema', 'rich'], ['schema', []], ['schema', undefined],
    ['seedEditorJson', false], ['seedEditorJson', 'text'], ['seedEditorJson', []],
    ['seedEditorJson', undefined], ['seedEditorJson', { type: 'paragraph' }],
    ['snapshotEncoding', 'br'], ['snapshotEncoding', 1],
  ])('rejects malformed %s metadata (%j) in both casings', (field, value) => {
    const key = field as string, pascal = key[0]!.toUpperCase() + key.slice(1)
    expect(() => normalizeJoin({ seq: 0, [key]: value })).toThrow(ProtocolError)
    expect(() => normalizeJoin({ seq: 0, [pascal]: value })).toThrow(ProtocolError)
  })

  it('returns detached immutable update and schema data', () => {
    const updates = ['AA=='], schema = { version: 1, profile: 'rich', plugins: [{ id: 'charts', version: 1 }] }
    const result = normalizeJoin({ seq: 0, updates, schema })
    updates[0] = 'changed'; schema.plugins[0]!.version = 99
    expect(result.updates).toEqual(['AA=='])
    expect(result.schema).toEqual({ version: 1, profile: 'rich', plugins: [{ id: 'charts', version: 1 }] })
    expect(() => (result.updates as string[]).push('changed')).toThrow(TypeError)
    expect(() => ((result.schema as typeof schema).plugins[0]!.version = 77)).toThrow(TypeError)
  })

  it('permits JSON extension fields and delegates document schema compatibility', () => {
    const result = normalizeJoin(Object.assign(Object.create(null), { seq: 0, schema: {}, futureServerField: { enabled: true } }))
    expect(result.seq).toBe(0)
    expect(result.schema).toEqual({})
    expect(result).not.toHaveProperty('futureServerField')
  })

  it('rejects a cyclic payload without attempting to normalize its metadata', () => {
    const value: Record<string, unknown> = { seq: 0 }; value.schema = value
    expect(() => normalizeJoin(value)).toThrow(ProtocolError)
  })

  it('does not evaluate an accessor that claims write permission', () => {
    const getter = vi.fn(() => true)
    const value = Object.defineProperty({ seq: 0 }, 'canWrite', { get: getter, enumerable: true })
    expect(() => normalizeJoin(value)).toThrow(ProtocolError)
    expect(getter).not.toHaveBeenCalled()
  })

  it('does not evaluate accessors inside an update array', () => {
    const getter = vi.fn(() => 'AA=='), updates: string[] = []
    Object.defineProperty(updates, '0', { get: getter, enumerable: true })
    expect(() => normalizeJoin({ seq: 0, updates })).toThrow(ProtocolError)
    expect(getter).not.toHaveBeenCalled()
  })

  it('returns a rich seed whose object attributes Yjs can store', () => {
    // Regression: a null-prototype attribute made the seeding snapshotter throw on every join,
    // so a page with a diagram, math or OpenAPI block could never be opened for editing.
    const data = { version: 1, source: 'flowchart LR\n  A --> B', caption: 'Flow' }
    const seed = normalizeJoin({ seq: 0, seedEditorJson: { type: 'doc', content: [{ type: 'diagram', attrs: { data } }] } })
      .seedEditorJson!
    const attribute = seed.content![0]!.attrs!.data as typeof data
    expect(Object.getPrototypeOf(attribute)).toBe(Object.prototype)
    expect(Object.isFrozen(attribute)).toBe(true)
    const element = new Y.XmlElement('diagram')
    new Y.Doc().getXmlFragment('document').insert(0, [element])
    element.setAttribute('data', attribute as never)
    expect(element.getAttribute('data')).toEqual(data)
  })

  it('keeps a __proto__ field of the rich seed as data', () => {
    const seed = normalizeJoin(JSON.parse('{"seq":0,"seedEditorJson":{"type":"doc","attrs":{"__proto__":{"polluted":true}}}}'))
      .seedEditorJson!
    expect(Object.hasOwn(seed.attrs!, '__proto__')).toBe(true)
    expect(Object.getPrototypeOf(seed.attrs!)).toBe(Object.prototype)
    expect(({} as Record<string, unknown>).polluted).toBeUndefined()
  })
})
