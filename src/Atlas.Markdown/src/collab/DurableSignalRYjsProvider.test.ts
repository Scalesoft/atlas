// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import * as Y from 'yjs'
import StarterKit from '@tiptap/starter-kit'
import { createTiptapEditor } from '../engine/editorFactory'
import { DurableSignalRYjsProvider } from './DurableSignalRYjsProvider'
import { cloneJournal, journalSha256, type DurableJournalStore, type JournalRecord } from './recovery/durableJournal'
import { b64ToU8, u8ToB64 } from './provider/protocol'
import type { MarkdownSyncConnection } from './provider/transport'

const schema = { version: 2 as const, profile: 'rich' as const, plugins: [] }
const scope = { serverId: 'server', tenantId: 'tenant', accountId: 'account', documentId: 'doc' }
const epoch = '11111111-2222-4333-8444-555555555555'
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes }); return { promise, resolve } }
class MemoryStore implements DurableJournalStore {
  record: JournalRecord | null = null
  writes: JournalRecord[] = []
  pause?: Promise<void>
  fail = false
  async read() { return this.record && cloneJournal(this.record) }
  async list() { return this.record ? [cloneJournal(this.record)] : [] }
  async write(record: JournalRecord, revision: number, owner: string) {
    const owned = cloneJournal(record)
    await this.pause
    if (this.fail) throw new Error('journal storage failed')
    if ((this.record?.revision ?? 0) !== revision || (this.record && this.record.owner !== owner)) throw new Error('journal conflict')
    this.record = owned; this.writes.push(owned)
  }
  close() {}
  async discard() { throw new Error('Not used by provider.') }
}
class Connection implements MarkdownSyncConnection {
  readonly server = new Y.Doc()
  readonly calls: Array<{ method: string; args: any[] }> = []
  readonly handlers = new Map<string, (...args: any[]) => void>()
  reconnecting = () => {}; reconnected = () => {}; closed = () => {}
  seq = 0; epoch = epoch; write = true; protocol = 2
  ack?: (value: any) => Promise<unknown>
  joinPatch: Record<string, unknown> = {}
  joinError?: Error
  receipts = new Map<string, unknown>()
  constructor() { const p = new Y.XmlElement('paragraph'); const t = new Y.XmlText(); t.insert(0, 'base'); p.insert(0, [t]); this.server.getXmlFragment('default').insert(0, [p]) }
  async start() {}
  async stop() {}
  async send() {}
  on(method: string, handler: (...args: any[]) => void) { this.handlers.set(method, handler) }
  onReconnecting(handler: () => void) { this.reconnecting = handler }
  onReconnected(handler: () => void) { this.reconnected = handler }
  onClose(handler: () => void) { this.closed = handler }
  async invoke<T>(method: string, ...args: any[]): Promise<T> {
    this.calls.push({ method, args })
    if (method === 'GetCapabilities') return { protocolVersion: this.protocol, authority: 'server', recovery: 'durable-receipts' } as T
    if (method === 'JoinDocumentV2' && this.joinError) throw this.joinError
    if (method === 'JoinDocumentV2') return { protocolVersion: 2, authority: 'server', recovery: 'durable-receipts',
      snapshot: u8ToB64(Y.encodeStateAsUpdate(this.server)), seq: this.seq, snapshotSequence: this.seq, persistedSequence: this.seq,
      epoch: this.epoch, fragment: 'default', schema, canWrite: this.write, limits: { maxUpdateBytes: 1024 * 1024 }, ...this.joinPatch } as T
    if (method === 'SendUpdateV2') {
      const request = args[1]
      if (this.ack) return await this.ack(request) as T
      let ack = this.receipts.get(request.updateId)
      if (!ack) {
        Y.applyUpdate(this.server, b64ToU8(request.update)); ++this.seq
        ack = { protocolVersion: 2, epoch: this.epoch, updateId: request.updateId, sha256: request.sha256, sequence: this.seq, persistedSequence: this.seq }
        this.receipts.set(request.updateId, ack)
      }
      return ack as T
    }
    throw new Error(`Unexpected RPC: ${method}`)
  }
  get sends() { return this.calls.filter(call => call.method === 'SendUpdateV2') }
}
const cleanups: Array<() => void> = []
afterEach(() => { cleanups.splice(0).reverse().forEach(cleanup => cleanup()); vi.useRealTimers() })
function fixture(store = new MemoryStore(), conn = new Connection(), branchId?: string, allowOfflineEditing = false) {
  const doc = new Y.Doc(), fatal = vi.fn(), synced = vi.fn(), pending = vi.fn(), writable = vi.fn(), projection = vi.fn()
  const editor = createTiptapEditor({ element: document.createElement('div'), extensions: [StarterKit], content: { type: 'doc', content: [{ type: 'paragraph' }] } })
  const provider = new DurableSignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc: doc, schema,
    durable: { store, scope, branchId }, allowOfflineEditing, onWritable: writable, onFatalError: fatal, onSynced: synced, onPendingUpdates: pending, onProjectionStatus: projection }, conn)
  provider.attachEditor(editor)
  cleanups.push(() => { provider.destroy(); editor.destroy(); doc.destroy(); conn.server.destroy() })
  const edit = (text: string) => (doc.getXmlFragment('default').get(0) as Y.XmlElement).get(0) instanceof Y.XmlText
    && ((doc.getXmlFragment('default').get(0) as Y.XmlElement).get(0) as Y.XmlText).insert(0, text)
  return { store, conn, doc, provider, fatal, synced, pending, writable, edit, editor, projection }
}
const settled = () => new Promise(resolve => setTimeout(resolve, 20))

describe('authoritative durable collaboration', () => {
  it('flushLocal awaits captured local commits independently of server ACK and view destruction', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<void>(); f.store.pause = gate.promise
    f.conn.ack = () => new Promise(() => {}); f.edit('one')
    let completed = false; const flushed = f.provider.flushLocal().then(() => { completed = true })
    f.provider.destroy(); await settled(); expect(completed).toBe(false)
    gate.resolve(); await flushed
    expect(f.store.record!.pending).toHaveLength(1); expect(completed).toBe(true); expect(f.conn.sends).toHaveLength(0)
  })
  it('flushLocal rejects failed local persistence instead of confirming an in-memory edit', async () => {
    const f = fixture(); await f.provider.start(); f.store.fail = true; f.edit('one')
    await expect(f.provider.flushLocal()).rejects.toThrow('local journal failed')
  })
  it.each(['DOCUMENT_FULL', 'INVALID_CONTENT', 'INVALID_UPDATE', 'MIGRATION_REQUIRED'])('stops retries for deterministic %s rejection and retains the pending batch', async code => {
    const f = fixture(); await f.provider.start(); f.conn.ack = async () => { throw new Error(`[${code}] rejected`) }
    f.edit('one'); await vi.waitFor(() => expect(f.fatal).toHaveBeenCalled())
    expect(f.conn.sends).toHaveLength(1); expect(f.store.record!.pending).toHaveLength(1)
    expect(f.projection).toHaveBeenLastCalledWith('degraded')
  })
  it('reports required migration as an actionable join failure', async () => {
    const f = fixture(); f.conn.joinError = new Error('[MIGRATION_REQUIRED] Explicit host cutover required.')
    await f.provider.start(); expect(f.fatal).toHaveBeenCalled(); expect(f.store.record).toBeNull()
  })
  it('does not label projections current while local edits await server persistence', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<unknown>(); f.conn.ack = () => gate.promise
    f.edit('one'); expect(f.projection).toHaveBeenLastCalledWith('pending')
    await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1)); expect(f.projection).toHaveBeenLastCalledWith('pending')
    const item = f.conn.sends[0].args[1]; Y.applyUpdate(f.conn.server, b64ToU8(item.update)); f.conn.seq = 1
    gate.resolve({ ...item, protocolVersion: 2, sequence: 1, persistedSequence: 1 })
    await vi.waitFor(() => expect(f.projection).toHaveBeenLastCalledWith('current'))
  })
  it('receives and persists remote changes while detached, then reconnects and reattaches without echo', async () => {
    const f = fixture(); await f.provider.start(); f.provider.detachEditor()
    const vector = Y.encodeStateVector(f.conn.server)
    const text = (f.conn.server.getXmlFragment('default').get(0) as Y.XmlElement).get(0) as Y.XmlText
    text.insert(0, 'REMOTE')
    const update = Y.encodeStateAsUpdate(f.conn.server, vector); f.conn.seq = 1
    f.conn.handlers.get('ReceiveUpdateV2')!('doc', { protocolVersion: 2, epoch, updateId: crypto.randomUUID(), sha256: await journalSha256(update), update: u8ToB64(update), sequence: 1, persistedSequence: 1 })
    await vi.waitFor(() => expect(f.store.record!.sequence).toBe(1))
    expect(f.doc.getXmlFragment('default').toString()).toContain('REMOTEbase')
    f.conn.reconnecting(); f.conn.reconnected()
    await vi.waitFor(() => expect(f.synced).toHaveBeenLastCalledWith(true))
    f.provider.attachEditor(f.editor)
    expect(f.fatal).not.toHaveBeenCalled(); expect(f.conn.sends).toHaveLength(0)
  })
  it('journals authorized offline edits before replaying them after reconnect when explicitly enabled', async () => {
    const f = fixture(undefined, undefined, undefined, true); await f.provider.start()
    f.conn.reconnecting(); expect(f.writable).toHaveBeenLastCalledWith(true)
    f.edit('offline')
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(1))
    expect(f.conn.sends).toHaveLength(0)
    const retained = f.store.record!.pending[0]
    f.conn.reconnected()
    await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    expect(f.conn.sends[0].args[1]).toMatchObject({ updateId: retained.updateId, sha256: retained.sha256 })
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(0))
    expect(f.conn.server.getXmlFragment('default').toString()).toContain('offlinebase')
  })
  it.each([false, true])('never grants offline write before a successful writable join (opt-in %s)', async allow => {
    const f = fixture(undefined, undefined, undefined, allow)
    f.conn.reconnecting(); expect(f.writable).toHaveBeenLastCalledWith(false)
    f.conn.write = false; await f.provider.start(); f.conn.reconnecting()
    expect(f.writable).toHaveBeenLastCalledWith(false)
  })
  it('keeps default disconnection and explicit ACL changes readonly', async () => {
    const ordinary = fixture(); await ordinary.provider.start(); ordinary.conn.reconnecting()
    expect(ordinary.writable).toHaveBeenLastCalledWith(false)
    const offline = fixture(undefined, undefined, undefined, true); await offline.provider.start()
    offline.conn.handlers.get('DocumentAccessChanged')!('doc', true, false)
    expect(offline.writable).toHaveBeenLastCalledWith(false)
    offline.conn.reconnecting(); expect(offline.writable).toHaveBeenLastCalledWith(false)
  })
  it('joins, journals each finished edit before send, and removes it only after a durable matching ACK', async () => {
    const f = fixture(); await f.provider.start(); expect(f.synced).toHaveBeenLastCalledWith(true)
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(0))
    const sent = f.conn.sends[0].args[1]
    const persisted = f.store.writes.find(record => record.pending.some(item => item.updateId === sent.updateId))!
    expect(persisted.pending[0].sha256).toBe(sent.sha256)
    expect(await journalSha256(persisted.pending[0].update)).toBe(sent.sha256)
    expect(f.conn.calls.some(call => ['PushSnapshot', 'SendUpdate', 'JoinDocument'].includes(call.method))).toBe(false)
  })
  it('does not send before IndexedDB completion and keeps synchronous pending status', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<void>(); f.store.pause = gate.promise
    f.edit('one'); expect(f.pending).toHaveBeenLastCalledWith(1); await settled(); expect(f.conn.sends).toHaveLength(0)
    gate.resolve(); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
  })
  it('fails visibly on storage failure without sending the edit', async () => {
    const f = fixture(); await f.provider.start(); f.store.fail = true; f.edit('one')
    await vi.waitFor(() => expect(f.fatal).toHaveBeenCalled()); expect(f.conn.sends).toHaveLength(0)
    expect(f.doc.getXmlFragment('default').toString()).toContain('onebase')
  })
  it.each(['epoch', 'updateId', 'sha256', 'protocolVersion', 'sequence', 'persistedSequence'])('rejects a corrupt %s receipt and retains the journal item', async field => {
    const f = fixture(); await f.provider.start()
    f.conn.ack = async item => ({ ...item, protocolVersion: 2, sequence: 1, persistedSequence: 1, [field]: field === 'sequence' ? 0 : field === 'persistedSequence' ? 0 : 'wrong' })
    f.edit('one'); await vi.waitFor(() => expect(f.fatal).toHaveBeenCalled()); expect(f.store.record!.pending).toHaveLength(1)
  })
  it('keeps unacknowledged changes after disconnect and ignores old generation ACKs', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<unknown>(); f.conn.ack = () => gate.promise
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    const item = f.conn.sends[0].args[1]; f.conn.reconnecting()
    gate.resolve({ ...item, protocolVersion: 2, sequence: 1, persistedSequence: 1 }); await settled()
    expect(f.store.record!.pending).toHaveLength(1)
  })
  it('retries the stable journal identity after reconnect without waiting for an obsolete RPC', async () => {
    const f = fixture(); await f.provider.start()
    const old = deferred<unknown>(), current = deferred<unknown>()
    let attempt = 0
    f.conn.ack = () => ++attempt === 1 ? old.promise : current.promise
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    const item = f.conn.sends[0].args[1]
    f.conn.reconnecting(); f.conn.reconnected()
    // The first RPC remains unresolved while the new transport retries.
    await vi.waitFor(() => expect(f.conn.sends).toHaveLength(2))
    expect(f.conn.sends[1].args[1]).toEqual(item)
    expect(f.store.record!.pending).toHaveLength(1)
    old.resolve({ ...item, protocolVersion: 2, sequence: 1, persistedSequence: 1 })
    f.edit('two')
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(2))
    // The old finally must not release ownership of the still-running new pump.
    expect(f.conn.sends).toHaveLength(2)
    expect(f.fatal).not.toHaveBeenCalled()
    f.provider.destroy(); current.resolve(null)
  })
  it('resumes a selected branch with the original update ID and hash after a crash', async () => {
    const f = fixture(); await f.provider.start(); f.conn.ack = () => new Promise(() => {})
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    const before = f.conn.sends[0].args[1], branch = f.store.record!.branchId; f.provider.destroy()
    const second = fixture(f.store, f.conn, branch); f.conn.ack = undefined; await second.provider.start()
    await vi.waitFor(() => expect(f.conn.sends).toHaveLength(2))
    expect(f.conn.sends[1].args[1]).toEqual(before)
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(0))
  })
  it('never replays a selected branch in another epoch', async () => {
    const f = fixture(); await f.provider.start(); const branch = f.store.record!.branchId; f.provider.destroy()
    const conn = new Connection(); conn.epoch = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee'
    const second = fixture(f.store, conn, branch); await second.provider.start()
    expect(second.fatal).toHaveBeenCalled(); expect(conn.sends).toHaveLength(0); expect(f.store.record!.epoch).toBe(epoch)
  })
  it.each([{ fragment: 'other' }, { seq: -1 }, { snapshotSequence: 2 }, { persistedSequence: -1 }, { canWrite: 'true' }, { snapshot: 'broken' }, { schema: { ...schema, version: 1 } }])('rejects invalid join metadata %j without a journal claim', async patch => {
    const f = fixture(); f.conn.joinPatch = patch; await f.provider.start()
    expect(f.fatal).toHaveBeenCalled(); expect(f.store.record).toBeNull()
  })
  it('fails closed for an unsupported protocol without legacy fallback', async () => {
    const f = fixture(); f.conn.protocol = 1; await f.provider.start()
    expect(f.fatal).toHaveBeenCalled(); expect(f.conn.calls.map(call => call.method)).toEqual(['GetCapabilities'])
  })
  it('does not send after an access revocation', async () => {
    const f = fixture(); await f.provider.start(); f.conn.handlers.get('DocumentAccessChanged')!('doc', false, false)
    f.edit('one'); await settled(); expect(f.conn.sends).toHaveLength(0); expect(f.fatal).toHaveBeenCalled()
  })
  it('fences an older tab when another session claims its selected branch', async () => {
    const f = fixture(); await f.provider.start(); const second = fixture(f.store, new Connection(), f.store.record!.branchId)
    await second.provider.start(); f.edit('one')
    await vi.waitFor(() => expect(f.fatal).toHaveBeenCalled()); expect(f.conn.sends).toHaveLength(0)
  })
  it('retains separately queued edits while the first ACK is in flight', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<unknown>(); f.conn.ack = () => gate.promise
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1)); f.edit('two')
    await vi.waitFor(() => expect(f.store.record!.pending).toHaveLength(2))
    expect(new Set(f.store.record!.pending.map(item => item.updateId)).size).toBe(2)
    f.provider.destroy(); gate.resolve(null)
  })
  it('advances the journal on its own next-sequence ACK without rejoining or flickering', async () => {
    const f = fixture(); await f.provider.start(); f.synced.mockClear(); f.writable.mockClear()
    f.edit('one'); await vi.waitFor(() => expect(f.store.record!.sequence).toBe(1))
    f.edit('two'); await vi.waitFor(() => expect(f.store.record!.sequence).toBe(2))
    expect(f.store.record!.pending).toHaveLength(0)
    expect(f.store.writes.some(record => record.sequence === 1 && record.pending.length === 0)).toBe(true)
    expect(f.conn.calls.filter(call => call.method === 'JoinDocumentV2')).toHaveLength(1)
    expect(f.synced).not.toHaveBeenCalledWith(false); expect(f.writable).not.toHaveBeenCalledWith(false)
    expect(f.fatal).not.toHaveBeenCalled()
  })
  it('orders its ACK after remote updates received during the send', async () => {
    const f = fixture(); await f.provider.start(); const gate = deferred<unknown>(); f.conn.ack = () => gate.promise
    f.edit('one'); await vi.waitFor(() => expect(f.conn.sends).toHaveLength(1))
    const text = (f.conn.server.getXmlFragment('default').get(0) as Y.XmlElement).get(0) as Y.XmlText
    for (const [sequence, value] of [[1, 'X'], [2, 'Y']] as const) {
      const vector = Y.encodeStateVector(f.conn.server); text.insert(text.length, value)
      const remote = Y.encodeStateAsUpdate(f.conn.server, vector)
      f.conn.handlers.get('ReceiveUpdateV2')!('doc', { protocolVersion: 2, epoch, updateId: crypto.randomUUID(), sha256: await journalSha256(remote), update: u8ToB64(remote), sequence, persistedSequence: sequence })
    }
    const item = f.conn.sends[0].args[1]; gate.resolve({ ...item, protocolVersion: 2, sequence: 3, persistedSequence: 3 })
    await vi.waitFor(() => expect(f.store.record!.sequence).toBe(3))
    expect(f.store.record!.pending).toHaveLength(0)
    expect(f.conn.calls.filter(call => call.method === 'JoinDocumentV2')).toHaveLength(1)
    expect(f.doc.getXmlFragment('default').toString()).toContain('onebaseXY')
  })
  it('rejoins when its ACK reveals missed remote sequences', async () => {
    const f = fixture(); await f.provider.start(); f.conn.seq = 1
    f.edit('one'); await vi.waitFor(() => expect(f.conn.calls.filter(call => call.method === 'JoinDocumentV2')).toHaveLength(2))
    await vi.waitFor(() => expect(f.store.record!.sequence).toBe(2))
    expect(f.synced).toHaveBeenCalledWith(false); expect(f.store.record!.pending).toHaveLength(0)
  })
  it('backs off transient send failures', async () => {
    const f = fixture(); await f.provider.start(); vi.useFakeTimers()
    let failures = 3; const times: number[] = []
    f.conn.ack = async item => {
      times.push(Date.now()); if (failures-- > 0) throw new Error('transient')
      f.conn.ack = undefined; return f.conn.invoke('SendUpdateV2', 'doc', item)
    }
    // Hashing settles outside the fake clock; waitFor advances it between checks.
    f.edit('one'); await vi.waitFor(() => expect(f.store.record!.sequence).toBe(1), { timeout: 10_000 })
    expect(f.store.record!.pending).toHaveLength(0)
    const gaps = times.slice(1).map((time, index) => time - times[index])
    expect(gaps).toHaveLength(3); expect(gaps[1]).toBeGreaterThan(gaps[0]); expect(gaps[2]).toBeGreaterThan(gaps[1])
    expect(f.fatal).not.toHaveBeenCalled()
  })
})
