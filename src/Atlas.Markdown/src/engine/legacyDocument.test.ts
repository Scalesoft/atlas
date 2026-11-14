// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownRichValue } from '../core'
import { editorForController } from '../core/internal'
import { repairLegacyDocument } from './legacyDocument'

// Shapes Atlas 2.x stored and rendered that the editor schema refuses (UX cycle 2, IM2): synthetic text only.
// The same cases are in tests/Atlas.Markdown.Sync.Tests/Fixtures/legacy-documents.json for the server.
const text = (value: string, marks?: JSONContent['marks']): JSONContent => ({ type: 'text', text: value, ...(marks ? { marks } : {}) })
const paragraph = (...content: JSONContent[]): JSONContent => ({ type: 'paragraph', ...(content.length ? { content } : {}) })
const link = (href: string) => ({ type: 'link', attrs: { href, target: '_blank', rel: 'noopener noreferrer' } })
const doc = (...content: JSONContent[]): JSONContent => ({ type: 'doc', content })
const value = (document: JSONContent): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document })

const nestedLink = doc(paragraph(text('badge', [link('https://example.com/outer'), link('https://example.com/inner')])))
const boldCode = doc(paragraph(text('a '), text('x', [{ type: 'bold' }, { type: 'code' }])))
const italicCode = doc(paragraph(text('y', [{ type: 'italic' }, { type: 'code' }])))
const itemStartingWithList = doc({ type: 'orderedList', attrs: { start: 12 }, content: [{ type: 'listItem', content: [
  { type: 'orderedList', attrs: { start: 12 }, content: [{ type: 'listItem', content: [paragraph(text('inner'))] }] },
] }] })
const emptyItem = doc({ type: 'orderedList', attrs: { start: 1 }, content: [{ type: 'listItem' }] })

// The server's LegacyDocumentRepairTests read the same cases.
const legacyDisclosures = Object.values(import.meta.glob<{ name: string; before: JSONContent; after: JSONContent }[]>(
  '../../../../tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/legacy-disclosures.json', { eager: true, import: 'default' }))[0]!

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.unstubAllGlobals() })
function open(document: JSONContent) {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: value(document) } })
  controllers.push(controller)
  const host = globalThis.document.createElement('div'); globalThis.document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Legacy document' })
  return { controller, host }
}

describe('Atlas 2.x documents the schema refused', () => {
  it('keeps bold and italic on inline code: the schema accepts them, as Markdown does', () => {
    const { controller, host } = open(boldCode)
    expect(controller.getProjection().markdown).toBe('a **`x`**')
    expect(host.querySelector('strong code, code strong')?.textContent).toBe('x')
    expect(open(italicCode).controller.getProjection().markdown).toBe('*`y`*')
    expect(repairLegacyDocument(boldCode)).toBe(boldCode)
  })

  it('keeps the innermost of two links on one text, the link a reader of 2.x clicked', () => {
    expect(repairLegacyDocument(nestedLink)).toEqual(doc(paragraph(text('badge', [link('https://example.com/inner')]))))
    const { controller, host } = open(nestedLink)
    expect(controller.getProjection().markdown).toBe('[badge](https://example.com/inner)')
    expect(host.querySelectorAll('a')).toHaveLength(1)
  })

  it('gives a list item that starts with a list, or has no content, the empty paragraph it must start with', () => {
    expect(repairLegacyDocument(emptyItem)).toEqual(doc({ type: 'orderedList', attrs: { start: 1 }, content: [{ type: 'listItem', content: [paragraph()] }] }))
    const repaired = repairLegacyDocument(itemStartingWithList)
    expect(repaired.content![0]!.content![0]!.content!.map(node => node.type)).toEqual(['paragraph', 'orderedList'])
    const { controller } = open(itemStartingWithList)
    expect(controller.getProjection().markdown).toContain('inner')
    expect(open(emptyItem).controller.state.lifecycle).toBe('ready')
  })

  it('keeps pasted inline code literal: the mark paste rules skip code they no longer exclude', () => {
    const { controller } = open(doc(paragraph()))
    const editor = editorForController(controller)!
    // jsdom has no ClipboardEvent; ProseMirror reads clipboardData off any paste event.
    const event = new Event('paste', { bubbles: true, cancelable: true })
    const markdown = 'Glob `**/*.ts` and `==x==`, link `https://example.com` and **`bold code`**'
    Object.defineProperty(event, 'clipboardData', { value: { getData: (type: string) => type === 'text/plain' ? markdown : '', types: ['text/plain'], files: [], items: [] } })
    editor.view.dom.dispatchEvent(event)
    const code = (editor.getJSON().content as JSONContent[]).flatMap(block => block.content ?? []).filter(node => node.marks?.some(mark => mark.type === 'code'))
    expect(code.map(node => [node.text, node.marks!.map(mark => mark.type)])).toEqual([
      ['**/*.ts', ['code']], ['==x==', ['code']], ['https://example.com', ['code']], ['bold code', ['bold', 'code']],
    ])
  })

  it('splits a contact card of several contacts into consecutive cards at any depth, as the server does', () => {
    const card = (...names: string[]): JSONContent => ({ type: 'contactCard', attrs: { data: { version: 1, contacts: names.map(name => ({ name })) } } })
    const quoted = (...content: JSONContent[]): JSONContent => ({ type: 'blockquote', content })
    const before = doc(paragraph(text('Lidé')), card('Jana', 'Petr', 'Eva'), quoted(card('Olga', 'Karel')), card('Solo'))
    const after = repairLegacyDocument(before)
    expect(after).toEqual(doc(paragraph(text('Lidé')), card('Jana'), card('Petr'), card('Eva'), quoted(card('Olga'), card('Karel')), card('Solo')))
    expect(repairLegacyDocument(after)).toBe(after)
    expect(before.content).toHaveLength(4)
    expect(repairLegacyDocument(card('Jana', 'Petr'))).toEqual(card('Jana', 'Petr'))
  })

  it('reads Atlas 3.3\'s deployment summary as the requirements overview at any depth, its id kept, as the server does', () => {
    const id = '6f1c1d3e-8a4b-4c55-9d1e-000000000001'
    const before = doc({ type: 'requirementSummary', attrs: { id } }, { type: 'blockquote', content: [{ type: 'requirementSummary', attrs: { id: null } }] })
    const after = repairLegacyDocument(before)
    expect(after).toEqual(doc({ type: 'overview', attrs: { id, kind: 'requirements' } }, { type: 'blockquote', content: [{ type: 'overview', attrs: { id: null, kind: 'requirements' } }] }))
    expect(repairLegacyDocument(after)).toBe(after)
    expect(before.content![0]!.type).toBe('requirementSummary')
  })

  it('reads a history line parsed as nested ordered lists as one dated line again, only inside a requirement, as the server does', () => {
    const list = (start: number, ...content: JSONContent[]): JSONContent => ({ type: 'orderedList', attrs: { tight: true, start }, content: [{ type: 'listItem', content }] })
    const dated = (day: number, month: number, ...inlines: JSONContent[]) => list(day, paragraph(), list(month, paragraph(...inlines)))
    const requirement = (...content: JSONContent[]): JSONContent => ({ type: 'requirement', attrs: { id: '6f1d2c3b-4a5e-4f60-8a71-92b3c4d5e6f7' }, content: [paragraph(text('VPN')), ...content] })
    const line = dated(30, 10, text('2026 23:40 — '), text('Urgováno', [{ type: 'bold' }]), text(': IT KBN'))
    const steps = dated(2, 3, text('krok'))
    const before = doc(requirement(
      { type: 'bulletList', content: [{ type: 'listItem', content: [paragraph(), line] }] },
      dated(1, 11, text('2026 — telefonát', [{ type: 'italic' }])),
      steps,
    ), line)
    const after = repairLegacyDocument(before)
    expect(after).toEqual(doc(requirement(
      { type: 'timedList', content: [{ type: 'timedItem', content: [paragraph(text('30. 10. 2026 23:40 — '), text('Urgováno', [{ type: 'bold' }]), text(': IT KBN'))] }] },
      paragraph(text('1. 11. '), text('2026 — telefonát', [{ type: 'italic' }])),
      steps,
    ), line))
    expect(repairLegacyDocument(after)).toBe(after)
  })

  // The same cases as the server's LegacyDocumentRepairTests (tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/legacy-disclosures.json).
  it.each(legacyDisclosures.map(entry => [entry.name, entry] as const))('turns a disclosure into a heading and its body: %s', (_, entry) => {
    const after = repairLegacyDocument(entry.before)
    expect(after).toEqual(entry.after)
    expect(repairLegacyDocument(after)).toBe(after)
  })

  it('drops a stored link target but _blank, a rel but its own and any class, as the server does; the link renders neither', () => {
    const odd = doc(paragraph(text('odkaz', [{ type: 'link', attrs: { href: 'https://example.com/', target: '_top', rel: 'opener', class: 'button', title: 'T' } }])))
    expect(repairLegacyDocument(odd)).toEqual(doc(paragraph(text('odkaz', [{ type: 'link', attrs: { href: 'https://example.com/', title: 'T' } }]))))
    expect(repairLegacyDocument(nestedLink)).toEqual(doc(paragraph(text('badge', [link('https://example.com/inner')]))))
    // Even unrepaired, the editor renders no stored rel or class: a new tab always gets noopener noreferrer.
    const { controller, host } = open(doc(paragraph(text('a', [{ type: 'link', attrs: { href: 'https://example.com/', target: '_blank', rel: 'opener', class: 'button' } }]))))
    const anchor = host.querySelector('a')!
    expect([anchor.getAttribute('target'), anchor.getAttribute('rel'), anchor.getAttribute('class')]).toEqual(['_blank', 'noopener noreferrer', null])
    // Pasted HTML gives no target, rel or class of its own.
    const editor = editorForController(controller)!
    // jsdom has no ClipboardEvent; view.pasteHTML only passes it on.
    vi.stubGlobal('ClipboardEvent', class extends Event { readonly clipboardData = null })
    editor.view.pasteHTML('<a href="https://example.com/x" target="_top" rel="opener" class="button">x</a>')
    const pasted: Record<string, unknown>[] = []
    editor.state.doc.descendants(node => { node.marks.forEach(mark => { if (mark.attrs.href === 'https://example.com/x') pasted.push(mark.attrs) }) })
    expect(pasted).toEqual([expect.objectContaining({ target: '_blank', rel: 'noopener noreferrer', class: null })])
  })

  it('returns a valid document unchanged and never mutates its input', () => {
    const before = JSON.stringify(nestedLink)
    repairLegacyDocument(nestedLink)
    expect(JSON.stringify(nestedLink)).toBe(before)
    const valid = doc(paragraph(text('ok', [{ type: 'bold' }])))
    expect(repairLegacyDocument(valid)).toBe(valid)
  })
})
