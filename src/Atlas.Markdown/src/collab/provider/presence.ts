import { Awareness, applyAwarenessUpdate, encodeAwarenessUpdate, removeAwarenessStates } from 'y-protocols/awareness'
import { b64ToU8, u8ToB64 } from './protocol'
import { collaborationStrings } from '../../ui/strings/collaborationAddons'
import type { MarkdownSyncConnection } from './transport'

/** A remote collaborator surfaced from awareness (drives PresenceStack). */
export interface PresenceUser {
  clientId: number
  id?: string
  name: string
  color: string
}

interface PresenceSessionOptions {
  readonly connection: MarkdownSyncConnection
  readonly documentId: string
  readonly isConnected: () => boolean
  readonly isJoined: () => boolean
  readonly onPeers?: (peers: PresenceUser[]) => void
  /** V2 server binds one awareness identity to each authenticated connection. */
  readonly ownStateOnly?: boolean
}

// Same liveness windows as y-websocket: local state every 15s, expire peers after 30s.
const HEARTBEAT_MS = 15000
const TIMEOUT_MS = 30000
const SWEEP_MS = 5000
const REMOTE_ORIGIN = 'remote'
// The server relays at most 30 awareness packets a second per connection; a caret and a pointer moving together share one every 50 ms.
const SEND_MS = 50

/** Presence is advisory and owns its listeners/timers independently of document durability. */
export class PresenceSession {
  private stopped = false
  private localStateBeforeLeave: ReturnType<Awareness['getLocalState']> = null
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null
  private sweepTimer: ReturnType<typeof setInterval> | null = null
  private publishedPeers = '[]'
  private readonly pending = new Set<number>()
  private pendingTimer: ReturnType<typeof setTimeout> | null = null
  private lastSent = -Infinity

  constructor(readonly awareness: Awareness, private readonly options: PresenceSessionOptions) {
    awareness.on('update', this.onUpdate)
  }

  receive(payload: string): void {
    if (this.stopped || !this.options.isConnected() || !this.options.isJoined()) return
    const known = new Set(this.awareness.getStates().keys())
    applyAwarenessUpdate(this.awareness, b64ToU8(payload), REMOTE_ORIGIN)
    // Returning clients retain their awareness clock, so they may arrive as updated rather than added.
    if ([...this.awareness.getStates().keys()].some(id => id !== this.awareness.clientID && !known.has(id))) this.announceLocal()
  }

  /** Re-establish identity and solicit fresh peer replies using the existing awareness messages. */
  announceJoin(): void {
    if (this.stopped || !this.options.isConnected() || !this.options.isJoined()) return
    const state = this.awareness.getLocalState() ?? this.localStateBeforeLeave
    this.clearPeers()
    if (state == null) return
    // The offline leave may have been lost. A null/live pair also lets peers that still know us reply.
    this.awareness.setLocalState(null)
    this.awareness.setLocalState(state)
    this.flush()
    this.localStateBeforeLeave = null
  }

  /** Broadcast the local state on join, reconnect, heartbeat, or discovery of a newcomer. */
  announceLocal(): void {
    if (this.stopped || !this.options.isConnected() || !this.options.isJoined()) return
    const state = this.awareness.getLocalState()
    if (state == null) return
    // Advance the clock even when identity/cursor content has not changed.
    this.awareness.setLocalState(state)
    // An answer to a newcomer and the heartbeat do not wait behind a move.
    this.flush()
  }

  /** Send the removal before the transport closes, even after listeners have been stopped. */
  announceLeave(): void {
    this.localStateBeforeLeave = this.awareness.getLocalState() ?? this.localStateBeforeLeave
    if (!this.options.isConnected()) return
    removeAwarenessStates(this.awareness, [this.awareness.clientID], 'destroy')
    this.options.connection.send('SendAwareness', this.options.documentId,
      u8ToB64(encodeAwarenessUpdate(this.awareness, [this.awareness.clientID]))).catch(() => { /* best-effort */ })
  }

  start(): void {
    if (this.stopped) return
    this.heartbeatTimer ??= setInterval(() => this.announceLocal(), HEARTBEAT_MS)
    this.sweepTimer ??= setInterval(() => this.sweepStalePeers(), SWEEP_MS)
  }

  /** Stop callbacks synchronously; the provider sends leave and then closes its transport. */
  stop(): void {
    this.stopped = true
    if (this.pendingTimer) { clearTimeout(this.pendingTimer); this.pendingTimer = null }
    this.pending.clear()
    if (this.heartbeatTimer) { clearInterval(this.heartbeatTimer); this.heartbeatTimer = null }
    if (this.sweepTimer) { clearInterval(this.sweepTimer); this.sweepTimer = null }
    this.awareness.off('update', this.onUpdate)
  }

  /** Final phase, after the transport has stopped. */
  destroy(): void { this.awareness.destroy() }

  clearPeers(): void {
    const remote = [...this.awareness.getStates().keys()].filter(id => id !== this.awareness.clientID)
    // Keep clocks to reject stale packets, but discard cached peers and caret positions while disconnected.
    removeAwarenessStates(this.awareness, remote, REMOTE_ORIGIN)
    this.publishedPeers = '[]'
    this.options.onPeers?.([])
  }

  private onUpdate = (changes: { added: number[]; updated: number[]; removed: number[] }, origin: unknown): void => {
    if (this.stopped) return
    // Awareness emits update (but not change) for fresh heartbeats with unchanged user/cursor content.
    const changed = changes.added.concat(changes.updated, changes.removed)
    if (changed.some(id => id !== this.awareness.clientID)) this.emitPeers()
    if (origin === REMOTE_ORIGIN) return
    const outgoing = changed.filter(id => !this.options.ownStateOnly || id === this.awareness.clientID)
    if (!outgoing.length) return
    outgoing.forEach(id => this.pending.add(id))
    // An arrival or a leave goes at once (the null/live pair of announceJoin must reach peers as two packets); a move may wait.
    const wait = this.lastSent + SEND_MS - Date.now()
    if (changes.added.length || changes.removed.length || wait <= 0) this.flush()
    else this.pendingTimer ??= setTimeout(this.flush, wait)
  }
  private flush = (): void => {
    if (this.pendingTimer) { clearTimeout(this.pendingTimer); this.pendingTimer = null }
    if (this.stopped || !this.pending.size) return
    const ids = [...this.pending]
    this.pending.clear()
    this.lastSent = Date.now()
    this.send(encodeAwarenessUpdate(this.awareness, ids))
  }
  private send(payload: Uint8Array): void {
    if (!this.options.isJoined() || !this.options.isConnected()) return
    this.options.connection.send('SendAwareness', this.options.documentId, u8ToB64(payload)).catch(() => { /* best-effort */ })
  }

  private sweepStalePeers(): void {
    const now = Date.now()
    const stale: number[] = []
    for (const clientId of this.awareness.getStates().keys()) {
      if (clientId === this.awareness.clientID) continue
      const meta = this.awareness.meta.get(clientId)
      if (meta && now - meta.lastUpdated > TIMEOUT_MS) stale.push(clientId)
    }
    // Local origin relays the removal; peers that already expired it ignore it safely.
    if (stale.length > 0) removeAwarenessStates(this.awareness, stale, 'timeout')
  }

  private emitPeers(): void {
    if (!this.options.onPeers) return
    const peers: PresenceUser[] = []
    for (const [clientId, state] of this.awareness.getStates()) {
      if (clientId === this.awareness.clientID) continue
      const user = (state as { user?: { id?: string; name?: string; color?: string } }).user
      if (!user) continue
      peers.push({ clientId, id: user.id, name: user.name ?? collaborationStrings.anonymousUser, color: user.color ?? '#888888' })
    }
    // A caret move or a heartbeat leaves the list as it was: republishing it re-rendered the avatars on every remote keystroke.
    const published = JSON.stringify(peers)
    if (published === this.publishedPeers) return
    this.publishedPeers = published
    this.options.onPeers(peers)
  }
}
