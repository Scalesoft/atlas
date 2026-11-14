import { expect, it } from 'vitest'
import * as Y from 'yjs'

// Atlas.Markdown.Sync's collaboration guard keeps its own replica of each document (YjsDocumentTree, a managed port of
// Yjs integration) to validate what an update produces. This test records, for every step of the Yjs boundary corpus
// (tests/Atlas.Markdown.Sync.Tests/Fixtures/yjs-corpus), a hash of the document real Yjs holds after it: elements with their
// attributes and text runs as lengths with their formats. YjsDocumentTreeTests requires the replica to hash the same.
// Regenerate after changing the corpus:
//   ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/collab/yjsCorpusDocuments.test.ts
const update = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ATLAS_UPDATE_SCHEMA === '1'
// Node modules without their types (the test typecheck has no Node globals).
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as {
  existsSync(path: string): boolean
  readFileSync(path: string, encoding?: 'utf8'): Uint8Array & string
  readdirSync(path: string): string[]
  writeFileSync(path: string, data: string): void
}
const { fileURLToPath } = await import(/* @vite-ignore */ `node:${'url'}`) as { fileURLToPath(url: string): string }
const { createHash } = await import(/* @vite-ignore */ `node:${'crypto'}`) as {
  createHash(algorithm: 'sha256'): { update(data: string): { digest(encoding: 'hex'): string } }
}
const corpus = fileURLToPath(new URL('../../../../tests/Atlas.Markdown.Sync.Tests/Fixtures/yjs-corpus/', import.meta.url).href)
const outputPath = `${corpus}documents.json`

type Json = null | boolean | number | string | Json[] | { [key: string]: Json }
const stable = (value: Json): Json => Array.isArray(value) ? value.map(stable)
  : value !== null && typeof value === 'object' ? Object.fromEntries(Object.keys(value).sort().map(key => [key, stable(value[key]!)])) : value

/** The "default" fragment: {t, a, c} per element, {x: length, m: formats} per text run (equal neighbours merged). */
function canonical(doc: Y.Doc): string {
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
const hash = (doc: Y.Doc) => createHash('sha256').update(canonical(doc)).digest('hex').slice(0, 16)

it('records the document real Yjs holds after every corpus step', () => {
  const manifest = JSON.parse(fs.readFileSync(`${corpus}manifest.json`, 'utf8')) as { scenario: string; seq: number; kind: string; file: string; expect: string; reason?: string }[]
  const read = (file: string) => new Uint8Array(fs.readFileSync(`${corpus}${file}`))
  const documents: Record<string, string> = {}
  const scenarios = new Map<string, typeof manifest>()
  for (const entry of manifest.filter(item => item.file)) scenarios.set(entry.scenario, [...(scenarios.get(entry.scenario) ?? []), entry])
  for (const [scenario, entries] of scenarios) {
    entries.sort((a, b) => a.seq - b.seq)
    const running = new Y.Doc()
    Y.applyUpdate(running, read(entries[0].file))
    for (const entry of entries) {
      // Yjs parks pending structs and deletes; the guard refuses them, so they have no document.
      if (entry.reason === 'pending' || entry.reason === 'embed') continue
      if (entry.kind === 'state') {
        const standalone = new Y.Doc()
        Y.applyUpdate(standalone, read(entry.file))
        documents[entry.file] = hash(standalone)
        continue
      }
      // Hostile cases are independent diffs against the baseline; every other scenario replays all its steps.
      const doc = scenario === 'hostile' ? new Y.Doc() : running
      if (scenario === 'hostile') Y.applyUpdate(doc, read(entries[0].file))
      Y.applyUpdate(doc, read(entry.file))
      documents[entry.file] = hash(doc)
    }
  }
  for (const size of fs.readdirSync(`${corpus}perf`).filter(name => fs.existsSync(`${corpus}perf/${name}/state.bin`))) {
    const doc = new Y.Doc()
    Y.applyUpdate(doc, read(`perf/${size}/state.bin`))
    documents[`perf/${size}/state.bin`] = hash(doc)
    for (const file of fs.readdirSync(`${corpus}perf/${size}`).filter(name => /^u\d+\.bin$/.test(name)).sort()) {
      Y.applyUpdate(doc, read(`perf/${size}/${file}`))
      documents[`perf/${size}/${file}`] = hash(doc)
    }
  }
  const generated = `${JSON.stringify(documents, null, 1)}\n`
  if (update) { fs.writeFileSync(outputPath, generated); return }
  const stored = fs.existsSync(outputPath) ? fs.readFileSync(outputPath, 'utf8').replace(/\r\n/g, '\n') : ''
  expect(stored, 'documents.json no longer matches the corpus; regenerate it (see the comment above)').toBe(generated)
}, 120_000)
