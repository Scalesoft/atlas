// H3/H7 corpus: grow a realistic, fragmented document through the real editor and, at each size
// threshold, save the full state plus the next ~200 typical edits (as the provider would send them).
// Then time the V2 Node authority (processAuthorityRequest) for one update at each size (H7).
import { createRequire } from 'node:module'
import { it } from 'vitest'
import { allPlugins, openSession, Y } from './harness'
const require = createRequire(import.meta.url)
const fs = require('node:fs') as typeof import('node:fs')
const path = require('node:path') as typeof import('node:path')
const OUT = path.resolve(import.meta.dirname, '../corpus/perf')
const SRC = `${import.meta.dirname}/../../../../../src/Atlas.Markdown/src`
const SIZES: Array<[string, number]> = [['50k', 50 * 1024], ['256k', 256 * 1024], ['1m', 1024 * 1024], ['4m', 4 * 1024 * 1024]]
const ONLY = process.env.PERF_ONLY?.split(',')

let seed = 12345
const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x7fffffff }
const pick = <T>(a: T[]) => a[Math.floor(rnd() * a.length)]
const WORDS = 'lorem ipsum dolor sit amet consectetur adipiscing elit sed do eiusmod tempor incididunt ut labore et dolore magna aliqua atlas server replika validace dokument stránka prostor tajemství'.split(' ')
const sentence = (n: number) => Array.from({ length: n }, () => pick(WORDS)).join(' ')
const T = (text: string, marks: any[] = []) => ({ type: 'text', text, ...(marks.length ? { marks: marks.map(m => typeof m === 'string' ? { type: m } : m) } : {}) })
function paragraph() {
  const parts: any[] = [T(sentence(20) + ' ')]
  if (rnd() < 0.5) parts.push(T(sentence(3), [pick(['bold', 'italic', 'underline', 'strike', 'code', 'highlight'])]))
  if (rnd() < 0.2) parts.push(T(' ' + sentence(2), [{ type: 'link', attrs: { href: 'https://example.com/' + Math.floor(rnd() * 1e6) } }]))
  if (rnd() < 0.1) parts.push({ type: 'emoji', attrs: { name: 'rocket' } })
  if (rnd() < 0.05) parts.push({ type: 'youtrackIssue', attrs: { id: '2-1', key: 'ATL-' + Math.floor(rnd() * 999), title: 'Issue', url: 'https://yt.example/issue/ATL-1', state: 'Open' } })
  parts.push(T(' ' + sentence(25)))
  return { type: 'paragraph', content: parts }
}
function block(editor: any, i: number) {
  const kinds = ['mathBlock', 'diagram', 'chart', 'attachment', 'openapi', 'tableOfContents', 'contactCard', 'callout', 'codeBlock', 'list', 'heading']
  const k = kinds[i % kinds.length]
  if (k === 'callout') return { type: 'callout', attrs: { variant: 'info' }, content: [paragraph()] }
  if (k === 'codeBlock') return { type: 'codeBlock', attrs: { language: 'ts' }, content: [T('const x = ' + i + '\n'.repeat(3) + sentence(10))] }
  if (k === 'list') return { type: 'bulletList', content: [1, 2, 3].map(() => ({ type: 'listItem', content: [paragraph()] })) }
  if (k === 'heading') return { type: 'heading', attrs: { level: 2 }, content: [T(sentence(4))] }
  return editor.schema.nodes[k].createAndFill().toJSON()
}
function randomTextPos(editor: any): number {
  const size = editor.state.doc.content.size
  for (let tries = 0; tries < 50; tries++) {
    const p = 1 + Math.floor(rnd() * (size - 2))
    const $p = editor.state.doc.resolve(p)
    if ($p.parent.isTextblock && $p.parent.type.name === 'paragraph') return p
  }
  return 1
}
function findNodes(editor: any, names: string[]) {
  const hits: Array<{ pos: number; node: any }> = []
  editor.state.doc.descendants((n: any, pos: number) => { if (names.includes(n.type.name)) hits.push({ pos, node: n }); return !n.isBlock || n.type.name === 'doc' || !n.isAtom })
  return hits
}
function randomEdit(e: any, i: number) {
  const r = rnd()
  if (r < 0.60) { const p = randomTextPos(e); for (const c of sentence(1).slice(0, 6)) e.view.dispatch(e.state.tr.insertText(c, p)) }
  else if (r < 0.75) { const p = randomTextPos(e); const $p = e.state.doc.resolve(p); const end = Math.min($p.end(), p + 8); if (end > p) e.chain().setTextSelection({ from: p, to: end }).toggleMark(pick(['bold', 'italic', 'underline', 'strike'])).run() }
  else if (r < 0.85) { const p = randomTextPos(e); const $p = e.state.doc.resolve(p); const end = Math.min($p.end(), p + 5); if (end > p) e.view.dispatch(e.state.tr.delete(p, end)) }
  else if (r < 0.92) { const p = randomTextPos(e); e.chain().setTextSelection(p).splitBlock().run() }
  else { const hits = findNodes(e, ['mathBlock', 'diagram', 'chart', 'attachment', 'tableOfContents', 'contactCard']); if (hits.length) { const h = pick(hits); const d = structuredClone(h.node.attrs.data); const key = Object.keys(d).find(k => typeof d[k] === 'string' && ['caption', 'title', 'expression', 'name'].includes(k)); if (key) { d[key] = d[key] + ' ' + i; e.view.dispatch(e.state.tr.setNodeMarkup(h.pos, undefined, { ...h.node.attrs, data: d })) } } }
}
function typicalEdit(e: any, i: number) {
  const r = rnd()
  if (r < 0.55) { const p = randomTextPos(e); e.view.dispatch(e.state.tr.insertText(pick(WORDS).slice(0, 1), p)) }
  else if (r < 0.65) { const p = randomTextPos(e); const $p = e.state.doc.resolve(p); const end = Math.min($p.end(), p + 6); if (end > p) e.chain().setTextSelection({ from: p, to: end }).toggleMark(pick(['bold', 'italic', 'link']), { href: 'https://e.x' }).run() }
  else if (r < 0.72) { const p = randomTextPos(e); e.chain().setTextSelection(p).splitBlock().run() }
  else if (r < 0.78) { e.commands.insertContentAt(e.state.doc.content.size, block(e, i)) }
  else if (r < 0.84) { const hits = findNodes(e, ['mathBlock', 'diagram', 'chart', 'attachment', 'tableOfContents', 'contactCard']); if (hits.length) { const h = pick(hits); const d = structuredClone(h.node.attrs.data); const key = Object.keys(d).find(k => typeof d[k] === 'string' && ['caption', 'title', 'expression', 'name'].includes(k)); if (key) { d[key] = d[key] + ' x'; e.view.dispatch(e.state.tr.setNodeMarkup(h.pos, undefined, { ...h.node.attrs, data: d })) } } }
  else if (r < 0.90) { const p = randomTextPos(e); const $p = e.state.doc.resolve(p); const end = Math.min($p.end(), p + 4); if (end > p) e.view.dispatch(e.state.tr.delete(p, end)) }
  else if (r < 0.95) { const p = randomTextPos(e); const $p = e.state.doc.resolve(p); e.commands.insertContentAt($p.after(1), [paragraph(), paragraph(), paragraph()]) }
  else { const hits = findNodes(e, ['mathBlock', 'diagram', 'callout', 'codeBlock', 'heading']); if (hits.length) { const h = pick(hits); e.view.dispatch(e.state.tr.delete(h.pos, h.pos + h.node.nodeSize)) } }
}

it('builds perf corpus + authority baseline', async () => {
  fs.mkdirSync(OUT, { recursive: true })
  const RESUME = process.env.PERF_RESUME
  let resumeDoc: any
  if (RESUME) {
    resumeDoc = new Y.Doc()
    const dir = path.join(OUT, RESUME)
    Y.applyUpdate(resumeDoc, fs.readFileSync(path.join(dir, 'state.bin')))
    for (const f of fs.readdirSync(dir).filter((x: string) => x.startsWith('u')).sort()) Y.applyUpdate(resumeDoc, fs.readFileSync(path.join(dir, f)))
  }
  const s = openSession({ secrets: false, doc: resumeDoc })
  const E: any = s.editor
  const log: string[] = []
  const t0 = performance.now()
  let batch = 0
  const states: Record<string, { state: Uint8Array; updates: Uint8Array[] }> = {}
  for (const [label, target] of SIZES) {
    if (RESUME && SIZES.findIndex(x => x[0] === label) <= SIZES.findIndex(x => x[0] === RESUME)) continue
    if (ONLY && !ONLY.some(o => o === label) && SIZES.findIndex(x => x[0] === label) > Math.max(...ONLY.map(o => SIZES.findIndex(x => x[0] === o)))) break
    let size = Y.encodeStateAsUpdate(s.doc).length
    while (size < target) {
      const big = size > 900_000
      for (let b = 0; b < (big ? 10 : 1); b++) {
        const content: any[] = Array.from({ length: 20 }, paragraph)
        if ((batch + b) % 3 === 0) content.splice(10, 0, block(E, batch + b))
        const n = s.updates.length
        s.capture(e => e.commands.insertContentAt(e.state.doc.content.size, content))
        if (s.updates.length === n) { const errs = (globalThis as any).__captureErrors ?? []; fs.writeFileSync(path.join(OUT, 'perf-capture-errors.txt'), errs.slice(-5).join(' --- ')) }
      }
      for (let i = 0; i < (big ? 12 : 30); i++) s.capture(e => randomEdit(e, i))
      if (big) { size = Y.encodeStateAsUpdate(s.doc).length; log.push(`  grow ${size} @${Math.round(performance.now() - t0)}ms`); fs.writeFileSync(path.join(OUT, 'perf-log-4m.txt'), log.join('\n')) }
      batch++
      if (batch % 5 === 0) size = Y.encodeStateAsUpdate(s.doc).length
    }
    const state = Y.encodeStateAsUpdate(s.doc)
    const updates: Uint8Array[] = []
    for (let i = 0; updates.length < 200; i++) updates.push(...s.capture(e => typicalEdit(e, i)))
    const dir = path.join(OUT, label); fs.mkdirSync(dir, { recursive: true })
    fs.writeFileSync(path.join(dir, 'state.bin'), state)
    updates.forEach((u, i) => fs.writeFileSync(path.join(dir, `u${String(i).padStart(3, '0')}.bin`), u))
    states[label] = { state, updates }
    const pmNodes = E.state.doc.content.childCount
    log.push(`${label}: state=${state.length}B topLevelNodes=${pmNodes} updates=${updates.length} (avg ${Math.round(updates.reduce((a, u) => a + u.length, 0) / updates.length)}B) elapsed=${Math.round(performance.now() - t0)}ms`)
    fs.writeFileSync(path.join(OUT, RESUME ? 'perf-log-4m.txt' : 'perf-log.txt'), log.join('\n'))
  }
  fs.writeFileSync(path.join(OUT, "schema.json"), JSON.stringify(s.controller.schema))
  s.destroy()

}, 0)
