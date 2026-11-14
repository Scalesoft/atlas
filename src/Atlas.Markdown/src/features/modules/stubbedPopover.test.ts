// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { createMarkdownStubbedPopover } from './stubbedPopover'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren(); vi.useRealTimers() })
function mount() {
  const host = document.createElement('div'), anchor = document.createElement('button'); document.body.append(host, anchor)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<p>text</p>' }); editors.push(editor)
  return { editor, anchor }
}
const module = { draw(container: HTMLElement) { const input = document.createElement('input'); container.append(input); return () => input.remove() } }
const later = <T,>(value: T) => { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done }); return { promise, resolve: () => resolve(value) } }

it('opens at once as a focused stub, then swaps to the editor keeping what was typed', async () => {
  const { editor, anchor } = mount(), gate = later(module)
  const popover = createMarkdownStubbedPopover(() => gate.promise)
  popover.open({ editor, anchor, title: 'Nastavení', render: (m, container) => m.draw(container) })
  const stub = document.querySelector<HTMLElement>('.md-stub')!
  expect(stub).not.toBeNull(); expect(document.activeElement).toBe(stub)
  stub.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true, cancelable: true }))
  stub.dispatchEvent(new KeyboardEvent('keydown', { key: 'b', bubbles: true, cancelable: true }))
  gate.resolve()
  await vi.waitFor(() => expect(document.querySelector('.md-stub')).toBeNull(), { timeout: 1000 })
  const input = document.querySelector<HTMLInputElement>('.md-context-popover input')!
  expect(input.value).toBe('ab'); expect(document.activeElement).toBe(input)
})

it('keeps the focus a swapped-in component placed itself (a menu\'s checked row)', async () => {
  const { editor, anchor } = mount()
  const rows = { draw(container: HTMLElement) { const [first, checked] = [0, 1].map(() => container.appendChild(document.createElement('button'))); checked!.focus(); return () => first!.remove() } }
  const gate = later(rows), popover = createMarkdownStubbedPopover(() => gate.promise)
  popover.open({ editor, anchor, title: 'Stav', render: (m, container) => m.draw(container) })
  gate.resolve()
  await vi.waitFor(() => expect(document.querySelector('.md-stub')).toBeNull(), { timeout: 1000 })
  expect(document.activeElement).toBe(document.querySelectorAll('.md-context-popover button')[1])
})

it('opens as the editor itself, with no stub, once loaded', async () => {
  const { editor, anchor } = mount()
  const popover = createMarkdownStubbedPopover(async () => module)
  popover.prefetch(); await vi.waitFor(() => expect(popover.loaded).toBe(module))
  popover.open({ editor, anchor, title: 'Nastavení', render: (m, container) => m.draw(container) })
  expect(document.querySelector('.md-stub')).toBeNull(); expect(document.querySelector('.md-context-popover input')).not.toBeNull()
})

it('draws nothing when the popover is closed before the editor arrived', async () => {
  const { editor, anchor } = mount(), gate = later(module), draw = vi.fn()
  const popover = createMarkdownStubbedPopover(() => gate.promise)
  const closed = vi.fn()
  const close = popover.open({ editor, anchor, title: 'Nastavení', closed, render: (m, container) => { draw(); return m.draw(container) } })
  close(); gate.resolve()
  // The host hears of the close while it was still the stub (an insert panel lets go of what it followed).
  expect(closed).toHaveBeenCalledOnce()
  await vi.waitFor(() => expect(popover.loaded).toBe(module))
  await new Promise(resolve => setTimeout(resolve, 250))
  expect(draw).not.toHaveBeenCalled()
})

it('retries after a failed download', async () => {
  const load = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue(module)
  const popover = createMarkdownStubbedPopover(load)
  popover.prefetch(); await new Promise(resolve => setTimeout(resolve, 10)); expect(popover.loaded).toBeUndefined()
  popover.prefetch(); await vi.waitFor(() => expect(popover.loaded).toBe(module)); expect(load).toHaveBeenCalledTimes(2)
})

it('settles `ready` when the editor stands, when closed first, and when the popover did not open', async () => {
  const { editor, anchor } = mount(), gate = later(module)
  const popover = createMarkdownStubbedPopover(() => gate.promise)
  const opened = popover.open({ editor, anchor, title: 'A', render: (m, container) => m.draw(container) })
  let done = false; void opened.ready.then(() => { done = true })
  await Promise.resolve(); expect(done).toBe(false)
  gate.resolve(); await opened.ready; expect(document.querySelector('.md-context-popover input')).not.toBeNull()
  const closed = createMarkdownStubbedPopover(() => new Promise<typeof module>(() => {})).open({ editor, anchor, title: 'B', render: (m, c) => m.draw(c) })
  closed(); await closed.ready
  const again = popover.open({ editor, anchor, title: 'A', render: (m, container) => m.draw(container) })
  await again.ready
})

it('calls `closed` once however the popover closes, also while still the stub, and `onCancelled` only before the editor arrived', async () => {
  const { editor, anchor } = mount(), closed = vi.fn(), cancelled = vi.fn()
  const never = createMarkdownStubbedPopover(() => new Promise<typeof module>(() => {}))
  never.open({ editor, anchor, title: 'S', closed, onCancelled: cancelled, render: (m, c) => m.draw(c) })()
  expect(closed).toHaveBeenCalledTimes(1); expect(cancelled).toHaveBeenCalledTimes(1)
  const loaded = createMarkdownStubbedPopover(async () => module); loaded.prefetch(); await vi.waitFor(() => expect(loaded.loaded).toBe(module))
  const again = vi.fn()
  loaded.open({ editor, anchor, title: 'L', closed: again, onCancelled: cancelled, render: (m, c) => m.draw(c) })()
  expect(again).toHaveBeenCalledTimes(1); expect(cancelled).toHaveBeenCalledTimes(1)
})

it('ends the loading with an error and a retry when the editor cannot be downloaded', async () => {
  const { editor, anchor } = mount()
  const load = vi.fn().mockRejectedValueOnce(new Error('offline')).mockResolvedValue(module)
  const popover = createMarkdownStubbedPopover<typeof module>(load)
  const close = popover.open({ editor, anchor, title: 'Nastavení', render: (m, container) => m.draw(container) })
  const stub = document.querySelector<HTMLElement>('.md-stub')!
  await vi.waitFor(() => expect(stub.getAttribute('aria-busy')).toBe('false'))
  expect(stub.querySelector('[role=alert]')!.textContent).not.toBe('')
  await close.ready
  const retry = stub.querySelector<HTMLButtonElement>('[data-module-retry]')!
  expect(document.activeElement).toBe(retry)
  retry.click()
  expect(stub.getAttribute('aria-busy')).toBe('true')
  await vi.waitFor(() => expect(document.querySelector('.md-stub')).toBeNull(), { timeout: 1000 })
  expect(document.querySelector('.md-context-popover input')).not.toBeNull(); expect(load).toHaveBeenCalledTimes(2)
})
