import { SNAPSHOT_RETRY_MS } from './retryPolicy'

const DEBOUNCE_MS = 1000
const MAX_WAIT_MS = 2000
/** A snapshot projects the whole document (tens of ms on a large page): it waits until keys and pointer rest this long… */
const QUIET_MS = 800
/** …and for an idle period, at the latest this long… */
const IDLE_TIMEOUT_MS = 3000
/**
 * …but continuous typing still gets one this often, so the readable projection never falls far behind. Another user's „Uložit“ waits
 * for the projection about 6 s (Atlas.Web savePage.ts: untilProjected 3 s, then 12 retries 250 ms apart): a change is due at most
 * MAX_WAIT_MS after it, so 2 s + 2,5 s + the push itself stays inside that window, while a typist still gets a quiet moment first.
 */
const MAX_DEFER_MS = 2_500
/**
 * Typing on into the last this long before the deadline: the snapshot runs right after the next key is drawn, so it takes the gap
 * before the following key instead of landing on one (86–243 ms at 4× CPU on a long page). About one key's gap: snapshots come
 * hardly more often than before.
 */
const AFTER_KEY_MS = 500
/** An idle period this long (the browser grants at most 50 ms) means no task or frame is waiting. */
const FREE_IDLE_MS = 40
const INPUT_EVENTS = ['keydown', 'pointerdown'] as const

type IdleWindow = Pick<Window, 'addEventListener' | 'removeEventListener'> & {
  requestIdleCallback?: Window['requestIdleCallback']
  cancelIdleCallback?: Window['cancelIdleCallback']
  requestAnimationFrame?: Window['requestAnimationFrame']
}

/**
 * Owns advisory snapshot deadlines; the provider decides whether a snapshot may be published. A due snapshot runs once the
 * user rests and the main thread is idle, not in the middle of typing or of the page's start-up work. `flush` runs a
 * waiting one at once (the page is hidden or left).
 */
export class SnapshotScheduler {
  private debounce: ReturnType<typeof setTimeout> | null = null
  private maxWait: ReturnType<typeof setTimeout> | null = null
  private retryTimer: ReturnType<typeof setTimeout> | null = null
  private quietTimer: ReturnType<typeof setTimeout> | null = null
  private idle: number | null = null
  private dueSince: number | null = null
  private lastInput = -Infinity
  /** In the last AFTER_KEY_MS before the deadline: the next key runs it once drawn. */
  private afterKey = false
  private destroyed = false
  private readonly window: IdleWindow | null

  constructor(private readonly push: () => void, window: IdleWindow | null = typeof globalThis.addEventListener === 'function' ? globalThis as unknown as IdleWindow : null) {
    this.window = window
    for (const type of INPUT_EVENTS) this.window?.addEventListener(type, this.onInput, { capture: true, passive: true })
  }

  schedule(): void {
    if (this.destroyed || this.dueSince !== null) return
    if (this.debounce) clearTimeout(this.debounce)
    this.debounce = setTimeout(this.due, DEBOUNCE_MS)
    this.maxWait ??= setTimeout(this.due, MAX_WAIT_MS)
  }

  /** A scheduled or waiting snapshot runs now. */
  flush(): void {
    if (this.destroyed || (!this.debounce && !this.maxWait && this.dueSince === null)) return
    this.clearSchedule()
    this.push()
  }

  clearSchedule(): void {
    if (this.debounce) { clearTimeout(this.debounce); this.debounce = null }
    if (this.maxWait) { clearTimeout(this.maxWait); this.maxWait = null }
    if (this.quietTimer) { clearTimeout(this.quietTimer); this.quietTimer = null }
    if (this.idle !== null) { this.window?.cancelIdleCallback?.(this.idle); this.idle = null }
    this.dueSince = null
    this.afterKey = false
  }

  retry(): void {
    if (this.retryTimer || this.destroyed) return
    this.retryTimer = setTimeout(() => {
      this.retryTimer = null
      this.push()
    }, SNAPSHOT_RETRY_MS)
  }

  clearRetry(): void {
    if (!this.retryTimer) return
    clearTimeout(this.retryTimer)
    this.retryTimer = null
  }

  destroy(): void {
    this.destroyed = true
    this.clearSchedule()
    this.clearRetry()
    for (const type of INPUT_EVENTS) this.window?.removeEventListener(type, this.onInput, { capture: true })
  }

  private readonly onInput = (): void => {
    this.lastInput = performance.now()
    if (!this.afterKey) return
    this.afterKey = false
    // After the frame that draws this key (its layout and paint done); the deadline timer stays the bound.
    const drawn = () => setTimeout(() => { if (this.dueSince !== null) { this.clearSchedule(); this.push() } })
    if (this.window?.requestAnimationFrame) this.window.requestAnimationFrame(drawn); else drawn()
  }

  private readonly due = (): void => {
    if (this.debounce) { clearTimeout(this.debounce); this.debounce = null }
    if (this.maxWait) { clearTimeout(this.maxWait); this.maxWait = null }
    this.dueSince ??= performance.now()
    this.whenQuiet()
  }

  private whenQuiet(): void {
    const now = performance.now()
    const left = MAX_DEFER_MS - (now - this.dueSince!), overdue = left <= 0
    const rest = QUIET_MS - (now - this.lastInput)
    if (rest > 0 && !overdue) {
      this.afterKey = left <= AFTER_KEY_MS
      // Never past the deadline: the save window above counts on it.
      this.quietTimer = setTimeout(() => { this.quietTimer = null; this.whenQuiet() }, Math.min(rest, this.afterKey ? left : left - AFTER_KEY_MS))
      return
    }
    const requestIdle = this.window?.requestIdleCallback
    if (overdue || !requestIdle) { this.run(); return }
    const since = now
    const waitIdle = () => {
      this.idle = requestIdle.call(this.window, deadline => {
        this.idle = null
        // A key or click while waiting for idle time: wait for the user to rest again.
        if (performance.now() - this.lastInput < QUIET_MS) this.whenQuiet()
        // A short gap between the page's own tasks is not a free thread: wait for a long idle period, or the timeout.
        else if (!deadline.didTimeout && deadline.timeRemaining() < FREE_IDLE_MS && performance.now() - since < IDLE_TIMEOUT_MS) waitIdle()
        else this.run()
      }, { timeout: Math.max(1, IDLE_TIMEOUT_MS - (performance.now() - since)) })
    }
    waitIdle()
  }

  private run(): void {
    this.dueSince = null
    this.afterKey = false
    this.push()
  }
}
