// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { Schema } from '@tiptap/pm/model'
import { moduleDocumentSnapshot } from './documentSnapshot'
import { openMarkdownContextPopover } from './contextToolbar'
import { renderImagePanel } from '../images/imageControls'
import { imagePresentation } from '../images/presentation'
import { renderTableRails, type TableRailOptions } from '../table/rails'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
it('shares an owned frozen document snapshot within one state without freezing PM attrs', () => {
  const schema = new Schema({ nodes: { doc: { content: 'block*' }, text: {}, widget: { group: 'block', attrs: { data: {} } } } })
  const data = { nested: { text: 'before' } }, doc = schema.node('doc', null, [schema.node('widget', { data })]), state = {}
  const toJSON = vi.spyOn(doc, 'toJSON'), first = moduleDocumentSnapshot(doc, state)
  expect(moduleDocumentSnapshot(doc, state)).toBe(first); expect(toJSON).toHaveBeenCalledOnce()
  const captured = first.content![0]!.attrs!.data as { nested: { text: string } }
  expect(Object.isFrozen(captured.nested)).toBe(true)
  expect(Object.isFrozen(data)).toBe(false)
  data.nested.text = 'after'
  expect(captured.nested.text).toBe('before')
  expect(moduleDocumentSnapshot(doc, {}).content![0]!.attrs!.data).toEqual({ nested: { text: 'after' } })
})
it('leaves a property panel and its draft open for composition Escape and submit', () => {
  const host = document.createElement('div'), anchor = document.createElement('button'); document.body.append(host, anchor)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<p>text</p>' }); editors.push(editor)
  const apply = vi.fn()
  openMarkdownContextPopover({ editor, anchor, title: 'Image text', render(container, close) {
    const controls = renderImagePanel({ container, panel: 'text', src: '/a.png', alt: '', initial: imagePresentation({}), preview: vi.fn(), close, apply })
    return controls.destroy
  } })
  const input = document.querySelector('textarea')!, form = input.form!
  input.dispatchEvent(new CompositionEvent('compositionstart', { bubbles: true }))
  const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
  input.dispatchEvent(escape); form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  expect(escape.defaultPrevented).toBe(false); expect(form.isConnected).toBe(true); expect(apply).not.toHaveBeenCalled()
  input.dispatchEvent(new CompositionEvent('compositionend', { bubbles: true }))
  form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
  expect(apply).toHaveBeenCalledOnce(); expect(form.isConnected).toBe(false)
})
it('retains focused table rails, updates selection and uses current callback ownership', () => {
  const root = document.createElement('div'); root.innerHTML = '<table><tbody><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></tbody></table><div></div>'; document.body.append(root)
  const table = root.querySelector('table')!, overlay = root.lastElementChild as HTMLDivElement
  const command = vi.fn(), second = vi.fn()
  const options: TableRailOptions = { richTableFeatures: true, runColumnCommand: command, runRowCommand: command,
    startColumnDrag: vi.fn(), startRowDrag: vi.fn(), startRowResize: vi.fn(), moveColumn: vi.fn(), moveRow: vi.fn() }
  renderTableRails(overlay, table, options)
  const chip = overlay.querySelector<HTMLButtonElement>('.tt-table-col-chip')!; chip.focus()
  renderTableRails(overlay, table, { ...options, runColumnCommand: second, selectedRange: () => ({ from: 0, count: 2 }) })
  expect(overlay.querySelector('.tt-table-col-chip')).toBe(chip); expect(document.activeElement).toBe(chip)
  expect(chip.getAttribute('aria-pressed')).toBe('true'); expect(chip.title).toContain('2 sloupce')
  chip.click(); expect(command).not.toHaveBeenCalled(); expect(second).toHaveBeenCalledOnce()
  chip.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', isComposing: true, bubbles: true }))
  expect(second).toHaveBeenCalledOnce()
  renderTableRails(overlay, table, options); expect(chip.dataset.selected).toBeUndefined()
  table.rows[0]!.insertCell().textContent = 'extra'; renderTableRails(overlay, table, options)
  expect(overlay.querySelectorAll('.tt-table-col-chip')).toHaveLength(3); expect(overlay.querySelector('.tt-table-col-chip')).not.toBe(chip)
})
it('places table rails in the scrolled content of a wide table, wherever it is scrolled when they are drawn', () => {
  const wrapper = document.createElement('div'); wrapper.innerHTML = '<table><tbody><tr><td>a</td></tr></tbody></table><div></div>'; document.body.append(wrapper)
  const table = wrapper.querySelector('table')!, overlay = wrapper.lastElementChild as HTMLDivElement
  // Scrolled 300 px: on screen the table starts 300 px left of the wrapper, in the wrapper's content it starts at its padding (28 px).
  Object.defineProperty(wrapper, 'scrollLeft', { value: 300 })
  wrapper.getBoundingClientRect = () => new DOMRect(100, 0, 400, 100)
  table.getBoundingClientRect = () => new DOMRect(100 + 28 - 300, 30, 900, 40)
  renderTableRails(overlay, table, { richTableFeatures: false, runColumnCommand: vi.fn(), runRowCommand: vi.fn(),
    startColumnDrag: vi.fn(), startRowDrag: vi.fn(), startRowResize: vi.fn(), moveColumn: vi.fn(), moveRow: vi.fn() })
  expect(overlay.style.left).toBe('28px')
})
