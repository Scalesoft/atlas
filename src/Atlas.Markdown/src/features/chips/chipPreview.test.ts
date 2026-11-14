// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { bindMarkdownChipPreview } from './chipPreview'

afterEach(() => { document.body.replaceChildren(); vi.useRealTimers() })

function bound() {
  const chip = document.createElement('button'); chip.textContent = 'Jana'; document.body.append(chip)
  const detach = bindMarkdownChipPreview(chip, () => ({ title: 'Jana Nováková', lines: ['Vedoucí'] }))
  return { chip, detach }
}
const shown = () => document.querySelector<HTMLElement>('.md-chip-preview')

it('shows the card, lets it leave with the popover exit and removes it only when that is over', async () => {
  vi.useFakeTimers()
  const { chip } = bound()
  chip.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' }))
  vi.advanceTimersByTime(400)
  const card = shown()!
  expect(card.dataset.leaving).toBeUndefined()
  // An exit animation is running once the card is marked leaving.
  let end!: () => void
  const finished = new Promise<void>(resolve => { end = resolve })
  card.getAnimations = (() => [{ finished }]) as never
  chip.dispatchEvent(new PointerEvent('pointerleave'))
  expect(shown()).toBe(card); expect(card.dataset.leaving).toBe('true'); expect(card.getAttribute('aria-hidden')).toBe('true')
  expect(chip.hasAttribute('aria-describedby')).toBe(false)
  end(); await vi.advanceTimersByTimeAsync(0)
  expect(shown()).toBeNull()
})

it('removes the card at once where nothing animates, and at the latest after a moment', async () => {
  vi.useFakeTimers()
  const { chip } = bound()
  chip.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' })); vi.advanceTimersByTime(400)
  chip.dispatchEvent(new PointerEvent('pointerleave'))
  expect(shown()).toBeNull()
  chip.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' })); vi.advanceTimersByTime(400)
  shown()!.getAnimations = (() => [{ finished: new Promise(() => {}) }]) as never
  chip.dispatchEvent(new PointerEvent('pointerleave'))
  expect(shown()).not.toBeNull()
  vi.advanceTimersByTime(500)
  expect(shown()).toBeNull()
})

it('takes a leaving card with it when the chip goes', () => {
  vi.useFakeTimers()
  const { chip, detach } = bound()
  chip.dispatchEvent(new PointerEvent('pointerenter', { pointerType: 'mouse' })); vi.advanceTimersByTime(400)
  shown()!.getAnimations = (() => [{ finished: new Promise(() => {}) }]) as never
  chip.dispatchEvent(new PointerEvent('pointerleave'))
  detach()
  expect(shown()).toBeNull()
})
