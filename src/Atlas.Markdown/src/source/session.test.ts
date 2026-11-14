// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { MarkdownPluginRegistry } from '../core/plugins/registry'
import type { MarkdownPlugin, MarkdownSchemaPlugin } from '../core/plugins/types'
import type { MarkdownRichValue } from '../core/types'
import { MarkdownDocumentCodec } from '../engine/documentCodec'
import { createTiptapEditor } from '../engine/editorFactory'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'
import { chartNode, createChartsPlugin, defaultChart } from '../plugins/charts'
import { contactNode, createContactsPlugin } from '../plugins/contacts'
import { attachmentNode, createAttachmentsPlugin } from '../plugins/attachments'
import { createOpenApiPlugin, defaultOpenApi, openApiNode } from '../plugins/openapi'
import { createMathBlockNode, createMathInlineNode, createMathPlugin } from '../plugins/math'
import { createDiagramsPlugin } from '../plugins/diagrams'
import { createTocNode, createTocPlugin } from '../plugins/toc'
import { createYouTrackPlugin } from '../plugins/youtrack'
import { createPagesPlugin, pageReferenceNode } from '../plugins/pages'
import { alignItems, createMarkdownSourceSession, MAX_ALIGNMENT_CELLS, type MarkdownSourceApplyResult, type MarkdownSourceSession, type SessionItem } from './session'
import { createProtectedFenceGuard } from './monaco/protectedBlocks'
import { protectedFence } from './protectedFence'

const { findProtectedRanges, resolveKnownBlocks, scanFences } = createProtectedFenceGuard(protectedFence)

const importedPages = import.meta.glob<MarkdownRichValue>('./fixtures/*.json', { eager: true, import: 'default' })

const plugins = (): MarkdownPlugin[] => [
  createHeadingAnchorsPlugin(), createChartsPlugin(), createContactsPlugin(), createAttachmentsPlugin(), createOpenApiPlugin(),
  createMathPlugin(), createDiagramsPlugin(), createTocPlugin(), createYouTrackPlugin({ client: { search: async () => [] } }), createPagesPlugin(),
]

// One detached editor builds test documents from Markdown, as the value loader does.
let registry: MarkdownPluginRegistry
let codec: MarkdownDocumentCodec<'rich'>
let editor: Editor
let schemaPlugins: MarkdownSchemaPlugin[]
beforeAll(() => {
  registry = new MarkdownPluginRegistry(plugins(), 'rich')
  codec = new MarkdownDocumentCodec('rich', registry, { profile: 'rich' })
  editor = createTiptapEditor({ element: null, editable: false, extensions: codec.extensions, content: { type: 'doc', content: [{ type: 'paragraph' }] } })
  schemaPlugins = registry.schemaPlugins.map(plugin => ({ id: plugin.id, version: plugin.version }))
})
afterAll(() => { editor.destroy(); registry.destroy() })

const sessions: MarkdownSourceSession[] = []
afterEach(() => { sessions.splice(0).forEach(session => session.destroy()) })
function open(value: MarkdownRichValue): MarkdownSourceSession {
  const session = createMarkdownSourceSession({ value, plugins: plugins() })
  sessions.push(session)
  return session
}

const t = (text: string, marks?: JSONContent['marks']): JSONContent => ({ type: 'text', text, ...(marks ? { marks } : {}) })
const p = (...content: Array<string | JSONContent>): JSONContent => ({ type: 'paragraph', content: content.map(item => typeof item === 'string' ? t(item) : item) })
const empty = (): JSONContent => ({ type: 'paragraph' })
const rich = (content: JSONContent[]): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content } as MarkdownRichValue['document'], plugins: schemaPlugins })

/** Parsed like a stored Markdown page, with deterministic heading identities. */
function fromMarkdown(markdown: string): JSONContent[] {
  let next = 0
  const identify = (node: JSONContent): JSONContent => ({
    ...node,
    ...(node.type === 'heading' ? { attrs: { ...node.attrs, headingId: `h-${++next}` } } : {}),
    ...(node.content ? { content: node.content.map(identify) } : {}),
  })
  return (codec.parseMarkdown(editor, markdown).toJSON().content as JSONContent[]).map(identify)
}

/** Key-order independent JSON identity (protected nodes are frozen null-prototype clones). */
function canonical(value: unknown): string {
  return JSON.stringify(value, (_key, item: unknown) => item && typeof item === 'object' && !Array.isArray(item)
    ? Object.fromEntries(Object.entries(item as Record<string, unknown>).sort(([a], [b]) => a.localeCompare(b)))
    : item)
}
function ok(result: MarkdownSourceApplyResult) {
  if (!result.ok) throw new Error(`Expected a successful apply: ${JSON.stringify(result.diagnostics)}`)
  return result
}
function contentOf(result: MarkdownSourceApplyResult): JSONContent[] {
  return ok(result).value.document.content as unknown as JSONContent[]
}
/** The top-level blocks of a value as node JSON. */
const stored = (value: MarkdownRichValue) => value.document.content as unknown as JSONContent[]
function lines(session: MarkdownSourceSession): string[] { return session.text.split('\n') }
function withoutRange(session: MarkdownSourceSession, id: string): string[] {
  const range = session.protectedBlocks.find(block => block.id === id)!
  return lines(session).filter((_line, index) => index + 1 < range.startLine || index + 1 > range.endLine)
}
function fenceLines(session: MarkdownSourceSession, id: string): string[] {
  const range = session.protectedBlocks.find(block => block.id === id)!
  return lines(session).slice(range.startLine - 1, range.endLine)
}
function onlyBlock(session: MarkdownSourceSession) {
  expect(session.blocks.size).toBe(1)
  return [...session.blocks.values()][0]!
}
function expectNoOp(session: MarkdownSourceSession, value: MarkdownRichValue) {
  const result = ok(session.apply(session.text))
  expect(result.changed).toBe(false)
  expect(result.value).toEqual(value)
  expect(result.diagnostics).toEqual([])
}
function expectConsistentRanges(session: MarkdownSourceSession) {
  const all = lines(session)
  expect(session.protectedBlocks.map(range => range.id).sort()).toEqual([...session.blocks.keys()].sort())
  for (const range of session.protectedBlocks) {
    const block = session.blocks.get(range.id)!
    expect(all.slice(range.startLine - 1, range.endLine)).toEqual(protectedFence.lines(range.id, block.label, block.text))
    expect(range.text).toBe(block.text)
    expect(range.label).toBe(block.label)
    if (range.startLine > 1) expect(all[range.startLine - 2]).toBe('')
    expect(all[range.endLine]).toBe('')
  }
}

const KITCHEN_SINK = [
  '# Provozní příručka', '',
  'Úvodní odstavec s **tučným**, *kurzívou*, `kódem` a [odkazem](https://example.com/docs).', '',
  '## Pozadí', '',
  'Druhý odstavec\\', 'se zalomením řádku.', '',
  '- první položka', '- druhá položka', '  - vnořená položka', '',
  '1. krok jedna', '2. krok dva', '',
  '> Citace s důrazem **zde**.', '',
  '```ts', 'const answer = 42', '```', '',
  '| Služba | Port |', '| --- | --- |', '| api | 8080 |', '',
  '---', '',
  '### Závěr', '',
  'Poslední odstavec.',
].join('\n')

function kitchenSink(): MarkdownRichValue {
  const content = fromMarkdown(KITCHEN_SINK)
  // Empty paragraphs have no Markdown form; imported and hand-edited pages still contain them.
  content.splice(2, 0, empty())
  content.push(empty())
  return rich(content)
}

/** A chart linked to a table that is not in the document has no Markdown form (the dialect names the table). */
const lossyChart = (title = defaultChart.title): JSONContent => chartNode({ ...defaultChart, title, sourceStatus: 'missing',
  source: { tableId: 'jinde', cells: defaultChart.grid.map((row, r) => row.map((_cell, c) => `jinde-${r}-${c}`)) } })

const pluginDocument = (): MarkdownRichValue => rich([
  { type: 'heading', attrs: { level: 1, headingId: 'plugin-title' }, content: [t('Přehled')] },
  lossyChart('Tržby 2025'),
  p('Mezi grafem a kontakty.'),
  contactNode({ name: 'Jana Nováková' }),
  openApiNode(defaultOpenApi),
  createMathBlockNode({ version: 1, expression: 'E = mc^2' }),
  attachmentNode({ version: 1, name: 'smlouva.pdf', url: 'https://files.example.com/smlouva.pdf' }),
  p('Vzorec v textu ', createMathInlineNode({ version: 1, expression: 'a^2' }), ' uvnitř odstavce.'),
  p('Úkol ', { type: 'youtrackIssue', attrs: { id: '2-17', key: 'ATLAS-17', title: 'Oprava importu', url: 'https://youtrack.example.com/issue/ATLAS-17', state: null } }, ' čeká.'),
  p('Konec.'),
])

const repoDocument = (): MarkdownRichValue => rich([
  { type: 'heading', attrs: { level: 1, headingId: 'rich-title' }, content: [t('Více než Markdown')] },
  p(t('Podtržení zůstává zachované.', [{ type: 'underline' }])),
  p('Vyberte text a prozkoumejte bohatší formátování.'),
])

describe('Markdown source session: no-op round trip', () => {
  it.each(Object.keys(importedPages).sort())('returns the imported page %s unchanged', name => {
    const value = structuredClone(importedPages[name]!)
    const session = open(value)
    expectConsistentRanges(session)
    expectNoOp(session, value)
    expect(ok(session.apply(session.text.replace(/\n/g, '\r\n'))).changed).toBe(false)
    // Imported images carry no or the default presentation, which Markdown reproduces; an imported task list is plain Markdown.
    expect([...session.blocks.values()].filter(block => block.kind === 'image' || block.kind === 'taskList')).toEqual([])
  })

  it.each([
    ['kitchen sink', kitchenSink],
    ['plugin document', pluginDocument],
    ['repository rich example', repoDocument],
    ['single empty paragraph', () => rich([empty()])],
  ] as const)('returns the %s unchanged', (_name, build) => {
    const value = build()
    const session = open(value)
    expectConsistentRanges(session)
    expectNoOp(session, value)
  })

  it('keeps the Markdown blocks of the kitchen sink editable', () => {
    const session = open(kitchenSink())
    expect(session.blocks.size).toBe(0)
    expect(session.text).toBe(`${KITCHEN_SINK}\n`)
  })

  it('returns the original envelope even when the session normalizes its plugin metadata', () => {
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [p('Bez metadat.')] } as MarkdownRichValue['document'] }
    const session = open(value)
    expectNoOp(session, value)
  })
})

// The source keeps runs of spaces and reads addresses as text; everything else it reads as the document editor does.
describe('Markdown source session: reading', () => {
  const documentRead = (markdown: string) => codec.parseMarkdown(editor, markdown).toJSON().content as JSONContent[]
  function sourceRead(markdown: string): JSONContent[] {
    return contentOf(open(rich([p('Úvod.')])).apply(`Úvod.\n\n${markdown}\n`)).slice(1)
  }

  it.each([
    ['soft line break', 'První řádek\ndruhý řádek'],
    ['soft line break in a list item', '- krok jedna\n  pokračuje'],
    ['soft line break in a callout', '> [!NOTE]\n> řádek jedna\n> řádek dva'],
    ['task list', '- [ ] úkol\n- [x] hotovo'],
    ['image between words', 'Krok ![a](https://example.com/a.png) dál'],
    ['nested list', '1. první\n   - vnořený\n   - další\n2. druhý'],
    ['loose list', '- a\n\n- b'],
    ['table', '| a | b |\n| --- | --- |\n| 1 | **2** |'],
    ['hard break', 'a  \nb'],
    ['backslash hard break', 'a\\\nb'],
    ['inline marks', '**tučně** *kurzíva* `kód` ~~škrt~~ ==zvýraznění=='],
    ['callout', '> [!TIP]\n> tip **tučně**\n>\n> druhý odstavec'],
    ['heading', '## Nadpis *s* důrazem'],
    ['inline math', 'Vzorec $a + b$ a text.'],
    ['html entity', 'a&nbsp;&nbsp;b &amp; c'],
  ])('%s reads as the document editor reads it', (_name, markdown) => {
    expect(sourceRead(markdown)).toEqual(documentRead(markdown))
  })

  it('an existing task list stays editable and a no-op leaves it as it is', () => {
    const value = rich([...documentRead('- [ ] úkol\n- [x] hotovo'), p('Konec.')])
    const session = open(value)
    expect([...session.blocks.values()]).toEqual([])
    expect(session.text).toContain('- [ ] úkol\n')
    expectNoOp(session, value)
    expect(contentOf(session.apply(session.text.replace('úkol', 'nový  úkol')))[0]!.content![0]!.content![0]!.content![0]!.text).toBe('nový  úkol')
  })
})

describe('Markdown source session: protection', () => {
  const th = (text: string, attrs: Record<string, unknown> = {}): JSONContent => ({ type: 'tableHeader', attrs, content: [p(text)] })
  const td = (text: string, attrs: Record<string, unknown> = {}): JSONContent => ({ type: 'tableCell', attrs, content: [p(text)] })
  const tr = (...cells: JSONContent[]): JSONContent => ({ type: 'tableRow', content: cells })
  const table = (...rows: JSONContent[]): JSONContent => ({ type: 'table', content: rows })

  // A table is written as GFM or, when GFM cannot carry it, as ```a-table: the round trip decides what either keeps.
  it.each<[string, 'lossy' | 'round-trip', () => JSONContent]>([
    ['underline', 'lossy', () => p(t('podtržené', [{ type: 'underline' }]))],
    ['paragraph alignment', 'lossy', () => ({ type: 'paragraph', attrs: { textAlign: 'center' }, content: [t('Na střed')] })],
    ['heading alignment', 'lossy', () => ({ type: 'heading', attrs: { level: 2, textAlign: 'right', headingId: 'aligned' }, content: [t('Vpravo')] })],
    ['table column width', 'round-trip', () => table(tr(th('A', { colwidth: [160] })), tr(td('1', { colwidth: [160] })))],
    ['table cell background', 'round-trip', () => table(tr(th('A')), tr(td('1', { backgroundColor: 'amber' })))],
    ['table row height', 'round-trip', () => table(tr(th('A')), { type: 'tableRow', attrs: { height: 48 }, content: [td('1')] })],
    ['table header cell in the body', 'round-trip', () => table(tr(th('A')), tr(th('1')))],
    ['table cell alignment differing from its header', 'round-trip', () => table(tr(th('A')), tr(td('1', { textAlign: 'right' })))],
  ])('protects a block Markdown cannot carry: %s', (_name, reason, build) => {
    const node = build()
    const value = rich([p('Před.'), node, p('Po.')])
    const session = open(value)
    const block = onlyBlock(session)
    expect(block.reason).toBe(reason)
    expect(block.kind).toBe(node.type)
    expect(block.node).toEqual(node)
    expect(block.text).not.toBe('')
    expect(session.text).toBe(`Před.\n\n${protectedFence.lines(block.id, block.label, block.text).join('\n')}\n\nPo.\n`)
    expectNoOp(session, value)
  })

  it.each<[string, () => JSONContent, string, string]>([
    ['colspan', () => table(tr(th('Sloučené', { colspan: 2 })), tr(td('a'), td('b'))), 'content: a', 'content: změna'],
    ['rowspan', () => table(tr(th('A'), th('B')), tr(td('1', { rowspan: 2 }), td('2')), tr(td('3'))), 'content: "3"', 'content: změna'],
    ['a cell with two paragraphs', () => table(tr(th('A')), tr({ type: 'tableCell', content: [p('jedna'), p('dvě')] })), 'jedna', 'změna'],
  ])('keeps a table GFM cannot carry editable as ```a-table: %s', (_name, build, from, to) => {
    const node = build()
    const value = rich([p('Před.'), node, p('Po.')])
    const session = open(value)
    expect([...session.blocks.values()]).toEqual([])
    expect(session.text).toContain('```a-table\n')
    expectNoOp(session, value)
    expect(session.text).toContain(from)
    const edited = session.text.replace(from, to)
    const result = ok(session.apply(edited))
    const content = contentOf(result)
    expect(content[1]!.type).toBe('table')
    expect(JSON.stringify(content[1])).toContain('změna')
    expect([content[0], content[2]]).toEqual([stored(value)[0], stored(value)[2]])
    const reopened = open(result.value)
    expect(reopened.text).toBe(edited)
    expectNoOp(reopened, result.value)
  })

  it('protects a paragraph with a YouTrack issue, which would serialize as a plain link', () => {
    const issue = { type: 'youtrackIssue', attrs: { id: '2-17', key: 'ATLAS-17', title: 'Oprava importu', url: 'https://youtrack.example.com/issue/ATLAS-17', state: 'Open' } }
    const value = rich([p('Viz ', issue, '.')])
    const markdown = codec.serializeMarkdown(editor, codec.parseRichDocument(value))
    expect(markdown).toBe('Viz [ATLAS-17: Oprava importu · Open](https://youtrack.example.com/issue/ATLAS-17).')
    const session = open(value)
    const block = onlyBlock(session)
    expect(block).toMatchObject({ reason: 'lossy', kind: 'paragraph', label: 'Chráněný blok: Odstavec „Viz ATLAS-17: Oprava importu · Open (https://youtrack…“' })
    expect(block.label.length).toBeLessThanOrEqual(80)
    expectNoOp(session, value)
  })

  it('protects every plugin node without a Markdown form with a readable Czech label', () => {
    const value = pluginDocument()
    const session = open(value)
    const labels = Object.fromEntries([...session.blocks.values()].map(block => [block.kind === 'paragraph' ? block.label.slice(0, 40) : block.kind, block.label]))
    expect(labels).toEqual({
      chart: 'Chráněný blok: Graf „Tržby 2025“',
      openapi: 'Chráněný blok: API „New API“',
      attachment: 'Chráněný blok: Příloha „smlouva.pdf“',
      'Chráněný blok: Odstavec „Úkol ATLAS-17: ': 'Chráněný blok: Odstavec „Úkol ATLAS-17: Oprava importu (https://youtrack.examp…“',
    })
    expect([...session.blocks.values()].every(block => block.reason === 'lossy')).toBe(true)
    expect(session.text).toContain('# Přehled\n\n```md-protected ')
    expect(session.text).toContain('\n\nMezi grafem a kontakty.\n\n')
    // Math and contacts are part of the editor's Markdown dialect.
    expect(session.text).toContain('\n\n```a-contact\n- name: Jana Nováková\n```\n\n')
    expect(session.text).toContain('\n\n$$\nE = mc^2\n$$\n\n')
    expect(session.text).toContain('\n\nVzorec v textu $a^2$ uvnitř odstavce.\n\n')
    expect(session.text.endsWith('\n\nKonec.\n')).toBe(true)
  })

  it('protects an existing md-protected code block, whose fence syntax is reserved', () => {
    const code: JSONContent = { type: 'codeBlock', attrs: { language: 'md-protected-widget' }, content: [t('{"widget": 1}')] }
    const session = open(rich([p('Text.'), code]))
    expect(onlyBlock(session)).toMatchObject({ reason: 'reserved-code', kind: 'codeBlock', label: 'Chráněný blok: Blok kódu „md-protected-widget“' })
  })

  it.each<[string, JSONContent]>([
    // The editor dialect reads ```mermaid as a diagram, so an older mermaid code block no longer comes back as code.
    ['a mermaid code block', { type: 'codeBlock', attrs: { language: 'mermaid' }, content: [t('flowchart LR')] }],
  ])('protects a block that fails an actual round trip: %s', (_name, node) => {
    const session = open(rich([node]))
    expect(onlyBlock(session)).toMatchObject({ reason: 'round-trip', kind: node.type })
  })

  it('keeps the editor dialect editable: H4, alerts, highlight, mermaid and math apply and render back', () => {
    const example = [
      '#### Nadpis 4', '',
      '> [!NOTE]', '> Poznámka', '',
      '> [!TIP]', '> Tip', '',
      '> [!IMPORTANT]', '> Důležité', '',
      '> [!WARNING]', '> Varování', '',
      '> [!CAUTION]', '> Pozor', '',
      'Text se ==zvýrazněním== a `==kód==` a https://example.com/?a==b==c', '',
      '```mermaid', 'flowchart LR', '  A --> B', '```', '',
      '$$', 'E = mc^2', '$$', '',
    ].join('\n')
    const result = ok(open(rich([p('Úvod.')])).apply(example))
    expect(result.diagnostics).toEqual([])
    const content = result.value.document.content as unknown as JSONContent[]
    expect(content.map(node => [node.type, node.attrs?.level ?? node.attrs?.variant ?? node.attrs?.data])).toEqual([
      ['heading', 4], ['callout', 'note'], ['callout', 'tip'], ['callout', 'important'], ['callout', 'warning'], ['callout', 'caution'],
      ['paragraph', undefined],
      ['diagram', { version: 1, source: 'flowchart LR\n  A --> B', caption: '' }],
      ['mathBlock', { version: 1, expression: 'E = mc^2' }],
    ])
    expect(content[6]!.content!.filter(node => node.marks?.some(mark => mark.type === 'highlight')).map(node => node.text)).toEqual(['zvýrazněním'])
    const reopened = open(result.value)
    expect(reopened.blocks.size).toBe(0)
    // A bare address is text in the source, not a link, and its `==` is no highlight.
    expect(reopened.text).toBe(example)
    expectNoOp(reopened, result.value)
  })

  describe('ordinary content stays editable', () => {
    const image = (src: string, presentation: Record<string, unknown>): JSONContent => {
      const node = fromMarkdown(`![Snímek](${src})`)[0]!
      return { ...node, attrs: { ...node.attrs, presentation: { ...node.attrs!.presentation, ...presentation } } }
    }
    const item = (...content: JSONContent[]): JSONContent => ({ type: 'listItem', content })
    const page = pageReferenceNode({ id: 'p-1', title: 'Připojení VPN', url: '/wiki/it/vpn' })
    /** Open → apply the unchanged text (no-op) → apply an edit → the edited text is what the result opens as. */
    function edit(value: MarkdownRichValue, from: string, to: string): JSONContent[] {
      const session = open(value)
      expect([...session.blocks.values()]).toEqual([])
      expectNoOp(session, value)
      expect(session.text).toContain(from)
      const edited = session.text.replace(from, to)
      const result = ok(session.apply(edited))
      expect(result.changed).toBe(true)
      const reopened = open(result.value)
      expect(reopened.text).toBe(edited)
      expectNoOp(reopened, result.value)
      return contentOf(result)
    }

    it('a list of steps with resized screenshots: an image keeps its presentation while its address stays', () => {
      const first = image('https://example.com/a.png', { width: 50, caption: 'Okno klienta' })
      const second = image('https://example.com/b.png', { width: 30, align: 'left' })
      const value = rich([{ type: 'orderedList', attrs: { start: 1 }, content: [item(p('Připojit na VPN'), first), item(p('Zadat heslo'), second)] }, first])
      const content = edit(value, 'Zadat heslo', 'Zadat **nové** heslo')
      const images = (content[0]!.content as JSONContent[]).map(step => step.content![1]!)
      expect(images.map(node => node.attrs!.presentation)).toEqual([first.attrs!.presentation, second.attrs!.presentation])
      expect(content[1]).toEqual(first)
      // A new address is a new image: nothing to carry.
      const session = open(value)
      const replaced = contentOf(session.apply(session.text.replace('https://example.com/b.png', 'https://example.com/c.png')))
      expect(replaced[0]!.content![1]!.content![1]!.attrs).toMatchObject({ src: 'https://example.com/c.png', presentation: { width: 100, caption: '' } })
    })

    it('protects only what one text cannot stand for: an address shown in two sizes, a page link that is also a plain link', () => {
      const plainImage = fromMarkdown('![Snímek](https://example.com/a.png)')[0]!
      const link = t('Připojení VPN', [{ type: 'link', attrs: { href: '/wiki/it/vpn' } }])
      const value = rich([image('https://example.com/a.png', { width: 50 }), p('Mezi.'), plainImage, p('Viz ', page, '.'), p('Ruční ', link, ' odkaz.')])
      const session = open(value)
      expect([...session.blocks.values()].map(block => [block.kind, block.reason, block.node])).toEqual([
        ['image', 'round-trip', stored(value)[0]], ['paragraph', 'round-trip', stored(value)[3]]])
      expectNoOp(session, value)
      const content = contentOf(session.apply(session.text.replace('Ruční', 'Vlastní')))
      expect(content[4]!.content![1]).toMatchObject({ type: 'text', text: 'Připojení VPN', marks: [{ type: 'link' }] })
      expect(content.slice(0, 4)).toEqual(stored(value).slice(0, 4))
    })

    it('a page reference with a non-ASCII address, and a link with its own tooltip', () => {
      const czech = pageReferenceNode({ id: 'p-2', title: 'Připojení', url: '/wiki/it/připojení' })
      const value = rich([p('Viz ', czech, ' a dál.')])
      const content = edit(value, 'a dál', 'a ještě dál')
      expect(content[0]!.content![1]).toEqual(czech)
      const session = open(value)
      const tipped = contentOf(session.apply(session.text.replace(/\]\(([^)]*)\)/, ']($1 "tip")')))[0]!.content![1]!
      expect(tipped).toMatchObject({ type: 'text', text: 'Připojení', marks: [{ type: 'link', attrs: { title: 'tip' } }] })
    })

    it('a page reference: its link with the same address and title is the reference again', () => {
      const value = rich([{ type: 'bulletList', content: [item(p('Viz ', page, ' a dál.'))] }])
      expect(open(value).text).toBe('- Viz [Připojení VPN](/wiki/it/vpn) a dál.\n')
      expect(edit(value, 'a dál', 'a ještě dál')[0]!.content![0]!.content![0]!.content![1]).toEqual(page)
      // A different label is the writer's own link.
      const session = open(value)
      const renamed = contentOf(session.apply(session.text.replace('[Připojení VPN]', '[návod]')))[0]!.content![0]!.content![0]!.content![1]!
      expect(renamed).toMatchObject({ type: 'text', text: 'návod', marks: [{ type: 'link', attrs: { href: '/wiki/it/vpn' } }] })
    })

    it('text that only looks like an address stays text, and a link stays a link', () => {
      const link = t('https://example.com/docs', [{ type: 'link', attrs: { href: 'https://example.com/docs' } }])
      const value = rich([p('Server mail.example.com a https://example.com/api bez odkazu, ', link, ' s odkazem.')])
      expect(open(value).text).toBe('Server mail.example.com a https://example.com/api bez odkazu, <https://example.com/docs> s odkazem.\n')
      const paragraph = edit(value, 'bez odkazu', 'stále bez odkazu')[0]!.content!
      expect(paragraph.map(node => (node.marks ?? []).map(mark => mark.type))).toEqual([[], ['link'], []])
      expect(paragraph[0]!.text).toBe('Server mail.example.com a https://example.com/api stále bez odkazu, ')
    })

    it('an address with characters Markdown escapes elsewhere, and an image whose alternative text holds them', () => {
      const href = 'https://example.com/~user/*_a_'
      const picture = { ...fromMarkdown('![x](https://example.com/a.png)')[0]!, attrs: { ...fromMarkdown('![x](https://example.com/a.png)')[0]!.attrs, alt: 'screen_(2)[1] *x*.png' } }
      const value = rich([p('Adresa ', t(href, [{ type: 'link', attrs: { href } }]), ' a dál.'), picture])
      expect(open(value).text.split('\n')[0]).toBe(`Adresa <${href}> a dál.`)
      const content = edit(value, 'a dál', 'a ještě dál')
      expect(content[0]!.content![1]).toMatchObject({ text: href, marks: [{ type: 'link', attrs: { href } }] })
      expect(content[1]).toEqual(picture)
    })

    it('an address a <…> would cut is written [text](url)', () => {
      const link = (href: string) => t(href, [{ type: 'link', attrs: { href } }])
      const value = rich([p(link('https://example.com/a<b>c'), ' a ', link('https://example.com/a b'))])
      const session = open(value)
      expect(session.text).not.toContain('<https://')
      expect(session.text).toContain('[https://example.com/a b](https://example.com/a b)')
      expectNoOp(session, value)
    })

    it('runs of spaces inside a line', () => {
      const value = rich([{ type: 'bulletList', content: [item(p('Host name -  jméno prostředí'))] }, p('Dva  odstavce,   tři mezery.')])
      const content = edit(value, 'jméno', 'název')
      expect(content[0]!.content![0]!.content![0]!.content![0]!.text).toBe('Host name -  název prostředí')
      expect(content[1]).toEqual(stored(value)[1])
    })

    it('a code block whose language is an empty string', () => {
      const value = rich([{ type: 'codeBlock', attrs: { language: '' }, content: [t('dotnet build')] }])
      expect(edit(value, 'dotnet build', 'dotnet test')[0]!.content![0]!.text).toBe('dotnet test')
    })

    it('a list stored tight whose items hold several blocks is shown as Markdown reads it', () => {
      const code: JSONContent = { type: 'codeBlock', attrs: { language: 'sh' }, content: [t('ls')] }
      const list: JSONContent = { type: 'orderedList', attrs: { start: 1, tight: true }, content: [item(p('první'), code), item(p('druhý'), code)] }
      const value = rich([{ type: 'heading', attrs: { level: 2, headingId: 'kroky' }, content: [t('Kroky')] }, list, p('Konec.')])
      expect((edit(value, 'druhý', 'druhý krok')[1]!.content as JSONContent[]).map(step => step.content!.map(node => node.type))).toEqual([['paragraph', 'codeBlock'], ['paragraph', 'codeBlock']])
    })
  })

  it('shows the whole text of a protected block, searchable and never read back', () => {
    const code: JSONContent = { type: 'codeBlock', attrs: { language: 'sh' }, content: [t('ls -la')] }
    const list: JSONContent = { type: 'bulletList', content: [
      { type: 'listItem', content: [p('první ', t('podtržená', [{ type: 'underline' }]), ' položka'), code] },
      { type: 'listItem', content: [p('druhá s ', t('odkazem', [{ type: 'link', attrs: { href: 'https://example.com/navod' } }]))] },
    ] }
    const value = rich([p('Před.'), list, p('Po.')])
    const session = open(value)
    const block = onlyBlock(session)
    expect(block.text.split('\n')).toEqual(['- první podtržená položka', '', '  ```sh', '  ls -la', '  ```', '- druhá s [odkazem](https://example.com/navod)'])
    // An inner fence is shown with tildes, so it cannot close the block (for the session and for Monaco alike).
    expect(fenceLines(session, block.id)).toEqual([`\`\`\`md-protected ${block.id}`, block.label,
      '- první podtržená položka', '', '  ~~~sh', '  ls -la', '  ~~~', '- druhá s [odkazem](https://example.com/navod)', '```'])
    expect(scanFences(lines(session))).toEqual([{ id: block.id, startLine: 3, endLine: 11 }])
    expectConsistentRanges(session)
    expectNoOp(session, value)
    // Editing around it keeps the node; editing inside it is refused.
    expect(contentOf(session.apply(session.text.replace('Po.', 'Potom.')))[1]).toEqual(list)
    expect(session.apply(session.text.replace('druhá s', 'třetí s')).ok).toBe(false)
  })

  it('keeps lists of one kind that follow each other apart with a separator line', () => {
    const list = (type: string, ...texts: string[]): JSONContent => ({ type, ...(type === 'orderedList' ? { attrs: { start: 1 } } : {}), content: texts.map(text => ({ type: 'listItem', content: [p(text)] })) })
    const value = rich([p('Úvod.'), list('bulletList', 'první seznam'), list('bulletList', 'druhý seznam'), list('bulletList', 'třetí seznam'),
      list('orderedList', 'krok'), list('orderedList', 'jiný krok'), p('Konec.')])
    const session = open(value)
    expect([...session.blocks.values()]).toEqual([])
    expect(session.text).toBe('Úvod.\n\n- první seznam\n\n<!-- -->\n\n- druhý seznam\n\n<!-- -->\n\n- třetí seznam\n\n1. krok\n\n<!-- -->\n\n1. jiný krok\n\nKonec.\n')
    expectNoOp(session, value)
    // An edit of one list leaves its neighbours as they were; without the line the two are one list.
    const edited = session.text.replace('druhý seznam', 'druhý seznam\n- a jeho druhá položka')
    const result = ok(session.apply(edited))
    const content = contentOf(result)
    expect(content.map(node => [node.type, node.content?.length])).toEqual([['paragraph', 1], ['bulletList', 1], ['bulletList', 2], ['bulletList', 1], ['orderedList', 1], ['orderedList', 1], ['paragraph', 1]])
    expect([content[1], content[3], content[4], content[5]]).toEqual([1, 3, 4, 5].map(index => stored(value)[index]))
    const reopened = open(result.value)
    expect(reopened.text).toBe(edited)
    expectNoOp(reopened, result.value)
    const joined = contentOf(session.apply(session.text.replace('- první seznam\n\n<!-- -->\n\n', '- první seznam\n')))
    expect(joined.map(node => [node.type, node.content?.length]).slice(0, 3)).toEqual([['paragraph', 1], ['bulletList', 2], ['bulletList', 1]])
  })

  // The package alone has no reader of `{#id}` (Atlas's requirement plugin brings it: there such a heading is editable, see its
  // requirementHeadingMarkdown.test.ts), so the heading does not read back and stays protected, shown as it would be written.
  it('a heading with more than its id is not carried by its place: without a reader of its name it stays protected, moved, deleted or retitled around', () => {
    const aliased: JSONContent = { type: 'heading', attrs: { level: 2, headingId: 'vpn', aliases: 'stare-vpn' }, content: [t('VPN')] }
    const plain: JSONContent = { type: 'heading', attrs: { level: 2, headingId: 'dalsi' }, content: [t('Další')] }
    const value = rich([aliased, p('Text.'), plain, p('Konec.')])
    const session = open(value)
    const block = onlyBlock(session)
    expect(block).toMatchObject({ kind: 'heading', reason: 'round-trip', text: '## VPN {#vpn}' })
    expectNoOp(session, value)
    // Moved below the other section, it is still itself and the other heading keeps its own id.
    const fence = fenceLines(session, block.id).join('\n')
    const moved = contentOf(session.apply(`Text.\n\n## Další\n\nKonec.\n\n${fence}\n`))
    expect(moved).toEqual([stored(value)[1], plain, stored(value)[3], aliased])
    // Deleted, with the next heading retitled: that heading keeps its own id and takes nothing of the deleted one.
    const deleted = contentOf(session.apply('Text.\n\n## Jiná\n\nKonec.\n'))
    expect(deleted[1]).toMatchObject({ type: 'heading', attrs: { level: 2, headingId: 'dalsi' }, content: [t('Jiná')] })
    expect(deleted[1]!.attrs!.aliases).toBeFalsy()
    // A heading typed above it takes nothing either.
    const above = contentOf(session.apply(`## Nová\n\n${session.text}`))
    expect(above[0]!.attrs).toMatchObject({ level: 2, headingId: null })
    expect(above[1]).toEqual(aliased)
    // Pasted twice it is an error, never a second owner of the id.
    expect(session.apply(`${session.text}\n${fence}\n`).ok).toBe(false)
  })

  it('a heading with aliases inside a quote keeps its block protected', () => {
    const quote: JSONContent = { type: 'blockquote', content: [{ type: 'heading', attrs: { level: 3, headingId: 'v-citaci', aliases: 'stara' }, content: [t('V citaci')] }, p('Text.')] }
    const value = rich([quote])
    const session = open(value)
    expect(onlyBlock(session)).toMatchObject({ kind: 'blockquote', text: '> ### V citaci\n>\n> Text.' })
    expectNoOp(session, value)
  })

  it('a separator line is the writer\'s text anywhere but between two lists', () => {
    const session = open(rich([p('Úvod.')]))
    const content = contentOf(session.apply('Úvod.\n\n<!-- -->\n\nKonec.\n\n- a\n\n  <!-- -->\n\n  b\n'))
    expect(content.map(node => node.type)).toEqual(['paragraph', 'paragraph', 'paragraph', 'bulletList'])
    expect(content[1]!.content![0]!.text).toBe('<!-- -->')
    expect(JSON.stringify(content[3])).toContain('<!-- -->')
    // After a list only one line separates: the writer's own comment lines stay, and a line before something else than a list too.
    const doubled = contentOf(session.apply('- a\n\n<!-- -->\n\n<!-- -->\n\n- b\n\n<!-- -->\n\nKonec.\n'))
    expect(doubled.map(node => [node.type, node.content?.[0]?.text])).toEqual([['bulletList', undefined], ['paragraph', '<!-- -->'], ['paragraph', '<!-- -->'],
      ['bulletList', undefined], ['paragraph', '<!-- -->'], ['paragraph', 'Konec.']])
  })

  it('sanitizes backticks and caps long labels', () => {
    const session = open(rich([lossyChart('Graf ```md-protected``` se zbytečně dlouhým názvem, který se do jednoho řádku popisku rozhodně nevejde')]))
    const block = onlyBlock(session)
    expect(block.label).not.toContain('`')
    expect(block.label.length).toBeLessThanOrEqual(80)
    expect(block.label.startsWith('Chráněný blok: Graf „Graf \'\'\'md-protected\'\'\' se zbytečně')).toBe(true)
    expect(block.label.endsWith('…“')).toBe(true)
  })

  it('uses random ids per session and the exact fence format', () => {
    const value = pluginDocument()
    const first = open(value), second = open(value)
    expect([...first.blocks.keys()].every(id => /^b[0-9a-z]{4,}$/.test(id))).toBe(true)
    expect([...first.blocks.keys()]).not.toEqual([...second.blocks.keys()])
    const block = [...first.blocks.values()][0]!
    expect(fenceLines(first, block.id)).toEqual([`\`\`\`md-protected ${block.id}`, block.label, ...block.text.split('\n'), '```'])
    expect(Object.isFrozen(block.node)).toBe(true)
  })
})

describe('Markdown source session: apply', () => {
  it('edits one paragraph and keeps every other node identical, including heading ids and empty paragraphs', () => {
    const value = kitchenSink()
    const original = value.document.content as unknown as JSONContent[]
    const session = open(value)
    const result = ok(session.apply(session.text.replace('Poslední odstavec.', 'Poslední upravený odstavec.')))
    expect(result.changed).toBe(true)
    const content = result.value.document.content as unknown as JSONContent[]
    expect(content).toHaveLength(original.length)
    const edited = original.findIndex(node => node.content?.[0]?.text === 'Poslední odstavec.')
    content.forEach((node, index) => { if (index !== edited) expect(node).toEqual(original[index]) })
    expect(content[edited]).toMatchObject({ type: 'paragraph', content: [{ type: 'text', text: 'Poslední upravený odstavec.' }] })
  })

  it('reuses the exact original JSON of untouched imported blocks', () => {
    const value = structuredClone(importedPages['./fixtures/imported-server-github-deployment-guide.json']!)
    const original = value.document.content as unknown as JSONContent[]
    const session = open(value)
    const content = contentOf(session.apply(session.text.replace('## Prerequisites', '## Prerequisites and access')))
    expect(content).toHaveLength(original.length)
    const edited = original.findIndex(node => node.type === 'heading' && node.content?.[0]?.text === 'Prerequisites')
    content.forEach((node, index) => { if (index !== edited) expect(node).toStrictEqual(original[index]) })
    expect(content[edited]).toMatchObject({ type: 'heading', attrs: { level: 2, headingId: original[edited]!.attrs!.headingId } })
  })

  it('keeps images with an absent or default presentation editable and untouched by a neighbouring edit', () => {
    const parsed = fromMarkdown('![Schéma](https://example.com/schema.png)')[0]!
    const stored: JSONContent = { type: 'image', attrs: { src: 'https://example.com/stored.png', alt: 'Uložený', title: null } }
    expect(parsed.attrs?.presentation).toBeTruthy()
    const value = rich([p('Před obrázkem.'), parsed, stored, p('Za obrázkem.')])
    const session = open(value)
    expect(session.blocks.size).toBe(0)
    expect(session.text).toBe('Před obrázkem.\n\n![Schéma](https://example.com/schema.png)\n\n![Uložený](https://example.com/stored.png)\n\nZa obrázkem.\n')
    expectNoOp(session, value)
    const content = contentOf(session.apply(session.text.replace('Před obrázkem.', 'Upraveno před obrázkem.')))
    expect(content[1]).toEqual(parsed)
    // Reused as stored: no presentation added by a Markdown reparse.
    expect(content[2]).toStrictEqual(stored)
    expect(content[0]).toMatchObject({ content: [{ text: 'Upraveno před obrázkem.' }] })
  })

  it('keeps the heading id when fixing a typo or changing the level in the same slot', () => {
    const session = open(kitchenSink())
    const typo = contentOf(session.apply(session.text.replace('## Pozadí', '## Pozadi projektu')))
    expect(typo.find(node => node.attrs?.headingId === 'h-2')).toMatchObject({ attrs: { level: 2 }, content: [{ text: 'Pozadi projektu' }] })
    const level = contentOf(session.apply(session.text.replace('## Pozadí', '### Pozadí')))
    expect(level.find(node => node.attrs?.headingId === 'h-2')).toMatchObject({ attrs: { level: 3 } })
  })

  it('does not copy a heading id onto a new heading elsewhere', () => {
    const session = open(kitchenSink())
    const content = contentOf(session.apply(session.text.replace('Poslední odstavec.', 'Poslední odstavec.\n\n## Nová kapitola')))
    const ids = content.filter(node => node.type === 'heading').map(node => node.attrs?.headingId ?? null)
    expect(ids).toEqual(['h-1', 'h-2', 'h-3', null])
  })

  it('deletes and moves protected blocks', () => {
    const value = pluginDocument()
    const original = value.document.content as unknown as JSONContent[]
    const session = open(value)
    const chart = [...session.blocks.values()].find(block => block.kind === 'chart')!

    const deleted = contentOf(session.apply(withoutRange(session, chart.id).join('\n')))
    expect(deleted).toEqual(original.filter(node => node.type !== 'chart'))

    const moved = contentOf(session.apply(`${withoutRange(session, chart.id).join('\n')}\n${fenceLines(session, chart.id).join('\n')}\n`))
    expect(moved).toEqual([...original.filter(node => node.type !== 'chart'), original.find(node => node.type === 'chart')])
  })

  it('keeps H5 headings without a warning', () => {
    const session = open(kitchenSink())
    const text = session.text.replace('Poslední odstavec.', 'Poslední odstavec.\n\n##### Hluboký nadpis\n\n```md\n##### v kódu zůstává\n```')
    const result = ok(session.apply(text))
    expect(result.diagnostics).toEqual([])
    const content = result.value.document.content as unknown as JSONContent[]
    expect(content).toContainEqual(expect.objectContaining({ type: 'heading', attrs: expect.objectContaining({ level: 5 }), content: [{ type: 'text', text: 'Hluboký nadpis' }] }))
    expect(content).toContainEqual(expect.objectContaining({ type: 'codeBlock', content: [{ type: 'text', text: '##### v kódu zůstává' }] }))
  })

  it('accepts an empty text as an empty document', () => {
    const session = open(rich([p('Jediný odstavec.')]))
    expect(contentOf(session.apply(''))).toEqual([{ type: 'paragraph' }])
  })

  it('rejects apply after destroy', () => {
    const session = createMarkdownSourceSession({ value: rich([p('A')]), plugins: plugins() })
    session.destroy()
    session.destroy()
    expect(() => session.apply('A')).toThrow(/destroyed/)
  })
})

describe('Markdown source session: diagnostics', () => {
  function withChart() {
    const session = open(rich([p('Úvod.'), lossyChart('Tržby 2025'), p('Závěr.')]))
    const block = onlyBlock(session)
    const range = session.protectedBlocks[0]!
    return { session, block, range, all: lines(session) }
  }
  const errors = (result: MarkdownSourceApplyResult) => {
    expect(result.ok).toBe(false)
    return result.diagnostics.filter(item => item.severity === 'error').map(({ line, message }) => ({ line, message }))
  }

  it('reports a duplicated protected block', () => {
    const { session, block, range, all } = withChart()
    const text = [...all.slice(0, range.endLine), '', ...all.slice(range.startLine - 1, range.endLine), ...all.slice(range.endLine)].join('\n')
    const result = session.apply(text)
    expect(result.diagnostics[0]?.column).toBe(17)
    expect(errors(result)).toEqual([{ line: range.endLine + 2,
      message: `Chráněný blok „${block.id}“ je v textu podruhé (poprvé na řádku ${range.startLine}). Chráněný blok lze přesunout nebo smazat, ne duplikovat.` }])
  })

  it('reports an unknown protected block', () => {
    const { session, all } = withChart()
    const text = [...all, '```md-protected bzzzz', 'Chráněný blok: Graf', '```', ''].join('\n')
    const result = session.apply(text)
    expect(result.diagnostics[0]?.column).toBe(17)
    expect(errors(result)).toEqual([{ line: all.length + 1, message: 'Neznámý chráněný blok „bzzzz“. Chráněné bloky nelze vytvářet ani přejmenovávat.' }])
  })

  it('reports an altered label and an edited body', () => {
    const { session, block, range, all } = withChart()
    const relabeled = all.map((line, index) => index === range.startLine ? 'Chráněný blok: Graf „Zisk“' : line).join('\n')
    expect(errors(session.apply(relabeled))).toEqual([{ line: range.startLine + 1,
      message: `Obsah chráněného bloku „${block.id}“ byl změněn. Vraťte původní řádek „${block.label}“, nebo smažte celý blok.` }])
    const body = [...all.slice(0, range.startLine + 1), 'vložený řádek', ...all.slice(range.startLine + 1)].join('\n')
    expect(errors(session.apply(body))).toEqual([{ line: range.startLine + 2,
      message: `Obsah chráněného bloku „${block.id}“ byl změněn. Vraťte původní řádek „${all[range.startLine + 1]}“, nebo smažte celý blok.` }])
    const open = [...all.slice(0, range.endLine - 1), 'vložený řádek', ...all.slice(range.endLine - 1)].join('\n')
    expect(errors(session.apply(open))).toEqual([{ line: range.endLine,
      message: `Chráněný blok „${block.id}“ musí končit řádkem \`\`\` hned za svým textem; jeho obsah nelze upravovat.` }])
    const unlabeled = all.filter((_line, index) => index !== range.startLine).join('\n')
    expect(errors(session.apply(unlabeled))[0]!.line).toBe(range.startLine + 1)
  })

  it('reserves the md-protected fence language', () => {
    const { session, all } = withChart()
    const text = [...all, '```md-protected', 'vlastní kód', '```', ''].join('\n')
    expect(errors(session.apply(text))).toEqual([{ line: all.length + 1,
      message: 'Jazyk „md-protected“ je vyhrazen pro chráněné bloky. Chráněný blok začíná na začátku řádku přesně jako ```md-protected <id>; pro ukázku kódu zvolte jiný jazyk.' }])
  })

  it('reports a protected block swallowed by an unclosed code block', () => {
    const { session, block, range, all } = withChart()
    const text = ['```js', 'neuzavřený kód', ...all].join('\n')
    expect(errors(session.apply(text))).toEqual([{ line: range.startLine + 2,
      message: `Chráněný blok „${block.id}“ je uvnitř neuzavřeného bloku kódu z řádku 1. Uzavřete blok kódu řádkem \`\`\`.` }])
  })

  // The Monaco guard (monaco/protectedBlocks.ts) must protect exactly the fences this session reads.
  it('shares its fence grammar with the Monaco guard', () => {
    const { session, block, range, all } = withChart()
    const spaced = all.map((line, index) => index === range.startLine - 1 ? `${line} \t` : index === range.endLine - 1 ? `${line} ` : line)
    expect(ok(session.apply(spaced.join('\n'))).changed).toBe(false)
    const { text: _text, ...guarded } = range
    expect(findProtectedRanges(spaced, resolveKnownBlocks(all, session.protectedBlocks))).toEqual([guarded])
    expect(findProtectedRanges(spaced, resolveKnownBlocks(spaced, session.protectedBlocks))).toEqual([guarded])

    for (const id of [`${block.id}.x`, `${block.id}é`, `b${'1'.repeat(64)}`]) {
      const invalid = all.map((line, index) => index === range.startLine - 1 ? `\`\`\`md-protected ${id}` : line)
      expect(errors(session.apply(invalid.join('\n')))[0]!.message).toMatch(/^Jazyk „md-protected“ je vyhrazen/)
      expect(scanFences(invalid)).toEqual([])
    }
  })
})

describe('Markdown source session: random edits in editable segments', () => {
  function random(seed: number) {
    return () => {
      seed = (seed + 0x6D2B79F5) | 0
      let value = Math.imul(seed ^ (seed >>> 15), 1 | seed)
      value = (value + Math.imul(value ^ (value >>> 7), 61 | value)) ^ value
      return ((value ^ (value >>> 14)) >>> 0) / 4294967296
    }
  }
  function mixedDocument(): MarkdownRichValue {
    const list = (text: string): JSONContent => ({ type: 'bulletList', content: [{ type: 'listItem', content: [p(text)] }] })
    const content = fromMarkdown(KITCHEN_SINK)
    const lossy: JSONContent[][] = [
      [p(t('podtržené', [{ type: 'underline' }]))],
      [lossyChart('Tržby 2025')],
      [list('sousední A'), list('sousední B')],
      [attachmentNode({ version: 1, name: 'smlouva.pdf', url: 'https://files.example.com/smlouva.pdf' })],
      [{ type: 'codeBlock', attrs: { language: 'md-protected' }, content: [t('rezervováno')] }],
      [createTocNode({ version: 1, title: 'Obsah', minLevel: 1, maxLevel: 4 })],
    ]
    lossy.forEach((nodes, index) => content.splice(1 + index * 3, 0, ...nodes))
    content.splice(4, 0, empty())
    return rich(content)
  }

  it('keeps every protected node deep-equal and in order', () => {
    const value = mixedDocument()
    const session = open(value)
    expect(session.blocks.size).toBe(5)
    const protectedLines = new Set(session.protectedBlocks.flatMap(range => Array.from({ length: range.endLine - range.startLine + 1 }, (_line, offset) => range.startLine + offset)))
    const protectedNodes = [...session.protectedBlocks].map(range => canonical(session.blocks.get(range.id)!.node))
    const next = random(20260926)
    const pick = <T>(items: readonly T[]) => items[Math.floor(next() * items.length)]!
    let inserted = 0
    for (let iteration = 0; iteration < 60; iteration++) {
      const text = lines(session).map((line, index) => ({ line, locked: protectedLines.has(index + 1) }))
      let inCode = false
      const classified = text.map(entry => {
        const fence = !entry.locked && /^\s*(```|~~~)/.test(entry.line)
        const item = { ...entry, fence, inCode: inCode && !fence }
        if (fence) inCode = !inCode
        return item
      })
      for (let edit = 0, edits = 1 + Math.floor(next() * 3); edit < edits; edit++) {
        const editable = classified.map((entry, index) => ({ ...entry, index })).filter(entry => !entry.locked && !entry.fence)
        const operation = pick(['append', 'insert', 'delete'] as const)
        if (operation === 'append') {
          const target = pick(editable.filter(entry => entry.line.trim()))
          classified[target.index] = { ...target, line: `${target.line} upraveno` }
        } else if (operation === 'insert') {
          const target = pick(editable.filter(entry => !entry.line.trim() && !entry.inCode))
          classified.splice(target.index, 0, { line: '', locked: false, fence: false, inCode: false },
            { line: `Nový odstavec ${++inserted}`, locked: false, fence: false, inCode: false })
        } else classified.splice(pick(editable).index, 1)
      }
      const result = session.apply(classified.map(entry => entry.line).join('\n'))
      if (!result.ok) throw new Error(`Iteration ${iteration} failed: ${JSON.stringify(result.diagnostics)}`)
      const content = (result.value.document.content as unknown as JSONContent[]).map(canonical)
      const positions = protectedNodes.map(node => {
        expect(content.filter(item => item === node)).toHaveLength(1)
        return content.indexOf(node)
      })
      expect(positions).toEqual([...positions].sort((a, b) => a - b))
    }
  }, 60_000)
})

describe('Markdown source session: block alignment', () => {
  const items = (...keys: string[]): SessionItem[] => keys.map(key => ({ kind: 'markdown', key, json: { type: 'paragraph' } }))
  it('aligns the longest common subsequence of a small edit', () => {
    expect(alignItems(items('a', 'b', 'c', 'd'), items('a', 'x', 'c', 'd'))).toEqual([[0, 0], [2, 2], [3, 3]])
  })
  it('falls back to the common prefix and suffix beyond the LCS table budget', () => {
    const size = Math.ceil(Math.sqrt(MAX_ALIGNMENT_CELLS)) + 1
    const middle = (side: string) => Array.from({ length: size }, (_, index) => index === size >> 1 ? 'shared' : `${side}${index}`)
    const left = items('start', ...middle('l'), 'end'), right = items('start', ...middle('r'), 'end')
    // Without the cap, 'shared' would be matched through a table of more than MAX_ALIGNMENT_CELLS cells.
    expect(alignItems(left, right)).toEqual([[0, 0], [size + 1, size + 1]])
  })
})

it('uses host-neutral protected fences and treats application names as ordinary code languages', () => {
  const value = rich([lossyChart()])
  const session = open(value)
  expect(session.text).toContain('```md-protected ')
  expectNoOp(session, value)
  const code = rich(fromMarkdown('```atlas\nconst answer = 42\n```'))
  const ordinary = open(code)
  expect(ordinary.blocks.size).toBe(0)
  expectNoOp(ordinary, code)
})