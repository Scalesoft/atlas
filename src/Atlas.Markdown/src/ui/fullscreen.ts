import {
  markdownOverlayConsumesEscapeAttribute, markdownOverlayForAttribute, markdownOverlayModalAttribute, markdownOverlaysFor,
} from '../core/overlay'

interface FullscreenOwner {
  element: HTMLElement
  focus: () => void
  trigger: HTMLElement | null
}

interface FullscreenSession {
  document: Document
  owners: FullscreenOwner[]
  inertElements: Map<HTMLElement, boolean>
  observer?: MutationObserver
  onFocus: (event: FocusEvent) => void
  onTab: (event: KeyboardEvent) => void
  onBeforeToggle: (event: Event) => void
}

const sessions = new WeakMap<Document, FullscreenSession>()
const focusableSelector = 'button, a[href], input, select, textarea, summary, [tabindex], [contenteditable="true"]'

function isActiveModal(element: HTMLElement): boolean {
  if (element.tagName === 'DIALOG') {
    try { if (element.matches(':modal')) return true } catch { /* selector unavailable */ }
    return (element as HTMLDialogElement).open
      && (element.getAttribute(markdownOverlayModalAttribute) === 'true' || element.getAttribute('aria-modal') === 'true')
  }
  return element.getAttribute(markdownOverlayModalAttribute) === 'true'
    || (['dialog', 'alertdialog'].includes(element.getAttribute('role') ?? '') && element.getAttribute('aria-modal') === 'true')
}

function documentModalDialogs(ownerDocument: Document): HTMLElement[] {
  return [...ownerDocument.querySelectorAll<HTMLElement>(
    `dialog[open], [role="dialog"][aria-modal="true"], [role="alertdialog"][aria-modal="true"], [${markdownOverlayModalAttribute}="true"]`,
  )].filter(isActiveModal)
}

function rootsFor(owner: FullscreenOwner): HTMLElement[] {
  return [owner.element, ...markdownOverlaysFor(owner.element)]
}

function belongsTo(owner: HTMLElement, candidate: Element): boolean {
  return owner.contains(candidate) || markdownOverlaysFor(owner).some(overlay => overlay === candidate || overlay.contains(candidate))
}

export function hasActiveModalDialog(owner: Document | HTMLElement = document): boolean {
  const isDocument = owner.nodeType === 9
  const ownerDocument = isDocument ? owner as Document : (owner as HTMLElement).ownerDocument
  const dialogs = documentModalDialogs(ownerDocument)
  return isDocument ? dialogs.length > 0 : dialogs.some(dialog => dialog !== owner && belongsTo(owner as HTMLElement, dialog))
}

/** True while this editor owns a suggestion popup that should consume Escape first. */
export function hasActiveEditorOverlay(owner: HTMLElement): boolean {
  return markdownOverlaysFor(owner)
    .some(root => root.getAttribute(markdownOverlayConsumesEscapeAttribute) === 'true')
}

function containsFocus(session: FullscreenSession, owner: FullscreenOwner, target: Node | null): boolean {
  return !!target && rootsFor(owner).some(root => root.contains(target))
}

function updateInert(session: FullscreenSession): void {
  for (const [element, inert] of session.inertElements) element.inert = inert
  session.inertElements.clear()
  const owner = session.owners.at(-1)
  if (!owner) return
  const makeInert = (element: HTMLElement) => {
    if (!session.inertElements.has(element)) session.inertElements.set(element, element.inert === true)
    element.inert = true
  }
  // Teleport updates and post-flush watchers are not ordered identically by every renderer.
  // Mark earlier owners themselves as inert even while they are still nested in their original
  // host; body-level isolation below then covers the surrounding application as well.
  for (const previous of session.owners.slice(0, -1)) makeInert(previous.element)
  const isolate = (container: HTMLElement, roots: readonly HTMLElement[]) => {
    for (const child of container.children) {
      if (!(child instanceof (session.document.defaultView?.HTMLElement ?? HTMLElement))) continue
      const element = child as HTMLElement
      const nestedRoots = roots.filter(root => element === root || element.contains(root))
      if (!nestedRoots.length) makeInert(element)
      else if (!nestedRoots.includes(element)) isolate(element, nestedRoots)
    }
  }
  isolate(session.document.body, rootsFor(owner))
}

function createSession(ownerDocument: Document): FullscreenSession {
  const session = {} as FullscreenSession
  Object.assign(session, {
    document: ownerDocument,
    owners: [],
    inertElements: new Map<HTMLElement, boolean>(),
    onFocus: (event: FocusEvent) => {
      const owner = session.owners.at(-1)
      if (owner && hasActiveModalDialog(owner.element)) return
      if (owner && !containsFocus(session, owner, event.target as Node)) owner.focus()
    },
    onTab: (event: KeyboardEvent) => {
      const owner = session.owners.at(-1)
      if (event.key !== 'Tab' || event.defaultPrevented || !owner || hasActiveModalDialog(owner.element)) return
      const ownerWindow = ownerDocument.defaultView
      const candidates = rootsFor(owner).flatMap(root => [...root.querySelectorAll<HTMLElement>(focusableSelector)])
        .filter(element => element.tabIndex >= 0 && !element.matches(':disabled') && !element.closest('[inert], [aria-hidden="true"]')
          && element.getClientRects().length > 0 && ownerWindow?.getComputedStyle(element).visibility !== 'hidden')
      const index = candidates.indexOf(ownerDocument.activeElement as HTMLElement)
      event.preventDefault()
      const nextIndex = index < 0 ? event.shiftKey ? candidates.length - 1 : 0
        : (index + (event.shiftKey ? -1 : 1) + candidates.length) % candidates.length
      const next = candidates[nextIndex]
      if (next) next.focus({ preventScroll: true })
      else owner.focus()
    },
    onBeforeToggle: (event: Event) => {
      const dialog = event.target as HTMLDialogElement | null
      if (dialog?.tagName !== 'DIALOG' || (event as Event & { newState?: string }).newState !== 'open') return
      const owner = session.owners.at(-1)
      if (!owner || !belongsTo(owner.element, dialog)
        || (dialog.getAttribute(markdownOverlayForAttribute) !== null
          && dialog.getAttribute(markdownOverlayModalAttribute) !== 'true')) return
      const previous = session.inertElements.get(dialog)
      if (previous !== undefined) {
        dialog.inert = previous
        session.inertElements.delete(dialog)
      }
    },
  } satisfies FullscreenSession)
  return session
}

function sessionFor(ownerDocument: Document): FullscreenSession {
  let session = sessions.get(ownerDocument)
  if (!session) {
    session = createSession(ownerDocument)
    sessions.set(ownerDocument, session)
  }
  return session
}

export function acquireFullscreenLock(
  element: HTMLElement,
  focus: () => void,
  trigger: HTMLElement | null,
): (restoreFocus?: boolean) => void {
  const ownerDocument = element.ownerDocument
  const session = sessionFor(ownerDocument)
  const body = ownerDocument.body
  const owner = { element, focus, trigger }
  session.owners.push(owner)
  body.setAttribute('data-editor-fullscreen', 'true')
  if (session.owners.length === 1) {
    ownerDocument.addEventListener('focusin', session.onFocus, true)
    ownerDocument.addEventListener('keydown', session.onTab)
    ownerDocument.addEventListener('beforetoggle', session.onBeforeToggle, true)
    const Observer = ownerDocument.defaultView?.MutationObserver
    if (Observer) {
      session.observer = new Observer(() => updateInert(session))
      session.observer.observe(body, { childList: true, subtree: true, attributes: true,
        attributeFilter: ['open', 'role', 'aria-modal', markdownOverlayForAttribute,
          markdownOverlayConsumesEscapeAttribute, markdownOverlayModalAttribute] })
    }
  }
  updateInert(session)
  if (!hasActiveModalDialog(element)) focus()
  let released = false
  return (restoreFocus = true) => {
    if (released) return
    released = true
    const wasTop = session.owners.at(-1) === owner
    const index = session.owners.indexOf(owner)
    if (index >= 0) session.owners.splice(index, 1)
    if (session.owners.length === 0) {
      session.observer?.disconnect()
      session.observer = undefined
      ownerDocument.removeEventListener('focusin', session.onFocus, true)
      ownerDocument.removeEventListener('keydown', session.onTab)
      ownerDocument.removeEventListener('beforetoggle', session.onBeforeToggle, true)
      body.removeAttribute('data-editor-fullscreen')
      sessions.delete(ownerDocument)
    }
    updateInert(session)
    if (!wasTop || hasActiveModalDialog(owner.element)) return
    if (restoreFocus && trigger?.isConnected && !trigger.closest('[inert]')) trigger.focus({ preventScroll: true })
    else session.owners.at(-1)?.focus()
  }
}
