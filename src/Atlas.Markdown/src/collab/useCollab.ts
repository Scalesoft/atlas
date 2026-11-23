// Atlas Markdown collaboration — the composable a consumer calls to turn a
// PoolEditor into a realtime multi-user editor.
//
// It owns the Y.Doc (the CRDT source of truth) and returns the TipTap
// extensions to hand to `<PoolEditor :collaborative :extensions>`. PoolEditor
// itself never imports yjs/signalr — collab arrives through `extensions`, so the
// standalone editor bundle stays pure.
//
// Two modes:
//   • local-only (no `hubUrl`): the Y.Doc lives in-process only. Multiple
//     editors sharing one injected `ydoc` still converge (the Collaboration
//     extension observes the shared doc) — the zero-server gallery demo.
//   • networked (`hubUrl` set): a SignalRYjsProvider relays opaque Yjs updates
//     over the /hubs/tiptap hub and rides an awareness channel for presence +
//     remote carets (CollaborationCaret). The consumer calls `attachEditor` once
//     the editor is created (needed for seeding + snapshot markdown rendering),
//     which also starts the connection.

import { markRaw, ref, type Ref } from 'vue'
import * as Y from 'yjs'
import Collaboration from '@tiptap/extension-collaboration'
import CollaborationCaret from '@tiptap/extension-collaboration-caret'
import type { AnyExtension, Editor } from '@tiptap/core'
import { SignalRYjsProvider, type PresenceUser } from './SignalRYjsProvider'
import { DurableSignalRYjsProvider, type DurableCollaborationOptions } from './DurableSignalRYjsProvider'
import { awarenessColor } from './awareness-color'
import { caretAwareness } from './caretAwareness'
import type { MarkdownDocumentSchema } from '../core/types'
import { CollaborationHistoryBoundary } from './historyBoundary'
import { CollaborationRemoteSelection } from './remoteSelection'
import { CollaborationRemoteRemoval } from './remoteRemoval'
import { CollaborationTableRepair } from './tableRepair'
import { assertRecoveryBinding, type MarkdownRecovery } from './recovery/session'

export interface CollabUser {
  id: string
  name: string
  /** Caret colour; defaults to a deterministic per-id palette colour. */
  color?: string
}

export type CollabStatus = 'local' | 'connecting' | 'connected' | 'reconnecting' | 'offline'

export type { PresenceUser }

export interface UseCollabOptions {
  /** Logical document id, e.g. `notes:{projectKey}`. */
  documentId: string
  /** The local user (drives awareness + caret identity). */
  user: CollabUser
  /** SignalR hub URL, e.g. `/hubs/tiptap`. Omit for local-only mode (no network). */
  hubUrl?: string
  accessToken?: () => string | Promise<string>
  /** Required for versioned documents. Omit only for existing legacy sessions with no schema contract. */
  schema?: MarkdownDocumentSchema
  /** Manual recovery checkpoint branch for a networked session. */
  recovery?: MarkdownRecovery
  /** Explicit authoritative V2 protocol with a durable per-tab journal. */
  durable?: DurableCollaborationOptions
  allowOfflineEditing?: boolean
  /**
   * A reader of a live page: present (avatar) but without a caret. Neither draws colleagues' carets nor publishes its own, so
   * their moves cost it no editor transaction. The hub strips the caret of a read-only participant anyway.
   */
  reader?: boolean
  /**
   * The server's state of the document (one Yjs update, e.g. from the page bundle): applied before the transport exists, so the
   * editor builds from it at once and the join only adds what came after. Never queued as a local change.
   */
  initialState?: Uint8Array
  /** Whether the server already said this person may write (with `initialState`): writable before the join confirms it. */
  initialWritable?: boolean
  /** Inject a pre-existing Y.Doc (e.g. the gallery two-editor demo shares one). */
  ydoc?: Y.Doc
  /** Yjs fragment name the ProseMirror doc binds to. Default `'default'`. */
  field?: string
  /** Fired when the server archived an external (VS Code) edit mid-session (CZ toast). */
  onExternalEditArchived?: () => void
  /**
   * Fired once on the elected seeding client after the readable file is parsed into the fresh doc
   * and before the first snapshot. Run one-shot document upgrades here (issue/contact mentions) so
   * they ride the initial snapshot on the single writer. No-op for non-seeding clients.
   */
  onSeeded?: (editor: Editor) => void
  /** Fired synchronously right before the server's document is applied to a session that may write. */
  onHydrating?: () => void
  /** Observational host callback failures are isolated and reported here. */
  onCallbackError?: (error: unknown) => void
}

export interface CollabHandle {
  /** Append these to `<PoolEditor :extensions>`. */
  extensions: AnyExtension[]
  /** The CRDT document (source of truth). */
  ydoc: Y.Doc
  /** Live connection status (drives the CollaborationStatus badge). */
  status: Ref<CollabStatus>
  /** True once the initial server snapshot has been applied (always true in local mode). */
  synced: Ref<boolean>
  /** True while the server reports it cannot flush to disk (CZ "not saving" banner). */
  flushDegraded: Ref<boolean>
  /** True once the server rejected an edit for exceeding the document size cap (CZ banner). */
  documentFull: Ref<boolean>
  /** Local updates waiting for a durable server acknowledgement. */
  pendingUpdates: Ref<number>
  projectionStatus: Ref<'pending' | 'current' | 'degraded'>
  /** Effective server write permission; false while disconnected or before join. */
  writable: Ref<boolean>
  /** Sequence of the latest snapshot durably stored on the server; null until the first announcement.
   * The first snapshot of a new document is sequence 0, so 0 is a real value, not the initial state. Watch it to re-read projections. */
  snapshotSeq: Ref<number | null>
  fatalError: Ref<Error | null>
  /** Remote collaborators currently present (drives PresenceStack). */
  peers: Ref<PresenceUser[]>
  /** Wire the editor and (networked mode) start the connection. Call on PoolEditor's @created. */
  attachEditor(editor: Editor): void
  /** Suspend projection rendering without disconnecting the shared document. */
  detachEditor(): void
  /** V2 only: await already captured local journal commits, independently of server ACKs. */
  flushLocal?: () => Promise<void>
  /** Tear down provider + doc. Call from the consumer's onBeforeUnmount. */
  destroy(): void
}

export function useCollab(opts: UseCollabOptions): CollabHandle {
  if (opts.durable && (!opts.hubUrl || !opts.schema || opts.recovery)) throw new TypeError('Durable V2 requires a networked versioned session and its own journal.')
  if (opts.recovery && !opts.hubUrl) throw new TypeError('Recovery checkpoints require a networked collaboration session.')
  if (opts.recovery) assertRecoveryBinding(opts.recovery, opts.documentId)
  const ownsDoc = !opts.ydoc
  // markRaw: never let Vue's reactive proxy wrap the Y.Doc — it corrupts Yjs's internal identity.
  const ydoc = markRaw(opts.ydoc ?? new Y.Doc())
  const field = opts.field ?? 'default'
  // Before the provider subscribes to the doc: the server's own state is no local change to send back.
  if (opts.initialState) Y.applyUpdate(ydoc, opts.initialState)
  const color = opts.user.color ?? awarenessColor(opts.user.id)

  const status = ref<CollabStatus>(opts.hubUrl ? 'connecting' : 'local')
  const synced = ref(!opts.hubUrl) // local mode is synced by construction
  const flushDegraded = ref(false)
  const documentFull = ref(false)
  const pendingUpdates = ref(0)
  const projectionStatus = ref<'pending' | 'current' | 'degraded'>('pending')
  const writable = ref(!opts.hubUrl || (!!opts.initialState && opts.initialWritable === true))
  const snapshotSeq = ref<number | null>(null)
  const fatalError = ref<Error | null>(null)
  const peers = ref<PresenceUser[]>([])

  const extensions: AnyExtension[] = [
    Collaboration.configure({ document: ydoc, field }),
    CollaborationHistoryBoundary,
    CollaborationRemoteSelection,
    CollaborationTableRepair,
    // J10.6: a colleague removing the block being typed in keeps the text and names them (the provider's presence, once it exists).
    CollaborationRemoteRemoval.configure({ awareness: () => provider?.awareness }),
  ]

  let provider: SignalRYjsProvider | DurableSignalRYjsProvider | null = null
  if (opts.hubUrl) {
    const providerOptions = {
      hubUrl: opts.hubUrl,
      documentId: opts.documentId,
      ydoc,
      accessToken: opts.accessToken,
      allowOfflineEditing: opts.allowOfflineEditing,
      schema: opts.schema,
      recovery: opts.recovery,
      field,
      ...(opts.initialState ? { knownState: opts.initialState } : {}),
      onStatus: (s) => { status.value = s },
      onSynced: (v) => { synced.value = v },
      onFlushDegraded: (d) => { flushDegraded.value = d },
      onDocumentFull: () => { documentFull.value = true },
      onPendingUpdates: (count) => { pendingUpdates.value = count },
      onProjectionStatus: (value) => { projectionStatus.value = value },
      onWritable: (value) => { writable.value = value },
      onSnapshotStored: (sequence) => { snapshotSeq.value = sequence },
      onFatalError: (error) => { fatalError.value = error },
      onCallbackError: opts.onCallbackError,
      onExternalEditArchived: () => opts.onExternalEditArchived?.(),
      onSeeded: (editor) => opts.onSeeded?.(editor),
      onHydrating: () => opts.onHydrating?.(),
      onPeers: (p) => { peers.value = p },
    } satisfies import('./SignalRYjsProvider').SignalRYjsProviderOptions
    provider = opts.durable
      ? new DurableSignalRYjsProvider({ ...providerOptions, schema: opts.schema!, durable: opts.durable, allowOfflineEditing: opts.allowOfflineEditing })
      : new SignalRYjsProvider(providerOptions)
    // Remote carets + selections. Extra `id` rides the awareness `user` field so
    // PresenceStack can key + colour peers consistently with their caret.
    const user = { id: opts.user.id, name: opts.user.name, color }
    if (opts.reader) provider.awareness.setLocalStateField('user', user)
    // Carets redraw only for a caret or a name that changed, not for every other field of the state (a host's pointers).
    else extensions.push(CollaborationCaret.configure({ provider: { awareness: caretAwareness(provider.awareness) }, user }))
  }

  function attachEditor(editor: Editor): void {
    if (!provider) return // local-only mode has no transport
    provider.attachEditor(editor)
    void provider.start()
  }

  function destroy(): void {
    provider?.destroy()
    if (ownsDoc) ydoc.destroy()
  }

  return {
    extensions,
    ydoc,
    status,
    synced,
    flushDegraded,
    documentFull,
    pendingUpdates,
    projectionStatus,
    writable,
    snapshotSeq,
    fatalError,
    peers,
    attachEditor,
    detachEditor: () => provider?.detachEditor(),
    ...(provider instanceof DurableSignalRYjsProvider ? { flushLocal: () => (provider as DurableSignalRYjsProvider).flushLocal() } : {}),
    destroy,
  }
}
