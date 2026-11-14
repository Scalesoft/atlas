/*
 * One proximity observer per document for every lazy part of a page (plan 3.6-G, lazy node views): a node view or module registers its
 * element and is hydrated once it comes within one viewport of the screen, in idle batches from the viewport outwards, the ones on
 * screen first and top to bottom. A focus, a press, the selection entering it, printing, an export or a reveal (`md-reveal`, a bubbling
 * event the host dispatches on what it scrolls to: a link to a block, a search jump) hydrates at once. What is hydrated stays so (no
 * dehydration). Without IntersectionObserver (jsdom, an old engine) everything hydrates at once.
 */

/** `wave` is the element's place among those hydrated on screen in one batch (0 first, top to bottom); -1 off screen or forced. */
export type MarkdownHydrate = (wave: number) => void
interface Entry { element: Element; hydrate: MarkdownHydrate }
interface Proximity {
  observer: IntersectionObserver
  entries: Map<Element, Entry>
  /** Within reach and waiting for a batch, with where it was when reported. */
  pending: Map<Entry, { top: number; bottom: number }>
  scheduled: boolean
}
const documents = new WeakMap<Document, Proximity>()
/** A batch yields after this long, so typing and scrolling stay smooth while the rest hydrates. */
const BATCH_MS = 8

function run(proximity: Proximity, entry: Entry, wave: number) {
  if (proximity.entries.get(entry.element) !== entry) return
  proximity.entries.delete(entry.element)
  proximity.pending.delete(entry)
  proximity.observer.unobserve?.(entry.element)
  entry.hydrate(wave)
}

function flush(proximity: Proximity, view: Window, deadline?: IdleDeadline) {
  proximity.scheduled = false
  const start = performance.now(), height = view.innerHeight
  const distance = ({ top, bottom }: { top: number; bottom: number }) => bottom <= 0 ? -bottom : top >= height ? top - height : 0
  const queue = [...proximity.pending].sort(([, a], [, b]) => distance(a) - distance(b) || a.top - b.top)
  let wave = 0
  for (const [entry, at] of queue) {
    const onScreen = distance(at) === 0
    // What is on screen hydrates in one batch, so its wave plays as one; the rest yields to the page.
    if (!onScreen && (deadline ? deadline.timeRemaining() < 1 : performance.now() - start > BATCH_MS)) break
    run(proximity, entry, onScreen ? wave++ : -1)
  }
  schedule(proximity, view)
}

function schedule(proximity: Proximity, view: Window) {
  if (proximity.scheduled || !proximity.pending.size) return
  proximity.scheduled = true
  if (typeof view.requestIdleCallback === 'function') view.requestIdleCallback(deadline => flush(proximity, view, deadline), { timeout: 200 })
  else view.setTimeout(() => flush(proximity, view), 1)
}

/** The registered elements `target` is in, innermost first. */
function around(proximity: Proximity, target: Node | null) {
  const found: Entry[] = []
  for (let node = target; node; node = node.parentNode) { const entry = proximity.entries.get(node as Element); if (entry) found.push(entry) }
  return found
}

function proximityOf(document: Document): Proximity | undefined {
  const view = document.defaultView
  if (typeof view?.IntersectionObserver !== 'function') return undefined
  const known = documents.get(document)
  if (known) return known
  const proximity: Proximity = {
    entries: new Map(), pending: new Map(), scheduled: false,
    // One viewport above and below: what scrolling reaches next is ready before it is seen.
    observer: new view.IntersectionObserver(changes => {
      for (const change of changes) {
        const entry = proximity.entries.get(change.target)
        if (!entry) continue
        const box = change.boundingClientRect
        if (change.isIntersecting) proximity.pending.set(entry, { top: box?.top ?? 0, bottom: box?.bottom ?? 0 })
        else proximity.pending.delete(entry)
      }
      schedule(proximity, view)
    }, { rootMargin: '100% 0px', threshold: 0 }),
  }
  documents.set(document, proximity)
  const force = (event: Event) => { for (const entry of around(proximity, event.target as Node)) run(proximity, entry, -1) }
  document.addEventListener('focusin', force, true)
  document.addEventListener('pointerdown', force, true)
  document.addEventListener('md-reveal', force, true)
  // The caret or a selection entering a lazy part (arrows, a click, the editor's own selection while focused).
  document.addEventListener('selectionchange', () => { for (const entry of around(proximity, document.getSelection()?.anchorNode ?? null)) run(proximity, entry, -1) })
  view.addEventListener('beforeprint', () => { for (const entry of [...proximity.entries.values()]) run(proximity, entry, -1) })
  return proximity
}

/**
 * Hydrates `element` once it nears the viewport (or at once without IntersectionObserver). `hydrate` runs once; `dispose` (the node view
 * destroyed first) lets go of it.
 */
export function observeMarkdownProximity(element: Element, hydrate: MarkdownHydrate) {
  const proximity = proximityOf(element.ownerDocument)
  if (!proximity) { hydrate(-1); return { force() {}, dispose() {} } }
  const entry: Entry = { element, hydrate }
  proximity.entries.set(element, entry)
  proximity.observer.observe(element)
  return {
    force: () => run(proximity, entry, -1),
    dispose() {
      if (proximity.entries.get(element) !== entry) return
      proximity.entries.delete(element); proximity.pending.delete(entry); proximity.observer.unobserve?.(element)
    },
  }
}

/** Hydrates everything under `root` (an export, a print of part of the page). */
export function hydrateMarkdownWithin(root: Element) {
  const proximity = documents.get(root.ownerDocument)
  if (proximity) for (const entry of [...proximity.entries.values()]) if (root.contains(entry.element)) run(proximity, entry, -1)
}
