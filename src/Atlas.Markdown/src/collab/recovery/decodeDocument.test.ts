import * as Y from 'yjs'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { decodeRecoveryDocument } from './decodeDocument'
import type { RecoveryCheckpoint } from './types'

const documents: Y.Doc[] = []
const limits = { maxNodes: 1000, maxDepth: 64 }
function doc(gc = false) { const value = new Y.Doc({ gc }); documents.push(value); return value }
function content(text = 'Hello světe 🌍', nodeName = 'paragraph') {
  const value = doc(), paragraph = new Y.XmlElement(nodeName), xmlText = new Y.XmlText()
  paragraph.insert(0, [xmlText]); xmlText.insert(0, text)
  value.getXmlFragment('body').insert(0, [paragraph])
  return { value, paragraph, xmlText }
}
function checkpoint(value: Y.Doc | Uint8Array, pending: Uint8Array[] = []): RecoveryCheckpoint {
  return {
    formatVersion: 1, scope: { serverId: 'server', tenantId: 'tenant', accountId: 'account', documentId: 'doc' },
    branchId: '00000000-0000-4000-8000-000000000001', revision: 1, capturedAt: 0,
    schema: { version: 2, profile: 'rich', plugins: [] }, fragment: 'body',
    observedSequence: 0, documentRevision: 1, connectionGeneration: 0, epoch: null, replayPolicy: 'manual-only',
    yjsState: value instanceof Y.Doc ? Y.encodeStateAsUpdate(value) : value,
    pendingUpdates: pending.map((update, index) => ({ id: String(index), update, updateCount: 1, attempted: false })),
  }
}
function change(value: Y.Doc, run: () => void) {
  const vector = Y.encodeStateVector(value); run(); return Y.encodeStateAsUpdate(value, vector)
}
afterEach(() => { vi.restoreAllMocks(); for (const value of documents.splice(0)) value.destroy() })

describe('isolated strict recovery document decoding', () => {
  it('preserves unicode text and leaves source bytes and live Y.Doc untouched', () => {
    const { value } = content(), saved = checkpoint(value), before = saved.yjsState.slice()
    const update = vi.fn(); value.on('update', update)
    expect(decodeRecoveryDocument(saved, limits)).toEqual({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Hello světe 🌍' }] }] })
    expect(saved.yjsState).toEqual(before); expect(Y.encodeStateAsUpdate(value)).toEqual(before); expect(update).not.toHaveBeenCalled()
  })
  it('accepts a real empty editor paragraph', () => {
    const value = doc(); value.getXmlFragment('body').insert(0, [new Y.XmlElement('paragraph')])
    expect(decodeRecoveryDocument(checkpoint(value), limits)).toEqual({ type: 'doc', content: [{ type: 'paragraph' }] })
  })
  it('preserves unknown nested nodes, marks and JSON attributes for subsequent schema validation', () => {
    const { value, paragraph, xmlText } = content('plugin', 'unknownBlock')
    paragraph.setAttribute('futureAttribute', 'keep me')
    xmlText.format(0, 6, { unknownMark: { future: [true, 5, null] } })
    const nested = new Y.XmlElement('unknownChild'); paragraph.insert(1, [nested])
    expect(decodeRecoveryDocument(checkpoint(value), limits)).toEqual({ type: 'doc', content: [{ type: 'unknownBlock', attrs: { futureAttribute: 'keep me' }, content: [
      { type: 'text', text: 'plugin', marks: [{ type: 'unknownMark', attrs: { future: [true, 5, null] } }] }, { type: 'unknownChild' },
    ] }] })
    expect(value.getXmlFragment('body').length).toBe(1)
  })
  it('preserves invalid table geometry rather than repairing it', () => {
    const value = doc(); value.getXmlFragment('body').insert(0, [new Y.XmlElement('table')])
    expect(decodeRecoveryDocument(checkpoint(value), limits)).toEqual({ type: 'doc', content: [{ type: 'table' }] })
  })
  it('rejects a missing selected fragment without creating a substitute', () => {
    const value = content().value, saved = checkpoint(value)
    expect(() => decodeRecoveryDocument({ ...saved, fragment: 'wrong' }, limits)).toThrow(/missing.*fragment/)
    expect(value.share.has('wrong')).toBe(false)
  })
  it('rejects an empty binary state with no recorded root', () => {
    expect(() => decodeRecoveryDocument(checkpoint(Uint8Array.of(0, 0)), limits)).toThrow(/missing.*fragment/)
  })
  it.each(['text', 'map', 'array', 'xml-text', 'xml-element-attrs'] as const)('rejects %s masquerading as the document root', kind => {
    const value = doc()
    if (kind === 'text') value.getText('body').insert(0, 'not XML')
    if (kind === 'map') value.getMap('body').set('secret', 'not XML')
    if (kind === 'array') value.getArray('body').insert(0, [42])
    if (kind === 'xml-text') value.getXmlFragment('body').insert(0, [new Y.XmlText('root inline')])
    if (kind === 'xml-element-attrs') value.getXmlElement('body').setAttribute('rootAttribute', 'not fragment')
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(TypeError)
  })
  it('rejects root map metadata even when valid XML children are also present', () => {
    const { value } = content()
    const remote = doc(); Y.applyUpdate(remote, Y.encodeStateAsUpdate(value))
    remote.getMap('body').set('hidden', 'map metadata')
    expect(() => decodeRecoveryDocument(checkpoint(remote), limits)).toThrow(/map attributes/)
  })
  it.each(['map', 'text', 'xml'] as const)('rejects another nonempty %s root rather than silently omitting its data', kind => {
    const { value } = content()
    if (kind === 'map') value.getMap('other').set('metadata', true)
    if (kind === 'text') value.getText('other').insert(0, 'other data')
    if (kind === 'xml') value.getXmlFragment('other').insert(0, [new Y.XmlElement('paragraph')])
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(/another nonempty root/)
  })
  it('allows another root with only deleted historical content', () => {
    const { value } = content(); value.getText('other').insert(0, 'gone'); value.getText('other').delete(0, 4)
    expect(decodeRecoveryDocument(checkpoint(value), limits).content?.[0].type).toBe('paragraph')
  })
  it('rejects nested XML hooks', () => {
    const { value, paragraph } = content()
    // Deliberately encode a runtime-supported Yjs value outside the editor's typed XML API.
    Reflect.apply(paragraph.insert, paragraph, [1, [new Y.XmlHook('hook')]])
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(TypeError)
  })
  it('rejects text embeds instead of losing their content', () => {
    const { value, xmlText } = content(); xmlText.insertEmbed(0, { image: 'hidden' })
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(/embed/)
  })
  it('rejects text block attributes which the raw converter omits', () => {
    const { value, xmlText } = content(); xmlText.setAttribute('hidden', 'metadata')
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(/block attributes/)
  })
  it.each([undefined, Number.NaN, Infinity, Uint8Array.of(3)])('rejects non-JSON XML attributes (%s)', invalid => {
    const value = doc(), paragraph = new Y.XmlElement('paragraph')
    Reflect.apply(paragraph.setAttribute, paragraph, ['invalid', invalid])
    value.getXmlFragment('body').insert(0, [paragraph])
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(TypeError)
  })
  it.each([Uint8Array.of(255), new Uint8Array(), Uint8Array.of(1, 1)])('rejects malformed V1 bytes %j', bytes => {
    expect(() => decodeRecoveryDocument(checkpoint(bytes), limits)).toThrow(TypeError)
  })
  it('rejects pending malformed bytes even when the full document itself is valid', () => {
    expect(() => decodeRecoveryDocument(checkpoint(content().value, [Uint8Array.of(255)]), limits)).toThrow(TypeError)
  })
  it('rejects unresolved struct dependencies without applying pending updates to repair them', () => {
    const { value, xmlText } = content('first'), initial = Y.encodeStateAsUpdate(value)
    const later = change(value, () => xmlText.insert(5, ' second'))
    expect(() => decodeRecoveryDocument(checkpoint(later, [initial]), limits)).toThrow(TypeError)
    expect(() => decodeRecoveryDocument(checkpoint(later), limits)).toThrow(/unresolved/)
  })
  it('rejects delete-only state whose target structs have not arrived', () => {
    const { value, xmlText } = content('ab')
    const deletion = change(value, () => xmlText.delete(0, 1))
    expect(() => decodeRecoveryDocument(checkpoint(deletion), limits)).toThrow(/unresolved/)
  })
  it('rejects a pending insertion absent from the full state', () => {
    const { value, xmlText } = content('a'), baseline = Y.encodeStateAsUpdate(value)
    const insertion = change(value, () => xmlText.insert(1, 'b'))
    expect(() => decodeRecoveryDocument(checkpoint(baseline, [insertion]), limits)).toThrow(/pending update/)
  })
  it('rejects a pending deletion absent from the full state despite matching struct clocks', () => {
    const { value, xmlText } = content('ab'), baseline = Y.encodeStateAsUpdate(value)
    const deletion = change(value, () => xmlText.delete(0, 1))
    expect(() => decodeRecoveryDocument(checkpoint(baseline, [deletion]), limits)).toThrow(/pending update/)
  })
  it.each([false, true])('accepts historical deletion coverage with gc=%s', gc => {
    const { value, xmlText } = content('abc'), baseline = Y.encodeStateAsUpdate(value)
    const deletion = change(value, () => xmlText.delete(0, 1)), target = doc(gc)
    Y.applyUpdate(target, Y.encodeStateAsUpdate(value))
    expect(decodeRecoveryDocument(checkpoint(target, [baseline, deletion]), limits).content?.[0].content?.[0].text).toBe('bc')
  })
  it('allows covered same-ID payload conflicts without claiming authentication, and never applies them', () => {
    const first = content('cat'), second = doc(); second.clientID = first.value.clientID
    const paragraph = new Y.XmlElement('paragraph'), text = new Y.XmlText('dog'); paragraph.insert(0, [text]); second.getXmlFragment('body').insert(0, [paragraph])
    expect(decodeRecoveryDocument(checkpoint(first.value, [Y.encodeStateAsUpdate(second)]), limits).content?.[0].content?.[0].text).toBe('cat')
  })
  it('accepts additional contiguous GC records without treating them as missing content', () => {
    const saved = checkpoint(content().value), encoder = new Y.UpdateEncoderV1()
    encoder.writeLen(1); encoder.writeLen(1); encoder.writeClient(7); encoder.writeLen(0)
    new Y.GC(Y.createID(7, 0), 3).write(encoder, 0); encoder.writeLen(0)
    const gc = encoder.toUint8Array()
    expect(decodeRecoveryDocument({ ...saved, yjsState: Y.mergeUpdates([saved.yjsState, gc]) }, limits).content?.[0].type).toBe('paragraph')
  })
  it('rejects unsafe exclusive clock ranges before applying them', () => {
    const encoder = new Y.UpdateEncoderV1()
    encoder.writeLen(0); encoder.writeLen(1); encoder.writeClient(7); encoder.writeLen(1)
    encoder.writeDsClock(Number.MAX_SAFE_INTEGER); encoder.writeDsLen(1)
    expect(() => decodeRecoveryDocument(checkpoint(encoder.toUint8Array()), limits)).toThrow(TypeError)
  })
  it('bounds the exact root/element/text node count', () => {
    const saved = checkpoint(content().value)
    expect(() => decodeRecoveryDocument(saved, { ...limits, maxNodes: 2 })).toThrow(RangeError)
    expect(decodeRecoveryDocument(saved, { ...limits, maxNodes: 3 }).content).toHaveLength(1)
  })
  it('counts split formatting text runs in the node budget', () => {
    const { value, xmlText } = content('abc'); xmlText.format(1, 1, { bold: {} })
    expect(() => decodeRecoveryDocument(checkpoint(value), { ...limits, maxNodes: 3 })).toThrow(RangeError)
  })
  it('checks deep XML iteratively before entering the recursive converter', () => {
    const value = doc(), root = new Y.XmlElement('nest'); value.getXmlFragment('body').insert(0, [root])
    let current = root
    for (let index = 0; index < 150; index++) { const child = new Y.XmlElement('nest'); current.insert(0, [child]); current = child }
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(RangeError)
    expect(decodeRecoveryDocument(checkpoint(value), { maxNodes: 152, maxDepth: 151 }).content?.[0].type).toBe('nest')
  })
  it('bounds deeply nested formatting JSON before recursive conversion', () => {
    const { value, xmlText } = content(), nested: Record<string, unknown> = {}
    let current = nested
    for (let index = 0; index < 70; index++) { const child = {}; current.child = child; current = child }
    xmlText.format(0, 1, { custom: nested })
    expect(() => decodeRecoveryDocument(checkpoint(value), limits)).toThrow(RangeError)
  })
  it.each([{ maxNodes: 0, maxDepth: 1 }, { maxNodes: 1, maxDepth: 0 }, { maxNodes: Infinity, maxDepth: 1 }, { maxNodes: 4, maxDepth: 1.5 }])('rejects invalid limits %j', invalid => {
    expect(() => decodeRecoveryDocument(checkpoint(content().value), invalid)).toThrow(RangeError)
  })
  it.each([false, true])('destroys the isolated document on %s failure', failure => {
    const saved = checkpoint(content().value), destroy = vi.spyOn(Y.Doc.prototype, 'destroy')
    if (failure) expect(() => decodeRecoveryDocument({ ...saved, fragment: 'missing' }, limits)).toThrow()
    else decodeRecoveryDocument(saved, limits)
    expect(destroy).toHaveBeenCalledTimes(1)
  })
})
