// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import FormattingToolbar from './FormattingToolbar.vue'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'

// J5.4: a reader's text selection gets a bubble with only the read actions; a writer's bubble stays the formatting one.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

let app: App | undefined, editor: Editor | undefined
afterEach(() => { app?.unmount(); editor?.destroy(); app = editor = undefined; document.body.replaceChildren(); vi.restoreAllMocks() })

const anchors = createHeadingAnchorsPlugin({ getHeadingUrl: id => `https://wiki.test/#/pages/p?heading=${id}` }).create({ profile: 'rich', signal: new AbortController().signal })
async function mount(editable: boolean, readerBubble?: boolean) {
  editor = new Editor({ element: document.body.appendChild(document.createElement('div')), editable, extensions: [StarterKit, ...anchors.extensions!],
    content: '<h2 data-heading-id="sekce">Nadpis sekce</h2><p>Text odstavce</p>' })
  const current = editor
  app = createApp({ render: () => h(FormattingToolbar, { editor: current, profile: 'rich', formatting: ['headings', 'bold', 'link'], ...readerBubble === undefined ? {} : { readerBubble } }) })
  app.mount(document.body.appendChild(document.createElement('div')))
  await nextTick()
  return current
}
/** The reader selects as with the mouse: the page's selection in the view, which ProseMirror reads into its state. */
function select(editor: Editor, from: number, to: number) {
  const start = editor.view.domAtPos(from), end = editor.view.domAtPos(to)
  document.getSelection()!.setBaseAndExtent(start.node, start.offset, end.node, end.offset)
  editor.commands.setTextSelection({ from, to })
}
const bubble = () => document.querySelector<HTMLElement>('[data-md-bubble]')
const shown = () => !!bubble()?.isConnected && bubble()!.style.visibility !== 'hidden'
const labels = () => [...bubble()!.querySelectorAll('button')].map(button => button.getAttribute('aria-label'))
const key = (target: EventTarget, name: string, init: KeyboardEventInit = {}) => target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init }))

it('a reader’s selection in a paragraph gets only „Kopírovat“, copied as the browser copies', async () => {
  const reader = await mount(false)
  select(reader, 15, 19)
  await vi.waitFor(() => expect(shown()).toBe(true))
  expect(labels()).toEqual(['Kopírovat'])
  const exec = vi.fn(() => true); document.execCommand = exec
  bubble()!.querySelector<HTMLElement>('[data-testid=bubble-copy]')!.click()
  expect(exec).toHaveBeenCalledWith('copy')
  // The browser refuses: the selected text goes to the clipboard.
  const writeText = vi.fn(async () => {}); Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
  exec.mockReturnValue(false)
  bubble()!.querySelector<HTMLElement>('[data-testid=bubble-copy]')!.click()
  expect(writeText).toHaveBeenCalledWith('Text')
})

it('a reader’s selection in a heading also copies its link, built by the host', async () => {
  const reader = await mount(false)
  select(reader, 1, 7)
  await vi.waitFor(() => expect(labels()).toEqual(['Kopírovat', 'Kopírovat odkaz']))
  const writeText = vi.fn(async () => {}); Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
  bubble()!.querySelector<HTMLElement>('[data-testid=bubble-copy-link]')!.click()
  await vi.waitFor(() => expect(writeText).toHaveBeenCalledWith('https://wiki.test/#/pages/p?heading=sekce'))
})

it('a writer’s bubble is the formatting one, without the read actions', async () => {
  const writer = await mount(true)
  writer.view.focus(); writer.commands.setTextSelection({ from: 15, to: 19 })
  await vi.waitFor(() => expect(shown()).toBe(true))
  expect(bubble()!.querySelector('[data-testid=bubble-bold]')).not.toBeNull()
  expect(bubble()!.querySelector('[data-testid=bubble-copy]')).toBeNull()
})

it('Alt+F10 takes the reader into the bubble, arrows move, Esc closes it and keeps the selection; a collapse hides it', async () => {
  const reader = await mount(false)
  select(reader, 1, 7)
  key(document.body, 'F10', { altKey: true })
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('data-testid')).toBe('bubble-copy'))
  key(document.activeElement!, 'ArrowRight')
  expect(document.activeElement?.getAttribute('data-testid')).toBe('bubble-copy-link')
  key(document.activeElement!, 'ArrowLeft')
  expect(document.activeElement?.getAttribute('data-testid')).toBe('bubble-copy')
  key(document.activeElement!, 'Escape')
  expect(shown()).toBe(false)
  expect(bubble()?.contains(document.activeElement) ?? false).toBe(false)
  expect([reader.state.selection.from, reader.state.selection.to]).toEqual([1, 7])
  // Shown again by Alt+F10 (jsdom, unlike a browser, drops the page's selection when a button takes the focus); a click elsewhere
  // (the page's selection collapsed outside the view) hides it.
  select(reader, 1, 7)
  key(document.body, 'F10', { altKey: true })
  await vi.waitFor(() => expect(shown()).toBe(true))
  document.getSelection()!.collapse(document.body, 0)
  document.dispatchEvent(new Event('selectionchange'))
  expect(shown()).toBe(false)
})

it('a host with its own reader bar turns the reader’s bubble off; the writer’s bubble stays', async () => {
  const reader = await mount(false, false)
  select(reader, 15, 19)
  key(document.body, 'F10', { altKey: true })
  await new Promise(resolve => setTimeout(resolve, 120))
  expect(shown()).toBe(false)
  expect(document.activeElement).toBe(document.body)
  reader.setEditable(true); reader.view.focus(); reader.commands.setTextSelection({ from: 15, to: 19 })
  await vi.waitFor(() => expect(bubble()!.querySelector('[data-testid=bubble-bold]')).not.toBeNull())
})

it('a copy says „Zkopírováno“ on its button for a moment, in place, and reads it out', async () => {
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  try {
    const reader = await mount(false)
    select(reader, 1, 7)
    await vi.waitFor(() => expect(labels()).toEqual(['Kopírovat', 'Kopírovat odkaz']))
    document.execCommand = vi.fn(() => true)
    const copy = bubble()!.querySelector<HTMLElement>('[data-testid=bubble-copy]')!
    const status = bubble()!.querySelector('[role=status]')!
    expect(status.getAttribute('aria-live')).toBe('polite')
    copy.click(); await vi.waitFor(() => expect(copy.textContent).toBe('Zkopírováno'))
    expect(copy.dataset.feedback).toBe('success')
    expect(status.textContent).toBe('Zkopírováno')
    // The label keeps the room of the longer text: the bubble does not shift.
    expect(copy.querySelector('.bb-said')!.getAttribute('data-alt')).toBe('Zkopírováno')
    vi.advanceTimersByTime(1200); await nextTick()
    expect(copy.textContent).toBe('Kopírovat')
    expect(copy.dataset.feedback).toBeUndefined()
    expect(status.textContent).toBe('')
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn(async () => {}) } })
    const link = bubble()!.querySelector<HTMLElement>('[data-testid=bubble-copy-link]')!
    link.click(); await vi.waitFor(() => expect(link.textContent).toBe('Zkopírováno'))
    vi.advanceTimersByTime(1200); await nextTick()
    expect(link.textContent).toBe('Kopírovat odkaz')
  } finally { vi.useRealTimers() }
})
