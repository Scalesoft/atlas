/**
 * `@atlas/markdown/collaboration/simulator` — a collaboration adapter that talks to nobody.
 *
 * A host that wraps the editor in its own chrome has to know what that chrome looks like while
 * the link is slow, interrupted or refused, and a real server is a poor way to rehearse it: the
 * interesting states are exactly the ones you cannot ask a healthy server for. This adapter
 * drives the same `MarkdownEditorState` a real session publishes, on demand, with no transport
 * and no document — so a gallery page, a design review or an integration test can walk every
 * state in order and watch what the surface does.
 *
 * It carries no CRDT: the document stays local. Use it to rehearse presentation, never to
 * rehearse convergence.
 */
import { Extension } from '@tiptap/core'
import { registerCollaborationAdapter, type CollaborationUpdate } from '../core/internal'
import { collaborationStrings } from '../ui/strings/collaborationAddons'
import type { MarkdownCollaborationAdapter, MarkdownEditorState, MarkdownRichValue } from '../core/types'

/** The states a real link actually produces, named for what a person would say about them. */
export type SimulatedLinkState =
  | 'live' | 'slow' | 'stalled' | 'not-saving' | 'read-only'
  | 'connecting' | 'reconnecting' | 'offline' | 'failed'

export interface SimulatedCollaborationOptions {
  /** Shown as remote collaborators while the session is online. */
  readonly peers?: MarkdownEditorState['peers']
  /** State to publish as soon as the editor attaches. Defaults to `live`. */
  readonly initial?: SimulatedLinkState
  /** Round-trip reported by the healthy states, in milliseconds. */
  readonly latencyMs?: number
  /**
   * Content to put in the editor once, standing in for the document a real session would have
   * hydrated. Rehearsing chrome against an empty page hides most of what moves.
   */
  readonly seed?: MarkdownRichValue
}

export interface SimulatedCollaboration {
  /** Hand this to `<MarkdownEditor :collaboration>`; one adapter belongs to one editor. */
  readonly adapter: MarkdownCollaborationAdapter
  /** The state last requested, whether or not an editor is attached yet. */
  readonly state: SimulatedLinkState
  /** Publish one state. `failed` is terminal, exactly as it is for a real session. */
  apply(state: SimulatedLinkState): void
  /**
   * Alternate between `live` and `interrupted` forever, the way a flaky link does. Returns the
   * stop function; the session is left healthy.
   */
  flap(periodMs: number, interrupted?: SimulatedLinkState): () => void
  destroy(): void
}

const HEALTHY_LATENCY_MS = 45
const SLOW_LATENCY_MS = 1600

export function createSimulatedCollaboration(options: SimulatedCollaborationOptions = {}): SimulatedCollaboration {
  const peers = Object.freeze([...(options.peers ?? [])])
  const healthyLatency = options.latencyMs ?? HEALTHY_LATENCY_MS
  let current: SimulatedLinkState = options.initial ?? 'live'
  let drops = 0
  let publish: ((update: CollaborationUpdate) => void) | undefined
  let stopFlap: (() => void) | undefined
  let destroyed = false

  function update(state: SimulatedLinkState): CollaborationUpdate {
    const online = { connection: 'online', hydrated: true, writable: true, error: null, peers } as const
    const link = (latencyMs: number | null, stalled = false) => Object.freeze({ latencyMs, drops, stalled })
    switch (state) {
      case 'slow':
        return { ...online, pendingChanges: 1, documentSave: 'pending', projectionSave: 'current', link: link(SLOW_LATENCY_MS) }
      case 'stalled':
        return { ...online, pendingChanges: 3, documentSave: 'pending', projectionSave: 'current', link: link(healthyLatency, true) }
      case 'not-saving':
        return { ...online, pendingChanges: 0, documentSave: 'saved', projectionSave: 'degraded', link: link(healthyLatency),
          error: Object.freeze({ kind: 'projection', message: collaborationStrings.projectionSaveFailed, recoverable: true }) }
      case 'read-only':
        return { ...online, writable: false, pendingChanges: 0, documentSave: 'saved', projectionSave: 'current', link: link(healthyLatency) }
      case 'connecting':
        return { connection: 'connecting', writable: false, pendingChanges: 0, documentSave: 'pending', projectionSave: 'pending', peers: Object.freeze([]), error: null, link: link(null) }
      case 'reconnecting':
      case 'offline':
        return { connection: state === 'offline' ? 'offline' : 'reconnecting', writable: false, pendingChanges: 0,
          documentSave: 'pending', projectionSave: 'pending', peers: Object.freeze([]), error: null, link: link(null) }
      case 'failed':
        return { connection: 'failed', writable: false, documentSave: 'degraded', projectionSave: 'degraded', peers: Object.freeze([]),
          link: link(null), error: Object.freeze({ kind: 'collaboration', message: collaborationStrings.connectionRefused, recoverable: false }) }
      default:
        return { ...online, pendingChanges: 0, documentSave: 'saved', projectionSave: 'current', link: link(healthyLatency) }
    }
  }

  const simulation: SimulatedCollaboration = {
    adapter: registerCollaborationAdapter(emit => {
      publish = emit
      const seed = options.seed
      return {
        // A real adapter hydrates through its CRDT extension; this one just places the content.
        extensions: seed ? [Extension.create({
          name: 'markdownSimulatedSeed',
          onCreate: ({ editor }) => { editor.commands.setContent(seed.document, { emitUpdate: false }) },
        })] : [],
        isRemoteTransaction: () => false,
        attachEditor: () => emit(update(current)),
        detachEditor: () => {},
        destroy: () => { publish = undefined },
      }
    }),
    get state() { return current },
    apply(state: SimulatedLinkState): void {
      if (destroyed) return
      // A real session counts a drop the moment a healthy transport goes away.
      const wasOnline = current === 'live' || current === 'slow' || current === 'stalled' || current === 'not-saving' || current === 'read-only'
      const goesDown = state === 'reconnecting' || state === 'offline' || state === 'failed'
      if (wasOnline && goesDown) drops += 1
      current = state
      publish?.(update(state))
    },
    flap(periodMs: number, interrupted: SimulatedLinkState = 'reconnecting'): () => void {
      stopFlap?.()
      let down = false
      const timer = setInterval(() => {
        down = !down
        simulation.apply(down ? interrupted : 'live')
      }, periodMs)
      const stop = () => { clearInterval(timer); if (stopFlap === stop) stopFlap = undefined; simulation.apply('live') }
      stopFlap = stop
      return stop
    },
    destroy(): void {
      destroyed = true
      stopFlap?.()
      stopFlap = undefined
      publish = undefined
    },
  }
  return simulation
}
