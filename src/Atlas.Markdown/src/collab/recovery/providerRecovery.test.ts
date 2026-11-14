// @vitest-environment jsdom
import * as Y from 'yjs'
import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createMarkdownRecovery, createSignalRMarkdownAdapter, SignalRYjsProvider,
  type MarkdownRecovery, type MarkdownSyncConnection, type RecoveryCheckpoint,
  type RecoveryScope, type RecoveryStore, type RecoverySummary, type SignalRYjsProviderOptions,
} from '../../collaboration/signalr'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { cloneRecoveryCheckpoint, summarizeRecoveryCheckpoint } from './indexedDbRecoveryStore'
import { collaborationStrings } from '../../ui/strings/collaborationAddons'
// Collaborative hydration takes seconds on a loaded machine; readiness waits below use a long deadline, this is the hang guard.
vi.setConfig({ testTimeout: 30_000 })

const transport = vi.hoisted(() => ({ current: null as MarkdownSyncConnection | null }))
vi.mock('../provider/transport', async importOriginal => {
  const actual = await importOriginal<typeof import('../provider/transport')>()
  return { ...actual, createSignalRConnection: () => {
    if (!transport.current) throw new Error('No recovery test connection was installed.')
    return transport.current
  } }
})

const scope: RecoveryScope = { serverId: 'server', tenantId: 'tenant', accountId: 'account', documentId: 'doc' }
const providers: SignalRYjsProvider[] = []
const documents: Y.Doc[] = []
const controllers: MarkdownEditorController[] = []
function document() { const doc = new Y.Doc(); documents.push(doc); return doc }
function decode(value: string) { return Uint8Array.from(atob(value), character => character.charCodeAt(0)) }
function textOf(state: Uint8Array) {
  const doc = new Y.Doc()
  try { Y.applyUpdate(doc, state); return doc.getText('content').toString() }
  finally { doc.destroy() }
}
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (cause: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

class MemoryRecoveryStore implements RecoveryStore {
  writes: RecoveryCheckpoint[] = []
  waits: Promise<void>[] = []
  saved = new Map<string, RecoveryCheckpoint>()
  onWrite?: (checkpoint: RecoveryCheckpoint) => void
  async write(checkpoint: RecoveryCheckpoint) {
    this.writes.push(checkpoint)
    const captured = cloneRecoveryCheckpoint(checkpoint)
    this.onWrite?.(checkpoint)
    if (this.waits.length) await this.waits.shift()
    this.saved.set(captured.branchId, captured)
  }
  read = vi.fn(async (_scope: RecoveryScope, branchId: string) => this.saved.get(branchId) ?? null)
  list = vi.fn(async (_scope: RecoveryScope): Promise<RecoverySummary[]> => [...this.saved.values()].map(summarizeRecoveryCheckpoint))
  discard = vi.fn(async (_scope: RecoveryScope, branchId: string, _revision: number) => { this.saved.delete(branchId) })
  close = vi.fn()
}

class RecoveryConnection implements MarkdownSyncConnection {
  calls: Array<{ method: string; args: unknown[] }> = []
  acknowledgements: Array<number | Promise<number> | Error> = []
  joins: Array<Record<string, unknown> | Promise<Record<string, unknown>>> = []
  snapshots: Promise<void>[] = []
  private seq = 0
  private closed: Array<() => void> = []
  private reconnecting: Array<() => void> = []
  private reconnected: Array<() => void> = []
  private handlers = new Map<string, (...args: any[]) => void>()
  start = vi.fn(async () => {})
  stop = vi.fn(async () => {})
  async send() {}
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.calls.push({ method, args })
    if (method === 'JoinDocument' || method === 'JoinDocumentWithSchema') return (this.joins.length ? this.joins.shift() : {
      snapshot: 'AAA=', updates: [], seq: this.seq, seedMarkdown: null,
      isSnapshotter: false, canWrite: true, schema: method === 'JoinDocumentWithSchema' ? args[1] : null,
    }) as T
    if (method === 'SendUpdate') {
      const result = this.acknowledgements.length ? this.acknowledgements.shift()! : ++this.seq
      if (result instanceof Error) throw result
      const sequence = await result
      this.seq = Math.max(this.seq, sequence)
      return sequence as T
    }
    if (method === 'PushSnapshot' && this.snapshots.length) await this.snapshots.shift()
    return undefined as T
  }
  on(method: string, handler: (...args: any[]) => void) { this.handlers.set(method, handler) }
  onClose(handler: () => void) { this.closed.push(handler) }
  onReconnecting(handler: () => void) { this.reconnecting.push(handler) }
  onReconnected(handler: () => void) { this.reconnected.push(handler) }
  reconnect() { this.reconnecting.forEach(handler => handler()); this.reconnected.forEach(handler => handler()) }
  close() { this.closed.forEach(handler => handler()) }
  emit(method: string, ...args: unknown[]) { this.handlers.get(method)?.(...args) }
  sent() { return this.calls.filter(call => call.method === 'SendUpdate') }
}

function recoveryHandle(store: MemoryRecoveryStore, override: RecoveryScope = scope) {
  return createMarkdownRecovery({ store, scope: override, checkpointDelayMs: 60_000, maxWaitMs: 60_000 })
}
function setup(options: Partial<SignalRYjsProviderOptions> = {}, store = new MemoryRecoveryStore()) {
  const ydoc = options.ydoc ?? document(), connection = new RecoveryConnection()
  const recovery = options.recovery ?? recoveryHandle(store), fatal = vi.fn(), pending = vi.fn(), synced = vi.fn()
  const provider = new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc, recovery,
    onFatalError: fatal, onPendingUpdates: pending, onSynced: synced, ...options }, connection)
  providers.push(provider)
  return { ydoc, connection, recovery, provider, store, fatal, pending, synced }
}
function saved(store: MemoryRecoveryStore, recovery: MarkdownRecovery) {
  const checkpoint = store.saved.get(recovery.getState().branchId)
  expect(checkpoint).toBeDefined()
  return checkpoint!
}

afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  providers.splice(0).forEach(provider => provider.destroy())
  documents.splice(0).forEach(doc => doc.destroy())
  documentBodyCleanup()
  transport.current = null
  vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks()
})
function documentBodyCleanup() { globalThis.document.body.replaceChildren() }

describe('provider opt-in manual recovery boundary', () => {
  it.each(['close', 'read-only'] as const)('invalidates recovery metadata when %s changes only the connection generation', async action => {
    const run = setup(); await run.provider.start()
    const old = await run.recovery.checkpoint()
    expect(saved(run.store, run.recovery).connectionGeneration).toBe(0)
    if (action === 'close') run.connection.close()
    else run.connection.emit('DocumentAccessChanged', 'doc', true, false)
    expect(run.recovery.getState().dirty).toBe(true)
    const next = await run.recovery.checkpoint()
    expect(next.revision).toBeGreaterThan(old.revision)
    expect(saved(run.store, run.recovery).connectionGeneration).toBe(1)
    expect(saved(run.store, run.recovery).observedSequence).toBe(0)
    expect(run.connection.sent()).toHaveLength(0)
  })

  it('captures the reconnect generation before the new join response arrives', async () => {
    const run = setup(), joining = deferred<Record<string, unknown>>()
    await run.provider.start(); const old = await run.recovery.checkpoint()
    run.connection.joins.push(joining.promise); run.connection.reconnect()
    try {
      await vi.waitFor(() => expect(run.connection.calls.filter(call => call.method === 'JoinDocument')).toHaveLength(2))
      const next = await run.recovery.checkpoint()
      expect(next.revision).toBeGreaterThan(old.revision)
      expect(saved(run.store, run.recovery).connectionGeneration).toBe(2)
    } finally {
      run.provider.destroy()
      joining.resolve({ snapshot: 'AAA=', updates: [], seq: 0, seedMarkdown: null, isSnapshotter: false, canWrite: true })
      await joining.promise
    }
  })

  it.each(['destroy', 'revoke', 'close'] as const)('retains pending data when a recovery observer %s during ACK tracking', async action => {
    const run = setup(), acknowledgement = deferred<number>()
    await run.provider.start(); run.connection.acknowledgements.push(acknowledgement.promise)
    run.ydoc.getText('content').insert(0, 'retain across observer invalidation')
    await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(1))
    await run.recovery.checkpoint()
    let armed = false, invalidated = false
    const unsubscribe = run.recovery.subscribe(state => {
      if (!armed || invalidated || !state.dirty) return
      invalidated = true
      if (action === 'destroy') run.provider.destroy()
      else if (action === 'revoke') run.connection.emit('DocumentAccessChanged', 'doc', false, false)
      else run.connection.close()
    })
    try {
      armed = true; acknowledgement.resolve(1)
      await acknowledgement.promise
      await vi.waitFor(() => expect(invalidated).toBe(true))
      expect(run.pending).toHaveBeenLastCalledWith(1)
      expect(run.ydoc.getText('content').toString()).toBe('retain across observer invalidation')
      if (action === 'close') {
        await run.recovery.checkpoint()
        expect(saved(run.store, run.recovery)).toMatchObject({ observedSequence: 1, connectionGeneration: 1 })
        expect(saved(run.store, run.recovery).pendingUpdates).toHaveLength(1)
      }
    } finally { unsubscribe(); acknowledgement.resolve(1); await acknowledgement.promise }
  })

  it('records a changed retry identity while its replacement invocation remains unacknowledged', async () => {
    const run = setup(), first = deferred<number>(), replacement = deferred<number>()
    await run.provider.start(); run.connection.acknowledgements.push(first.promise, replacement.promise)
    run.ydoc.getText('content').insert(0, 'retry identity')
    await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(1))
    const old = await run.recovery.checkpoint(), oldId = saved(run.store, run.recovery).pendingUpdates[0].id
    try {
      first.reject(new Error('[RETRY_WITH_NEW_ID] retained metadata expired'))
      await first.promise.catch(() => {})
      await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(2))
      expect(run.recovery.getState().dirty).toBe(true)
      const next = await run.recovery.checkpoint(), pending = saved(run.store, run.recovery).pendingUpdates[0]
      expect(next.revision).toBeGreaterThan(old.revision)
      expect(pending.id).not.toBe(oldId)
      expect(pending.id).toBe(run.connection.sent()[1].args[1])
      expect(pending.attempted).toBe(true)
      expect(textOf(pending.update)).toBe('retry identity')
    } finally { first.resolve(1); replacement.resolve(1); await Promise.allSettled([first.promise, replacement.promise]) }
  })

  it('records attempted metadata when the initial snapshot seals an already recorded pending prefix', async () => {
    const run = setup(), snapshot = deferred<void>()
    run.connection.joins.push({ snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true, canWrite: true })
    run.connection.snapshots.push(snapshot.promise)
    run.ydoc.getText('content').insert(0, 'pending initial baseline')
    const starting = run.provider.start()
    await vi.waitFor(() => expect(run.connection.calls.some(call => call.method === 'JoinDocument')).toBe(true))
    const old = await run.recovery.checkpoint()
    expect(saved(run.store, run.recovery).pendingUpdates[0].attempted).toBe(false)
    const editor = {
      commands: { setContent: vi.fn(), insertContentAt: vi.fn() },
      chain() { const chain = { setMeta: () => chain, setContent: (value: unknown, options: unknown) => { (this as unknown as { commands: { setContent: (value: unknown, options: unknown) => unknown } }).commands.setContent(value, options); return chain }, run: () => true }; return chain },
      state: { doc: { content: { size: 0 }, descendants() {} }, plugins: [{ getState: () => ({ binding: { prosemirrorView: {} } }) }] },
      view: {}, storage: { markdown: { getMarkdown: () => '' } },
      getJSON: () => ({ type: 'doc', content: [] }), getText: () => '', isEmpty: true,
    } as unknown as Editor
    try {
      run.provider.attachEditor(editor)
      await vi.waitFor(() => expect(run.connection.calls.filter(call => call.method === 'PushSnapshot')).toHaveLength(1))
      const next = await run.recovery.checkpoint()
      expect(next.revision).toBeGreaterThan(old.revision)
      expect(saved(run.store, run.recovery).pendingUpdates[0].attempted).toBe(true)
      expect(run.connection.sent()).toHaveLength(0)
    } finally { snapshot.resolve(); await snapshot.promise; await starting }
  })

  it('does not duplicate the in-flight send when an attempted-state observer synchronously edits', async () => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store), acknowledgement = deferred<number>()
    let armed = false, edited = false
    const run = setup({ recovery, onPendingUpdates: count => { if (count === 1) armed = true } }, store)
    await run.provider.start(); run.connection.acknowledgements.push(acknowledgement.promise)
    const unsubscribe = recovery.subscribe(state => {
      if (!armed || edited || !state.dirty) return
      edited = true
      run.ydoc.getText('content').insert(run.ydoc.getText('content').length, ' observer')
    })
    try {
      run.ydoc.getText('content').insert(0, 'first')
      await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(1))
      expect(edited).toBe(true)
      acknowledgement.resolve(1); await acknowledgement.promise
      await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(2))
      await vi.waitFor(() => expect(run.synced).toHaveBeenLastCalledWith(true))
      expect(run.connection.sent()[0].args[1]).not.toBe(run.connection.sent()[1].args[1])
      expect(run.ydoc.getText('content').toString()).toBe('first observer')
      expect(run.fatal).not.toHaveBeenCalled()
    } finally { unsubscribe(); acknowledgement.resolve(1); await acknowledgement.promise }
  })

  it('does not open IndexedDB or introduce storage work when recovery is omitted', async () => {
    vi.useFakeTimers()
    const open = vi.fn(() => { throw new Error('Unexpected IndexedDB access') })
    vi.stubGlobal('indexedDB', { open })
    const connection = new RecoveryConnection(), ydoc = document()
    const provider = new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc }, connection)
    providers.push(provider)
    await provider.start()
    ydoc.getText('content').insert(0, 'ordinary editing')
    await vi.advanceTimersByTimeAsync(6000)
    expect(connection.sent()).toHaveLength(1)
    expect(open).not.toHaveBeenCalled()
  })

  it('records an explicit pre-start checkpoint with matching full state and pending updates', async () => {
    const run = setup()
    run.ydoc.getText('content').insert(0, 'draft')
    run.ydoc.getText('content').insert(5, ' before start')
    const summary = await run.recovery.checkpoint(), checkpoint = saved(run.store, run.recovery)
    expect(textOf(checkpoint.yjsState)).toBe('draft before start')
    expect(checkpoint.pendingUpdates).toHaveLength(1)
    expect(textOf(checkpoint.pendingUpdates[0].update)).toBe('draft before start')
    expect(checkpoint.pendingUpdates[0]).toMatchObject({ attempted: false, updateCount: 2 })
    expect(checkpoint.observedSequence).toBe(0)
    expect(summary.pendingCount).toBe(2)
    expect(checkpoint).toMatchObject({ formatVersion: 1, scope, branchId: summary.branchId,
      epoch: null, replayPolicy: 'manual-only', fragment: 'default', schema: null })
    expect(run.connection.start).not.toHaveBeenCalled()
    expect(run.store.read).not.toHaveBeenCalled(); expect(run.store.list).not.toHaveBeenCalled()
  })

  it('defers a checkpoint requested from a projection observer until the update has entered the outbox', async () => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store)
    let capture: Promise<RecoverySummary> | undefined
    const run = setup({ recovery, onProjectionStatus: () => {
      capture = recovery.checkpoint()
      expect(store.writes).toHaveLength(0)
    } }, store)
    run.ydoc.getText('content').insert(0, 'callback edit')
    expect(capture).toBeDefined()
    await capture
    const checkpoint = saved(store, recovery)
    expect(textOf(checkpoint.yjsState)).toBe('callback edit')
    expect(checkpoint.pendingUpdates).toHaveLength(1)
    expect(textOf(checkpoint.pendingUpdates[0].update)).toBe('callback edit')
    expect(checkpoint.documentRevision).toBe(1)
  })

  it('captures a durable ACK after both sequence and pending bookkeeping have completed', async () => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store)
    let afterAck: Promise<RecoverySummary> | undefined
    const run = setup({ recovery, onPendingUpdates: count => {
      if (count === 0) afterAck = recovery.checkpoint()
    } }, store)
    await run.provider.start()
    run.ydoc.getText('content').insert(0, 'acknowledged')
    await vi.waitFor(() => expect(afterAck).toBeDefined())
    await afterAck
    const checkpoint = saved(store, recovery)
    expect(checkpoint.observedSequence).toBe(1)
    expect(checkpoint.pendingUpdates).toEqual([])
    expect(textOf(checkpoint.yjsState)).toBe('acknowledged')
    expect(run.fatal).not.toHaveBeenCalled()
  })

  it('keeps the previous checkpoint when a write fails while collaboration continues normally', async () => {
    const run = setup(), failure = deferred<void>()
    run.ydoc.getText('content').insert(0, 'first')
    const first = await run.recovery.checkpoint()
    run.store.waits.push(failure.promise)
    run.ydoc.getText('content').insert(5, ' then second')
    const failed = run.recovery.checkpoint(), rejection = expect(failed).rejects.toThrow('storage unavailable')
    try {
      await vi.waitFor(() => expect(run.store.writes).toHaveLength(2))
      failure.reject(new Error('storage unavailable')); await rejection
      expect(run.recovery.getState()).toMatchObject({ status: 'degraded', lastRecorded: first, dirty: true })
      expect(textOf(saved(run.store, run.recovery).yjsState)).toBe('first')
      await run.provider.start()
      expect(run.connection.sent()).toHaveLength(1)
      expect(run.synced).toHaveBeenLastCalledWith(true)
      expect(run.fatal).not.toHaveBeenCalled()
      await run.recovery.checkpoint()
      expect(textOf(saved(run.store, run.recovery).yjsState)).toBe('first then second')
      expect(saved(run.store, run.recovery).pendingUpdates).toEqual([])
    } finally { failure.resolve(); await failure.promise.catch(() => {}) }
  })

  it('isolates live Yjs and pending bytes from a host store mutating its checkpoint argument', async () => {
    const store = new MemoryRecoveryStore()
    store.onWrite = checkpoint => {
      checkpoint.yjsState.fill(255)
      checkpoint.pendingUpdates.forEach(update => update.update.fill(255))
    }
    const run = setup({}, store)
    run.ydoc.getText('content').insert(0, 'do not corrupt')
    await run.recovery.checkpoint()
    expect(run.ydoc.getText('content').toString()).toBe('do not corrupt')
    await run.provider.start()
    expect(run.connection.sent()).toHaveLength(1)
    expect(textOf(decode(run.connection.sent()[0].args[2] as string))).toBe('do not corrupt')
    expect(run.fatal).not.toHaveBeenCalled()
  })

  it.each([undefined, { version: 2 as const, profile: 'rich' as const, plugins: [{ id: 'charts', version: 1 }] }])(
    'records validated schema, fragment and manual-only metadata (%j)', async schema => {
      const run = setup({ schema, field: 'editor-body' })
      run.ydoc.getText('content').insert(0, 'metadata')
      await run.provider.start(); await run.recovery.checkpoint()
      const checkpoint = saved(run.store, run.recovery)
      expect(cloneRecoveryCheckpoint(checkpoint)).toEqual(checkpoint)
      expect(checkpoint.schema).toEqual(schema ?? null)
      expect(checkpoint.fragment).toBe('editor-body')
      expect(checkpoint.observedSequence).toBe(1)
      expect(checkpoint.connectionGeneration).toBe(0)
      expect(checkpoint.documentRevision).toBeGreaterThan(0)
      expect(checkpoint.capturedAt).toBeGreaterThan(0)
      expect(checkpoint.revision).toBe(1)
      expect(checkpoint.epoch).toBeNull()
      expect(checkpoint.replayPolicy).toBe('manual-only')
    },
  )

  it('finishes an already detached storage write after provider and Y.Doc destruction', async () => {
    const run = setup(), write = deferred<void>(); run.store.waits.push(write.promise)
    run.ydoc.getText('content').insert(0, 'detached recovery bytes')
    const checkpoint = run.recovery.checkpoint()
    try {
      await vi.waitFor(() => expect(run.store.writes).toHaveLength(1))
      run.provider.destroy(); run.ydoc.destroy()
      expect(run.recovery.getState().status).toBe('closed')
      write.resolve(); await checkpoint
      expect(textOf(saved(run.store, run.recovery).yjsState)).toBe('detached recovery bytes')
      expect(run.store.close).not.toHaveBeenCalled()
      expect(run.store.writes).toHaveLength(1)
      expect(run.recovery.getState()).toMatchObject({ status: 'closed', dirty: false })
    } finally { write.resolve(); await write.promise }
  })

  it('keeps every unsent edit recoverable when the server refuses an update, and never resends it', async () => {
    const run = setup(), refusal = deferred<number>()
    await run.provider.start(); run.connection.acknowledgements.push(refusal.promise)
    const text = run.ydoc.getText('content')
    text.insert(0, 'refused ')
    await vi.waitFor(() => expect(run.connection.sent()).toHaveLength(1))
    // Valid edits queue behind the update in flight; the refusal ends the session before they are sent.
    text.insert(8, 'valid')
    text.insert(13, ' after')
    refusal.reject(new Error('[INVALID_CONTENT] The document does not accept this content (InvalidDocument).'))
    await vi.waitFor(() => expect(run.fatal).toHaveBeenCalledOnce())
    await vi.waitFor(() => expect(run.recovery.getState().lastRecorded).not.toBeNull())
    const checkpoint = saved(run.store, run.recovery)
    expect(textOf(checkpoint.yjsState)).toBe('refused valid after')
    const replayed = document()
    for (const pending of checkpoint.pendingUpdates) Y.applyUpdate(replayed, pending.update)
    expect(replayed.getText('content').toString()).toBe('refused valid after')
    expect(run.recovery.getState().status).toBe('closed')
    await new Promise(resolve => setTimeout(resolve, 50))
    expect(run.connection.sent()).toHaveLength(1)
    expect(run.store.writes).toHaveLength(1)
  })

  it('opens a fresh branch for the same scope without reading, listing or replaying an older checkpoint', async () => {
    const store = new MemoryRecoveryStore(), first = setup({}, store)
    first.ydoc.getText('content').insert(0, 'old private draft')
    const old = await first.recovery.checkpoint(); first.provider.destroy()
    const second = setup({}, store)
    await second.provider.start(); const current = await second.recovery.checkpoint()
    expect(current.branchId).not.toBe(old.branchId)
    expect(second.ydoc.getText('content').toString()).toBe('')
    expect(second.connection.sent()).toHaveLength(0)
    expect(store.read).not.toHaveBeenCalled(); expect(store.list).not.toHaveBeenCalled()
    expect(store.saved.size).toBe(2)
    expect(textOf(store.saved.get(old.branchId)!.yjsState)).toBe('old private draft')
  })

  it('rejects a recovery namespace bound to another document before touching the transport', () => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store, { ...scope, documentId: 'other' })
    const connection = new RecoveryConnection(), ydoc = document()
    expect(() => new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc, recovery }, connection)).toThrow(/scoped|document/i)
    expect(connection.start).not.toHaveBeenCalled(); expect(store.writes).toEqual([])
    expect(store.read).not.toHaveBeenCalled(); expect(store.list).not.toHaveBeenCalled()
  })

  it('does not permit a disposed recovery handle to attach to a second provider', () => {
    const run = setup(); run.provider.destroy()
    expect(() => new SignalRYjsProvider({ hubUrl: '/hub', documentId: 'doc', ydoc: document(), recovery: run.recovery }, new RecoveryConnection()))
      .toThrow(/fresh handle/i)
    expect(run.store.close).not.toHaveBeenCalled()
  })

  it('tells the user that a refused change ended the session and where the unsent edits are', async () => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store), connection = new RecoveryConnection()
    transport.current = connection
    const adapter = createSignalRMarkdownAdapter({ hubUrl: '/hub', documentId: 'doc', presence: { id: 'account', name: 'User' }, recovery })
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    const host = globalThis.document.createElement('div'); globalThis.document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Refused collaboration test' })
    await vi.waitFor(() => expect(controller.state.lifecycle).toBe('ready'), { timeout: 20_000, interval: 20 })
    connection.acknowledgements.push(new Error('[INVALID_CONTENT] The document does not accept this content (InvalidDocument).'))
    const sentBefore = connection.sent().length
    const editor = editorForController(controller)!
    editor.view.dispatch(editor.state.tr.insertText('refused', 1))
    await vi.waitFor(() => expect(controller.state.connection).toBe('failed'))
    expect(controller.state.error).toMatchObject({ kind: 'collaboration', message: collaborationStrings.contentRefused, recoverable: false })
    await vi.waitFor(() => expect(recovery.getState().lastRecorded).not.toBeNull())
    expect(connection.sent()).toHaveLength(sentBefore + 1)
  })

  it.each([undefined, true])('keeps adapter offline editing opt-in despite enabled recovery (allowOfflineEditing=%s)', async allowOfflineEditing => {
    const store = new MemoryRecoveryStore(), recovery = recoveryHandle(store), connection = new RecoveryConnection()
    transport.current = connection
    const adapter = createSignalRMarkdownAdapter({ hubUrl: '/hub', documentId: 'doc',
      presence: { id: 'account', name: 'User' }, recovery, allowOfflineEditing })
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    const host = globalThis.document.createElement('div'); globalThis.document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Recovery collaboration test' })
    // Hydration takes seconds on a loaded machine; the deadline only guards against a hang.
    await vi.waitFor(() => expect(controller.state.lifecycle).toBe('ready'), { timeout: 20_000, interval: 20 })
    expect(controller.state.editable).toBe(true)
    connection.close()
    expect(controller.state.connection).toBe('offline')
    expect(controller.state.editable).toBe(allowOfflineEditing === true)
    await recovery.checkpoint()
    expect(recovery.getState().status).toBe('recorded')
    expect(controller.state.editable).toBe(allowOfflineEditing === true)
    expect(store.read).not.toHaveBeenCalled(); expect(store.list).not.toHaveBeenCalled()
  })
})
