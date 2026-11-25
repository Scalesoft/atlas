// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { createApp, h, nextTick, reactive, type App } from 'vue'
import type { Editor } from '@tiptap/core'
import { MarkdownEditor, type MarkdownPlugin } from '../index'

let app: App
beforeEach(() => { vi.stubGlobal('ResizeObserver', class { observe() {} disconnect() {} unobserve() {} }) })
afterEach(() => { app?.unmount(); document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

async function mount(options = {}) {
  let editor!: Editor
  const props = reactive({ showBlockAddButton: true, showBlockInserter: false, readonly: false,
    variant: 'document' as 'document' | 'compact', ...options })
  const plugins: MarkdownPlugin[] = [{ id: 'test.inserter', apiVersion: 1,
    create: () => ({ onAttach: context => { editor = context.editor } }) }]
  const host = document.createElement('div'); document.body.append(host)
  app = createApp({ render: () => h(MarkdownEditor, { ...props, modelValue: 'First\n\nSecond', ariaLabel: 'Insert blocks', plugins }) })
  app.mount(host); await nextTick()
  // jsdom cannot measure text ranges when ProseMirror scrolls the undo selection.
  editor.setOptions({ editorProps: { handleScrollToSelection: () => true } })
  const content = host.querySelector<HTMLElement>('.ProseMirror')!
  vi.spyOn(content, 'getBoundingClientRect').mockReturnValue(new DOMRect(40, 100, 400, 100))
  Array.from(content.children).forEach((element, i) => {
    vi.spyOn(element, 'getBoundingClientRect').mockReturnValue(new DOMRect(40, 100 + i * 60, 400, 40))
  })
  const hover = async (y: number, buttons = 0) => {
    content.dispatchEvent(new MouseEvent('pointermove', { bubbles: true, clientX: 240, clientY: y, buttons }))
    await nextTick()
  }
  const inserter = () => host.querySelector<HTMLButtonElement>('.md-block-inserter:not([inert]):not(.md-block-inserter-fade-leave-active) [data-testid="block-inserter"]')
  return { host, props, editor, content, hover, inserter }
}

it('toggles the two insertion controls independently without remounting content', async () => {
  const { host, props, content, hover, inserter } = await mount()
  expect(host.querySelector('[data-testid="block-add-btn"]')).not.toBeNull()
  expect(inserter()).toBeNull()
  props.showBlockAddButton = false; props.showBlockInserter = true; await nextTick()
  await hover(150)
  expect(host.querySelector('[data-testid="block-add-btn"]')).toBeNull()
  expect(host.querySelector('[data-testid="block-drag-btn"]')).not.toBeNull()
  expect(inserter()).not.toBeNull()
  props.showBlockAddButton = true; await nextTick()
  expect(host.querySelector('[data-testid="block-add-btn"]')).not.toBeNull()
  props.showBlockInserter = false; await nextTick()
  expect(inserter()).toBeNull()
  expect(host.querySelector('.ProseMirror')).toBe(content)
})

it.each([[88, '/FirstSecond'], [150, 'First/Second'], [212, 'FirstSecond/']])('inserts at boundary %i and isolates undo from earlier typing', async (y, expected) => {
  const { editor, hover, inserter } = await mount({ showBlockInserter: true })
  editor.commands.setTextSelection(6); editor.commands.insertContent('!')
  await hover(y)
  expect(inserter()).not.toBeNull()
  inserter()!.click(); await nextTick()
  expect(editor.state.doc.textContent).toBe(expected.replace('First', 'First!'))
  expect(editor.state.doc.childCount).toBe(3)
  editor.commands.undo()
  expect(editor.state.doc.textContent).toBe('First!Second')
  editor.commands.undo()
  expect(editor.state.doc.textContent).toBe('FirstSecond')
})

it('hides insertion over text, during selection and dragging, and after document changes', async () => {
  const { editor, content, hover, inserter } = await mount({ showBlockInserter: true })
  await hover(120); expect(inserter()).toBeNull()
  await hover(150); expect(inserter()).not.toBeNull()
  await hover(150, 1); expect(inserter()).toBeNull()
  content.dataset.blockDragging = 'true'
  await hover(150); expect(inserter()).toBeNull()
  delete content.dataset.blockDragging
  await hover(150); expect(inserter()).not.toBeNull()
  editor.commands.insertContent('Changed'); await nextTick()
  expect(inserter()).toBeNull()
})

it('supports keyboard insertion and removes controls when readonly or compact', async () => {
  const { editor, props, content, inserter } = await mount({ showBlockInserter: true })
  editor.commands.setTextSelection(2)
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'Insert', altKey: true, bubbles: true }))
  await nextTick()
  expect(inserter()).not.toBeNull()
  expect(document.activeElement).toBe(inserter())
  inserter()!.click(); await nextTick()
  expect(editor.state.doc.textContent).toBe('First/Second')
  props.readonly = true; await nextTick()
  expect(inserter()).toBeNull()
  props.readonly = false; props.variant = 'compact'; await nextTick()
  expect(inserter()).toBeNull()
})

it('offers insertion after a tapped block with the side button disabled', async () => {
  const { editor, content, inserter } = await mount({ showBlockAddButton: false, showBlockInserter: true })
  const touch = new MouseEvent('pointerup', { bubbles: true })
  Object.defineProperty(touch, 'pointerType', { value: 'touch' })
  content.querySelector('p')!.dispatchEvent(touch)
  await nextTick()
  expect(inserter()).not.toBeNull()
  inserter()!.click(); await nextTick()
  expect(editor.state.doc.textContent).toBe('First/Second')
})

it('stacks the side "+" under the grip in one column, the grip first in reading and focus order', async () => {
  const { host, props } = await mount()
  const row = host.querySelector<HTMLElement>('.dh-row')!
  expect([...row.children].map(element => element.getAttribute('data-testid'))).toEqual(['block-drag-btn', 'block-add-btn'])
  props.showBlockAddButton = false; await nextTick()
  expect([...row.children].map(element => element.getAttribute('data-testid'))).toEqual(['block-drag-btn'])
})

it('Alt+F10 then Enter on the grip opens the block menu with the keys in the text; a row runs and the focus stays there (J4.9)', async () => {
  const { host, editor, content } = await mount()
  editor.commands.setTextSelection(9)
  content.focus()
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', altKey: true, bubbles: true, cancelable: true }))
  const grip = host.querySelector<HTMLElement>('[data-testid="block-drag-btn"]')!
  expect(document.activeElement).toBe(grip)
  grip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(document.activeElement).toBe(content)
  for (const key of 'nad 1') content.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  expect([...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(option => option.textContent!.trim())).toEqual(['Změnit na: Nadpis 1'])
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(editor.getJSON().content!.map(node => node.type).slice(0, 2)).toEqual(['paragraph', 'heading'])
  expect(editor.state.doc.textContent).toBe('FirstSecond')
  expect(document.activeElement).toBe(content)
})

it('Shift+F10 and the right button on the grip open the block menu as a click does (P2)', async () => {
  const { host, editor, content } = await mount()
  editor.commands.setTextSelection(9)
  content.focus()
  const grip = host.querySelector<HTMLElement>('[data-testid="block-drag-btn"]')!
  grip.dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', shiftKey: true, bubbles: true, cancelable: true }))
  expect(document.querySelector('[data-testid=intentions]')).not.toBeNull()
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(document.querySelector('[data-testid=intentions]')).toBeNull()
  grip.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))
  expect(document.querySelector('[data-testid=intentions]')).not.toBeNull()
})

it('Alt+F10 in a table: its row chip, Tab to its column chip and back, never out of the table; Esc to the cell (J6.4, F24)', async () => {
  const { host, editor, content } = await mount()
  const cell = (text: string) => ({ type: 'tableCell', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })
  editor.commands.insertContentAt(0, { type: 'table', content: [{ type: 'tableRow', content: [cell('a'), cell('b')] }, { type: 'tableRow', content: [cell('c'), cell('d')] }] })
  await vi.waitFor(() => expect(host.querySelector('.tt-table-controls [data-row-index="1"]')).not.toBeNull())
  let at = 0
  editor.state.doc.descendants((node, pos) => { if (node.isText && node.text === 'd') at = pos + 1 })
  content.focus(); editor.commands.setTextSelection(at)
  // jsdom's DOM selection inside a table cell reads back as a gap: the state's caret stands.
  ;(editor.view as unknown as { domObserver: { flush(): void } }).domObserver.flush = () => {}
  content.dispatchEvent(new KeyboardEvent('keydown', { key: 'F10', altKey: true, bubbles: true, cancelable: true }))
  const active = () => (document.activeElement as HTMLElement).dataset
  expect(active().rowIndex).toBe('1')
  const tab = (shiftKey = false) => { const event = new KeyboardEvent('keydown', { key: 'Tab', shiftKey, bubbles: true, cancelable: true }); document.activeElement!.dispatchEvent(event); return event }
  expect(tab().defaultPrevented).toBe(true)
  expect(active().colIndex).toBe('1')
  tab(); expect(active().rowIndex).toBe('1')
  tab(true); expect(active().colIndex).toBe('1')
  document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(document.activeElement).toBe(content)
  // The table's bubble menus update on a timer: let it run while the editor still stands.
  await new Promise(resolve => setTimeout(resolve, 300))
})

it('a click on the grip opens the block menu; keys and text typed with the focus elsewhere (the grip) go to it, never into the page (J4.9)', async () => {
  const { host, editor, content } = await mount()
  editor.commands.setTextSelection(9)
  const grip = host.querySelector<HTMLElement>('[data-testid="block-drag-btn"]')!
  grip.click()
  const titles = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(option => option.textContent!.trim())
  // The harmless rows first, the first one preselected; Smazat last.
  expect(titles()[0]).toMatch(/^Změnit na: /)
  expect(document.querySelector('[data-testid=intentions] [aria-selected=true]')?.textContent).toMatch(/Změnit na/)
  expect(titles().at(-1)).toMatch(/^Smazat/)
  // The focus on the grip (a toolbar button would be the same): a key and a key-less text input still filter the menu.
  grip.focus()
  for (const key of 'nad') expect(grip.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))).toBe(false)
  const typed = new InputEvent('beforeinput', { inputType: 'insertText', data: ' 2', bubbles: true, cancelable: true })
  content.dispatchEvent(typed)
  expect(typed.defaultPrevented).toBe(true)
  expect(titles()).toEqual(['Změnit na: Nadpis 2'])
  expect(editor.state.doc.textContent).toBe('FirstSecond')
  // Esc closes it and gives the focus back to the text.
  grip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(document.querySelector('[data-testid=intentions]')).toBeNull()
  expect(document.activeElement).toBe(content)
  // Closed, the keys are the page's again.
  expect(content.dispatchEvent(new InputEvent('beforeinput', { inputType: 'insertText', data: 'x', bubbles: true, cancelable: true }))).toBe(true)
})