// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { trackVisibility, type Visible } from './visibility'

/** Stand-in observers: `near` and `far` are the two margins, in the order the registry creates them. */
const observers: Array<{ callback: IntersectionObserverCallback; margin: string; targets: Set<Element> }> = []
class Observer {
  entry: (typeof observers)[number]
  constructor(callback: IntersectionObserverCallback, options?: IntersectionObserverInit) {
    this.entry = { callback, margin: options?.rootMargin ?? '', targets: new Set() }
    observers.push(this.entry)
  }
  observe(target: Element) { this.entry.targets.add(target) }
  unobserve(target: Element) { this.entry.targets.delete(target) }
  disconnect() { this.entry.targets.clear(); observers.splice(observers.indexOf(this.entry), 1) }
}
function report(which: 0 | 1, changes: Array<[Element, boolean]>) {
  const observer = observers[which]!
  observer.callback(changes.map(([target, isIntersecting]) => ({ target, isIntersecting }) as IntersectionObserverEntry), observer as unknown as IntersectionObserver)
}
function surface() {
  const element = document.createElement('div')
  const log: string[] = []
  let backed = false
  const client: Visible = {
    near: near => log.push(near ? 'near' : 'away'), backed: () => backed,
    back: () => { backed = true; log.push('back') }, unback: () => { backed = false; log.push('unback') },
  }
  const tracking = trackVisibility(document, element, client)
  if (!tracking) throw new Error('tracking unavailable')
  return { element, log, tracking, backed: () => backed }
}

describe('shared canvas visibility', () => {
  beforeEach(() => { vi.useFakeTimers(); vi.stubGlobal('IntersectionObserver', Observer) })
  afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); observers.length = 0 })

  it('backs every surface within reach in one batch when the first one comes near', () => {
    const [a, b, c] = [surface(), surface(), surface()]
    report(1, [[a.element, true], [b.element, true], [c.element, false]])
    expect([a.backed(), b.backed(), c.backed()]).toEqual([false, false, false])
    report(0, [[a.element, true]])
    expect([a.backed(), b.backed(), c.backed()]).toEqual([true, true, false])
    // Pixels come before drawing starts.
    expect(a.log).toEqual(['back', 'near'])
    // b is already backed when it scrolls in: no new batch.
    report(0, [[b.element, true]])
    expect(b.log).toEqual(['back', 'near'])
    a.tracking.untrack(); b.tracking.untrack(); c.tracking.untrack()
  })
  it('releases far surfaces together once scrolling pauses, and keeps those that came back', () => {
    const [a, b] = [surface(), surface()]
    report(1, [[a.element, true], [b.element, true]]); report(0, [[a.element, true]])
    report(0, [[a.element, false]])
    report(1, [[a.element, false], [b.element, false]])
    vi.advanceTimersByTime(1000)
    // Scrolling back before the pause keeps b's pixels.
    report(1, [[b.element, true]])
    vi.advanceTimersByTime(499)
    expect(a.backed()).toBe(true)
    vi.advanceTimersByTime(1)
    expect([a.backed(), b.backed()]).toEqual([false, true])
    a.tracking.untrack(); b.tracking.untrack()
  })
  it('shares two observers per document and disconnects them with the last surface', () => {
    const [a, b] = [surface(), surface()]
    expect(observers.map(observer => observer.margin)).toEqual(['10% 0px', '100% 0px'])
    a.tracking.untrack()
    expect(observers).toHaveLength(2)
    b.tracking.untrack()
    expect(observers).toHaveLength(0)
    expect(vi.getTimerCount()).toBe(0)
  })
  it('reports unavailable without IntersectionObserver', () => {
    vi.stubGlobal('IntersectionObserver', undefined)
    expect(trackVisibility(document, document.createElement('div'), { near() {}, backed: () => false, back() {}, unback() {} })).toBe(false)
  })
})
