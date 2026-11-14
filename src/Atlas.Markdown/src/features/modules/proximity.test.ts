// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { hydrateMarkdownWithin, observeMarkdownProximity } from './proximity'

const callbacks: IntersectionObserverCallback[] = []
class Observer {
  constructor(callback: IntersectionObserverCallback) { callbacks.push(callback) }
  observe() {}
  unobserve() {}
}
/** The observer reports `element` within reach at `top` (the viewport is jsdom's 768 px). */
const near = (element: Element, top: number) => callbacks.at(-1)!([{ target: element, isIntersecting: true, boundingClientRect: { top, bottom: top + 40 } } as IntersectionObserverEntry], {} as IntersectionObserver)
const idle = () => new Promise(resolve => setTimeout(resolve, 5))
beforeEach(() => vi.stubGlobal('IntersectionObserver', Observer))
afterEach(() => { document.body.replaceChildren(); vi.unstubAllGlobals() })

function part() {
  const element = document.body.appendChild(document.createElement('div')), hydrate = vi.fn()
  return { element, hydrate, proximity: observeMarkdownProximity(element, hydrate) }
}

describe('the shared proximity observer', () => {
  it('hydrates in an idle batch, on screen first top to bottom with their wave places, then outwards without a wave', async () => {
    const below = part(), second = part(), first = part()
    near(below.element, 1200); near(second.element, 400); near(first.element, 100)
    expect(first.hydrate).not.toHaveBeenCalled()
    await idle()
    expect([first, second, below].map(entry => entry.hydrate.mock.calls)).toEqual([[[0]], [[1]], [[-1]]])
    // Once: no dehydration, no second hydration.
    near(first.element, 100); await idle()
    expect(first.hydrate).toHaveBeenCalledOnce()
  })
  it.each(['focusin', 'pointerdown', 'md-reveal'])('hydrates at once on %s inside it', event => {
    const entry = part(), inner = entry.element.appendChild(document.createElement('span'))
    inner.dispatchEvent(new Event(event, { bubbles: true }))
    expect(entry.hydrate).toHaveBeenCalledWith(-1)
  })
  it('hydrates when the selection enters it, before printing and before an export of its part of the page', () => {
    const selected = part(), printed = part(), exported = part(), disposed = part()
    disposed.proximity.dispose()
    document.getSelection()!.collapse(selected.element, 0)
    document.dispatchEvent(new Event('selectionchange'))
    expect(selected.hydrate).toHaveBeenCalled()
    hydrateMarkdownWithin(exported.element)
    expect([exported.hydrate.mock.calls.length, printed.hydrate.mock.calls.length]).toEqual([1, 0])
    window.dispatchEvent(new Event('beforeprint'))
    expect(printed.hydrate).toHaveBeenCalledOnce()
    expect(disposed.hydrate).not.toHaveBeenCalled()
  })
  it('hydrates at once without IntersectionObserver', () => {
    vi.stubGlobal('IntersectionObserver', undefined)
    expect(part().hydrate).toHaveBeenCalledWith(-1)
  })
})
