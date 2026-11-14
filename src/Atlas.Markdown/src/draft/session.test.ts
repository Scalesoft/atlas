// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController } from '../core/controller'
import { getMarkdownContentCapability } from '../core/contentCapability'
import { editorForController, registerCollaborationAdapter } from '../core/internal'
import type { MarkdownEditorController } from '../core/types'
import { cloneMarkdownDraftRecord, createMarkdownDraft, createIndexedDbMarkdownDraftStore, normalizeMarkdownDraftScope } from './index'
import type { MarkdownDraftRecord, MarkdownDraftStore } from './types'

const scope = { serverId: 'installation', tenantId: 'tenant', accountId: 'account', documentId: 'doc' }
const dispose: Array<() => void> = []
const deferred = () => { let resolve!: () => void; const promise = new Promise<void>(yes => { resolve = yes }); return { promise, resolve } }
class Store implements MarkdownDraftStore {
  records: MarkdownDraftRecord[] = []
  writes: MarkdownDraftRecord[] = []
  gate?: Promise<void>
  failure = false
  async write(value: MarkdownDraftRecord, revision: number) {
    const own = cloneMarkdownDraftRecord(value); this.writes.push(own)
    await this.gate
    if (this.failure) throw new Error('quota failure')
    if ((this.records.at(-1)?.revision ?? 0) !== revision) throw new Error('CAS conflict')
    this.records.push(own)
  }
  async read() { return this.records.at(-1) ?? null }
  async list() { return [...this.records] }
  async discard() {}
  close() {}
}
function fixture(options: { delayMs?: number; maxWaitMs?: number } = {}) {
  const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: 'Initial' } })
  dispose.push(() => controller.destroy())
  const store = new Store()
  const draft = createMarkdownDraft({ controller, store, scope, ...options })
  dispose.push(() => { draft.destroy(); controller.destroy() })
  return { controller, store, draft, content: getMarkdownContentCapability(controller)! }
}
afterEach(() => { dispose.splice(0).reverse().forEach(stop => stop()); vi.restoreAllMocks(); vi.useRealTimers() })

describe('periodic local draft sessions', () => {
  it('captures no values on edit notifications and waits for the default quiet period', async () => {
    vi.useFakeTimers(); const f = fixture()
    f.controller.setValue('Newest'); expect(f.store.writes).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(999); expect(f.store.writes).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(1)
    expect(f.store.records).toHaveLength(1)
    expect(f.store.records[0].snapshot).toEqual({ ...f.content.getStamp(), value: 'Newest' })
    expect(f.draft.getState().status).toBe('persisted')
  })
  it('bounds repeated typing by maxWait without serializing each edit', async () => {
    vi.useFakeTimers(); const f = fixture()
    for (let index = 0; index < 6; index++) {
      f.controller.setValue(`Value ${index}`)
      await vi.advanceTimersByTimeAsync(800)
    }
    expect(f.store.writes).toHaveLength(0)
    await vi.advanceTimersByTimeAsync(200)
    expect(f.store.records).toHaveLength(1)
    expect(f.store.records[0].snapshot.value).toBe('Value 5')
  })
  it('captures explicit checkpoints before waiting and does not mark newer edits persisted', async () => {
    const f = fixture(); const gate = deferred(); f.store.gate = gate.promise
    const first = f.draft.checkpoint(); await Promise.resolve()
    f.controller.setValue('Later')
    expect(f.store.writes[0].snapshot.value).toBe('Initial')
    gate.resolve(); await first
    expect(f.draft.getState()).toMatchObject({ status: 'pending', current: { revision: 1 }, persisted: { revision: 0 } })
    const second = await f.draft.checkpoint()
    expect(second.revision).toBe(2); expect(second.snapshot.value).toBe('Later')
    expect(f.draft.getState().status).toBe('persisted')
  })
  it('serializes overlapping checkpoints and compares the preceding committed store revision', async () => {
    const f = fixture(); const gate = deferred(); f.store.gate = gate.promise
    const first = f.draft.checkpoint(); f.controller.setValue('Second'); const second = f.draft.checkpoint()
    await Promise.resolve(); expect(f.store.writes).toHaveLength(1)
    gate.resolve(); await Promise.all([first, second])
    expect(f.store.records.map(record => [record.revision, record.snapshot.value])).toEqual([[1, 'Initial'], [2, 'Second']])
    expect(f.draft.getState().persisted).toEqual(f.content.getStamp())
  })
  it('retains the last good draft after a failure and supports explicit retry with the same branch', async () => {
    const f = fixture(); const before = await f.draft.checkpoint()
    f.controller.setValue('Unsaved'); f.store.failure = true
    await expect(f.draft.checkpoint()).rejects.toThrow('quota')
    expect(f.store.records).toHaveLength(1)
    expect(f.draft.getState()).toMatchObject({ status: 'failed', persisted: { sessionId: before.snapshot.sessionId, revision: before.snapshot.revision } })
    f.store.failure = false; const after = await f.draft.checkpoint()
    expect(after.branchId).toBe(before.branchId); expect(after.revision).toBe(2)
    expect(after.snapshot.value).toBe('Unsaved'); expect(f.draft.getState().status).toBe('persisted')
  })
  it('stops automatic retry after storage failure until explicit checkpoint', async () => {
    vi.useFakeTimers(); const f = fixture(); f.store.failure = true
    await vi.advanceTimersByTimeAsync(1000)
    f.controller.setValue('After failure'); await vi.advanceTimersByTimeAsync(20_000)
    expect(f.store.writes).toHaveLength(1); expect(f.draft.getState().status).toBe('failed')
  })
  it('allows an already detached write to finish after destroy, cancelling future capture and notifications', async () => {
    const f = fixture(); const gate = deferred(); f.store.gate = gate.promise
    const listener = vi.fn(); f.draft.subscribe(listener)
    const pending = f.draft.checkpoint(); await Promise.resolve()
    f.draft.destroy(); const count = listener.mock.calls.length
    gate.resolve(); await pending
    expect(f.store.records).toHaveLength(1); expect(f.draft.getState().status).toBe('closed')
    expect(listener).toHaveBeenCalledTimes(count)
    await expect(f.draft.checkpoint()).rejects.toMatchObject({ code: 'closed' })
  })
  it('cancels uncaptured timers when the controller is destroyed', async () => {
    vi.useFakeTimers(); const f = fixture(); f.controller.setValue('Changed'); f.controller.destroy()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(f.store.writes).toHaveLength(0); expect(f.draft.getState().status).toBe('closed')
  })
  it('does not hide a live content failure when an older valid write completes', async () => {
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: {
      format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] },
    } } })
    const host = document.createElement('div'); document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Draft failure' })
    const store = new Store(), gate = deferred(); store.gate = gate.promise
    const draft = createMarkdownDraft({ controller, store, scope })
    dispose.push(() => { draft.destroy(); detach(); controller.destroy(); host.remove() })
    const pending = draft.checkpoint(); await Promise.resolve()
    const editor = editorForController(controller)!
    Object.assign(editor.state.doc.firstChild!.attrs, { textAlign: 'invalid-alignment' })
    editor.view.dispatch(editor.state.tr.setMeta('validate', true))
    expect(controller.state.lifecycle).toBe('failed')
    gate.resolve(); await pending
    expect(store.records).toHaveLength(1)
    expect(draft.getState().status).toBe('failed')
    await expect(draft.checkpoint()).rejects.toThrow(/cannot export/)
  })
  it('owns scope and separates branches across controllers without auto-applying an old draft', async () => {
    const f = fixture(), second = fixture()
    expect(f.draft.getState().branchId).not.toBe(second.draft.getState().branchId)
    const mutable = { ...scope }
    const third = createMarkdownDraft({ controller: f.controller, store: new Store(), scope: mutable })
    dispose.push(() => third.destroy()); mutable.accountId = 'other'
    expect((await third.checkpoint()).scope.accountId).toBe('account')
    expect(f.controller.getValue()).toBe('Initial')
  })
  it('isolates observer failure and publishes deeply frozen stamps', async () => {
    const f = fixture(); const subscriber = vi.fn(() => { throw new Error('observer') })
    f.draft.subscribe(subscriber); await f.draft.checkpoint()
    expect(f.draft.getState().status).toBe('persisted')
    expect(Object.isFrozen(f.draft.getState().persisted)).toBe(true)
    expect(() => Object.assign(f.draft.getState().current, { revision: 42 })).toThrow()
  })
  it('rejects external controllers without inventing an atomic snapshot capability', () => {
    expect(() => createMarkdownDraft({ controller: {} as MarkdownEditorController, store: new Store(), scope })).toThrow(/snapshot capability/)
  })
  it('rejects collaboration controllers', () => {
    const adapter = registerCollaborationAdapter(() => ({ extensions: [], isRemoteTransaction: () => true, attachEditor() {}, detachEditor() {}, destroy() {} }))
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    dispose.push(() => controller.destroy())
    expect(() => createMarkdownDraft({ controller, store: new Store(), scope })).toThrow(/local controller/)
  })
  it.each([{ delayMs: -1 }, { delayMs: NaN }, { maxWaitMs: 0 }, { delayMs: 2000, maxWaitMs: 1000 }, { maxWaitMs: 60_001 }])('rejects invalid timing %j', config => {
    expect(() => fixture(config)).toThrow(/timing/)
  })
})

describe('draft envelope and native-store preflight', () => {
  it.each(['serverId', 'tenantId', 'accountId', 'documentId'])('rejects missing/empty %s identity', field => {
    expect(() => normalizeMarkdownDraftScope({ ...scope, [field]: '' })).toThrow()
  })
  it('never invokes getters when capturing metadata', () => {
    const getter = vi.fn(() => 'secret')
    const input = { ...scope }; Object.defineProperty(input, 'accountId', { enumerable: true, get: getter })
    expect(() => normalizeMarkdownDraftScope(input)).toThrow(); expect(getter).not.toHaveBeenCalled()
  })
  it('rejects unsupported record revisions and profile/value mismatch before opening storage', async () => {
    const f = fixture(), value = await f.draft.checkpoint()
    const native = createIndexedDbMarkdownDraftStore()
    await expect(native.write({ ...value, revision: 0 }, 0)).rejects.toThrow()
    await expect(native.write({ ...value, revision: 2 }, 0)).rejects.toThrow(/advance/)
    await expect(native.write({ ...value, schema: { version: 2, profile: 'rich', plugins: [] } }, 0)).rejects.toThrow(/envelope/)
    native.close()
  })
  it('reports unavailable native IndexedDB, and close consistently rejects later reads', async () => {
    const store = createIndexedDbMarkdownDraftStore()
    await expect(store.list(scope)).rejects.toMatchObject({ code: 'unavailable' })
    store.close(); await expect(store.list(scope)).rejects.toMatchObject({ code: 'closed' })
  })
})
