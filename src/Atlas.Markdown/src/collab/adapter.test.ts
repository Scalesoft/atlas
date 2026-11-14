// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick } from 'vue'
import * as signalr from '../collaboration/signalr'
import { createMarkdownEditorController, type MarkdownEditorController } from '../core'
import MarkdownEditor from '../vue/MarkdownEditor.vue'
import * as Y from 'yjs'

// Hydrating a rich collaborative editor takes seconds on a loaded machine (the full parallel suite on CI). The waits are
// readiness conditions; their deadline, like this file's test timeout, only guards against a hang.
vi.setConfig({ testTimeout: 30_000 })
const waitFor = <T>(callback: () => T | Promise<T>) => vi.waitFor(callback, { timeout: 20_000, interval: 20 })
const transport = vi.hoisted(() => ({ current: null as any, builds: 0 }))
vi.mock('@microsoft/signalr', async importOriginal => {
  const original = await importOriginal<typeof import('@microsoft/signalr')>()
  return { ...original, HubConnectionBuilder: class {
    withUrl() { return this }
    withAutomaticReconnect() { return this }
    withKeepAliveInterval() { return this }
    withServerTimeout() { return this }
    configureLogging() { return this }
    build() { transport.builds++; return transport.current }
  } }
})

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail })
  return { promise, resolve, reject }
}
function connection(holdUpdates = false) {
  const join = deferred<unknown>()
  const snapshot = deferred<void>()
  const updateAck = deferred<number>()
  const handlers = new Map<string, (...args: any[]) => void>()
  let close = () => {}
  let reconnecting = () => {}
  let reconnect = () => {}
  let seq = 0
  let heldUpdate = false
  const updates = new Map<string, number>()
  const snapshotWrites: unknown[] = []
  const joinCalls: Array<{ method: string; args: unknown[] }> = []
  const server = new Y.Doc()
  cleanups.push(() => server.destroy())
  const hub = {
    start: async () => {}, stop: async () => {}, send: async () => {},
    on: (name: string, handler: (...args: any[]) => void) => { handlers.set(name, handler) },
    onclose: (handler: () => void) => { close = handler },
    onreconnecting: (handler: () => void) => { reconnecting = handler }, onreconnected: (handler: () => void) => { reconnect = handler },
    invoke: async (method: string, ...args: any[]) => {
      if (method === 'JoinDocumentWithSchema') {
        joinCalls.push({ method, args })
        const initial = joinCalls.length === 1
        return join.promise.then(result => initial ? result : {
          ...(result as Record<string, unknown>), snapshot: btoa(String.fromCharCode(...Y.encodeStateAsUpdate(server))),
          updates: [], seq, seedMarkdown: null, isSnapshotter: false,
        })
      }
      if (method === 'JoinDocument') throw new Error('[SCHEMA_MISMATCH] A controller adapter must use versioned negotiation.')
      if (method === 'PushSnapshot') {
        snapshotWrites.push(args)
        Y.applyUpdate(server, Uint8Array.from(atob(args[2]), c => c.charCodeAt(0)))
        return snapshot.promise
      }
      if (method === 'SendUpdate') {
        // The server durably commits before acknowledging and retries preserve update ids.
        if (!updates.has(args[1])) {
          Y.applyUpdate(server, Uint8Array.from(atob(args[2]), c => c.charCodeAt(0)))
          updates.set(args[1], ++seq)
        }
        if (holdUpdates && !heldUpdate) { heldUpdate = true; return updateAck.promise }
        return updates.get(args[1])
      }
    },
  }
  transport.current = hub
  transport.builds = 0
  return { join, joinCalls, snapshot, snapshotWrites, updateAck, server, sent: () => updates.size, offline: () => close(), reconnect: () => reconnect(),
    interrupt: () => { reconnecting(); updateAck.reject(new Error('Connection lost before the update acknowledgement.')) },
    emit: (name: string, ...args: any[]) => handlers.get(name)?.(...args) }
}
const controllers: MarkdownEditorController[] = []
const cleanups: (() => void)[] = []
afterEach(() => {
  cleanups.splice(0).forEach(cleanup => cleanup())
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.useRealTimers()
})
function adapter(allowOfflineEditing = false) {
  expect(signalr.createSignalRMarkdownAdapter).toBeTypeOf('function')
  return signalr.createSignalRMarkdownAdapter({ hubUrl: 'http://localhost/hubs/markdown', documentId: 'wiki:test', presence: { id: 'alice', name: 'Alice' }, allowOfflineEditing })
}
function controller(handle = adapter()) {
  const result = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter: handle } })
  controllers.push(result)
  return result
}
function attach(editor: MarkdownEditorController) {
  const host = document.createElement('div')
  document.body.append(host)
  editor.attachView(host, { ariaLabel: 'Live document' })
  return host
}
const seed = { snapshot: null, updates: [], seq: 0, seedMarkdown: '# Shared seed', isSnapshotter: true, canWrite: true,
  schema: { version: 7, profile: 'rich', plugins: [] } }

describe('optional collaboration adapter public contract', () => {
  it.each(['rich', 'markdown-safe'] as const)('passes the controller %s schema through the public adapter before hydration', async profile => {
    const hub = connection()
    const editor = createMarkdownEditorController({ profile, source: { kind: 'collaborative', adapter: adapter() } })
    controllers.push(editor)
    attach(editor)
    await waitFor(() => expect(hub.joinCalls).toHaveLength(1))
    expect(hub.joinCalls[0]).toEqual({ method: 'JoinDocumentWithSchema', args: ['wiki:test', editor.schema] })
    expect(editor.schema.version).toBe(profile === 'rich' ? 7 : 1)
    hub.join.resolve({ ...seed, schema: editor.schema })
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    expect(editor.getProjection().markdown).toBe('# Shared seed')
  })

  it.each([
    { version: 1, profile: 'rich', plugins: [] },
    { version: 1, profile: 'markdown-safe', plugins: [] },
    { version: 2, profile: 'rich', plugins: [] },
    { version: 7, profile: 'rich', plugins: [{ id: 'charts', version: 2 }] },
  ])('surfaces incompatible server schema as a terminal noneditable error: %j', async schema => {
    const hub = connection()
    const editor = controller()
    const host = attach(editor)
    hub.join.resolve({ ...seed, schema })
    await waitFor(() => expect(editor.state.lifecycle).toBe('failed'))
    expect(editor.state.error).toMatchObject({ kind: 'collaboration', recoverable: false })
    expect(editor.state.editable).toBe(false)
    editor.setEditable(true)
    expect(editor.state.editable).toBe(false)
    expect(host.textContent).not.toContain('Shared seed')
    expect(hub.snapshotWrites).toHaveLength(0)
  })

  it('builds from the server state at once, takes input before the join and merges the join without duplicating', async () => {
    const first = connection()
    const seeder = controller()
    attach(seeder)
    first.join.resolve(seed)
    first.snapshot.resolve()
    await waitFor(() => expect(seeder.state.connection).toBe('online'))
    const state = Y.encodeStateAsUpdate(first.server)
    const hub = connection()
    const editor = controller(signalr.createSignalRMarkdownAdapter({ hubUrl: 'http://localhost/hubs/markdown', documentId: 'wiki:test',
      presence: { id: 'bob', name: 'Bob' }, initialState: state, initialWritable: true }))
    const host = attach(editor)
    // Before any join: the document is there and takes input.
    expect(host.textContent).toContain('Shared seed')
    expect(editor.state).toMatchObject({ lifecycle: 'ready', editable: true })
    ;(host.querySelector('.ProseMirror') as any).editor.commands.insertContentAt(1, 'Early ')
    Y.applyUpdate(hub.server, state)
    hub.join.resolve({ snapshot: btoa(String.fromCharCode(...state)), updates: [], seq: 0, seedMarkdown: null, isSnapshotter: false,
      canWrite: true, schema: editor.schema })
    await waitFor(() => expect(editor.state.connection).toBe('online'))
    expect(editor.getProjection().markdown).toBe('# Early Shared seed')
    // The early edit reached the server on top of its own state.
    await waitFor(() => expect(Y.encodeStateVector(hub.server)).not.toEqual(Y.encodeStateVector(first.server)))
  })

  it('joins a bundled state the server holds as is: nothing applied or sent back, a later edit still goes out', async () => {
    const first = connection()
    const seeder = controller()
    attach(seeder)
    first.join.resolve(seed)
    first.snapshot.resolve()
    await waitFor(() => expect(seeder.state.connection).toBe('online'))
    const state = Y.encodeStateAsUpdate(first.server)
    const hub = connection()
    const editor = controller(signalr.createSignalRMarkdownAdapter({ hubUrl: 'http://localhost/hubs/markdown', documentId: 'wiki:test',
      presence: { id: 'bob', name: 'Bob' }, initialState: state, initialWritable: true }))
    const host = attach(editor)
    Y.applyUpdate(hub.server, state)
    hub.join.resolve({ snapshot: btoa(String.fromCharCode(...state)), updates: [], seq: 0, seedMarkdown: null, isSnapshotter: false,
      canWrite: true, schema: editor.schema })
    await waitFor(() => expect(editor.state.connection).toBe('online'))
    expect(editor.getProjection().markdown).toBe('# Shared seed')
    expect(hub.sent()).toBe(0)
    ;(host.querySelector('.ProseMirror') as any).editor.commands.insertContentAt(1, 'Late ')
    await waitFor(() => expect(hub.sent()).toBe(1))
    expect(hub.server.getXmlFragment('default').toString()).toContain('Late ')
  })

  it('rejects competing source values before claiming an adapter', () => {
    connection()
    const handle = adapter()
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter: handle, value: 'competing' } } as never)).toThrow(/source|value/i)
    expect(() => controller(handle)).not.toThrow()
    expect(transport.builds).toBe(0)
  })

  it('claims one session without network and never exposes local replacement', () => {
    connection()
    const handle = adapter()
    const editor = controller(handle)
    expect('setValue' in editor).toBe(false)
    expect(transport.builds).toBe(0)
    expect(() => controller(handle)).toThrow(/already|once|used/i)
    editor.destroy()
    expect(() => controller(handle)).toThrow(/already|once|used/i)
  })

  it('waits for durable seed hydration, keeps explicitly enabled offline edits and stops callbacks on destroy', async () => {
    const hub = connection()
    const editor = controller(adapter(true))
    const host = attach(editor)
    expect(editor.state.editable).toBe(false)
    expect(editor.state.lifecycle).toBe('starting')
    hub.join.resolve(seed)
    await waitFor(() => expect(host.textContent).toContain('Shared seed'))
    expect(editor.state.lifecycle).toBe('starting')
    expect(editor.dispatch({ type: 'set-paragraph' })).toBe(false)
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    expect(editor.state.documentSave).toBe('saved')
    const revision = editor.state.contentRevision
    hub.offline()
    expect(editor.state.connection).toBe('offline')
    expect(editor.state.contentRevision).toBe(revision)
    expect(editor.state.editable).toBe(true)
    expect(editor.dispatch({ type: 'set-paragraph' })).toBe(true)
    expect(editor.getProjection().markdown).toBe('Shared seed')
    expect(editor.state.documentSave).toBe('pending')
    expect(editor.state.pendingChanges).toBeGreaterThan(0)
    expect(editor.state.projectionSave).toBe('pending')
    hub.emit('FlushDegraded', 'wiki:test', true)
    expect(editor.state.projectionSave).toBe('degraded')
    expect(editor.getProjection().markdown).toBe('Shared seed')
    let callbacks = 0
    editor.subscribe(() => callbacks++)
    editor.destroy()
    const before = callbacks
    hub.emit('FlushDegraded', 'wiki:test', false)
    expect(callbacks).toBe(before)
  })

  it('keeps editing through a transport reconnect and sends the edits made meanwhile after the rejoin (COL2-7)', async () => {
    const hub = connection(true), editor = controller(), host = attach(editor)
    hub.join.resolve(seed); hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.editable).toBe(true))
    expect(editor.dispatch({ type: 'set-paragraph' })).toBe(true)
    expect(editor.state.pendingChanges).toBeGreaterThan(0)
    hub.interrupt()
    expect(editor.state.connection).toBe('reconnecting')
    // Locking the editor here dropped the keys typed during every rejoin.
    expect(editor.state.editable).toBe(true)
    expect(host.querySelector('.ProseMirror')?.getAttribute('contenteditable')).toBe('true')
    expect(editor.dispatch({ type: 'set-alignment', alignment: 'center' })).toBe(true)
    const draft = editor.getValue()
    expect(editor.state.pendingChanges).toBeGreaterThan(0)
    // SignalR rejects pending invocations when transport is lost. Its new connection is
    // established in a later task; the persisted update is then reconciled during rejoin.
    await new Promise(resolve => setTimeout(resolve, 0))
    hub.reconnect()
    await waitFor(() => expect(editor.state.pendingChanges).toBe(0))
    expect(editor.state.documentSave).toBe('saved')
    expect(editor.state.editable).toBe(true)
    expect(editor.getValue()).toEqual(draft)
    expect(hub.server.getXmlFragment('default').toString()).toContain('textAlign="center"')
    expect(editor.dispatch({ type: 'undo' })).toBe(true)
  })

  it('pauses edits once the connection is closed for good and keeps the unacknowledged ones', async () => {
    const hub = connection(true), editor = controller(), host = attach(editor)
    hub.join.resolve(seed); hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.editable).toBe(true))
    expect(editor.dispatch({ type: 'set-paragraph' })).toBe(true)
    const draft = editor.getValue()
    hub.offline()
    expect(editor.state.connection).toBe('offline')
    expect(editor.state.editable).toBe(false)
    expect(host.querySelector('.ProseMirror')?.getAttribute('contenteditable')).toBe('false')
    expect(editor.dispatch({ type: 'set-alignment', alignment: 'center' })).toBe(false)
    expect(editor.state.pendingChanges).toBeGreaterThan(0)
    expect(editor.getValue()).toEqual(draft)
  })

  it('never overrides host readonly preference after a disconnected session rejoins', async () => {
    const hub = connection(), editor = controller()
    attach(editor); hub.join.resolve(seed); hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.editable).toBe(true))
    hub.offline(); editor.setEditable(false); hub.reconnect()
    await waitFor(() => expect(editor.state.connection).toBe('online'))
    expect(editor.state.editable).toBe(false)
    editor.setEditable(true)
    expect(editor.state.editable).toBe(true)
  })

  it('Vue emits created once after hydration and changes without model updates', async () => {
    const hub = connection()
    const handle = adapter()
    const created: MarkdownEditorController[] = []
    const changes: unknown[] = []
    const origins: unknown[] = []
    const models: unknown[] = []
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(MarkdownEditor, {
      collaboration: handle, profile: 'rich', ariaLabel: 'Realtime', variant: 'compact',
      onCreated: (value: MarkdownEditorController) => created.push(value),
      onChange: (value: unknown, metadata: unknown) => { changes.push(value); origins.push(metadata) },
      'onUpdate:modelValue': (value: unknown) => models.push(value),
    }) })
    app.mount(host)
    cleanups.push(() => app.unmount())
    hub.join.resolve(seed)
    await waitFor(() => expect(host.textContent).toContain('Shared seed'))
    expect(created).toHaveLength(0)
    expect(changes).toHaveLength(0)
    hub.snapshot.resolve()
    await waitFor(() => expect(created).toHaveLength(1))
    created[0].dispatch({ type: 'set-alignment', alignment: 'center' })
    await nextTick()
    expect(changes).toHaveLength(1)
    expect(models).toHaveLength(0)
    expect(origins).toEqual([{ origin: 'local' }])
    const vector = Y.encodeStateVector(hub.server)
    const paragraph = hub.server.getXmlFragment('default').get(0) as Y.XmlElement
    ;(paragraph.get(0) as Y.XmlText).insert(0, 'Remote ')
    hub.emit('ReceiveUpdate', 'wiki:test', btoa(String.fromCharCode(...Y.encodeStateAsUpdate(hub.server, vector))), 2)
    await nextTick()
    expect(host.textContent).toContain('Remote Shared seed')
    expect(origins.at(-1)).toEqual({ origin: 'remote' })
    expect(created[0].dispatch({ type: 'undo' })).toBe(true)
    await nextTick()
    expect(created[0].getProjection().markdown).toBe('# Remote Shared seed')
    expect(origins.at(-1)).toEqual({ origin: 'local' })
    hub.offline()
    expect(created).toHaveLength(1)
  })

  it('treats authorization rejection as terminal instead of editable or endlessly starting', async () => {
    const hub = connection()
    const editor = controller()
    attach(editor)
    hub.join.reject(new Error('Unauthorized'))
    await waitFor(() => expect(editor.state.lifecycle).toBe('failed'))
    expect(editor.state.error).toMatchObject({ kind: 'collaboration', recoverable: false })
    expect(editor.state.editable).toBe(false)
    editor.setEditable(true)
    expect(editor.state.editable).toBe(false)
  })

  it('hydrates a read-only session but never enables editing', async () => {
    const hub = connection()
    const editor = controller()
    attach(editor)
    const snapshot = btoa(String.fromCharCode(...Y.encodeStateAsUpdate(hub.server)))
    hub.join.resolve({ ...seed, snapshot, seedMarkdown: null, isSnapshotter: false, canWrite: false })

    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))

    expect(editor.state.connection).toBe('online')
    expect(editor.state.editable).toBe(false)
    editor.setEditable(true)
    expect(editor.state.editable).toBe(false)
    expect(editor.dispatch({ type: 'toggle-bold' })).toBe(false)
  })

  it('also treats an authorization rejection of the seed snapshot as terminal', async () => {
    const hub = connection()
    const editor = controller()
    const host = attach(editor)
    hub.join.resolve(seed)
    await waitFor(() => expect(host.textContent).toContain('Shared seed'))
    // The rejection needs a consumer: without it the promise is rejected before the adapter awaits it (unhandled rejection under load).
    await waitFor(() => expect(hub.snapshotWrites).toHaveLength(1))
    hub.snapshot.reject(new Error('Forbidden'))
    await waitFor(() => expect(editor.state.lifecycle).toBe('failed'))
    expect(editor.getProjection().markdown).toBe('# Shared seed')
    expect(editor.state.editable).toBe(false)
  })

  it('reattaches the same hydrated session without replacing content or reconnecting', async () => {
    const hub = connection()
    const editor = controller()
    const first = document.createElement('div')
    const detach = editor.attachView(first, { ariaLabel: 'First' })
    hub.join.resolve(seed)
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    detach()
    expect(editor.state.lifecycle).toBe('idle')
    const next = attach(editor)
    expect(next.textContent).toContain('Shared seed')
    expect(editor.state.lifecycle).toBe('ready')
    expect(transport.builds).toBe(1)
  })

  it('does not put authoritative seed content into the local undo history', async () => {
    const hub = connection()
    const editor = controller()
    attach(editor)
    hub.join.resolve(seed)
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    expect(editor.query({ type: 'undo' }).enabled).toBe(false)
    expect(editor.dispatch({ type: 'undo' })).toBe(false)
    expect(editor.getProjection().markdown).toBe('# Shared seed')
  })

  it('preserves the last valid document when the server sends corrupt update bytes', async () => {
    const hub = connection()
    const editor = controller()
    attach(editor)
    hub.join.resolve(seed)
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    hub.emit('ReceiveUpdate', 'wiki:test', 'not base64!', 1)
    expect(editor.state.lifecycle).toBe('failed')
    expect(editor.state.editable).toBe(false)
    expect(editor.getProjection().markdown).toBe('# Shared seed')
  })

  it('never resumes snapshot work when an outstanding hydration completes after destroy', async () => {
    vi.useFakeTimers()
    const hub = connection()
    const editor = controller()
    attach(editor)
    await Promise.resolve()
    editor.destroy()
    hub.join.resolve({ ...seed, seedMarkdown: null, snapshot: btoa(String.fromCharCode(...Y.encodeStateAsUpdate(hub.server))) })
    hub.snapshot.resolve()
    await vi.advanceTimersByTimeAsync(2500)
    expect(hub.snapshotWrites).toHaveLength(0)
    expect(editor.state.lifecycle).toBe('destroyed')
  })

  it('suspends projections while detached and resumes from the current collaborative document', async () => {
    const hub = connection()
    const editor = controller()
    const first = document.createElement('div')
    const detach = editor.attachView(first, { ariaLabel: 'First view' })
    hub.join.resolve(seed)
    hub.snapshot.resolve()
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    detach()
    const writes = hub.snapshotWrites.length
    const revision = editor.state.contentRevision
    const vector = Y.encodeStateVector(hub.server)
    const heading = hub.server.getXmlFragment('default').get(0) as Y.XmlElement
    ;(heading.get(0) as Y.XmlText).insert(0, 'Remote ')
    hub.emit('ReceiveUpdate', 'wiki:test', btoa(String.fromCharCode(...Y.encodeStateAsUpdate(hub.server, vector))), 1)
    hub.emit('RequestSnapshot', 'wiki:test')
    await Promise.resolve()
    await Promise.resolve()
    expect(() => editor.getValue()).toThrow(/attach|view/i)
    expect(() => editor.getProjection()).toThrow(/attach|view/i)
    expect(hub.snapshotWrites).toHaveLength(writes)
    const next = attach(editor)
    expect(next.textContent).toContain('Remote Shared seed')
    expect(editor.state.contentRevision).toBeGreaterThan(revision)
    expect(editor.state.changeOrigin).toBe('remote')
    hub.emit('RequestSnapshot', 'wiki:test')
    await waitFor(() => expect(hub.snapshotWrites.length).toBeGreaterThan(writes))
    expect((hub.snapshotWrites.at(-1) as any[])[3].markdown).toBe('# Remote Shared seed')
  })

  it('retains an authoritative seed received before the initial view is reattached', async () => {
    const hub = connection()
    const editor = controller()
    const first = document.createElement('div')
    const detach = editor.attachView(first, { ariaLabel: 'Starting view' })
    detach()
    hub.join.resolve(seed)
    hub.snapshot.resolve()
    // Let the pending Join promise settle fully while there is no rendering view.
    await new Promise(resolve => setTimeout(resolve, 0))
    const next = attach(editor)
    await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
    expect(next.textContent).toContain('Shared seed')
    expect((hub.snapshotWrites.at(-1) as any[])[3].markdown).toBe('# Shared seed')
  })
})

it('locks the default adapter immediately on browser offline and rejoins on online', async () => {
  const hub = connection()
  const handle = signalr.createSignalRMarkdownAdapter({ hubUrl: 'http://localhost/hubs/markdown', documentId: 'wiki:test',
    presence: { id: 'alice', name: 'Alice' } })
  const editor = controller(handle), host = attach(editor)
  hub.join.resolve({ ...seed, schema: editor.schema }); hub.snapshot.resolve()
  await waitFor(() => expect(editor.state.lifecycle).toBe('ready'))
  expect(editor.state.editable).toBe(true)
  window.dispatchEvent(new Event('offline'))
  expect(editor.state.editable).toBe(false)
  expect(host.querySelector('.tiptap')?.getAttribute('contenteditable')).toBe('false')
  window.dispatchEvent(new Event('online'))
  await waitFor(() => expect(hub.joinCalls).toHaveLength(2))
  await waitFor(() => expect(editor.state.editable).toBe(true))
})