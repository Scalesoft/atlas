// @vitest-environment jsdom

import { afterEach, expect, it, vi } from 'vitest'
import { createSuggestionPopup } from './floatingPopup'

afterEach(() => {
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

it('reads live anchor geometry between explicit suggestion updates', async () => {
  const content = document.createElement('div')
  let top = 20
  const getReferenceRect = vi.fn(() => new DOMRect(10, top, 1, 14))
  const popup = createSuggestionPopup({ getReferenceRect, content })
  try {
    await new Promise(resolve => setTimeout(resolve, 30))
    const calls = getReferenceRect.mock.calls.length
    top = 80
    await new Promise(resolve => setTimeout(resolve, 30))
    expect(getReferenceRect.mock.calls.length).toBeGreaterThan(calls)
  } finally {
    popup.destroy()
  }
})

it('uses the context element document and has idempotent cleanup', () => {
  const frameDocument = document.implementation.createHTMLDocument('frame')
  const contextElement = frameDocument.createElement('div')
  const content = frameDocument.createElement('div')
  frameDocument.body.append(contextElement)
  const popup = createSuggestionPopup({ getReferenceRect: () => new DOMRect(), contextElement, content })
  expect(frameDocument.body.contains(content)).toBe(true)
  expect(document.body.contains(content)).toBe(false)
  popup.destroy()
  popup.destroy()
  expect(frameDocument.body.contains(content)).toBe(false)
})

it('reads no layout or style while it opens and follows keystrokes: it is measured in the next frame', async () => {
  const getReferenceRect = vi.fn(() => new DOMRect(40, 60, 1, 14))
  const contextElement = document.createElement('div'); document.body.append(contextElement)
  const style = vi.spyOn(window, 'getComputedStyle')
  const content = document.createElement('div')
  const popup = createSuggestionPopup({ getReferenceRect, contextElement, content })
  popup.setReferenceRect(getReferenceRect); popup.setReferenceRect(getReferenceRect)
  expect(getReferenceRect).not.toHaveBeenCalled()
  expect(style).not.toHaveBeenCalled()
  await vi.waitFor(() => expect(content.parentElement!.dataset.positioned).toBe('true'))
  popup.destroy()
})

it('keeps the portal inert until its first anchored position is ready', async () => {
  const content = document.createElement('button')
  const popup = createSuggestionPopup({
    getReferenceRect: () => new DOMRect(40, 60, 1, 14),
    contextElement: document.body,
    content,
  })
  const portal = content.parentElement as HTMLElement
  expect(portal.style.visibility).toBe('hidden')
  expect(portal.style.pointerEvents).toBe('none')
  expect(portal.inert).toBe(true)

  await vi.waitFor(() => expect(portal.dataset.positioned).toBe('true'))
  expect(portal.style.visibility).toBe('visible')
  expect(portal.style.pointerEvents).toBe('')
  expect(portal.inert).toBe(false)
  popup.destroy()
})
