import { createParticleField } from './particleField'
import type { Rail } from './rail'
import type { Timers } from './timers'

type Result = { element: HTMLElement; dispose?: () => void }
type Loading = { name: 'loading'; request: AbortController; full: boolean }
type Shown = { name: 'shown'; request: AbortController; full: boolean; opening: boolean; result: Result; content: HTMLElement }
/**
 * rest → loading → shown, and back to rest through `reset` (every hide, abort, visibility loss or destroy)
 * or a failed or empty authorization. Host content exists only in `shown`. `full` means wholly uncovered
 * rather than scratched; `opening` that the secret ring is still sweeping.
 */
type Phase = { name: 'rest' } | Loading | Shown
/**
 * How the mist returns, beside the phase: the grace after leaving, the slow roll after it, or an explicit
 * gentle Hide. Resuming cancels grace and rolling; every roll ends in a hide.
 */
type Cover = 'none' | 'grace' | 'rolling' | 'closing'

export interface ReleaseContext {
  doc: Document
  element: HTMLElement
  particles: HTMLElement
  containerTag: 'div' | 'span'
  timers: Timers
  rail: Rail
  /** Only for available content. The host authorizes inside it and returns a transient element. */
  reveal?: (context: { signal: AbortSignal }) => Promise<Result | void>
  secret: boolean
  toggle: boolean
  revealTimeout: number
  /** Scratched share (0–1) after which the rest of the mist parts on its own, or null to leave it to the user. */
  completeAt: number | null
  leaveDelay: number | null
  limitInactivity: boolean
  destroyed(): boolean
  /** The whole surface reset; deadlines and finished rolls end through it. */
  hide(): void
}

/** The mist rolls back slowly and calmly after leaving. */
const COVER_MS = 4000

/** Release lifecycle of available content and the synthetic fog whose mask uncovers it. */
export function createRelease(ctx: ReleaseContext) {
  const { doc, element, particles, timers, rail } = ctx
  let phase: Phase = { name: 'rest' }
  let cover: Cover = 'none'
  /** After a hide, scratching stays shut until the pointer leaves or comes back, or a deliberate reveal. */
  let blocked = false
  let mask = 'linear-gradient(transparent, transparent)'
  let coverage = 1
  const field = createParticleField(element, particles, value => {
    mask = value
    applyMask()
  }, () => { if (phase.name !== 'rest') ctx.hide(); field.pause() }, (share, x, y) => {
    if (ctx.completeAt !== null && share >= ctx.completeAt) complete(x, y)
  })

  function applyMask() {
    const open = phase
    if (open.name !== 'shown') return
    const { content } = open
    const fullyOpen = open.full && !open.opening
    content.style.maskImage = fullyOpen ? '' : mask
    content.style.opacity = fullyOpen ? String(coverage) : ''
    content.style.pointerEvents = fullyOpen ? '' : 'none'
    content.inert = !fullyOpen
    if (fullyOpen) content.removeAttribute('aria-hidden'); else content.setAttribute('aria-hidden', 'true')
    // One fixed-size synthetic bitmap, inverted for the fog. No growing list of old samples.
    particles.style.maskImage = fullyOpen ? '' : `linear-gradient(black, black), ${mask}`
    // Open blocks keep a quiet glitter rim; an open inline capsule keeps none, it would sit on the value.
    const inline = element.dataset.concealedLayout === 'inline'
    particles.style.clipPath = fullyOpen && coverage === 1 && !inline
      ? 'polygon(evenodd, 0 0, 100% 0, 100% 100%, 0 100%, 0 0, 6px 6px, 6px calc(100% - 6px), calc(100% - 6px) calc(100% - 6px), calc(100% - 6px) 6px, 6px 6px)'
      : ''
    if (fullyOpen && coverage < 1) { particles.style.clipPath = ''; particles.style.opacity = String(1 - coverage) }
    else particles.style.opacity = fullyOpen && inline ? '0' : ''
    rail.stepAside(fullyOpen)
  }
  function setCoverage(value: number) {
    coverage = value
    element.dataset.concealedCoverage = String(value)
    field.setCoverage(value)
    applyMask()
  }
  function resume() {
    if (cover === 'closing') return
    timers.stop('grace', 'cover'); cover = 'none'
    setCoverage(1); field.wake()
  }
  function noteActivity() {
    if (phase.name !== 'shown') return
    // Inactivity still ends a release, but the mist rolls back instead of snapping shut.
    if (ctx.limitInactivity) timers.after('lifetime', 30000, () => dismissGently(false)); else timers.stop('lifetime')
  }
  /** The mist rolls back over the uncovered path, a little at random, instead of a flat fade. */
  function roll(kind: 'rolling' | 'closing', ms: number, interval: number, ease: (t: number) => number, done: () => void) {
    cover = kind
    const open = phase.name === 'rest' ? undefined : phase
    // Wholly uncovered content first becomes a full mask, so the mist can erode it like a scratch.
    if (field.startCover(!!open?.full && !(open.name === 'shown' && open.opening)) && open?.full) { open.full = false; applyMask() }
    timers.tween('cover', ms, interval, t => setCoverage(ease(t)), done)
  }
  function scheduleCover() {
    const delay = ctx.leaveDelay
    if (ctx.destroyed() || delay === null || phase.name === 'rest' || cover !== 'none') return
    cover = 'grace'
    timers.after('grace', delay, () => roll('rolling', COVER_MS, 60,
      t => Math.max(0, 1 - (t < .5 ? 2 * t * t : 1 - Math.pow(2 - 2 * t, 2) / 2)), ctx.hide))
  }
  /** Explicit Hide lets the mist roll back quickly and smoothly; security events still clear at once. */
  function dismissGently(refocus = true) {
    const finish = () => { ctx.hide(); if (refocus) element.focus() }
    if (phase.name !== 'shown' || cover === 'closing') { finish(); return }
    blocked = true
    timers.stop('grace', 'cover')
    if (ctx.toggle) rail.setMode('reveal'); else rail.dismiss.dataset.concealedLeaving = 'true'
    roll('closing', 700, 40, t => 1 - t * t * (3 - 2 * t), finish)
  }
  function startOpening(open: Shown, from?: { x: number; y: number }) {
    open.opening = true
    // A completed scratch keeps what is already open; the ring grows from the stroke over it.
    if (!from) field.reset()
    const rect = element.getBoundingClientRect()
    const width = rect.width || 320
    const height = rect.height || 160
    // Randomness affects decorative motion only, never authorization or secret material.
    // Like invisible ink, one ring sweeps the specks outwards. It starts at a random inner point and
    // drifts in a random direction while growing, so every parting looks different.
    const origin = from ?? { x: width * (.2 + Math.random() * .6), y: height * (.2 + Math.random() * .6) }
    const angle = Math.random() * Math.PI * 2
    const drift = Math.min(width, height) * .35
    const reach = Math.max(...[[0, 0], [width, 0], [0, height], [width, height]].map(([x, y]) => Math.hypot(x! - origin.x, y! - origin.y))) + drift + 18
    const sweep = (progress: number) => {
      const eased = 1 - Math.pow(1 - progress, 1.7)
      field.ripple(origin.x + Math.cos(angle) * drift * eased, origin.y + Math.sin(angle) * drift * eased, 4 + reach * eased)
    }
    sweep(0)
    // Leaving `shown` always passes through reset, which stops this tween, so it never outlives its content.
    timers.tween('opening', 850, 33, sweep, () => { open.opening = false; field.endStroke(); applyMask() })
  }
  /** Most of the surface is scratched open: the rest of the mist parts on its own, from the stroke outwards. */
  function complete(x: number, y: number) {
    const open = phase
    if (open.name !== 'shown' || open.full || open.opening || cover !== 'none' || ctx.destroyed()) return
    open.full = true
    field.endStroke()
    if (element.dataset.concealedReducedMotion !== 'true') startOpening(open, { x, y }); else applyMask()
  }
  function disposeResult(result: Result) {
    result.element.remove()
    try { result.dispose?.() } catch { /* Host errors never become visible plaintext. */ }
  }
  /** Authorization returned nothing or failed: back to rest. A running cover still ends in its hide. */
  function settle() {
    phase = { name: 'rest' }; timers.stop('lifetime')
    element.removeAttribute('aria-busy'); element.dataset.concealedState = 'concealed'
    rail.rest()
  }
  async function reveal(showFull: boolean, deliberate = false) {
    const authorize = ctx.reveal
    if (ctx.destroyed() || !authorize || (blocked && !deliberate)) return
    if (deliberate) blocked = false
    resume()
    if (phase.name !== 'rest') { phase.full = phase.full || showFull; applyMask(); return }
    const loading: Loading = { name: 'loading', request: new AbortController(), full: showFull }
    const { request } = loading
    phase = loading
    const current = () => phase === loading && !ctx.destroyed()
    timers.after('lifetime', ctx.revealTimeout, ctx.hide)
    element.dataset.concealedState = 'loading'; element.setAttribute('aria-busy', 'true'); rail.status.textContent = ''
    // A deliberate secret request immediately turns the toggle into Hide, so a second press cancels.
    if (ctx.toggle && deliberate) rail.setMode('hide')
    try {
      const result = await authorize({ signal: request.signal })
      if (!current() || request.signal.aborted || doc.visibilityState === 'hidden') {
        if (result) disposeResult(result)
        // A release that lands on a hidden page is dropped; the surface returns to rest instead of loading forever.
        if (current()) ctx.hide()
        return
      }
      element.removeAttribute('aria-busy')
      if (!result) { settle(); return }
      const content = doc.createElement(ctx.containerTag); content.className = 'md-concealed__content'; content.dataset.concealedContent = ''
      content.append(result.element)
      const shown: Shown = { name: 'shown', request, full: loading.full, opening: false, result, content }
      phase = shown
      noteActivity()
      element.insertBefore(content, particles)
      // Secrets keep their single toggle; other content gets the corner Hide.
      if (ctx.toggle) rail.setMode('hide'); else rail.dismiss.hidden = false
      element.dataset.concealedState = 'revealed'
      if (ctx.secret && element.dataset.concealedReducedMotion !== 'true') startOpening(shown)
      applyMask()
    } catch {
      if (!current() || request.signal.aborted) return
      settle()
      rail.status.textContent = 'Unable to reveal content. Please try again.'
    }
  }
  /** Security reset: content leaves the DOM before the host disposes it, and pending authorization is aborted. */
  function reset() {
    const previous = phase
    // Rest comes first, so a host dispose that reenters destroy finds nothing left to release.
    phase = { name: 'rest' }; cover = 'none'; blocked = true
    if (previous.name === 'shown') previous.content.remove()
    rail.dismiss.hidden = true
    if (previous.name !== 'rest') previous.request.abort()
    if (previous.name === 'shown') disposeResult(previous.result)
    coverage = 1
    field.reset()
    particles.style.maskImage = ''; particles.style.clipPath = ''
    element.dataset.concealedCoverage = '1'
  }
  return {
    field, reveal, reset, resume, noteActivity, scheduleCover, dismissGently,
    unblock() { blocked = false },
    get blocked() { return blocked },
    /** Authorization is pending or content is mounted. */
    get busy() { return phase.name !== 'rest' },
    get shown() { return phase.name === 'shown' },
    get full() { return phase.name !== 'rest' && phase.full },
    get closing() { return cover === 'closing' },
  }
}
