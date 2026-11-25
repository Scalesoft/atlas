// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import { prosemirrorJSONToYXmlFragment, yUndoPluginKey, yXmlFragmentToProseMirrorFragment } from '@tiptap/y-tiptap'
import { Slice, type Schema } from '@tiptap/pm/model'
import { EditorState } from '@tiptap/pm/state'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, migrateMarkdownValue, type MarkdownPlugin, type MarkdownRichValue } from '../core'
import { editorForController, registerCollaborationAdapter } from '../core/internal'
import { describeMarkdownSchema } from '../schema'
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
import { CollaborationTableRepair } from './tableRepair'
// Loaded up front: the secret and concealed views import it lazily, which would outlive the test.
import '../concealment'

// Conformance corpus of Atlas.Markdown.Sync's YjsDocumentMaterializer, the managed reconstruction of the document a Y.Doc
// holds (tests/Atlas.Markdown.Sync.Tests/Fixtures/yjs-materializer). Every case is a full state and the updates after it; for
// each step the corpus holds JSON.stringify of the document the editor shows (y-tiptap's _forceRerender: the nodes it builds
// from the fragment replace an empty document) or why the editor cannot show it (y-tiptap deletes what it cannot build, Yjs parks
// a payload as pending); "refused" steps are structures the editor never writes that the materializer refuses although
// y-tiptap renders something. Cases marked `editor` are also opened in a real collaborative editor, whose getJSON() must equal the
// last expected document. Without the variable the test recomputes every expectation from the committed payloads; regenerate
// after a Yjs, y-tiptap, ProseMirror or schema change, then run YjsMaterializerCorpusTests:
//   ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/collab/yjsMaterializerCorpus.test.ts
const update = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ATLAS_UPDATE_SCHEMA === '1'
// Node modules without their types (the test typecheck has no Node globals; jsdom replaces the global URL).
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as {
  existsSync(path: string): boolean
  readFileSync(path: string, encoding?: 'utf8'): Uint8Array & string
  writeFileSync(path: string, data: string | Uint8Array): void
  mkdirSync(path: string, options: { recursive: true }): void
  rmSync(path: string, options: { recursive: true; force: true }): void
}
const { fileURLToPath } = await import(/* @vite-ignore */ `node:${'url'}`) as { fileURLToPath(url: string): string }
const { createHash } = await import(/* @vite-ignore */ `node:${'crypto'}`) as {
  createHash(algorithm: 'sha256'): { update(data: string | Uint8Array): { digest(encoding: 'hex'): string } }
}
const zlib = await import(/* @vite-ignore */ `node:${'zlib'}`) as { gzipSync(data: Uint8Array, options: { level: number }): Uint8Array; gunzipSync(data: Uint8Array): Uint8Array }
const local = (relative: string) => fileURLToPath(new URL(relative, import.meta.url).href)
const fixtures = local('../../../../tests/Atlas.Markdown.Sync.Tests/Fixtures/')
const corpus = `${fixtures}yjs-materializer/`

/** Every built-in plugin, as the Sync package's built-in manifest describes them (Atlas installs all of them). */
function plugins(): MarkdownPlugin[] {
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

interface Step {
  /** Relative to the corpus folder; `.gz` is gzipped. */
  payload: string
  expect: 'json' | 'sha256' | 'error' | 'none'
  /** For an error: why the editor cannot show the document, or why the materializer refuses it. */
  why?: string
  /** The editor renders something, but the structure is one it never writes and the materializer refuses it. */
  refused?: true
  sha256?: string
  bytes?: number
}
interface Case { name: string; source: string; editor?: true; steps: Step[] }

const REMOTE = Symbol('remote')
const sessions: { destroy(): void }[] = []
afterEach(() => { sessions.splice(0).forEach(session => session.destroy()) })

interface Client { doc: Y.Doc; editor: Editor; local: Uint8Array[] }

/** A real rich editor with every plugin bound to a Y.Doc as useCollab binds it; `local` collects the updates it writes. */
function open(clientID: number, payloads: readonly Uint8Array[] = []): Client {
  const doc = new Y.Doc()
  doc.clientID = clientID
  for (const payload of payloads) Y.applyUpdate(doc, payload, REMOTE)
  const client: Client = { doc, editor: null as unknown as Editor, local: [] }
  doc.on('update', (payload: Uint8Array, origin: unknown) => { if (origin !== REMOTE) client.local.push(payload) })
  const adapter = registerCollaborationAdapter(publish => ({
    extensions: [Collaboration.configure({ document: doc, field: 'default' }), CollaborationTableRepair],
    isRemoteTransaction: () => false,
    attachEditor: () => publish({ hydrated: true, writable: true }),
    detachEditor() {}, destroy() {},
  }))
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: plugins(), source: { kind: 'collaborative', adapter } })
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: `materializer ${clientID}` })
  client.editor = editorForController(controller)!
  sessions.push({
    destroy() {
      // Saving on destroy validates the document, which the raw cases' values do not pass.
      try { controller.destroy() } catch { /* the view is gone either way */ }
      host.remove()
      doc.destroy()
    },
  })
  return client
}

/** Delivers every client's new updates to the others until none answers any more (format cleanups, table repairs). */
async function exchange(clients: Client[], relay: Uint8Array[]): Promise<void> {
  for (let round = 0; round < 8 && clients.some(client => client.local.length); round++) {
    const sent = clients.map(client => client.local.splice(0))
    for (const updates of sent) relay.push(...updates)
    clients.forEach((client, index) => sent.forEach((updates, from) => {
      if (from !== index) for (const payload of updates) Y.applyUpdate(client.doc, payload, REMOTE)
    }))
    await Promise.resolve()
  }
}

/**
 * The relay's document: payloads integrated as they are, without the formatting cleanup a receiving Y.Doc runs (see passive in
 * yjsGuardFuzz.test.ts), or why Yjs cannot integrate them.
 */
function replay(payloads: readonly Uint8Array[]): Y.Doc | string {
  const doc = new Y.Doc()
  doc.on('afterTransaction', (transaction: Y.Transaction) => { (transaction as unknown as { _needFormattingCleanup: boolean })._needFormattingCleanup = false })
  for (const payload of payloads) Y.applyUpdate(doc, payload)
  const store = doc.store as unknown as { pendingStructs: unknown; pendingDs: unknown }
  return store.pendingStructs || store.pendingDs ? 'Yjs parks a payload whose dependencies are missing' : doc
}

/** Values y-tiptap passes into ProseMirror although they are no document data: why, or null. */
function foreign(type: Y.XmlFragment | Y.XmlElement): string | null {
  if (type instanceof Y.XmlElement && Object.values(type.getAttributes()).some(value => value instanceof Y.AbstractType))
    return 'a shared type as an attribute value (ProseMirror keeps the Y type)'
  for (const child of type.toArray()) {
    // ProseMirror builds a text node whose text is the embed; the fragment's size becomes NaN and toJSON() drops its content.
    if (child instanceof Y.XmlText && child.toDelta().some((op: { insert: unknown }) => typeof op.insert !== 'string')) return 'an embed in a text'
    const nested = child instanceof Y.XmlElement ? foreign(child) : null
    if (nested) return nested
  }
  return null
}

/** What the editor shows for a document (ySyncPlugin's _forceRerender), or why it cannot. */
function render(doc: Y.Doc, schema: Schema): { json: string } | { error: string } {
  const unsupported = foreign(doc.getXmlFragment('default'))
  if (unsupported) return { error: unsupported }
  let deleted = false
  const listener = () => { deleted = true }
  doc.on('update', listener)
  try {
    const content = yXmlFragmentToProseMirrorFragment(doc.getXmlFragment('default'), schema)
    if (deleted) return { error: 'y-tiptap deletes content it cannot build' }
    const empty = EditorState.create({ schema })
    const shown = empty.tr.replace(0, empty.doc.content.size, new Slice(content, 0, 0)).doc
    if (content.size > 0 && !shown.content.eq(content)) return { error: 'ProseMirror wraps top-level nodes the document does not accept' }
    return { json: JSON.stringify(shown.toJSON()) }
  } catch (error) {
    return { error: `y-tiptap fails: ${String(error)}` }
  } finally {
    doc.off('update', listener)
  }
}

const read = (file: string): Uint8Array => {
  const bytes = new Uint8Array(fs.readFileSync(`${corpus}${file}`))
  return file.endsWith('.gz') ? new Uint8Array(zlib.gunzipSync(bytes)) : bytes
}
const sha256 = (text: string) => createHash('sha256').update(new TextEncoder().encode(text)).digest('hex')

/** The expectation of every step from the payloads; refused and unchecked steps keep theirs. Returns the expected.jsonl lines. */
function expectations(entry: Case, payloads: readonly Uint8Array[], schema: Schema): string[] {
  return entry.steps.map((step, index) => {
    if (step.refused || step.expect === 'none') return ''
    const doc = replay(payloads.slice(0, index + 1))
    const shown = typeof doc === 'string' ? { error: doc } : render(doc, schema)
    if ('error' in shown) {
      Object.assign(step, { expect: 'error', why: shown.error, sha256: undefined, bytes: undefined })
      return ''
    }
    if (step.expect === 'sha256') {
      Object.assign(step, { sha256: sha256(shown.json), bytes: new TextEncoder().encode(shown.json).length })
      return ''
    }
    Object.assign(step, { expect: 'json', why: undefined })
    return shown.json
  })
}

/** A real editor opened on the case's final document shows exactly the expected JSON, and writes nothing back. */
function checkEditor(entry: Case, payloads: readonly Uint8Array[], lines: readonly string[]): void {
  const last = entry.steps.length - 1
  if (!entry.editor || entry.steps[last]!.expect !== 'json') return
  const client = open(9_000_001, payloads)
  expect(JSON.stringify(client.editor.getJSON()), `${entry.name}: the editor shows another document`).toBe(lines[last])
  expect(client.local, `${entry.name}: the editor normalizes the document on load`).toEqual([])
}

// ---- Cases ----

interface Draft {
  name: string
  source: string
  editor?: boolean
  /** Payloads written into the case folder, or the path of a payload already in the corpus fixtures (relative to the corpus). */
  payloads: (Uint8Array | string)[]
  /** Only the state and the last step are checked (long update logs of large documents). */
  ends?: boolean
  /** Documents compared by hash, payloads gzipped. */
  large?: boolean
  /** Step index → why the materializer refuses a structure the editor never writes. */
  refused?: Record<number, string>
}

type Node = JSONContent
const doc = (...content: Node[]): Node => ({ type: 'doc', content })
const p = (...content: Node[]): Node => ({ type: 'paragraph', ...(content.length ? { content } : {}) })
const t = (text: string, ...marks: (string | { type: string; attrs: Record<string, unknown> })[]): Node =>
  ({ type: 'text', text, ...(marks.length ? { marks: marks.map(mark => typeof mark === 'string' ? { type: mark } : mark) } : {}) })
const link = (href: string, attrs: Record<string, unknown> = {}) => ({ type: 'link', attrs: { href, ...attrs } })

const UNICODE = doc(
  { type: 'heading', attrs: { level: 1 }, content: [t('Příliš žluťoučký kůň úpěl ďábelské ódy')] },
  p(t('Emoji: 😀 🚀 👨‍👩‍👧‍👦 👍🏽 🇨🇿 🏳️‍🌈 and a lone 🦊 at the end 🦊')),
  p(t('Combining: e\u0301 a\u030A n\u0303 Z\u0335\u0321a\u0336\u031Bl\u0337g\u0334o\u0338 — precomposed é å ñ')),
  p(t('Scripts: 日本語のテキスト, 中文, 한국어, العربية, עברית, ελληνικά, кириллица')),
  p(t('Astral only: 𝔘𝔫𝔦𝔠𝔬𝔡𝔢 𝟘𝟙𝟚 𓀀𓁐 🀄')),
  p(t('Invisible: zero\u200Bwidth, joiner\u200D, bom\uFEFF, nbsp\u00A0, soft\u00ADhyphen, rtl\u202Emark\u202C')),
  { type: 'codeBlock', attrs: { language: 'ts' }, content: [t('const kůň = "🐎"\n\tconst y = \'\\\\\'\r\nreturn kůň')] },
  { type: 'image', attrs: { src: 'https://example.com/obrázek.png', alt: 'Žluťoučký 🐎', title: 'Kůň "v uvozovkách"' } },
)

const ESCAPES = doc(
  p(t('Quotes "double" and \'single\', backslash \\ and slash /, braces {} [] <tag> & amp')),
  p(t('Controls: nul\u0000 bell\u0007 bs\u0008 ff\u000C vt\u000B esc\u001B unit\u001F del\u007F c1\u0085\u009F')),
  p(t('Separators: line\u2028para\u2029 end')),
  { type: 'codeBlock', content: [t('tab\tnewline\ncr\rcrlf\r\n"quoted" \\backslash\\')] },
)

const MARKS = doc(
  p(t('plain '), t('bold', 'bold'), t(' then '), t('bold and italic', 'bold', 'italic'), t(' italic', 'italic'), t(' end')),
  p(t('adjacent', 'bold'), t('marks', 'italic'), t('touch', 'underline'), t('here', 'strike'), t('!', 'highlight')),
  p(t('code ', 'code'), t('next to code', 'code'), t(' and '), t('struck code-free', 'strike', 'underline')),
  p(t('two links: '), t('first', link('https://example.com/a')), t('second', link('https://example.com/b', { title: 'B' })), t(' same link split: '),
    t('one ', link('https://example.com/same')), t('two', link('https://example.com/same'), 'bold')),
  p(t('link with every attribute', link('https://example.com/ř?q=1&x="y"#frag', { target: null, rel: 'nofollow', class: 'external', title: 'Název ✓' }))),
  p(t('nested ', 'bold'), t('link inside bold', 'bold', link('https://example.com/nested')), t(' still bold', 'bold')),
)

const EMPTY_BLOCKS = doc(
  p(), p(), { type: 'heading', attrs: { level: 2 } }, p(t('between')), p(),
  { type: 'blockquote', content: [p()] },
  { type: 'bulletList', content: [{ type: 'listItem', content: [p()] }, { type: 'listItem', content: [p(t('x'))] }] },
  { type: 'codeBlock' }, p(),
)

const EDITING = doc(
  { type: 'heading', attrs: { level: 2 }, content: [t('Plán')] },
  p(t('První odstavec s textem.')),
  p(t('Druhý odstavec, který se bude upravovat.')),
  { type: 'bulletList', content: [1, 2].map(n => ({ type: 'listItem', content: [p(t(`položka ${n}`))] })) },
)

const CONCURRENT = doc(
  p(t('The quick brown fox jumps over the lazy dog.')),
  p(t('Příliš žluťoučký kůň úpěl ďábelské ódy.')),
  { type: 'table', content: [0, 1].map(row => ({ type: 'tableRow', content: [0, 1].map(col => ({
    type: row === 0 ? 'tableHeader' : 'tableCell', content: [p(t(`c${row}${col}`))],
  })) })) },
)

/** The doc position of a text's first character. */
function at(editor: Editor, needle: string): number {
  let found = -1
  editor.state.doc.descendants((node, pos) => {
    const index = node.isText ? node.text!.indexOf(needle) : -1
    if (found < 0 && index >= 0) found = pos + index
    return found < 0
  })
  if (found < 0) throw new Error(`no text "${needle}"`)
  return found
}
const range = (editor: Editor, needle: string) => ({ from: at(editor, needle), to: at(editor, needle) + needle.length })

let clientIds = 100
/** A document as an editor writes it: setContent on a collaborative editor. */
function written(content: Node): Uint8Array {
  const client = open(clientIds++)
  client.editor.commands.setContent(content)
  return Y.encodeStateAsUpdate(client.doc)
}

/** A document built with the Yjs API (structures and values the editor's writer does not produce). */
function raw(build: (fragment: Y.XmlFragment, doc: Y.Doc) => void): Uint8Array {
  const ydoc = new Y.Doc()
  ydoc.clientID = clientIds++
  ydoc.transact(() => build(ydoc.getXmlFragment('default'), ydoc))
  return Y.encodeStateAsUpdate(ydoc)
}
const element = (name: string, children: unknown[] = [], attrs: Record<string, unknown> = {}) => {
  const el = new Y.XmlElement(name)
  for (const [key, value] of Object.entries(attrs)) el.setAttribute(key, value as never)
  el.insert(0, children as Y.XmlElement[])
  return el
}
const text = (...runs: [string, Record<string, unknown>?][]) => {
  const ytext = new Y.XmlText()
  let offset = 0
  for (const [value, attributes] of runs) {
    ytext.insert(offset, value, attributes)
    offset += value.length
  }
  return ytext
}
/** A second client's edits on a copy of `state`, as one update. */
function edit(state: Uint8Array, run: (fragment: Y.XmlFragment, doc: Y.Doc) => void): Uint8Array {
  const ydoc = new Y.Doc()
  ydoc.clientID = clientIds++
  Y.applyUpdate(ydoc, state)
  const before = Y.encodeStateVector(ydoc)
  ydoc.transact(() => run(ydoc.getXmlFragment('default'), ydoc))
  return Y.encodeStateAsUpdate(ydoc, before)
}
const xml = (fragment: Y.XmlFragment | Y.XmlElement, ...path: number[]) =>
  path.reduce<Y.XmlFragment | Y.XmlElement | Y.XmlText>((type, index) => (type as Y.XmlElement).get(index) as Y.XmlElement, fragment)

function random(seed: number) {
  let state = seed >>> 0 || 1
  const next = () => { state ^= state << 13; state ^= state >>> 17; state ^= state << 5; return (state >>> 0) / 0x1_0000_0000 }
  return { next, int: (n: number) => Math.floor(next() * n), pick: <T>(items: readonly T[]) => items[Math.floor(next() * items.length)]! }
}

async function drafts(): Promise<Draft[]> {
  const list: Draft[] = []
  // The editor's own fixtures, written by a collaborative editor.
  const envelope = (file: string) => (JSON.parse(fs.readFileSync(file, 'utf8')) as MarkdownRichValue).document as Node
  list.push({ name: 'fixture-web-document', source: 'Fixtures/document-schema/web-document.json: every node, mark and component', editor: true,
    payloads: [written(envelope(`${fixtures}document-schema/web-document.json`))] })
  const imported = import.meta.glob<MarkdownRichValue>('../source/fixtures/*.json', { eager: true, import: 'default' })
  for (const [path, value] of Object.entries(imported).sort(([a], [b]) => a.localeCompare(b))) {
    const name = path.slice(path.lastIndexOf('/') + 1, -'.json'.length)
    list.push({ name: `fixture-${name}`, source: `src/Atlas.Markdown/src/source/fixtures/${name}.json: a foreign wiki page as the editor imports it`, editor: true,
      payloads: [written(value.document as Node)] })
  }
  const secrets = import.meta.glob<MarkdownRichValue>('../plugins/secrets/fixtures/*.json', { eager: true, import: 'default' })
  for (const [path, value] of Object.entries(secrets).sort(([a], [b]) => a.localeCompare(b))) {
    const name = path.slice(path.lastIndexOf('/') + 1, -'.json'.length)
    list.push({ name: `fixture-${name}`, source: `src/Atlas.Markdown/src/plugins/secrets/fixtures/${name}.json, migrated to the current plugin version`, editor: true,
      payloads: [written(migrateMarkdownValue(value, 'rich', plugins()).document as Node)] })
  }
  const crashes = JSON.parse(fs.readFileSync(local('./fixtures/y-tiptap-selection-crash.json'), 'utf8')) as { cause: string; before: string; update: string }[]
  const bytes = (base64: string) => Uint8Array.from(atob(base64), char => char.charCodeAt(0))
  crashes.forEach((crash, index) => list.push({ name: `fixture-y-tiptap-crash-${index + 1}`,
    source: `src/Atlas.Markdown/src/collab/fixtures/y-tiptap-selection-crash.json: ${crash.cause}`, payloads: [bytes(crash.before), bytes(crash.update)] }))

  // Text, marks and empty blocks as the editor writes them.
  list.push({ name: 'editor-unicode', source: 'Czech, astral characters (emoji, ZWJ sequences, flags), combining marks, other scripts, invisible characters', editor: true, payloads: [written(UNICODE)] })
  list.push({ name: 'editor-escapes', source: 'every character JSON escapes, control characters, U+2028/2029', editor: true, payloads: [written(ESCAPES)] })
  list.push({ name: 'editor-marks', source: 'overlapping, adjacent and nested marks, links with attributes, split equal links', editor: true, payloads: [written(MARKS)] })
  list.push({ name: 'editor-empty-blocks', source: 'empty paragraphs, heading, quote, list item and code block', editor: true, payloads: [written(EMPTY_BLOCKS)] })
  list.push({ name: 'empty-document', source: 'a Y.Doc nothing was written to: the editor shows its empty document', editor: true, payloads: [Y.encodeStateAsUpdate(new Y.Doc())] })

  // A session: the state, then every update the editor sends for one command after another.
  {
    const client = open(clientIds++)
    client.editor.commands.setContent(EDITING)
    const payloads: Uint8Array[] = [Y.encodeStateAsUpdate(client.doc)]
    client.local.length = 0
    const chain = () => client.editor.chain().focus()
    const commands: (() => unknown)[] = [
      () => chain().setTextSelection(at(client.editor, 'odstavec s') + 'odstavec '.length).insertContent('česky 🐎 ').run(),
      () => chain().setTextSelection(range(client.editor, 'První odstavec')).toggleBold().run(),
      () => chain().setTextSelection({ from: at(client.editor, 'odstavec'), to: at(client.editor, 'česky') + 'česky'.length }).toggleItalic().run(),
      () => chain().setTextSelection(range(client.editor, 'textem')).setLink({ href: 'https://example.cz/ř?x=1', target: null }).run(),
      () => chain().setTextSelection(at(client.editor, 'který')).splitBlock().run(),
      () => chain().setTextSelection(range(client.editor, 'se bude')).deleteSelection().run(),
      () => chain().setTextSelection(at(client.editor, 'Druhý')).setTextAlign('center').run(),
      () => chain().setTextSelection(at(client.editor, 'položka 2') + 'položka'.length).setHardBreak().run(),
      () => chain().setTextSelection(at(client.editor, 'Plán') + 4).insertContent(' 2026').run(),
      () => chain().setTextSelection(at(client.editor, 'upravovat')).insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run(),
      () => chain().insertContent('buňka ✓').run(),
      () => chain().addRowAfter().run(),
      () => chain().setTextSelection(range(client.editor, 'česky')).toggleCode().run(),
      () => chain().setTextSelection(at(client.editor, 'položka 1')).toggleTaskList().run(),
      () => chain().setTextSelection(range(client.editor, 'textem')).unsetLink().run(),
      () => client.editor.commands.undo(),
      () => chain().setTextSelection({ from: at(client.editor, 'Plán'), to: at(client.editor, 'Druhý') }).deleteSelection().run(),
    ]
    for (const command of commands) {
      command()
      payloads.push(...client.local.splice(0))
      // Each command its own undo step (the undo manager merges changes within 500 ms).
      ;(yUndoPluginKey.getState(client.editor.state) as { undoManager: Y.UndoManager }).undoManager.stopCapturing()
    }
    list.push({ name: 'editor-session', source: 'one editor: typing, marks, a link, split, delete, align, hard break, table, task list, undo; one update per step', editor: true, payloads })
  }

  // Two editors edit the same text at the same time; the relay receives A's updates, then B's, then what each answers.
  {
    const a = open(clientIds++)
    a.editor.commands.setContent(CONCURRENT)
    const baseline = Y.encodeStateAsUpdate(a.doc)
    a.local.length = 0
    const b = open(clientIds++, [baseline])
    a.editor.chain().setTextSelection(range(a.editor, 'quick brown')).toggleBold().run()
    a.editor.chain().setTextSelection(at(a.editor, 'fox')).insertContent('ryšavá ').run()
    a.editor.chain().setTextSelection(range(a.editor, 'lazy ')).deleteSelection().run()
    a.editor.chain().setTextSelection(at(a.editor, 'Příliš')).insertContent('A: ').run()
    a.editor.chain().setTextSelection(at(a.editor, 'c11')).addColumnAfter().run()
    b.editor.chain().setTextSelection(range(b.editor, 'brown fox jumps')).toggleItalic().run()
    b.editor.chain().setTextSelection(range(b.editor, 'over the lazy')).deleteSelection().run()
    b.editor.chain().setTextSelection(at(b.editor, 'Příliš')).insertContent('B: 🦊 ').run()
    b.editor.chain().setTextSelection(range(b.editor, 'žluťoučký kůň')).setLink({ href: 'https://example.cz/kůň' }).run()
    b.editor.chain().setTextSelection(at(b.editor, 'c11')).addRowAfter().run()
    const payloads: Uint8Array[] = [baseline]
    await exchange([a, b], payloads)
    const merged = replay(payloads)
    const shown = typeof merged === 'string' ? merged : render(merged, a.editor.schema)
    // Both live editors converged on the document the relay holds.
    for (const client of [a, b]) expect(JSON.stringify(client.editor.getJSON())).toBe((shown as { json: string }).json)
    list.push({ name: 'editor-concurrent', source: 'two editors: overlapping bold and italic, inserts and deletes in the same sentence, a link, a row and a column at once', editor: true, payloads })
  }

  // Garbage collection: A deletes a paragraph (its content becomes GC structs in A's state) while B types into it and adds one after it.
  {
    const baseline = written(doc(p(t('Jedna')), p(t('Dvě dvě')), p(t('Tři'))))
    const a = new Y.Doc()
    a.clientID = clientIds++
    Y.applyUpdate(a, baseline)
    a.getXmlFragment('default').delete(1, 1)
    const late = edit(baseline, fragment => {
      ;(xml(fragment, 1, 0) as Y.XmlText).insert(3, ' (B píše)')
      ;(xml(fragment, 1) as Y.XmlElement).setAttribute('textAlign', 'right')
      fragment.insert(2, [element('paragraph', [text(['Nový od B'])])])
    })
    list.push({ name: 'gc-concurrent-delete', source: 'a state with GC structs, then an update whose items reference the collected content', editor: true, payloads: [Y.encodeStateAsUpdate(a), late] })
    const all = new Y.Doc()
    Y.applyUpdate(all, baseline)
    all.getXmlFragment('default').delete(0, all.getXmlFragment('default').length)
    list.push({ name: 'gc-everything-deleted', source: 'every block deleted and collected: the editor shows its empty document', editor: true, payloads: [Y.encodeStateAsUpdate(all)] })
  }

  // A second client inserts between the halves of a surrogate pair: Yjs splits the string and replaces both halves with U+FFFD.
  {
    const state = raw(fragment => fragment.insert(0, [element('paragraph', [text(['a😀b'])])]))
    list.push({ name: 'raw-surrogate-split', source: 'an insert inside an emoji: the split halves become U+FFFD', editor: true,
      payloads: [state, edit(state, fragment => (xml(fragment, 0, 0) as Y.XmlText).insert(2, 'X'))] })
  }

  // Values and orders y-tiptap passes through as they are.
  list.push({ name: 'raw-attributes', source: 'attributes set out of schema order, unknown, undefined, null and removed ones: schema order, defaults', editor: true, payloads: [raw(fragment => {
    const heading = element('heading', [text(['Nadpis'])], { level: 3, unknown: 'ignored', textAlign: 'center', headingId: undefined })
    const image = element('image', [], { title: null, src: 'https://example.com/a.png', alt: 'gone' })
    fragment.insert(0, [heading, image, element('paragraph', [text(['x'])], { textAlign: null })])
    image.removeAttribute('alt')
  })] })
  list.push({ name: 'raw-mark-order', source: 'formats in another order than the mark ranks, partial and hashed mark attributes, a `true` format value', editor: true, payloads: [raw(fragment => {
    fragment.insert(0, [element('paragraph', [text(
      ['all marks', { underline: {}, link: { href: 'https://example.cz' }, italic: {}, bold: {} }],
      [' hashed', { 'bold--abcdefgh': {}, 'link--A1b2+/=x': { href: 'https://example.cz/hash', title: 'h' } }],
      [' true value', { italic: true }],
    )])])
  })] })
  list.push({ name: 'raw-adjacent-texts', source: 'adjacent XmlTexts and runs whose marks are equal merge into one text node', editor: true, payloads: [raw(fragment => {
    fragment.insert(0, [element('paragraph', [
      text(['ab', { bold: {} }]), text(['cd', { bold: {} }]),
      text(['ef', { link: { href: 'h', title: 't' } }]), text(['gh', { link: { title: 't', href: 'h', rel: 'noopener noreferrer' } }]),
      text(), text(['ij']),
    ])])
  })] })
  list.push({ name: 'raw-numbers', source: 'number values as JSON.stringify writes them: floats, exponents, -0, extremes', editor: true, payloads: [raw(fragment => {
    fragment.insert(0, [
      element('paragraph', [text(['numbers'])], { textAlign: [0, -0, 1, -1, 0.1, 0.5, 1.25, -2.5, 1e21, 1e-7, 1e-6, 123456789012345680000, 2 ** 53, 2 ** 31, -(2 ** 31), 2 ** 31 - 1,
        5e-324, 1.7976931348623157e308, 1 / 3, 1e15, 1e16, 123.456, -0.000001234, 4294967296.5] }),
      element('paragraph', [text(['object keys'])], { textAlign: { b: 1, 2: true, 1: 'x', a: { z: 1, y: [1, { k: null }] }, '01': 'not an index', 4294967295: 'not an index', 4294967294: 'index' } }),
    ])
  })] })
  list.push({ name: 'raw-ignored', source: 'attributes on a text, other roots and an empty text: nothing the editor shows', editor: true, payloads: [raw((fragment, ydoc) => {
    const ytext = text(['visible'])
    fragment.insert(0, [element('paragraph', [ytext, text()])])
    ytext.setAttribute('data-x', 'ignored')
    ydoc.getMap('meta').set('title', 'other root')
    ydoc.getText('notes').insert(0, 'other text root')
  })] })

  // Structures the editor cannot show or never writes: the materializer must refuse them.
  const refusedRaw = (name: string, source: string, build: (fragment: Y.XmlFragment, doc: Y.Doc) => void, why?: string) =>
    list.push({ name: `unsupported-${name}`, source, payloads: [raw(build)], ...(why ? { refused: { 0: why } } : {}) })
  refusedRaw('unknown-node', 'an element the schema does not know', fragment => fragment.insert(0, [element('blink', [text(['x'])])]))
  refusedRaw('unknown-mark', 'a format the schema does not know', fragment => fragment.insert(0, [element('paragraph', [text(['x', { sparkle: {} }])])]))
  refusedRaw('mark-not-allowed', 'bold inside a code block', fragment => fragment.insert(0, [element('codeBlock', [text(['x', { bold: {} }])])]))
  refusedRaw('invalid-content', 'a paragraph inside a paragraph', fragment => fragment.insert(0, [element('paragraph', [element('paragraph', [text(['x'])])])]))
  refusedRaw('text-in-list', 'text directly in a bullet list', fragment => fragment.insert(0, [element('bulletList', [text(['x'])])]))
  refusedRaw('top-level-list-item', 'a list item at the top level', fragment => fragment.insert(0, [element('listItem', [element('paragraph', [text(['x'])])])]))
  refusedRaw('text-at-root', 'a text directly in the fragment', fragment => fragment.insert(0, [text(['x'])]))
  refusedRaw('hook', 'an XmlHook inside a paragraph', fragment => fragment.insert(0, [element('paragraph', [new Y.XmlHook('hook')])]),
    'a child the editor never writes (y-tiptap 3.3 skips it and shows the paragraph without it)')
  refusedRaw('map-child', 'a Y.Map inside a paragraph', fragment => fragment.insert(0, [element('paragraph', [new Y.Map()])]),
    'a child the editor never writes (y-tiptap 3.3 skips it and shows the paragraph without it)')
  refusedRaw('any-child', 'a plain value inside a paragraph', fragment => fragment.insert(0, [element('paragraph', ['loose'])]),
    'a child the editor never writes (y-tiptap 3.3 skips it and shows the paragraph without it)')
  refusedRaw('embed', 'an embed inside a text', fragment => {
    const ytext = text(['ab'])
    fragment.insert(0, [element('paragraph', [ytext])])
    ytext.insertEmbed(1, { image: 'https://example.com/a.png' })
  })
  refusedRaw('type-attribute', 'a shared type as an attribute value', fragment => fragment.insert(0, [element('paragraph', [text(['x'])], { textAlign: new Y.XmlText('center') })]))
  refusedRaw('binary-attribute', 'binary data as an attribute value', fragment => fragment.insert(0, [element('paragraph', [text(['x'])], { textAlign: new Uint8Array([1, 2, 3]) })]),
    'binary data as an attribute value (JSON.stringify writes an object of indexes)')
  refusedRaw('nested-undefined', 'undefined inside an attribute value', fragment => fragment.insert(0, [element('paragraph', [text(['x'])], { textAlign: { a: undefined, b: [undefined] } })]),
    'undefined inside an attribute value (JSON.stringify drops or nulls it)')
  refusedRaw('nan', 'NaN as an attribute value', fragment => fragment.insert(0, [element('paragraph', [text(['x'])], { textAlign: Number.NaN })]),
    'a non-finite number (JSON.stringify writes null)')
  refusedRaw('bigint', 'a bigint as an attribute value', fragment => fragment.insert(0, [element('paragraph', [text(['x'])], { textAlign: 10n })]))
  refusedRaw('format-string', 'a link format whose value is a string', fragment => fragment.insert(0, [element('paragraph', [text(['x', { link: 'https://example.com' }])])]),
    'a format value that is not an object for a mark with attributes (ProseMirror falls back to the defaults)')
  {
    const state = raw(fragment => fragment.insert(0, [element('paragraph', [text(['a'])])]))
    const ydoc = new Y.Doc()
    ydoc.clientID = clientIds++
    Y.applyUpdate(ydoc, state)
    ;(xml(ydoc.getXmlFragment('default'), 0, 0) as Y.XmlText).insert(1, 'b')
    const middle = Y.encodeStateVector(ydoc)
    ;(xml(ydoc.getXmlFragment('default'), 0, 0) as Y.XmlText).insert(2, 'c')
    list.push({ name: 'unsupported-pending', source: 'an update whose previous update is missing', payloads: [state, Y.encodeStateAsUpdate(ydoc, middle)] })
  }

  // The Yjs boundary corpus (real editor sessions per node type, editing, hostile payloads, perf documents): every step.
  const manifest = JSON.parse(fs.readFileSync(`${fixtures}yjs-corpus/manifest.json`, 'utf8')) as { scenario: string; seq: number; kind: string; file: string }[]
  const scenarios = new Map<string, typeof manifest>()
  for (const entry of manifest.filter(item => item.file)) scenarios.set(entry.scenario, [...(scenarios.get(entry.scenario) ?? []), entry])
  for (const [scenario, entries] of scenarios) {
    entries.sort((a, b) => a.seq - b.seq)
    const states = entries.filter(entry => entry.kind === 'state')
    if (scenario === 'hostile') {
      for (const entry of entries.slice(1))
        list.push({ name: `corpus-hostile-${entry.file.slice('hostile/'.length, 'hostile/'.length + 3)}`, source: `yjs-corpus/${entry.file} against the baseline`,
          payloads: [`../yjs-corpus/${entries[0]!.file}`, `../yjs-corpus/${entry.file}`] })
      continue
    }
    list.push({ name: `corpus-${scenario}`, source: `yjs-corpus/${scenario}: the baseline, then every captured update`,
      payloads: [`../yjs-corpus/${entries[0]!.file}`, ...entries.slice(1).filter(entry => entry.kind === 'update').map(entry => `../yjs-corpus/${entry.file}`)] })
    for (const entry of states.slice(1))
      list.push({ name: `corpus-${scenario}-${entry.file.slice(scenario.length + 1, scenario.length + 4)}-state`, source: `yjs-corpus/${entry.file} on its own`,
        payloads: [`../yjs-corpus/${entry.file}`] })
  }
  for (const size of ['50k', '256k', '1m']) {
    const folder = `${fixtures}yjs-corpus/perf/${size}/`
    const updates = Array.from({ length: 200 }, (_, index) => `perf/${size}/u${String(index).padStart(3, '0')}.bin`).filter(file => fs.existsSync(`${fixtures}yjs-corpus/${file}`))
    if (!fs.existsSync(`${folder}state.bin`)) continue
    list.push({ name: `corpus-perf-${size}`, source: `yjs-corpus/perf/${size}: the state and 200 random edits`, ends: true, large: size !== '50k',
      payloads: [`../yjs-corpus/perf/${size}/state.bin`, ...updates.map(file => `../yjs-corpus/${file}`)] })
  }
  list.push(nearLimit())
  return list
}

/**
 * A document just under the 4 MiB default MaxDocumentBytes: varied blocks written by one editor, then thousands of small edits of
 * four other clients (inserts, deletes, formats) that split its items as a long-lived document's are.
 */
function nearLimit(): Draft {
  const limit = 4 * 1024 * 1024
  const rng = random(20261026)
  const words = ['Příliš', 'žluťoučký', 'kůň', 'úpěl', 'ďábelské', 'ódy', 'dokument', 'spolupráce', 'server', 'editor', 'Yjs', 'ProseMirror', '🚀', 'naïve',
    'café', 'e\u0301', '日本語', 'שלום', 'materializace', 'tabulka', 'seznam', 'odkaz', 'text', 'a', 'v', 'na', 'se', 'je', 'to', 'pro']
  // Sentences from a small pool keep the gzipped payload small; the materializer's cost does not depend on the words.
  const pool = Array.from({ length: 64 }, () => Array.from({ length: 8 }, () => rng.pick(words)))
  const sentence = (count: number) => rng.pick(pool).slice(0, count).join(' ')
  const block = (index: number): Node[] => {
    const paragraph = p(t(`${sentence(6)} `), t(sentence(3), 'bold'), t(` ${sentence(5)} `), t(sentence(2), link(`https://example.cz/${index % 50}`)), t(` ${sentence(8)}.`))
    switch (index % 12) {
      case 0: return [{ type: 'heading', attrs: { level: 2 + (index % 3) }, content: [t(sentence(4))] }, paragraph]
      case 3: return [{ type: 'bulletList', content: [1, 2, 3].map(() => ({ type: 'listItem', content: [p(t(sentence(5)))] })) }]
      case 5: return [{ type: 'codeBlock', attrs: { language: 'ts' }, content: [t(`const x${index} = "${sentence(4)}"\nreturn x${index}`)] }]
      case 7: return [{ type: 'table', content: [0, 1, 2].map(row => ({ type: 'tableRow', content: [0, 1, 2].map(() => ({
        type: row === 0 ? 'tableHeader' : 'tableCell', content: [p(t(sentence(2)))],
      })) })) }]
      case 9: return [{ type: 'taskList', content: [true, false].map(checked => ({ type: 'taskItem', attrs: { checked }, content: [p(t(sentence(4)))] })) }]
      default: return [paragraph]
    }
  }
  const schema = open(clientIds++).editor.schema
  const build = (count: number) => {
    const ydoc = new Y.Doc()
    ydoc.clientID = 1
    prosemirrorJSONToYXmlFragment(schema, doc(...Array.from({ length: count }, (_, index) => block(index)).flat()), ydoc.getXmlFragment('default'))
    const texts: Y.XmlText[] = []
    const walk = (type: Y.XmlFragment | Y.XmlElement): void => type.toArray().forEach(child => {
      if (child instanceof Y.XmlText) texts.push(child)
      else if (child instanceof Y.XmlElement && child.nodeName !== 'codeBlock') walk(child)
    })
    walk(ydoc.getXmlFragment('default'))
    for (let round = 0; round < count * 2; round++) {
      // The edits of four other clients: Yjs gives each transaction the document's current client id.
      ydoc.clientID = 2 + rng.int(4)
      const target = rng.pick(texts)
      const length = target.length
      const roll = rng.next()
      if (roll < 0.6 || length < 4) target.insert(rng.int(length + 1), `${rng.pick(words)} `)
      else if (roll < 0.8) target.delete(rng.int(length - 2), 1 + rng.int(2))
      else target.format(rng.int(length - 3), 3, rng.next() < 0.5 ? { italic: {} } : { italic: null })
    }
    return Y.encodeStateAsUpdate(ydoc)
  }
  // Blocks scaled from a sample until the state lands between 97 and 100 % of the limit.
  let count = 400
  let state = build(count)
  for (let attempt = 0; attempt < 6 && (state.length > limit || state.length < 0.97 * limit); attempt++) {
    count = Math.floor(count * (0.985 * limit / state.length))
    state = build(count)
  }
  expect(state.length).toBeLessThanOrEqual(limit)
  return { name: 'near-4m', source: `${count} blocks of every kind and ${count * 2} small edits of four other clients: ${state.length} bytes`, large: true, payloads: [state] }
}

// ---- The test ----

const loadManifest = (): Case[] => (JSON.parse(fs.readFileSync(`${corpus}cases.json`, 'utf8')) as { cases: Case[] }).cases

it('records or re-checks the materializer corpus against the editor', async () => {
  const schema = open(1).editor.schema
  // The schema the corpus is rendered with is the one the Sync package embeds.
  expect(describeMarkdownSchema(plugins())).toEqual(JSON.parse(fs.readFileSync(local('../../schema/markdown-schema.json'), 'utf8')))
  if (!update) {
    const cases = loadManifest()
    for (const entry of cases) {
      const payloads = entry.steps.map(step => read(step.payload))
      const recomputed = structuredClone(entry)
      const lines = expectations(recomputed, payloads, schema)
      expect(recomputed, `${entry.name}: the corpus no longer matches the editor; regenerate it (see the comment above)`).toEqual(entry)
      const stored = fs.readFileSync(`${corpus}${entry.name}/expected.jsonl`, 'utf8').replace(/\r\n/g, '\n').split('\n').slice(0, entry.steps.length)
      expect(stored, `${entry.name}: expected.jsonl no longer matches the editor; regenerate it`).toEqual(lines)
      checkEditor(entry, payloads, lines)
      sessions.splice(0).forEach(session => session.destroy())
    }
    return
  }
  fs.rmSync(corpus, { recursive: true, force: true })
  fs.mkdirSync(corpus, { recursive: true })
  const cases: Case[] = []
  for (const draft of await drafts()) {
    sessions.splice(0).forEach(session => session.destroy())
    fs.mkdirSync(`${corpus}${draft.name}`, { recursive: true })
    const payloads = draft.payloads.map(payload => typeof payload === 'string' ? read(payload) : payload)
    const last = payloads.length - 1
    const entry: Case = {
      name: draft.name,
      source: draft.source,
      ...(draft.editor ? { editor: true as const } : {}),
      steps: draft.payloads.map((payload, index) => {
        const file = typeof payload === 'string' ? payload
          : `${draft.name}/${String(index).padStart(3, '0')}.${index === 0 ? 'state' : 'update'}.bin${draft.large ? '.gz' : ''}`
        if (typeof payload !== 'string') fs.writeFileSync(`${corpus}${file}`, draft.large ? zlib.gzipSync(payload, { level: 9 }) : payload)
        const why = draft.refused?.[index]
        if (why) return { payload: file, expect: 'error', why, refused: true }
        if (draft.ends && index !== 0 && index !== last) return { payload: file, expect: 'none' }
        return { payload: file, expect: draft.large ? 'sha256' : 'json' }
      }),
    }
    const lines = expectations(entry, payloads, schema)
    if (draft.name.startsWith('unsupported-')) expect(entry.steps.at(-1)!.expect, `${draft.name} must be refused`).toBe('error')
    // Drops the undefined fields expectations() cleared.
    cases.push(JSON.parse(JSON.stringify(entry)) as Case)
    fs.writeFileSync(`${corpus}${draft.name}/expected.jsonl`, `${lines.join('\n')}\n`)
    checkEditor(entry, payloads, lines)
  }
  const versions = Object.fromEntries(['yjs', '@tiptap/y-tiptap', 'prosemirror-model'].map(name => [name, versionOf(name)]))
  fs.writeFileSync(`${corpus}cases.json`, `${JSON.stringify({ versions, cases }, null, 1)}\n`)
}, 600_000)

function versionOf(name: string): string {
  const lock = fs.readFileSync(local('../../../Atlas.Web/pnpm-lock.yaml'), 'utf8')
  // The prosemirror-model y-tiptap builds with; the lock also holds prosemirror-markdown's older copy.
  const match = name === 'prosemirror-model' ? /@tiptap\/y-tiptap@[^(\s]+\(prosemirror-model@([0-9][^)]*)\)/.exec(lock)
    : new RegExp(`\\n  '?${name.replace(/[/+]/g, '\\$&')}@([0-9][^(:'\\s]*)`).exec(lock)
  return match?.[1] ?? 'unknown'
}
