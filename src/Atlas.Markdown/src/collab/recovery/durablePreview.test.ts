// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import { prepareDurableRecovery } from './durablePreview'
import { journalSha256, type DurableJournalStore, type JournalRecord } from './durableJournal'
// prepareDurableRecovery imports the projection (editor engine graph) lazily. Warm it at file load so that cost is not
// charged to the first test's 5 s timeout on a loaded host (about 1 s alone, over 5 s under a full parallel run).
await import('./previewProjection')
const id = '11111111-2222-4333-8444-555555555555'
const scope = { serverId: 's', tenantId: 't', accountId: 'a', documentId: 'd' }
const schema = { version: 7 as const, profile: 'rich' as const, plugins: [] }
async function fixture() {
  const doc = new Y.Doc(), paragraph = new Y.XmlElement('paragraph'), text = new Y.XmlText()
  text.insert(0, 'Recovered text'); paragraph.insert(0, [text]); doc.getXmlFragment('default').insert(0, [paragraph])
  const snapshot = Y.encodeStateAsUpdate(doc); doc.destroy()
  const record: JournalRecord = { version: 2, scope, branchId: id, owner: id, epoch: id, schema, fragment: 'default', revision: 2,
    sequence: 0, snapshot, pending: [{ updateId: id, update: snapshot.slice(), sha256: await journalSha256(snapshot) }] }
  const store = { read: vi.fn(async () => record), list: vi.fn(), write: vi.fn(), discard: vi.fn(), close: vi.fn() } satisfies DurableJournalStore
  const options = { store, scope, branchId: id, revision: 2, epoch: id, schema, fragment: 'default' }
  return { record, store, options }
}
describe('selected durable journal preview', () => {
  it('projects an isolated complete document without claiming, acknowledging or deleting', async () => {
    const f = await fixture(), result = await prepareDurableRecovery(f.options)
    expect(result.projection.text).toBe('Recovered text'); expect(Object.isFrozen(result.projection.rich.document)).toBe(true)
    expect(f.store.write).not.toHaveBeenCalled(); expect(f.store.discard).not.toHaveBeenCalled(); expect(f.store.close).not.toHaveBeenCalled()
    expect(f.record.pending).toHaveLength(1)
  })
  it.each(['epoch', 'revision', 'fragment'])('rejects another selected %s', async field => {
    const f = await fixture()
    await expect(prepareDurableRecovery({ ...f.options, [field]: field === 'revision' ? 1 : 'wrong' })).rejects.toThrow()
  })
  it.each(['serverId', 'tenantId', 'accountId', 'documentId'])('rejects a store returning another %s', async field => {
    const f = await fixture()
    await expect(prepareDurableRecovery({ ...f.options, scope: { ...scope, [field]: 'different' } })).rejects.toThrow()
  })
  it('rejects a valid-looking record with a mismatched update hash', async () => {
    const f = await fixture(); f.record.pending[0].update[0] ^= 1
    await expect(prepareDurableRecovery(f.options)).rejects.toHaveProperty('code', 'content')
  })
  it('copies fulfilled storage bytes before a later host microtask can mutate them', async () => {
    const f = await fixture(), result = prepareDurableRecovery(f.options)
    queueMicrotask(() => { f.record.snapshot.fill(255); f.record.pending[0].update.fill(255) })
    expect((await result).projection.text).toBe('Recovered text')
  })
  it('cancels a pending read without waiting for storage or changing the branch', async () => {
    const f = await fixture(), abort = new AbortController(); f.store.read.mockImplementation(() => new Promise(() => {}))
    const result = prepareDurableRecovery({ ...f.options, signal: abort.signal }); abort.abort()
    await expect(result).rejects.toHaveProperty('code', 'aborted'); expect(f.store.write).not.toHaveBeenCalled()
  })
  it('enforces the requested byte limit before hashing or copying an oversized selected record', async () => {
    const f = await fixture()
    await expect(prepareDurableRecovery({ ...f.options, maxCheckpointBytes: 1 })).rejects.toHaveProperty('code', 'limit')
  })
  it('ignores late cancelled read data without inspecting its properties', async () => {
    const f = await fixture(), abort = new AbortController()
    let finish!: (record: JournalRecord) => void
    f.store.read.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const result = prepareDurableRecovery({ ...f.options, signal: abort.signal }); abort.abort()
    await expect(result).rejects.toHaveProperty('code', 'aborted')
    const getter = vi.fn(() => { throw new Error('Late data must not be inspected.') })
    Object.defineProperty(f.record, 'snapshot', { enumerable: true, get: getter })
    finish(f.record); await Promise.resolve(); await Promise.resolve()
    expect(getter).not.toHaveBeenCalled()
  })
})
