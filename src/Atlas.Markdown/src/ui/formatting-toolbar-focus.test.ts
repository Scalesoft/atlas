// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, type App } from 'vue'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import FormattingToolbar from './FormattingToolbar.vue'

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

let app: App | undefined
afterEach(() => { app?.unmount(); app = undefined; document.body.replaceChildren() })

async function shown() {
  const editor = new Editor({ element: document.body.appendChild(document.createElement('div')), extensions: [StarterKit], content: '<p>Text</p>' })
  app = createApp({ render: () => h(FormattingToolbar, { editor, profile: 'rich', formatting: ['bold'] }) }); app.mount(document.body.appendChild(document.createElement('div')))
  await nextTick()
  editor.commands.focus(); editor.commands.setTextSelection({ from: 1, to: 5 })
  await vi.waitFor(() => expect(document.querySelector('[data-md-bubble]')).not.toBeNull())
  return { editor, bubble: document.querySelector<HTMLElement>('[data-md-bubble]')!, field: document.body.appendChild(document.createElement('input')) }
}
const hides = (dispatch: { mock: { calls: unknown[][] } }) => dispatch.mock.calls.filter(([tr]) => (tr as { getMeta(key: string): unknown }).getMeta('formattingBubble$') === 'hide')

it('a focus that moves while the bubble is invisible dispatches no hide', async () => {
  const { editor, bubble, field } = await shown()
  // Hidden by its placement (its text scrolled away): the bubble keeps its place in the page, invisible.
  bubble.style.visibility = 'hidden'
  const dispatch = vi.spyOn(editor.view, 'dispatch')
  bubble.tabIndex = -1; bubble.focus(); field.focus()
  expect(hides(dispatch)).toEqual([])
  editor.destroy()
})

async function mounted() {
  const editor = new Editor({ element: document.body.appendChild(document.createElement('div')), extensions: [StarterKit], content: '<p>Text here</p>' })
  app = createApp({ render: () => h(FormattingToolbar, { editor, profile: 'rich', formatting: ['headings', 'bold', 'link'] }) }); app.mount(document.body.appendChild(document.createElement('div')))
  await nextTick()
  editor.view.focus()
  return editor
}
const altF10 = (editor: Editor) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', altKey: true, bubbles: true, cancelable: true }))
const key = (target: Element, name: string) => target.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }))

it('Alt+F10 right after a selection, before the bubble’s delayed show, goes into the bubble', async () => {
  const editor = await mounted()
  editor.commands.setTextSelection({ from: 1, to: 5 })
  altF10(editor)
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('data-testid')).toBe('bubble-kind'))
  editor.destroy()
})

it('„Typ bloku“ is a menu: the focus on the current kind, arrows move, leaving it closes it', async () => {
  const editor = await mounted()
  editor.commands.setTextSelection({ from: 1, to: 5 })
  altF10(editor)
  const trigger = await vi.waitFor(() => { const element = document.querySelector<HTMLElement>('[data-testid=bubble-kind]'); expect(document.activeElement).toBe(element); return element! })
  trigger.click()
  await vi.waitFor(() => expect(document.activeElement?.matches('.bb-popover .bb-item.on')).toBe(true))
  key(document.activeElement!, 'ArrowDown')
  expect(document.activeElement?.textContent?.trim()).toBe('Nadpis 1')
  key(document.activeElement!, 'End')
  expect(document.activeElement?.textContent?.trim()).toBe('Nadpis 6')
  editor.view.focus()
  await vi.waitFor(() => expect(document.querySelector('.bb-popover')).toBeNull())
  editor.destroy()
})

it('Esc in „Typ bloku“ closes it and gives the focus back to its button, the next Esc to the text (J6.3)', async () => {
  const editor = await mounted()
  editor.commands.setTextSelection({ from: 1, to: 5 })
  altF10(editor)
  const trigger = await vi.waitFor(() => { const element = document.querySelector<HTMLElement>('[data-testid=bubble-kind]'); expect(document.activeElement).toBe(element); return element! })
  trigger.click()
  await vi.waitFor(() => expect(document.activeElement?.matches('.bb-popover .bb-item')).toBe(true))
  key(document.activeElement!, 'Escape')
  await nextTick()
  expect(document.querySelector('.bb-popover')).toBeNull()
  expect(document.activeElement).toBe(trigger)
  // Opened again and Esc on the button itself: the same.
  trigger.click(); await nextTick()
  trigger.focus(); key(trigger, 'Escape'); await nextTick()
  expect(document.querySelector('.bb-popover')).toBeNull()
  expect(document.activeElement).toBe(trigger)
  key(trigger, 'Escape')
  expect(document.activeElement).toBe(editor.view.dom)
  expect(editor.state.selection.from).toBe(1)
  editor.destroy()
})

it('„Odkaz“ puts the keyboard into the link field, from the mouse as from the keys', async () => {
  const editor = await mounted()
  editor.commands.setTextSelection({ from: 1, to: 5 })
  await vi.waitFor(() => expect(document.querySelector('[data-md-bubble]')).not.toBeNull())
  document.querySelector<HTMLElement>('[data-md-bubble] [aria-label="Odkaz"]')!.click()
  await vi.waitFor(() => expect(document.activeElement?.classList.contains('link-input')).toBe(true))
  editor.destroy()
})

it('a focus that moves after the editor is gone throws nothing', async () => {
  const { editor, field } = await shown()
  editor.destroy()
  const errors: unknown[] = []
  const report = (event: ErrorEvent) => { errors.push(event.error) }
  window.addEventListener('error', report)
  try { field.focus() } finally { window.removeEventListener('error', report) }
  expect(errors).toEqual([])
})
