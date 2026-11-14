// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import { createMarkdownKit } from '../../createMarkdownKit'
import { runTableAxisCommand } from './commands'

const editors: Editor[] = []
const frames = new Map<number, FrameRequestCallback>()
let frameId = 0

beforeEach(() => {
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => {
    frames.set(++frameId, callback)
    return frameId
  })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id) })
})
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  frames.clear()
  vi.restoreAllMocks()
  document.body.replaceChildren()
})

function renderFrame() {
  const pending = [...frames.values()]
  frames.clear()
  pending.forEach(callback => callback(0))
}
function mount() {
  const element = document.createElement('div')
  document.body.appendChild(element)
  const editor = new Editor({ element, content: '| Name | Status |\n| --- | --- |\n| Alpha | Ready |\n\nAfter', extensions: createMarkdownKit({ profile: 'rich' }).extensions })
  editors.push(editor)
  renderFrame()
  return editor
}
function pointer(type: string, y: number) {
  const event = new MouseEvent(type, { bubbles: true, clientY: y })
  Object.defineProperty(event, 'pointerId', { value: 1 })
  return event
}

describe('table control ownership and transactions', () => {
  it('cancels a captured column drag when a document edit invalidates its positions', () => {
    const editor = mount()
    const handle = editor.view.dom.querySelector<HTMLElement>('.tt-table-col-chip')!
    handle.dispatchEvent(pointer('pointerdown', 0))
    const move = pointer('pointermove', 0)
    Object.defineProperty(move, 'clientX', { value: 60 })
    document.dispatchEvent(move)
    expect(editor.view.dom.querySelector('[data-column-dragging]')).not.toBeNull()
    editor.commands.insertContentAt(0, { type: 'paragraph', content: [{ type: 'text', text: 'New paragraph' }] })
    expect(editor.view.dom.querySelector('[data-column-dragging]')).toBeNull()
    document.dispatchEvent(pointer('pointerup', 0))
    expect(editor.view.dom.querySelectorAll('th')[0].textContent).toBe('Name')
  })

  it('releases the column drag and its Escape ownership when the view unmounts', () => {
    const editor = mount()
    editor.view.dom.querySelector<HTMLElement>('.tt-table-col-chip')!.dispatchEvent(pointer('pointerdown', 0))
    const move = pointer('pointermove', 0)
    Object.defineProperty(move, 'clientX', { value: 60 })
    document.dispatchEvent(move)
    expect(document.querySelector('[data-md-overlay-consumes-escape="true"]')).not.toBeNull()
    editor.destroy()
    expect(document.querySelector('[data-md-overlay-consumes-escape="true"]')).toBeNull()
    expect(document.querySelector('[data-column-dragging]')).toBeNull()
  })

  it('draws controls only for the selected and the hovered table', () => {
    const element = document.createElement('div')
    document.body.appendChild(element)
    const md = '| A | B |\n| --- | --- |\n| 1 | 2 |\n\nmid\n\n| C | D |\n| --- | --- |\n| 3 | 4 |\n\n| E | F |\n| --- | --- |\n| 5 | 6 |'
    editors.push(new Editor({ element, content: md, extensions: createMarkdownKit({ profile: 'rich' }).extensions }))
    renderFrame()
    const controls = () => [...element.querySelectorAll('.tt-table-controls')].map(c => c.parentElement!.querySelector('th')!.textContent)
    expect(controls()).toEqual(['A'])
    element.querySelectorAll('th')[2]!.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }))
    renderFrame()
    expect(controls()).toEqual(['A', 'C'])
    element.querySelectorAll('th')[4]!.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }))
    renderFrame()
    expect(controls()).toEqual(['A', 'E'])
    expect(element.querySelector('.tt-table-controls svg')).toBeNull()
  })

  it('removes and restores controls when editability changes without a document edit', () => {
    const editor = mount()
    expect(editor.view.dom.querySelectorAll('.tt-table-controls')).toHaveLength(1)
    editor.setEditable(false)
    renderFrame()
    expect(editor.view.dom.querySelector('.tt-table-controls')).toBeNull()
    editor.setEditable(true)
    renderFrame()
    expect(editor.view.dom.querySelectorAll('.tt-table-controls')).toHaveLength(1)
  })

  it('moves the stable inserters through compositor offsets instead of layout coordinates', () => {
    const editor = mount()
    const row = editor.view.dom.querySelector<HTMLElement>('.tt-table-row-insert')!
    const column = editor.view.dom.querySelector<HTMLElement>('.tt-table-col-insert')!
    expect(row.style.top).toBe('')
    expect(column.style.left).toBe('')
    expect(row.style.getPropertyValue('--tt-insert-offset')).toMatch(/px$/)
    expect(column.style.getPropertyValue('--tt-insert-offset')).toMatch(/px$/)

    editor.view.dom.querySelector('th')!.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }))
    expect(row.hasAttribute('data-active')).toBe(true)
    expect(column.hasAttribute('data-active')).toBe(true)
    expect(row.style.top).toBe('')
    expect(column.style.left).toBe('')
  })

  it('moves the hidden inserters to the edges of a table that got smaller', () => {
    let width = 400, rowHeight = 40
    const rect = (left: number, top: number, w: number, h: number) =>
      ({ left, top, right: left + w, bottom: top + h, width: w, height: h, x: left, y: top, toJSON: () => ({}) }) as DOMRect
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
      if (this.matches('table')) return rect(0, 0, width, rowHeight * 2)
      if (this.matches('tr')) return rect(0, (this as HTMLTableRowElement).rowIndex * rowHeight, width, rowHeight)
      if (this.matches('th, td')) return this.matches(':last-child') ? rect(100, 0, width - 100, rowHeight) : rect(0, 0, 100, rowHeight)
      return rect(0, 0, 0, 0)
    })
    const editor = mount()
    const row = editor.view.dom.querySelector<HTMLElement>('.tt-table-row-insert')!
    const column = editor.view.dom.querySelector<HTMLElement>('.tt-table-col-insert')!
    expect([column.style.getPropertyValue('--tt-insert-offset'), row.style.getPropertyValue('--tt-insert-offset')]).toEqual(['400px', '80px'])
    width = 300; rowHeight = 30
    window.dispatchEvent(new Event('resize'))
    renderFrame()
    expect(editor.view.dom.querySelector('.tt-table-col-insert')).toBe(column)
    expect([column.style.getPropertyValue('--tt-insert-offset'), row.style.getPropertyValue('--tt-insert-offset')]).toEqual(['300px', '60px'])
  })

  it('cancels pending rendering and detached-table listeners on destroy', () => {
    const editor = mount()
    const table = editor.view.dom.querySelector('table')!
    const overlay = editor.view.dom.querySelector('.tt-table-controls')!
    const cell = table.querySelector('th')!
    editor.commands.insertContent('edit')
    editor.destroy()
    renderFrame()
    cell.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }))
    expect(overlay.querySelector('[data-active]')).toBeNull()
    expect(document.querySelector('.tt-table-controls')).toBeNull()
    expect(frames.size).toBe(0)
  })

  it('normalizes column widths within the structural edit and undoes both together', () => {
    const editor = mount()
    const tr = editor.state.tr
    tr.doc.descendants((node, pos) => {
      if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') tr.setNodeAttribute(pos, 'colwidth', [140])
    })
    editor.view.dispatch(tr)
    const before = editor.getJSON()
    const transactions = vi.fn()
    editor.on('transaction', ({ transaction }) => { if (transaction.docChanged) transactions() })
    expect(runTableAxisCommand(editor, 0, 'column', 0, 'insertAfter')).toBe(true)
    expect(transactions).toHaveBeenCalledTimes(1)
    const widths: unknown[] = []
    editor.state.doc.descendants(node => {
      if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') widths.push(node.attrs.colwidth)
    })
    expect(widths).toEqual(Array(6).fill(null))
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })

  it('cancels a resize before a document edit can move its captured row', () => {
    const editor = mount()
    const handle = editor.view.dom.querySelector<HTMLElement>('.tt-table-row-resize')!
    const row = editor.view.dom.querySelector('tr')!
    const original = row.style.height
    handle.dispatchEvent(pointer('pointerdown', 20))
    handle.dispatchEvent(pointer('pointermove', 90))
    expect(document.body.classList.contains('tt-row-resizing')).toBe(true)
    expect(row.style.height).not.toBe(original)
    editor.commands.insertContentAt(0, { type: 'paragraph', content: [{ type: 'text', text: 'Before' }] })
    handle.dispatchEvent(pointer('pointerup', 90))
    expect(document.body.classList.contains('tt-row-resizing')).toBe(false)
    expect(row.style.height).toBe(original)
    const heights: unknown[] = []
    editor.state.doc.descendants(node => { if (node.type.name === 'tableRow') heights.push(node.attrs.height) })
    expect(heights).toEqual([null, null])
  })

  it('releases an active resize when the editor unmounts', () => {
    const editor = mount()
    const handle = editor.view.dom.querySelector<HTMLElement>('.tt-table-row-resize')!
    handle.dispatchEvent(pointer('pointerdown', 20))
    handle.dispatchEvent(pointer('pointermove', 60))
    editor.destroy()
    handle.dispatchEvent(pointer('pointerup', 60))
    expect(document.body.classList.contains('tt-row-resizing')).toBe(false)
    expect(frames.size).toBe(0)
  })

  it('previews row resizing without document edits and commits one undoable change', () => {
    const editor = mount()
    const before = editor.getJSON()
    const handle = editor.view.dom.querySelector<HTMLElement>('.tt-table-row-resize')!
    const transactions = vi.fn()
    editor.on('transaction', ({ transaction }) => { if (transaction.docChanged) transactions() })
    handle.dispatchEvent(pointer('pointerdown', 20))
    handle.dispatchEvent(pointer('pointermove', 60))
    handle.dispatchEvent(pointer('pointermove', 80))
    expect(transactions).not.toHaveBeenCalled()
    handle.dispatchEvent(pointer('pointerup', 80))
    expect(transactions).toHaveBeenCalledTimes(1)
    expect(editor.state.doc.firstChild!.firstChild!.attrs.height).toBe(60)
    editor.commands.undo()
    expect(editor.getJSON()).toEqual(before)
  })
})
