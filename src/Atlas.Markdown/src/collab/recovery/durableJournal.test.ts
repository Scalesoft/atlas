import { describe, expect, it, vi } from 'vitest'
import { cloneJournal, createIndexedDbDurableJournal, journalSha256, type JournalRecord } from './durableJournal'
const uuid = '11111111-2222-4333-8444-555555555555'
function record(): JournalRecord { return { version: 2, scope: { serverId: 's', tenantId: 't', accountId: 'a', documentId: 'd' },
  branchId: uuid, owner: uuid, epoch: uuid, schema: { version: 2, profile: 'rich', plugins: [] }, fragment: 'default',
  revision: 1, sequence: 0, snapshot: new Uint8Array([0, 0]), pending: [{ updateId: uuid, sha256: 'a'.repeat(64), update: new Uint8Array([1, 2]) }] } }
describe('durable journal input boundary', () => {
  it('owns nested metadata and binary buffers', () => {
    const input = record(), copied = cloneJournal(input)
    input.snapshot[0] = 99; input.pending[0].update[0] = 88
    expect(copied.snapshot[0]).toBe(0); expect(copied.pending[0].update[0]).toBe(1)
    expect(Object.isFrozen(copied.schema.plugins)).toBe(true); expect(Object.isFrozen(copied.pending)).toBe(true)
  })
  it.each(['version', 'scope', 'branchId', 'epoch', 'schema', 'fragment', 'revision', 'owner', 'sequence', 'snapshot', 'pending'])('rejects missing %s', field => {
    const input = record() as any; delete input[field]; expect(() => cloneJournal(input)).toThrow()
  })
  it.each(['version', 'scope', 'pending'])('rejects %s getters without invoking them', field => {
    const input = record(), get = vi.fn(); Object.defineProperty(input, field, { enumerable: true, get })
    expect(() => cloneJournal(input)).toThrow(); expect(get).not.toHaveBeenCalled()
  })
  it.each(['updateId', 'sha256', 'update'])('rejects pending %s getters', field => {
    const input = record(), get = vi.fn(); Object.defineProperty(input.pending[0], field, { enumerable: true, get })
    expect(() => cloneJournal(input)).toThrow(); expect(get).not.toHaveBeenCalled()
  })
  it('rejects getters in the pending array', () => {
    const input = record(), get = vi.fn(); Object.defineProperty(input.pending, '0', { enumerable: true, get })
    expect(() => cloneJournal(input)).toThrow(); expect(get).not.toHaveBeenCalled()
  })
  it.each([
    { epoch: 'not-a-uuid' }, { owner: '' }, { branchId: 'x' }, { revision: 0 }, { revision: 1.5 },
    { sequence: -1 }, { sequence: Number.MAX_SAFE_INTEGER + 1 }, { snapshot: [] }, { fragment: '' },
    { fragment: 'a'.repeat(257) }, { pending: new Array(2) }, { version: 1 }, { unexpected: true },
  ])('rejects invalid fields %j', patch => expect(() => cloneJournal({ ...record(), ...patch } as any)).toThrow())
  it('rejects duplicate pending identities', () => {
    const input = record(); expect(() => cloneJournal({ ...input, pending: [...input.pending, ...input.pending] })).toThrow()
  })
  it('rejects unknown pending fields', () => {
    const input = record(); expect(() => cloneJournal({ ...input, pending: [{ ...input.pending[0], extra: true }] } as any)).toThrow()
  })
  it('hashes an owned byte copy before awaiting WebCrypto', async () => {
    const input = new Uint8Array([1, 2]), original = await journalSha256(input), pending = journalSha256(input)
    input[0] = 9; expect(await pending).toBe(original)
  })
  it('rejects unavailable IndexedDB and closed storage without pretending to persist', async () => {
    const store = createIndexedDbDurableJournal()
    await expect(store.read(record().scope, uuid)).rejects.toHaveProperty('code', 'unavailable')
    store.close(); await expect(store.write(record(), 0, uuid)).rejects.toHaveProperty('code', 'closed')
  })
  it.each([0, -1, 1.2, Number.POSITIVE_INFINITY])('rejects invalid total budget %s', maxTotalBytes => {
    expect(() => createIndexedDbDurableJournal({ maxTotalBytes })).toThrow()
  })
})
