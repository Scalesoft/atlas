/** A field whose display canvas the document shares out: pixels only near the viewport, drawing only when close. */
export interface Visible {
  /** Close to the viewport: the field draws. */
  near(near: boolean): void
  backed(): boolean
  /** Give the display canvas its pixels, or take them away. */
  back(): void
  unback(): void
}
export interface Tracking { untrack(): void }
interface Tracked { client: Visible; near: boolean; far: boolean }

/** Drawing starts just before a surface scrolls into view; pixels are ready a viewport ahead. */
const NEAR_MARGIN = '10% 0px'
const FAR_MARGIN = '100% 0px'
/** Pixels further away are released together once scrolling pauses this long, or with the next batch. */
const RELEASE_MS = 1500

const registries = new WeakMap<Document, ReturnType<typeof createRegistry>>()

/**
 * Tracks a surface's distance from the viewport for all ink fields of a document together. Every canvas with pixels is
 * a compositor layer: each one makes every frame's commit slower, and the more there are, the longer the browser takes
 * to re-layer the page, which it does whenever a canvas gains or loses pixels and as a long page scrolls. So only
 * surfaces within a viewport of the screen keep pixels. They get them in batches, for all surfaces within reach at
 * once, and lose them in batches once scrolling pauses, so the page is re-layered about once a viewport, cheaply.
 * False without IntersectionObserver.
 */
export function trackVisibility(doc: Document, element: Element, client: Visible): Tracking | false {
  const Observer = doc.defaultView?.IntersectionObserver
  if (!Observer) return false
  let registry = registries.get(doc)
  if (!registry) registries.set(doc, registry = createRegistry(doc, Observer))
  return registry.track(element, client)
}

function createRegistry(doc: Document, Observer: typeof IntersectionObserver) {
  const tracked = new Map<Element, Tracked>()
  /** Backed fields that left the far margin, released at the next batch. */
  const leaving = new Set<Tracked>()
  let timer: ReturnType<typeof setTimeout> | undefined
  function release() {
    clearTimeout(timer); timer = undefined
    for (const entry of leaving) if (!entry.far && !entry.near) entry.client.unback()
    leaving.clear()
  }

  /** One batch of new pixels for every surface within the far margin, and the pending releases with it. */
  function batch() {
    for (const entry of tracked.values()) if ((entry.far || entry.near) && !entry.client.backed()) entry.client.back()
    release()
  }

  const near = new Observer(entries => {
    let needed = false
    for (const record of entries) {
      const entry = tracked.get(record.target)
      if (!entry) continue
      entry.near = record.isIntersecting
      if (entry.near && !entry.client.backed()) needed = true
    }
    if (needed) batch()
    for (const record of entries) { const entry = tracked.get(record.target); if (entry) entry.client.near(entry.near) }
  }, { rootMargin: NEAR_MARGIN })
  const far = new Observer(entries => {
    for (const record of entries) {
      const entry = tracked.get(record.target)
      if (!entry) continue
      entry.far = record.isIntersecting
      if (entry.far) leaving.delete(entry)
      else if (entry.client.backed()) { leaving.add(entry); clearTimeout(timer); timer = setTimeout(release, RELEASE_MS) }
    }
  }, { rootMargin: FAR_MARGIN })
  return {
    track(element: Element, client: Visible): Tracking {
      const entry: Tracked = { client, near: false, far: false }
      tracked.set(element, entry)
      near.observe(element); far.observe(element)
      return {
        untrack() {
          if (tracked.get(element) !== entry) return
          tracked.delete(element); leaving.delete(entry)
          near.unobserve(element); far.unobserve(element)
          if (tracked.size) return
          near.disconnect(); far.disconnect(); clearTimeout(timer)
          registries.delete(doc)
        },
      }
    },
  }
}
