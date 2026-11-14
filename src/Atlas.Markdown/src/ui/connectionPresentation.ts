/**
 * One reading of the collaboration link, shared by the corner dot and the corner card.
 *
 * Pure on purpose: the same state always produces the same badge, so a test can pin the copy
 * without mounting anything, and the two surfaces can never disagree about what is happening.
 * Nothing here decides *when* a level is shown — see `useEscalation` for the grace period that
 * keeps a one-second blip from ever reaching the screen.
 */
import type { MarkdownEditorState } from '../core/types'
import { collaborationStrings } from './strings/collaboration'

export type ConnectionLevel = 'dormant' | 'local' | 'live' | 'weak' | 'connecting' | 'reconnecting' | 'offline' | 'failed'

export interface ConnectionPresentation {
  readonly level: ConnectionLevel
  /** Higher means worse. A rise is an escalation to hold back; a fall is a recovery to show at once. */
  readonly severity: number
  /** Lit segments of the three-segment strength meter. */
  readonly bars: 0 | 1 | 2 | 3
  /** Two or three words for the dot's accessible name and the card's heading. */
  readonly label: string
  /** One sentence naming the consequence for the person typing. */
  readonly detail: string
  /** Whether this level deserves the corner card, not just the dot. */
  readonly card: boolean
  /** Whether the work is in flight, so the dot may animate. */
  readonly busy: boolean
}

/** Median acknowledgement round-trip above which the link reads as weak rather than live. */
export const WEAK_LATENCY_MS = 800

const severities: Readonly<Record<ConnectionLevel, number>> = {
  dormant: 0, local: 0, live: 0, weak: 1, connecting: 2, reconnecting: 2, offline: 3, failed: 4,
}
const bars: Readonly<Record<ConnectionLevel, 0 | 1 | 2 | 3>> = {
  dormant: 0, local: 3, live: 3, weak: 2, connecting: 1, reconnecting: 1, offline: 0, failed: 0,
}
/** Levels with nothing worth putting on screen: no dot, no card. */
export const SILENT_LEVELS: ReadonlySet<ConnectionLevel> = new Set<ConnectionLevel>(['dormant', 'local'])

function badge(level: ConnectionLevel, label: string, detail: string, options: { card?: boolean; busy?: boolean } = {}): ConnectionPresentation {
  return Object.freeze({
    level, label, detail, severity: severities[level], bars: bars[level],
    card: options.card ?? false, busy: options.busy ?? false,
  })
}

export function connectionPresentation(state: MarkdownEditorState): ConnectionPresentation {
  const strings = collaborationStrings
  if (state.binding !== 'collaborative') return badge('local', strings.localDocumentLabel, strings.localDocumentDetail)
  if (state.lifecycle === 'destroyed') return badge('dormant', strings.sessionEndedLabel, strings.sessionEndedDetail)
  if (state.lifecycle === 'failed' || state.connection === 'failed' || state.error?.recoverable === false) {
    return badge('failed', strings.failedLabel, strings.failedDetail, { card: true })
  }
  // An editable session that lost the transport keeps accepting edits; a paused one does not.
  const interrupted = state.editable
    ? strings.offlineEditableDetail
    : strings.offlinePausedDetail
  if (state.connection === 'offline') {
    return badge('offline', strings.offlineLabel, state.lifecycle === 'ready' || state.editable
      ? interrupted
      : strings.offlineNotLoadedDetail, { card: true })
  }
  if (state.connection === 'reconnecting') return badge('reconnecting', strings.reconnectingLabel, interrupted, { card: true, busy: true })
  if (state.connection === 'connecting' || state.lifecycle !== 'ready') {
    return badge('connecting', strings.connectingLabel, state.editable ? interrupted : strings.connectingNotEditableDetail, { card: true, busy: true })
  }
  if (state.documentSave === 'degraded' || state.projectionSave === 'degraded') {
    return badge('weak', strings.notSavingLabel, strings.notSavingDetail)
  }
  if (state.link?.stalled) return badge('weak', strings.weakLabel, strings.stalledDetail, { busy: true })
  if (state.link !== null && state.link.latencyMs !== null && state.link.latencyMs > WEAK_LATENCY_MS) {
    return badge('weak', strings.weakLabel, strings.weakDetail)
  }
  return badge('live', strings.liveLabel, strings.liveDetail)
}
