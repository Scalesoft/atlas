import { watch } from 'vue'
import { registerCollaborationAdapter } from '../core/internal'
import { collaborationStrings } from '../ui/strings/collaborationAddons'
import type { MarkdownCollaborationAdapter, MarkdownEditorState } from '../core/types'
import { useCollab, type CollabUser } from './useCollab'
import { createLinkQuality } from './linkQuality'
import { isInvalidContentError } from './provider/protocol'
import { SignalRYjsProvider } from './SignalRYjsProvider'
import { DurableSignalRYjsProvider, type DurableCollaborationOptions } from './DurableSignalRYjsProvider'
import type { MarkdownRecovery } from './recovery/session'
import { registerJournalCapability } from './recovery/journalCapability'

export interface SignalRMarkdownAdapterOptions {
  readonly hubUrl: string
  readonly documentId: string
  readonly presence: Readonly<CollabUser>
  readonly accessToken?: () => string | Promise<string>
  /** Separate, manual-only local recovery status; does not change server save status. */
  readonly recovery?: MarkdownRecovery
  /** Opt in to V2; unsupported servers fail closed without a V1 fallback. */
  readonly durable?: DurableCollaborationOptions
  /** Opt in to unconfirmed offline edits after the initial document has loaded. Defaults to false. */
  readonly allowOfflineEditing?: boolean
  /**
   * The server's state of the document (one Yjs update, the page bundle's): the editor is ready and, with `initialWritable`, takes
   * input at once; the transport joins behind it and its edits queue in the Y.Doc until then (see useCollab).
   */
  readonly initialState?: Uint8Array
  readonly initialWritable?: boolean
  /** A reader of a live page: light presence without carets (see useCollab). */
  readonly reader?: boolean
}

/** One opaque handle owns one document session; construction never opens a connection. */
export function createSignalRMarkdownAdapter(options: SignalRMarkdownAdapterOptions): MarkdownCollaborationAdapter {
  if (!options.hubUrl?.trim() || !options.documentId?.trim() || !options.presence?.id?.trim() || !options.presence.name?.trim()) {
    throw new TypeError('Collaboration requires a hub URL, document id and presence identity.')
  }
  if (options.accessToken !== undefined && typeof options.accessToken !== 'function') throw new TypeError('accessToken must be a token factory.')
  if (options.allowOfflineEditing !== undefined && typeof options.allowOfflineEditing !== 'boolean') throw new TypeError('allowOfflineEditing must be a boolean.')
  if (options.reader !== undefined && typeof options.reader !== 'boolean') throw new TypeError('reader must be a boolean.')
  const config = { ...options, presence: { ...options.presence } }
  const adapter = registerCollaborationAdapter((publish, schema) => {
    const session = useCollab({ ...config, allowOfflineEditing: config.allowOfflineEditing === true, schema, user: config.presence,
      onHydrating: () => { if (!hydrated) publish({ hydrating: true }) } })
    const unregisterJournal = session.flushLocal ? registerJournalCapability(adapter, { flushLocal: session.flushLocal }) : () => {}
    let remoteTransaction = false
    const trackOrigin = (transaction: { origin: unknown }) => { remoteTransaction = transaction.origin instanceof SignalRYjsProvider || transaction.origin instanceof DurableSignalRYjsProvider }
    session.ydoc.on('beforeTransaction', trackOrigin)
    let hydrated = !!config.initialState
    // A stall is discovered by a timer, not by a session change, so it republishes on its own.
    const quality = createLinkQuality(() => republish())
    // The same list while nobody joins or leaves: a new array on every state change re-rendered the avatars per remote keystroke.
    let peersOf: unknown = null
    let peers: MarkdownEditorState['peers'] = Object.freeze([])
    function republish(): void {
      hydrated ||= session.synced.value
      const full = session.documentFull.value
      const fatal = session.fatalError.value
      const degraded = session.flushDegraded.value || session.projectionStatus.value === 'degraded'
      // A rejoin of a live transport (a sequence gap, a refused or lost acknowledgement, a server restart) keeps the editor
      // writable: its edits queue in the Y.Doc and the rejoin sends them. Locking it dropped the keys typed meanwhile (COL2-7).
      const status = session.status.value
      publish({
        hydrated,
        writable: session.writable.value && (config.allowOfflineEditing === true
          || (hydrated && !full && (status === 'reconnecting' || (status === 'connected' && session.synced.value)
            // Built from the server's state: typing before the first join is kept in the Y.Doc and sent by the join.
            || (!!config.initialState && status === 'connecting')))),
        connection: full || fatal ? 'failed' : session.status.value === 'connected' ? 'online' : session.status.value === 'local' ? 'offline' : session.status.value,
        pendingChanges: session.pendingUpdates.value,
        documentSave: full || fatal ? 'degraded' : !hydrated || session.pendingUpdates.value > 0 ? 'pending' : 'saved',
        projectionSave: degraded ? 'degraded' : session.projectionStatus.value,
        peers: peersOf === session.peers.value ? peers
          : (peersOf = session.peers.value, peers = Object.freeze(session.peers.value.map(peer => Object.freeze({ ...peer })))),
        link: quality.observe(session.status.value, session.pendingUpdates.value),
        error: fatal ? Object.freeze({ kind: 'collaboration', message: isInvalidContentError(fatal) ? collaborationStrings.contentRefused : fatal.message, recoverable: false })
          : full ? Object.freeze({ kind: 'collaboration', message: collaborationStrings.documentSizeLimitExceeded, recoverable: false })
          : degraded ? Object.freeze({ kind: 'projection', message: collaborationStrings.projectionSaveFailed, recoverable: true }) : null,
      })
    }
    const stop = watch([
      session.status, session.synced, session.pendingUpdates, session.flushDegraded,
      session.documentFull, session.projectionStatus, session.peers, session.fatalError, session.writable,
    ], republish, { immediate: true, flush: 'sync' })
    return {
      extensions: session.extensions,
      isRemoteTransaction: () => remoteTransaction,
      attachEditor: session.attachEditor,
      detachEditor: session.detachEditor,
      destroy() { unregisterJournal(); stop(); quality.destroy(); session.ydoc.off('beforeTransaction', trackOrigin); session.destroy() },
    }
  })
  return adapter
}
