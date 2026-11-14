/**
 * How good the collaboration link actually is, measured from what the session already reports.
 *
 * The provider tells us how many local updates are still waiting for a durable server
 * acknowledgement. A falling count acknowledges the oldest queued edits, even while newer edits
 * are still pending. Track their enqueue times to measure edit-to-acknowledgement latency without
 * extra wire traffic, and flag a stall only when an individual edit has waited too long.
 *
 * Readings are frozen and reused while nothing changes — the editor state is compared by
 * reference, so a fresh object per keystroke would publish a state change on every transaction.
 */
import type { MarkdownLinkQuality } from '../core/types'
import type { CollabStatus } from './useCollab'

export interface LinkQualityOptions {
  /** Pending acknowledgement older than this reads as a stalled link. */
  readonly stallAfterMs?: number
  /** Round-trips kept for the median. */
  readonly samples?: number
  readonly now?: () => number
  /** Timer seam; returns its own cancel. */
  readonly schedule?: (run: () => void, ms: number) => () => void
}

export interface LinkQuality {
  /** Folds one session observation in and returns the current reading. */
  observe(status: CollabStatus, pendingUpdates: number): MarkdownLinkQuality
  destroy(): void
}

const DEFAULT_STALL_MS = 2000
const DEFAULT_SAMPLES = 5

function median(values: readonly number[]): number | null {
  if (values.length === 0) return null
  const sorted = [...values].sort((a, b) => a - b)
  return sorted[(sorted.length - 1) >> 1]!
}

/** `onStall` fires once per overdue batch; the caller republishes the editor state from it. */
export function createLinkQuality(onStall: () => void, options: LinkQualityOptions = {}): LinkQuality {
  const stallAfterMs = options.stallAfterMs ?? DEFAULT_STALL_MS
  const keep = options.samples ?? DEFAULT_SAMPLES
  const now = options.now ?? (() => Date.now())
  const schedule = options.schedule ?? ((run, ms) => { const id = setTimeout(run, ms); return () => clearTimeout(id) })

  const roundTrips: number[] = []
  let reading: MarkdownLinkQuality = Object.freeze({ latencyMs: null, drops: 0, stalled: false })
  let previousStatus: CollabStatus | null = null
  let drops = 0
  // Counts are reported synchronously; both providers acknowledge their outboxes in FIFO order.
  const pending: { count: number; since: number }[] = []
  let pendingCount = 0
  let stalled = false
  let cancelStall: (() => void) | undefined
  let destroyed = false

  function disarm(): void {
    cancelStall?.()
    cancelStall = undefined
  }

  function arm(): void {
    disarm()
    cancelStall = schedule(() => {
      cancelStall = undefined
      if (destroyed || stalled || pending.length === 0) return
      stalled = true
      onStall()
    }, Math.max(0, stallAfterMs - (now() - pending[0]!.since)))
  }

  return {
    observe(status: CollabStatus, pendingUpdates: number): MarkdownLinkQuality {
      if (destroyed) return reading
      if (previousStatus === 'connected' && status !== 'connected') drops += 1
      previousStatus = status

      if (status !== 'connected') {
        // An acknowledgement cannot arrive while the transport is down, so the wait that outage
        // caused is neither a latency sample nor a stall — the interruption already says enough.
        pending.length = 0
        pendingCount = 0
        stalled = false
        disarm()
      }
      else {
        const previousOldest = pending[0]
        if (pendingUpdates > pendingCount) {
          pending.push({ count: pendingUpdates - pendingCount, since: now() })
        } else if (pendingUpdates < pendingCount) {
          roundTrips.push(now() - pending[0]!.since)
          if (roundTrips.length > keep) roundTrips.splice(0, roundTrips.length - keep)
          let acknowledged = pendingCount - pendingUpdates
          while (acknowledged > 0) {
            const first = pending[0]!
            const removed = Math.min(first.count, acknowledged)
            first.count -= removed
            acknowledged -= removed
            if (first.count === 0) pending.shift()
          }
        }
        pendingCount = pendingUpdates
        if (pending[0] !== previousOldest) {
          stalled = pending.length > 0 && now() - pending[0]!.since >= stallAfterMs
          disarm()
          if (pending.length > 0 && !stalled) arm()
        }
      }

      const latencyMs = median(roundTrips)
      if (latencyMs === reading.latencyMs && drops === reading.drops && stalled === reading.stalled) return reading
      reading = Object.freeze({ latencyMs, drops, stalled })
      return reading
    },
    destroy(): void {
      destroyed = true
      disarm()
    },
  }
}
