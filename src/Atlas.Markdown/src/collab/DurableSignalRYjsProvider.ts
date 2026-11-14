import * as Y from 'yjs'
import { Awareness } from 'y-protocols/awareness'
import type { Editor } from '@tiptap/core'
import type { MarkdownDocumentSchema } from '../core/types'
import { RICH_FORMAT } from '../core/types'
import { validateValue } from '../engine/valueValidation'
import { clonePlainData } from '../core/plainData'
import type { SignalRYjsProviderOptions } from './SignalRYjsProvider'
import { confirmDocumentSchema, normalizeDocumentSchema } from './documentSchema'
import { createSignalRConnection, isAuthorizationError, type MarkdownSyncConnection } from './provider/transport'
import { PresenceSession } from './provider/presence'
import { b64ToU8, u8ToB64, ProtocolError, validateServerSequence } from './provider/protocol'
import { hasMissingUpdateContent } from './provider/updateCoverage'
import { normalizeRecoveryScope } from './recovery/indexedDbRecoveryStore'
import { decodeRecoveryDocument } from './recovery/decodeDocument'
import { cloneJournal, journalSha256, type DurableJournalStore, type JournalRecord, type JournalUpdate } from './recovery/durableJournal'
import type { RecoveryScope } from './recovery/types'
import { createCanonicalUpdateCollector } from './recovery/canonicalUpdates'
import { joinRetryDelay } from './provider/retryPolicy'

export interface DurableCollaborationOptions {
  readonly store: DurableJournalStore
  readonly scope: RecoveryScope
  /** Explicit selected branch to resume; omission creates a separate branch for this tab. */
  readonly branchId?: string
  readonly onJournal?: (status: { branchId: string; state: 'writing' | 'durable' | 'failed'; pending: number }) => void
}
export interface DurableSignalRYjsProviderOptions extends Omit<SignalRYjsProviderOptions, 'recovery' | 'schema'> {
  schema: MarkdownDocumentSchema
  durable: DurableCollaborationOptions
  /** Continue a previously authorized, journaled session during a transport outage. */
  allowOfflineEditing?: boolean
}
interface Receipt { protocolVersion: 2; epoch: string; updateId: string; sha256: string; sequence: number; persistedSequence: number }
interface RemoteUpdate extends Receipt { update: string }
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
function receipt(raw: unknown, expected: { epoch: string; updateId: string; sha256: string }): Receipt {
  const value = clonePlainData(raw) as Receipt
  if (!value || value.protocolVersion !== 2 || value.epoch !== expected.epoch || value.updateId !== expected.updateId || value.sha256 !== expected.sha256) throw new ProtocolError('Mismatched durable receipt.')
  validateServerSequence(value.sequence); validateServerSequence(value.persistedSequence)
  if (value.persistedSequence < value.sequence) throw new ProtocolError('Receipt does not confirm durable storage.')
  return value
}

/** Explicit V2 only. No V1 fallback, client snapshot upload, receipt eviction or ID renaming. */
export class DurableSignalRYjsProvider {
  readonly awareness: Awareness
  private readonly opts: DurableSignalRYjsProviderOptions
  private readonly conn: MarkdownSyncConnection
  private readonly presence: PresenceSession
  private readonly owner = crypto.randomUUID()
  private readonly branch: string
  private readonly collector: ReturnType<typeof createCanonicalUpdateCollector>
  private editor: Editor | null = null
  private editorSchema: Editor['schema'] | null = null
  private record: JournalRecord | null = null
  private tail: Promise<void> = Promise.resolve()
  private pumpingGeneration: number | null = null
  private started = false
  private destroyed = false
  private failed = false
  private connected = false
  private joined = false
  private canWrite = false
  private generation = 0
  private volatile = 0
  private readonly capturedUpdates = new Set<Uint8Array>()
  private applying = false
  private retry: ReturnType<typeof setTimeout> | undefined
  private retries = 0
  private buffered: RemoteUpdate[] = []
  private bufferBytes = 0
  private maxUpdateBytes = 1024 * 1024

  constructor(options: DurableSignalRYjsProviderOptions, connection?: MarkdownSyncConnection) {
    const scope = normalizeRecoveryScope(options.durable.scope)
    if (scope.documentId !== options.documentId || (options.durable.branchId && !uuid.test(options.durable.branchId))) throw new TypeError('Invalid durable collaboration binding.')
    this.opts = { ...options, schema: normalizeDocumentSchema(options.schema), durable: { ...options.durable, scope } }
    this.branch = options.durable.branchId?.toLowerCase() ?? crypto.randomUUID()
    this.conn = connection ?? createSignalRConnection(options.hubUrl, options.accessToken)
    this.awareness = new Awareness(options.ydoc)
    this.presence = new PresenceSession(this.awareness, { connection: this.conn, documentId: options.documentId, ownStateOnly: true,
      isConnected: () => this.connected, isJoined: () => this.joined,
      onPeers: peers => this.notify(() => this.opts.onPeers?.(peers)) })
    this.collector = createCanonicalUpdateCollector({ doc: options.ydoc,
      isRemoteOrigin: origin => origin === this || this.applying,
      onBatch: batch => this.capture(batch.update, batch.snapshot), onError: error => this.fail(error) })
    this.conn.on('ReceiveUpdateV2', (docId: string, value: unknown) => {
      if (docId !== this.opts.documentId || this.destroyed || this.failed) return
      try {
        const update = clonePlainData(value) as RemoteUpdate
        if (typeof update.update !== 'string') throw new ProtocolError('Missing update bytes.')
        if (!this.joined) {
          this.bufferBytes += update.update.length
          if (this.buffered.length >= 128 || this.bufferBytes > 8 * 1024 * 1024) throw new ProtocolError('Pre-join update buffer exceeded.')
          this.buffered.push(update)
        } else this.receive(update)
      } catch (error) { this.fail(error) }
    })
    this.conn.on('ReceiveAwareness', (docId: string, payload: string) => {
      if (docId === this.opts.documentId && this.joined) try { this.presence.receive(payload) } catch { /* advisory */ }
    })
    this.conn.on('DocumentAccessChanged', (docId: string, read: boolean, write: boolean) => {
      if (docId !== this.opts.documentId || this.destroyed) return
      if (typeof read !== 'boolean' || typeof write !== 'boolean') { this.fail(new ProtocolError('Invalid access notification.')); return }
      this.canWrite = read && write
      this.invalidate()
      if (!read) this.fail(new Error('[FORBIDDEN] Document access was revoked. The local journal is retained.'))
      else { this.canWrite = write; if (this.connected) void this.join() }
    })
    this.conn.onReconnecting(() => { this.connected = false; this.invalidate(true); this.notify(() => this.opts.onStatus?.('reconnecting')) })
    this.conn.onReconnected(() => { if (this.destroyed || this.failed) return; this.connected = true; this.invalidate(); void this.join() })
    this.conn.onClose(() => { this.connected = false; this.invalidate(true); this.notify(() => this.opts.onStatus?.('offline')); this.schedule(() => this.connect()) })
  }
  attachEditor(editor: Editor): void { this.editor = editor; this.editorSchema = editor.schema; this.collector.attachEditor(editor) }
  detachEditor(): void { this.collector.detachEditor(); this.editor = null }
  async start(): Promise<void> { if (this.started || this.destroyed) return; this.started = true; this.presence.start(); await this.connect() }
  async flushLocal(): Promise<void> {
    const boundary = this.tail
    await boundary
    if (this.failed) throw new Error('The local journal failed. Pending in-memory changes are not confirmed durable.')
    if (!this.record) throw new Error('The local journal has not been initialized.')
  }
  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true; this.invalidate(); if (this.retry) clearTimeout(this.retry)
    this.collector.destroy(); this.presence.stop(); this.presence.announceLeave()
    void this.conn.stop().catch(() => {}).finally(() => this.presence.destroy())
    // Already captured storage writes finish; host owns store lifetime.
  }
  private notify(...callbacks: Array<() => void>): void {
    for (const callback of callbacks) { if (this.destroyed) return; try { callback() } catch (error) { try { this.opts.onCallbackError?.(error) } catch {} } }
  }
  private invalidate(transportOffline = false): void {
    ++this.generation; this.joined = false; this.buffered = []; this.bufferBytes = 0
    const offlineWritable = transportOffline && this.opts.allowOfflineEditing === true
      && this.record !== null && this.canWrite && !this.failed && !this.destroyed
    this.notify(() => this.opts.onSynced?.(false), () => this.opts.onWritable?.(offlineWritable))
  }
  private fail(cause: unknown): void {
    if (this.failed || this.destroyed) return
    this.failed = true; this.invalidate(); if (this.retry) clearTimeout(this.retry)
    this.notify(() => this.opts.onFatalError?.(cause instanceof Error ? cause : new Error(String(cause))), () => this.opts.onProjectionStatus?.('degraded'), () => this.opts.durable.onJournal?.({ branchId: this.branch, state: 'failed', pending: this.count() }))
  }
  private count(): number { return (this.record?.pending.length ?? 0) + this.volatile }
  private status(state: 'writing' | 'durable'): void {
    if (this.failed) return
    this.notify(() => this.opts.onPendingUpdates?.(this.count()),
      () => this.opts.onProjectionStatus?.(this.count() > 0 || !this.joined ? 'pending' : 'current'),
      () => this.opts.durable.onJournal?.({ branchId: this.branch, state, pending: this.count() }))
  }
  private schedule(action: () => Promise<void>): void {
    if (this.destroyed || this.failed || this.retry) return
    this.retry = setTimeout(() => { this.retry = undefined; void action() }, joinRetryDelay(this.retries++))
  }
  private async connect(): Promise<void> {
    if (this.destroyed || this.failed || this.connected) return
    this.notify(() => this.opts.onStatus?.('connecting'))
    try { await this.conn.start(); if (this.destroyed || this.failed) return; this.connected = true; this.invalidate(); await this.join() }
    catch (error) { if (isAuthorizationError(error)) this.fail(error); else this.schedule(() => this.connect()) }
  }
  private enqueue(action: () => Promise<void>): Promise<void> {
    const work = this.tail.then(action)
    this.tail = work.catch(error => { this.fail(error) })
    return work
  }
  private async save(next: Omit<JournalRecord, 'revision'>): Promise<void> {
    const previous = this.record
    const record = cloneJournal({ ...next, revision: (previous?.revision ?? 0) + 1 })
    await this.opts.durable.store.write(record, previous?.revision ?? 0, previous?.owner ?? this.owner)
    this.record = record
  }
  private capture(update: Uint8Array, snapshot: Uint8Array): void {
    if (this.applying || this.destroyed || this.failed) return
    if (!this.record) return // The adapter keeps editing readonly until the initial join is durable.
    const owned = Uint8Array.from(update), state = Uint8Array.from(snapshot), updateId = crypto.randomUUID()
    this.capturedUpdates.add(owned)
    ++this.volatile
    const work = this.enqueue(async () => {
      if (!this.record) throw new Error('Journal baseline is missing.')
      if (owned.byteLength > this.maxUpdateBytes || this.record.pending.reduce((sum, item) => sum + item.update.byteLength, owned.byteLength) > (this.opts.maxPendingBytes ?? 4 * 1024 * 1024)) throw new RangeError('Durable outbox exceeds its byte budget; local changes are retained in memory.')
      const sha256 = await journalSha256(owned)
      await this.save({ ...this.record, snapshot: Y.mergeUpdates([this.record.snapshot, state]), pending: [...this.record.pending, { updateId, sha256, update: owned }] })
      this.capturedUpdates.delete(owned); --this.volatile; this.status('durable')
    })
    this.status('writing')
    void work.then(() => this.pump()).catch(() => {})
  }
  private validateState(snapshot: Uint8Array): void {
    const document = decodeRecoveryDocument({ yjsState: snapshot, pendingUpdates: [], fragment: this.opts.field ?? 'default' }, { maxNodes: 100_000, maxDepth: 100 })
    if (!this.editorSchema) throw new ProtocolError('The durable provider requires an attached editor before its first join.')
    validateValue('rich', { format: RICH_FORMAT, version: 1, document, plugins: this.opts.schema.plugins }, this.editorSchema, this.opts.schema.plugins, new Map())
  }
  /** Host callbacks may make a local edit synchronously inside remote Yjs publication. */
  private captureResidual(): void {
    if (!this.record || this.destroyed || this.failed) return
    const known = Y.mergeUpdates([this.record.snapshot, ...this.capturedUpdates])
    const diff = Y.encodeStateAsUpdate(this.opts.ydoc, Y.encodeStateVectorFromUpdate(known))
    if (hasMissingUpdateContent(diff, known)) {
      const state = Y.encodeStateAsUpdate(this.opts.ydoc)
      this.validateState(state)
      this.capture(diff, state)
    }
  }
  private async join(): Promise<void> {
    const generation = this.generation
    const current = () => !this.destroyed && !this.failed && this.connected && generation === this.generation
    try {
      const capabilities = clonePlainData(await this.conn.invoke('GetCapabilities', this.opts.documentId)) as any
      if (!current()) return
      if (capabilities.protocolVersion !== 2 || capabilities.authority !== 'server' || capabilities.recovery !== 'durable-receipts') throw new ProtocolError('This server does not support durable V2 collaboration.')
      const joined = clonePlainData(await this.conn.invoke('JoinDocumentV2', this.opts.documentId, this.opts.schema)) as any
      if (!current()) return
      if (joined.protocolVersion !== 2 || joined.authority !== 'server' || joined.recovery !== 'durable-receipts' || !uuid.test(joined.epoch) || joined.fragment !== (this.opts.field ?? 'default') || typeof joined.canWrite !== 'boolean') throw new ProtocolError('Invalid authoritative join.')
      confirmDocumentSchema(this.opts.schema, joined.schema)
      const sequence = validateServerSequence(joined.seq, true)
      if (joined.snapshotSequence !== sequence || validateServerSequence(joined.persistedSequence, true) < sequence) throw new ProtocolError('Join snapshot is not durably bound to its sequence.')
      this.maxUpdateBytes = validateServerSequence(joined.limits?.maxUpdateBytes)
      const server = b64ToU8(joined.snapshot)
      this.validateState(server)
      await this.enqueue(async () => {
        if (!current()) return
        const stored = this.record ?? await this.opts.durable.store.read(this.opts.durable.scope, this.branch)
        if (!current()) return
        if (stored) {
          const owned = cloneJournal(stored)
          if (owned.branchId !== this.branch || JSON.stringify(owned.scope) !== JSON.stringify(this.opts.durable.scope)) throw new ProtocolError('The journal store returned another account or branch.')
          confirmDocumentSchema(this.opts.schema, owned.schema)
          if (owned.epoch !== joined.epoch || owned.fragment !== joined.fragment) throw new ProtocolError('The recovery branch belongs to another document epoch or fragment. Export it separately.')
          if (owned.sequence > sequence) throw new ProtocolError('The server is older than the recovery branch.')
          for (const item of owned.pending) if (await journalSha256(item.update) !== item.sha256) throw new ProtocolError('Recovery update hash mismatch.')
          if (!current()) return
          this.record = owned
        } else if (this.opts.durable.branchId) throw new ProtocolError('The selected recovery branch no longer exists.')
        const merged = this.record ? Y.mergeUpdates([server, this.record.snapshot]) : server
        this.validateState(merged)
        await this.save({ version: 2, scope: this.opts.durable.scope, branchId: this.branch, epoch: joined.epoch, owner: this.owner,
          schema: this.opts.schema, fragment: joined.fragment, sequence, snapshot: merged, pending: this.record?.pending ?? [] })
        if (!current()) return
        this.applying = true
        try { Y.applyUpdate(this.opts.ydoc, merged, this) } finally { this.applying = false }
        this.captureResidual()
        if (!current()) return
        this.canWrite = joined.canWrite; this.joined = true; this.retries = 0
        const buffered = this.buffered.splice(0); this.bufferBytes = 0
        for (const update of buffered) this.receive(update)
        this.status('durable')
        this.notify(() => this.opts.onStatus?.('connected'), () => { if (current()) this.opts.onSynced?.(true) }, () => { if (current()) this.opts.onWritable?.(this.canWrite) }, () => { if (current()) this.opts.onProjectionStatus?.(this.count() > 0 ? 'pending' : 'current') })
        this.presence.announceLocal()
      })
      if (current()) void this.pump()
    } catch (error) {
      if (!current()) return
      if (error instanceof ProtocolError || isAuthorizationError(error) || /SCHEMA|epoch|journal|[Ii]nvalid|[Uu]nsupported|[Rr]ecovery|MIGRATION_REQUIRED|READ_ONLY|DOCUMENT_FULL|INVALID_CONTENT|INVALID_UPDATE|AUTHORITY_LIMIT/.test(String(error))) this.fail(error)
      else this.schedule(() => this.join())
    }
  }
  private receive(update: RemoteUpdate): void {
    if (!this.record || !this.joined) return
    const validated = receipt(update, { ...update, epoch: this.record.epoch })
    if (validated.sequence <= this.record.sequence) return
    // Gaps are judged on the queue: earlier remote updates or an own ACK may still be ahead of it.
    const generation = this.generation
    void this.enqueue(async () => {
      if (!this.record || generation !== this.generation || this.destroyed || this.failed) return
      if (validated.sequence <= this.record.sequence) return
      if (validated.sequence !== this.record.sequence + 1) { this.invalidate(); void this.join(); return }
      const bytes = b64ToU8(update.update)
      if (await journalSha256(bytes) !== update.sha256) throw new ProtocolError('Remote update hash mismatch.')
      const merged = Y.mergeUpdates([this.record.snapshot, bytes])
      this.validateState(merged)
      await this.save({ ...this.record, snapshot: merged, sequence: validated.sequence })
      if (generation !== this.generation || this.destroyed || this.failed) return
      this.applying = true
      try { Y.applyUpdate(this.opts.ydoc, bytes, this) } finally { this.applying = false }
      this.captureResidual()
    }).catch(() => {})
  }
  private async pump(): Promise<void> {
    if (this.pumpingGeneration === this.generation || !this.joined || !this.canWrite || this.failed || this.destroyed) return
    const generation = this.generation
    this.pumpingGeneration = generation
    try {
      while (this.joined && this.canWrite && !this.failed && !this.destroyed && generation === this.generation) {
        await this.tail
        if (generation !== this.generation || !this.joined || !this.canWrite || this.failed || this.destroyed) return
        const item = this.record?.pending[0]
        if (!item || !this.record) return
        const epoch = this.record.epoch
        // The ownership CAS is repeated immediately before every network send. A selected
        // branch claimed by another tab can no longer silently advance this writer's journal.
        await this.enqueue(async () => {
          if (generation !== this.generation || this.failed || this.destroyed || !this.joined) return
          await this.save({ ...this.record! })
        })
        if (generation !== this.generation || this.failed || this.destroyed || !this.joined) return
        const result = await this.conn.invoke('SendUpdateV2', this.opts.documentId, { epoch, updateId: item.updateId, sha256: item.sha256, update: u8ToB64(item.update) })
        if (generation !== this.generation || this.failed || this.destroyed || !this.joined) return
        const ack = receipt(result, { epoch, updateId: item.updateId, sha256: item.sha256 })
        await this.enqueue(async () => {
          if (!this.record || generation !== this.generation) return
          // The server excludes the sender from its broadcast, so the next sequence is this update.
          // Its bytes are already in the ydoc and in the journal snapshot since capture.
          const own = ack.sequence === this.record.sequence + 1
          await this.save({ ...this.record, sequence: own ? ack.sequence : this.record.sequence, pending: this.record.pending.filter(pending => pending.updateId !== item.updateId) })
          this.retries = 0; this.status('durable')
          if (ack.sequence > this.record!.sequence) { this.invalidate(); void this.join() }
        })
      }
    } catch (error) {
      if (generation !== this.generation || this.destroyed) return
      if (error instanceof ProtocolError || isAuthorizationError(error) || /READ_ONLY|EPOCH|COLLISION|SCHEMA|LIMIT|VALIDATION|conflict|DOCUMENT_FULL|INVALID_CONTENT|INVALID_UPDATE|MIGRATION_REQUIRED/i.test(String(error))) {
        if (/DOCUMENT_FULL/.test(String(error))) this.notify(() => this.opts.onDocumentFull?.())
        this.fail(error)
      }
      else this.schedule(() => this.pump())
    } finally {
      // An obsolete RPC may never settle. A rejoined generation owns its own pump,
      // and a late completion from the old one must not unlock that newer pump.
      if (this.pumpingGeneration === generation) this.pumpingGeneration = null
    }
  }
}
