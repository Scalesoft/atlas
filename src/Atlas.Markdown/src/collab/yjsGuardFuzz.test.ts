// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import { yXmlFragmentToProseMirrorRootNode } from '@tiptap/y-tiptap'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownPlugin } from '../core'
import { editorForController, registerCollaborationAdapter } from '../core/internal'
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
import { createTocPlugin } from '../plugins/toc'
import { createYouTrackPlugin } from '../plugins/youtrack'
import { moveBlock } from '../features/drag/blockCommands'
import { CollaborationRemoteSelection } from './remoteSelection'
import { CollaborationTableRepair } from './tableRepair'

// Differential fuzzer of the collaboration guard's Yjs replica (Atlas.Markdown.Sync YjsDocumentTree): several real
// editors bound to their own Y.Docs edit concurrently, their provider outboxes merge pending updates (as SignalRYjsProvider
// does), a simulated relay admits them in arrival order and broadcasts them late and in batches. Every admitted update is
// recorded with the canonical hash of the relay's real Yjs document after it. YjsGuardFuzzTests replays the traces through
// the managed guard: it must admit every update and hold the same document after each.
//   ATLAS_YJS_FUZZ_OUT=<dir> ATLAS_YJS_FUZZ_SEEDS=1-20 ATLAS_YJS_FUZZ_STEPS=400 pnpm --filter @atlas/markdown exec vitest run src/collab/yjsGuardFuzz.test.ts
//   ATLAS_YJS_FUZZ=<dir> dotnet test tests/Atlas.Markdown.Sync.Tests --filter YjsGuardFuzzTests
const env = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env
const out = env.ATLAS_YJS_FUZZ_OUT
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as {
  mkdirSync(path: string, options: { recursive: true }): void
  writeFileSync(path: string, data: string): void
}
const { createHash } = await import(/* @vite-ignore */ `node:${'crypto'}`) as {
  createHash(algorithm: 'sha256'): { update(data: string): { digest(encoding: 'hex'): string } }
}

const sessions: { destroy(): void }[] = []
afterEach(() => { sessions.splice(0).forEach(session => session.destroy()) })

/** Every built-in plugin but secrets (its nodes are host-decided reserved nodes), as Atlas installs them. */
function plugins(): MarkdownPlugin[] {
  return [
    createAttachmentsPlugin(), createChartsPlugin(), createConcealedContentPlugin(), createContactsPlugin(), createDiagramsPlugin(),
    createEditorToolsPlugin(), createEmojiPlugin(), createHeadingAnchorsPlugin(), createMathPlugin(), createOpenApiPlugin(), createPagesPlugin(),
    createTocPlugin(), createYouTrackPlugin({ client: { search: async () => [] } }),
  ]
}

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }
const stable = (value: Json): Json => Array.isArray(value) ? value.map(stable)
  : value !== null && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key]!)])) : value

/** The canonical document of yjsCorpusDocuments.test.ts (YjsDocumentTreeTests.Canonical, hashed the same way): elements, attributes, text run lengths and formats. */
function yjsCanonical(doc: Y.Doc): string {
  const children = (type: Y.XmlFragment | Y.XmlElement): Json[] => type.toArray().flatMap((child): Json[] => {
    if (child instanceof Y.XmlElement) {
      const attrs = Object.fromEntries(Object.entries(child.getAttributes()).filter(([, value]) => value !== undefined)) as Json
      return [{ t: child.nodeName, a: stable(attrs), c: children(child) }]
    }
    if (!(child instanceof Y.XmlText)) return []
    const runs: { x: number; m: Json }[] = []
    for (const op of child.toDelta() as { insert: unknown; attributes?: Json }[]) {
      if (typeof op.insert !== 'string') continue
      const marks = stable(op.attributes ?? {})
      const last = runs.at(-1)
      if (last && JSON.stringify(last.m) === JSON.stringify(marks)) last.x += op.insert.length
      else runs.push({ x: op.insert.length, m: marks })
    }
    return runs
  })
  return JSON.stringify(stable(children(doc.getXmlFragment('default'))))
}

interface Client {
  id: number
  doc: Y.Doc
  editor: Editor
  controller: ReturnType<typeof createMarkdownEditorController>
  /** Provider outbox: the head is in flight; a new local update merges into the tail when that was not sent yet. */
  outbox: { update: Uint8Array; attempted: boolean }[]
  inbox: Uint8Array[]
  online: boolean
}

const REMOTE = Symbol('remote')

function open(id: number, initial?: Uint8Array, profile: 'rich' | 'markdown-safe' = 'rich'): Client {
  const doc = new Y.Doc()
  if (initial) Y.applyUpdate(doc, initial, REMOTE)
  const client: Client = { id, doc, editor: null as unknown as Editor, controller: null as never, outbox: [], inbox: [], online: true }
  doc.on('update', (update: Uint8Array, origin: unknown) => {
    if (origin === REMOTE) return
    const tail = client.outbox.at(-1)
    if (tail && !tail.attempted) tail.update = Y.mergeUpdates([tail.update, update])
    else client.outbox.push({ update: update.slice(), attempted: false })
  })
  const adapter = registerCollaborationAdapter(publish => ({
    // As useCollab binds them.
    extensions: [Collaboration.configure({ document: doc, field: 'default' }), CollaborationRemoteSelection, CollaborationTableRepair],
    isRemoteTransaction: () => false,
    attachEditor: () => publish({ hydrated: true, writable: true }),
    detachEditor() {}, destroy() {},
  }))
  const controller = createMarkdownEditorController({ profile, plugins: profile === 'rich' ? plugins() : [], source: { kind: 'collaborative', adapter } })
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: `fuzz ${id}` })
  client.controller = controller
  client.editor = editorForController(controller)!
  sessions.push({ destroy() { controller.destroy(); host.remove(); doc.destroy() } })
  return client
}

function random(seed: number) {
  let state = seed >>> 0 || 1
  const next = () => { state ^= state << 13; state ^= state >>> 17; state ^= state << 5; return (state >>> 0) / 0x1_0000_0000 }
  return { next, int: (n: number) => Math.floor(next() * n), pick: <T>(items: readonly T[]) => items[Math.floor(next() * items.length)]! }
}

const WORDS = ['a', 'xy', 'Žluť', 'emoji 🚀', ' ', 'kůň', 'lorem ipsum', '\u{1F600}']

const INITIAL = {
  type: 'doc',
  content: [
    { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Title' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'First ' }, { type: 'text', text: 'bold', marks: [{ type: 'bold' }] }, { type: 'text', text: ' paragraph' }] },
    { type: 'bulletList', content: [1, 2, 3].map(n => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: `item ${n}` }] }] })) },
    { type: 'blockquote', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'quote one' }] }, { type: 'paragraph', content: [{ type: 'text', text: 'quote two' }] }] },
    { type: 'table', content: [0, 1, 2].map(row => ({ type: 'tableRow', content: [0, 1, 2].map(col => ({
      type: row === 0 ? 'tableHeader' : 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text: `c${row}${col}` }] }],
    })) })) },
    { type: 'orderedList', content: [1, 2].map(n => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text: `step ${n}` }] }] })) },
    { type: 'codeBlock', content: [{ type: 'text', text: 'const x = 1' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'Last paragraph with a ' }, { type: 'text', text: 'link', marks: [{ type: 'link', attrs: { href: 'https://example.com' } }] }] },
  ],
}

type Rng = ReturnType<typeof random>

function textPositions(editor: Editor): number[] {
  const positions: number[] = []
  editor.state.doc.descendants((node, pos) => {
    if (node.isTextblock) for (let offset = 0; offset <= node.content.size; offset++) positions.push(pos + 1 + offset)
    return true
  })
  return positions
}

function blockPositions(editor: Editor): number[] {
  const positions: number[] = []
  editor.state.doc.forEach((_node, offset) => positions.push(offset))
  return positions
}

/** One user action through the editor's commands, as the toolbar, keyboard and menus run them. */
function edit(client: Client, rng: Rng): string {
  const editor = client.editor
  const chain = () => editor.chain().focus()
  const positions = textPositions(editor)
  if (positions.length === 0) return 'empty'
  const at = rng.pick(positions)
  const range = () => {
    const end = Math.min(editor.state.doc.content.size, at + 1 + rng.int(12))
    return { from: at, to: Math.max(at, end) }
  }
  const actions: [number, string, () => unknown][] = [
    [30, 'type', () => chain().setTextSelection(at).insertContent(rng.pick(WORDS)).run()],
    [8, 'delete', () => chain().setTextSelection(range()).deleteSelection().run()],
    [4, 'backspace', () => chain().setTextSelection(at).joinBackward().run()],
    [4, 'enter', () => chain().setTextSelection(at).splitBlock().run()],
    [3, 'listEnter', () => chain().setTextSelection(at).splitListItem('listItem').run()],
    [6, 'bold', () => chain().setTextSelection(range()).toggleBold().run()],
    [3, 'italic', () => chain().setTextSelection(range()).toggleItalic().run()],
    [3, 'code', () => chain().setTextSelection(range()).toggleCode().run()],
    [3, 'link', () => chain().setTextSelection(range()).setLink({ href: `https://example.com/${rng.int(100)}` }).run()],
    [2, 'unlink', () => chain().setTextSelection(range()).unsetLink().run()],
    [2, 'heading', () => chain().setTextSelection(at).toggleHeading({ level: (1 + rng.int(6)) as 1 }).run()],
    [2, 'codeBlock', () => chain().setTextSelection(at).toggleCodeBlock().run()],
    [2, 'blockquote', () => chain().setTextSelection(at).toggleBlockquote().run()],
    [2, 'bulletList', () => chain().setTextSelection(at).toggleBulletList().run()],
    [2, 'orderedList', () => chain().setTextSelection(at).toggleOrderedList().run()],
    [2, 'sink', () => chain().setTextSelection(at).sinkListItem('listItem').run()],
    [2, 'lift', () => chain().setTextSelection(at).liftListItem('listItem').run()],
    [2, 'align', () => chain().setTextSelection(at).setTextAlign(rng.pick(['left', 'center', 'right'])).run()],
    [1, 'insertTable', () => chain().setTextSelection(at).insertTable({ rows: 2, cols: 2, withHeaderRow: rng.next() < 0.5 }).run()],
    [3, 'addRow', () => chain().setTextSelection(at).addRowAfter().run()],
    [3, 'addColumn', () => chain().setTextSelection(at).addColumnAfter().run()],
    [2, 'deleteRow', () => chain().setTextSelection(at).deleteRow().run()],
    [2, 'deleteColumn', () => chain().setTextSelection(at).deleteColumn().run()],
    [2, 'mergeCells', () => {
      const to = rng.pick(positions)
      const [from, end] = at < to ? [at, to] : [to, at]
      return editor.chain().focus().setCellSelection({ anchorCell: from - 1, headCell: end - 1 }).mergeCells().run()
    }],
    [2, 'splitCell', () => chain().setTextSelection(at).splitCell().run()],
    [1, 'headerRow', () => chain().setTextSelection(at).toggleHeaderRow().run()],
    [1, 'deleteTable', () => chain().setTextSelection(at).deleteTable().run()],
    [2, 'hardBreak', () => chain().setTextSelection(at).setHardBreak().run()],
    [1, 'rule', () => chain().setTextSelection(at).setHorizontalRule().run()],
    [3, 'moveBlock', () => {
      const blocks = blockPositions(editor)
      return moveBlock(editor, rng.pick(blocks), rng.next() < 0.5 ? -1 : 1)
    }],
    [2, 'deleteBlock', () => {
      const blocks = blockPositions(editor)
      const from = rng.pick(blocks)
      return editor.view.dispatch(editor.state.tr.delete(from, from + editor.state.doc.nodeAt(from)!.nodeSize))
    }],
    [2, 'paste', () => chain().setTextSelection(range()).insertContent(rng.pick([
      '<p>pasted <strong>bold</strong> and <a href="https://example.com/p">link</a></p>',
      '<ul><li><p>pasted</p><ul><li><p>nested</p></li></ul></li></ul>',
      '<blockquote><p>quoted</p></blockquote><h3>h</h3>',
      '<table><tr><th>a</th><th>b</th></tr><tr><td>1</td><td>2</td></tr></table>',
    ])).run()],
    [4, 'undo', () => client.controller.dispatch({ type: 'undo' })],
    [2, 'redo', () => client.controller.dispatch({ type: 'redo' })],
  ]
  let roll = rng.int(actions.reduce((sum, [weight]) => sum + weight, 0))
  for (const [weight, name, run] of actions) {
    if ((roll -= weight) >= 0) continue
    try { run() } catch { /* a command that does not apply here */ }
    return name
  }
  return 'none'
}

interface Trace {
  seed: number
  baseline: string
  /** The relay's whole state at the end, as a snapshot pushes it. */
  final?: string
  /** authorDoc: the sender's own document passes ProseMirror's check; relayDoc: the merged document does. */
  steps: { client: number; update: string; hash: string; actions: string[]; authorDoc: string; relayDoc: string }[]
}

const check = (run: () => { check(): void }) => { try { run().check(); return 'valid' } catch (error) { return String(error) } }

const b64 = (bytes: Uint8Array) => btoa(Array.from(bytes, byte => String.fromCharCode(byte)).join(''))

/**
 * A document that integrates updates as they are. A receiving Yjs document deletes formats it finds redundant after each remote
 * transaction (cleanupYTextAfterTransaction) and its editor sends those deletions as an update of its own; the relay stores
 * updates as they are, so the guard's replica must hold the plain integration. (The cleanup is not neutral under concurrency:
 * text inserted later between a deleted start and its end format takes the format only where the cleanup did not run.)
 */
function passive(doc: Y.Doc): Y.Doc {
  doc.on('afterTransaction', (transaction: Y.Transaction) => { (transaction as unknown as { _needFormattingCleanup: boolean })._needFormattingCleanup = false })
  return doc
}

async function run(seed: number, steps: number): Promise<Trace> {
  const rng = random(seed)
  const seeder = open(0)
  seeder.editor.commands.setContent(INITIAL)
  seeder.outbox.length = 0
  const baseline = Y.encodeStateAsUpdate(seeder.doc)
  const relay = passive(new Y.Doc())
  Y.applyUpdate(relay, baseline)
  const clients = [seeder, open(1, baseline), open(2, baseline)]
  const trace: Trace = { seed, baseline: b64(baseline), steps: [] }
  const actions = new Map<number, string[]>(clients.map(client => [client.id, []]))
  const send = (client: Client) => {
    const head = client.outbox.shift()
    if (!head) return
    Y.applyUpdate(relay, head.update)
    // A copy: y-prosemirror deletes the elements it cannot build.
    const copy = new Y.Doc()
    passive(copy)
    Y.applyUpdate(copy, Y.encodeStateAsUpdate(relay))
    trace.steps.push({
      client: client.id, update: b64(head.update), hash: createHash('sha256').update(yjsCanonical(relay)).digest('hex').slice(0, 16), actions: actions.get(client.id)!.splice(0),
      authorDoc: check(() => client.editor.state.doc),
      relayDoc: check(() => yXmlFragmentToProseMirrorRootNode(copy.getXmlFragment('default'), client.editor.schema)),
    })
    for (const other of clients) if (other !== client) other.inbox.push(head.update)
  }
  const receive = async (client: Client, count: number) => {
    for (const update of client.inbox.splice(0, count)) {
      // An editor that throws here ends its session in the product (the provider fails); reported, not hidden.
      try { Y.applyUpdate(client.doc, update, REMOTE) }
      catch (error) { editorErrors.push(`seed ${seed} client ${client.id}: ${String(error)}`) }
    }
    // A table repair follows a remote change as a change of its own.
    await Promise.resolve()
  }
  for (let step = 0; step < steps; step++) {
    const client = rng.pick(clients)
    const roll = rng.next()
    if (roll < 0.5) actions.get(client.id)!.push(edit(client, rng))
    else if (roll < 0.7) { if (client.online) { const head = client.outbox[0]; if (head) head.attempted = true; if (rng.next() < 0.7) send(client) } }
    else if (roll < 0.9) { if (client.online) await receive(client, 1 + rng.int(4)) }
    else client.online = !client.online
  }
  // Everyone reconnects and drains: the relay holds every edit, every editor converges on it.
  for (let round = 0; round < 4; round++) for (const client of clients) {
    client.online = true
    while (client.outbox.length) send(client)
    await receive(client, client.inbox.length)
  }
  for (const client of clients) expect(yjsCanonical(client.doc)).toBe(yjsCanonical(relay))
  trace.final = b64(Y.encodeStateAsUpdate(relay))
  return trace
}

const editorErrors: string[] = []
const seeds = (env.ATLAS_YJS_FUZZ_SEEDS ?? '1-1').split('-').map(Number)
const steps = Number(env.ATLAS_YJS_FUZZ_STEPS ?? 150)

it.skipIf(!out)('records concurrent real-editor sessions for the guard replay', async () => {
  fs.mkdirSync(out!, { recursive: true })
  for (let seed = seeds[0]!; seed <= (seeds[1] ?? seeds[0]!); seed++) {
    const trace = await run(seed, steps)
    fs.writeFileSync(`${out}/seed-${seed}.json`, JSON.stringify(trace))
    sessions.splice(0).forEach(session => session.destroy())
  }
  expect(editorErrors, 'editors that threw on a remote update (the provider would end their session)').toEqual([])
}, 0)

/** Delivers every pending update to every other client until no repair answers any more (or the round limit). */
async function exchange(clients: Client[], rounds = 12): Promise<void> {
  for (let round = 0; round < rounds && clients.some(client => client.outbox.length); round++) {
    const sent = clients.map(client => client.outbox.splice(0).map(entry => entry.update))
    clients.forEach((client, index) => sent.forEach((updates, from) => {
      if (from !== index) for (const update of updates) Y.applyUpdate(client.doc, update, REMOTE)
    }))
    // A table repair follows a remote change as a change of its own.
    await Promise.resolve()
  }
}

const tableShape = (client: Client) => {
  const rows: number[] = []
  client.editor.state.doc.descendants(node => {
    if (node.type.name === 'tableRow') rows.push(node.childCount)
    return node.type.name !== 'tableRow'
  })
  return rows
}

function textAt(client: Client, text: string): number {
  let found = -1
  client.editor.state.doc.descendants((node, pos) => { if (found < 0 && node.isText && node.text === text) found = pos })
  return found
}

function collaborators(count: number, profile?: 'rich' | 'markdown-safe'): Client[] {
  const seeder = open(0, undefined, profile)
  seeder.editor.commands.setContent(INITIAL)
  seeder.outbox.length = 0
  const baseline = Y.encodeStateAsUpdate(seeder.doc)
  return [seeder, ...Array.from({ length: count - 1 }, (_, index) => open(index + 1, baseline, profile))]
}

/** The cell count of each row of every table in the shared Yjs document (what the relay and the guard hold). */
const yTableShape = (client: Client) => {
  const rows: number[] = []
  const walk = (type: Y.XmlFragment | Y.XmlElement): void => type.toArray().forEach(child => {
    if (!(child instanceof Y.XmlElement)) return
    if (child.nodeName === 'tableRow') rows.push(child.length)
    else walk(child)
  })
  walk(client.doc.getXmlFragment('default'))
  return rows
}

it.each(['rich', 'markdown-safe'] as const)('converges when one collaborator adds a table row while another adds a column at the same instant (%s)', async profile => {
  const clients = collaborators(3, profile)
  const [, rows, columns] = clients as [Client, Client, Client]
  rows.editor.chain().setTextSelection(textAt(rows, 'c11')).addRowAfter().run()
  columns.editor.chain().setTextSelection(textAt(columns, 'c11')).addColumnAfter().run()
  await exchange(clients)
  // Everyone keeps typing: a repair appended to the remote change used to stay out of Yjs and ride along with each client's next edit.
  for (let turn = 0; turn < 3; turn++) {
    for (const client of clients) client.editor.chain().setTextSelection(textAt(client, 'Title') + 1).insertContent('x').run()
    await exchange(clients)
  }
  for (const client of clients) {
    expect(tableShape(client), `client ${client.id}`).toEqual([4, 4, 4, 4])
    expect(yTableShape(client), `client ${client.id}`).toEqual([4, 4, 4, 4])
    expect(yjsCanonical(client.doc)).toBe(yjsCanonical(clients[0]!.doc))
  }
})

it.each(['rich', 'markdown-safe'] as const)('keeps tables rectangular, bounded and convergent under concurrent row and column edits (%s)', async profile => {
  const commands = ['addRowBefore', 'addRowAfter', 'addColumnBefore', 'addColumnAfter', 'deleteRow', 'deleteColumn'] as const
  for (let seed = 1; seed <= 25; seed++) {
    const rng = random(seed)
    const clients = collaborators(3, profile)
    let added = 0
    for (let burst = 0; burst < 4; burst++) {
      for (const client of clients) {
        const cells: number[] = []
        client.editor.state.doc.descendants((node, pos, parent) => { if (node.type.name === 'paragraph' && parent?.type.spec.tableRole === 'cell') cells.push(pos + 1) })
        if (!cells.length || rng.next() < 0.2) continue
        const command = rng.pick(commands)
        if (command.startsWith('add')) added++
        client.editor.chain().setTextSelection(rng.pick(cells))[command]().run()
      }
      await exchange(clients)
      for (const client of clients) client.editor.chain().setTextSelection(textAt(client, 'Title') + 1).insertContent('x').run()
      await exchange(clients)
    }
    const shape = tableShape(clients[0]!)
    for (const client of clients) {
      expect(tableShape(client), `seed ${seed} client ${client.id}`).toEqual(shape)
      expect(yTableShape(client), `seed ${seed} client ${client.id}`).toEqual(shape)
      expect(yjsCanonical(client.doc), `seed ${seed}`).toBe(yjsCanonical(clients[0]!.doc))
    }
    expect(new Set(shape).size, `seed ${seed}: ${shape}`).toBeLessThanOrEqual(1)
    // Each add makes one row or column; a three-way conflict may repair twice, adding one more.
    expect(shape.length + (shape[0] ?? 0), `seed ${seed}: ${shape}`).toBeLessThanOrEqual(6 + 2 * added)
    sessions.splice(0).forEach(session => session.destroy())
  }
}, 60_000)
