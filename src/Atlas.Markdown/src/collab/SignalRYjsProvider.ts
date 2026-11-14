// Atlas Markdown collaboration — a hand-written Yjs provider over
// SignalR against our own .NET relay (`/hubs/markdown`). The server relays opaque
// Yjs update bytes and persists a snapshot pushed by the elected snapshotter;
// this client speaks the framing protocol.
//
// Wire note: the JSON hub protocol carries byte[] as base64 strings (C# STJ
// serializes byte[] ↔ base64; a JS Uint8Array would JSON-stringify to an object,
// so we base64-encode/decode explicitly). Keystroke updates are tens of bytes,
// so the +33% is noise (per the ADR-018 decision to use JSON, not MessagePack).
//
// A-M4 — presence: a `y-protocols/awareness` instance rides the same connection
// via SendAwareness/ReceiveAwareness (relay-only, never stored server-side).
// CollaborationCaret binds to `provider.awareness` for remote cursors; the
// `onPeers` callback drives PresenceStack. Local state is re-announced on a 15 s
// heartbeat and remote peers idle for >30 s are dropped (matches y-websocket).

import * as Y from 'yjs'
import { Awareness } from 'y-protocols/awareness'
import type { Editor, JSONContent } from '@tiptap/core'
import type { MarkdownDocumentSchema } from '../core/types'
import { projectEditor } from '../engine/projections'
import { confirmDocumentSchema, isSchemaError, normalizeDocumentSchema } from './documentSchema'
import { PresenceSession, type PresenceUser } from './provider/presence'
import { createSignalRConnection, isAuthorizationError, type MarkdownSyncConnection } from './provider/transport'
import {
  b64ToU8, u8ToB64, normalizeJoin, normalizeSeed, newUpdateId, sha256Hex, isDocumentFull, isUpdateIdCollision,
  shouldRetryWithNewUpdateId, isReadOnlyError, isInvalidContentError, isSupersededError, type JoinResultRaw,
  ProtocolError, validateServerSequence,
} from './provider/protocol'
import { createUpdateCoverage, hasMissingUpdateContent } from './provider/updateCoverage'
import { delay, joinRetryDelay, INITIAL_CONNECT_RETRY_MS, OUTBOX_RETRY_MS, SNAPSHOT_RETRY_MS } from './provider/retryPolicy'
import { SnapshotScheduler } from './provider/snapshotScheduler'
import { assertRecoveryBinding, attachRecovery, type MarkdownRecovery, type RecoveryCapturePort } from './recovery/session'
import { repairLegacyDocument } from '../engine/legacyDocument'

export type { PresenceUser } from './provider/presence'
export type { MarkdownSyncConnection } from './provider/transport'

export type ProviderStatus = 'connecting' | 'connected' | 'reconnecting' | 'offline'

type OutboxDrainResult = 'drained' | 'blocked' | 'fatal'
const DEFAULT_MAX_PENDING_BYTES = 4 * 1024 * 1024
/** Edits waiting behind an update in flight merge into one only up to this size, so a batch never outgrows the hub's per-update limit
 * (DocumentAuthorityOptions.MaxUpdateBytes); one edit larger than it (a big paste) is sent as it is. */
const DEFAULT_MAX_MERGED_UPDATE_BYTES = 1024 * 1024
// The hub acknowledges and broadcasts concurrent writes independently, so a peer's update often overtakes the
// acknowledgement of our own (seq 24 arrives before the ACK of 23). Such a gap closes within milliseconds; only one
// that stays open this long means a lost delivery and needs a fresh join, which blocks typing for its round trip.
const SEQUENCE_GAP_GRACE_MS = 3000
/** A remote update that took longer than a frame to apply switches to batching (see receive). */
const SLOW_REMOTE_APPLY_MS = 16

class SeedTransformError extends Error {
  constructor(cause: unknown) {
    super('The document seed transformation failed.', { cause })
    this.name = 'SeedTransformError'
  }
}

export interface SignalRYjsProviderOptions {
  hubUrl: string
  documentId: string
  ydoc: Y.Doc
  accessToken?: () => string | Promise<string>
  /** Set false to lock on browser offline and require a fresh authenticated join. */
  allowOfflineEditing?: boolean
  /** Omit only for unversioned legacy documents; versioned sessions never downgrade on failure. */
  schema?: MarkdownDocumentSchema
  /** Opt-in periodic, manual-only local recovery checkpoints. Never replays cached work. */
  recovery?: MarkdownRecovery
  /** Actual Yjs editor fragment recorded for manual recovery; defaults to 'default'. */
  field?: string
  onStatus?: (status: ProviderStatus) => void
  onSynced?: (synced: boolean) => void
  /** Fired synchronously right before the server's document is applied to a session that may write. */
  onHydrating?: () => void
  onFlushDegraded?: (degraded: boolean) => void
  onExternalEditArchived?: () => void
  /** Fired when the server rejected an edit because the document exceeds its size cap. */
  onDocumentFull?: () => void
  /** Fired whenever the set of remote peers (from awareness) changes. */
  onPeers?: (peers: PresenceUser[]) => void
  /** Number of local updates that have not yet received a durable server acknowledgement. */
  onPendingUpdates?: (count: number) => void
  /** Snapshot/projection durability is independent of the update acknowledgement. */
  onProjectionStatus?: (status: 'pending' | 'current' | 'degraded') => void
  /** Effective document write permission from the latest join or live access notification. */
  onWritable?: (writable: boolean) => void
  /** Sequence number of the latest snapshot the server reports as durably stored (hosts re-read projections here). */
  onSnapshotStored?: (sequence: number) => void
  onFatalError?: (error: Error) => void
  /** Receives failures thrown by observational callbacks without changing provider semantics. */
  onCallbackError?: (error: unknown) => void
  /** Maximum encoded bytes retained in the in-memory durable outbox before editing is marked full. */
  maxPendingBytes?: number
  /** Waiting edits merge into one update only up to this many bytes (default 1 MiB). */
  maxMergedUpdateBytes?: number
  /**
   * Fired once, on the elected seeding client only, right after the readable file has been parsed
   * into the fresh shared doc and before the initial snapshot is pushed. The host runs any one-shot
   * document upgrades here (e.g. rewriting issue/contact references into mention nodes) so they ride
   * the initial snapshot on the single writer instead of racing on every client.
   */
  onSeeded?: (editor: Editor) => void
  /** The state the host opened `ydoc` with (a page bundle): a first join that brings exactly it has nothing to apply. */
  knownState?: Uint8Array
}

interface PendingUpdate {
  id: string
  update: Uint8Array
  updateCount: number
  attempted: boolean
}

export class SignalRYjsProvider {
  private readonly conn: MarkdownSyncConnection
  private editor: Editor | null = null
  private waitingForEditor: (() => void) | null = null
  private isSnapshotter = false
  /** The server derives the snapshot's projections (join `projectionsDerived`): the snapshotter pushes the state alone. */
  private projectionsDerived = false
  private snapshotterEventRevision = 0
  private incorporatedSeq = 0
  /** Server sequences received past a gap, applied in order once it closes; `null` is our own acknowledged write. */
  private readonly aheadOfGap = new Map<number, Uint8Array | null>()
  private aheadGeneration = 0
  private gapTimer: ReturnType<typeof setTimeout> | null = null
  /** Remote updates waiting for one batched transaction while applying them is slow (see receive). */
  private readonly inbox: Array<{ update: string; seq: number }> = []
  private cancelInbox: (() => void) | null = null
  private slowRemoteApply = false
  private documentRevision = 0
  private projectedRevision = -1
  /** The revision at which the join found the server's snapshot holding this very state (nothing after it, nothing of ours); -1 none. */
  private serverRevision = -1
  /** The server asked for a snapshot (RequestSnapshot, SnapshotterChanged) that has not been sent yet: no join's finding skips it. */
  private snapshotAsked = false
  /** The newest sequence the server announced stored (SnapshotStored), and a pushed projection waiting for it to be stored. */
  private storedSequence = -1
  private storedWait: { sequence: number; revision: number } | null = null
  private connected = false
  // A completed RPC belongs to one SignalR connection. Reconnected transports have no old
  // group membership, so an earlier join must never authorize or hydrate the new connection.
  private connectionGeneration = 0
  private joined = false
  private writable: boolean | null = null
  private schemaAccepted = false
  private readonly beforeSchemaUpdates: Array<{ update: string; seq: number }> = []
  private seeding = false
  private initialSnapshotPending = false
  private destroyed = false
  private failing = false
  private applyingHydration = false
  private accessDenied = false
  private started = false
  private readonly snapshotSchedule = new SnapshotScheduler(() => { void this.pushSnapshot() })
  private snapshotPush: Promise<boolean> | null = null
  private joining: Promise<void> | null = null
  private outboxRetry: ReturnType<typeof setTimeout> | null = null
  private outboxPump: Promise<OutboxDrainResult> | null = null
  private outboxContinuationRequested = false
  private readonly pendingUpdates: PendingUpdate[] = []
  private pendingBytes = 0
  private pendingUpdateCount = 0
  private outboxFull = false
  private eventWindow: Window | null = null
  private browserOffline = false
  private browserConnectionRevision = 0
  private browserStop: Promise<void> | null = null
  private connectionOperation: Promise<void> | null = null
  private readonly opts: SignalRYjsProviderOptions
  private recoveryCapture: RecoveryCapturePort | null = null
  private knownState: Uint8Array | null

  // ---- presence ----
  readonly awareness: Awareness
  private readonly presence: PresenceSession

  constructor(opts: SignalRYjsProviderOptions, connection?: MarkdownSyncConnection) {
    if (opts.recovery) assertRecoveryBinding(opts.recovery, opts.documentId)
    if (opts.field !== undefined && (typeof opts.field !== 'string' || !opts.field.trim())) throw new TypeError('Collaboration field must be a non-empty string.')
    this.opts = { ...opts, schema: opts.schema ? normalizeDocumentSchema(opts.schema) : undefined }
    this.knownState = opts.knownState ?? null
    this.awareness = new Awareness(opts.ydoc)
    this.conn = connection ?? createSignalRConnection(opts.hubUrl, opts.accessToken)
    this.presence = new PresenceSession(this.awareness, {
      connection: this.conn, documentId: this.opts.documentId,
      isConnected: () => this.connected, isJoined: () => this.joined,
      onPeers: peers => this.notify(() => this.opts.onPeers?.(peers)),
    })

    const forThisDoc = (docId: string) => !this.destroyed && !this.accessDenied && docId === this.opts.documentId

    // Optional server event: older servers continue to establish permissions at join.
    this.conn.on('DocumentAccessChanged', (docId: string, canRead: boolean, canWrite: boolean) => {
      if (forThisDoc(docId) && typeof canRead === 'boolean' && typeof canWrite === 'boolean') {
        this.onAccessChanged(canRead, canWrite)
      }
    })

    this.conn.on('ReceiveUpdate', (docId: string, update: string, seq: number) => {
      if (forThisDoc(docId)) this.receive(update, seq)
    })
    this.conn.on('ReceiveAwareness', (docId: string, payload: string) => {
      if (forThisDoc(docId)) this.presence.receive(payload)
    })
    this.conn.on('RequestSnapshot', (docId: string) => {
      // The server asks for one: it is sent whatever the join found.
      if (forThisDoc(docId)) { this.snapshotAsked = true; void this.pushSnapshot() }
    })
    this.conn.on('SnapshotterChanged', (docId: string, youAreSnapshotter: boolean) => {
      if (!forThisDoc(docId)) return
      this.snapshotterEventRevision++
      this.isSnapshotter = youAreSnapshotter && this.writable !== false
      if (this.isSnapshotter) { this.snapshotAsked = true; void this.pushSnapshot() }
    })
    this.conn.on('FlushDegraded', (docId: string, degraded: boolean) => {
      if (forThisDoc(docId)) this.notify(() => this.opts.onFlushDegraded?.(degraded))
    })
    this.conn.on('ExternalEditArchived', (docId: string) => {
      if (forThisDoc(docId)) this.notify(() => this.opts.onExternalEditArchived?.())
    })
    this.conn.on('SnapshotStored', (docId: string, sequence: number) => {
      if (!forThisDoc(docId)) return
      this.storedSequence = Math.max(this.storedSequence, sequence)
      const wait = this.storedWait
      if (wait && sequence >= wait.sequence) {
        this.storedWait = null
        if (this.documentRevision === wait.revision && this.incorporatedSeq === wait.sequence && this.pendingUpdates.length === 0) {
          this.notify(() => this.opts.onProjectionStatus?.('current'))
        }
      } else if (!wait && !this.isSnapshotter && sequence >= this.incorporatedSeq && this.pendingUpdates.length === 0) {
        // Another writer's or the server's own checkpoint (3.6) stored everything this session holds: its save goes through now.
        this.notify(() => this.opts.onProjectionStatus?.('current'))
      }
      this.notify(() => this.opts.onSnapshotStored?.(sequence))
    })

    this.conn.onReconnecting(() => {
      if (this.destroyed || this.accessDenied || this.browserOffline) return
      const generation = ++this.connectionGeneration
      this.waitingForEditor?.()
      this.waitingForEditor = null
      this.connected = false
      this.joined = false
      this.schemaAccepted = false
      this.beforeSchemaUpdates.length = 0
      if (!this.markRecoveryDirty(generation)) return
      this.setSynced(false)
      this.setStatus('reconnecting')
    })
    // Reconnect = a brand-new connectionId; SignalR group membership does NOT survive it,
    // so re-JoinDocument (which re-hydrates + re-runs back-sync). Duplicate applies are CRDT no-ops.
    this.conn.onReconnected(() => {
      if (this.destroyed || this.accessDenied) return
      if (this.browserOffline) { void this.stopBrowserTransport(); return }
      this.connected = true
      this.beginRehydrate()
    })
    this.conn.onClose(() => {
      if (this.destroyed || this.accessDenied) return
      const generation = ++this.connectionGeneration
      this.waitingForEditor?.()
      this.waitingForEditor = null
      this.connected = false
      this.joined = false
      if (!this.markRecoveryDirty(generation)) return
      this.setSynced(false)
      this.presence.clearPeers()
      this.setStatus('offline')
    })

    this.opts.ydoc.on('update', this.onDocUpdate)
    if (this.opts.recovery) {
      this.recoveryCapture = attachRecovery(this.opts.recovery, this.opts.documentId, () => ({
        schema: this.opts.schema ? normalizeDocumentSchema(this.opts.schema) : null,
        fragment: this.opts.field ?? 'default', observedSequence: this.incorporatedSeq,
        documentRevision: this.documentRevision, connectionGeneration: this.connectionGeneration,
        yjsState: Y.encodeStateAsUpdate(this.opts.ydoc),
        pendingUpdates: this.pendingUpdates.map(item => ({ ...item, update: item.update.slice() })),
      }))
    }
  }

  /** Give the provider the editor (needed to render markdown for snapshots + seed the doc). */
  attachEditor(editor: Editor): void {
    if (this.destroyed) return
    this.editor = editor
    const nextWindow = editor.view?.dom?.ownerDocument?.defaultView ?? null
    if (nextWindow !== this.eventWindow) {
      this.removeWindowListeners()
      this.eventWindow = nextWindow
      this.eventWindow?.addEventListener('beforeunload', this.onBeforeUnload)
      this.eventWindow?.document.addEventListener('visibilitychange', this.onVisibilityChange)
      if (this.opts.allowOfflineEditing === false) {
        this.eventWindow?.addEventListener('offline', this.onBrowserOffline)
        this.eventWindow?.addEventListener('online', this.onBrowserOnline)
        if (this.eventWindow?.navigator.onLine === false) this.onBrowserOffline()
        else if (this.browserOffline) this.onBrowserOnline()
      }
    }
    this.waitingForEditor?.()
    this.waitingForEditor = null
    if (this.isSnapshotter && this.joined) this.scheduleSnapshot()
  }

  /** Keep the Y.Doc/transport alive, but never serialize a stale unmounted editor. */
  detachEditor(): void {
    // Leaving inside the app (a route change): a waiting snapshot is projected now, while the editor lives, so a peer's save does
    // not wait for it in vain (performSnapshotPush projects before its first await).
    if (this.editor && this.isSnapshotter && this.joined) this.snapshotSchedule.flush()
    this.editor = null
    this.removeWindowListeners()
    this.eventWindow = null
    this.snapshotSchedule.clearSchedule()
    this.snapshotSchedule.clearRetry()
    this.notify(() => this.opts.onProjectionStatus?.('pending'))
  }

  /** Connect + join. Initial outages retry indefinitely; local edits remain queued in the Y.Doc. */
  async start(): Promise<void> {
    if (this.started || this.destroyed) return
    this.started = true
    await this.ensureConnected()
  }

  private async ensureConnected(): Promise<void> {
    if (this.connectionOperation) return this.connectionOperation
    const operation = this.connectAndJoin()
    this.connectionOperation = operation
    try { await operation }
    finally { if (this.connectionOperation === operation) this.connectionOperation = null }
  }

  private async connectAndJoin(): Promise<void> {
    if (this.browserOffline) { this.setStatus('offline'); return }
    this.setStatus('connecting')
    while (!this.destroyed && !this.browserOffline && !this.connected) {
      const browserConnectionRevision = this.browserConnectionRevision
      try {
        await this.conn.start()
        if (browserConnectionRevision !== this.browserConnectionRevision || this.browserOffline || this.destroyed) {
          await this.stopBrowserTransport()
          return
        }
        this.connected = true
      } catch (error) {
        if (browserConnectionRevision !== this.browserConnectionRevision || this.browserOffline || this.destroyed) return
        if (isAuthorizationError(error)) { this.fail(error); return }
        this.setStatus('offline')
        // A retry is still the same outage. Keep the UI stable until joining succeeds.
        await delay(INITIAL_CONNECT_RETRY_MS)
      }
    }
    if (this.destroyed || this.browserOffline || !this.connected) return
    await this.joinWithRetry()
    // destroy() can land during the connect/join awaits (fire-and-forget start + synchronous
    // teardown on unmount); re-check so we don't re-create the awareness intervals it just cleared.
    if (this.destroyed || this.browserOffline) return
    this.presence.start()
  }

  destroy(): void {
    if (this.destroyed) return
    this.destroyed = true
    this.recoveryCapture?.detach()
    this.beforeSchemaUpdates.length = 0
    this.waitingForEditor?.()
    this.waitingForEditor = null
    this.snapshotSchedule.destroy()
    this.clearOutboxRetry()
    this.dropAheadOfGap()
    this.cancelInbox?.()
    this.inbox.length = 0
    this.presence.stop()
    this.opts.ydoc.off('update', this.onDocUpdate)
    this.removeWindowListeners()
    this.eventWindow = null
    // Updates are durable before their acknowledgements. Do not start advisory snapshot work
    // here: the owning controller destroys its editor synchronously after this call.
    try { this.presence.announceLeave() } catch { /* best-effort advisory cleanup */ }
    // Awareness owns local listeners and state, so release it synchronously even if transport
    // shutdown stalls. The leave payload was already encoded and queued above.
    try { this.presence.destroy() } catch { /* best-effort advisory cleanup */ }
    void this.conn.stop().catch(() => { /* transport teardown cannot invalidate committed data */ })
  }

  // ---- doc update pump ----

  private onAccessChanged(canRead: boolean, canWrite: boolean): void {
    // Pending RPCs must not restore permission, acknowledge retained edits or publish
    // a snapshot after revocation. Wake any bootstrap waiting for an editor as well.
    const generation = ++this.connectionGeneration
    // Gate even synchronous/reentrant document events from observer callbacks.
    this.accessDenied = !canRead
    this.waitingForEditor?.()
    this.waitingForEditor = null
    this.isSnapshotter = false
    this.snapshotSchedule.clearSchedule()
    this.snapshotSchedule.clearRetry()
    this.clearOutboxRetry()
    this.setWritable(false)
    if (!this.markRecoveryDirty(generation)) return

    if (!canRead) {
      this.joined = false
      this.connected = false
      this.schemaAccepted = false
      this.beforeSchemaUpdates.length = 0
      this.setSynced(false)
      this.setStatus('offline')
      this.fail(new Error('[FORBIDDEN] Access to the document has been revoked. Local changes remain in this session.'))
      return
    }
    if (!canWrite && (this.pendingUpdates.length > 0 || this.initialSnapshotPending || this.seeding)) {
      this.setSynced(false)
      this.setStatus('offline')
      this.fail(new Error('[READ_ONLY] Write permission was revoked while local changes were pending. Local changes remain in this session.'))
      return
    }
    // A clean reader stays subscribed. A grant (or a notification during hydration)
    // requires a fresh authoritative join before enabling writes or snapshot work.
    if (canWrite || !this.joined || !this.schemaAccepted) this.beginRehydrate()
  }

  private onDocUpdate = (update: Uint8Array, origin: unknown): void => {
    if (this.destroyed) return
    this.documentRevision++
    this.recoveryCapture?.markDirty()
    if (this.destroyed) return
    this.notify(() => this.opts.onProjectionStatus?.('pending'))
    const isRemote = origin === this
    if (!isRemote && !this.seeding) {
      if (this.joined && this.writable === false) {
        this.fail(new Error('[READ_ONLY] The document no longer accepts local changes.'))
        return
      }
      this.enqueueUpdate(update)
      if (this.joined) void this.drainOutbox()
    }
    // The snapshotter re-renders after ANY change (local or remote).
    if (this.isSnapshotter && this.joined) this.scheduleSnapshot()
  }

  private enqueueUpdate(update: Uint8Array): PendingUpdate {
    const copy = update.slice()
    const tail = this.pendingUpdates.at(-1)
    let item: PendingUpdate
    if (tail && !tail.attempted && tail.update.byteLength + copy.byteLength <= (this.opts.maxMergedUpdateBytes ?? DEFAULT_MAX_MERGED_UPDATE_BYTES)) {
      this.pendingBytes -= tail.update.byteLength
      tail.update = Y.mergeUpdates([tail.update, copy])
      tail.updateCount++
      this.pendingBytes += tail.update.byteLength
      item = tail
    } else {
      item = { id: newUpdateId(), update: copy, updateCount: 1, attempted: false }
      this.pendingUpdates.push(item)
      this.pendingBytes += copy.byteLength
    }
    this.pendingUpdateCount++
    this.notifyPendingUpdates()
    const limit = this.opts.maxPendingBytes ?? DEFAULT_MAX_PENDING_BYTES
    if (!this.outboxFull && this.pendingBytes > limit) {
      this.outboxFull = true
      this.setSynced(false)
      this.notify(() => this.opts.onDocumentFull?.())
    }
    return item
  }

  private async drainOutbox(): Promise<OutboxDrainResult> {
    if (this.outboxPump) return this.outboxPump
    // Install the single-flight promise before recovery observers can synchronously edit.
    let resolveOperation!: (result: OutboxDrainResult) => void
    let rejectOperation!: (error: unknown) => void
    const operation = new Promise<OutboxDrainResult>((resolve, reject) => {
      resolveOperation = resolve
      rejectOperation = reject
    })
    this.outboxPump = operation
    void this.drainOutboxCore().then(resolveOperation, rejectOperation)
    try {
      return await operation
    } finally {
      if (this.outboxPump === operation) this.outboxPump = null
      // Observer callbacks may synchronously edit while the completed pump is still
      // registered. Keep those edits moving after the shared promise is released.
      const continuePump = this.outboxContinuationRequested
      this.outboxContinuationRequested = false
      if (continuePump && !this.destroyed && this.connected && this.joined && this.writable !== false
        && !this.initialSnapshotPending && !this.outboxRetry && this.pendingUpdates.length > 0) {
        queueMicrotask(() => { void this.drainOutbox() })
      }
    }
  }

  private canContinueOutbox(generation: number): boolean {
    return generation === this.connectionGeneration && !this.destroyed
      && this.connected && this.joined && this.writable !== false
  }

  private async drainOutboxCore(): Promise<OutboxDrainResult> {
    // A fresh document cannot accept update-log records until its baseline snapshot exists.
    // Pre-join edits ride that initial snapshot instead of being pumped into an unseeded store.
    // A local safety cap disables further editing through the host callback, but already captured
    // edits still have to cross the durability boundary when a connection is available.
    if (!this.connected || !this.joined || this.destroyed || this.initialSnapshotPending) return 'blocked'
    while (this.connected && this.joined && !this.destroyed && this.writable !== false && this.pendingUpdates.length > 0) {
      const generation = this.connectionGeneration
      const item = this.pendingUpdates[0]
      if (!item.attempted) {
        item.attempted = true
        if (!this.markRecoveryDirty(generation) || !this.canContinueOutbox(generation)) return 'blocked'
      }
      try {
        const seq = await this.conn.invoke<number>(
          'SendUpdate',
          this.opts.documentId,
          item.id,
          u8ToB64(item.update),
        )
        // Let the new join reconcile even a successful old ACK against its hydrated state.
        // The pending id/bytes stay intact, so an uncertain write remains safe to retry.
        if (generation !== this.connectionGeneration || this.destroyed) return 'blocked'
        validateServerSequence(seq)
        this.track(seq)
        // Recovery observers may revoke or reconnect while tracking the acknowledged sequence.
        if (!this.canContinueOutbox(generation)) return 'blocked'
        if (this.pendingUpdates[0] === item) this.removePendingHead(item)
      } catch (err) {
        if (generation !== this.connectionGeneration || this.destroyed) return 'blocked'
        if (shouldRetryWithNewUpdateId(err)) {
          // The store still knows the old id is durable but no longer has enough metadata to
          // validate/replay it. A fresh id safely appends the same idempotent Yjs update and lets
          // peers that missed the original broadcast converge.
          item.id = newUpdateId()
          if (!this.markRecoveryDirty(generation) || !this.canContinueOutbox(generation)) return 'blocked'
          continue
        }
        // A superseded generation is final as well: the host's session ends (DocumentEditor offers to join the current one).
        if (isUpdateIdCollision(err) || isInvalidContentError(err) || isSupersededError(err) || err instanceof ProtocolError) {
          this.fail(err)
          return 'fatal'
        }
        if (isReadOnlyError(err)) {
          this.setWritable(false)
          this.fail(err)
          return 'fatal'
        }
        if (isDocumentFull(err)) {
          this.outboxFull = true
          this.setSynced(false)
          this.notify(() => this.opts.onDocumentFull?.())
          return 'blocked'
        }
        if (isAuthorizationError(err)) {
          this.fail(err)
          return 'fatal'
        }
        this.setSynced(false)
        this.setStatus('reconnecting')
        this.scheduleOutboxRetry()
        return 'blocked'
      }
    }
    if (!this.connected || !this.joined || this.destroyed || this.initialSnapshotPending
      || this.pendingUpdates.length > 0 || this.outboxFull) return 'blocked'
    this.clearOutboxRetry()
    this.setSynced(true)
    if (this.destroyed || !this.joined || this.pendingUpdates.length > 0) {
      this.outboxContinuationRequested = this.pendingUpdates.length > 0
      this.setSynced(false)
      return 'blocked'
    }
    this.setStatus('connected')
    if (this.destroyed || !this.joined || this.pendingUpdates.length > 0) {
      this.outboxContinuationRequested = this.pendingUpdates.length > 0
      this.setSynced(false)
      return 'blocked'
    }
    return 'drained'
  }

  private scheduleOutboxRetry(): void {
    if (this.outboxRetry || this.destroyed) return
    this.outboxRetry = setTimeout(() => {
      this.outboxRetry = null
      void this.drainOutbox()
    }, OUTBOX_RETRY_MS)
  }

  private clearOutboxRetry(): void {
    if (!this.outboxRetry) return
    clearTimeout(this.outboxRetry)
    this.outboxRetry = null
  }

  /**
   * A remote update is applied at once while that is cheap. Once applying one takes longer than a frame (a large page: the
   * page on a slow machine), the updates that arrive meanwhile wait for the next animation frame and go in as ONE transaction
   * right before it paints: the cost is per frame, not per update, so a busy page catches up instead of falling behind by a
   * minute under 30 writers (COL2-5). A hidden tab, whose frames stop, catches up within 100 ms.
   */
  private receive(update: string, seq: number): void {
    if (!this.slowRemoteApply && this.inbox.length === 0) { this.timedRemoteApply(() => this.onReceiveUpdate(update, seq)); return }
    this.inbox.push({ update, seq })
    if (this.cancelInbox) return
    const flush = () => {
      this.cancelInbox?.()
      const batch = this.inbox.splice(0)
      if (this.destroyed || !batch.length) return
      this.timedRemoteApply(() => this.opts.ydoc.transact(() => {
        for (const item of batch) { if (this.destroyed) return; this.onReceiveUpdate(item.update, item.seq) }
      }, this))
    }
    const frames = typeof requestAnimationFrame === 'function'
    const frame = frames ? requestAnimationFrame(flush) : 0
    const timer = setTimeout(flush, frames ? 100 : 0)
    this.cancelInbox = () => { if (frames) cancelAnimationFrame(frame); clearTimeout(timer); this.cancelInbox = null }
  }

  private timedRemoteApply(apply: () => void): void {
    const start = performance.now()
    apply()
    this.slowRemoteApply = performance.now() - start > SLOW_REMOTE_APPLY_MS
  }

  private onReceiveUpdate(updateB64: string, seq: number): void {
    if (this.destroyed) return
    let update: Uint8Array
    try {
      validateServerSequence(seq)
      update = this.decodeRemoteUpdate(updateB64)
    } catch (error) { this.fail(error); return }
    if (!this.schemaAccepted || this.applyingHydration) { this.beforeSchemaUpdates.push({ update: updateB64, seq }); return }
    if (seq > this.incorporatedSeq + 1) {
      this.holdAheadOfGap(seq, update)
      return
    }
    try { Y.applyUpdate(this.opts.ydoc, update, this) } // origin === this → not echoed back
    catch (error) { this.fail(error); return }
    this.track(seq)
  }

  private track(seq: number): void {
    if (seq > this.incorporatedSeq + 1) {
      this.holdAheadOfGap(seq, null)
      return
    }
    if (seq > this.incorporatedSeq) {
      this.incorporatedSeq = seq
      this.recoveryCapture?.markDirty()
    }
    this.releaseAheadOfGap()
  }

  /** The document stays an exact server prefix: a sequence past a gap waits for the gap instead of a fresh join. */
  private holdAheadOfGap(seq: number, update: Uint8Array | null): void {
    if (this.aheadGeneration !== this.connectionGeneration) this.dropAheadOfGap()
    this.aheadGeneration = this.connectionGeneration
    this.aheadOfGap.set(seq, update)
    this.gapTimer ??= setTimeout(() => {
      this.gapTimer = null
      if (this.aheadOfGap.size === 0 || this.aheadGeneration !== this.connectionGeneration) return
      this.beginRehydrate()
    }, SEQUENCE_GAP_GRACE_MS)
  }

  private releaseAheadOfGap(): void {
    if (this.aheadOfGap.size === 0) return
    const generation = this.connectionGeneration
    if (this.aheadGeneration !== generation) { this.dropAheadOfGap(); return }
    for (const seq of this.aheadOfGap.keys()) if (seq <= this.incorporatedSeq) this.aheadOfGap.delete(seq)
    while (this.aheadOfGap.has(this.incorporatedSeq + 1)) {
      const seq = this.incorporatedSeq + 1
      const update = this.aheadOfGap.get(seq)!
      this.aheadOfGap.delete(seq)
      if (update) {
        try { Y.applyUpdate(this.opts.ydoc, update, this) }
        catch (error) { this.fail(error); return }
        // A synchronous observer may have started a fresh join, which owns the sequence from here.
        if (generation !== this.connectionGeneration || this.destroyed) return
      }
      this.incorporatedSeq = seq
      this.recoveryCapture?.markDirty()
    }
    if (this.aheadOfGap.size === 0) this.dropAheadOfGap()
  }

  private dropAheadOfGap(): void {
    this.aheadOfGap.clear()
    if (this.gapTimer) clearTimeout(this.gapTimer)
    this.gapTimer = null
  }

  // ---- join / hydrate / back-sync ----

  private async joinWithRetry(): Promise<void> {
    if (this.joining) return this.joining
    const operation = this.joinWithRetryCore()
    this.joining = operation
    try {
      await operation
    } finally {
      if (this.joining === operation) this.joining = null
    }
  }

  private async joinWithRetryCore(): Promise<void> {
    for (let attempt = 0; !this.destroyed && this.connected; attempt++) {
      const generation = this.connectionGeneration
      try {
        await this.join(generation)
        if (generation !== this.connectionGeneration) continue
        return
      } catch (error) {
        // Ignore both successful and failed responses from superseded connections. The shared
        // join task is reused by onReconnected, so it must issue the new join itself immediately.
        if (generation !== this.connectionGeneration) continue
        if (isAuthorizationError(error) || isReadOnlyError(error) || isSchemaError(error) || error instanceof SeedTransformError || error instanceof ProtocolError) {
          if (isReadOnlyError(error)) this.setWritable(false)
          this.fail(error)
          return
        }
        this.joined = false
        this.setSynced(false)
        this.setStatus('reconnecting')
        await delay(joinRetryDelay(attempt))
      }
    }
  }

  private beginRehydrate(): void {
    if (this.destroyed || this.browserOffline || !this.connected) return
    // Also invalidates a hydration currently draining its initial back-sync. Its outer
    // retry loop must perform another join when that ACK reveals a missing prefix.
    const generation = ++this.connectionGeneration
    this.joined = false
    this.schemaAccepted = false
    this.dropAheadOfGap()
    if (!this.markRecoveryDirty(generation)) return
    this.setSynced(false)
    this.setStatus('reconnecting')
    void this.joinWithRetry()
  }

  private async join(generation: number): Promise<void> {
    if (!this.connected) return
    const snapshotterEventRevision = this.snapshotterEventRevision
    const result = this.opts.schema
      ? await this.conn.invoke<JoinResultRaw>('JoinDocumentWithSchema', this.opts.documentId, this.opts.schema)
      : await this.conn.invoke<JoinResultRaw>('JoinDocument', this.opts.documentId)
    // A send acknowledgement can trigger this rejoin when it reveals a sequence gap.
    // Finish the old pump before re-arming membership: its result is legitimately
    // "blocked" while joined=false and must not stand in for the new hydration's drain.
    if (this.outboxPump) await this.outboxPump
    if (generation !== this.connectionGeneration) return
    const res = normalizeJoin(result)
    // The compressed seed comes instead of seedMarkdown/seedEditorJson; inflated while updates wait as during the invocation.
    const seed = res.seed === null ? null : normalizeSeed(new TextDecoder().decode(await this.inflate(res.seed, 'seed')))
    if (generation !== this.connectionGeneration) return
    // Updates arriving during the inflation wait in beforeSchemaUpdates, as during the invocation itself.
    const snapshot = res.snapshot === null ? null
      : res.snapshotEncoding === 'gzip' ? await this.inflateRemoteUpdate(res.snapshot) : this.decodeRemoteUpdate(res.snapshot)
    if (generation !== this.connectionGeneration) return
    if (this.destroyed || !this.connected) return
    confirmDocumentSchema(this.opts.schema, res.schema)
    // Parse every blob before publishing even the valid prefix. A Yjs transaction is
    // not a rollback boundary: truncated delete data can throw after structs applied.
    const serverUpdates = [...(snapshot ? [snapshot] : []), ...res.updates.map(value => this.decodeRemoteUpdate(value))]
    const earlyUpdates = this.beforeSchemaUpdates.splice(0)
    // The server's snapshot is its whole state: no update after it.
    const snapshotOnly = snapshot !== null && res.updates.length === 0 && earlyUpdates.length === 0
    let hydratedSequence = res.seq
    for (const item of earlyUpdates) {
      if (item.seq > hydratedSequence + 1) throw new Error('An update sequence gap requires a fresh document hydration.')
      hydratedSequence = Math.max(hydratedSequence, item.seq)
      serverUpdates.push(this.decodeRemoteUpdate(item.update))
    }
    let serverState: Uint8Array
    // The host's state again (the page bundle's) and nothing local since, as every local change waits in the outbox: the doc holds
    // exactly this, so nothing is checked, rehearsed, applied or sent back (3.6-G open: six passes over a 1 MB state after it showed).
    const known = this.knownState
    this.knownState = null
    let unchanged = false
    try {
      serverState = serverUpdates.length ? Y.mergeUpdates(serverUpdates) : new Uint8Array([0, 0])
      unchanged = known !== null && this.pendingUpdates.length === 0 && sameBytes(serverState, known)
      if (!unchanged) createUpdateCoverage(serverState) // Reject unsafe identity ranges before live publication.
    } catch (cause) { throw new ProtocolError('The server hydration has invalid update identities.', { cause }) }
    // A fresh join (nothing local yet) skips the rehearsal: a failed apply ends the session the same way, and nothing local can
    // be half-merged. Every local change after it goes through the outbox, so there is no residual to back-sync either.
    const fresh = this.opts.ydoc.store.clients.size === 0 && this.pendingUpdates.length === 0
    if (!fresh && !unchanged) {
      const rehearsal = new Y.Doc({ gc: this.opts.ydoc.gc })
      try {
        Y.applyUpdate(rehearsal, Y.encodeStateAsUpdate(this.opts.ydoc))
        Y.applyUpdate(rehearsal, serverState)
      } catch (cause) { throw new ProtocolError('The server hydration cannot be applied.', { cause }) }
      finally { rehearsal.destroy() }
    }
    this.setWritable(res.canWrite)
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
    this.schemaAccepted = true
    if (snapshotterEventRevision === this.snapshotterEventRevision) this.isSnapshotter = res.isSnapshotter
    this.projectionsDerived = res.projectionsDerived
    const seedContent = seed ? seed.seedEditorJson ?? seed.seedMarkdown : res.seedEditorJson ?? res.seedMarkdown
    this.initialSnapshotPending = seedContent != null && res.snapshot == null
    if (this.initialSnapshotPending && !res.canWrite) {
      throw new Error('[READ_ONLY] A read-only session cannot initialize the document snapshot.')
    }

    // Seeding path: parse the readable file into the fresh Y.Doc and push the initial snapshot.
    if (seedContent != null && res.snapshot == null) {
      this.incorporatedSeq = res.seq
      this.seeding = true
      try {
        await this.seedInitialContent(seedContent, generation)
        if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
        this.joined = true // pump stays suppressed while `seeding` is true
      } finally {
        this.seeding = false
      }
      // The seed content (+ upgrades) must become the durable baseline before this client reports
      // itself as synced. Retry on the same seeding connection; re-joining would make the seeder
      // wait on its own unfinished seed.
      while (generation === this.connectionGeneration && !this.destroyed && this.connected && this.joined) {
        if (await this.pushSnapshot(false, true)) break
        if (generation !== this.connectionGeneration) return
        await delay(SNAPSHOT_RETRY_MS)
      }
      if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.joined) return
      this.initialSnapshotPending = false
      // Edits made while the initial snapshot was in flight were deliberately not acknowledged by
      // that baseline and remain in the outbox. They can be appended now that the seed is durable.
      const drain = await this.drainOutbox()
      if (generation !== this.connectionGeneration || drain !== 'drained') return
      if (this.isSnapshotter && this.projectedRevision !== this.documentRevision) this.scheduleSnapshot()
      this.presence.announceJoin() // let peers see our caret now that we're live
      return
    }

    // Hydrate only after the complete batch passed parsing and isolated rehearsal.
    if (res.canWrite) this.notify(() => this.opts.onHydrating?.())
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
    this.applyingHydration = true
    try {
      if (!unchanged) Y.applyUpdate(this.opts.ydoc, serverState, this)
    } catch (error) { this.fail(error); return }
    finally { this.applyingHydration = false }
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
    this.incorporatedSeq = hydratedSequence
    this.recoveryCapture?.markDirty()
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
    // A synchronous host observer can dispatch another delivery during publication.
    // Account for it only after the captured baseline has become the live sequence.
    const duringHydration = this.beforeSchemaUpdates.splice(0)
    for (const item of duringHydration) {
      this.onReceiveUpdate(item.update, item.seq)
      if (generation !== this.connectionGeneration || this.destroyed || !this.connected) return
      serverUpdates.push(this.decodeRemoteUpdate(item.update))
    }
    if (this.destroyed) return
    if (!this.schemaAccepted) throw new Error('An update sequence gap requires a fresh document hydration.')

    let diff: Uint8Array | null = null
    if (!fresh && !unchanged) {
      if (duringHydration.length) serverState = Y.mergeUpdates(serverUpdates)
      // Back-sync: compute the server's state vector client-side and send any local-only diff
      // (covers offline edits accumulated while disconnected).
      this.reconcilePending(serverState)
      if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.schemaAccepted) return

      // Pending updates already cover most ordinary offline edits. Compute the residual against
      // server + outbox so a Y.Doc supplied with pre-existing local state is also back-synced once.
      const knownUpdates = serverUpdates.concat(this.pendingUpdates.map(item => item.update))
      const knownState = knownUpdates.length ? Y.mergeUpdates(knownUpdates) : new Uint8Array([0, 0])
      const knownStateVector = Y.encodeStateVectorFromUpdate(knownState)
      const candidate = Y.encodeStateAsUpdate(this.opts.ydoc, knownStateVector)
      if (hasMissingUpdateContent(candidate, knownState)) diff = candidate
    }
    const hasResidual = diff !== null

    if (!res.canWrite && (this.pendingUpdates.length > 0 || hasResidual)) {
      throw new Error('[READ_ONLY] Local changes cannot be merged into a read-only document.')
    }

    // The server already holds exactly this state (its snapshot, nothing after it, nothing of ours to send): the snapshot that
    // follows a join need not upload it again (~40 KB on every open, 3.6 perf). Any change from here on moves the revision.
    this.serverRevision = snapshotOnly && duringHydration.length === 0 && !diff && this.pendingUpdates.length === 0 ? this.documentRevision : -1
    // Arm the pump BEFORE sending back-sync — synchronous, so no local edit can interleave and
    // be lost between "compute diff" and "pump live".
    this.joined = true
    if (diff) this.enqueueUpdate(diff)
    const drain = await this.drainOutbox()
    if (generation !== this.connectionGeneration || drain !== 'drained') return
    if (this.isSnapshotter) this.scheduleSnapshot()
    this.presence.announceJoin() // (re)announce presence after a fresh join / reconnect
  }

  private reconcilePending(serverState: Uint8Array): void {
    const before = this.pendingUpdates.length
    const coverage = createUpdateCoverage(serverState)
    const missing = this.pendingUpdates.filter(item =>
      coverage.hasMissing(item.update))
    if (missing.length === before) return
    this.pendingUpdates.splice(0, this.pendingUpdates.length, ...missing)
    this.recalculatePendingMetrics()
    this.notifyPendingUpdates()
  }

  /**
   * Seed a fresh shared doc from the readable file (this connection is the elected seeding
   * snapshotter). y-tiptap's ySync plugin runs a one-time Y→ProseMirror `_forceRerender` inside its
   * plugin `view()` when the EditorView mounts; that reflects the (empty) Y.Doc and would clobber a
   * setContent that lands before it — leaving an empty doc that then flushes an empty projection
   * over notes.md (data loss). So WAIT until that initial sync has run (the binding has a live
   * ProseMirror view); from that point setContent is bidirectional (PM→Y) and sticks. Then run the
   * host's one-shot post-seed hook (issue/contact upgraders) so those rewrites ride the initial
   * snapshot on this single writer rather than conflicting across every client.
   */
  private async seedInitialContent(content: string | JSONContent, generation: number): Promise<void> {
    while (generation === this.connectionGeneration && !this.destroyed && this.connected) {
      if (!this.editor) await new Promise<void>(resolve => { this.waitingForEditor = resolve })
      else if (this.isYSyncViewMounted()) break
      else await delay(16)
    }
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.editor) return
    // The editor can become interactive while the initial connection is still in flight. Preserve
    // those local nodes when installing the server's readable seed; setContent alone would replace
    // them even though their Yjs updates are queued for durability.
    const localContent = this.pendingUpdates.length > 0 || !this.editor.isEmpty
      ? this.editor.getJSON().content ?? []
      : []
    // The server baseline (including seed upgrades) is not a local user's undo step.
    this.opts.ydoc.transact(() => {
      // A stored 2.x shape the schema refuses is repaired before y-prosemirror builds it unchecked.
      // preventAutolink: opening a page must not link a bare URL the stored text has (the seed is no one's typing).
      this.editor!.chain().setMeta('preventAutolink', true).setContent(typeof content === 'string' ? content : repairLegacyDocument(content), { emitUpdate: false }).run()
      if (localContent.length > 0) {
        this.editor!.commands.insertContentAt(this.editor!.state.doc.content.size, localContent, {
          updateSelection: false,
        })
      }
      if (!this.destroyed && this.editor && this.opts.onSeeded) {
        try { this.opts.onSeeded(this.editor) }
        catch (error) { throw new SeedTransformError(error) }
      }
    }, this)
  }

  /**
   * True once y-tiptap's ySync binding has mounted its ProseMirror view — i.e. the initial,
   * clobbering Y→PM sync has already run, so a setContent now holds. Detected via the plugin state
   * (rather than importing `ySyncPluginKey`) to avoid a duplicate-module PluginKey mismatch.
   */
  private isYSyncViewMounted(): boolean {
    const editor = this.editor
    if (!editor || !editor.view) return false
    for (const plugin of editor.state.plugins) {
      const state = plugin.getState?.(editor.state) as { binding?: { prosemirrorView?: unknown } } | undefined
      if (state && typeof state === 'object' && 'binding' in state) return !!state.binding?.prosemirrorView
    }
    return false
  }

  // ---- snapshotter duty ----

  private scheduleSnapshot(): void {
    if (this.destroyed || !this.editor || this.writable === false) return
    this.snapshotSchedule.schedule()
  }

  private async pushSnapshot(retryOnFailure = true, initialBaseline = false): Promise<boolean> {
    if (this.snapshotPush) return this.snapshotPush
    let resolveOperation!: (result: boolean) => void
    let rejectOperation!: (error: unknown) => void
    const operation = new Promise<boolean>((resolve, reject) => {
      resolveOperation = resolve
      rejectOperation = reject
    })
    this.snapshotPush = operation
    void this.performSnapshotPush(retryOnFailure, initialBaseline).then(resolveOperation, rejectOperation)
    try {
      return await operation
    } finally {
      if (this.snapshotPush === operation) this.snapshotPush = null
    }
  }

  /**
   * The content migrations (repairLegacyDocument) for a live document that predates them: its shared state never passes the
   * readable decode or the seed, so the one elected snapshotter rewrites it once, as a change no one's undo reverts. Peers see the
   * same result, and the next snapshot finds nothing left to do.
   */
  private migrateLiveContent(): void {
    if (!this.editor || !this.isYSyncViewMounted()) return
    const current = this.editor.getJSON()
    const migrated = repairLegacyDocument(current)
    if (migrated !== current) this.editor.chain().setMeta('addToHistory', false).setContent(migrated, { emitUpdate: false }).run()
  }

  private async performSnapshotPush(retryOnFailure: boolean, initialBaseline: boolean): Promise<boolean> {
    const generation = this.connectionGeneration
    this.snapshotSchedule.clearSchedule()
    if (this.destroyed || this.writable === false || !this.isSnapshotter || !this.joined || !this.connected || !this.editor) return false
    if (!initialBaseline) this.migrateLiveContent()
    // Unchanged since the join found the server's snapshot holding this state (a migration above, a remote or a local edit moves the
    // revision): the stored snapshot and its projections stand.
    if (!initialBaseline && !this.snapshotAsked && this.documentRevision === this.serverRevision && this.pendingUpdates.length === 0) {
      this.projectedRevision = this.documentRevision
      this.notify(() => this.opts.onProjectionStatus?.('current'))
      return true
    }
    // The projection is taken before any wait on the network, while the editor surely lives: a closing tab or an editor leaving
    // (detachEditor flushes) still sends it. What arrives meanwhile is projected again below, by the editor if it is still there.
    // A server that derives the projections gets the state alone: rendering the whole page after every batch of changes was the
    // snapshotter's main cost, and its Markdown, JSON and text tripled the upload.
    const derived = this.projectionsDerived
    const project = (editor: Editor) => {
      if (derived) return { markdown: '', rich: { document: { type: 'doc', content: [] } as JSONContent }, text: '' }
      try { return projectEditor(editor) } catch { return null }
    }
    let projectedRevision = this.documentRevision
    let projection = project(this.editor)
    if (!initialBaseline) await this.drainOutbox()
    if (generation !== this.connectionGeneration || this.destroyed || (!initialBaseline && this.pendingUpdates.length > 0) || !this.connected || !this.joined || !this.isSnapshotter) return false
    if (this.documentRevision !== projectedRevision) {
      if (!this.editor) return false
      projectedRevision = this.documentRevision
      projection = project(this.editor)
    }

    // Snapshot the exact prefix represented by this state. New edits may arrive while hashing and
    // writing; only this captured prefix is discharged after a successful initial baseline.
    const includedPending = initialBaseline ? this.pendingUpdates.slice() : []
    // Freeze the captured prefix. enqueueUpdate mutates the latest unattempted item when it
    // coalesces edits; without sealing these objects, an edit arriving during PushSnapshot would
    // be merged into the captured item and incorrectly acknowledged by an older snapshot.
    const newlyAttempted = includedPending.some(item => !item.attempted)
    for (const item of includedPending) item.attempted = true
    if (newlyAttempted && !this.markRecoveryDirty(generation)) return false
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.joined || !this.isSnapshotter) return false
    const sourceSequence = this.incorporatedSeq
    const capturedRevision = projectedRevision
    const state = Y.encodeStateAsUpdate(this.opts.ydoc)
    if (!projection) {
      this.notify(() => this.opts.onProjectionStatus?.('degraded'))
      if (retryOnFailure) this.snapshotSchedule.retry()
      return false
    }
    let stateSha256: string
    try { stateSha256 = await sha256Hex(state) }
    catch {
      if (generation !== this.connectionGeneration || this.destroyed) return false
      this.notify(() => this.opts.onProjectionStatus?.('degraded'))
      if (retryOnFailure) this.snapshotSchedule.retry()
      return false
    }
    const projections = {
      sourceSequence,
      stateSha256,
      markdown: projection.markdown,
      editorJson: projection.rich.document,
      plainText: projection.text,
    }
    if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.joined || !this.isSnapshotter) return false
    try {
      const compressed = derived ? await gzip(state) : null
      if (generation !== this.connectionGeneration || this.destroyed || !this.connected || !this.joined || !this.isSnapshotter) return false
      if (compressed) await this.conn.invoke('PushSnapshotGzip', this.opts.documentId, sourceSequence, u8ToB64(compressed), projections)
      else await this.conn.invoke('PushSnapshot', this.opts.documentId, sourceSequence, u8ToB64(state), projections)
      if (generation !== this.connectionGeneration || this.destroyed) return false
      if (initialBaseline && includedPending.length > 0) this.acknowledgeSnapshotUpdates(includedPending)
      this.snapshotAsked = false
      this.projectedRevision = capturedRevision
      const current = this.documentRevision === capturedRevision && this.incorporatedSeq === sourceSequence && this.pendingUpdates.length === 0
      // The server stores the adopted snapshot with its next flush (SnapshotStored); a save before that is refused as pending.
      const stored = initialBaseline || this.storedSequence >= sourceSequence
      this.storedWait = current && !stored ? { sequence: sourceSequence, revision: capturedRevision } : null
      if (!this.destroyed) this.notify(() => this.opts.onProjectionStatus?.(current && stored ? 'current' : 'pending'))
      this.snapshotSchedule.clearRetry()
      // A timer firing during this RPC merely joins its promise. Re-arm work for a
      // newer acknowledged revision once that older snapshot has completed.
      if (!initialBaseline && !current) this.scheduleSnapshot()
      return true
    } catch (error) {
      if (generation !== this.connectionGeneration || this.destroyed) return false
      if (isAuthorizationError(error) || isInvalidContentError(error)) { this.fail(error); return false }
      // The role moved to a colleague who writes (SnapshotterChanged) while this push was on its way: theirs follows.
      if (!this.isSnapshotter) return false
      if (!this.destroyed) this.notify(() => this.opts.onProjectionStatus?.('degraded'))
      if (retryOnFailure) this.snapshotSchedule.retry()
      return false
    }
  }

  private acknowledgeSnapshotUpdates(included: readonly PendingUpdate[]): void {
    for (const item of included) {
      if (this.pendingUpdates[0] !== item) break
      this.removePendingHead(item, false)
    }
    this.notifyPendingUpdates()
  }

  private decodeRemoteUpdate(value: string | Uint8Array): Uint8Array {
    try {
      if (typeof value !== 'string' && !(value instanceof Uint8Array)) throw new Error('Expected a base64 string.')
      const bytes = typeof value === 'string' ? b64ToU8(value) : value
      Y.decodeUpdate(bytes)
      return bytes
    } catch (cause) { throw new ProtocolError('The server sent an invalid Yjs update.', { cause }) }
  }

  private async inflateRemoteUpdate(value: string): Promise<Uint8Array> {
    return this.decodeRemoteUpdate(await this.inflate(value, 'snapshot'))
  }

  private async inflate(value: string, what: string): Promise<Uint8Array> {
    try { return new Uint8Array(await new Response(new Response(b64ToU8(value) as Uint8Array<ArrayBuffer>).body!.pipeThrough(new DecompressionStream('gzip'))).arrayBuffer()) }
    catch (cause) { throw new ProtocolError(`The server sent an invalid compressed ${what}.`, { cause }) }
  }

  private removeWindowListeners(): void {
    this.eventWindow?.removeEventListener('beforeunload', this.onBeforeUnload)
    this.eventWindow?.document.removeEventListener('visibilitychange', this.onVisibilityChange)
    this.eventWindow?.removeEventListener('offline', this.onBrowserOffline)
    this.eventWindow?.removeEventListener('online', this.onBrowserOnline)
  }

  private onBrowserOffline = (): void => {
    if (this.destroyed || this.browserOffline || this.opts.allowOfflineEditing !== false) return
    this.browserConnectionRevision++
    const generation = ++this.connectionGeneration
    this.waitingForEditor?.()
    this.waitingForEditor = null
    this.browserOffline = true
    try { this.presence.announceLeave() } catch { /* advisory only */ }
    this.connected = false
    this.joined = false
    this.schemaAccepted = false
    this.isSnapshotter = false
    this.beforeSchemaUpdates.length = 0
    this.snapshotSchedule.clearSchedule()
    this.snapshotSchedule.clearRetry()
    this.clearOutboxRetry()
    if (!this.markRecoveryDirty(generation)) return
    this.setSynced(false)
    this.presence.clearPeers()
    this.setStatus('offline')
    void this.stopBrowserTransport()
  }

  private onBrowserOnline = (): void => {
    if (this.destroyed || !this.browserOffline || this.opts.allowOfflineEditing !== false) return
    this.browserOffline = false
    void this.resumeAfterBrowserOnline()
  }

  private async resumeAfterBrowserOnline(): Promise<void> {
    const stopping = this.browserStop
    if (stopping) await stopping
    const connecting = this.connectionOperation
    if (connecting) await connecting
    if (this.destroyed || this.browserOffline || (this.connected && this.joined)) return
    this.setStatus('reconnecting')
    await this.ensureConnected()
  }

  private stopBrowserTransport(): Promise<void> {
    let operation: Promise<void>
    try { operation = this.conn.stop().catch(() => {}) }
    catch { operation = Promise.resolve() }
    this.browserStop = operation
    void operation.finally(() => { if (this.browserStop === operation) this.browserStop = null })
    return operation
  }

  private onBeforeUnload = (): void => {
    // Tab close / F5 don't fire Vue's onUnmounted, so destroy()/announceLeave() never run.
    // Best-effort awareness removal here so peers drop our caret + presence chip immediately
    // instead of waiting out the 30 s stale-peer sweep (matches y-websocket's pagehide leave).
    if (this.joined) this.presence.announceLeave()
    if (this.isSnapshotter && this.joined) void this.pushSnapshot()
  }

  /** A snapshot waiting for the user to rest is sent when the page is hidden: the tab may be closed or frozen next. */
  private onVisibilityChange = (): void => {
    if (this.eventWindow?.document.visibilityState === 'hidden') this.snapshotSchedule.flush()
  }

  private removePendingHead(item: PendingUpdate, notify = true): void {
    if (this.pendingUpdates[0] !== item) return
    this.pendingUpdates.shift()
    this.pendingBytes -= item.update.byteLength
    this.pendingUpdateCount -= item.updateCount
    if (notify) this.notifyPendingUpdates()
  }

  private recalculatePendingMetrics(): void {
    this.pendingBytes = this.pendingUpdates.reduce((sum, item) => sum + item.update.byteLength, 0)
    this.pendingUpdateCount = this.pendingUpdates.reduce((sum, item) => sum + item.updateCount, 0)
  }

  private notifyPendingUpdates(): void {
    this.recoveryCapture?.markDirty()
    this.notify(() => this.opts.onPendingUpdates?.(this.pendingUpdateCount))
  }

  /** Every changed recovery field invalidates the checkpoint, even without a document edit. */
  private markRecoveryDirty(generation: number): boolean {
    this.recoveryCapture?.markDirty()
    return generation === this.connectionGeneration && !this.destroyed
  }

  private notify(callback: (() => void) | undefined): void {
    if (!callback) return
    try { callback() }
    catch (error) {
      try { this.opts.onCallbackError?.(error) } catch { /* callback diagnostics must remain observational */ }
    }
  }

  private setStatus(status: ProviderStatus): void { this.notify(() => this.opts.onStatus?.(status)) }
  private setSynced(synced: boolean): void { this.notify(() => this.opts.onSynced?.(synced)) }
  private setWritable(writable: boolean): void {
    if (this.writable === writable) return
    this.writable = writable
    this.notify(() => this.opts.onWritable?.(writable))
  }
  private fail(error: unknown): void {
    if (this.destroyed || this.failing) return
    this.failing = true
    this.setSynced(false)
    // Edits the server never acknowledged (a refused update and the valid ones batched with or after it) stay recoverable.
    this.recoveryCapture?.seal()
    this.notify(() => this.opts.onFatalError?.(error instanceof Error ? error : new Error(String(error))))
    this.destroy()
  }
}

/** Gzip in the browser; null where CompressionStream is missing (the caller sends the state raw). */
async function gzip(bytes: Uint8Array): Promise<Uint8Array | null> {
  if (typeof CompressionStream !== 'function') return null
  return new Uint8Array(await new Response(new Response(bytes as Uint8Array<ArrayBuffer>).body!.pipeThrough(new CompressionStream('gzip'))).arrayBuffer())
}

function sameBytes(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false
  for (let index = 0; index < a.length; index++) if (a[index] !== b[index]) return false
  return true
}
