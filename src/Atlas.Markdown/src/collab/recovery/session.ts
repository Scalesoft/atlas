import { newUpdateId } from '../provider/protocol'
import { normalizeRecoveryScope, summarizeRecoveryCheckpoint } from './indexedDbRecoveryStore'
import type { RecoveryCheckpoint, RecoveryScope, RecoveryStore, RecoverySummary } from './types'

export interface MarkdownRecoveryState {
  readonly status: 'idle' | 'recording' | 'recorded' | 'degraded' | 'closed'
  readonly branchId: string
  readonly dirty: boolean
  readonly lastRecorded: RecoverySummary | null
  readonly error: Error | null
}

/** A manual recovery branch, never a transport outbox or permission to replay cached updates. */
export interface MarkdownRecovery {
  getState(): MarkdownRecoveryState
  subscribe(listener: (state: MarkdownRecoveryState) => void): () => void
  /** Record at least the state requested by this call, after the current synchronous edit finishes. */
  checkpoint(): Promise<RecoverySummary>
}

export interface MarkdownRecoveryOptions {
  readonly store: RecoveryStore
  /** Authenticated host identity, independent of presence names and access tokens. */
  readonly scope: RecoveryScope
  /** Debounce for periodic checkpoints; these do not promise durability of every keystroke. */
  readonly checkpointDelayMs?: number
  readonly maxWaitMs?: number
}

type CapturedState = Omit<RecoveryCheckpoint, 'formatVersion' | 'scope' | 'branchId' | 'revision' | 'capturedAt' | 'epoch' | 'replayPolicy'>
export interface RecoveryCapturePort {
  markDirty(): void
  detach(): void
  /** Captures what is not recorded yet (a session that ends with unsent edits), writes it and detaches. */
  seal(): void
}
const sessions = new WeakMap<MarkdownRecovery, RecoverySession>()

/** No database is opened and no document is captured until attached to a provider. */
export function createMarkdownRecovery(options: MarkdownRecoveryOptions): MarkdownRecovery {
  const session = new RecoverySession(options)
  const handle: MarkdownRecovery = Object.freeze({
    getState: () => session.state,
    subscribe: (listener: (state: MarkdownRecoveryState) => void) => session.subscribe(listener),
    checkpoint: () => session.checkpoint(),
  })
  sessions.set(handle, session)
  return handle
}

export function assertRecoveryBinding(handle: MarkdownRecovery, documentId: string): void {
  const session = sessions.get(handle)
  if (!session || session.scope.documentId !== documentId || session.attached) {
    throw new TypeError('Recovery requires a fresh handle scoped to this document.')
  }
}

/** Internal provider boundary. One handle can be attached only once, including after teardown. */
export function attachRecovery(handle: MarkdownRecovery, documentId: string, capture: () => CapturedState): RecoveryCapturePort {
  assertRecoveryBinding(handle, documentId)
  const session = sessions.get(handle)!
  session.attach(capture)
  return { markDirty: () => session.markDirty(), detach: () => session.detach(), seal: () => session.seal() }
}

class RecoverySession {
  readonly scope: RecoveryScope
  readonly branchId = newUpdateId()
  attached = false
  state: MarkdownRecoveryState
  private readonly store: RecoveryStore
  private readonly delayMs: number
  private readonly maxWaitMs: number
  private readonly listeners = new Set<(state: MarkdownRecoveryState) => void>()
  private source: (() => CapturedState) | null = null
  private closed = false
  private dirtyVersion = 0
  private recordedVersion = -1
  private checkpointRevision = 0
  private writing: Promise<RecoverySummary> | null = null
  private debounce: ReturnType<typeof setTimeout> | null = null
  private deadline: ReturnType<typeof setTimeout> | null = null

  constructor(options: MarkdownRecoveryOptions) {
    if (!options?.store || typeof options.store.write !== 'function') throw new TypeError('Recovery requires a store.')
    this.store = options.store
    this.scope = normalizeRecoveryScope(options.scope)
    this.delayMs = options.checkpointDelayMs ?? 1000
    this.maxWaitMs = options.maxWaitMs ?? 5000
    if (!Number.isSafeInteger(this.delayMs) || this.delayMs < 1 || this.delayMs > 60_000
      || !Number.isSafeInteger(this.maxWaitMs) || this.maxWaitMs < this.delayMs || this.maxWaitMs > 60_000) {
      throw new TypeError('Recovery checkpoint timing must be integers with 1 <= delay <= maxWait <= 60000.')
    }
    this.state = Object.freeze({ status: 'idle', branchId: this.branchId, dirty: false, lastRecorded: null, error: null })
  }

  subscribe(listener: (state: MarkdownRecoveryState) => void): () => void {
    if (typeof listener !== 'function') throw new TypeError('Recovery subscriber must be a function.')
    this.listeners.add(listener)
    this.notify(listener)
    return () => { this.listeners.delete(listener) }
  }

  attach(capture: () => CapturedState): void {
    this.attached = true
    this.source = capture
    this.markDirty()
  }

  markDirty(): void {
    if (this.closed || !this.source) return
    this.dirtyVersion++
    this.publish(this.writing ? 'recording' : this.state.status === 'degraded' ? 'degraded' : 'idle')
    if (this.closed) return // A host observer may destroy the provider synchronously.
    if (this.debounce) clearTimeout(this.debounce)
    this.debounce = setTimeout(this.automaticCheckpoint, this.delayMs)
    this.deadline ??= setTimeout(this.automaticCheckpoint, this.maxWaitMs)
  }

  async checkpoint(): Promise<RecoverySummary> {
    const target = this.dirtyVersion
    // Even a call from a Yjs/projection observer must capture after enqueue/ACK bookkeeping.
    await Promise.resolve()
    while (this.recordedVersion < target) {
      if (!this.writing) {
        if (this.closed || !this.source) throw new Error('Recovery is not attached to a live document.')
        this.clearTimers()
        const version = this.dirtyVersion
        // Install the promise before capture/state observers can request another checkpoint.
        const operation = Promise.resolve().then(() => this.captureAndWrite(version))
        this.writing = operation
        void operation.then(() => this.finish(operation), () => this.finish(operation))
      }
      await this.writing
    }
    if (!this.state.lastRecorded) throw new Error('No recovery checkpoint has been recorded.')
    return this.state.lastRecorded
  }

  seal(): void {
    if (this.closed || !this.source) return
    if (this.recordedVersion < this.dirtyVersion) {
      this.clearTimers()
      // Captured now, while the provider still holds its outbox; written after any write in flight.
      const version = this.dirtyVersion, captured = this.source()
      const operation = (this.writing ?? Promise.resolve()).catch(() => undefined).then(() => this.captureAndWrite(version, captured))
      this.writing = operation
      void operation.then(() => this.finish(operation), () => this.finish(operation))
    }
    this.detach()
  }

  detach(): void {
    if (this.closed) return
    this.closed = true
    this.source = null
    this.clearTimers()
    this.publish('closed')
  }

  private async captureAndWrite(version: number, sealed?: CapturedState): Promise<RecoverySummary> {
    try {
      if (!sealed && (this.closed || !this.source)) throw new Error('Recovery capture was closed before it started.')
      const captured = sealed ?? this.source!()
      const checkpoint: RecoveryCheckpoint = {
        ...captured, formatVersion: 1, scope: this.scope, branchId: this.branchId,
        revision: ++this.checkpointRevision, capturedAt: Date.now(), epoch: null, replayPolicy: 'manual-only',
      }
      // Provider capture already owns its bytes. Metadata summary is captured before handing
      // the independent checkpoint to a host store that may retain or mutate its argument.
      const summary = Object.freeze(summarizeRecoveryCheckpoint(checkpoint))
      this.publish('recording')
      await this.store.write(checkpoint)
      this.recordedVersion = version
      this.publish(this.closed ? 'closed' : 'recorded', summary, null)
      return summary
    } catch (cause) {
      const error = cause instanceof Error ? cause : new Error(String(cause))
      this.publish(this.closed ? 'closed' : 'degraded', this.state.lastRecorded, error)
      throw error
    }
  }

  private finish(operation: Promise<RecoverySummary>): void {
    if (this.writing !== operation) return
    this.writing = null
    // A newer edit already scheduled its deadline. Failed storage is retried explicitly
    // or after another edit, rather than spinning over the same failed checkpoint.
  }

  private automaticCheckpoint = (): void => {
    this.clearTimers()
    void this.checkpoint().catch(() => { /* state exposes the failure; last good checkpoint remains */ })
  }

  private clearTimers(): void {
    if (this.debounce) clearTimeout(this.debounce)
    if (this.deadline) clearTimeout(this.deadline)
    this.debounce = null
    this.deadline = null
  }

  private publish(status: MarkdownRecoveryState['status'], lastRecorded = this.state.lastRecorded, error = this.state.error): void {
    this.state = Object.freeze({ status, branchId: this.branchId, dirty: this.recordedVersion < this.dirtyVersion, lastRecorded, error })
    for (const listener of [...this.listeners]) this.notify(listener)
  }

  private notify(listener: (state: MarkdownRecoveryState) => void): void {
    try { listener(this.state) } catch { /* observer failures cannot change local durability */ }
  }
}
