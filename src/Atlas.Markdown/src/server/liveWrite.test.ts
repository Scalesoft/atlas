// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import * as authority from './authority'
import { builtinAuthorityPlugins } from './testPlugins'
import { b64ToU8, u8ToB64 } from '../collab/provider/protocol'

const processAuthorityRequest = (request: authority.AuthorityRequest) => authority.processAuthorityRequest(request, builtinAuthorityPlugins)
const processLiveWriteRequest = (request: authority.LiveWriteRequest) => authority.processLiveWriteRequest(request, builtinAuthorityPlugins)

// 3.6-H prototype: an AI write becomes a collaborator's update against the live document instead of a generation reset.
const schema = { version: 7 as const, profile: 'rich' as const, plugins: [] }
const fragment = 'default'
const seed = '# A\n\nAlpha text\n\n# B\n\nBeta text\n\n# C\n\nGamma text'

function live() {
  const base = processAuthorityRequest({ schema, fragment, seedMarkdown: seed })
  return { base, document: JSON.parse(base.editorJson).document }
}

/** A browser's replica that types `text` at `offset` of the paragraph at `index`, as its own update. */
function type(snapshot: string, index: number, offset: number, text: string) {
  const doc = new Y.Doc()
  Y.applyUpdate(doc, b64ToU8(snapshot))
  const before = Y.encodeStateVector(doc)
  const paragraph = doc.getXmlFragment(fragment).get(index) as Y.XmlElement
  ;(paragraph.get(0) as Y.XmlText).insert(offset, text)
  const update = u8ToB64(Y.encodeStateAsUpdate(doc, before))
  doc.destroy()
  return update
}

/** The document with the paragraph of section B (block 3) replaced, as the server's section merge would write it. */
function withSectionB(document: { content: Array<Record<string, unknown>> }, text: string) {
  const content = structuredClone(document.content)
  content[3] = { type: 'paragraph', content: [{ type: 'text', text }] }
  content.splice(4, 0, { type: 'paragraph', content: [{ type: 'text', text: 'Added by AI' }] })
  return { ...document, content }
}

describe('live write (3.6-H prototype)', () => {
  it('merges an AI section rewrite with a concurrent human edit of another section, in either order', () => {
    const { base, document } = live()
    const human = type(base.snapshot, 1, 'Alpha text'.length, ' typed by human')
    const ai = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target: withSectionB(document, 'Beta rewritten by AI') })
    expect(ai.update).toBeDefined()
    const humanFirst = processAuthorityRequest({ schema, fragment, snapshot: base.snapshot, updates: [human, ai.update!] })
    const aiFirst = processAuthorityRequest({ schema, fragment, snapshot: base.snapshot, updates: [ai.update!, human] })
    expect(humanFirst.markdown).toBe(aiFirst.markdown)
    expect(humanFirst.markdown).toContain('Alpha text typed by human')
    expect(humanFirst.markdown).toContain('Beta rewritten by AI')
    expect(humanFirst.markdown).toContain('Added by AI')
    expect(humanFirst.markdown).not.toContain('Beta text')
    expect(humanFirst.markdown).toContain('Gamma text')
  })

  it('writes against the live tail (snapshot + pending updates), not the last saved revision', () => {
    const { base } = live()
    const human = type(base.snapshot, 1, 0, 'Fresh ')
    const current = JSON.parse(processAuthorityRequest({ schema, fragment, snapshot: base.snapshot, updates: [human] }).editorJson).document
    const ai = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, updates: [human], target: withSectionB(current, 'AI') })
    const result = processAuthorityRequest({ schema, fragment, snapshot: base.snapshot, updates: [human, ai.update!] })
    expect(result.markdown).toContain('Fresh Alpha text')
    expect(result.markdown).toContain('AI')
  })

  it('touches only the changed block: the update is small and an unchanged target writes nothing', () => {
    const { base, document } = live()
    const same = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target: document })
    expect(Y.decodeUpdate(b64ToU8(same.update!)).structs).toHaveLength(0)
    const ai = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target: withSectionB(document, 'Beta rewritten by AI') })
    expect(b64ToU8(ai.update!).byteLength).toBeLessThan(b64ToU8(base.snapshot).byteLength)
  })

  it('keeps both texts when the human types into the very block the AI rewrites (no loss, character merge)', () => {
    const { base, document } = live()
    const human = type(base.snapshot, 3, 0, 'HUMAN ')
    const ai = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target: withSectionB(document, 'Beta rewritten by AI') })
    const result = processAuthorityRequest({ schema, fragment, snapshot: base.snapshot, updates: [human, ai.update!] })
    expect(result.plainText).toContain('HUMAN')
    expect(result.plainText).toContain('rewritten by AI')
  })

  it('refuses a target the schema does not accept, writing nothing', () => {
    const { base, document } = live()
    const target = { ...document, content: [...document.content, { type: 'script' }] }
    expect(() => processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target })).toThrow()
  })

  it('measures a section write on a long page (1000 paragraphs)', () => {
    const long = Array.from({ length: 1000 }, (_, i) => `${i % 50 === 0 ? `# Section ${i}\n\n` : ''}Paragraph ${i} with some words.`).join('\n\n')
    const base = processAuthorityRequest({ schema, fragment, seedMarkdown: long })
    const document = JSON.parse(base.editorJson).document
    const target = { ...document, content: document.content.map((block: Record<string, unknown>, i: number) =>
      i === 500 ? { type: 'paragraph', content: [{ type: 'text', text: 'Rewritten' }] } : block) }
    const times: number[] = []
    let update = ''
    for (let i = 0; i < 5; i++) {
      const start = performance.now()
      update = processLiveWriteRequest({ schema, fragment, snapshot: base.snapshot, target }).update!
      times.push(performance.now() - start)
    }
    times.sort((a, b) => a - b)
    console.info(`live write, 1000 paragraphs: median ${times[2].toFixed(1)} ms, max ${times[4].toFixed(1)} ms, update ${b64ToU8(update).byteLength} B, snapshot ${b64ToU8(base.snapshot).byteLength} B`)
    expect(b64ToU8(update).byteLength).toBeLessThan(1024)
  })

  it('writes structured attributes a plugin parses into null-prototype objects (contacts)', () => {
    const contacts = { ...schema, plugins: [{ id: 'contacts', version: 2 }] }
    const base = processAuthorityRequest({ schema: contacts, fragment, seedMarkdown: 'Text' })
    const target = { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'contactInline', attrs: { data: { version: 1,
      contacts: [{ name: 'Jana Nová', phones: ['+420777123456'] }] } } }] }] }
    const written = processLiveWriteRequest({ schema: contacts, fragment, snapshot: base.snapshot, target })
    expect(JSON.parse(processAuthorityRequest({ schema: contacts, fragment, snapshot: base.snapshot, updates: [written.update!] }).editorJson).document.content[0].content[0].attrs.data.contacts[0].name)
      .toBe('Jana Nová')
  })
})
