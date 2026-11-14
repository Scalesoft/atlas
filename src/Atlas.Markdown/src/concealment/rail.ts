import { createIconElement } from '../ui/iconMarkup'
import type { ConcealedContentOptions } from './index'
import type { Timers } from './timers'

export interface RailContext {
  doc: Document
  element: HTMLElement
  containerTag: 'div' | 'span'
  timers: Timers
  listen(target: EventTarget, type: string, handler: EventListener): void
  destroyed(): boolean
  secretRequest: boolean
  secretToggle: boolean
  showRevealControl: boolean
  /** Password requests: whether the visible request button currently steps aside (a request is pending). */
  requestSettled(): boolean
}

/** Controls around the fog: the secret action rail (Reveal/Hide toggle, Copy, status) and the corner Hide. */
export function createRail(options: ConcealedContentOptions, ctx: RailContext) {
  const { doc, element, containerTag, timers, secretRequest, secretToggle, showRevealControl } = ctx
  const secret = options.kind === 'secret'
  const revealLabel = options.labels?.reveal ?? 'Reveal'
  const hideLabel = options.labels?.hide ?? 'Hide'
  const actions = doc.createElement(containerTag)
  actions.className = 'md-concealed__actions'
  actions.dataset.concealedRail = ''
  const primary = doc.createElement('button')
  primary.type = 'button'
  primary.className = 'md-concealed__button'
  primary.dataset.concealedAction = secretRequest ? 'request' : 'reveal'
  primary.textContent = secretRequest
    ? options.labels?.request ?? 'Request access'
    : options.access === 'requestable'
      ? options.labels?.hold ?? 'Hold Space, Enter or press to request access'
      : revealLabel
  // A password request is visible at rest: the lock says why the values stay covered.
  if (secretRequest) primary.prepend(createIconElement(doc, 'lock', 16))
  const restHidden = () => !showRevealControl || (secretRequest && ctx.requestSettled())
  primary.hidden = restHidden()
  let toggleText: HTMLElement | undefined
  let toggleLabel: HTMLElement | undefined
  const stackedIcons = (entries: Array<[Parameters<typeof createIconElement>[1], string]>) => {
    const icons = doc.createElement('span'); icons.className = 'md-concealed__icons'
    for (const [name, state] of entries) { const icon = createIconElement(doc, name, 15); icon.dataset.concealedIcon = state; icons.append(icon) }
    return icons
  }
  if (secretToggle) {
    // The inactive label stays in the same grid cell as a pseudo-element, so the pill never changes width.
    toggleLabel = doc.createElement('span'); toggleLabel.className = 'md-concealed__swap'; toggleLabel.dataset.concealedAlt = hideLabel
    toggleText = doc.createElement('span'); toggleText.className = 'md-concealed__swap-text'; toggleText.textContent = revealLabel
    toggleLabel.append(toggleText)
    primary.replaceChildren(stackedIcons([['eye', 'reveal'], ['eye-off', 'hide']]), toggleLabel)
    primary.dataset.concealedMode = 'reveal'
    // The fading alternate label is decorative; the accessible name is always the current action.
    primary.setAttribute('aria-label', revealLabel)
  }
  let copyButton: HTMLButtonElement | undefined
  if (secretToggle && options.copy) {
    copyButton = doc.createElement('button'); copyButton.type = 'button'
    copyButton.className = 'md-concealed__button md-concealed__copy'; copyButton.dataset.concealedCopy = ''
    const copyLabel = options.labels?.copy ?? 'Copy'
    copyButton.title = copyLabel; copyButton.setAttribute('aria-label', copyLabel)
    copyButton.append(stackedIcons([['copy', 'copy'], ['check', 'done']]))
  }
  const status = doc.createElement('span')
  status.className = 'md-concealed__status'
  status.setAttribute('role', 'status')
  status.setAttribute('aria-live', 'polite')
  if (copyButton) actions.append(copyButton)
  actions.append(primary)
  // Hide lives outside the masked, inert content so it is clickable as soon as anything is uncovered.
  const dismiss = doc.createElement(containerTag)
  dismiss.className = 'md-concealed__dismiss'
  dismiss.dataset.concealedDismiss = ''
  dismiss.hidden = true
  const hideButton = doc.createElement('button')
  hideButton.type = 'button'; hideButton.className = 'md-concealed__button md-concealed__hide'
  hideButton.textContent = hideLabel
  dismiss.append(hideButton)

  /** Set while the host shows the secret in its own UI: the toggle reads Hide and calls this instead. */
  let hostActive: (() => void) | undefined
  let copyRequest: AbortController | undefined
  /** Morph the secret toggle between Reveal and Hide: label and icon cross-fade in place. */
  function setMode(mode: 'reveal' | 'hide') {
    if (!toggleText || !toggleLabel || primary.dataset.concealedMode === mode) return
    toggleLabel.dataset.concealedAlt = toggleText.textContent ?? ''
    toggleText.textContent = mode === 'hide' ? hideLabel : revealLabel
    primary.setAttribute('aria-label', toggleText.textContent)
    primary.dataset.concealedMode = mode
    delete primary.dataset.concealedSwap
    void primary.offsetWidth
    primary.dataset.concealedSwap = 'true'
  }
  /** Nothing released here: the toggle offers Reveal again unless the host still shows the secret. */
  function rest() { if (!hostActive) setMode('reveal') }
  function setActive(active: boolean, onHide?: () => void) {
    if (!secretToggle || ctx.destroyed()) return
    hostActive = active ? onHide ?? (() => {}) : undefined
    if (active) element.dataset.concealedHostOpen = 'true'; else delete element.dataset.concealedHostOpen
    setMode(active ? 'hide' : 'reveal')
  }
  function close() { delete element.dataset.concealedActionsOpen }
  if (copyButton) {
    const button = copyButton
    ctx.listen(button, 'click', () => {
      if (!options.copy || copyRequest || ctx.destroyed()) return
      const request = new AbortController(); copyRequest = request
      const copied = options.labels?.copied ?? 'Copied'
      Promise.resolve().then(() => options.copy!({ signal: request.signal })).then(() => {
        if (ctx.destroyed() || request.signal.aborted) return
        // The icon turns into a check for a moment, then back; nothing jumps or stays behind.
        button.dataset.concealedDone = 'true'; status.textContent = copied
        timers.after('copy', 1600, () => { delete button.dataset.concealedDone; if (status.textContent === copied) status.textContent = '' })
      }, () => { if (!ctx.destroyed() && !request.signal.aborted) status.textContent = 'Unable to copy.' })
        .finally(() => { if (copyRequest === request) copyRequest = undefined })
    })
  }
  return {
    actions, primary, status, dismiss, hideButton,
    get hostActive() { return hostActive },
    setMode, rest, setActive, close,
    open() { if (secret && !ctx.destroyed()) element.dataset.concealedActionsOpen = 'true' },
    /** Controls never vanish abruptly: the primary button fades, then leaves the layout. */
    fadeOut() {
      if (primary.hidden || primary.dataset.concealedLeaving) return
      primary.dataset.concealedLeaving = 'true'
      timers.after('leave', 380, () => { delete primary.dataset.concealedLeaving; primary.hidden = true })
    },
    showNow() {
      timers.stop('leave')
      delete primary.dataset.concealedLeaving; primary.hidden = false
    },
    /** Secret rails fade out through CSS once content is open; ordinary opt-in buttons simply step aside. */
    stepAside(fullyOpen: boolean) { primary.hidden = !showRevealControl || (fullyOpen && !secret) },
    /** Back to the resting controls; a pending copy is aborted. The surface stops the timers. */
    reset() {
      copyRequest?.abort(); copyRequest = undefined
      if (copyButton) delete copyButton.dataset.concealedDone
      delete primary.dataset.concealedLeaving; delete dismiss.dataset.concealedLeaving
      close()
      status.textContent = ''
      primary.hidden = restHidden()
      rest()
    },
  }
}
export type Rail = ReturnType<typeof createRail>
