// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createSuggestionField } from './field'

afterEach(() => { vi.useRealTimers(); document.body.replaceChildren() })

it('keeps its room from the start and shows the loading rows only when the answer is not in 100 ms after the request (D7)', async () => {
  vi.useFakeTimers()
  const input = document.createElement('input'); document.body.append(input)
  const answers: ((rows: string[]) => void)[] = []
  const field = createSuggestionField<string>({ input, label: 'Lidé', debounceMs: 150, search: () => new Promise(resolve => answers.push(resolve)),
    item: (title, index) => ({ id: String(index), title }), pick: () => {} })
  document.body.append(field.element)
  const busy = () => field.element.getAttribute('aria-busy') === 'true'
  expect(field.element.hidden).toBe(false)

  input.value = 'ja'; input.dispatchEvent(new Event('input'))
  expect(busy()).toBe(false); expect(field.element.textContent).toBe('')
  await vi.advanceTimersByTimeAsync(150 + 99)
  expect(answers).toHaveLength(1); expect(busy()).toBe(false)
  await vi.advanceTimersByTimeAsync(1)
  expect(busy()).toBe(true)
  answers[0]!(['Jana']); await vi.advanceTimersByTimeAsync(0)
  expect(busy()).toBe(false); expect(field.element.querySelector('[role=option] .t')?.textContent).toBe('Jana')

  // A faster answer shows directly; the rows of the last answer stay meanwhile.
  input.value = 'jan'; input.dispatchEvent(new Event('input'))
  await vi.advanceTimersByTimeAsync(150)
  answers[1]!(['Jan']); await vi.advanceTimersByTimeAsync(200)
  expect(busy()).toBe(false); expect(field.element.querySelector('[role=option] .t')?.textContent).toBe('Jan')

  // An emptied field empties the list but keeps its room.
  input.value = ''; input.dispatchEvent(new Event('input'))
  expect(field.element.hidden).toBe(false); expect(field.element.querySelector('[role=option]')).toBeNull()
  field.destroy()
})
