import { expect, it } from 'vitest'
import * as Y from 'yjs'

// Conformance corpus of Atlas.Markdown.Sync's YjsUpdateMerger (tests/Atlas.Markdown.Sync.Tests/Fixtures/yjs-merge): for payload sets
// built from the materializer corpus, the bytes Y.mergeUpdates writes. Besides each case's own payloads, full states taken at two
// points of its history overlap each other and the updates, so structs are covered in part and sliced. Without the variable the test
// recomputes every expectation from the committed payloads; regenerate after a Yjs upgrade, then run YjsUpdateMergerTests:
//   ATLAS_UPDATE_SCHEMA=1 pnpm --filter @atlas/markdown exec vitest run --configLoader runner src/collab/yjsMergeCorpus.test.ts
const update = (globalThis as unknown as { process: { env: Record<string, string | undefined> } }).process.env.ATLAS_UPDATE_SCHEMA === '1'
const fs = await import(/* @vite-ignore */ `node:${'fs'}`) as {
  readFileSync(path: string, encoding?: 'utf8'): Uint8Array & string
  writeFileSync(path: string, data: string | Uint8Array): void
  mkdirSync(path: string, options: { recursive: true }): void
  rmSync(path: string, options: { recursive: true; force: true }): void
}
const { fileURLToPath } = await import(/* @vite-ignore */ `node:${'url'}`) as { fileURLToPath(url: string): string }
const zlib = await import(/* @vite-ignore */ `node:${'zlib'}`) as { gunzipSync(data: Uint8Array): Uint8Array }
const fixtures = fileURLToPath(new URL('../../../../tests/Atlas.Markdown.Sync.Tests/Fixtures/', import.meta.url).href)
const corpus = `${fixtures}yjs-merge/`

interface Case { name: string, steps: Array<{ payload: string, expect: string }> }
interface Entry { name: string, payloads: string[], expected: string }

const read = (relative: string) => {
  const bytes = new Uint8Array(fs.readFileSync(`${fixtures}${relative}`))
  return relative.endsWith('.gz') ? new Uint8Array(zlib.gunzipSync(bytes)) : bytes
}

function build(): { entries: Entry[], files: Map<string, Uint8Array> } {
  const cases = (JSON.parse(fs.readFileSync(`${fixtures}yjs-materializer/cases.json`, 'utf8')) as { cases: Case[] }).cases
  const entries: Entry[] = []
  const files = new Map<string, Uint8Array>()
  for (const entry of cases) {
    // Refused or pending histories are no log the relay admits; the large performance documents would only bloat the corpus.
    if (entry.steps.length < 2 || entry.steps.some(step => step.expect === 'error')) continue
    const own = entry.steps.map(step => `yjs-materializer/${step.payload}`)
    const payloads = own.map(read)
    if (payloads[0].byteLength > 64 * 1024) continue
    const stateAt = (step: number) => {
      const doc = new Y.Doc()
      for (const payload of payloads.slice(0, step + 1)) Y.applyUpdate(doc, payload)
      const state = Y.encodeStateAsUpdate(doc)
      doc.destroy()
      return state
    }
    const middle = Math.floor(entry.steps.length / 2)
    const early = `yjs-merge/${entry.name}/early.state.bin`
    const late = `yjs-merge/${entry.name}/late.state.bin`
    files.set(early, stateAt(middle))
    files.set(late, stateAt(entry.steps.length - 1))
    const sets: Array<[string, string[]]> = [
      ['log', own],
      ['states', [early, late]],
      ['late-first', [late, early, ...own.slice(1)]],
      ['snapshot-and-log', [early, ...own.slice(Math.max(1, middle - 1))]],
    ]
    for (const [scenario, set] of sets) {
      const name = `${entry.name}-${scenario}`
      const expected = `yjs-merge/${name}.bin`
      files.set(expected, Y.mergeUpdates(set.map(path => files.get(path) ?? read(path))))
      entries.push({ name, payloads: set, expected })
    }
  }
  return { entries, files }
}

it('records what Y.mergeUpdates writes for every payload set of the merge corpus', () => {
  const { entries, files } = build()
  expect(entries.length).toBeGreaterThan(100)
  if (update) {
    fs.rmSync(corpus, { recursive: true, force: true })
    for (const [path, bytes] of files) {
      fs.mkdirSync(`${fixtures}${path.slice(0, path.lastIndexOf('/'))}`, { recursive: true })
      fs.writeFileSync(`${fixtures}${path}`, bytes)
    }
    fs.writeFileSync(`${corpus}manifest.json`, `${JSON.stringify({ yjs: '13.6', entries }, null, 1)}\n`)
  }
  const committed = JSON.parse(fs.readFileSync(`${corpus}manifest.json`, 'utf8')) as { entries: Entry[] }
  expect(committed.entries).toEqual(entries)
  for (const [path, bytes] of files) expect(read(path), path).toEqual(bytes)
})
