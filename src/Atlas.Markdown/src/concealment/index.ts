import './style.css'
import { createIconElement } from '../ui/iconMarkup'
import { createRail } from './rail'
import { createRelease } from './release'
import { createTimers } from './timers'

export interface ConcealedContentOptions {
  ownerDocument: Document
  kind: 'text' | 'image' | 'secret'
  layout?: 'block' | 'inline'
  access: 'available' | 'requestable'
  signal?: AbortSignal
  /** The host authorizes access before returning content. Never return hidden content eagerly. */
  reveal?: (context: { signal: AbortSignal }) => Promise<{ element: HTMLElement; dispose?: () => void } | void>
  requestAccess?: () => void | Promise<void>
  /**
   * Asked when the user sends a request, before `requestAccess`, for example for a reason. The surface stays
   * requestable while it runs; resolve false (or reject) to cancel quietly. `signal` aborts when the surface
   * hides or is destroyed. `anchor` is the surface itself, for an anchored panel.
   */
  confirmRequest?: (context: { anchor: HTMLElement; signal: AbortSignal }) => boolean | Promise<boolean>
  requestPending?: boolean
  /** Password requests only: the host knows the reader's last request was not approved (rejected, cancelled or
   *  expired). Shown with a new request offered; the surface also shows it when a pending request ends without access. */
  requestRefused?: boolean
  labels?: {
    reveal?: string; request?: string; hold?: string; hide?: string; denied?: string; pending?: string; sending?: string; copy?: string; copied?: string
    /** Password requests: the one-line message beside the visible request button (values hidden, access can be requested). */
    requestHint?: string
    /** Password requests: the message after a request that was not approved. */
    refused?: string
    requestFailed?: string
  }
  /** Secrets only: offer a small Copy icon beside Reveal. The host copies its own value; none is passed here. */
  copy?: (context: { signal: AbortSignal }) => void | Promise<void>
  /** How long `reveal` may take before it is aborted, 1000–600000 ms (default 30000). Hosts that ask for a
   *  second factor inside `reveal` need more time. */
  revealTimeoutMs?: number
  /** Leave/focus-leave grace in milliseconds, 0–600000. Null keeps ordinary content open; secret limits remain. */
  reconcealAfterMs?: number | null
  scratchToReveal?: boolean
  /** Decoration only, never protected content: once wholly uncovered it stays so when the window loses focus or the tab hides
   *  (a security reset would remove and re-mount the host's content, and a mark's intro would play again). */
  keepRevealed?: boolean
  /** Scratching only: once this share (0.1–1) of the surface is scratched open, the rest parts on its own.
   *  Null or omitted leaves uncovering entirely to the user. */
  completeRevealAt?: number | null
  /** Optional reading control. Keyboard interaction is always available on the surface. */
  showRevealControl?: boolean
}
export interface ConcealedContent {
  element: HTMLElement
  /** Secret action rail: explicit host controls slide in from the right with the primary button. */
  actions: HTMLElement
  /** Secrets released in host UI: while active, the same toggle reads Hide and calls `onHide`. */
  setActive(active: boolean, onHide?: () => void): void
  /** Explicit release requested by the host UI, exactly as if Reveal was pressed. Available content only. */
  reveal(): void
  /** Rolls the mist back gently, like the Hide control. Security events should call `hide` instead. */
  conceal(): void
  hide(): void
  destroy(): void
}

/** Presentation only: access checks, audit and secret lifetime remain the host's responsibility. */
export function createConcealedContent(options: ConcealedContentOptions): ConcealedContent {
  const configuredDelay = options.reconcealAfterMs
  if (configuredDelay !== undefined && configuredDelay !== null
    && (typeof configuredDelay !== 'number' || !Number.isFinite(configuredDelay) || configuredDelay < 0 || configuredDelay > 600000))
    throw new RangeError('reconcealAfterMs must be null or a finite number from 0 to 600000.')
  const revealTimeout = options.revealTimeoutMs ?? 30000
  if (typeof revealTimeout !== 'number' || !Number.isFinite(revealTimeout) || revealTimeout < 1000 || revealTimeout > 600000)
    throw new RangeError('revealTimeoutMs must be a finite number from 1000 to 600000.')
  const completeRevealAt = options.completeRevealAt ?? null
  if (completeRevealAt !== null && (typeof completeRevealAt !== 'number' || !Number.isFinite(completeRevealAt) || completeRevealAt < .1 || completeRevealAt > 1))
    throw new RangeError('completeRevealAt must be null or a finite number from 0.1 to 1.')
  const leaveDelay = configuredDelay === null && options.kind !== 'secret' ? null : configuredDelay ?? 1500
  const limitInactivity = options.kind === 'secret' || configuredDelay === undefined
  const doc = options.ownerDocument
  const win = doc.defaultView
  const secret = options.kind === 'secret'
  // A password request takes Reveal's place in the rail and, unlike Reveal, stays visible at rest with a one-line
  // message: without it a reader could not tell that access can be requested.
  const secretRequest = secret && options.access === 'requestable'
  const showRevealControl = secret || options.showRevealControl === true
  // Available secrets use one toggle in one place: Reveal and Hide morph into each other instead of swapping buttons.
  const secretToggle = secret && options.access === 'available'
  const restState = options.access === 'requestable' ? 'requestable' : 'concealed'
  const pendingLabel = options.labels?.pending ?? 'Request pending'
  const failedLabel = options.labels?.requestFailed ?? 'Unable to request access. Please try again.'
  // The host's access request outlives a hide: once sent it stays pending, also after a reload.
  let requestState: 'idle' | 'sending' | 'pending' = options.requestPending ? 'pending' : 'idle'
  /** A password request that ended without access; a new request clears it. */
  let refused = options.requestRefused === true && !options.requestPending
  /** Password requests: the message the rail shows at rest for the current request state. */
  const requestMessage = () => requestState === 'pending' ? pendingLabel
    : requestState === 'sending' ? options.labels?.sending ?? 'Sending request…'
    : refused ? options.labels?.refused ?? 'Your request was not approved. You can ask again.'
    : options.labels?.requestHint ?? 'The values are hidden. You can request access.'
  const containerTag = options.layout === 'inline' ? 'span' : 'div'
  const element = doc.createElement(containerTag)
  element.className = 'md-concealed'
  // A password request's one action is its own visible button, so Tab goes straight to it; the surface stays
  // focusable for scripts (focus returns here once the button steps aside).
  element.tabIndex = secretRequest ? -1 : 0
  element.setAttribute('role', 'group')
  element.setAttribute('aria-label', secretRequest ? requestMessage() : options.access === 'requestable'
    ? options.labels?.hold ?? 'Concealed content. Hold Space or Enter to request access.'
    : secret
      ? `Concealed secret. Use the ${options.labels?.reveal ?? 'Reveal'} button.`
      : `${options.labels?.reveal ?? 'Reveal concealed content'}. Press Enter or Space.`)
  element.dataset.concealedSurface = ''
  element.dataset.concealedKind = options.kind
  element.dataset.concealedLayout = options.layout ?? 'block'
  element.dataset.concealedState = restState
  // Access is part of the look: without it the ink slowly turns red, once requested it slowly turns blue.
  if (options.access === 'requestable') element.dataset.concealedAccess = options.requestPending ? 'pending' : 'denied'
  // Decorative only: which corner the colour rolls in from.
  element.dataset.concealedTintDir = String(Math.floor(Math.random() * 4))
  element.dataset.concealedCoverage = '1'
  const motion = win?.matchMedia?.('(prefers-reduced-motion: reduce)')
  element.dataset.concealedReducedMotion = String(motion?.matches ?? false)
  const skeleton = doc.createElement(containerTag)
  skeleton.className = 'md-concealed__skeleton'
  skeleton.setAttribute('aria-hidden', 'true')
  // Fixed, blurred decorative shapes. Nothing here derives from the payload or its length.
  for (let i = 0; i < 3; i++) skeleton.append(doc.createElement('span'))
  const particles = doc.createElement(containerTag)
  particles.dataset.concealedParticles = ''
  particles.className = 'md-concealed__particles'
  particles.setAttribute('aria-hidden', 'true')
  const controls = doc.createElement(containerTag)
  controls.className = 'md-concealed__controls'

  const listeners: Array<() => void> = []
  function listen(target: EventTarget, type: string, handler: EventListener) {
    target.addEventListener(type, handler); listeners.push(() => target.removeEventListener(type, handler))
  }
  let destroyed = false
  const timers = createTimers()
  const rail = createRail(options, {
    doc, element, containerTag, timers, listen, destroyed: () => destroyed, secretRequest, secretToggle, showRevealControl,
    requestSettled: () => requestState === 'pending',
  })
  const { actions, primary, status } = rail
  if (secret) {
    // Secret status (pending request, copied, errors) glides in from the right with the rail, like Reveal.
    actions.prepend(status)
    element.append(skeleton, particles, controls, actions, rail.dismiss)
  } else {
    controls.append(actions, status)
    element.append(skeleton, particles, controls, rail.dismiss)
  }
  const release = createRelease({
    doc, element, particles, containerTag, timers, rail, secret, toggle: secretToggle,
    reveal: options.access === 'available' ? options.reveal : undefined,
    revealTimeout, leaveDelay, limitInactivity, destroyed: () => destroyed, hide,
    // Only scratched content completes on its own; secrets always need their explicit button.
    completeAt: !secret && (options.scratchToReveal ?? true) ? completeRevealAt : null,
  })
  const { field } = release
  const scratch = !secret && (options.scratchToReveal ?? true)
  // Behind the glitter, a large tilted sign says "no" (or "waiting" once requested) instead of a message.
  // It sits partly off the left corner, as if it did not quite fit, and only shows with the tint.
  if (secretRequest) {
    // The blue "requested" colour is its own layer so it can roll in while the red rolls away.
    const tint = doc.createElement(containerTag)
    tint.className = 'md-concealed__tint'; tint.setAttribute('aria-hidden', 'true')
    element.prepend(tint)
    for (const [state, icon] of [['denied', 'octagon-x'], ['pending', 'hourglass']] as const) {
      const sign = doc.createElement(containerTag)
      sign.className = 'md-concealed__sign'; sign.dataset.concealedSign = state; sign.setAttribute('aria-hidden', 'true')
      sign.append(createIconElement(doc, icon, 24))
      particles.prepend(sign)
    }
  }

  /** Bumped by every hide, so a late request answer never touches controls it no longer owns. */
  let requestGeneration = 0
  /** Ordinary requestable content: the explicit Request access that appears after a completed hold. */
  let requestButton: HTMLButtonElement | undefined
  /** `confirmRequest` in progress: the rail stays open and nothing is sent yet. */
  let confirming: AbortController | undefined
  /** What the user is physically holding: a pointer on the surface, or Space/Enter. */
  const held = { pointer: false, key: false }

  /** Password requests: state attributes, the group's name and the resting message follow the request. */
  function syncRequest(failed = false) {
    if (!secretRequest || destroyed) return
    element.dataset.concealedRequestState = failed ? 'failed' : requestState === 'idle' && refused ? 'refused' : requestState
    element.setAttribute('aria-label', requestMessage())
    status.textContent = failed ? failedLabel : requestMessage()
  }
  function cancelHold() {
    timers.stop('hold', 'progress')
    element.style.removeProperty('--concealed-progress')
    delete element.dataset.concealedArmed
    field.setAgitation(0)
  }
  /** Security reset: every timer stops, released content and pending work are removed, controls return to rest. */
  function hide() {
    requestGeneration++
    confirming?.abort(); confirming = undefined
    timers.clear()
    held.pointer = held.key = false
    release.reset()
    cancelHold()
    requestButton?.remove(); requestButton = undefined
    delete element.dataset.concealedDenied
    rail.reset()
    element.removeAttribute('aria-busy')
    element.dataset.concealedState = restState
    syncRequest()
  }
  function destroy() {
    if (destroyed) return
    destroyed = true; hide(); field.destroy()
    listeners.splice(0).forEach(remove => remove()); element.remove()
  }
  function feedback() {
    if (destroyed) return
    // The colour clouds drift only on surfaces someone is on; the field drops this again far off screen.
    element.dataset.concealedAurora = ''
    field.wake()
    if (options.access === 'requestable') {
      if (requestState === 'pending') delete element.dataset.concealedDenied
      else element.dataset.concealedDenied = 'true'
      // A password request keeps its resting message (syncRequest); the rolling red and the sign only add to it.
      if (secretRequest && requestState === 'idle') return
      status.textContent = requestState === 'pending' ? pendingLabel
        : requestState === 'sending' ? options.labels?.sending ?? 'Sending request…'
        : options.labels?.denied ?? 'You do not have access to this content.'
    }
  }
  function sendRequest() {
    if (destroyed || requestState !== 'idle' || confirming) return
    if (!options.requestAccess) { if (secretRequest) syncRequest(true); else status.textContent = failedLabel; return }
    if (!options.confirmRequest) { submitRequest(); return }
    const generation = requestGeneration
    const operation = new AbortController(); confirming = operation
    const trigger = requestButton ?? (secretRequest ? primary : undefined)
    if (trigger) trigger.disabled = true
    rail.open()
    const settle = (confirmed: boolean) => {
      if (confirming === operation) confirming = undefined
      if (destroyed) return
      if (trigger) trigger.disabled = false
      if (operation.signal.aborted || generation !== requestGeneration) return
      if (confirmed) submitRequest()
      else if (secretRequest) rail.showNow()
    }
    Promise.resolve().then(() => options.confirmRequest!({ anchor: element, signal: operation.signal }))
      .then(confirmed => settle(confirmed === true), () => settle(false))
  }
  function submitRequest() {
    const requestAccess = options.requestAccess
    if (destroyed || requestState !== 'idle' || !requestAccess) return
    const generation = requestGeneration
    requestState = 'sending'
    status.textContent = options.labels?.sending ?? 'Sending request…'
    syncRequest()
    const trigger = requestButton ?? (secretRequest ? primary : undefined)
    if (trigger) trigger.disabled = true
    function finish(success: boolean) {
      if (destroyed) return
      requestState = success ? 'pending' : 'idle'
      if (success) { element.dataset.concealedAccess = 'pending'; refused = false }
      if (secretRequest) {
        // A sent request replaces the button with its pending message; a failed one keeps it, with the error.
        primary.disabled = false
        if (success) rail.fadeOut(); else rail.showNow()
        syncRequest(!success)
      }
      if (generation !== requestGeneration) return
      if (success) {
        const refocus = element.contains(doc.activeElement)
        requestButton?.remove(); requestButton = undefined
        if (!secretRequest) primary.hidden = true
        delete element.dataset.concealedDenied; field.endStroke(); cancelHold()
        rail.close()
        if (refocus) element.focus()
        status.textContent = pendingLabel
      } else {
        if (trigger) trigger.disabled = false
        status.textContent = failedLabel
      }
    }
    try {
      const result = requestAccess()
      if (result) void Promise.resolve(result).then(() => finish(true), () => finish(false))
      else finish(true)
    } catch { finish(false) }
  }
  /** Ordinary requestable content: a deliberate hold earns the Request access button. Password requests show theirs. */
  function startHold() {
    if (destroyed || secretRequest || options.access !== 'requestable' || requestState !== 'idle' || timers.has('hold') || requestButton) return
    feedback()
    let progress = 0
    // The specks bubble up more and more while the hold lasts.
    timers.every('progress', 85, () => {
      progress = Math.min(1, progress + .1)
      element.style.setProperty('--concealed-progress', String(progress)); field.setAgitation(progress)
    })
    timers.after('hold', 850, () => {
      cancelHold()
      if (destroyed) return
      element.style.setProperty('--concealed-progress', '1')
      // The finished thread fades out gently instead of staying lit or snapping back.
      element.dataset.concealedArmed = 'true'
      requestButton = doc.createElement('button')
      requestButton.type = 'button'; requestButton.className = 'md-concealed__button'
      requestButton.textContent = options.labels?.request ?? 'Request access'
      requestButton.dataset.concealedRequest = ''
      requestButton.prepend(createIconElement(doc, 'x', 18))
      requestButton.addEventListener('click', () => {
        sendRequest()
      })
      controls.append(requestButton)
    })
  }
  function isControl(event: Event) {
    return event.target !== element && (event.target as Element | null)?.closest?.('button, a, input, textarea, select') != null
  }
  function position(event: PointerEvent) {
    const rect = element.getBoundingClientRect()
    return { x: Math.max(0, event.clientX - rect.left), y: Math.max(0, event.clientY - rect.top) }
  }
  function scratchAt(event: PointerEvent) {
    // Scratching like a lottery ticket is direct manipulation, so reduced motion keeps it available.
    if (!scratch || release.full || release.blocked) return false
    const point = position(event); field.move(point.x, point.y, false); void release.reveal(false)
    return true
  }
  listen(rail.hideButton, 'click', () => release.dismissGently())
  function activatePrimary() {
    if (primary.dataset.concealedLeaving || release.closing) return
    const hostHide = rail.hostActive
    if (hostHide) { hostHide(); return }
    if (secretToggle && release.busy) { release.dismissGently(); return }
    if (options.access === 'available') void release.reveal(true, true)
    else if (secretRequest) sendRequest()
  }
  listen(primary, 'click', activatePrimary)
  listen(element, 'click', event => {
    if (isControl(event)) return
    // A plain tap only exposes the explicit actions; it never releases or requests anything.
    if (secret) { rail.open(); return }
    if (options.access !== 'available') return
    if (!scratch) void release.reveal(true, true)
  })
  listen(element, 'pointerenter', event => {
    if ((event as PointerEvent).pointerType === 'touch') return
    release.unblock(); release.resume(); feedback(); startHold()
  })
  listen(element, 'pointermove', event => {
    const pointer = event as PointerEvent
    if (isControl(event) || pointer.isPrimary === false || (pointer.pointerType === 'touch' && !held.pointer)) return
    feedback(); release.noteActivity()
    if (options.access === 'requestable') { if (requestState !== 'pending') { const point = position(pointer); field.move(point.x, point.y, true) } }
    // Where nothing is scratched (explicit Reveal, secrets), the glitter still parts and glides around the pointer.
    else if (!scratchAt(pointer)) { const point = position(pointer); field.stir(point.x, point.y) }
  })
  listen(element, 'pointerdown', event => {
    const pointer = event as PointerEvent
    if (pointer.button !== 0 || pointer.isPrimary === false || (isControl(event) && event.target !== primary)) return
    held.pointer = true; release.unblock(); release.resume()
    if (options.access === 'requestable') { startHold(); event.preventDefault() }
    else if (!isControl(event)) scratchAt(pointer)
  })
  listen(element, 'pointerup', event => {
    held.pointer = false; cancelHold(); field.endStroke()
    if (options.access === 'available' && (event as PointerEvent).pointerType === 'touch') release.scheduleCover()
  })
  listen(element, 'pointercancel', () => { hide(); field.pause() })
  listen(element, 'pointerleave', event => {
    // Moving onto the floating action panel, which may extend past the capsule, is not leaving.
    if (element.contains((event as PointerEvent).relatedTarget as Node | null)) return
    held.pointer = false; cancelHold(); field.endStroke(); release.unblock()
    delete element.dataset.concealedDenied
    if ((event as PointerEvent).pointerType !== 'touch') rail.close()
    if (secretRequest) {
      // The request stays where it is; a failure message gives way to the resting one.
      if (requestState === 'idle' && !confirming) syncRequest()
      return
    }
    if (options.access === 'requestable') {
      if (!requestButton || (event as PointerEvent).pointerType !== 'touch') { requestButton?.remove(); requestButton = undefined }
    } else if (!release.full || !element.contains(doc.activeElement)) release.scheduleCover()
  })
  listen(element, 'keydown', event => {
    const key = event as KeyboardEvent
    release.noteActivity()
    if (key.key === 'Escape') { key.preventDefault(); hide(); return }
    if ((key.key !== ' ' && key.key !== 'Enter') || (isControl(event) && event.target !== primary)) return
    key.preventDefault()
    if (key.repeat) return
    if (secret) {
      if (event.target === primary) {
        activatePrimary()
        return
      }
      rail.open()
      // Space activates buttons on keyup, so focus moves only after it is released.
      if (key.key === 'Enter') { if (!primary.hidden) primary.focus() }
      else held.key = true
    }
    else if (options.access === 'requestable') { held.key = true; startHold() }
    else void release.reveal(true, true)
  })
  listen(element, 'keyup', event => {
    const key = event as KeyboardEvent
    if ((key.key !== ' ' && key.key !== 'Enter') || !held.key) return
    key.preventDefault(); held.key = false
    if (secret) { cancelHold(); if (!primary.hidden) primary.focus(); return }
    cancelHold(); requestButton?.focus()
  })
  listen(element, 'focusin', () => { feedback(); if (release.full) release.resume() })
  listen(element, 'focusout', event => {
    if (element.contains((event as FocusEvent).relatedTarget as Node | null)) return
    cancelHold(); held.key = false; delete element.dataset.concealedDenied
    // Focus moving into the request panel keeps the rail and its request button where they are.
    if (confirming) return
    rail.close(); release.scheduleCover()
  })
  listen(element, 'blur', () => { cancelHold(); held.key = false; delete element.dataset.concealedDenied; release.scheduleCover() })
  listen(doc, 'pointerdown', event => { if (!confirming && !element.contains(event.target as Node | null)) rail.close() })
  const stays = () => !!options.keepRevealed && release.full
  listen(doc, 'visibilitychange', () => { if (doc.visibilityState === 'hidden' && !stays()) { hide(); field.pause() } })
  if (win) listen(win, 'blur', () => { if (!stays()) { hide(); field.pause() } })
  if (motion?.addEventListener) listen(motion, 'change', () => { element.dataset.concealedReducedMotion = String(motion.matches); field.wake() })
  if (secretRequest) syncRequest()
  else if (options.requestPending) { primary.hidden = true; status.textContent = pendingLabel }
  if (options.signal) { listen(options.signal, 'abort', destroy); if (options.signal.aborted) destroy() }
  return {
    element, actions, hide, destroy, setActive: rail.setActive,
    reveal: () => { if (!release.busy) void release.reveal(true, true) },
    conceal: () => { if (release.shown) release.dismissGently(false); else hide() },
  }
}
