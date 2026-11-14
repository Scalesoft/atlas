// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, defineMarkdownEntityKind, type MarkdownPlugin, type MarkdownRichValue } from '../core'
import { describeMarkdownSchema, type MarkdownSchemaManifest } from '.'
import { createAttachmentsPlugin } from '../plugins/attachments'
import { createChartsPlugin } from '../plugins/charts'
import { createConcealedContentPlugin } from '../plugins/concealed'
import { createContactsPlugin } from '../plugins/contacts'
import { createDiagramsPlugin } from '../plugins/diagrams'
import { createEditorToolsPlugin } from '../plugins/editor-tools'
import { createEmojiPlugin } from '../plugins/emoji'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'
import { createMathPlugin } from '../plugins/math'
import { createOpenApiPlugin } from '../plugins/openapi'
import { createPagesPlugin } from '../plugins/pages'
import { createSecretsPlugin } from '../plugins/secrets'
import { createTocPlugin } from '../plugins/toc'
import { createYouTrackPlugin } from '../plugins/youtrack'

// The schema manifest of every built-in plugin is shipped with the npm package (@atlas/markdown/schema.json) and embedded
// in Atlas.Markdown.Sync, whose MarkdownDocumentValidator and collaboration content guard enforce it. This test keeps three
// generated files equal to what the editor does:
// - packages/markdown/schema/markdown-schema.json: describeMarkdownSchema of every built-in plugin;
// - tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/web-document.json: a document as the editor saves it, holding
//   every node, mark and component (the Sync validator must accept it);
// - …/document-schema/verdicts.json: the editor's accept/reject on generated variants of that document, opened through the
//   real editor (MarkdownDocumentValidatorCorpusTests requires the same verdicts, apart from its commented deliberate list).
// After a schema, constraint or validator change, regenerate all three and run the Sync tests:
//   ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/schema/manifest.test.ts
const regenerate = 'ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/schema/manifest.test.ts'
const update = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ATLAS_UPDATE_SCHEMA === '1'
// Node's fs and paths without their types (the test typecheck has no Node globals; jsdom replaces the global URL).
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as {
  existsSync(path: string): boolean
  readFileSync(path: string, encoding: 'utf8'): string
  writeFileSync(path: string, data: string): void
  mkdirSync(path: string, options: { recursive: true }): void
}
const { fileURLToPath } = await import(/* @vite-ignore */ `node:${'url'}`) as { fileURLToPath(url: string): string }
const local = (relative: string) => fileURLToPath(new URL(relative, import.meta.url).href)
const manifestPath = local('../../schema/markdown-schema.json')
const fixtures = '../../../../tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/'
const documentPath = local(`${fixtures}web-document.json`)
const verdictsPath = local(`${fixtures}verdicts.json`)

/** Every built-in plugin, as a host installs all of them (Atlas does). */
function builtinPlugins(): MarkdownPlugin[] {
  return [
    createAttachmentsPlugin(), createChartsPlugin(), createConcealedContentPlugin(), createContactsPlugin(), createDiagramsPlugin(),
    createEditorToolsPlugin(), createEmojiPlugin(), createHeadingAnchorsPlugin(), createMathPlugin(), createOpenApiPlugin(), createPagesPlugin(),
    createSecretsPlugin({ services: {
      discover: async () => 'hidden', requestAccess: async () => {}, reveal: async () => { throw new Error('unused') },
      copy: async () => {}, subscribeRevocation: () => () => {},
    } }),
    createTocPlugin(), createYouTrackPlugin({ client: { search: async () => [] } }),
  ]
}

/** Write the file when regenerating, else require it to equal `generated` (a checkout may use CRLF). */
function compareGenerated(path: string, generated: string, label: string): void {
  if (update) { fs.mkdirSync(path.slice(0, Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'))), { recursive: true }); fs.writeFileSync(path, generated); return }
  const stored = fs.existsSync(path) ? fs.readFileSync(path, 'utf8').replace(/\r\n/g, '\n') : ''
  expect(stored, `${label} no longer matches the editor. Regenerate it with \`${regenerate}\` and run the Sync tests.`).toBe(generated)
}

beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null) })
afterEach(() => { vi.restoreAllMocks(); document.body.replaceChildren() })

describe('schema manifest', () => {
  it('matches the checked-in manifest of the built-in plugins', () => {
    const manifest = describeMarkdownSchema(builtinPlugins())
    compareGenerated(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'src/Atlas.Markdown/schema/markdown-schema.json')
  })

  it('declares a constraint for every attribute and describes every structured validator', () => {
    const manifest = describeMarkdownSchema(builtinPlugins())
    const attributes = [...manifest.nodes.map(node => ['nodes', node] as const), ...manifest.marks.map(mark => ['marks', mark] as const)]
      .flatMap(([kind, type]) => type.attrs.map(attribute => ({ key: `${kind}:${type.name}:${attribute.name}`, attribute })))
    expect(attributes.filter(({ attribute }) => attribute.constraint.kind === 'unconstrained').map(({ key }) => key),
      'Declare the rule next to the validator (defineMarkdownAttributeValidator) or in schema/builtin.ts').toEqual([])
    const named = new Set(attributes.flatMap(({ attribute }) => attribute.constraint.kind === 'structured' ? [attribute.constraint.validator] : []))
    expect([...named].filter(name => !manifest.validators[name])).toEqual([])
    expect(manifest.plugins.map(plugin => plugin.id)).toEqual(['attachments', 'charts', 'concealed', 'contacts', 'diagrams', 'emoji', 'heading-anchors',
      'math', 'openapi', 'pages', 'secrets', 'toc', 'youtrack'])
  })

  it('names the kind of entity of every inline atom, and the patterns its recognizers match', () => {
    const manifest = describeMarkdownSchema(builtinPlugins())
    expect(manifest.nodes.filter(node => node.owner && node.inline && !node.content).map(node => node.name)
      .filter(name => !manifest.entities.some(entity => entity.inline === name)),
    'Declare the kind with defineMarkdownEntityKind in the plugin that owns the node').toEqual([])
    expect(manifest.recognizers.filter(recognizer => !manifest.patterns[recognizer.pattern])).toEqual([])
    expect(new RegExp(`^(?:${manifest.patterns['issue-key']})$`, 'u').test('PX2-1235')).toBe(true)
    // \d, \w, \s and \b mean ASCII in JavaScript (flag u) but any Unicode digit, letter or space in .NET: „PX2-1٣“ would be a key on the server only.
    expect(Object.values(manifest.patterns).filter(source => /\\[dDwWsSbB]/u.test(source.replace(/\\\\/gu, '')))).toEqual([])
    for (const source of [String.raw`[1-9]\d{0,6}`, String.raw`\D`, String.raw`\w+`, String.raw`[\s,]`, String.raw`\bIN\B`, String.raw`\\\d`]) {
      expect(() => defineMarkdownEntityKind({ kind: 'issue', inline: 'youtrackIssue', patterns: { key: source } }), source).toThrow(TypeError)
    }
    expect(defineMarkdownEntityKind({ kind: 'issue', inline: 'youtrackIssue', patterns: { key: String.raw`C:\\d[0-9]` } }).patterns)
      .toEqual({ key: String.raw`C:\\d[0-9]` })
    expect(() => describeMarkdownSchema([{ ...createMathPlugin(), entities: [{ kind: 'math', inline: 'mathBlock' }] }]))
      .toThrow('Entity kind math names mathBlock, no inline atom of plugin math.')
    expect(() => describeMarkdownSchema([createMathPlugin(), { ...createEmojiPlugin(), entities: [{ kind: 'math', inline: 'emoji' }] }]))
      .toThrow('Duplicate entity kind: math')
  })

  it('follows the host configuration: profile, image policy and plugin set', () => {
    const narrow = describeMarkdownSchema([createMathPlugin()], { resourcePolicy: { imageSources: { allowDataImages: false } } })
    expect(narrow.plugins).toEqual([{ id: 'math', version: 1, migratesFrom: [] }])
    expect(narrow.nodes.map(node => node.name)).toContain('mathInline')
    expect(narrow.nodes.map(node => node.name)).not.toContain('chart')
    expect(narrow.nodes.find(node => node.name === 'image')!.attrs.find(attribute => attribute.name === 'src')!.constraint)
      .toEqual({ kind: 'url', policy: 'image', nullable: true, imageSources: { allowDataImages: false } })
    const safe = describeMarkdownSchema([], { profile: 'markdown-safe' })
    expect(safe.schemaRevision).toBe(1)
    expect(safe.nodes.map(node => node.name)).not.toContain('callout')
  })

  it('keeps the web document fixture: every node and mark as the editor saves them', () => {
    const json = saved(webDocument())
    const manifest = describeMarkdownSchema(builtinPlugins())
    const used = new Set<string>()
    const visit = (node: { type?: string; content?: unknown[]; marks?: { type: string }[] }) => {
      used.add(node.type!)
      node.marks?.forEach(mark => used.add(mark.type))
      ;(node.content as typeof node[] | undefined)?.forEach(visit)
    }
    visit((JSON.parse(json) as MarkdownRichValue).document as Parameters<typeof visit>[0])
    expect([...manifest.nodes, ...manifest.marks].map(item => item.name).filter(name => !used.has(name)),
      'Add the new node or mark to webDocument() in this test').toEqual([])
    compareGenerated(documentPath, json, 'tests/Atlas.Markdown.Sync.Tests/Fixtures/document-schema/web-document.json')
  })

  it('keeps the editor\'s verdict on every corpus variant (MarkdownDocumentValidatorCorpusTests must agree)', () => {
    const generated = buildVerdicts(describeMarkdownSchema(builtinPlugins()))
    if (update) { fs.writeFileSync(verdictsPath, generated); return }
    const stored = fs.existsSync(verdictsPath) ? fs.readFileSync(verdictsPath, 'utf8') : ''
    // Name the cases that changed (a whole-file diff is unreadable). The error text is for the reader only: an engine message
    // may vary with the Node version, the case and the verdict may not.
    const byId = (text: string) => new Map((text.trim() ? JSON.parse(text) as VerdictCase[] : [])
      .map(({ editorError: _, ...item }) => [item.id, JSON.stringify(item)]))
    const [before, after] = [byId(stored), byId(generated)]
    const changed = [...new Set([...before.keys(), ...after.keys()])].filter(id => before.get(id) !== after.get(id))
    expect(changed.slice(0, 30), `verdicts.json no longer matches the editor (${changed.length} cases differ). Regenerate it with \`${regenerate}\` `
      + 'and run the Sync tests.').toEqual([])
  }, 600_000)
})

// ---------------------------------------------------------------------------------------------------------------------------
// The web document and the editor's open path.

const presentation = { width: 60, align: 'left', caption: 'Obrazovka přihlášení', rotate: 90, flip: true, brightness: 110, contrast: 95,
  saturation: 100, filter: 'grayscale', cropLeft: 5, cropRight: 10, cropTop: 0, cropBottom: 20, naturalWidth: 1280, naturalHeight: 720 }
const attachmentId = '0f8fad5b-d9cb-469f-a165-70867728950e'
const secretId = '5b2f6a3c-1d4e-4f5a-8b6c-7d8e9f0a1b2c'
const text = (value: string, marks?: unknown[]) => ({ type: 'text', text: value, ...(marks ? { marks } : {}) })
const paragraph = (...content: unknown[]) => ({ type: 'paragraph', ...(content.length ? { content } : {}) })
const cell = (type: string, value: string, chartRef: string, attrs: Record<string, unknown> = {}) =>
  ({ type, attrs: { chartRef, ...attrs }, content: [paragraph(text(value))] })

/** What an author builds in the editor: every node, mark and component of the built-in plugins. */
const webDocument = () => ({
  format: 'atlas-rich', version: 1, plugins: describeMarkdownSchema(builtinPlugins()).plugins.map(({ id, version }) => ({ id, version })),
  document: { type: 'doc', content: [
    { type: 'heading', attrs: { level: 1, headingId: 'uvod' }, content: [text('Úvod')] },
    { type: 'paragraph', attrs: { textAlign: 'center' }, content: [
      text('Tučně', [{ type: 'bold' }]), text(' '), text('kurzíva', [{ type: 'italic' }]), text(' '),
      text('odkaz', [{ type: 'link', attrs: { href: 'https://example.com/a?b=1', target: '_blank', rel: 'noopener noreferrer', class: null, title: 'Titulek' } }]),
      text(' '), text('kód', [{ type: 'code' }]), text(' '), text('zvýraznění', [{ type: 'highlight' }]), text(' '),
      text('podtržení', [{ type: 'underline' }]), text(' '), text('přeškrtnutí', [{ type: 'strike' }]), { type: 'hardBreak' },
      { type: 'emoji', attrs: { name: 'smile' } }, text(' '),
      { type: 'pageReference', attrs: { id: '7d3c5f7e-8a51-4f0e-9b3d-2d1f0c2b1a00', title: 'Příručky', url: '/#/pages/7d3c5f7e-8a51-4f0e-9b3d-2d1f0c2b1a00' } },
      text(' '), { type: 'youtrackIssue', attrs: { id: '2-123', key: 'ATL-42', title: 'Chyba', url: 'https://youtrack.example.com/issue/ATL-42', state: 'Open' } },
      text(' '), { type: 'mathInline', attrs: { data: { version: 1, expression: 'a^2+b^2' } } },
      text(' '), { type: 'contactInline', attrs: { data: { version: 1, contacts: [{ name: 'Jana Nováková', emails: ['jana@example.com'] }] } } },
      text(' '), { type: 'secretInlineReference', attrs: { secretId, field: 'username', fields: ['username', 'password'] } },
    ] },
    { type: 'heading', attrs: { level: 4, headingId: 'postup', textAlign: 'right' }, content: [text('Postup')] },
    { type: 'bulletList', content: [{ type: 'listItem', content: [paragraph(text('Bod')), { type: 'orderedList', attrs: { start: 3 }, content: [
      { type: 'listItem', content: [paragraph(text('Vnořený'))] }] }] }] },
    { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: true }, content: [paragraph(text('Hotovo'))] }] },
    { type: 'timedList', content: [{ type: 'timedItem', content: [paragraph(text('3. 11. 2026 17:19 — Zápis'))] }] },
    { type: 'blockquote', content: [paragraph(text('Citace'))] },
    { type: 'codeBlock', attrs: { language: 'typescript' }, content: [text('const a = 1;')] },
    { type: 'horizontalRule' },
    { type: 'image', attrs: { src: `/api/attachments/${attachmentId}/content`, alt: 'Přihlášení', title: null, presentation } },
    { type: 'table', attrs: { chartRef: 'tbl-1' }, content: [
      { type: 'tableRow', attrs: { chartRef: 'row-1' }, content: [cell('tableHeader', 'Měsíc', 'c-1', { colwidth: [160] }), cell('tableHeader', 'Počet', 'c-2')] },
      { type: 'tableRow', attrs: { chartRef: 'row-2', height: 48 }, content: [cell('tableCell', 'Leden', 'c-3', { colwidth: [160], backgroundColor: 'green' }),
        cell('tableCell', '12', 'c-4', { textAlign: 'right' })] },
      { type: 'tableRow', attrs: { chartRef: 'row-3' }, content: [cell('tableCell', 'Únor', 'c-5', { colwidth: [160] }), cell('tableCell', '18', 'c-6')] },
    ] },
    { type: 'chart', attrs: { data: { version: 1, title: 'Počty', kind: 'bar', grid: [['Měsíc', 'Počet'], ['Leden', '12'], ['Únor', '18']],
      source: { tableId: 'tbl-1', cells: [['c-1', 'c-2'], ['c-3', 'c-4'], ['c-5', 'c-6']] }, sourceStatus: 'linked' } } },
    { type: 'callout', attrs: { variant: 'warning' }, content: [paragraph(text('Pozor'))] },
    { type: 'concealedContent', attrs: { reveal: 'button', reconcealAfterMs: null, completeAtPercent: 80 }, content: [paragraph(text('Tajenka'))] },
    { type: 'attachment', attrs: { data: { version: 1, name: 'Příručka.pdf', url: `/api/attachments/${attachmentId}/download`, size: 52344,
      mimeType: 'application/pdf', description: 'Původní PDF' } } },
    { type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Petr Svoboda', title: 'Správce', organization: 'Modelsoft', phones: ['+420 123 456 789'],
      emails: ['petr@example.com'], website: 'https://example.com', kind: 'internal', internalUser: { id: 'user-1', name: 'Petr Svoboda' } }] } } },
    { type: 'diagram', attrs: { data: { version: 1, source: 'flowchart LR\n  A --> B', caption: 'Tok' } } },
    { type: 'mathBlock', attrs: { data: { version: 1, expression: 'E = mc^2' } } },
    { type: 'tableOfContents', attrs: { data: { version: 1, title: 'Obsah', minLevel: 1, maxLevel: 4 } } },
    { type: 'openapi', attrs: { data: { version: 1, filename: 'api.yaml', mode: 'reference', uploadedBy: 'Jana Nováková',
      source: 'openapi: 3.1.0\ninfo:\n  title: Ukázka\n  version: \'1.0\'\npaths:\n  /ping:\n    get:\n      responses:\n        \'200\':\n          description: OK\n' } } },
    { type: 'secretReference', attrs: { secretId, field: 'password', showDetails: true, autoHideSeconds: 30 } },
    paragraph(),
  ] },
}) as unknown as MarkdownRichValue

/** The document as the editor saves it: opened (which validates it) and read back. */
function saved(value: MarkdownRichValue): string {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins: builtinPlugins() })
  try {
    controller.attachView(document.createElement('div'), { ariaLabel: 'Webový dokument' })
    return `${JSON.stringify(controller.getValue(), null, 2)}\n`
  } finally { controller.destroy() }
}

// ---------------------------------------------------------------------------------------------------------------------------
// The verdict corpus: variants of the web document's nodes, marks and component data, each opened through the editor's real
// load path (parse, then the controller with every built-in plugin: plugin migrations, envelope and plugin requirement checks,
// Node.check with the attribute validators, the built-in document validation, then the view). The variants are generated from
// the manifest and the web document's sample values, so a new attribute or data field is probed without listing it here.

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }
type JsonObject = { [key: string]: Json }
type VerdictCase = { id: string; description?: string; document: Json; editorAccepts: boolean; editorError?: string }

const absent = Symbol('absent')

/**
 * Long values stay short in the fixture: {"$string": prefix, "pad": c, "length": n} is prefix padded with c to n characters and
 * {"$array": item, "count": n} is n copies of item. Both this test and the Sync test expand them before validating.
 */
const longString = (prefix: string, pad: string, length: number): Json => ({ $string: prefix, pad, length })
const repeated = (item: Json, count: number): Json => ({ $array: item, count })
function expand(value: Json): Json {
  if (Array.isArray(value)) return value.map(expand)
  if (value === null || typeof value !== 'object') return value
  if (typeof value.$string === 'string') return value.$string + (value.pad as string).repeat((value.length as number) - value.$string.length)
  if ('$array' in value) return Array.from({ length: value.count as number }, () => expand(value.$array))
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, expand(item)]))
}

function editorVerdict(document: Json): { editorAccepts: boolean; editorError?: string } {
  let controller: ReturnType<typeof createMarkdownEditorController> | undefined
  try {
    controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: JSON.parse(JSON.stringify(expand(document))) },
      plugins: builtinPlugins() })
    controller.attachView(window.document.createElement('div'), { ariaLabel: 'Korpus' })
    return { editorAccepts: true }
  } catch (error) {
    const parts: string[] = []
    for (let item = error as { message?: string; cause?: unknown } | undefined; item; item = item.cause as typeof item) parts.push(String(item.message ?? item))
    return { editorAccepts: false, editorError: parts.join(' <- ').slice(0, 300) }
  } finally {
    try { controller?.destroy() } catch { /* a half-built controller */ }
  }
}

const numbers = [-1, 0, 0.5, 1, 6, 7, 9, 10, 23, 24, 90, 91, 100, 101, 200, 201, 270, 1000, 1001, 2000, 2001, 3600, 3601, 10000, 10001, 600000, 600001,
  1000000, 1000001, 9007199254740991, 9007199254740992]
/** For an attribute without a sample value or default type (null default): a few of each kind. */
const someNumbers = [-1, 0, 1, 1.5, 100, 1001]
const texts: [string, string][] = [['empty', ''], ['blank', '   '], ['padded', ' x '], ['newline', 'a\nb'], ['carriage-return', 'a\rb'], ['tab', 'a\tb'],
  ['control', 'a\u0001b'], ['delete', 'a\u007fb'], ['nbsp', ' '], ['unicode', 'Žluťoučký kůň 🐎'], ['markup', '<b>x</b>']]
const urls: [string, string][] = [['https', 'https://example.com/a?b=1#c'], ['http', 'http://example.com'], ['relative-path', '/api/x'],
  ['relative', 'x/y'], ['protocol-relative', '//example.com/x'], ['credentials', 'https://user:pw@example.com/'],
  ['username', 'https://user@example.com/'], ['empty-credentials', 'https://:@example.com/'], ['javascript', 'javascript:alert(1)'],
  ['javascript-upper', 'JAVASCRIPT:alert(1)'], ['javascript-spaced', ' javascript:alert(1)'], ['javascript-split', 'java\nscript:alert(1)'],
  ['javascript-ideographic-space', '　javascript:alert(1)'], ['javascript-nbsp', ' javascript:alert(1)'],
  ['vbscript', 'vbscript:x'], ['data-html', 'data:text/html,<b>x</b>'], ['data-image', 'data:image/png;base64,iVBORw0KGgo='],
  ['data-svg', 'data:image/svg+xml;base64,PHN2Zz4='], ['mailto', 'mailto:a@example.com'], ['tel', 'tel:+420123'], ['ftp', 'ftp://example.com/a'],
  ['file', 'file:///etc/passwd'], ['file-share', 'file://server/share/a.docx'], ['smb', 'smb://server/share'],
  ['onenote', 'onenote:https://example.com/a.one'], ['blob', 'blob:https://example.com/0f8fad5b'], ['no-host', 'https://'], ['space-host', 'https://exa mple.com/'],
  ['ipv6', 'https://[::1]:8080/'], ['bad-port', 'https://example.com:99999/'], ['backslashes', '\\\\server\\share'], ['custom-scheme', 'atlas:page/1'],
  ['fragment', '#section']]
const chartNumbers: [string, string][] = [['grouped', '1 000'], ['decimal-comma', '1,5'], ['decimal-point', '1.5'], ['negative', '-3'],
  ['plus', '+3'], ['exponent', '1e3'], ['word', 'abc'], ['blank-cell', ' '], ['nbsp-grouped', '1 000'], ['bad-grouping', '1 00']]
/** Values the editor's enumerations use, tried on every attribute holding a lowercase word. */
const words = ['left', 'center', 'right', 'justify', 'start', 'accent', 'green', 'amber', 'red', 'blue', 'note', 'tip', 'warning', 'important',
  'caution', 'info', 'danger', 'scratch', 'button', 'hover', 'none', 'grayscale', 'sepia', 'invert', 'bar', 'line', 'pie', 'doughnut', 'area',
  'download', 'reference', 'interactive', 'auto', 'internal', 'external', 'linked', 'missing', 'invalid', 'title', 'path', 'username', 'password',
  'url', 'notes', '_blank', '_self', 'noopener', 'Left', 'CENTER']
const arrayLengths = [0, 2, 6, 7, 20, 21, 100, 101, 200, 201, 202]

function buildVerdicts(manifest: MarkdownSchemaManifest): string {
  const web = JSON.parse(saved(webDocument())) as { document: JsonObject }
  const nodes = new Map(manifest.nodes.map(node => [node.name, node]))
  const marksByName = new Map(manifest.marks.map(mark => [mark.name, mark]))
  const version = new Map(manifest.plugins.map(plugin => [plugin.id, plugin.version]))
  const clone = <T>(value: T): T => structuredClone(value)

  // The first occurrence of every node type and mark in the web document, reduced to its first child at each level.
  const samples = new Map<string, JsonObject>()
  const markSamples = new Map<string, JsonObject>()
  const minimal = (node: JsonObject): JsonObject => {
    const content = node.content as JsonObject[] | undefined
    return content?.length ? { ...node, content: content.slice(0, 1).map(minimal) } : node
  }
  // A value some node of the web document gives each attribute name, which types an attribute the sample leaves null.
  const seen = new Map<string, Json>()
  const collect = (node: JsonObject) => {
    if (!samples.has(node.type as string)) samples.set(node.type as string, minimal(clone(node)))
    for (const [name, value] of Object.entries((node.attrs as JsonObject | undefined) ?? {})) if (value !== null && !seen.has(name)) seen.set(name, value)
    for (const mark of (node.marks as JsonObject[] | undefined) ?? []) if (!markSamples.has(mark.type as string)) markSamples.set(mark.type as string, mark)
    for (const child of (node.content as JsonObject[] | undefined) ?? []) collect(child)
  }
  collect(web.document)
  // Null attributes are the default: leaving them out keeps the fixture short (a probe still sets null where it matters).
  const dropNulls = (node: JsonObject) => {
    const attrs = node.attrs as JsonObject | undefined
    if (attrs) for (const key of Object.keys(attrs)) if (attrs[key] === null) delete attrs[key]
    for (const child of (node.content as JsonObject[] | undefined) ?? []) dropNulls(child)
  }
  for (const sample of samples.values()) dropNulls(sample)
  for (const mark of markSamples.values()) dropNulls(mark)
  // Optional component fields the web document leaves out, so their probes run too.
  const card = samples.get('contactCard')!
  Object.assign(((card.attrs as JsonObject).data as { contacts: JsonObject[] }).contacts[0], { department: 'Vývoj', address: 'Praha 1',
    directoryId: 'dir-1', username: 'petr', photo: 'https://example.com/p.png' })
  Object.assign((((card.attrs as JsonObject).data as { contacts: JsonObject[] }).contacts[0].internalUser as JsonObject), { photo: '/a/p.png' })
  // Numeric attributes whose default is null, so they get the numeric boundary probes.
  Object.assign(samples.get('tableRow')!.attrs as JsonObject, { height: 48 })
  Object.assign(samples.get('concealedContent')!.attrs as JsonObject, { reconcealAfterMs: 5000 })
  Object.assign(samples.get('image')!.attrs as JsonObject, { width: 640, height: 360 })

  // The smallest valid document holding a node: inline nodes in a paragraph, list items, rows and cells in their containers.
  const wrap = (node: JsonObject): JsonObject => {
    const type = node.type as string
    if (nodes.get(type)?.inline) return { type: 'paragraph', content: [node] }
    if (type === 'listItem') return { type: 'bulletList', content: [node] }
    if (type === 'taskItem') return { type: 'taskList', content: [node] }
    if (type === 'timedItem') return { type: 'timedList', content: [node] }
    if (type === 'tableRow') return { type: 'table', content: [node] }
    if (type === 'tableCell' || type === 'tableHeader') return wrap({ type: 'tableRow', content: [node] })
    return node
  }
  // Plugin requirements for what the document uses (node owners and owners of attributes it sets).
  const required = (node: Json, into = new Set<string>()): Set<string> => {
    if (Array.isArray(node)) node.forEach(item => required(item, into))
    if (node === null || typeof node !== 'object' || Array.isArray(node)) return into
    const type = nodes.get(node.type as string)
    if (type?.owner) into.add(type.owner)
    for (const attribute of type?.attrs ?? []) if (attribute.owner && node.attrs && typeof node.attrs === 'object' && attribute.name in (node.attrs as JsonObject)) into.add(attribute.owner)
    required(node.content ?? null, into)
    return into
  }
  const envelope = (blocks: Json[], plugins?: Json[]): JsonObject => ({ format: 'atlas-rich', version: 1,
    plugins: plugins ?? [...required(blocks)].sort().map(id => ({ id, version: version.get(id)! })), document: { type: 'doc', content: blocks } })

  const cases: { id: string; description: string; document: Json }[] = []
  const ids = new Set<string>()
  const add = (id: string, description: string, document: Json) => {
    if (ids.has(id)) throw new Error(`Duplicate corpus case ${id}`)
    ids.add(id)
    cases.push({ id, description, document })
  }
  const addBlock = (id: string, description: string, node: JsonObject) => add(id, description, envelope([wrap(node)]))

  const setAt = (target: JsonObject, path: (string | number)[], value: Json | typeof absent) => {
    let owner = target as Record<string | number, Json>
    for (const key of path.slice(0, -1)) owner = (owner[key] ??= {}) as Record<string | number, Json>
    if (value === absent) delete owner[path.at(-1)!]
    else owner[path.at(-1)!] = value
  }
  const getAt = (target: Json, path: (string | number)[]): Json | undefined =>
    path.reduce<Json | undefined>((value, key) => (value as Record<string | number, Json> | null | undefined)?.[key], target)
  const label = (path: (string | number)[]) => path.map(key => (typeof key === 'number' ? `[${key}]` : `.${key}`)).join('').replace(/^\./, '')

  /** Probes one value of a sample (an attribute or a field nested in it) and recurses into objects and arrays. */
  const probe = (prefix: string, sample: JsonObject, path: (string | number)[], skip: number, defaultValue: unknown, depth = 0) => {
    const value = getAt(sample, path)
    const verdictWith = (next: Json) => {
      const node = clone(sample)
      setAt(node, path, next)
      return editorVerdict(envelope([wrap(node)])).editorAccepts
    }
    const variant = (name: string, description: string, next: Json | typeof absent) => {
      const node = clone(sample)
      setAt(node, path, next)
      addBlock(`${prefix}.${label(path.slice(skip))}/${name}`, `${prefix} ${label(path.slice(skip))} ${description}`, node)
    }
    variant('absent', 'left out', absent)
    for (const [name, next] of [['null', null], ['true', true], ['number', 0], ['text', 'x'], ['object', {}], ['array', []]] as [string, Json][])
      if (JSON.stringify(next) !== JSON.stringify(value)) variant(`type-${name}`, `= ${JSON.stringify(next)}`, next)
    const reference = value ?? defaultValue
    // Enumerations: a lowercase word where the editor refuses a made-up word.
    if (typeof reference === 'string' && /^[a-z]+$/.test(reference) && !verdictWith('zzzz'))
      for (const word of words) if (word !== reference) variant(`word-${word}`, `= "${word}"`, word)
    if (reference === null || reference === undefined) {
      for (const number of someNumbers) variant(`number-${number}`, `= ${number}`, number)
      for (const [name, text] of texts.slice(0, 5)) variant(`text-${name}`, `= ${JSON.stringify(text)}`, text)
    }
    if (typeof reference === 'number')
      for (const number of numbers) {
        // The editor's Markdown projection writes "#" level times: a heading level in the billions exhausts the heap.
        if (`${prefix}.${label(path)}` === 'nodes.heading.attrs.level' && number > 1_000_000) continue
        variant(`number-${number}`, `= ${number}`, number)
      }
    if (typeof reference === 'string') {
      for (const [name, text] of texts) variant(`text-${name}`, `= ${JSON.stringify(text)}`, text)
      if (/src|url|href|website|photo/i.test(String(path.at(-1)))) for (const [name, url] of urls) variant(`url-${name}`, `= ${JSON.stringify(url)}`, url)
      if (typeof value === 'string' && /^\d+$/.test(value)) for (const [name, text] of chartNumbers) variant(`chart-number-${name}`, `= ${JSON.stringify(text)}`, text)
      // The longest value the editor accepts, found by bisection, and one character more.
      const base = typeof value === 'string' ? value : ''
      const accepts = (pad: string, length: number) => verdictWith(base + pad.repeat(length - base.length))
      const pad = ['x', '1', '#'].find(candidate => accepts(candidate, base.length + 1))
      if (pad) {
        const cap = 1 << 20
        let [low, high] = [base.length + 1, base.length + 2]
        while (high <= cap && accepts(pad, high)) [low, high] = [high, high * 2]
        if (high > cap) variant(`length-${cap}`, `is ${cap} characters long`, longString(base, pad, cap))
        else {
          while (high - low > 1) { const middle = Math.floor((low + high) / 2); if (accepts(pad, middle)) low = middle; else high = middle }
          variant(`length-${low}`, `is ${low} characters long (the longest the editor accepts)`, longString(base, pad, low))
          variant(`length-${high}`, `is ${high} characters long`, longString(base, pad, high))
        }
      }
    }
    if (depth >= 5 || value === null || typeof value !== 'object') return
    if (Array.isArray(value)) {
      if (value.length === 0) return
      for (const length of arrayLengths) if (length !== value.length)
        variant(`items-${length}`, `has ${length} copies of its last item`, repeated(value.at(-1)!, length))
      for (const index of new Set([0, value.length - 1])) probe(prefix, sample, [...path, index], skip, undefined, depth + 1)
      return
    }
    variant('extra-key', 'has an unknown key', { ...value, 'x-unknown': 1 })
    for (const key of Object.keys(value)) probe(prefix, sample, [...path, key], skip, undefined, depth + 1)
  }

  // Node attributes, their component data and every nested field; an absent attribute is probed by its default's type.
  for (const [type, sample] of samples) {
    const declared = nodes.get(type)!
    if (type === 'doc') continue
    for (const attribute of declared.attrs) probe(`nodes.${type}`, sample, ['attrs', attribute.name], 0, seen.get(attribute.name) ?? attribute.default)
    if (declared.attrs.length) addBlock(`nodes.${type}.attrs/unknown`, `${type} with an unknown attribute`, { ...sample, attrs: { ...(sample.attrs as JsonObject), 'x-unknown': 1 } })
    addBlock(`nodes.${type}/valid`, `${type} as the editor saves it`, sample)
    addBlock(`nodes.${type}/unknown-field`, `${type} with an unknown node field`, { ...sample, data: 1 })
    addBlock(`nodes.${type}/attrs-array`, `${type} with attrs as an array`, { ...sample, attrs: [] })
    if (declared.text) {
      addBlock('nodes.text/empty', 'an empty text node', { type: 'text', text: '' })
      addBlock('nodes.text/missing', 'a text node without text', { type: 'text' })
      addBlock('nodes.text/number', 'a text node whose text is a number', { type: 'text', text: 1 })
      addBlock('nodes.text/attrs', 'a text node with attrs', { type: 'text', text: 'x', attrs: {} })
      continue
    }
    // Content expressions: no content, a text child in a block container, a paragraph in an inline container or leaf.
    if (declared.content) addBlock(`nodes.${type}.content/empty`, `${type} with no content`, { ...sample, content: [] })
    if (declared.content) addBlock(`nodes.${type}.content/missing`, `${type} without a content field`, Object.fromEntries(Object.entries(sample).filter(([key]) => key !== 'content')))
    addBlock(`nodes.${type}.content/text`, `${type} holding a bare text node`, { ...sample, content: [{ type: 'text', text: 'x' }] })
    addBlock(`nodes.${type}.content/paragraph`, `${type} holding a paragraph`, { ...sample, content: [{ type: 'paragraph' }] })
    addBlock(`nodes.${type}.content/not-array`, `${type} whose content is an object`, { ...sample, content: { type: 'paragraph' } })
    // Marks a node allows in its content, or refuses.
    if (/inline|text/.test(declared.content))
      for (const mark of manifest.marks)
        addBlock(`nodes.${type}.marks/${mark.name}`, `${type} holding text marked ${mark.name}${declared.marks.includes(mark.name) ? '' : ' (not allowed)'}`,
          { ...sample, content: [{ type: 'text', text: 'x', marks: [markSamples.get(mark.name) ?? { type: mark.name }] }] })
    if (declared.inline)
      for (const mark of ['bold', 'link'])
        addBlock(`nodes.${type}/marked-${mark}`, `${type} marked ${mark}`, { ...sample, marks: [markSamples.get(mark)!] })
  }
  addBlock('nodes/unknown-type', 'an unknown node type', { type: 'figure' })
  addBlock('nodes/missing-type', 'a node without a type', { content: [] })
  addBlock('nodes/doc-nested', 'a doc node as a block', { type: 'doc', content: [{ type: 'paragraph' }] })

  // Marks: attributes, unknown ones, and every ordered pair (duplicates and mutual exclusion).
  const marked = (marks: Json[]): JsonObject => ({ type: 'paragraph', content: [{ type: 'text', text: 'x', marks }] })
  for (const mark of manifest.marks) {
    const sample = markSamples.get(mark.name)!
    for (const attribute of mark.attrs) probe(`marks.${mark.name}`, marked([sample]), ['content', 0, 'marks', 0, 'attrs', attribute.name], 4,
      marksByName.get(mark.name)!.attrs.find(item => item.name === attribute.name)?.default)
    add(`marks.${mark.name}.attrs/unknown`, `${mark.name} mark with an unknown attribute`, envelope([marked([{ ...sample, attrs: { ...(sample.attrs as JsonObject), 'x-unknown': 1 } }])]))
    add(`marks.${mark.name}/unknown-field`, `${mark.name} mark with an unknown field`, envelope([marked([{ ...sample, data: 1 }])]))
    for (const other of manifest.marks)
      add(`marks.${mark.name}+${other.name}`, `text marked ${mark.name} then ${other.name}`, envelope([marked([sample, markSamples.get(other.name)!])]))
  }
  add('marks/unknown-type', 'an unknown mark type', envelope([marked([{ type: 'superscript' }])]))
  add('marks/not-array', 'marks as an object', envelope([marked({ type: 'bold' } as unknown as Json[])]))

  // Plugin requirements: each owned node without its plugin, each version, duplicates and unknown plugins.
  for (const plugin of manifest.plugins) {
    const owned = manifest.nodes.filter(node => node.owner === plugin.id || node.attrs.some(attribute => attribute.owner === plugin.id))
    for (const node of owned) {
      const document = envelope([wrap(samples.get(node.name)!)])
      document.plugins = (document.plugins as JsonObject[]).filter(item => item.id !== plugin.id)
      add(`plugins.${plugin.id}/without-for-${node.name}`, `${node.name} without the ${plugin.id} plugin requirement`, document)
    }
    const sample = wrap(samples.get(owned[0]?.name ?? 'paragraph')!)
    for (const number of new Set([0, ...plugin.migratesFrom, plugin.version, plugin.version + 1, plugin.version + 0.5]))
      add(`plugins.${plugin.id}/version-${number}`, `${plugin.id} plugin at version ${number}`, envelope([sample], [{ id: plugin.id, version: number }]))
    add(`plugins.${plugin.id}/version-text`, `${plugin.id} plugin version as text`, envelope([sample], [{ id: plugin.id, version: String(plugin.version) }]))
    add(`plugins.${plugin.id}/duplicate`, `${plugin.id} plugin required twice`, envelope([sample], [{ id: plugin.id, version: plugin.version }, { id: plugin.id, version: plugin.version }]))
    add(`plugins.${plugin.id}/extra-field`, `${plugin.id} plugin requirement with an unknown field`, envelope([sample], [{ id: plugin.id, version: plugin.version, x: 1 }]))
  }
  const paragraphOnly: Json[] = [{ type: 'paragraph' }]
  add('plugins/unknown', 'an unknown plugin requirement', envelope(paragraphOnly, [{ id: 'unknown-plugin', version: 1 }]))
  add('plugins/missing-id', 'a plugin requirement without an id', envelope(paragraphOnly, [{ version: 1 }]))
  add('plugins/not-array', 'plugins as an object', { ...envelope(paragraphOnly), plugins: {} })
  add('plugins/absent', 'a plain document without plugins', Object.fromEntries(Object.entries(envelope(paragraphOnly)).filter(([key]) => key !== 'plugins')))

  // The envelope.
  add('envelope/valid', 'a document with one empty paragraph', envelope(paragraphOnly))
  add('envelope/format', 'another format', { ...envelope(paragraphOnly), format: 'markdown' })
  add('envelope/version-2', 'envelope version 2', { ...envelope(paragraphOnly), version: 2 })
  add('envelope/extra-field', 'an unknown envelope field', { ...envelope(paragraphOnly), x: 1 })
  add('envelope/no-document', 'no document', Object.fromEntries(Object.entries(envelope(paragraphOnly)).filter(([key]) => key !== 'document')))
  add('envelope/document-paragraph', 'a paragraph as the document', { ...envelope(paragraphOnly), document: { type: 'paragraph' } })
  add('envelope/empty-doc', 'a doc without content', { ...envelope(paragraphOnly), document: { type: 'doc', content: [] } })
  add('envelope/doc-attrs', 'a doc with attrs', { ...envelope(paragraphOnly), document: { type: 'doc', attrs: { x: 1 }, content: paragraphOnly } })

  // Nesting depth.
  for (const depth of [10, 20, 28, 29, 30, 31, 32, 40, 63, 64, 65, 100]) {
    let node: JsonObject = { type: 'paragraph' }
    for (let level = 1; level < depth; level++) node = { type: 'blockquote', content: [node] }
    add(`structure.depth/${depth}`, `${depth} nested nodes below the doc`, envelope([node]))
  }

  // Table geometry and size (prosemirror-tables TableMap and the editor's dimension bounds).
  const tableCell = (attrs: JsonObject = {}): JsonObject => ({ type: 'tableCell', attrs, content: [{ type: 'paragraph' }] })
  const table = (...rows: JsonObject[][]): JsonObject => ({ type: 'table', content: rows.map(cells => ({ type: 'tableRow', content: cells })) })
  const tables: [string, string, JsonObject][] = [
    ['2x2', 'a full 2×2 table', table([tableCell(), tableCell()], [tableCell(), tableCell()])],
    ['hole', 'a row shorter than the others', table([tableCell(), tableCell()], [tableCell()])],
    ['empty-row', 'a row without cells', table([tableCell()], [])],
    ['colspan', 'a colspan covering the row', table([tableCell({ colspan: 2 })], [tableCell(), tableCell()])],
    ['colspan-overflow', 'a colspan wider than the other rows', table([tableCell({ colspan: 3 })], [tableCell(), tableCell()])],
    ['rowspan', 'a rowspan covering the next row', table([tableCell({ rowspan: 2 }), tableCell()], [tableCell()])],
    ['rowspan-collision', 'a rowspan the next row overlaps', table([tableCell({ rowspan: 2 }), tableCell()], [tableCell(), tableCell()])],
    ['rowspan-overlong', 'a rowspan past the last row', table([tableCell({ rowspan: 3 })])],
    ['colwidth-consistent', 'equal widths in a column', table([tableCell({ colwidth: [120] })], [tableCell({ colwidth: [120] })])],
    ['colwidth-mismatch', 'different widths in a column', table([tableCell({ colwidth: [120] })], [tableCell({ colwidth: [80] })])],
    ['colwidth-partial', 'a width on one cell of a column', table([tableCell({ colwidth: [120] })], [tableCell()])],
    ['colwidth-colspan', 'two widths on a cell spanning two columns', table([tableCell({ colspan: 2, colwidth: [100, 50] })], [tableCell(), tableCell()])],
    ['colwidth-colspan-short', 'one width on a cell spanning two columns', table([tableCell({ colspan: 2, colwidth: [100] })], [tableCell(), tableCell()])],
    ['colwidth-zero', 'a zero width', table([tableCell({ colwidth: [0] })])],
    ['header-cells', 'header and body cells mixed', table([{ ...tableCell(), type: 'tableHeader' }, tableCell()])],
    ['cells-1000', '1000 cells', { type: 'table', content: repeated({ type: 'tableRow', content: repeated(tableCell(), 10) }, 100) }],
    ['cells-1001', '1001 cells', { type: 'table', content: repeated({ type: 'tableRow', content: repeated(tableCell(), 13) }, 77) }],
    ['rows-1000', '1000 rows of one cell', { type: 'table', content: repeated({ type: 'tableRow', content: [tableCell()] }, 1000) }],
    ['rows-1001', '1001 rows of one cell', { type: 'table', content: repeated({ type: 'tableRow', content: [tableCell()] }, 1001) }],
    ['span-1001', 'a colspan of 1001', table([tableCell({ colspan: 1001 })])],
    ['span-area', 'a 40×40 merged cell', table([tableCell({ colspan: 40, rowspan: 40 })], ...Array.from({ length: 39 }, () => []))],
  ]
  for (const [id, description, node] of tables) add(`structure.table/${id}`, description, envelope([node]))
  add('structure.table/row-outside', 'a table row outside a table', envelope([{ type: 'tableRow', content: [tableCell()] }]))
  add('structure.list/item-outside', 'a list item outside a list', envelope([{ type: 'listItem', content: [{ type: 'paragraph' }] }]))
  add('structure.list/item-heading-first', 'a list item starting with a heading', envelope([{ type: 'bulletList', content: [{ type: 'listItem',
    content: [{ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'x' }] }] }] }]))
  add('structure.inline/top-level', 'inline text at the top level', envelope([{ type: 'text', text: 'x' }]))

  // Verdicts, reduced; one case per line keeps the fixture's diffs readable.
  const verdicts = cases.map(({ id, document }) => ({ id, document, ...editorVerdict(document) }))
  return `[\n${packCorpus(reduceCorpus(verdicts)).map(item => JSON.stringify(item)).join(',\n')}\n]\n`
}

/**
 * Keeps the corpus small without losing a rule: the full probe set tests each rule many times over. What stays is every case
 * beside an outcome change (the boundaries of numbers and array lengths), one case per outcome class of an unordered family
 * (text forms, enumeration words, URL kinds, wrong types) plus a fixed set of representatives for the forms a server rule might
 * treat differently from the editor, and everything else (absent, unknown, content, plugin, envelope and structure cases) as
 * generated. The deliberate-difference cases of the Sync test stay.
 */
function reduceCorpus(rows: VerdictCase[]): VerdictCase[] {
  const outcome = (row: VerdictCase) => `${row.editorAccepts}|${(row.editorError ?? '').replace(/\d+/g, '#')}`
  // Targets that differ only in an array index and header cells that share a cell's attributes, answering every variant alike, are one rule.
  const normalized = (target: string) => target.replace(/\[\d+\]/g, '[]').replace('nodes.tableHeader', 'nodes.tableCell')
  const profiles = new Map<string, Map<string, string>>()
  for (const row of rows) {
    const slash = row.id.lastIndexOf('/')
    if (slash < 0 || !/\[\d+\]|^nodes\.table(Header|Cell)\./.test(row.id.slice(0, slash))) continue
    const target = row.id.slice(0, slash)
    const byTarget = profiles.get(normalized(target)) ?? new Map<string, string>()
    byTarget.set(target, `${byTarget.get(target) ?? ''}${row.id.slice(slash)}:${outcome(row)};`)
    profiles.set(normalized(target), byTarget)
  }
  const repeats = new Set<string>()
  for (const byTarget of profiles.values()) {
    const seenProfiles = new Set<string>()
    for (const [target, profile] of byTarget) { if (seenProfiles.has(profile)) repeats.add(target); seenProfiles.add(profile) }
  }
  rows = rows.filter(row => !repeats.has(row.id.slice(0, Math.max(row.id.lastIndexOf('/'), 0))))
  const keep = new Set<string>()
  const families = new Map<string, VerdictCase[]>()
  const urlKinds: Record<string, string> = { https: 'web', http: 'web', credentials: 'credentials', username: 'credentials', 'empty-credentials': 'credentials',
    'relative-path': 'relative', relative: 'relative', 'protocol-relative': 'relative', fragment: 'relative', backslashes: 'relative',
    javascript: 'script', 'javascript-upper': 'script', vbscript: 'script', 'data-html': 'data', 'data-image': 'data', 'data-svg': 'data',
    'no-host': 'malformed', 'space-host': 'malformed', ipv6: 'malformed', 'bad-port': 'malformed' }
  const genericNodes = ['heading', 'image', 'table', 'chart', 'contactCard', 'callout']
  const byId = new Map(rows.map(row => [row.id, row]))
  const textProfiles = new Map<string, number>()
  const representativeTexts = new Set(['empty', 'newline', 'control'])
  const markPairs = ['bold+italic', 'italic+bold', 'link+bold', 'bold+link', 'strike+highlight', 'bold+bold', 'code+code', 'link+link',
    ...['bold', 'link', 'italic'].flatMap(other => [`code+${other}`, `${other}+code`])]
  for (const row of rows) {
    const slash = row.id.lastIndexOf('/')
    const [target, variant] = slash < 0 ? [row.id, ''] : [row.id.slice(0, slash), row.id.slice(slash + 1)]
    const family = /^(number|items|text|word|url|type)-/.exec(variant)?.[1] ?? (/^nodes\.\w+\.marks$/.test(target) ? 'marks' : undefined)
    if (/^marks\.\w+\+\w+$/.test(row.id)) { if (markPairs.includes(row.id.slice(6))) keep.add(row.id) }
    else if (target === 'structure.depth') { if (['63', '64', '65'].includes(variant)) keep.add(row.id) }
    else if (target === 'nodes.paragraph.marks' || (target === 'marks.link.attrs.href' && /^url-(javascript|vbscript|data)/.test(variant))) keep.add(row.id)
    // The same generic rule on every node or plugin: a few of each stand for all.
    else if (/^nodes\.(\w+)(\.attrs\/unknown|\/unknown-field|\/attrs-array)$/.test(row.id)) { if (genericNodes.includes(/^nodes\.(\w+)/.exec(row.id)![1])) keep.add(row.id) }
    else if (/^plugins\.([\w-]+)\/(duplicate|extra-field|version-text)$/.test(row.id)) { if (['attachments', 'charts'].includes(/^plugins\.([\w-]+)/.exec(row.id)![1])) keep.add(row.id) }
    else if (!family) keep.add(row.id)
    else families.set(`${target}|${family}`, [...(families.get(`${target}|${family}`) ?? []), row])
  }
  for (const [key, group] of families) {
    const family = key.split('|')[1]
    const nameOf = (row: VerdictCase) => row.id.slice(row.id.lastIndexOf('/') + 1).replace(/^[a-z]+-/, '')
    if (family === 'number' || family === 'items') {
      // Ordered: the ends and every case beside an outcome change.
      const ordered = [...group].sort((a, b) => Number(nameOf(a)) - Number(nameOf(b)))
      ordered.forEach((row, index) => {
        if (index === 0 || index === ordered.length - 1 || outcome(row) !== outcome(ordered[index - 1]) || outcome(row) !== outcome(ordered[index + 1])) keep.add(row.id)
      })
    } else if (family === 'marks') {
      // One accepted and one refused mark per node type.
      for (const accepted of [true, false]) { const first = group.find(row => row.editorAccepts === accepted); if (first) keep.add(first.id) }
    } else {
      // Unordered: one case per outcome class (per URL kind for URLs, per wrong JSON type for none), plus the fixed representatives;
      // every wrong type the editor still opens (a server rule is likely stricter there), and a null that answers unlike the absent value.
      const seenClasses = new Set<string>()
      // Text forms: a field whose outcomes over all forms repeat those of another is the same editor rule; its type cases stay.
      if (family === 'text') {
        const profile = group.map(row => nameOf(row) + ':' + outcome(row)).join(',')
        const count = textProfiles.get(profile) ?? 0
        textProfiles.set(profile, count + 1)
        if (count >= 1) continue
      }
      for (const row of group) {
        const name = nameOf(row)
        const kind = family === 'url' ? urlKinds[name] ?? 'scheme' : family === 'word' ? (['Left', 'CENTER'].includes(name) ? name : 'word') : family === 'type' || family === 'text' ? '' : name
        const classKey = `${kind}|${outcome(row)}`
        const keepType = name === 'number' || (row.editorAccepts && !(name === 'text' && rows.some(item => item.id.startsWith(key.split('|')[0] + '/text-'))))
          || (name === 'null' && outcome(row) !== outcome(byId.get(row.id.replace('type-null', 'absent')) ?? row))
        if (family === 'type' ? keepType : !seenClasses.has(classKey) || (family === 'text' && representativeTexts.has(name))) keep.add(row.id)
        seenClasses.add(classKey)
      }
    }
  }
  return rows.filter(row => keep.has(row.id)).map(({ id, document, editorAccepts, editorError }) =>
    ({ id, document, editorAccepts, ...(editorError ? { editorError: editorError.slice(0, 40) } : {}) }))
}

/**
 * The fixture's compact form: a case is its whole "document", or the "base" case it derives from (a stored document, by id) with
 * a "patch" of ["a/b/0/c", value] changes (the base is "nodes.<type>/valid" when the id starts "nodes.<type>." and no base is
 * named), where {"$absent": true} removes the key. Each probe changes one value of a sample, so patches are short; the Sync test
 * and applyPatch below expand them identically.
 */
const absentMarker: Json = { $absent: true }
function diffPatch(from: Json, to: Json, path: (string | number)[] = [], into: Json[] = []): Json[] {
  if (JSON.stringify(from) === JSON.stringify(to)) return into
  const plain = (value: Json): value is JsonObject => value !== null && typeof value === 'object' && !Array.isArray(value)
  if (plain(from) && plain(to)) {
    for (const key of Object.keys(from)) if (!(key in to)) into.push([[...path, key].join('/'), absentMarker])
    for (const key of Object.keys(to)) { if (key in from) diffPatch(from[key], to[key], [...path, key], into); else into.push([[...path, key].join('/'), to[key]]) }
  } else if (Array.isArray(from) && Array.isArray(to) && from.length === to.length) from.forEach((item, index) => diffPatch(item, to[index], [...path, index], into))
  else into.push([path.join('/'), to])
  return into
}
const plainMarker = (value: Json) => value !== null && typeof value === 'object' && !Array.isArray(value) && value.$absent === true
function applyPatch(base: Json, patch: Json[]): Json {
  const result = structuredClone(base)
  for (const [pointer, value] of patch as [string, Json][]) {
    const path = pointer.split('/')
    let owner = result as Record<string, Json>
    for (const key of path.slice(0, -1)) owner = owner[key] as Record<string, Json>
    if (plainMarker(value)) delete owner[path.at(-1)!]
    else owner[path.at(-1)!] = value
  }
  return result
}

function packCorpus(rows: VerdictCase[]): VerdictCase[] {
  // Bases are the cases of a whole node as the editor saves it, which every probe of that node differs from by one value.
  const bases = rows.filter(row => /\/valid$/.test(row.id) || row.id === 'envelope/valid')
  return rows.map(row => {
    const tail = { editorAccepts: row.editorAccepts, ...(row.editorError ? { editorError: row.editorError } : {}) }
    let best: { base: string; patch: Json[] } | undefined
    for (const base of bases) {
      if (bases.includes(row)) break
      const patch = diffPatch(base.document, row.document)
      if (!best || JSON.stringify(patch).length < JSON.stringify(best.patch).length) best = { base: base.id, patch }
    }
    if (!best || JSON.stringify(best.patch).length + 30 >= JSON.stringify(row.document).length) return { id: row.id, document: row.document, ...tail }
    expect(applyPatch(bases.find(base => base.id === best.base)!.document, best.patch), `${row.id} patch must rebuild its document`).toEqual(row.document)
    // A probe of "nodes.image..." derives from "nodes.image/valid", which is left implied.
    const implied = /^nodes\.(\w+)[./]/.exec(row.id)
    return { id: row.id, ...(implied && best.base === `nodes.${implied[1]}/valid` ? {} : { base: best.base }), patch: best.patch, ...tail } as unknown as VerdictCase
  })
}
