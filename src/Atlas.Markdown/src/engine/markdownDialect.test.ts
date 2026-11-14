// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, parseMarkdownDialect, serializeMarkdownContent, type MarkdownPlugin } from '../core'
import { editorForController } from '../core/internal'
import { contactNode, createContactsPlugin, inlineContactNode, type MarkdownContact } from '../plugins/contacts'
import { chartNode, createChartsPlugin, type ChartData } from '../plugins/charts'
import { parseMarkdownDialectYaml, writeMarkdownDialectYaml } from './markdownDialect'

const cleanups: (() => void)[] = []
afterEach(() => { cleanups.splice(0).forEach(cleanup => cleanup()); document.body.replaceChildren() })

function mount(content: JSONContent[] = [{ type: 'paragraph' }], plugins: MarkdownPlugin[] = [createContactsPlugin(), createChartsPlugin()]): Editor {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: plugins.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content } } } })
  cleanups.push(() => controller.destroy())
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Dialect' })
  return editorForController(controller)!
}
const markdownOf = (editor: Editor) => serializeMarkdownContent(editor, editor.state.doc)
const read = (editor: Editor, markdown: string, strict = false) => parseMarkdownDialect(editor, markdown, { strict })
const blocks = (result: ReturnType<typeof read>) => result.document!.toJSON().content as JSONContent[]

const jana: MarkdownContact = { name: 'Jana Nováková', title: 'Vedoucí provozu', organization: 'ModelSoft', phones: ['+420 777 123 456'], emails: ['jana.novakova@example.test'] }

describe('a-YAML', () => {
  it('reads maps, sequences, flow sequences, literal blocks and the core schema', () => {
    expect(parseMarkdownDialectYaml([
      '# comment', '- name: "true"', '  phones: [+420 777, "a, b"]', '  address: |', '    Line 1', '', '    Line 3', '  kind: auto',
      '- name: B', '  emails:', '  - b@x.cz', '  internalUser:', '    id: u1', '  title: 012', '  department: ~',
    ].join('\n'))).toEqual([
      { name: 'true', phones: ['+420 777', 'a, b'], address: 'Line 1\n\nLine 3\n', kind: 'auto' },
      { name: 'B', emails: ['b@x.cz'], internalUser: { id: 'u1' }, title: 12, department: null },
    ])
    expect(parseMarkdownDialectYaml('a: |-\n  x\nb: |+\n  y\n\nc: 1')).toEqual({ a: 'x', b: 'y\n\n', c: 1 })
  })

  it('refuses anchors, aliases, tags, flow maps, folded blocks, duplicate keys, bad indentation and deep nesting with a line', () => {
    for (const [source, line] of [['a: &x 1', 1], ['a: *x', 1], ['a: !!str 1', 1], ['a: {b: 1}', 1], ['a: >\n  x', 1], ['a: 1\na: 2', 2],
      ['a: 1\n   b: 2', 2], ['a:\n b:\n  c:\n   d:\n    e: 1', 5], ['<<: x', 1], ['a: b: c', 1]] as const) {
      expect(() => parseMarkdownDialectYaml(source), source).toThrow(`Řádek ${line} těla`)
    }
  })

  it('writes strings plain only when they read back unchanged', () => {
    const value = { a: 'true', b: '+420777123456', c: ' x', d: 'a: b', e: 'a #b', f: '-x', g: '012', h: 'Jana Nováková', i: 'multi\nline\n', j: ['x, y', 'z'], k: '1-3' }
    const written = writeMarkdownDialectYaml(value)
    expect(written).toBe([
      'a: "true"', 'b: "+420777123456"', 'c: " x"', 'd: "a: b"', 'e: "a #b"', 'f: "-x"', 'g: "012"', 'h: Jana Nováková',
      'i: |', '  multi', '  line', 'j: ["x, y", z]', 'k: 1-3',
    ].join('\n'))
    expect(parseMarkdownDialectYaml(written)).toEqual(value)
  })
})

const paragraph = (text: string): JSONContent => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const cell = (type: 'tableHeader' | 'tableCell', text: string, chartRef: string): JSONContent => ({ type, attrs: { chartRef }, content: [paragraph(text)] })
/** The source table of `chart`: three columns, the chart covers rows 1–3 and columns 2–3. */
const salesTable: JSONContent = { type: 'table', attrs: { chartRef: 'tabulka-trzby' }, content: [
  { type: 'tableRow', content: [cell('tableHeader', 'Pobočka', 'h0'), cell('tableHeader', 'Období', 'h1'), cell('tableHeader', 'Tržby', 'h2')] },
  { type: 'tableRow', content: [cell('tableCell', 'Brno', 'a0'), cell('tableCell', 'Leden', 'a1'), cell('tableCell', '12,5', 'a2')] },
  { type: 'tableRow', content: [cell('tableCell', 'Praha', 'b0'), cell('tableCell', 'Únor', 'b1'), cell('tableCell', '18', 'b2')] },
] }
const linkedChart: ChartData = { version: 1, title: 'Tržby 2026', kind: 'line', grid: [['Období', 'Tržby'], ['Leden', '12,5'], ['Únor', '18']],
  source: { tableId: 'tabulka-trzby', cells: [['h1', 'h2'], ['a1', 'a2'], ['b1', 'b2']] }, sourceStatus: 'linked' }
const gridChart: ChartData = { ...linkedChart, source: null, sourceStatus: null }

/** Chart cell ids are identity (normalization N4): fresh ids on every read. */
function withoutCellIds(node: JSONContent): JSONContent {
  const attrs = node.attrs && { ...node.attrs }
  if (attrs && (node.type === 'tableCell' || node.type === 'tableHeader')) attrs.chartRef = null
  if (attrs?.data?.source) attrs.data = { ...attrs.data, source: { ...attrs.data.source, cells: [] } }
  return { ...node, ...(attrs ? { attrs } : {}), ...(node.content ? { content: node.content.map(withoutCellIds) } : {}) }
}

describe('a-contact', () => {
  it('writes a list of contacts as a fence and one contact as an inline link, and reads both back', () => {
    const petr: MarkdownContact = { name: 'Petr', emails: ['p@x.cz'], address: 'Náměstí 1\nBrno', kind: 'internal', internalUser: { id: 'u-7', name: 'Petr S.' } }
    const editor = mount([contactNode(jana), contactNode(petr), { type: 'paragraph', content: [{ type: 'text', text: 'Volejte ' }, inlineContactNode({ ...jana, name: 'Jana [Nováková]' }), { type: 'text', text: '.' }] }])
    const markdown = [
      '```a-contact', '- name: Jana Nováková', '  title: Vedoucí provozu', '  organization: ModelSoft', '  phones: ["+420 777 123 456"]', '  emails: [jana.novakova@example.test]', '```',
      '', '```a-contact', '- name: Petr', '  emails: [p@x.cz]', '  address: |-', '    Náměstí 1', '    Brno', '  kind: internal', '  internalUser:', '    id: u-7', '    name: Petr S.', '```',
      '',
      'Volejte [Jana \\[Nováková\\]](a-contact:?name=Jana%20[Nováková]&title=Vedoucí%20provozu&organization=ModelSoft&phone=+420%20777%20123%20456&email=jana.novakova@example.test).',
    ].join('\n')
    expect(markdownOf(editor)).toBe(markdown)
    const result = read(editor, markdown, true)
    expect(result).toMatchObject({ errors: [], warnings: [] })
    expect(blocks(result)).toEqual(editor.getJSON().content)
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
    // One contact per block: an older fence listing both reads as the same two blocks, its comment kept with the first.
    const listed = markdown.replace('```\n\n```a-contact\n- name: Petr', '- name: Petr').replace('```a-contact\n', '```a-contact\n# tým\n')
    expect(listed.match(/```a-contact/g)).toHaveLength(1)
    expect(blocks(read(editor, listed, true))).toEqual(editor.getJSON().content)
  })

  it('reads the inline form with repeated keys, encoded characters and an internal user', () => {
    const editor = mount()
    const result = read(editor, 'Kontakt: [cokoli](a-contact:?name=Eva%20Malá&phone=+420%20111&phone=222&email=eva@x.cz&address=A%26B%0AC&internalUser.id=u1)', true)
    expect(result.errors).toEqual([])
    expect(blocks(result)[0]!.content![1]).toEqual(inlineContactNode({ name: 'Eva Malá', phones: ['+420 111', '222'], emails: ['eva@x.cz'], address: 'A&B\nC', internalUser: { id: 'u1' } }))
  })

  it('round-trips the page-local role in both forms, right after the name', () => {
    const editor = mount()
    const markdown = ['```a-contact', '- name: Jana', '  role: PM projektu', '  title: Vedoucí', '```', '', 'Volejte [Petr](a-contact:?name=Petr&role=Garant).'].join('\n')
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(blocks(result)[0]).toEqual(contactNode({ name: 'Jana', role: 'PM projektu', title: 'Vedoucí' }))
    expect(blocks(result)[1]!.content![1]).toEqual(inlineContactNode({ name: 'Petr', role: 'Garant' }))
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
  })
  it('round-trips the directory object id in both forms and leaves contacts without it unchanged', () => {
    const editor = mount()
    const markdown = [
      '```a-contact', '- name: Jana', '  directoryId: d1', '  objectId: 0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', '```', '',
      'Volejte [Petr](a-contact:?name=Petr&objectId=0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b).',
    ].join('\n')
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(blocks(result)[0]).toEqual(contactNode({ name: 'Jana', directoryId: 'd1', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }))
    expect(blocks(result)[1]!.content![1]).toEqual(inlineContactNode({ name: 'Petr', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }))
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
    expect(read(editor, 'Volejte [Petr](a-contact:?name=Petr).', true).errors).toEqual([])
  })

  it('reads plain scalars of string fields exactly as written, without number or boolean typing', () => {
    const editor = mount()
    const markdown = '```a-contact\n- name: 2026\n  title: 1.50\n  organization: true\n  phones: [+420777123456, 0042 1]\n  emails:\n    - 123456@x.cz\n  internalUser:\n    id: 007\n```'
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(blocks(result)[0]).toEqual(contactNode({ name: '2026', title: '1.50', organization: 'true', phones: ['+420777123456', '0042 1'], emails: ['123456@x.cz'], internalUser: { id: '007' } }))
    // The writer still quotes them, so its own output round-trips byte for byte.
    const written = serializeMarkdownContent(editor, result.document!)
    expect(written).toBe('```a-contact\n- name: "2026"\n  title: "1.50"\n  organization: "true"\n  phones: ["+420777123456", 0042 1]\n  emails: [123456@x.cz]\n  internalUser:\n    id: "007"\n```')
    expect(serializeMarkdownContent(editor, read(editor, written, true).document!)).toBe(written)
    // Fields that are not strings keep strict typing.
    expect(read(editor, '```a-contact\n- name: Jana\n  kind: 1\n```', true).errors).toHaveLength(1)
  })

  it('keeps the plugin validators: limits and fields fail in strict mode with a position and stay code or text in lenient mode', () => {
    const editor = mount()
    const markdown = [
      'Úvod', '', '```a-contact', '- name: Jana', '  fax: 123', '```', '', '```a-contact', '- name: Jana', '  emails: [not-an-email]', '```', '',
      '> Text [Jana](a-contact:?name=Jana&fax=1) a [x](a-contact:?title=Bez%20jména).', '', '```a-contact', '- name: Jana', '  title: [a', '```',
    ].join('\n')
    const strict = read(editor, markdown, true)
    expect(strict.document).toBeUndefined()
    expect(strict.errors.map(({ line, column, construct }) => [line, column, construct])).toEqual([[3, 1, 'a-contact'], [8, 1, 'a-contact'], [13, 8, 'a-contact'], [13, 45, 'a-contact'], [15, 1, 'a-contact']])
    expect(strict.errors[0]!.message).toBe('Neplatná komponenta a-contact: Neznámý klíč fax.')
    expect(strict.errors[1]!.message).toBe('Neplatná komponenta a-contact: Zkontrolujte e-mailové adresy.')
    expect(strict.errors[4]!.message).toMatch(/Řádek 2 těla/)
    const lenient = read(editor, markdown)
    expect(lenient.errors).toEqual([])
    expect(lenient.warnings).toHaveLength(5)
    expect(blocks(lenient).map(node => [node.type, node.attrs?.language])).toEqual([['paragraph', undefined], ['codeBlock', 'a-contact'], ['codeBlock', 'a-contact'], ['blockquote', undefined], ['codeBlock', 'a-contact']])
    expect(blocks(lenient)[3]!.content![0]!.content).toEqual([{ type: 'text', text: 'Text [Jana](a-contact:?name=Jana&fax=1) a [x](a-contact:?title=Bez%20jména).' }])
    // An invalid fence read leniently is a code block and stays one: it is written in the reserved `a-code` form.
    expect(serializeMarkdownContent(editor, lenient.document!)).toContain('```a-code a-contact\n- name: Jana\n  fax: 123\n```')
  })

  it('splits a fence of several people as the server does (MarkdownDialectTests): an indented list too, each item validated on its own', () => {
    const editor = mount()
    const people = (markdown: string) => blocks(read(editor, markdown)).map(node => node.type === 'contactCard' ? node.attrs!.data.contacts.map((c: MarkdownContact) => c.name).join() : node.type)
    expect(people('```a-contact\n  - name: Jana\n  - name: Petr\n```')).toEqual(['Jana', 'Petr'])
    const invalid = '```a-contact\n- name: Jana\n- name: Petr\n  fax: 1\n- name: Eva\n```'
    expect(people(invalid)).toEqual(['Jana', 'codeBlock', 'Eva'])
    // Each part's error points at its `-` line, the first's too (after a comment).
    expect(read(editor, invalid, true).errors.map(({ line, message }) => [line, message])).toEqual([[3, 'Neplatná komponenta a-contact: Neznámý klíč fax.']])
    expect(read(editor, '```a-contact\n# lidé\n- name: Jana\n  fax: 1\n- name: Petr\n```', true).errors.map(({ line }) => line)).toEqual([3])
    const many = Array.from({ length: 101 }, (_, i) => `- name: K${i}`).join('\n')
    expect(people(`\`\`\`a-contact\n${many}\n\`\`\``)).toHaveLength(101)
    expect(people('```a-contact\nlidé:\n- name: Jana\n- name: Petr\n```')).toEqual(['codeBlock'])
  })
})

describe('a-chart', () => {
  it('writes and reads a chart without a source', () => {
    const editor = mount([chartNode({ ...gridChart, kind: 'bar', title: '' })])
    const markdown = '```a-chart\n---\n| Období | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```'
    expect(markdownOf(editor)).toBe(markdown)
    const result = read(editor, markdown, true)
    expect(blocks(result)).toEqual(editor.getJSON().content)
    const titled = mount([chartNode(gridChart)])
    expect(markdownOf(titled)).toBe('```a-chart\ntitle: Tržby 2026\nkind: line\n---\n| Období | Tržby |\n| --- | --- |\n| Leden | 12,5 |\n| Únor | 18 |\n```')
  })

  it('reads a number-like title and table id as text, and keeps ranges strictly typed', () => {
    const editor = mount()
    const markdown = '| a | b |\n| --- | --- |\n| x | 1 |\n\n{#2026}\n\n```a-chart\ntitle: 2026\nsource:\n  table: 2026\n  rows: 1-2\n  columns: 1-2\n---\n| a | b |\n| --- | --- |\n| x | 1 |\n```'
    const result = read(editor, markdown, true)
    expect(result).toMatchObject({ errors: [], warnings: [] })
    expect(blocks(result)[0]!.attrs!.chartRef).toBe('2026')
    expect(blocks(result)[1]!.attrs!.data).toMatchObject({ title: '2026', source: { tableId: '2026' } })
    const written = markdown.replace('title: 2026', 'title: "2026"').replace('table: 2026', 'table: "2026"')
    expect(serializeMarkdownContent(editor, result.document!)).toBe(written)
    expect(serializeMarkdownContent(editor, read(editor, written, true).document!)).toBe(written)
    expect(read(editor, markdown.replace('rows: 1-2', 'rows: 2'), true).errors.map(error => error.message)).toEqual(['Neplatná komponenta a-chart: Neplatný odkaz na tabulku.'])
  })

  it('escapes |, \\ and line breaks in grid cells', () => {
    const grid = [['Kategorie', 'A|B'], ['C:\\data\\n', '1'], ['dva\nřádky', '2']]
    const editor = mount([chartNode({ ...gridChart, grid })])
    const markdown = markdownOf(editor)
    expect(markdown).toContain('| Kategorie | A\\|B |\n| --- | --- |\n| C:\\\\data\\\\n | 1 |\n| dva\\nřádky | 2 |')
    const result = read(editor, markdown, true)
    expect((blocks(result)[0]!.attrs!.data as ChartData).grid).toEqual(grid)
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
  })

  it('writes the linked table id and the source rectangle, and reads them back with new cell ids', () => {
    const editor = mount([salesTable, chartNode(linkedChart)])
    const markdown = [
      '| Pobočka | Období | Tržby |', '| --- | --- | --- |', '| Brno | Leden | 12,5 |', '| Praha | Únor | 18 |', '', '{#tabulka-trzby}', '',
      '```a-chart', 'title: Tržby 2026', 'kind: line', 'source:', '  table: tabulka-trzby', '  rows: 1-3', '  columns: 2-3', '---',
      '| Období | Tržby |', '| --- | --- |', '| Leden | 12,5 |', '| Únor | 18 |', '```',
    ].join('\n')
    expect(markdownOf(editor)).toBe(markdown)
    const result = read(editor, markdown, true)
    expect(result).toMatchObject({ errors: [], warnings: [] })
    const [table, chart] = blocks(result) as [JSONContent, JSONContent]
    expect(table.attrs!.chartRef).toBe('tabulka-trzby')
    const ids = table.content!.map(row => row.content!.map(item => item.attrs!.chartRef as string))
    expect(new Set(ids.flat()).size).toBe(9)
    expect((chart.attrs!.data as ChartData).source).toEqual({ tableId: 'tabulka-trzby', cells: ids.map(row => row.slice(1)) })
    expect(blocks(result).map(withoutCellIds)).toEqual(editor.getJSON().content!.map(withoutCellIds))
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
  })

  it('takes the values of the linked table over the grid, with a warning', () => {
    const editor = mount()
    const markdown = '| Měsíc | Kusy |\n| --- | --- |\n| Leden | 7 |\n\n{#t1}\n\n```a-chart\nsource:\n  table: t1\n  rows: 1-2\n  columns: 1-2\n---\n| Měsíc | Kusy |\n| --- | --- |\n| Leden | 99 |\n```'
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(result.warnings).toEqual([{ line: 7, column: 1, construct: 'a-chart', message: 'Mřížka grafu se liší od tabulky {#t1}; platí hodnoty tabulky.' }])
    expect((blocks(result)[1]!.attrs!.data as ChartData).grid).toEqual([['Měsíc', 'Kusy'], ['Leden', '7']])
    // Without a chart naming it, `{#id}` stays text; text that would name a linked table is escaped.
    expect(blocks(read(editor, '| a | b |\n| --- | --- |\n| x | 1 |\n\n{#t1}'))[1]).toMatchObject(paragraph('{#t1}'))
    const escaped = mount([salesTable, chartNode(linkedChart), paragraph('{#tabulka-trzby}')])
    expect(markdownOf(escaped)).toMatch(/```\n\n\\\{#tabulka-trzby\}$/)
  })

  it('refuses a missing table, a bad rectangle or invalid values in strict mode and keeps a code block in lenient mode', () => {
    const editor = mount()
    const fence = (header: string, rows = '| a | b |\n| --- | --- |\n| x | 1 |') => `\`\`\`a-chart\n${header}---\n${rows}\n\`\`\``
    for (const [markdown, message] of [
      [fence('source:\n  table: nikde\n  rows: 1-2\n  columns: 1-2\n'), 'Tabulka {#nikde} pro graf neexistuje.'],
      [`| a | b |\n| --- | --- |\n| x | 1 |\n\n{#t}\n\n${fence('source:\n  table: t\n  rows: 1-5\n  columns: 1-2\n')}`, 'Neplatný odkaz na tabulku.'],
      [fence('kind: pie\n', '| a | b |\n| --- | --- |\n| x | -1 |'), 'Koláčové a prstencové grafy potřebují nezáporné hodnoty.'],
      [fence('', '| a | b |\n| x | 1 |'), 'Graf potřebuje za řádkem --- mřížku jako tabulku Markdownu (záhlaví, oddělovač, řádky).'],
      [fence('colour: red\n'), 'Neznámý klíč colour.'],
    ] as const) {
      const strict = read(editor, markdown, true)
      expect(strict.errors.map(error => error.message), markdown).toEqual([`Neplatná komponenta a-chart: ${message}`])
      expect(blocks(read(editor, markdown)).at(-1)).toMatchObject({ type: 'codeBlock', attrs: { language: 'a-chart' } })
    }
  })
})

describe('reserved a- namespace', () => {
  it('keeps a code block whose language starts with a- as ```a-code <language>', () => {
    const code: JSONContent = { type: 'codeBlock', attrs: { language: 'a-chart' }, content: [{ type: 'text', text: 'title: x\n---' }] }
    const editor = mount([code])
    expect(markdownOf(editor)).toBe('```a-code a-chart\ntitle: x\n---\n```')
    const result = read(editor, markdownOf(editor), true)
    expect(result).toMatchObject({ errors: [], warnings: [] })
    expect(blocks(result)).toEqual(editor.getJSON().content)
    expect(blocks(read(editor, '```a-code mermaid\nA --> B\n```'))[0]).toMatchObject({ type: 'codeBlock', attrs: { language: 'mermaid' } })
  })

  it('reports unknown a- components, and without the plugin a-contact is unknown too', () => {
    const editor = mount([{ type: 'paragraph' }], [])
    const markdown = '```a-contact\n- name: Jana\n```\n\n```a-page\nid: 1\n```\n\nViz [stránku](a-page:42).'
    expect(read(editor, markdown, true).errors.map(({ line, column, message }) => [line, column, message])).toEqual([
      [1, 1, 'Neznámá komponenta a-contact.'], [5, 1, 'Neznámá komponenta a-page.'], [9, 5, 'Neznámá komponenta a-page.'],
    ])
    const lenient = read(editor, markdown)
    expect(blocks(lenient).map(node => node.attrs?.language ?? node.content?.[0]?.text)).toEqual(['a-contact', 'a-page', 'Viz [stránku](a-page:42).'])
    expect(serializeMarkdownContent(editor, lenient.document!)).toBe('```a-code a-contact\n- name: Jana\n```\n\n```a-code a-page\nid: 1\n```\n\nViz \\[stránku\\](a-page:42).')
  })
})

describe('a-table (§4.12)', () => {
  const td = (content: JSONContent[], attrs: Record<string, unknown> = {}, type = 'tableCell'): JSONContent => ({ type, attrs: { colspan: 1, rowspan: 1, colwidth: null, ...attrs }, content })
  const list: JSONContent = { type: 'bulletList', content: ['a', 'b'].map(text => ({ type: 'listItem', content: [paragraph(text)] })) }
  const table = (rows: JSONContent[][]): JSONContent => ({ type: 'table', content: rows.map(content => ({ type: 'tableRow', content })) })
  const comparable = (node: unknown): unknown => JSON.parse(JSON.stringify(node, (key, value) => (value === null || (key === 'colspan' || key === 'rowspan') && value === 1 ? undefined : value)))

  it('writes a table with a list in a cell as ```a-table and reads the same table back', () => {
    const rich = table([
      [td([paragraph('Fáze')], {}, 'tableHeader'), td([paragraph('Popis')], { textAlign: 'center', align: 'center' }, 'tableHeader')],
      [td([paragraph('Návrh')]), td([paragraph('Body:'), list])],
    ])
    const editor = mount([rich])
    const markdown = markdownOf(editor)
    expect(markdown).toBe([
      '```a-table', '- cells:', '    - header: true', '      content: Fáze', '    - header: true', '      align: center', '      content: Popis',
      '- cells:', '    - content: Návrh', '    - content: |-', '        Body:', '', '        - a', '        - b', '```',
    ].join('\n'))
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(comparable(blocks(result))).toEqual(comparable(editor.getJSON().content))
    expect(serializeMarkdownContent(editor, result.document!)).toBe(markdown)
  })

  it('keeps merged cells and a table nested in a list item', () => {
    const merged = table([[td([paragraph('Spojené')], { colspan: 2 }, 'tableHeader')], [td([paragraph('L')]), td([paragraph('P')])]])
    const editor = mount([{ type: 'bulletList', attrs: { tight: false }, content: [{ type: 'listItem', content: [paragraph('Položka'), merged] }] }])
    const markdown = markdownOf(editor)
    expect(markdown).toContain('  ```a-table\n  - cells:\n      - header: true\n        colspan: 2\n')
    const result = read(editor, markdown, true)
    expect(result.errors).toEqual([])
    expect(comparable(blocks(result))).toEqual(comparable(editor.getJSON().content))
  })

  it('reports invalid a-table data and keeps it as code', () => {
    const editor = mount()
    for (const [body, message] of [['- cells:\n    - colspan: 0', 'Data komponenty nemají očekávaný tvar.'], ['- rows: []', 'Neznámý klíč rows.'], ['- cells:\n    - align: justify', 'Data komponenty nemají očekávaný tvar.']]) {
      const markdown = '```a-table\n' + body + '\n```'
      expect(read(editor, markdown, true).errors.map(error => error.message), body).toEqual([`Neplatná komponenta a-table: ${message}`])
      expect(blocks(read(editor, markdown))[0]).toMatchObject({ type: 'codeBlock', attrs: { language: 'a-table' } })
    }
  })
})
