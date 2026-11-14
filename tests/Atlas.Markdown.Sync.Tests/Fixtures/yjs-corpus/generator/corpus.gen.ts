// Builds the H1/H2/H6 corpus from the real editor. Output: ../corpus/<scenario>/*.bin + ../corpus/manifest.json
import { createRequire } from 'node:module'
import { it } from 'vitest'
import { openSession, Y } from './harness'
import { factsFor } from './facts'
const require = createRequire(import.meta.url)
const fs = require('node:fs') as typeof import('node:fs')
const path = require('node:path') as typeof import('node:path')
const OUT = path.resolve(import.meta.dirname, '../corpus')
const RESERVED = ['secretReference', 'secretInlineReference']

interface Entry { scenario: string; seq: number; label: string; kind: 'state' | 'update'; file: string; bytes: number; facts?: any; expect: 'accept' | 'reject'; reason?: string }
const manifest: Entry[] = []
let schemaInfo: any

function verdict(f: any): { expect: 'accept' | 'reject'; reason?: string } {
  const nodes = new Set(schemaInfo.nodes.map((n: any) => n.name)), marks = new Set(schemaInfo.marks.map((m: any) => m.name))
  const attrOk = new Map<string, Set<string>>(schemaInfo.nodes.map((n: any) => [n.name, new Set(n.attrs)]))
  if (f.pendingStructs || f.pendingDs) return { expect: 'reject', reason: 'pending' }
  if (f.otherRoots.length) return { expect: 'reject', reason: 'other-root' }
  if (f.embeds) return { expect: 'reject', reason: 'embed' }
  for (const e of f.elements) { if (RESERVED.includes(e)) return { expect: 'reject', reason: `reserved:${e}` }; if (!nodes.has(e)) return { expect: 'reject', reason: `unknown-node:${e}` } }
  for (const m of f.marks) { const base = m.split('--')[0]; if (!marks.has(base)) return { expect: 'reject', reason: `unknown-mark:${m}` } }
  for (const a of f.attrs) { const [n, k] = a.split('.'); if (n === '#text') return { expect: 'reject', reason: `text-attr:${k}` }; if (RESERVED.includes(n)) return { expect: 'reject', reason: `reserved-attr:${a}` }; if (attrOk.has(n) && !attrOk.get(n)!.has(k)) return { expect: 'reject', reason: `unknown-attr:${a}` } }
  return { expect: 'accept' }
}

class Scenario {
  seq = 0
  replica = new Y.Doc()
  constructor(public name: string) { fs.mkdirSync(path.join(OUT, name), { recursive: true }) }
  private write(label: string, kind: 'state' | 'update', bytes: Uint8Array, facts?: any, forced?: { expect: 'accept' | 'reject'; reason?: string }) {
    const file = `${this.name}/${String(this.seq).padStart(3, '0')}-${label.replace(/[^a-zA-Z0-9_-]+/g, '_')}.${kind}.bin`
    fs.writeFileSync(path.join(OUT, file), bytes)
    const v = forced ?? (facts ? verdict(facts) : { expect: 'accept' as const })
    manifest.push({ scenario: this.name, seq: this.seq++, label, kind, file, bytes: bytes.length, facts, ...v })
  }
  state(label: string, doc: Y.Doc, forced?: { expect: 'accept' | 'reject'; reason?: string }) {
    const bytes = Y.encodeStateAsUpdate(doc)
    const facts = factsFor(new Y.Doc(), bytes)
    this.write(label, 'state', bytes, facts, forced)
  }
  update(label: string, bytes: Uint8Array) {
    const facts = factsFor(this.replica, bytes)
    this.write(label, 'update', bytes, facts)
  }
  updates(label: string, list: Uint8Array[]) {
    if (!list.length) { manifest.push({ scenario: this.name, seq: -1, label: `${label} (NO UPDATE EMITTED)`, kind: 'update', file: '', bytes: 0, expect: 'accept' }); return }
    list.forEach((u, i) => this.update(list.length > 1 ? `${label}#${i}` : label, u))
  }
  /** Replica must track everything the session emitted before the next capture. */
  sync(doc: Y.Doc) { Y.applyUpdate(this.replica, Y.encodeStateAsUpdate(doc, Y.encodeStateVector(this.replica))) }
}

const SAMPLE_ATTRS: Record<string, any> = {
  youtrackIssue: { id: '2-17', key: 'ATL-17', title: 'Crash on save', url: 'https://youtrack.example/issue/ATL-17', state: 'Open' },
  pageReference: { id: 'b7e1c0de-0000-4000-8000-000000000001', title: 'Runbook', url: '/pages/runbook' },
  emoji: { name: 'rocket' },
  image: { src: 'https://example.com/pic.png', alt: 'pic', title: null, width: null, height: null },
  secretReference: { secretId: 'd847b3af-4a87-4e97-aab8-a74a4cf48c89', field: 'password', fields: ['username', 'password', 'url'], showCopy: true, showDetails: true, autoHideSeconds: 60 },
  secretInlineReference: { secretId: 'd847b3af-4a87-4e97-aab8-a74a4cf48c89', field: 'password', fields: null, showCopy: true, showDetails: false, autoHideSeconds: 5 },
  heading: { level: 2 },
  codeBlock: { language: 'typescript' },
  callout: { variant: 'warning' },
  disclosure: { title: 'Details' },
}
const T = (text: string, marks: any[] = []) => ({ type: 'text', text, ...(marks.length ? { marks: marks.map(m => typeof m === 'string' ? { type: m } : m) } : {}) })
const P = (text: string) => ({ type: 'paragraph', content: [T(text)] })
const CHILD_ONLY = new Set(['doc', 'text', 'listItem', 'tableRow', 'tableHeader', 'tableCell', 'taskItem'])

function nodeJson(editor: any, name: string) {
  const type = editor.schema.nodes[name]
  const attrs = SAMPLE_ATTRS[name]
  const node = type.createAndFill(attrs) ?? type.create(attrs)
  return node.toJSON()
}
function findPos(editor: any, name: string): { pos: number; node: any } | null {
  let hit: any = null
  editor.state.doc.descendants((n: any, pos: number) => { if (!hit && n.type.name === name) { hit = { pos, node: n }; return false } return !hit })
  return hit
}
const PREFERRED = ['title', 'caption', 'source', 'expression', 'name', 'label', 'text', 'notes', 'description', 'fileName', 'email']
function mutateValue(v: any): any {
  if (typeof v === 'string') return v + ' edited'
  if (typeof v === 'number') return v + 1
  if (typeof v === 'boolean') return !v
  if (v && typeof v === 'object') {
    const out = structuredClone(v)
    const leaves: Array<[any, string | number]> = []
    const walk = (o: any) => { for (const k of Object.keys(o)) { if (typeof o[k] === 'string') leaves.push([o, Array.isArray(o) ? Number(k) : k]); else if (o[k] && typeof o[k] === 'object') walk(o[k]) } }
    walk(out)
    const pick = leaves.find(([, k]) => PREFERRED.includes(String(k))) ?? leaves.find(([, k]) => !['version', 'id', 'type', 'mode', 'kind'].includes(String(k)))
    if (pick) { const cur = pick[0][pick[1]] as string; pick[0][pick[1]] = pick[1] === 'source' ? (cur.includes('openapi') ? cur + '\n# edited' : cur + '\n') : cur + ' edited' }
    return out
  }
  return v
}
function lastParagraphEnd(editor: any) {
  let end = -1
  editor.state.doc.descendants((n: any, pos: number) => { if (n.type.name === 'paragraph') end = pos + n.nodeSize - 1 })
  return end
}

it('builds corpus', () => {
  fs.mkdirSync(OUT, { recursive: true }); for (const d of fs.readdirSync(OUT)) if (d !== 'perf') fs.rmSync(path.join(OUT, d), { recursive: true, force: true })
  const probe = openSession()
  schemaInfo = {
    fragment: 'default',
    nodes: Object.entries(probe.editor.schema.nodes).map(([n, t]: any) => ({ name: n, inline: t.isInline, attrs: Object.keys(t.spec.attrs ?? {}) })),
    marks: Object.entries(probe.editor.schema.marks).map(([n, t]: any) => ({ name: n, attrs: Object.keys(t.spec.attrs ?? {}) })),
    reserved: RESERVED,
    documentSchema: probe.controller.schema,
  }
  probe.destroy()
  fs.writeFileSync(path.join(OUT, 'schema.json'), JSON.stringify(schemaInfo, null, 1))

  // ---- per node type: insert / change attrs / type inside / delete ----
  for (const { name, inline } of schemaInfo.nodes) {
    if (CHILD_ONLY.has(name)) continue
    const s = openSession(); const sc = new Scenario(`node-${name}`)
    s.capture(e => e.commands.insertContent(P('Hello world')))
    sc.sync(s.doc); sc.state('baseline', s.doc)
    const json = nodeJson(s.editor, name)
    const ins = s.capture(e => {
      if (name === 'table') e.chain().setTextSelection(lastParagraphEnd(e)).insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run()
      else if (inline) e.commands.insertContentAt(lastParagraphEnd(e), json)
      else e.commands.insertContentAt(e.state.doc.content.size, json)
    })
    sc.updates(`insert ${name}`, ins)
    const hit = findPos(s.editor, name)
    if (hit) {
      const attrNames = Object.keys(hit.node.attrs)
      const pickT = (t: string) => attrNames.find(k => typeof hit.node.attrs[k] === t && !['textAlign', 'headingId', 'secretId', 'id', 'chartRef'].includes(k))
        const key = attrNames.find(k => k === 'data') ?? pickT('boolean') ?? pickT('number') ?? pickT('string') ?? attrNames[0]
      if (key) {
        const upd = s.capture(e => e.view.dispatch(e.state.tr.setNodeMarkup(hit.pos, undefined, { ...hit.node.attrs, [key]: mutateValue(hit.node.attrs[key] ?? 'x') })))
        sc.updates(`attr ${name}.${key}`, upd)
      }
      const h2 = findPos(s.editor, name)!
      if (h2.node.isTextblock || h2.node.firstChild?.isTextblock) {
        const inner = h2.pos + (h2.node.isTextblock ? 1 : 2)
        sc.updates(`type into ${name}`, s.capture(e => { for (const c of 'abc') e.view.dispatch(e.state.tr.insertText(c, inner)) }))
      }
      const h3 = findPos(s.editor, name)!
      sc.updates(`delete ${name}`, s.capture(e => e.view.dispatch(e.state.tr.delete(h3.pos, h3.pos + h3.node.nodeSize))))
    } else {
      manifest.push({ scenario: sc.name, seq: -1, label: `insert ${name} NOT FOUND IN PM DOC`, kind: 'update', file: '', bytes: 0, expect: 'accept' })
    }
    // re-insert so the final state contains the node
    s.capture(e => { if (name === 'table') e.chain().setTextSelection(lastParagraphEnd(e)).insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run(); else if (inline) e.commands.insertContentAt(lastParagraphEnd(e), json); else e.commands.insertContentAt(e.state.doc.content.size, json) })
    sc.state('final', s.doc)
    s.destroy()
  }

  // ---- typical editing: typing, marks, structure ----
  {
    const s = openSession(); const sc = new Scenario('editing')
    s.capture(e => e.commands.insertContent([P('The quick brown fox jumps over the lazy dog.'), P('Second paragraph.')]))
    sc.sync(s.doc); sc.state('baseline', s.doc)
    const E = s.editor
    sc.updates('type 5 chars', s.capture(e => { for (const c of 'Hello') e.view.dispatch(e.state.tr.insertText(c, 5)) }))
    sc.updates('bold', s.capture(e => e.chain().setTextSelection({ from: 5, to: 10 }).toggleBold().run()))
    sc.updates('italic', s.capture(e => e.chain().setTextSelection({ from: 8, to: 14 }).toggleItalic().run()))
    sc.updates('underline', s.capture(e => e.chain().setTextSelection({ from: 15, to: 20 }).toggleUnderline().run()))
    sc.updates('strike', s.capture(e => e.chain().setTextSelection({ from: 21, to: 25 }).toggleStrike().run()))
    sc.updates('highlight', s.capture(e => e.chain().setTextSelection({ from: 26, to: 30 }).toggleHighlight().run()))
    sc.updates('code', s.capture(e => e.chain().setTextSelection({ from: 31, to: 34 }).toggleCode().run()))
    sc.updates('link', s.capture(e => e.chain().setTextSelection({ from: 35, to: 40 }).setLink({ href: 'https://example.com' }).run()))
    sc.updates('change link href', s.capture(e => e.chain().setTextSelection({ from: 35, to: 40 }).extendMarkRange('link').setLink({ href: 'https://example.org/x' }).run()))
    sc.updates('unbold', s.capture(e => e.chain().setTextSelection({ from: 5, to: 10 }).toggleBold().run()))
    sc.updates('type inside marked text', s.capture(e => { for (const c of 'XY') e.view.dispatch(e.state.tr.insertText(c, 9)) }))
    sc.updates('enter (split)', s.capture(e => e.chain().setTextSelection(12).splitBlock().run()))
    sc.updates('heading', s.capture(e => e.chain().setTextSelection(3).setNode('heading', { level: 2 }).run()))
    sc.updates('heading level', s.capture(e => e.chain().setTextSelection(3).setNode('heading', { level: 3 }).run()))
    sc.updates('text align', s.capture(e => e.chain().setTextSelection(3).setTextAlign('center').run()))
    sc.updates('bullet list', s.capture(e => { const p = lastParagraphEnd(e) - 1; e.chain().setTextSelection(p).toggleBulletList().run() }))
    sc.updates('ordered list', s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, { type: 'orderedList', attrs: { start: 3 }, content: [{ type: 'listItem', content: [P('three')] }, { type: 'listItem', content: [P('four')] }] })))
    sc.updates('task list', s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: false }, content: [P('todo')] }] })))
    sc.updates('check task', s.capture(e => { const h = findPos(e, 'taskItem')!; e.view.dispatch(e.state.tr.setNodeMarkup(h.pos, undefined, { ...h.node.attrs, checked: true })) }))
    sc.updates('blockquote', s.capture(e => e.chain().setTextSelection(3).toggleBlockquote().run()))
    sc.updates('code block', s.capture(e => { e.commands.insertContentAt(e.state.doc.content.size, { type: 'codeBlock', attrs: { language: 'ts' }, content: [{ type: 'text', text: 'const a = 1' }] }) }))
    sc.updates('table insert', s.capture(e => { e.chain().setTextSelection(e.state.doc.content.size - 1).insertTable({ rows: 2, cols: 3, withHeaderRow: true }).run() }))
    sc.updates('table type in cell', s.capture(e => { const h = findPos(e, 'tableCell')!; for (const c of 'cell') e.view.dispatch(e.state.tr.insertText(c, h.pos + 2)) }))
    sc.updates('table add row', s.capture(e => { const h = findPos(e, 'tableCell')!; e.chain().setTextSelection(h.pos + 2).addRowAfter().run() }))
    sc.updates('table add column', s.capture(e => { const h = findPos(e, 'tableCell')!; e.chain().setTextSelection(h.pos + 2).addColumnAfter().run() }))
    sc.updates('cell background', s.capture(e => { const h = findPos(e, 'tableCell')!; e.view.dispatch(e.state.tr.setNodeMarkup(h.pos, undefined, { ...h.node.attrs, backgroundColor: '#ffeeaa' })) }))
    sc.updates('paste rich html', s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, [{ type: 'heading', attrs: { level: 3 }, content: [T('Pasted')] }, { type: 'bulletList', content: [{ type: 'listItem', content: [{ type: 'paragraph', content: [T('b', ['bold']), T(' '), T('i', ['italic']), T(' '), T('l', [{ type: 'link', attrs: { href: 'https://a.b' } }])] }] }] }])))
    sc.updates('delete range', s.capture(e => e.view.dispatch(e.state.tr.delete(3, 12))))
    sc.updates('delete all', s.capture(e => e.commands.clearContent()))
    sc.state('final', s.doc)
    s.destroy()
  }

  // ---- everything document: all node types + all marks, as one full state ----
  {
    const s = openSession(); const sc = new Scenario('all-nodes')
    const E = s.editor
    s.capture(e => e.commands.insertContent({ type: 'paragraph', content: [T('b', ['bold']), T(' '), T('i', ['italic']), T(' '), T('u', ['underline']), T(' '), T('s', ['strike']), T(' '), T('h', ['highlight']), T(' '), T('c', ['code']), T(' '), T('l', [{ type: 'link', attrs: { href: 'https://x.y' } }]), T(' '), T('bi', ['bold', 'italic'])] }))
    for (const { name, inline } of schemaInfo.nodes) {
      if (CHILD_ONLY.has(name) || RESERVED.includes(name)) continue
      const json = nodeJson(E, name)
      s.capture(e => { if (inline) e.commands.insertContentAt(lastParagraphEnd(e), json); else e.commands.insertContentAt(e.state.doc.content.size, json) })
    }
    s.capture(e => e.chain().setTextSelection(e.state.doc.content.size - 1).insertTable({ rows: 2, cols: 2, withHeaderRow: true }).run())
    s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, [{ type: 'bulletList', content: [{ type: 'listItem', content: [P('a')] }] }, { type: 'orderedList', content: [{ type: 'listItem', content: [P('b')] }] }, { type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: true }, content: [P('t')] }] }]))
    sc.state('all-allowed-nodes', s.doc)
    const present = new Set<string>(); E.state.doc.descendants((n: any) => { present.add(n.type.name) })
    fs.writeFileSync(path.join(OUT, 'all-nodes', 'present-in-pm.json'), JSON.stringify([...present].sort()))
    // same doc + secret block and inline references (must be rejected as a snapshot)
    s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, nodeJson(E, 'secretReference')))
    s.capture(e => e.commands.insertContentAt(lastParagraphEnd(e), nodeJson(E, 'secretInlineReference')))
    sc.state('with-secrets', s.doc)
    s.destroy()
  }

  // ---- hostile updates crafted with raw Yjs (an attacker does not use the editor) ----
  {
    const s = openSession(); const sc = new Scenario('hostile')
    s.capture(e => e.commands.insertContent(P('Hello world'))); s.capture(e => e.chain().setTextSelection(e.state.doc.content.size - 1).insertTable({ rows: 2, cols: 2, withHeaderRow: false }).run()); fs.writeFileSync(path.join(OUT, 'hostile-baseline.xml'), s.doc.getXmlFragment('default').toString())
    sc.sync(s.doc); sc.state('baseline', s.doc)
    const baseState = Y.encodeStateAsUpdate(s.doc)
    s.destroy()
    const attacker = () => { const d = new Y.Doc({ gc: false }); Y.applyUpdate(d, baseState); return d }
    const diff = (d: Y.Doc, fn: (d: Y.Doc, frag: Y.XmlFragment) => void) => { const sv = Y.encodeStateVector(d); fn(d, d.getXmlFragment('default')); return Y.encodeStateAsUpdate(d, sv) }
    const para = (frag: Y.XmlFragment) => frag.get(0) as Y.XmlElement
    const cases: Array<[string, (d: Y.Doc, f: Y.XmlFragment) => void]> = [
      ['unknown top-level node', (d, f) => { f.insert(1, [new Y.XmlElement('evilNode')]) }],
      ['script node', (d, f) => { f.insert(1, [new Y.XmlElement('script')]) }],
      ['raw secretReference block', (d, f) => { const el = new Y.XmlElement('secretReference'); for (const [k, v] of Object.entries(SAMPLE_ATTRS.secretReference)) el.setAttribute(k, v as any); f.insert(1, [el]) }],
      ['secretInlineReference deep in table cell', (d, f) => { const p = [...f.createTreeWalker(el => el instanceof Y.XmlElement && el.nodeName === 'paragraph' && ['tableCell', 'tableHeader'].includes((el.parent as any)?.nodeName))][0] as Y.XmlElement; const el = new Y.XmlElement('secretInlineReference'); el.setAttribute('secretId', 'x' as any); p.insert(p.length, [el]) }],
      ['secret nested inside new blockquote subtree', (d, f) => { const bq = new Y.XmlElement('blockquote'); const el = new Y.XmlElement('secretReference'); bq.insert(0, [el]); f.insert(1, [bq]); el.setAttribute('secretId', 'y' as any) }],
      ['unknown attribute on existing paragraph', (d, f) => { para(f).setAttribute('hiddenPayload', 'x' as any) }],
      ['unknown mark on existing text', (d, f) => { (para(f).get(0) as Y.XmlText).format(0, 5, { evilMark: {} }) }],
      ['mark named secretInlineReference', (d, f) => { (para(f).get(0) as Y.XmlText).insert(0, 'z', { secretInlineReference: { secretId: 'x' } }) }],
      ['text embed', (d, f) => { (para(f).get(0) as Y.XmlText).insertEmbed(0, { secret: 'x' }) }],
      ['other root map', (d) => { d.getMap('hidden').set('value', 'x') }],
      ['other root text', (d) => { d.getText('shadow').insert(0, 'secret') }],
      ['secret inserted and deleted in the same update (smuggle)', (d, f) => { d.transact(() => { const el = new Y.XmlElement('secretReference'); for (const [k, v] of Object.entries(SAMPLE_ATTRS.secretReference)) el.setAttribute(k, v as any); f.insert(1, [el]); f.delete(1, 1) }) }],
      ['unknown node inserted and deleted in the same update (smuggle)', (d, f) => { d.transact(() => { f.insert(1, [new Y.XmlElement('evilNode')]); f.delete(1, 1) }) }],
      ['legit type (control)', (d, f) => { (para(f).get(0) as Y.XmlText).insert(5, ' there') }],
      ['legit delete-only (control)', (d, f) => { f.delete(0, 1) }],
    ]
    for (const [label, fn] of cases) {
      const d = attacker(); const u = diff(d, fn)
      const facts = factsFor(Y.decodeUpdate ? (() => { const r = new Y.Doc(); Y.applyUpdate(r, baseState); return r })() : new Y.Doc(), u)
      const file = `hostile/${String(sc.seq).padStart(3, '0')}-${label.replace(/[^a-zA-Z0-9_-]+/g, '_')}.update.bin`
      fs.writeFileSync(path.join(OUT, file), u)
      manifest.push({ scenario: 'hostile', seq: sc.seq++, label, kind: 'update', file, bytes: u.length, facts, ...verdict(facts) })
      d.destroy()
    }
    // pending structs: an update whose parent item is missing
    {
      const d = attacker(); const sv0 = Y.encodeStateVector(d)
      const p = new Y.XmlElement('paragraph'); d.getXmlFragment('default').insert(0, [p]); const sv1 = Y.encodeStateVector(d)
      const t = new Y.XmlText(); p.insert(0, [t]); t.insert(0, 'orphan')
      const u = Y.encodeStateAsUpdate(d, sv1) // depends on the paragraph from (sv0..sv1] which the server never saw
      const facts = factsFor((() => { const r = new Y.Doc(); Y.applyUpdate(r, baseState); return r })(), u)
      const file = `hostile/${String(sc.seq).padStart(3, '0')}-pending_structs.update.bin`
      fs.writeFileSync(path.join(OUT, file), u)
      manifest.push({ scenario: 'hostile', seq: sc.seq++, label: 'pending structs (missing parent)', kind: 'update', file, bytes: u.length, facts, ...verdict(facts) })
      void sv0
    }
    // pending delete set: deletes items the server has not seen
    {
      const d = attacker(); const sv1 = Y.encodeStateVector(d)
      ;(para(d.getXmlFragment('default')).get(0) as Y.XmlText).insert(0, 'unseen')
      const sv2 = Y.encodeStateVector(d)
      ;(para(d.getXmlFragment('default')).get(0) as Y.XmlText).delete(0, 6)
      // delete-set-only update against the unseen insert
      const full = Y.encodeStateAsUpdate(d, sv2)
      const facts = factsFor((() => { const r = new Y.Doc(); Y.applyUpdate(r, baseState); return r })(), full)
      const file = `hostile/${String(sc.seq).padStart(3, '0')}-pending_ds.update.bin`
      fs.writeFileSync(path.join(OUT, file), full)
      manifest.push({ scenario: 'hostile', seq: sc.seq++, label: 'pending delete set (deletes unseen items)', kind: 'update', file, bytes: full.length, facts, ...verdict(facts) })
      void sv1
    }
  }

  fs.writeFileSync(path.join(OUT, 'manifest.json'), JSON.stringify(manifest, null, 1)); fs.writeFileSync(path.join(OUT, 'capture-errors.json'), JSON.stringify((globalThis as any).__captureErrors ?? [], null, 1))
})
