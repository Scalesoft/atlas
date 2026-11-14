// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import * as authority from './authority'
import { builtinAuthorityPlugins } from './testPlugins'
import { b64ToU8, u8ToB64 } from '../collab/provider/protocol'

const processAuthorityRequest = (request: authority.AuthorityRequest) => authority.processAuthorityRequest(request, builtinAuthorityPlugins)
const schema = { version: 7 as const, profile: 'rich' as const, plugins: [] }
const run = (snapshot: string, plugins = schema.plugins as Array<{ id: string; version: number }>) => processAuthorityRequest({ schema: { ...schema, plugins }, fragment: 'default', snapshot })
function snapshot(type: string, attrs: Record<string, unknown> = {}) {
  const doc = new Y.Doc(), node = new Y.XmlElement(type)
  for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value as string)
  doc.getXmlFragment('default').insert(0, [node]); const result = u8ToB64(Y.encodeStateAsUpdate(doc)); doc.destroy(); return result
}
describe('isolated authority reducer', () => {
  it('retains supported structured math without requiring its browser renderer', () => {
    const state = snapshot('mathBlock', { data: { version: 1, expression: 'E = mc^2' } })
    const result = run(state, [{ id: 'math', version: 1 }])
    expect(result.plainText).toContain('E = mc^2'); expect(JSON.parse(result.editorJson).document.content[0].type).toBe('mathBlock')
    expect(run(result.snapshot, [{ id: 'math', version: 1 }])).toEqual(result)
  })
  it('preserves safe diagram source and schema version', () => {
    const source = 'graph LR\n A-->B', result = run(snapshot('diagram', { data: { version: 1, source, caption: 'Diagram' } }), [{ id: 'diagrams', version: 2 }])
    expect(result.plainText).toContain(source)
  })
  it.each(['unknown', 'mathBlock', 'script'])('rejects unsupported %s nodes without repairing the source', type => {
    const encoded = snapshot(type); expect(() => run(encoded)).toThrow()
    const source = new Y.Doc(); Y.applyUpdate(source, b64ToU8(encoded)); expect((source.getXmlFragment('default').get(0) as Y.XmlElement).nodeName).toBe(type); source.destroy()
  })
  it('rejects unknown attributes before ProseMirror can discard them', () => expect(() => run(snapshot('paragraph', { hiddenPayload: 'x' }))).toThrow())
  it.each(['<script>alert(1)</script>', '%%{init: {}}%%\ngraph LR\n A-->B', '---\nconfig: x'])('rejects active diagram source %s', source => {
    expect(() => run(snapshot('diagram', { data: { version: 1, source, caption: '' } }), [{ id: 'diagrams', version: 2 }])).toThrow()
  })
  it('rejects an additional nonempty shared root', () => {
    const base = processAuthorityRequest({ schema, fragment: 'default', seedMarkdown: 'Good' })
    const doc = new Y.Doc(); Y.applyUpdate(doc, b64ToU8(base.snapshot)); doc.getMap('hidden').set('value', 'x')
    expect(() => run(u8ToB64(Y.encodeStateAsUpdate(doc)))).toThrow(); doc.destroy()
  })
  it('rejects an update whose dependencies are missing', () => {
    const doc = new Y.Doc(), paragraph = new Y.XmlElement('paragraph'), text = new Y.XmlText()
    paragraph.insert(0, [text]); doc.getXmlFragment('default').insert(0, [paragraph]); const vector = Y.encodeStateVector(doc)
    text.insert(0, 'Missing parent'); const update = u8ToB64(Y.encodeStateAsUpdate(doc, vector)); doc.destroy()
    expect(() => processAuthorityRequest({ schema, fragment: 'default', updates: [update] })).toThrow()
  })
  it('applies genuine deletion updates and retains their replay effect', () => {
    const base = processAuthorityRequest({ schema, fragment: 'default', seedMarkdown: 'ABCDE' })
    const doc = new Y.Doc(); Y.applyUpdate(doc, b64ToU8(base.snapshot)); const vector = Y.encodeStateVector(doc)
    const text = (doc.getXmlFragment('default').get(0) as Y.XmlElement).get(0) as Y.XmlText; text.delete(1, 3)
    const result = processAuthorityRequest({ schema, fragment: 'default', snapshot: base.snapshot, updates: [u8ToB64(Y.encodeStateAsUpdate(doc, vector))] })
    expect(result.plainText).toBe('AE'); expect(run(result.snapshot).plainText).toBe('AE'); doc.destroy()
  })
  it('does not share content between consecutive requests', () => {
    const first = processAuthorityRequest({ schema, fragment: 'default', seedMarkdown: 'SECRET A' })
    const second = processAuthorityRequest({ schema, fragment: 'default', seedMarkdown: 'PUBLIC B' })
    expect(second.editorJson).not.toContain('SECRET'); expect(run(first.snapshot).plainText).toBe('SECRET A')
  })
})
