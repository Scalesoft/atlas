/**
 * Draws one frame: `elapsed` is the real time since the client's previous frame; `restyle` marks a calm moment to re-read styles;
 * `detail` (0.02–1) is the share of specks to draw: what this device draws comfortably, shared by every field in the
 * document, and half of it once the client has settled after its last wake.
 */
export type MotionDraw = (now: number, elapsed: number, restyle: boolean, detail: number) => void
export interface MotionClient {
  /** Draw at the active rate for a while; a stopped client starts drawing again. */
  wake(): void
  /** The document's current detail level, for sizing a field before its first frame. */
  detail(): number
  /** Stop drawing until the next wake. */
  stop(): void
  /** Leave the loop; the last client to leave tears it down. */
  remove(): void
}
interface Entry {
  draw: MotionDraw; reduced: () => boolean; running: boolean; active: boolean; wokeAt: number; drawnAt: number; restyledAt: number
  /** Smoothed drawing time in ms this client would take at full detail. */
  cost: number
}

// Surfaces someone interacts with draw every frame (up to 60 per second); unattended ink eases down to a smooth thirty.
// Sparkle density, not the frame rate, is what gives way on slower devices.
const ACTIVE_MS = 16
const IDLE_MS = 33
/** Reduced motion only twinkles in place, which thirty frames per second already render smoothly. */
const REDUCED_MS = 33
/** rAF timestamps jitter; a draw this early still counts as on time, so 60 Hz draws every frame and 30 fps every other. */
const SLACK_MS = 4
/** How long a wake keeps a client at the full rate, and how gradually it then settles to the idle rate and density. */
const AWAKE_MS = 10000
const SETTLE_MS = 5000
/** Unattended ink keeps half of its specks: the rest fade out while it settles and fade back in on the next wake. */
const IDLE_DETAIL = .5
/**
 * Drawing time the ink may take per frame. The ink starts sparse and adds specks while it stays well below this, so a
 * slow device never draws more than it can; only a device that becomes busier later thins the specks again.
 */
const DETAIL_TARGET_MS = 4
const DETAIL_START = .35
/** Pages with hundreds of surfaces may need to go this sparse on a slow phone; each field still keeps a few specks. */
const DETAIL_MIN = .04
/** Detail changes after this long and this many measured frames, growing by at most 30 % at a time. */
const DETAIL_TUNE_MS = 250
const DETAIL_SAMPLES = 8
const DETAIL_GROWTH = 1.3
/** After local typing the ink holds still, leaving every frame to the editor. */
const TYPING_MS = 800
/** Theme reads force a style recalc, so they happen rarely and never while the document is quiet. */
const RESTYLE_MS = 5000
/** Work per animation frame; clients still due afterwards draw first on the next one. */
const BUDGET_MS = 6
/** A sleeping loop asks for a frame this long before the next client is due. */
const FRAME_MS = 16

const loops = new WeakMap<Document, (draw: MotionDraw, reduced: () => boolean) => MotionClient>()

/** Shares one animation-frame loop per document between all invisible-ink fields. */
export function joinMotion(doc: Document, draw: MotionDraw, reduced: () => boolean): MotionClient {
  let join = loops.get(doc)
  if (!join) loops.set(doc, join = createLoop(doc))
  return join(draw, reduced)
}

function createLoop(doc: Document) {
  const win = doc.defaultView
  const entries: Entry[] = []
  let frame: number | undefined
  let timer: ReturnType<typeof setTimeout> | undefined
  let cursor = 0
  let dragging = false
  let typingUntil = 0
  // Drawing time is measured finer than the millisecond clock that schedules frames.
  const clock = win?.performance ? () => win.performance.now() : () => Date.now()
  let detail = DETAIL_START
  /** Frames measured since the last detail adjustment and when that was. */
  let samples = 0
  let tunedAt = 0
  /** 0 while a client is awake, easing to 1 as it settles after its last wake. */
  function settling(since: number) {
    const t = Math.max(0, Math.min(1, (since - AWAKE_MS) / SETTLE_MS))
    return t * t * (3 - 2 * t)
  }
  function intervalOf(entry: Entry, settled: number) {
    return Math.max(ACTIVE_MS + (IDLE_MS - ACTIVE_MS) * settled, entry.reduced() ? REDUCED_MS : 0)
  }
  /**
   * Adds specks while drawing stays cheap, in steps sized so the next one still fits, and thins them if it gets costly.
   * The estimate assumes every visible client awake at full rate, so thinned idle ink never invites more than a wake can draw.
   */
  function tune(now: number) {
    if (++samples < DETAIL_SAMPLES || now - tunedAt < DETAIL_TUNE_MS) return
    samples = 0; tunedAt = now
    let cost = 0
    for (const entry of entries) if (entry.running) cost += entry.cost * detail
    if (cost > DETAIL_TARGET_MS) detail = Math.max(DETAIL_MIN, detail * Math.max(.5, DETAIL_TARGET_MS / cost))
    else if (cost < DETAIL_TARGET_MS * .6) detail = Math.min(1, detail * Math.min(DETAIL_GROWTH, DETAIL_TARGET_MS * .8 / Math.max(cost, .01)))
  }
  /** Requests a frame for `due` (default: now). Until then, and while typing, a timer sleeps instead of empty frames. */
  function pump(due = 0) {
    clearTimeout(timer); timer = undefined
    if (frame !== undefined || dragging || !entries.some(entry => entry.running)) return
    const wait = Math.max(due, typingUntil) - Date.now()
    if (wait > FRAME_MS) timer = setTimeout(pump, wait - FRAME_MS)
    else frame = win?.requestAnimationFrame(tick)
  }
  function tick() {
    frame = undefined
    const started = Date.now()
    // Drags and typing own the main thread: the ink keeps its last frame until they are over.
    if (dragging || started < typingUntil) { pump(); return }
    const queue = entries.slice(cursor).concat(entries.slice(0, cursor))
    cursor = 0
    // Every running client is due again within one idle interval.
    let next = started + IDLE_MS
    let drawn = false
    for (const entry of queue) {
      if (!entry.running) continue
      const now = Date.now()
      if (now - started >= BUDGET_MS) { cursor = Math.max(0, entries.indexOf(entry)); next = now; break }
      const active = now - entry.wokeAt < AWAKE_MS
      const settled = settling(now - entry.wokeAt)
      const due = intervalOf(entry, settled) - SLACK_MS
      if (entry.drawnAt && now - entry.drawnAt < due) { next = Math.min(next, entry.drawnAt + due); continue }
      const restyle = (active && !entry.active) || now - entry.restyledAt >= RESTYLE_MS
      const elapsed = entry.drawnAt ? now - entry.drawnAt : due + SLACK_MS
      entry.drawnAt = now; entry.active = active
      if (restyle) entry.restyledAt = now
      const level = detail * (1 - (1 - IDLE_DETAIL) * settled)
      const drawStarted = clock()
      entry.draw(now, elapsed, restyle, level)
      entry.cost += ((clock() - drawStarted) / level - entry.cost) * .3
      drawn = true
      next = Math.min(next, now + due)
    }
    if (drawn) tune(started)
    pump(next)
  }
  // Listening on the document keeps this independent of the editor and working in readonly views.
  // A drop that removes its source never delivers dragend here; the next pointer move proves the drag is over.
  const release = () => { if (dragging) { dragging = false; pump() } }
  const typed = () => { typingUntil = Date.now() + TYPING_MS }
  // A key in editable content quiets the ink before its input arrives, so no keystroke shares a frame with a draw.
  const keyed = (event: Event) => { if ((event.target as HTMLElement | null)?.isContentEditable) typed() }
  const listeners: Array<[string, (event: Event) => void]> = [
    ['dragstart', () => { dragging = true }], ['dragend', release], ['drop', release], ['pointermove', release],
    ['keydown', keyed], ['beforeinput', typed], ['input', typed],
  ]
  for (const [type, handler] of listeners) doc.addEventListener(type, handler, true)
  return (draw: MotionDraw, reduced: () => boolean): MotionClient => {
    const entry: Entry = { draw, reduced, running: false, active: false, wokeAt: 0, drawnAt: 0, restyledAt: 0, cost: 0 }
    entries.push(entry)
    return {
      wake() {
        if (!entry.running) { entry.running = true; entry.active = false; entry.drawnAt = 0 }
        entry.wokeAt = Date.now(); pump()
      },
      detail: () => detail,
      stop() { entry.running = false },
      remove() {
        entry.running = false
        const index = entries.indexOf(entry)
        if (index < 0) return
        entries.splice(index, 1)
        if (entries.length) return
        if (frame !== undefined) win?.cancelAnimationFrame(frame)
        clearTimeout(timer); frame = undefined; timer = undefined
        for (const [type, handler] of listeners) doc.removeEventListener(type, handler, true)
        loops.delete(doc)
      },
    }
  }
}
