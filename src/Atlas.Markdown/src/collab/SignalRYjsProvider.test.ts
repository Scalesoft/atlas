// @vitest-environment jsdom

import * as Y from 'yjs'
import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { MarkdownDocumentSchema } from '../core/types'
import { registerEditorProjection } from '../engine/projections'
import {
  SignalRYjsProvider,
  type MarkdownSyncConnection,
} from './SignalRYjsProvider'

type Invocation = { method: string; args: unknown[] }

class FakeConnection implements MarkdownSyncConnection {
  readonly invocations: Invocation[] = []
  stopped = false
  stopCalls = 0
  startCalls = 0
  startResults: Array<void | Error | Promise<void>> = []
  joinError?: Error
  joinResults: Array<Record<string, unknown> | Promise<Record<string, unknown>>> = []
  beforeJoin?: () => void
  joinResult: Record<string, unknown> = {
    snapshot: toBase64(Y.encodeStateAsUpdate(new Y.Doc())),
    updates: [],
    seq: 0,
    seedMarkdown: null,
    isSnapshotter: false,
    canWrite: true,
  }
  sendUpdateResults: Array<number | Error | Promise<number>> = []
  pushSnapshotResults: Array<void | Error | Promise<void>> = []
  rejectUpdatesUntilInitialSnapshot = false
  initialSnapshotStored = false
  storeOnPush = true

  private readonly handlers = new Map<string, Array<(...args: unknown[]) => void>>()
  private readonly reconnectingHandlers: Array<(error?: Error) => void> = []
  private readonly reconnectedHandlers: Array<(connectionId?: string) => void> = []
  private readonly closeHandlers: Array<(error?: Error) => void> = []

  async start(): Promise<void> {
    this.startCalls++
    const result = this.startResults.shift()
    if (result instanceof Error) throw result
    await result
  }

  async stop(): Promise<void> {
    this.stopped = true
    this.stopCalls++
  }

  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.invocations.push({ method, args })
    if (method === 'JoinDocument' || method === 'JoinDocumentWithSchema') {
      this.beforeJoin?.()
      if (this.joinError) throw this.joinError
      return (this.joinResults.shift() ?? this.joinResult) as T
    }
    if (method === 'SendUpdate') {
      if (this.rejectUpdatesUntilInitialSnapshot && !this.initialSnapshotStored) {
        throw new Error('The initial snapshot is not durably stored yet.')
      }
      const result = this.sendUpdateResults.shift() ?? 1
      if (result instanceof Error) throw result
      return result as T
    }
    if (method === 'PushSnapshot' || method === 'PushSnapshotGzip') {
      const result = this.pushSnapshotResults.shift()
      if (result instanceof Error) throw result
      if (result instanceof Promise) await result
      this.initialSnapshotStored = true
      // The server's flush stores it and says so (SnapshotStored); off: a test sends it itself.
      if (this.storeOnPush) this.emit('SnapshotStored', args[0], args[1])
    }
    return undefined as T
  }

  onSend?: (method: string, ...args: unknown[]) => void
  async send(method: string, ...args: unknown[]): Promise<void> { this.onSend?.(method, ...args) }

  on(method: string, handler: (...args: unknown[]) => void): void {
    const handlers = this.handlers.get(method) ?? []
    handlers.push(handler)
    this.handlers.set(method, handlers)
  }

  onReconnecting(handler: (error?: Error) => void): void {
    this.reconnectingHandlers.push(handler)
  }

  onReconnected(handler: (connectionId?: string) => void): void {
    this.reconnectedHandlers.push(handler)
  }

  onClose(handler: (error?: Error) => void): void {
    this.closeHandlers.push(handler)
  }

  emit(method: string, ...args: unknown[]): void {
    for (const handler of this.handlers.get(method) ?? []) handler(...args)
  }

  reconnect(): void {
    for (const handler of this.reconnectingHandlers) handler()
    for (const handler of this.reconnectedHandlers) handler('new-connection')
  }

  close(): void {
    for (const handler of this.closeHandlers) handler()
  }

  calls(method: string): Invocation[] {
    return this.invocations.filter(call => call.method === method)
  }
}

const providers: SignalRYjsProvider[] = []

afterEach(() => {
  for (const provider of providers.splice(0)) provider.destroy()
  vi.restoreAllMocks()
  vi.useRealTimers()
})

const gzip = async (bytes: Uint8Array) => new Uint8Array(await new Response(new Response(bytes as Uint8Array<ArrayBuffer>).body!.pipeThrough(new CompressionStream('gzip'))).arrayBuffer())

describe('SignalRYjsProvider compressed join snapshot', () => {
  it('hydrates a gzip snapshot like a raw one and fails closed on a broken one', async () => {
    const remote = new Y.Doc()
    remote.getText('content').insert(0, 'compressed '.repeat(500))
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    connection.joinResult = { ...connection.joinResult, snapshot: toBase64(await gzip(Y.encodeStateAsUpdate(remote))), snapshotEncoding: 'gzip' }
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider); await provider.start()
    await vi.waitFor(() => expect(local.getText('content').toString()).toBe('compressed '.repeat(500)))
    expect(fatal).not.toHaveBeenCalled()

    const broken = new FakeConnection(), brokenFatal = vi.fn()
    broken.joinResult = { ...broken.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(remote)), snapshotEncoding: 'gzip' }
    const failing = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(), onFatalError: brokenFatal }, broken)
    providers.push(failing); await failing.start()
    await vi.waitFor(() => expect(String(brokenFatal.mock.calls[0]?.[0])).toMatch(/PROTOCOL_ERROR.*compressed snapshot/))
    remote.destroy()
  })
})

describe('SignalRYjsProvider live document access', () => {
  it('downgrades a clean writer immediately while continuing to receive readable content', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const writable = vi.fn(), fatal = vi.fn()
    connection.joinResult.isSnapshotter = true
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onWritable: writable, onFatalError: fatal }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor()); await provider.start()
    connection.emit('DocumentAccessChanged', 'wiki:home', true, false)
    expect(writable.mock.calls).toEqual([[true], [false]])
    remote.getText('content').insert(0, 'readable remote edit')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(remote)), 1)
    connection.emit('SnapshotterChanged', 'wiki:home', true)
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.advanceTimersByTimeAsync(10_000)
    expect(local.getText('content').toString()).toBe('readable remote edit')
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.stopped).toBe(false)
    expect(fatal).not.toHaveBeenCalled()
    remote.destroy()
  })

  it.each([
    { outcome: 'acknowledges', canRead: true }, { outcome: 'rejects', canRead: true },
    { outcome: 'acknowledges', canRead: false }, { outcome: 'rejects', canRead: false },
  ])('retains pending data when a revoked write later $outcome (canRead=$canRead)', async ({ outcome, canRead }) => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), local = new Y.Doc(), reply = deferred<number>()
    const pending = vi.fn(), writable = vi.fn(), fatal = vi.fn(), synced = vi.fn()
    connection.sendUpdateResults.push(reply.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onPendingUpdates: pending, onWritable: writable, onFatalError: fatal, onSynced: synced }, connection)
    providers.push(provider); await provider.start()
    local.getText('content').insert(0, 'retain this draft')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    const sent = connection.calls('SendUpdate')[0]!.args.slice()
    connection.emit('DocumentAccessChanged', 'wiki:home', canRead, false)
    expect(writable).toHaveBeenLastCalledWith(false)
    expect(synced).toHaveBeenLastCalledWith(false)
    expect(fatal).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining(canRead ? '[READ_ONLY]' : '[FORBIDDEN]') }))
    if (outcome === 'acknowledges') reply.resolve(1)
    else reply.reject(new Error('[READ_ONLY] stale reply'))
    await vi.advanceTimersByTimeAsync(10_000)
    expect(pending).toHaveBeenLastCalledWith(1)
    expect(local.getText('content').toString()).toBe('retain this draft')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    expect(connection.calls('SendUpdate')[0]!.args).toEqual(sent)
    expect(connection.stopped).toBe(true)
    expect(fatal).toHaveBeenCalledOnce()
  })

  it('ignores document broadcasts and reconnect callbacks after read access is denied', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const fatal = vi.fn(), status = vi.fn(), archived = vi.fn(), degraded = vi.fn()
    remote.getText('content').insert(0, 'must not apply')
    const payload = toBase64(Y.encodeStateAsUpdate(remote))
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onFatalError: fatal, onStatus: status, onExternalEditArchived: archived, onFlushDegraded: degraded,
      // Observer reentrance must not bypass revocation before destroy() finishes.
      onWritable: value => { if (!value) connection.emit('ReceiveUpdate', 'wiki:home', payload, 1) },
    }, connection)
    providers.push(provider); await provider.start()
    connection.emit('DocumentAccessChanged', 'wiki:home', false, false)
    const statuses = status.mock.calls.slice()
    connection.emit('ReceiveUpdate', 'wiki:home', payload, 1)
    connection.emit('ReceiveAwareness', 'wiki:home', 'invalid base64')
    connection.emit('RequestSnapshot', 'wiki:home')
    connection.emit('SnapshotterChanged', 'wiki:home', true)
    connection.emit('FlushDegraded', 'wiki:home', true)
    connection.emit('ExternalEditArchived', 'wiki:home')
    connection.emit('DocumentAccessChanged', 'wiki:home', true, true)
    connection.reconnect(); connection.close()
    await Promise.resolve()
    expect(local.getText('content').toString()).toBe('')
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.stopped).toBe(true)
    expect(fatal).toHaveBeenCalledOnce()
    expect(fatal).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('[FORBIDDEN]') }))
    expect(status.mock.calls).toEqual(statuses)
    expect(archived).not.toHaveBeenCalled(); expect(degraded).not.toHaveBeenCalled()
    remote.destroy()
  })

  it('does not apply a stale successful join after denial', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc(), response = deferred<Record<string, unknown>>()
    const writable = vi.fn(), fatal = vi.fn()
    connection.joinResults.push(response.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onWritable: writable, onFatalError: fatal }, connection)
    providers.push(provider)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    connection.emit('DocumentAccessChanged', 'wiki:home', false, false)
    remote.getText('content').insert(0, 'old permitted snapshot')
    response.resolve({ ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(remote)) })
    await starting
    expect(local.getText('content').toString()).toBe('')
    expect(writable.mock.calls).toEqual([[false]])
    expect(fatal).toHaveBeenCalledOnce()
    remote.destroy()
  })

  it('supersedes an in-flight writable join with an authoritative reader join', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), response = deferred<Record<string, unknown>>()
    const writable = vi.fn()
    connection.joinResults.push(response.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onWritable: writable }, connection)
    providers.push(provider)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    connection.joinResult.canWrite = false
    connection.emit('DocumentAccessChanged', 'wiki:home', true, false)
    response.resolve({ ...connection.joinResult, canWrite: true })
    await starting
    expect(connection.calls('JoinDocument')).toHaveLength(2)
    expect(writable.mock.calls).toEqual([[false]])
    expect(connection.stopped).toBe(false)
  })

  it('rehydrates a read-to-write grant before enabling local edits', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), response = deferred<Record<string, unknown>>()
    const writable = vi.fn()
    connection.joinResult.canWrite = false
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onWritable: writable }, connection)
    providers.push(provider); await provider.start()
    connection.joinResults.push(response.promise)
    connection.emit('DocumentAccessChanged', 'wiki:home', true, true)
    expect(writable.mock.calls).toEqual([[false]])
    response.resolve({ ...connection.joinResult, canWrite: true })
    await vi.waitFor(() => expect(writable).toHaveBeenLastCalledWith(true))
    local.getText('content').insert(0, 'newly allowed edit')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    expect(connection.calls('JoinDocument')).toHaveLength(2)
    expect(connection.stopped).toBe(false)
  })

  it('ignores notifications for other documents, malformed flags, and disposed providers', async () => {
    const connection = new FakeConnection(), writable = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
      onWritable: writable, onFatalError: fatal }, connection)
    providers.push(provider); await provider.start()
    connection.emit('DocumentAccessChanged', 'wiki:other', false, false)
    connection.emit('DocumentAccessChanged', 'wiki:home', 'false', false)
    expect(writable.mock.calls).toEqual([[true]])
    expect(connection.stopped).toBe(false)
    provider.destroy()
    connection.emit('DocumentAccessChanged', 'wiki:home', false, false)
    expect(writable.mock.calls).toEqual([[true]])
    expect(fatal).not.toHaveBeenCalled()
  })

  it('seeds from a compressed seed and pushes the state alone, gzipped, when the server derives the projections (S7)', async () => {
    const connection = new FakeConnection(), local = new Y.Doc()
    const gzip = async (text: string) => new Uint8Array(await new Response(new Response(new TextEncoder().encode(text)).body!
      .pipeThrough(new CompressionStream('gzip'))).arrayBuffer())
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seed: toBase64(await gzip('{"markdown":"# Seed","editorJson":null}')),
      projectionsDerived: true, isSnapshotter: true, canWrite: true }
    const editor = seedableEditor()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local }, connection)
    providers.push(provider); provider.attachEditor(editor)
    await provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshotGzip')).toHaveLength(1))
    expect(editor.commands.setContent).toHaveBeenCalled()
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    const [, , state, projections] = connection.calls('PushSnapshotGzip')[0]!.args as [string, number, string, Record<string, unknown>]
    const inflated = new Uint8Array(await new Response(new Response(Uint8Array.from(atob(state), c => c.charCodeAt(0))).body!
      .pipeThrough(new DecompressionStream('gzip'))).arrayBuffer())
    expect(inflated).toEqual(Y.encodeStateAsUpdate(local))
    expect(projections).toMatchObject({ markdown: '', plainText: '', editorJson: { type: 'doc', content: [] } })
  })

  it('does not accept an in-flight initial snapshot acknowledgement after write revocation', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), reply = deferred<void>(), pending = vi.fn(), fatal = vi.fn()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true, canWrite: true }
    connection.pushSnapshotResults.push(reply.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onPendingUpdates: pending, onFatalError: fatal }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor())
    local.getText('content').insert(0, 'pending baseline')
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    connection.emit('DocumentAccessChanged', 'wiki:home', true, false)
    reply.resolve(); await starting
    expect(pending).toHaveBeenLastCalledWith(1)
    expect(local.getText('content').toString()).toBe('pending baseline')
    expect(fatal).toHaveBeenCalledOnce()
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.stopped).toBe(true)
  })

  it('cancels a snapshot being hashed before it can be sent by a downgraded writer', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), hash = deferred<ArrayBuffer>()
    connection.joinResult.isSnapshotter = true
    const provider = createProvider(connection, new Y.Doc())
    provider.attachEditor(seedableEditor()); await provider.start()
    const digest = vi.spyOn(crypto.subtle, 'digest').mockImplementationOnce(() => hash.promise)
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(digest).toHaveBeenCalledOnce())
    connection.emit('DocumentAccessChanged', 'wiki:home', true, false)
    hash.resolve(new ArrayBuffer(32))
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.stopped).toBe(false)
  })

  it.each([true, false])('unblocks a seed waiting for its editor when access becomes canRead=%s', async canRead => {
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '# Seed', isSnapshotter: true, canWrite: true }
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    connection.emit('DocumentAccessChanged', 'wiki:home', canRead, false)
    await starting
    const editor = seedableEditor(); provider.attachEditor(editor)
    expect(editor.commands.setContent).not.toHaveBeenCalled()
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(fatal).toHaveBeenCalledOnce()
    expect(connection.stopped).toBe(true)
  })

  it('honors reentrant denial from the permission callback before hydrating a join snapshot', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    remote.getText('content').insert(0, 'do not hydrate after denial')
    connection.joinResult.snapshot = toBase64(Y.encodeStateAsUpdate(remote))
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onWritable: writable => { if (writable) connection.emit('DocumentAccessChanged', 'wiki:home', false, false) },
    }, connection)
    providers.push(provider); await provider.start()
    expect(local.getText('content').toString()).toBe('')
    expect(connection.stopped).toBe(true)
    remote.destroy()
  })
})

describe('SignalRYjsProvider durable outbox', () => {
  it('joins the new connection before hydrating a response from a superseded connection', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), stale = new Y.Doc(), current = new Y.Doc()
    const oldJoin = deferred<Record<string, unknown>>()
    stale.getText('content').insert(0, 'stale response')
    current.getText('content').insert(0, 'current response')
    connection.joinResults.push(oldJoin.promise)
    connection.joinResult = { ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(current)) }
    const provider = createProvider(connection, local)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))

    connection.reconnect()
    oldJoin.resolve({ ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(stale)), canWrite: false })
    await starting

    expect(connection.calls('JoinDocument')).toHaveLength(2)
    expect(local.getText('content').toString()).toBe('current response')
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    stale.destroy(); current.destroy()
  })

  it('does not fail the new connection because an old join rejects after reconnect', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    const oldJoin = deferred<Record<string, unknown>>()
    connection.joinResults.push(oldJoin.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    connection.reconnect()
    oldJoin.reject(new Error('Forbidden: old connection'))
    await starting

    expect(fatal).not.toHaveBeenCalled()
    expect(connection.stopped).toBe(false)
    expect(connection.calls('JoinDocument')).toHaveLength(2)
  })

  it('preserves attempted bytes and ids when more edits arrive before a lost ACK is retried', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), ack = deferred<number>()
    const pending: number[] = []
    connection.sendUpdateResults.push(ack.promise, 1, 2)
    const provider = createProvider(connection, local, count => pending.push(count))
    await provider.start()
    local.getText('content').insert(0, 'first')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    local.getText('content').insert(5, ' second')
    ack.reject(new Error('Connection closed before ACK'))
    await vi.waitFor(() => expect(pending.at(-1)).toBe(2))
    connection.reconnect()
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(3))
    await vi.waitFor(() => expect(pending.at(-1)).toBe(0))

    const [first, retry, second] = connection.calls('SendUpdate')
    expect(retry.args).toEqual(first.args)
    expect(second.args[1]).not.toBe(first.args[1])
    const server = new Y.Doc()
    for (const call of [first, retry, second]) Y.applyUpdate(server, fromBase64(call.args[2] as string))
    expect(server.getText('content').toString()).toBe('first second')
    server.destroy()
  })

  it('abandons the old seed when reconnect happens before the editor mounts', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), current = new Y.Doc()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '# Obsolete seed', isSnapshotter: true }
    const provider = createProvider(connection, local)
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    current.getText('content').insert(0, 'current baseline')
    connection.joinResult = { snapshot: toBase64(Y.encodeStateAsUpdate(current)), updates: [], seq: 1, seedMarkdown: null, isSnapshotter: false, canWrite: true }
    connection.reconnect()
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
    const editor = seedableEditor()
    provider.attachEditor(editor)
    await starting

    expect(editor.commands.setContent).not.toHaveBeenCalled()
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.calls('JoinDocument')).toHaveLength(2)
    expect(local.getText('content').toString()).toBe('current baseline')
    current.destroy()
  })

  it('does not let a superseded initial snapshot rejection terminate the new connection', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    const snapshotWrite = deferred<void>()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true }
    connection.pushSnapshotResults.push(snapshotWrite.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider)
    provider.attachEditor(seedableEditor())
    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    connection.joinResult = { snapshot: toBase64(Y.encodeStateAsUpdate(local)), updates: [], seq: 0, seedMarkdown: null, isSnapshotter: false, canWrite: true }
    connection.reconnect()
    snapshotWrite.reject(new Error('Forbidden: expired old connection'))
    await starting

    expect(fatal).not.toHaveBeenCalled()
    expect(connection.stopped).toBe(false)
    expect(connection.calls('JoinDocument')).toHaveLength(2)
    expect(connection.calls('PushSnapshot')).toHaveLength(1)
  })

  it('retries an update after an old connection rejects without treating the new session as forbidden', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    const oldSend = deferred<number>()
    connection.sendUpdateResults.push(oldSend.promise, 1)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider)
    await provider.start()
    local.getText('content').insert(0, 'keep pending work')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    connection.reconnect()
    oldSend.reject(new Error('Forbidden: expired old connection'))
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))

    expect(fatal).not.toHaveBeenCalled()
    expect(connection.stopped).toBe(false)
    expect(connection.calls('SendUpdate')[1].args).toEqual(connection.calls('SendUpdate')[0].args)
  })

  it('applies reordered remote updates in sequence order without a rejoin or a duplicate delivery', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const provider = createProvider(connection, local)
    await provider.start()
    remote.getText('content').insert(0, 'one')
    const first = Y.encodeStateAsUpdate(remote), baseline = Y.encodeStateVector(remote)
    remote.getText('content').insert(3, ' two')
    const second = Y.encodeStateAsUpdate(remote, baseline)
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(second), 2)
    // The document stays an exact server prefix while sequence 1 is missing.
    expect(local.getText('content').toString()).toBe('')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(first), 1)
    expect(local.getText('content').toString()).toBe('one two')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(second), 2)
    expect(local.getText('content').toString()).toBe('one two')
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    remote.destroy()
  })

  it('batches remote updates into one transaction per frame while applying them is slow and goes back once it is fast (COL2-5)', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const provider = createProvider(connection, local)
    await provider.start()
    const updates: Uint8Array[] = []
    remote.on('update', (update: Uint8Array) => updates.push(update))
    for (const word of ['one', ' two', ' three', ' four', ' five']) remote.getText('content').insert(remote.getText('content').length, word)
    const transactions = vi.fn()
    local.on('afterTransaction', transactions)
    // The first apply takes 20 ms (a large page), the later ones none.
    let clock = 0, slow = true
    const now = vi.spyOn(performance, 'now').mockImplementation(() => { clock += slow ? 20 : 0; return clock })
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(updates[0]), 1)
    expect(local.getText('content').toString()).toBe('one')
    slow = false
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(updates[1]), 2)
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(updates[2]), 3)
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(updates[3]), 4)
    expect(local.getText('content').toString()).toBe('one')
    // One transaction in the next animation frame.
    await new Promise(resolve => requestAnimationFrame(resolve))
    expect(local.getText('content').toString()).toBe('one two three four')
    expect(transactions).toHaveBeenCalledTimes(2)
    // The batch was fast: the next update is applied at once again.
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(updates[4]), 5)
    expect(local.getText('content').toString()).toBe('one two three four five')
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    now.mockRestore()
    remote.destroy()
  })

  it('keeps editing writable when a peer update overtakes the acknowledgement of our own write', async () => {
    // Concurrent writers: the hub acknowledges our write as 1 after it broadcast a peer's write as 2.
    // A fresh join here took the editor readonly for a round trip and dropped the keys typed meanwhile.
    vi.useFakeTimers()
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const statuses: string[] = [], synced: boolean[] = []
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local,
      onStatus: value => statuses.push(value), onSynced: value => synced.push(value) }, connection)
    providers.push(provider); await provider.start()
    const ack = deferred<number>()
    connection.sendUpdateResults.push(ack.promise)
    local.getText('mine').insert(0, 'typed')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    remote.getText('peer').insert(0, 'peer')
    statuses.length = 0; synced.length = 0
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(remote)), 2)
    expect(local.getText('peer').toString()).toBe('')
    ack.resolve(1)
    await vi.waitFor(() => expect(local.getText('peer').toString()).toBe('peer'))
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    expect(statuses).not.toContain('reconnecting')
    expect(synced).not.toContain(false)
    expect(local.getText('mine').toString()).toBe('typed')
    remote.destroy()
  })

  it('ignores late transport updates after destruction', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), remote = new Y.Doc()
    const provider = createProvider(connection, local)
    await provider.start()
    provider.destroy()
    remote.getText('content').insert(0, 'late update')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(remote)), 1)
    expect(local.getText('content').toString()).toBe('')
    remote.destroy()
  })

  it('starts a fresh outbox drain when an acknowledgement triggers sequence-gap rehydration', async () => {
    const connection = new FakeConnection(), ydoc = new Y.Doc()
    const statuses: string[] = [], synced: boolean[] = [], pending: number[] = []
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onStatus: value => statuses.push(value), onSynced: value => synced.push(value), onPendingUpdates: value => pending.push(value),
    }, connection)
    providers.push(provider); await provider.start()
    connection.sendUpdateResults.push(3)
    connection.beforeJoin = () => {
      // The write was committed at seq 3, but the client missed broadcasts 1 and 2.
      // A fresh join includes its own write and the missing remote state.
      const server = new Y.Doc()
      Y.applyUpdate(server, Y.encodeStateAsUpdate(ydoc))
      server.getText('remote').insert(0, 'remote changes')
      connection.joinResult = { ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(server)), seq: 3 }
      server.destroy()
    }
    vi.useFakeTimers()
    ydoc.getText('content').insert(0, 'local change')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    // Broadcasts 1 and 2 may still be on their way: only a gap that stays open needs a fresh join.
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(3_000)
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
    await vi.waitFor(() => expect(statuses.at(-1)).toBe('connected'))
    expect(synced.at(-1)).toBe(true); expect(pending.at(-1)).toBe(0)
    expect(ydoc.getText('content').toString()).toBe('local change')
    expect(ydoc.getText('remote').toString()).toBe('remote changes')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
  })

  it('does not report connected or synced while a durable outbox send is blocked', async () => {
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('disk unavailable'))
    const ydoc = new Y.Doc()
    ydoc.getText('content').insert(0, 'not durable yet')
    const statuses: string[] = []
    const synced: boolean[] = []
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onStatus: status => statuses.push(status), onSynced: value => synced.push(value),
    }, connection)
    providers.push(provider)

    await provider.start()

    expect(statuses.at(-1)).toBe('reconnecting')
    expect(synced.at(-1)).toBe(false)
    expect(connection.calls('SendUpdate')).toHaveLength(1)
  })

  it('reports durable snapshot sequences for its own document only', async () => {
    const connection = new FakeConnection()
    const stored: number[] = []
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(), onSnapshotStored: seq => stored.push(seq) }, connection)
    providers.push(provider)
    await provider.start()
    connection.emit('SnapshotStored', 'wiki:other', 7)
    connection.emit('SnapshotStored', 'wiki:home', 3)
    expect(stored).toEqual([3])
  })

  it('reports the projection current once the server stored the pushed snapshot, not when it acknowledged the push', async () => {
    // 3.6: autosave waited for 'current', which came with the push's acknowledgement; the server stores it with its next flush, so
    // the save got collaboration_pending (409) and retried on normal typing.
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), projection = vi.fn()
    connection.joinResult.isSnapshotter = true
    connection.storeOnPush = false
    connection.sendUpdateResults.push(1)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onProjectionStatus: projection }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor())
    await provider.start()
    ydoc.getText('content').insert(0, 'typed')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    await vi.advanceTimersByTimeAsync(5000)
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(connection.calls('PushSnapshot')[0].args[1]).toBe(1)
    expect(projection).toHaveBeenLastCalledWith('pending')
    connection.emit('SnapshotStored', 'wiki:home', 1)
    expect(projection).toHaveBeenLastCalledWith('current')
  })

  // Campaign 2026-11-05 F5: the snapshotter froze; the server's own checkpoint stored the colleague's words, but the colleague's tab
  // only learned it from its next timed retry (~31 s, an error state on the way).
  it('reports the projection current to a writer that is no snapshotter once a stored snapshot covers all it holds', async () => {
    const connection = new FakeConnection(), ydoc = new Y.Doc(), projection = vi.fn()
    connection.sendUpdateResults.push(1)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onProjectionStatus: projection }, connection)
    providers.push(provider)
    await provider.start()
    ydoc.getText('content').insert(0, 'typed')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    await new Promise(done => setTimeout(done, 0)) // the acknowledgement is incorporated
    expect(projection).toHaveBeenLastCalledWith('pending')
    connection.emit('SnapshotStored', 'wiki:home', 0) // older than what it holds: still pending
    expect(projection).toHaveBeenLastCalledWith('pending')
    connection.emit('SnapshotStored', 'wiki:home', 1)
    expect(projection).toHaveBeenLastCalledWith('current')
  })

  it('isolates observer failures from a committed Yjs edit', async () => {
    const connection = new FakeConnection()
    const ydoc = new Y.Doc()
    const callbackError = vi.fn()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onPendingUpdates: () => { throw new Error('host observer failed') }, onCallbackError: callbackError,
    }, connection)
    providers.push(provider)
    await provider.start()

    expect(() => ydoc.getText('content').insert(0, 'committed')).not.toThrow()
    expect(ydoc.getText('content').toString()).toBe('committed')
    await vi.waitFor(() => expect(callbackError).toHaveBeenCalled())
  })

  it('coalesces a long offline edit stream into one durable update batch', async () => {
    const connection = new FakeConnection()
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    await provider.start()
    connection.close()
    for (let index = 0; index < 2_000; index++) ydoc.getText('content').insert(index, 'x')

    connection.reconnect()
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))

    const durable = new Y.Doc()
    Y.applyUpdate(durable, fromBase64(connection.calls('SendUpdate')[0].args[2] as string))
    expect(durable.getText('content').length).toBe(2_000)
  })

  it('still sends already captured edits after the local outbox safety cap is crossed', async () => {
    const connection = new FakeConnection()
    const ydoc = new Y.Doc()
    const full = vi.fn()
    const synced: boolean[] = []
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, maxPendingBytes: 1,
      onDocumentFull: full, onSynced: value => synced.push(value),
    }, connection)
    providers.push(provider)
    await provider.start()

    ydoc.getText('content').insert(0, 'must become durable')

    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    expect(full).toHaveBeenCalledOnce()
    expect(synced.at(-1)).toBe(false)
  })

  it('treats a deterministic seed transform failure as fatal without retrying join', async () => {
    const connection = new FakeConnection()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '# Seed', isSnapshotter: true }
    const fatal = vi.fn()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
      onSeeded: () => { throw new Error('migration is invalid') }, onFatalError: fatal,
    }, connection)
    providers.push(provider)
    provider.attachEditor(seedableEditor())

    await provider.start()

    expect(connection.calls('JoinDocument')).toHaveLength(1)
    expect(fatal).toHaveBeenCalledWith(expect.objectContaining({ name: 'SeedTransformError' }))
  })

  it('retries an initial transport failure until it can join', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    connection.startResults.push(new Error('server starting'), undefined)
    const provider = createProvider(connection, new Y.Doc())

    const starting = provider.start()
    await vi.waitFor(() => expect(connection.startCalls).toBe(1))
    await vi.advanceTimersByTimeAsync(500)
    await starting

    expect(connection.startCalls).toBe(2)
    expect(connection.calls('JoinDocument')).toHaveLength(1)
  })

  it('retries a durable-write failure without changing the client update id', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('disk unavailable'), 1)
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    await provider.start()

    ydoc.getText('content').insert(0, 'retry me')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    await vi.advanceTimersByTimeAsync(1000)
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))

    const [first, retry] = connection.calls('SendUpdate')
    expect(retry.args[1]).toBe(first.args[1])
  })

  it('rekeys a durable update only when the server can no longer validate its old retry metadata', async () => {
    const connection = new FakeConnection()
    connection.joinResult.seq = 6
    connection.sendUpdateResults.push(new Error('[RETRY_WITH_NEW_ID] Replay metadata expired.'), 7)
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    await provider.start()

    ydoc.getText('content').insert(0, 'replay safely')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))

    const [first, retry] = connection.calls('SendUpdate')
    expect(retry.args[1]).not.toBe(first.args[1])
    expect(retry.args[2]).toBe(first.args[2])
  })

  it('treats an update id collision as fatal instead of retrying an ambiguous payload', async () => {
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('[UPDATE_ID_COLLISION] Client update id was reused.'))
    const fatal = vi.fn()
    const ydoc = new Y.Doc()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onFatalError: fatal,
    }, connection)
    providers.push(provider)
    await provider.start()

    ydoc.getText('content').insert(0, 'collision')
    await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    expect(connection.stopped).toBe(true)
  })

  // Campaign 2026-11-05 F6: a writer on a replaced generation got "Update could not be durably stored" and resent every second.
  it('ends the session on a superseded generation instead of resending the update every second', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('[SUPERSEDED] The document was replaced; join it again.'))
    const fatal = vi.fn()
    const ydoc = new Y.Doc()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onFatalError: fatal,
    }, connection)
    providers.push(provider)
    try {
      await provider.start()
      ydoc.getText('content').insert(0, 'late')
      await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
      await vi.advanceTimersByTimeAsync(10_000)
      expect(fatal.mock.calls[0]![0].message).toContain('[SUPERSEDED]')
      expect(connection.calls('SendUpdate')).toHaveLength(1)
      expect(connection.stopped).toBe(true)
    } finally {
      vi.useRealTimers()
    }
  })

  it('treats refused content as fatal instead of resending it forever', async () => {
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('[INVALID_CONTENT] The document does not accept this content (ReservedNode).'))
    const fatal = vi.fn()
    const ydoc = new Y.Doc()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onFatalError: fatal,
    }, connection)
    providers.push(provider)
    await provider.start()

    ydoc.getText('content').insert(0, 'refused')
    await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
    expect(fatal.mock.calls[0]![0].message).toContain('[INVALID_CONTENT]')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    expect(connection.stopped).toBe(true)
  })

  it('treats a refused snapshot as fatal instead of retrying it', async () => {
    const connection = new FakeConnection(), local = new Y.Doc(), fatal = vi.fn()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true, canWrite: true }
    connection.pushSnapshotResults.push(new Error('[INVALID_CONTENT] The document does not accept this content (UnknownNode).'))
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onFatalError: fatal }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor())
    local.getText('content').insert(0, 'refused baseline')
    void provider.start()
    await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
    expect(connection.calls('PushSnapshot')).toHaveLength(1)
    expect(connection.stopped).toBe(true)
  })

  it('stops on a forbidden write without an access event and retains every pending edit', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), reply = deferred<number>()
    const fatal = vi.fn(), pending = vi.fn()
    connection.sendUpdateResults.push(reply.promise)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onFatalError: fatal, onPendingUpdates: pending }, connection)
    providers.push(provider); await provider.start()
    ydoc.getText('content').insert(0, 'first draft')
    ydoc.getText('content').insert(11, ' and queued edit')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    const originalSend = connection.calls('SendUpdate')[0]!.args.slice()
    // The permission notification was never delivered; the RPC is authoritative.
    reply.reject(new Error('[FORBIDDEN] Document write access has been revoked.'))
    await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.stopped).toBe(true)
    expect(fatal).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('[FORBIDDEN]') }))
    expect(pending).toHaveBeenLastCalledWith(2)
    expect(ydoc.getText('content').toString()).toBe('first draft and queued edit')
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    expect(connection.calls('SendUpdate')[0]!.args).toEqual(originalSend)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
  })

  it('stops on a forbidden join without an access event and preserves the local outbox', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), fatal = vi.fn(), pending = vi.fn()
    connection.joinError = new Error('[FORBIDDEN] Document read access has been revoked.')
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onFatalError: fatal, onPendingUpdates: pending }, connection)
    providers.push(provider)
    ydoc.getText('content').insert(0, 'keep offline draft')
    await provider.start()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.stopped).toBe(true)
    expect(fatal).toHaveBeenCalledOnce()
    expect(pending).toHaveBeenLastCalledWith(1)
    expect(ydoc.getText('content').toString()).toBe('keep offline draft')
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    expect(connection.calls('SendUpdate')).toHaveLength(0)
  })

  it('treats a read-only write rejection as terminal instead of retrying the outbox', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    connection.sendUpdateResults.push(new Error('[READ_ONLY] Updates are not accepted.'))
    const fatal = vi.fn()
    const ydoc = new Y.Doc()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onFatalError: fatal,
    }, connection)
    providers.push(provider)
    await provider.start()

    ydoc.getText('content').insert(0, 'cannot persist')
    await vi.waitFor(() => expect(fatal).toHaveBeenCalledOnce())
    await vi.advanceTimersByTimeAsync(5_000)
    expect(connection.calls('SendUpdate')).toHaveLength(1)
    expect(connection.stopped).toBe(true)
  })

  it('joins an authoritative read-only document without attempting a back-sync', async () => {
    const connection = new FakeConnection()
    connection.joinResult.canWrite = false
    const writable: boolean[] = []
    const synced: boolean[] = []
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
      onWritable: value => writable.push(value), onSynced: value => synced.push(value),
    }, connection)
    providers.push(provider)

    await provider.start()

    expect(writable).toEqual([false])
    expect(synced.at(-1)).toBe(true)
    expect(connection.calls('SendUpdate')).toHaveLength(0)
  })

  it('fails closed when a read-only join would merge pre-existing local changes', async () => {
    const connection = new FakeConnection()
    connection.joinResult.canWrite = false
    const ydoc = new Y.Doc()
    ydoc.getText('content').insert(0, 'local draft')
    const fatal = vi.fn()
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, onFatalError: fatal,
    }, connection)
    providers.push(provider)

    await provider.start()

    expect(fatal).toHaveBeenCalledWith(expect.objectContaining({ message: expect.stringContaining('[READ_ONLY]') }))
    expect(connection.calls('SendUpdate')).toHaveLength(0)
  })

  it('reuses one client update id until the server acknowledges it', async () => {
    const connection = new FakeConnection()
    connection.joinResult.seq = 6
    connection.sendUpdateResults.push(new Error('disk unavailable'), 7)
    const pending: number[] = []
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc, count => pending.push(count))
    await provider.start()

    ydoc.getText('content').insert(0, 'hello')
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
    connection.reconnect()
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))

    const [first, retry] = connection.calls('SendUpdate')
    expect(first.args[1]).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i)
    expect(retry.args[1]).toBe(first.args[1])
    expect(pending).toEqual([1, 0])
  })

  it('queues offline edits and sends the missing state after reconnect', async () => {
    const connection = new FakeConnection()
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    await provider.start()

    connection.close()
    ydoc.getText('content').insert(0, 'offline edit')
    expect(connection.calls('SendUpdate')).toHaveLength(0)

    connection.reconnect()
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))

    const durable = new Y.Doc()
    const payload = connection.calls('SendUpdate')[0].args[2] as string
    Y.applyUpdate(durable, fromBase64(payload))
    expect(durable.getText('content').toString()).toBe('offline edit')
  })

  it('back-syncs local state that existed before the provider was created', async () => {
    const connection = new FakeConnection()
    const ydoc = new Y.Doc()
    ydoc.getText('content').insert(0, 'draft before connect')
    const provider = createProvider(connection, ydoc)

    await provider.start()
    await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))

    const durable = new Y.Doc()
    const payload = connection.calls('SendUpdate')[0].args[2] as string
    Y.applyUpdate(durable, fromBase64(payload))
    expect(durable.getText('content').toString()).toBe('draft before connect')
  })

  it('applies a remote update without echoing it to SendUpdate', async () => {
    const connection = new FakeConnection()
    const local = new Y.Doc()
    const provider = createProvider(connection, local)
    await provider.start()

    const remote = new Y.Doc()
    remote.getText('content').insert(0, 'from Bob')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(remote)), 1)
    await Promise.resolve()

    expect(local.getText('content').toString()).toBe('from Bob')
    expect(connection.calls('SendUpdate')).toHaveLength(0)
  })

  it('pushes markdown, editor JSON and plain text bound to the same state hash', async () => {
    const connection = new FakeConnection()
    connection.joinResult.isSnapshotter = true
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    provider.attachEditor({
      storage: { markdown: { getMarkdown: () => '# Hello' } },
      getJSON: () => ({ type: 'doc', content: [] }),
      getText: () => 'Hello',
      state: { doc: { descendants() {} } },
    } as never)
    await provider.start()

    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot').length).toBeGreaterThan(0))

    const call = connection.calls('PushSnapshot').at(-1)!
    const state = fromBase64(call.args[2] as string)
    const projection = call.args[3] as Record<string, unknown>
    expect(projection).toMatchObject({
      sourceSequence: call.args[1],
      stateSha256: await sha256(state),
      markdown: '# Hello',
      editorJson: { type: 'doc', content: [] },
      plainText: 'Hello',
    })
  })

  it('publishes the controller projection instead of independently serializing plugin content', async () => {
    const connection = new FakeConnection()
    connection.joinResult.isSnapshotter = true
    const provider = createProvider(connection, new Y.Doc())
    const editor = seedableEditor()
    const document = { type: 'doc', content: [{ type: 'chart', attrs: { title: 'Revenue' } }] }
    registerEditorProjection(editor, () => ({
      markdown: '[Chart: Revenue]', text: 'Chart\n\nRevenue', markdownLossy: true,
      rich: { format: 'atlas-rich', version: 1, plugins: [{ id: 'charts', version: 1 }], document },
    }))
    provider.attachEditor(editor)
    await provider.start()
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(connection.calls('PushSnapshot')[0].args[3]).toMatchObject({ markdown: '[Chart: Revenue]', plainText: 'Chart\n\nRevenue', editorJson: document })
  })

  it('lets the elected snapshotter migrate a live document once, outside every undo history', async () => {
    const card = (...names: string[]) => ({ type: 'contactCard', attrs: { data: { version: 1, contacts: names.map(name => ({ name })) } } })
    const connection = new FakeConnection()
    connection.joinResult.isSnapshotter = true
    const provider = createProvider(connection, new Y.Doc())
    let live = { type: 'doc', content: [card('Jana', 'Petr')] }
    const chain = { setMeta: vi.fn(() => chain), setContent: vi.fn((next: typeof live) => { live = next; return chain }), run: vi.fn() }
    const editor = Object.assign(seedableEditor(), { getJSON: () => live, chain: () => chain })
    provider.attachEditor(editor)
    await provider.start()
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(chain.setMeta).toHaveBeenCalledWith('addToHistory', false)
    expect(chain.setContent).toHaveBeenCalledExactlyOnceWith({ type: 'doc', content: [card('Jana'), card('Petr')] }, { emitUpdate: false })
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(2))
    expect(chain.setContent).toHaveBeenCalledOnce()
  })

  it('seeds rich documents without losing structure through a Markdown projection', async () => {
    const connection = new FakeConnection()
    const richSeed = { type: 'doc', content: [{ type: 'paragraph', attrs: { textAlign: 'center' }, content: [{ type: 'text', text: 'Rich seed' }] }] }
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: 'Lossy fallback', seedEditorJson: richSeed, isSnapshotter: true, canWrite: true }
    const provider = createProvider(connection, new Y.Doc())
    const editor = seedableEditor()
    provider.attachEditor(editor)
    await provider.start()
    expect(editor.commands.setContent).toHaveBeenCalledWith(richSeed, { emitUpdate: false })
    expect(connection.calls('PushSnapshot')).toHaveLength(1)
  })

  it('does not report an initial seed as synced until its snapshot retry succeeds', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    connection.joinResult = {
      snapshot: null,
      updates: [],
      seq: 0,
      seedMarkdown: '# Seed',
      isSnapshotter: true,
    }
    connection.pushSnapshotResults.push(new Error('disk unavailable'), undefined)
    const synced: boolean[] = []
    const provider = createProvider(connection, new Y.Doc(), undefined, value => synced.push(value))
    provider.attachEditor(seedableEditor())

    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(synced).not.toContain(true)

    await vi.advanceTimersByTimeAsync(1000)
    await starting

    expect(connection.calls('PushSnapshot')).toHaveLength(2)
    expect(synced.at(-1)).toBe(true)
  })

  it('stores local state created before the first join in the initial snapshot without pumping an unseeded outbox', async () => {
    const connection = new FakeConnection()
    connection.rejectUpdatesUntilInitialSnapshot = true
    connection.joinResult = {
      snapshot: null,
      updates: [],
      seq: 0,
      seedMarkdown: '',
      isSnapshotter: true,
    }
    const pending: number[] = []
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc, count => pending.push(count))
    provider.attachEditor(seedableEditor())
    ydoc.getText('content').insert(0, 'typed while joining')

    await provider.start()

    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.calls('PushSnapshot')).toHaveLength(1)
    expect(pending).toEqual([1, 0])
    const durable = new Y.Doc()
    Y.applyUpdate(durable, fromBase64(connection.calls('PushSnapshot')[0].args[2] as string))
    expect(durable.getText('content').toString()).toBe('typed while joining')
  })

  it('installs the server seed before preserving local editor nodes', async () => {
    const connection = new FakeConnection()
    connection.rejectUpdatesUntilInitialSnapshot = true
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '# Server seed', isSnapshotter: true }
    const ydoc = new Y.Doc()
    ydoc.getText('content').insert(0, 'state supplied before provider construction')
    const provider = createProvider(connection, ydoc)
    const calls: Array<{ command: string, value: unknown }> = []
    const localContent = [{ type: 'paragraph', content: [{ type: 'text', text: 'Local draft' }] }]
    const editor = seedableEditor() as unknown as {
      commands: { setContent: (value: unknown, options: unknown) => boolean, insertContentAt: (position: number, value: unknown, options: unknown) => boolean }
      getJSON: () => { type: string, content: typeof localContent }
      state: { doc: { content: { size: number } } }
      isEmpty: boolean
    }
    editor.isEmpty = false
    editor.getJSON = () => ({ type: 'doc', content: localContent })
    editor.state.doc.content = { size: 12 }
    editor.commands.setContent = (value) => { calls.push({ command: 'seed', value }); return true }
    editor.commands.insertContentAt = (position, value) => { calls.push({ command: `local@${position}`, value }); return true }
    provider.attachEditor(editor as never)

    await provider.start()

    expect(calls).toEqual([
      { command: 'seed', value: '# Server seed' },
      { command: 'local@12', value: localContent },
    ])
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.calls('PushSnapshot')).toHaveLength(1)
  })

  it('keeps edits made during the initial snapshot in the durable outbox', async () => {
    const connection = new FakeConnection()
    connection.rejectUpdatesUntilInitialSnapshot = true
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true }
    const snapshotWrite = deferred<void>()
    connection.pushSnapshotResults.push(snapshotWrite.promise)
    const ydoc = new Y.Doc()
    const provider = createProvider(connection, ydoc)
    provider.attachEditor(seedableEditor())
    ydoc.getText('content').insert(0, 'before snapshot')

    const starting = provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    ydoc.getText('content').insert(15, ' and during write')
    snapshotWrite.resolve()
    await starting

    expect(connection.calls('SendUpdate')).toHaveLength(1)
    const durable = new Y.Doc()
    Y.applyUpdate(durable, fromBase64(connection.calls('PushSnapshot')[0].args[2] as string))
    Y.applyUpdate(durable, fromBase64(connection.calls('SendUpdate')[0].args[2] as string))
    expect(durable.getText('content').toString()).toBe('before snapshot and during write')
  })

  it('coalesces concurrent snapshot requests into one in-flight write', async () => {
    const connection = new FakeConnection()
    connection.joinResult.isSnapshotter = true
    const pending = deferred<void>()
    connection.pushSnapshotResults.push(pending.promise)
    const provider = createProvider(connection, new Y.Doc())
    provider.attachEditor(seedableEditor())
    await provider.start()

    connection.emit('RequestSnapshot', 'wiki:home')
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))

    expect(connection.calls('PushSnapshot')).toHaveLength(1)
    pending.resolve()
    await pending.promise
  })

  it('rehydrates instead of applying an update that skips a server sequence that never arrives', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection()
    const local = new Y.Doc()
    const provider = createProvider(connection, local)
    await provider.start()

    const remote = new Y.Doc()
    remote.getText('content').insert(0, 'must arrive through rehydrate')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(remote)), 2)

    await vi.advanceTimersByTimeAsync(2_900)
    expect(connection.calls('JoinDocument')).toHaveLength(1)
    await vi.advanceTimersByTimeAsync(100)
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
    expect(local.getText('content').toString()).toBe('')
  })

  it('stops the injected transport during teardown', async () => {
    const connection = new FakeConnection()
    const provider = createProvider(connection, new Y.Doc())
    await provider.start()

    provider.destroy()
    await vi.waitFor(() => expect(connection.stopped).toBe(true))
  })

  it.each([
    ['the page is hidden', () => {
      vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
      document.dispatchEvent(new Event('visibilitychange'))
    }],
    ['the page is left', () => { window.dispatchEvent(new Event('beforeunload')) }],
    // A route change inside the app: the editor is projected before it goes, the push goes on without it.
    ['the editor leaves inside the app', (provider: SignalRYjsProvider) => { provider.detachEditor() }],
  ])('sends a snapshot still waiting for the user to rest when %s', async (_, leave: (provider: SignalRYjsProvider) => void) => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), local = new Y.Doc()
    connection.joinResult.isSnapshotter = true
    const provider = createProvider(connection, local)
    provider.attachEditor(Object.assign(seedableEditor(), { view: { dom: document.createElement('div') } }))
    await provider.start()
    // An edit the server's snapshot does not hold yet (an unchanged page needs none, 3.6 perf).
    local.getText('content').insert(0, 'typed')
    // Due, but the user is typing: the snapshot waits for them to rest.
    await vi.advanceTimersByTimeAsync(900)
    window.dispatchEvent(new Event('keydown'))
    await vi.advanceTimersByTimeAsync(400)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)

    leave(provider)
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
  })

  it('releases the attached DOM realm while the transport remains alive', () => {
    const connection = new FakeConnection()
    const provider = createProvider(connection, new Y.Doc())
    const remove = vi.spyOn(window, 'removeEventListener')
    provider.attachEditor({ view: { dom: document.createElement('div') } } as never)

    provider.detachEditor()

    expect(remove).toHaveBeenCalledWith('beforeunload', expect.any(Function))
  })
})

function createProvider(
  connection: FakeConnection,
  ydoc: Y.Doc,
  onPendingUpdates?: (count: number) => void,
  onSynced?: (synced: boolean) => void,
): SignalRYjsProvider {
  const provider = new SignalRYjsProvider({
    hubUrl: '/hubs/markdown',
    documentId: 'wiki:home',
    ydoc,
    onPendingUpdates,
    onSynced,
  }, connection)
  providers.push(provider)
  return provider
}

function seedableEditor() {
  return {
    commands: { setContent: vi.fn() },
    // The seed runs as a chain (it carries preventAutolink); the chain's setContent is the command the tests watch.
    chain() { const chain = { setMeta: () => chain, setContent: (value: unknown, options: unknown) => { (this as unknown as { commands: { setContent: (value: unknown, options: unknown) => unknown } }).commands.setContent(value, options); return chain }, run: () => true }; return chain },
    state: {
      doc: { descendants() {} },
      plugins: [{ getState: () => ({ binding: { prosemirrorView: {} } }) }],
    },
    view: {},
    storage: { markdown: { getMarkdown: () => '# Seed' } },
    getJSON: () => ({ type: 'doc', content: [] }),
    getText: () => 'Seed',
  } as unknown as Editor
}

describe('SignalRYjsProvider schema negotiation', () => {
  const schema: MarkdownDocumentSchema = { version: 1, profile: 'rich', plugins: [{ id: 'youtrack', version: 2 }, { id: 'charts', version: 1 }] }

  function create(connection: FakeConnection, document: Y.Doc, onFatalError = vi.fn()) {
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: document, schema, onFatalError }, connection)
    providers.push(provider)
    return provider
  }

  it('sends canonical schema and accepts equivalent PascalCase metadata before hydrating', async () => {
    const connection = new FakeConnection()
    const remote = new Y.Doc()
    remote.getText('content').insert(0, 'preserved')
    connection.joinResult.snapshot = toBase64(Y.encodeStateAsUpdate(remote))
    connection.joinResult.Schema = { Version: 1, Profile: 'rich', Plugins: [{ Id: 'youtrack', Version: 2 }, { Id: 'charts', Version: 1 }] }
    const local = new Y.Doc()
    await create(connection, local).start()

    expect(connection.calls('JoinDocument')).toHaveLength(0)
    expect(connection.calls('JoinDocumentWithSchema')[0].args[1]).toEqual({ ...schema, plugins: [...schema.plugins].reverse() })
    expect(local.getText('content').toString()).toBe('preserved')
  })

  it('applies updates arriving before schema confirmation only after acceptance, without echoing them', async () => {
    const connection = new FakeConnection()
    connection.joinResult.schema = schema
    const remote = new Y.Doc()
    remote.getText('content').insert(0, 'early remote update')
    const encoded = toBase64(Y.encodeStateAsUpdate(remote))
    const local = new Y.Doc()
    connection.beforeJoin = () => {
      connection.emit('ReceiveUpdate', 'wiki:home', encoded, 1)
      expect(local.getText('content').toString()).toBe('')
    }
    await create(connection, local).start()
    expect(local.getText('content').toString()).toBe('early remote update')
    expect(connection.calls('SendUpdate')).toHaveLength(0)
  })

  it.each([
    null,
    { ...schema, version: 2 },
    { ...schema, profile: 'markdown-safe' },
    { ...schema, plugins: [] },
    { ...schema, plugins: [{ id: 'charts', version: 9 }, { id: 'youtrack', version: 2 }] },
  ])('rejects incompatible schema before applying snapshots, early updates or sending local edits: %j', async received => {
    const connection = new FakeConnection()
    const remote = new Y.Doc()
    remote.getText('remote').insert(0, 'must not hydrate')
    const encoded = toBase64(Y.encodeStateAsUpdate(remote))
    connection.joinResult.snapshot = encoded
    connection.joinResult.schema = received
    connection.beforeJoin = () => connection.emit('ReceiveUpdate', 'wiki:home', encoded, 1)
    const local = new Y.Doc()
    local.getText('draft').insert(0, 'keep local work')
    const fatal = vi.fn()
    await create(connection, local, fatal).start()

    expect(fatal).toHaveBeenCalledOnce()
    expect(local.getText('remote').toString()).toBe('')
    expect(local.getText('draft').toString()).toBe('keep local work')
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.stopped).toBe(true)
  })

  it('rejects revision 1 snapshots and buffered updates for a revision 2 rich client', async () => {
    const connection = new FakeConnection()
    const remote = new Y.Doc()
    remote.getText('remote').insert(0, 'old built-in schema')
    const encoded = toBase64(Y.encodeStateAsUpdate(remote))
    connection.joinResult.snapshot = encoded
    connection.joinResult.schema = schema
    connection.beforeJoin = () => connection.emit('ReceiveUpdate', 'wiki:home', encoded, 1)
    const local = new Y.Doc()
    local.getText('draft').insert(0, 'keep revision 2 local work')
    const fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, schema: { ...schema, version: 2 }, onFatalError: fatal }, connection)
    providers.push(provider)
    await provider.start()

    expect(fatal).toHaveBeenCalledOnce()
    expect(local.getText('remote').toString()).toBe('')
    expect(local.getText('draft').toString()).toBe('keep revision 2 local work')
    expect(connection.calls('JoinDocument')).toHaveLength(0)
    expect(connection.calls('SendUpdate')).toHaveLength(0)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(connection.stopped).toBe(true)
    remote.destroy(); local.destroy()
  })

  it.each(['[SCHEMA_MISMATCH] Missing charts', "Unknown hub method 'JoinDocumentWithSchema'."])(
    'treats negotiation failure as fatal and never falls back to legacy: %s', async message => {
      const connection = new FakeConnection()
      connection.joinError = new Error(message)
      const fatal = vi.fn()
      await create(connection, new Y.Doc(), fatal).start()
      expect(fatal).toHaveBeenCalledOnce()
      expect(connection.calls('JoinDocumentWithSchema')).toHaveLength(1)
      expect(connection.calls('JoinDocument')).toHaveLength(0)
      expect(connection.stopped).toBe(true)
    },
  )
})

describe('SignalRYjsProvider single-flight completion races', () => {
  it('follows an initial seed snapshot with edits whose scheduling deadlines elapsed during its write', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), baseline = deferred<void>()
    connection.joinResult = { snapshot: null, updates: [], seq: 0, seedMarkdown: '', isSnapshotter: true, canWrite: true }
    connection.rejectUpdatesUntilInitialSnapshot = true
    connection.pushSnapshotResults.push(baseline.promise)
    connection.sendUpdateResults.push(1)
    const projection = vi.fn(), pending = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onProjectionStatus: projection, onPendingUpdates: pending, onFatalError: fatal }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor())
    try {
      const starting = provider.start()
      await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
      ydoc.getText('content').insert(0, 'edit after captured initial baseline')
      await vi.advanceTimersByTimeAsync(2500)
      expect(connection.calls('PushSnapshot')).toHaveLength(1)
      expect(connection.calls('SendUpdate')).toHaveLength(0)
      expect(pending).toHaveBeenLastCalledWith(1)
      baseline.resolve()
      await starting
      await vi.waitFor(() => expect(pending).toHaveBeenLastCalledWith(0))
      expect(connection.calls('SendUpdate')).toHaveLength(1)
      await vi.advanceTimersByTimeAsync(2500)
      await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(2))
      const finalSnapshot = connection.calls('PushSnapshot')[1]
      expect(finalSnapshot.args[1]).toBe(1)
      const durable = new Y.Doc()
      try {
        Y.applyUpdate(durable, fromBase64(finalSnapshot.args[2] as string))
        expect(durable.getText('content').toString()).toBe('edit after captured initial baseline')
      } finally { durable.destroy() }
      await vi.waitFor(() => expect(projection).toHaveBeenLastCalledWith('current'))
      expect(fatal).not.toHaveBeenCalled()
    } finally {
      baseline.resolve(); await baseline.promise
      provider.destroy(); ydoc.destroy()
    }
  })

  it('does not publish a superseded join after pending reconciliation synchronously reconnects', async () => {
    const connection = new FakeConnection(), ydoc = new Y.Doc()
    const oldAck = deferred<number>(), newestJoin = deferred<Record<string, unknown>>()
    let armed = false, reconnected = false, latestResolved = false
    const prematureReady = vi.fn(), synced = vi.fn(), status = vi.fn(), pending = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onPendingUpdates: count => {
        pending(count)
        if (armed && count === 0 && !reconnected) {
          reconnected = true
          connection.reconnect()
        }
      },
      onSynced: value => {
        synced(value)
        if (value && reconnected && !latestResolved) prematureReady('synced')
      },
      onStatus: value => {
        status(value)
        if (value === 'connected' && reconnected && !latestResolved) prematureReady('connected')
      },
      onFatalError: fatal,
    }, connection)
    providers.push(provider)
    try {
      await provider.start()
      connection.sendUpdateResults.push(oldAck.promise)
      ydoc.getText('content').insert(0, 'persisted but unacknowledged')
      await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
      const covered = { ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(ydoc)), seq: 1 }
      connection.joinResults.push(covered, newestJoin.promise)
      armed = true
      connection.reconnect()
      oldAck.resolve(1) // ignored because it belongs to the first connection generation
      await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(3))
      expect(reconnected).toBe(true)
      expect(pending).toHaveBeenLastCalledWith(0)
      expect(prematureReady).not.toHaveBeenCalled()
      expect(synced).toHaveBeenLastCalledWith(false)
      expect(status).toHaveBeenLastCalledWith('reconnecting')
      latestResolved = true
      newestJoin.resolve(covered)
      await vi.waitFor(() => expect(synced).toHaveBeenLastCalledWith(true))
      expect(status).toHaveBeenLastCalledWith('connected')
      expect(connection.calls('SendUpdate')).toHaveLength(1)
      expect(fatal).not.toHaveBeenCalled()
    } finally {
      oldAck.resolve(1)
      newestJoin.resolve(connection.joinResult)
      await Promise.all([oldAck.promise, newestJoin.promise])
      provider.destroy(); ydoc.destroy()
    }
  })

  it.each([false, true])('sends a callback edit beyond the local cap without busy retry after server full (serverFull=%s)', async serverFull => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), secondAck = deferred<number>()
    connection.sendUpdateResults.push(1, secondAck.promise)
    let armed = false, inserted = false
    const pending = vi.fn(), full = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      maxPendingBytes: 128, onPendingUpdates: pending, onDocumentFull: full, onFatalError: fatal,
      onSynced: value => {
        if (value && armed && !inserted) {
          inserted = true
          ydoc.getText('content').insert(ydoc.getText('content').length, 'x'.repeat(256))
        }
      },
    }, connection)
    providers.push(provider)
    try {
      await provider.start()
      armed = true
      ydoc.getText('content').insert(0, 'first')
      await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))
      expect(inserted).toBe(true)
      expect(full).toHaveBeenCalledOnce()
      expect(pending).toHaveBeenLastCalledWith(1)
      if (serverFull) secondAck.reject(new Error('Document size limit exceeded.'))
      else secondAck.resolve(2)
      await secondAck.promise.catch(() => {})
      await vi.waitFor(() => {
        if (serverFull) expect(full).toHaveBeenCalledTimes(2)
        else expect(pending).toHaveBeenLastCalledWith(0)
      })
      // A local cap cannot strand retained data, while a definite server rejection
      // must not turn the completion continuation into an immediate or timer retry loop.
      await vi.advanceTimersByTimeAsync(10_000)
      expect(connection.calls('SendUpdate')).toHaveLength(2)
      expect(pending).toHaveBeenLastCalledWith(serverFull ? 1 : 0)
      expect(ydoc.getText('content').toString()).toBe('first' + 'x'.repeat(256))
      expect(fatal).not.toHaveBeenCalled()
    } finally {
      secondAck.resolve(2); await secondAck.promise.catch(() => {})
      provider.destroy(); ydoc.destroy()
    }
  })

  it('merges waiting edits only up to the per-update size, so no batch outgrows the hub limit', async () => {
    const connection = new FakeConnection(), ydoc = new Y.Doc(), firstAck = deferred<number>()
    connection.sendUpdateResults.push(firstAck.promise, 2, 3)
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc, maxMergedUpdateBytes: 96 }, connection)
    providers.push(provider)
    try {
      await provider.start()
      ydoc.getText('content').insert(0, 'first')
      await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
      // Two edits behind the one in flight: together beyond the size, so each is an update of its own.
      ydoc.getText('content').insert(5, 'a'.repeat(60))
      ydoc.getText('content').insert(65, 'b'.repeat(60))
      firstAck.resolve(1)
      await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(3))
    } finally {
      firstAck.resolve(1); provider.destroy(); ydoc.destroy()
    }
  })

  it('performs a fresh join when initial back-sync acknowledges a sequence gap', async () => {
    const connection = new FakeConnection(), ydoc = new Y.Doc()
    ydoc.getText('content').insert(0, 'pre-existing draft')
    connection.sendUpdateResults.push(3)
    connection.beforeJoin = () => {
      if (connection.calls('JoinDocument').length !== 2) return
      const server = new Y.Doc()
      try {
        Y.applyUpdate(server, Y.encodeStateAsUpdate(ydoc))
        server.getText('remote').insert(0, 'missed remote updates')
        connection.joinResult = { ...connection.joinResult, snapshot: toBase64(Y.encodeStateAsUpdate(server)), seq: 3 }
      } finally { server.destroy() }
    }
    const status = vi.fn(), synced = vi.fn(), pending = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onStatus: status, onSynced: synced, onPendingUpdates: pending, onFatalError: fatal }, connection)
    providers.push(provider)
    try {
      vi.useFakeTimers()
      const starting = provider.start()
      await vi.advanceTimersByTimeAsync(3_000)
      await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
      await starting
      await vi.waitFor(() => expect(status).toHaveBeenLastCalledWith('connected'))
      expect(synced).toHaveBeenLastCalledWith(true)
      expect(pending).toHaveBeenLastCalledWith(0)
      expect(connection.calls('SendUpdate')).toHaveLength(1)
      expect(ydoc.getText('content').toString()).toBe('pre-existing draft')
      expect(ydoc.getText('remote').toString()).toBe('missed remote updates')
      expect(fatal).not.toHaveBeenCalled()
    } finally { provider.destroy(); ydoc.destroy() }
  })

  it('publishes edits queued while a snapshot outlives both scheduling deadlines without another edit', async () => {
    vi.useFakeTimers()
    const connection = new FakeConnection(), ydoc = new Y.Doc(), write = deferred<void>()
    connection.joinResult.isSnapshotter = true
    connection.pushSnapshotResults.push(write.promise)
    connection.sendUpdateResults.push(1)
    const projectionStatus = vi.fn(), fatal = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
      onProjectionStatus: projectionStatus, onFatalError: fatal }, connection)
    providers.push(provider); provider.attachEditor(seedableEditor())
    try {
      await provider.start()
      connection.emit('RequestSnapshot', 'wiki:home')
      await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
      expect(connection.calls('PushSnapshot')[0].args[1]).toBe(0)
      ydoc.getText('content').insert(0, 'edit during snapshot write')
      await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(1))
      // Both the debounce and max-wait callbacks encounter the already running snapshot.
      await vi.advanceTimersByTimeAsync(2500)
      expect(connection.calls('PushSnapshot')).toHaveLength(1)
      write.resolve()
      await write.promise
      await vi.advanceTimersByTimeAsync(2500)
      await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(2))
      const followup = connection.calls('PushSnapshot')[1]
      expect(followup.args[1]).toBe(1)
      const decoded = new Y.Doc()
      try {
        Y.applyUpdate(decoded, fromBase64(followup.args[2] as string))
        expect(decoded.getText('content').toString()).toBe('edit during snapshot write')
      } finally { decoded.destroy() }
      await vi.waitFor(() => expect(projectionStatus).toHaveBeenLastCalledWith('current'))
      expect(connection.calls('SendUpdate')).toHaveLength(1)
      expect(fatal).not.toHaveBeenCalled()
    } finally {
      write.resolve()
      await write.promise
      provider.destroy(); ydoc.destroy()
    }
  })

  it.each(['onSynced', 'onStatus'] as const)(
    'drains a synchronous edit created by %s while the previous pump completes', async callback => {
      const connection = new FakeConnection(), ydoc = new Y.Doc(), secondAck = deferred<number>()
      connection.sendUpdateResults.push(1, secondAck.promise)
      let armed = false, inserted = false, acknowledged = false
      const pending = vi.fn(), synced = vi.fn(), status = vi.fn(), fatal = vi.fn()
      const prematureSynced = vi.fn()
      const insertOnce = () => {
        if (!armed || inserted) return
        inserted = true
        ydoc.getText('content').insert(ydoc.getText('content').length, ' + observer edit')
      }
      const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc,
        onPendingUpdates: pending, onFatalError: fatal,
        onSynced: value => {
          synced(value)
          if (value && inserted && !acknowledged) prematureSynced()
          if (value && callback === 'onSynced') insertOnce()
        },
        onStatus: value => {
          status(value)
          if (value === 'connected' && callback === 'onStatus') insertOnce()
        },
      }, connection)
      providers.push(provider)
      try {
        await provider.start()
        armed = true
        ydoc.getText('content').insert(0, 'first edit')
        await vi.waitFor(() => expect(connection.calls('SendUpdate')).toHaveLength(2))
        expect(inserted).toBe(true)
        expect(pending).toHaveBeenLastCalledWith(1)
        expect(prematureSynced).not.toHaveBeenCalled()
        expect(connection.calls('PushSnapshot')).toHaveLength(0)
        acknowledged = true
        secondAck.resolve(2)
        await secondAck.promise
        await vi.waitFor(() => expect(pending).toHaveBeenLastCalledWith(0))
        await vi.waitFor(() => expect(synced).toHaveBeenLastCalledWith(true))
        expect(status).toHaveBeenLastCalledWith('connected')
        expect(ydoc.getText('content').toString()).toBe('first edit + observer edit')
        expect(connection.calls('SendUpdate')).toHaveLength(2)
        expect(fatal).not.toHaveBeenCalled()
      } finally {
        secondAck.resolve(2)
        await secondAck.promise
        provider.destroy(); ydoc.destroy()
      }
    },
  )
})

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void
  let reject!: (reason?: unknown) => void
  const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej })
  return { promise, resolve, reject }
}

function toBase64(value: Uint8Array): string {
  return btoa(String.fromCharCode(...value))
}

function fromBase64(value: string): Uint8Array {
  return Uint8Array.from(atob(value), char => char.charCodeAt(0))
}

async function sha256(value: Uint8Array): Promise<string> {
  const bytes = new Uint8Array(value.byteLength)
  bytes.set(value)
  const digest = await crypto.subtle.digest('SHA-256', bytes.buffer)
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('')
}

describe('presence reconnect', () => {
  function pair() {
    const connectionA = new FakeConnection()
    const connectionB = new FakeConnection()
    const peersA = vi.fn()
    const peersB = vi.fn()
    const statusesB: string[] = []
    const a = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'presence:test', ydoc: new Y.Doc(), onPeers: peersA }, connectionA)
    const b = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'presence:test', ydoc: new Y.Doc(),
      allowOfflineEditing: false, onPeers: peersB, onStatus: status => statusesB.push(status),
    }, connectionB)
    providers.push(a, b)
    a.awareness.setLocalState({ user: { name: 'Alice', color: '#123456' } })
    b.awareness.setLocalState({ user: { name: 'Bob', color: '#654321' } })
    let connected = true
    let packets = 0
    connectionA.onSend = (method, ...args) => {
      if (method === 'SendAwareness' && connected) {
        if (++packets > 30) throw new Error('Presence handshake did not converge')
        connectionB.emit('ReceiveAwareness', ...args)
      }
    }
    connectionB.onSend = (method, ...args) => {
      if (method === 'SendAwareness' && connected) {
        if (++packets > 30) throw new Error('Presence handshake did not converge')
        connectionA.emit('ReceiveAwareness', ...args)
      }
    }
    return { a, b, connectionA, connectionB, peersA, peersB, statusesB, packetCount: () => packets, disconnect: () => { connected = false }, reconnect: () => { connected = true; packets = 0 } }
  }

  it('publishes the roster only when it changes, not when a colleague moves the caret', async () => {
    const { a, b, peersA, packetCount } = pair()
    await a.start()
    await b.start()
    expect(peersA).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Bob' })])
    const calls = peersA.mock.calls.length
    const packets = packetCount()
    b.awareness.setLocalStateField('cursor', { anchor: 1, head: 1 })
    b.awareness.setLocalStateField('pointer', { x: 1, y: 1 })
    b.awareness.setLocalStateField('cursor', { anchor: 2, head: 2 })
    // Moves within 50 ms share one packet (the server relays 30 a second).
    await vi.waitFor(() => expect(a.awareness.getStates().get(b.awareness.clientID)).toMatchObject({ cursor: { anchor: 2 }, pointer: { x: 1 } }))
    expect(packetCount() - packets).toBeLessThanOrEqual(2)
    expect(peersA.mock.calls.length).toBe(calls)
    b.awareness.setLocalStateField('user', { name: 'Bobby', color: '#654321' })
    await vi.waitFor(() => expect(peersA).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Bobby' })]))
  })

  it('rebuilds both rosters after a lost leave without changing either user or cursor', async () => {
    vi.useFakeTimers()
    const { a, b, peersA, peersB, statusesB, packetCount, disconnect, reconnect } = pair()
    b.attachEditor({ view: { dom: document.createElement('div') } } as never)
    await a.start()
    await b.start()
    expect(peersA).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Bob' })])
    expect(peersB).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Alice' })])
    const identity = b.awareness.getLocalState()
    disconnect() // Browser is already offline: its best-effort leave cannot reach Alice.
    window.dispatchEvent(new Event('offline'))
    expect(peersB).toHaveBeenLastCalledWith([])
    reconnect()
    window.dispatchEvent(new Event('online'))
    await vi.waitFor(() => expect(statusesB.at(-1)).toBe('connected'))
    expect(b.awareness.getLocalState()).toEqual(identity)
    expect(packetCount()).toBeLessThan(20)
    expect(peersA).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Bob' })])
    expect(peersB).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Alice' })])
  })

  it('forgets cached peers on disconnect and republishes a fresh unchanged awareness update', async () => {
    vi.useFakeTimers()
    const { a, b, connectionB, peersB, disconnect, reconnect, statusesB } = pair()
    await a.start()
    await b.start()
    expect(peersB).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Alice' })])
    disconnect()
    connectionB.close()
    expect(peersB).toHaveBeenLastCalledWith([])
    expect(b.awareness.getStates().has(a.awareness.clientID)).toBe(false)
    // Rejoin while the relay is silent: no stale roster may be exposed from the cache.
    connectionB.reconnect()
    await vi.waitFor(() => expect(statusesB.at(-1)).toBe('connected'))
    expect(peersB).toHaveBeenLastCalledWith([])
    reconnect()
    a.awareness.setLocalState(a.awareness.getLocalState())
    expect(peersB).toHaveBeenLastCalledWith([expect.objectContaining({ name: 'Alice' })])
  })
})

describe('merge-preserved provider behavior', () => {
  it('preserves a snapshotter handoff received while the join response is settling', async () => {
    const connection = new FakeConnection()
    connection.beforeJoin = () => {
      connection.emit('SnapshotterChanged', 'wiki:home', true)
      connection.emit('RequestSnapshot', 'wiki:home')
    }
    const provider = createProvider(connection, new Y.Doc())
    provider.attachEditor(seedableEditor())
    await provider.start()
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1), { timeout: 3_000 })
  })

  it('locks immediately offline and rechecks permissions on a fresh browser-online join', async () => {
    const connection = new FakeConnection(), synced = vi.fn(), writable = vi.fn(), status = vi.fn()
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
      allowOfflineEditing: false, onSynced: synced, onWritable: writable, onStatus: status }, connection)
    providers.push(provider)
    provider.attachEditor({ view: { dom: document.createElement('div') } } as never)
    await provider.start()
    window.dispatchEvent(new Event('offline'))
    expect(synced).toHaveBeenLastCalledWith(false)
    expect(status).toHaveBeenLastCalledWith('offline')
    connection.joinResult.canWrite = false
    window.dispatchEvent(new Event('online'))
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(2))
    await vi.waitFor(() => expect(status).toHaveBeenLastCalledWith('connected'))
    expect(writable).toHaveBeenLastCalledWith(false)
  })
})
  it('stops a transport whose start finishes after an offline-online cycle before rejoining', async () => {
    const connection = new FakeConnection()
    const firstStart = deferred<void>()
    connection.startResults.push(firstStart.promise, undefined)
    const writable: boolean[] = []
    const provider = new SignalRYjsProvider({
      hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
      allowOfflineEditing: false, onWritable: value => writable.push(value),
    }, connection)
    providers.push(provider)
    provider.attachEditor({ view: { dom: document.createElement('div') } } as never)

    const starting = provider.start()
    await vi.waitFor(() => expect(connection.startCalls).toBe(1))
    window.dispatchEvent(new Event('offline'))
    await vi.waitFor(() => expect(connection.stopCalls).toBe(1))
    window.dispatchEvent(new Event('online'))
    firstStart.resolve()
    await starting

    await vi.waitFor(() => expect(connection.startCalls).toBe(2))
    await vi.waitFor(() => expect(connection.calls('JoinDocument')).toHaveLength(1))
    expect(connection.stopCalls).toBeGreaterThanOrEqual(2)
    expect(writable).toEqual([true])
  })


it.each([undefined, true])('preserves low-level offline editing when allowOfflineEditing=%s', async allowOfflineEditing => {
  const connection = new FakeConnection(), status = vi.fn()
  const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: new Y.Doc(),
    allowOfflineEditing, onStatus: status }, connection)
  providers.push(provider)
  provider.attachEditor({ view: { dom: document.createElement('div') } } as never)
  await provider.start()
  window.dispatchEvent(new Event('offline'))
  expect(connection.stopCalls).toBe(0)
  expect(status).toHaveBeenLastCalledWith('connected')
})

// 3.6 perf: every open pushed the whole document's snapshot (~40 KB) although the server's snapshot already held it.
describe('SignalRYjsProvider snapshot after a join', () => {
  /** A server whose stored snapshot is `server` (and `after` updates past it), this client its snapshotter. */
  function snapshotter(server: Y.Doc, after: Uint8Array[] = [], local = new Y.Doc()) {
    const connection = new FakeConnection(), projection = vi.fn()
    connection.joinResult = { snapshot: toBase64(Y.encodeStateAsUpdate(server)), updates: after.map(toBase64), seq: after.length,
      seedMarkdown: null, isSnapshotter: true, canWrite: true }
    const provider = new SignalRYjsProvider({ hubUrl: '/hubs/markdown', documentId: 'wiki:home', ydoc: local, onProjectionStatus: projection }, connection)
    providers.push(provider)
    provider.attachEditor(Object.assign(seedableEditor(), { view: { dom: document.createElement('div') } }))
    return { connection, provider, local, projection }
  }
  const page = () => { const doc = new Y.Doc(); doc.getText('content').insert(0, 'Stránka'); return doc }
  const pushedText = (connection: FakeConnection) => {
    const pushed = new Y.Doc()
    Y.applyUpdate(pushed, fromBase64(connection.calls('PushSnapshot').at(-1)!.args[2] as string))
    return pushed.getText('content').toString()
  }

  it('sends none when the server already holds this state, and reports the projection current', async () => {
    vi.useFakeTimers()
    const { connection, provider, local, projection } = snapshotter(page())
    await provider.start()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.calls('PushSnapshot')).toHaveLength(0)
    expect(projection).toHaveBeenLastCalledWith('current')
    expect(local.getText('content').toString()).toBe('Stránka')
  })

  it('sends one when updates follow the server\'s snapshot', async () => {
    vi.useFakeTimers()
    const server = page(), later = new Y.Doc()
    Y.applyUpdate(later, Y.encodeStateAsUpdate(server))
    const before = Y.encodeStateVector(later)
    later.getText('content').insert(7, ' a dál')
    const { connection, provider } = snapshotter(server, [Y.encodeStateAsUpdate(later, before)])
    await provider.start()
    await vi.advanceTimersByTimeAsync(10_000)
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(pushedText(connection)).toBe('Stránka a dál')
  })

  it('sends one for another writer\'s change that arrives after the join, and that change is in it', async () => {
    vi.useFakeTimers()
    const server = page(), other = new Y.Doc()
    Y.applyUpdate(other, Y.encodeStateAsUpdate(server))
    const { connection, provider, local } = snapshotter(server)
    await provider.start()
    const before = Y.encodeStateVector(other)
    other.getText('content').insert(7, ' kolegy')
    connection.emit('ReceiveUpdate', 'wiki:home', toBase64(Y.encodeStateAsUpdate(other, before)), 1)
    await vi.advanceTimersByTimeAsync(10_000)
    expect(local.getText('content').toString()).toBe('Stránka kolegy')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(pushedText(connection)).toBe('Stránka kolegy')
  })

  it('sends offline edits recovered into the document before the join, and snapshots them', async () => {
    vi.useFakeTimers()
    const server = page(), local = new Y.Doc()
    Y.applyUpdate(local, Y.encodeStateAsUpdate(server))
    local.getText('content').insert(7, ' offline')
    const { connection, provider } = snapshotter(server, [], local)
    await provider.start()
    await vi.advanceTimersByTimeAsync(10_000)
    expect(connection.calls('SendUpdate').length).toBeGreaterThan(0)
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
    expect(pushedText(connection)).toBe('Stránka offline')
  })

  it('sends one when the server asks for it, whatever the join found', async () => {
    vi.useFakeTimers()
    const { connection, provider } = snapshotter(page())
    await provider.start()
    await vi.advanceTimersByTimeAsync(10_000)
    connection.emit('RequestSnapshot', 'wiki:home')
    await vi.waitFor(() => expect(connection.calls('PushSnapshot')).toHaveLength(1))
  })
})