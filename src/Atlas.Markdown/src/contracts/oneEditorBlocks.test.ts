// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { Editor, type JSONContent } from '@tiptap/core'
import { CellSelection } from '@tiptap/pm/tables'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownKit } from '../createMarkdownKit'
import { parseMarkdownContent, serializeMarkdownContent } from '../core'

/*
 * Contract of the one-editor page model.
 * Tables, callouts and the timed list are real tests; the other blocks stay pending until their pass.
 * jsdom has no layout, so a "click" is a selection placed in the cell/paragraph the click would land in.
 */
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
// jsdom has no ClipboardEvent; view.pasteHTML/pasteText only pass it on.
;(globalThis as { ClipboardEvent?: unknown }).ClipboardEvent ??= class extends Event { clipboardData = null }

const editors: Editor[] = []
const frames = new Map<number, FrameRequestCallback>()
let frameId = 0
beforeEach(() => {
  vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date('2026-11-03T16:19:00Z'))
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation(callback => { frames.set(++frameId, callback); return frameId })
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(id => { frames.delete(id) })
})
afterEach(() => {
  editors.splice(0).forEach(editor => editor.destroy())
  frames.clear(); vi.useRealTimers(); vi.restoreAllMocks(); document.body.replaceChildren()
})
const renderFrame = () => { const pending = [...frames.values()]; frames.clear(); pending.forEach(callback => callback(0)) }

function mount(content: JSONContent[]) {
  const element = document.createElement('div'); document.body.appendChild(element)
  const editor = new Editor({ element, content: { type: 'doc', content }, extensions: createMarkdownKit({ profile: 'rich' }).extensions })
  editors.push(editor); renderFrame()
  return editor
}
const p = (text?: string): JSONContent => ({ type: 'paragraph', ...text ? { content: [{ type: 'text', text }] } : {} })
const cell = (...texts: (string | undefined)[]): JSONContent => ({ type: 'tableCell', content: texts.map(p) })
const head = (text: string): JSONContent => ({ type: 'tableHeader', content: [p(text)] })
const table = (...rows: JSONContent[][]): JSONContent => ({ type: 'table', content: rows.map(content => ({ type: 'tableRow', content })) })
const grid = (): JSONContent => table([head('A'), head('B')], [cell('Alpha'), cell('Beta')], [cell('Gamma'), cell('Delta')])
const callout = (variant: string, ...texts: (string | undefined)[]): JSONContent => ({ type: 'callout', attrs: { variant }, content: texts.map(p) })
const item = (text?: string): JSONContent => ({ type: 'timedItem', content: [p(text)] })
const timed = (...texts: (string | undefined)[]): JSONContent => ({ type: 'timedList', content: texts.map(item) })
const NOW = '3. 11. 2026 17:19 — '

/** Caret at `offset` (default: the end) of the first textblock holding `text`. */
function caret(editor: Editor, text: string, offset = text.length) {
  let found = -1
  editor.state.doc.descendants((node, pos) => { if (found < 0 && node.isTextblock && node.textContent === text) found = pos + 1 + offset })
  if (found < 0) throw new Error(`no textblock "${text}"`)
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, found)))
}
function press(editor: Editor, key: string, mods: { shift?: boolean; ctrl?: boolean } = {}) {
  const event = new KeyboardEvent('keydown', { key, shiftKey: mods.shift, ctrlKey: mods.ctrl, bubbles: true, cancelable: true })
  editor.view.dom.dispatchEvent(event)
  return event
}
function type(editor: Editor, text: string) {
  for (const char of text) {
    const { from, to } = editor.state.selection
    if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
  }
}
const count = (editor: Editor, name: string) => { let n = 0; editor.state.doc.descendants(node => { if (node.type.name === name) n++ }); return n }
const texts = (editor: Editor, name: string) => { const found: string[] = []; editor.state.doc.descendants(node => { if (node.type.name === name) found.push(node.textContent) }); return found }
const cellTexts = (editor: Editor) => [...texts(editor, 'tableHeader'), ...texts(editor, 'tableCell')]
const doc = (editor: Editor) => editor.getJSON()
const cellSelection = (editor: Editor, from: string, to = from) => {
  const cells: number[] = []; const wanted = [from, to]
  editor.state.doc.descendants((node, pos) => { if ((node.type.name === 'tableCell' || node.type.name === 'tableHeader') && wanted.includes(node.textContent)) cells[wanted.indexOf(node.textContent)] = pos })
  editor.view.dispatch(editor.state.tr.setSelection(CellSelection.create(editor.state.doc, cells[0], cells[1])))
}
/** What a Ctrl+C would put on the clipboard for the current selection. */
const copied = (editor: Editor) => {
  const { dom, text } = editor.view.serializeForClipboard(editor.state.selection.content())
  return { html: dom.innerHTML, text }
}
const paste = (editor: Editor, html: string) => editor.view.pasteHTML(html)

describe('one editor: tables', () => {
  it('click and caret: the caret sits in the cell that was clicked, text is typed there', () => {
    const editor = mount([grid()])
    caret(editor, 'Beta', 2)
    expect(editor.state.selection.$from.node(-1).type.name).toBe('tableCell')
    type(editor, 'x')
    expect(cellTexts(editor)).toEqual(['A', 'B', 'Alpha', 'Bexta', 'Gamma', 'Delta'])
  })

  it('Enter inside a cell adds a paragraph in that cell and never leaves the table, also in an empty last paragraph', () => {
    const editor = mount([grid(), p('After')])
    caret(editor, 'Alpha'); press(editor, 'Enter')
    expect(count(editor, 'table')).toBe(1)
    expect(editor.state.selection.$from.node(-1).childCount).toBe(2)
    press(editor, 'Enter')
    expect(editor.state.selection.$from.node(-1).childCount).toBe(3)
    expect(count(editor, 'table')).toBe(1)
    expect(editor.state.doc.lastChild!.textContent).toBe('After')
  })

  it('Backspace at the start of a cell never merges cells; Delete at the end never either', () => {
    const editor = mount([grid()])
    const before = cellTexts(editor)
    caret(editor, 'Beta', 0); press(editor, 'Backspace')
    expect(cellTexts(editor)).toEqual(before)
    caret(editor, 'Alpha'); press(editor, 'Delete')
    expect(cellTexts(editor)).toEqual(before)
    caret(editor, 'Gamma', 0); press(editor, 'Backspace')
    expect(cellTexts(editor)).toEqual(before)
  })

  it('Backspace at the start of a second paragraph in a cell merges only inside the cell', () => {
    const editor = mount([table([head('A'), head('B')], [cell('one', 'two'), cell('Beta')])])
    caret(editor, 'two', 0); press(editor, 'Backspace')
    expect(texts(editor, 'tableCell')).toEqual(['onetwo', 'Beta'])
  })

  it('Backspace/Delete on a CellSelection clears the cells and keeps the table', () => {
    const editor = mount([grid()])
    cellSelection(editor, 'Alpha', 'Delta'); press(editor, 'Backspace')
    expect(count(editor, 'table')).toBe(1)
    expect(cellTexts(editor)).toEqual(['A', 'B', '', '', '', ''])
    editor.commands.undo()
    cellSelection(editor, 'Alpha'); press(editor, 'Delete')
    expect(count(editor, 'table')).toBe(1)
    expect(cellTexts(editor)).toEqual(['A', 'B', '', 'Beta', 'Gamma', 'Delta'])
  })

  it('Tab moves to the next cell, Shift+Tab back; Tab in the last cell adds a row, as one undo step', () => {
    const editor = mount([grid()])
    caret(editor, 'Alpha'); press(editor, 'Tab')
    expect(editor.state.selection.$from.parent.textContent).toBe('Beta')
    press(editor, 'Tab', { shift: true })
    expect(editor.state.selection.$from.parent.textContent).toBe('Alpha')
    caret(editor, 'Delta'); press(editor, 'Tab')
    expect(count(editor, 'tableRow')).toBe(4)
    expect(editor.state.selection.$from.node(-2).childCount).toBe(2)
    editor.commands.undo()
    expect(count(editor, 'tableRow')).toBe(3)
  })

  it('row and column insert are each one undo step', () => {
    const editor = mount([grid()])
    caret(editor, 'Alpha')
    editor.commands.addRowAfter(); expect(count(editor, 'tableRow')).toBe(4)
    editor.commands.undo(); expect(count(editor, 'tableRow')).toBe(3)
    editor.commands.addColumnAfter(); expect(texts(editor, 'tableRow')[0]).toBeDefined()
    expect(editor.state.doc.firstChild!.firstChild!.childCount).toBe(3)
    editor.commands.undo(); expect(editor.state.doc.firstChild!.firstChild!.childCount).toBe(2)
  })

  it('copy of a CellSelection is an HTML table and tab-separated text; Markdown keeps the grid', () => {
    const editor = mount([grid()])
    cellSelection(editor, 'Alpha', 'Delta')
    const { html, text } = copied(editor)
    expect(html).toContain('<table')
    expect(text).toBe('Alpha\tBeta\nGamma\tDelta')
    const markdown = serializeMarkdownContent(editor, editor.state.doc)
    expect(parseMarkdownContent(editor, markdown).firstChild!.type.name).toBe('table')
    expect(markdown).toContain('| Alpha | Beta |')
  })

  it('pasting a table into a cell nests nothing; in a paragraph it inserts a table', () => {
    const editor = mount([grid(), p('After')])
    caret(editor, 'Gamma')
    paste(editor, '<table><tbody><tr><td>x</td><td>y</td></tr></tbody></table>')
    expect(count(editor, 'table')).toBe(1)
    expect(editor.state.doc.firstChild!.descendants.length).toBeUndefined
    let nested = false
    editor.state.doc.descendants((node, pos) => { if (node.type.name === 'table' && editor.state.doc.resolve(pos).depth > 0) nested = true })
    expect(nested).toBe(false)
    expect(cellTexts(editor).join('|')).toContain('x')
    caret(editor, 'After')
    paste(editor, '<table><tbody><tr><td>q</td></tr></tbody></table>')
    expect(count(editor, 'table')).toBe(2)
  })

  it('pasted plain text into a cell stays in the cell', () => {
    const editor = mount([grid(), p('After')])
    caret(editor, 'Beta')
    editor.view.pasteText('one\ntwo')
    expect(count(editor, 'table')).toBe(1)
    expect(cellTexts(editor).slice(0, 2)).toEqual(['A', 'B'])
    expect(editor.state.doc.lastChild!.textContent).toBe('After')
    expect(cellTexts(editor).join('')).toContain('Betaone')
  })

  it('controls exist only for the selected table; none when editable=false, none on hover, and they return with the content and caret kept', () => {
    const editor = mount([grid(), p('mid'), grid()])
    caret(editor, 'Alpha', 2)
    const controls = () => editor.view.dom.querySelectorAll('.tt-table-controls').length
    expect(controls()).toBe(1)
    const json = doc(editor), at = editor.state.selection.from
    editor.setEditable(false); renderFrame()
    expect(controls()).toBe(0)
    editor.view.dom.querySelectorAll('th')[2]!.dispatchEvent(new MouseEvent('mouseover', { bubbles: true })); renderFrame()
    expect(controls()).toBe(0)
    expect(editor.view.dom.querySelector('.tt-table-col-chip, .tt-table-row-chip')).toBeNull()
    expect(doc(editor)).toEqual(json)
    expect(editor.state.selection.from).toBe(at)
    editor.setEditable(true); renderFrame()
    expect(controls()).toBe(1)
    expect(doc(editor)).toEqual(json)
    expect(editor.state.selection.from).toBe(at)
  })

  it('reader: Enter, Backspace and typing change nothing, copy still works', () => {
    const editor = mount([grid()])
    editor.setEditable(false)
    caret(editor, 'Alpha'); const json = doc(editor)
    press(editor, 'Enter'); press(editor, 'Backspace'); press(editor, 'Tab')
    expect(doc(editor)).toEqual(json)
    cellSelection(editor, 'Alpha', 'Delta')
    expect(copied(editor).text).toBe('Alpha\tBeta\nGamma\tDelta')
  })
})

describe('one editor: callouts', () => {
  it('click and caret: the caret sits in the callout paragraph and text is typed there', () => {
    const editor = mount([callout('warning', 'Careful')])
    caret(editor, 'Careful', 3); type(editor, '!')
    expect(editor.state.selection.$from.node(-1).type.name).toBe('callout')
    expect(editor.state.doc.firstChild!.textContent).toBe('Car!eful')
  })

  it('Enter splits a paragraph; Enter in an empty last paragraph exits the callout', () => {
    const editor = mount([callout('note', 'one')])
    caret(editor, 'one'); press(editor, 'Enter')
    expect(editor.state.doc.firstChild!.childCount).toBe(2)
    press(editor, 'Enter')
    expect(editor.state.doc.firstChild!.type.name).toBe('callout')
    expect(editor.state.doc.firstChild!.childCount).toBe(1)
    expect(editor.state.selection.$from.depth).toBe(1)
    expect(editor.state.doc.child(1).type.name).toBe('paragraph')
  })

  it('Backspace at the start of the first paragraph lifts it out of the callout, not into the previous block', () => {
    const editor = mount([p('before'), callout('tip', 'inside')])
    caret(editor, 'inside', 0); press(editor, 'Backspace')
    expect(count(editor, 'callout')).toBe(0)
    expect(editor.state.doc.child(0).textContent).toBe('before')
    expect(editor.state.doc.child(1).textContent).toBe('inside')
    expect(editor.state.selection.$from.parentOffset).toBe(0)
  })

  it.each([
    ['quote', { type: 'blockquote', content: [p('before')] }],
    ['list', { type: 'bulletList', content: [{ type: 'listItem', content: [p('before')] }] }],
    ['table', table([head('before')])],
  ] as [string, JSONContent][])('Backspace at the start of a callout after a %s lifts its paragraph out, nothing merges', (_, previous) => {
    const editor = mount([previous, callout('tip', 'inside', 'second')])
    caret(editor, 'inside', 0); press(editor, 'Backspace')
    expect(editor.state.doc.child(0).textContent).toBe('before')
    expect(editor.state.doc.child(1).type.name).toBe('paragraph')
    expect(editor.state.doc.child(1).textContent).toBe('inside')
    expect(editor.state.doc.child(2).type.name).toBe('callout')
    expect(editor.state.doc.child(2).textContent).toBe('second')
    expect(editor.state.selection.$from.parent.textContent).toBe('inside')
  })

  it('Backspace in an empty first paragraph lifts it out and keeps the rest of the callout', () => {
    const editor = mount([p('before'), callout('tip', undefined, 'second')])
    caret(editor, ''); press(editor, 'Backspace')
    expect(editor.state.doc.child(0).textContent).toBe('before')
    expect(editor.state.doc.child(1).type.name).toBe('paragraph')
    expect(editor.state.doc.child(2).type.name).toBe('callout')
    expect(editor.state.doc.child(2).textContent).toBe('second')
  })

  it('Backspace at the start of a later paragraph merges inside the callout only', () => {
    const editor = mount([callout('note', 'a', 'b')])
    caret(editor, 'b', 0); press(editor, 'Backspace')
    expect(doc(editor).content![0].content).toHaveLength(1)
    expect(editor.state.doc.firstChild!.textContent).toBe('ab')
  })

  it('the variant is changeable by a command with a variant, undoable in ONE step, the text kept', () => {
    const editor = mount([callout('note', 'a', 'b')])
    caret(editor, 'a')
    editor.commands.setCalloutVariant('warning')
    expect(editor.state.doc.firstChild!.attrs.variant).toBe('warning')
    expect(editor.state.doc.firstChild!.textContent).toBe('ab')
    editor.commands.undo()
    expect(editor.state.doc.firstChild!.attrs.variant).toBe('note')
    expect(editor.state.doc.firstChild!.textContent).toBe('ab')
  })

  it('insert-callout with a variant creates a callout that undoes in one step', () => {
    const editor = mount([p('x')])
    caret(editor, 'x')
    editor.chain().focus().insertContent({ type: 'callout', attrs: { variant: 'caution' }, content: [p()] }).run()
    expect(editor.state.doc.content.content.some(node => node.type.name === 'callout' && node.attrs.variant === 'caution')).toBe(true)
    editor.commands.undo()
    expect(count(editor, 'callout')).toBe(0)
  })

  it('copy keeps the variant: HTML carries it, Markdown round-trips it', () => {
    const editor = mount([callout('warning', 'Careful')])
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 1, 8)))
    editor.commands.selectAll()
    const { html } = copied(editor)
    expect(html).toContain('data-variant="warning"')
    const markdown = serializeMarkdownContent(editor, editor.state.doc)
    expect(markdown).toContain('[!WARNING]')
    expect(parseMarkdownContent(editor, markdown).firstChild!.attrs.variant).toBe('warning')
  })

  it('paste into a callout keeps it a callout', () => {
    const editor = mount([callout('tip', 'abc'), p('After')])
    caret(editor, 'abc')
    editor.view.pasteText(' more')
    expect(count(editor, 'callout')).toBe(1)
    expect(editor.state.doc.firstChild!.textContent).toBe('abc more')
    paste(editor, '<p>one</p><p>two</p>')
    expect(count(editor, 'callout')).toBe(1)
    expect(editor.state.doc.lastChild!.textContent).toBe('After')
    expect(editor.state.doc.firstChild!.textContent).toContain('onetwo'.slice(0, 3))
  })

  it('reader: no variant menu or toolbar opens, keys change nothing, setEditable keeps content and caret', () => {
    const editor = mount([callout('important', 'Careful')])
    caret(editor, 'Careful', 4)
    const json = doc(editor), at = editor.state.selection.from
    editor.setEditable(false)
    const label = editor.view.dom.querySelector<HTMLElement>('.md-callout-label')!
    label.click(); label.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(document.querySelector('[data-variant="warning"][role="menuitemradio"], .md-context-choices')).toBeNull()
    press(editor, 'Backspace'); press(editor, 'Enter')
    expect(doc(editor)).toEqual(json)
    expect(label.textContent).not.toBe('')
    expect(editor.state.selection.from).toBe(at)
    editor.setEditable(true)
    expect(doc(editor)).toEqual(json)
    expect(editor.view.dom.querySelector('.md-callout-label')).toBe(label)
  })
})

describe('one editor: timed list', () => {
  const stamps = (editor: Editor) => texts(editor, 'timedItem')

  it('click and caret: the caret sits in the item text and typing goes there', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`); type(editor, '!')
    expect(stamps(editor)).toEqual([`${NOW}first!`])
  })

  it('Enter adds an item with a new stamp, the caret after it', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`); press(editor, 'Enter')
    expect(stamps(editor)).toEqual([`${NOW}first`, NOW])
    expect(editor.state.selection.$from.parentOffset).toBe(NOW.length)
  })

  it('Enter in an item holding only its stamp (the last one) leaves the list', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`); press(editor, 'Enter'); press(editor, 'Enter')
    expect(count(editor, 'timedItem')).toBe(1)
    expect(editor.state.doc.lastChild!.type.name).toBe('paragraph')
  })

  it('Backspace right after a stamp removes the stamp, the next Backspace the item', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`); press(editor, 'Enter')
    press(editor, 'Backspace'); expect(stamps(editor)).toEqual([`${NOW}first`, ''])
    press(editor, 'Backspace'); expect(stamps(editor)).toEqual([`${NOW}first`])
    expect(editor.state.selection.$from.parent.textContent).toBe(`${NOW}first`)
  })

  it('Tab sinks an item under the previous one, Shift+Tab lifts it back', () => {
    const editor = mount([timed(`${NOW}a`, `${NOW}b`)])
    caret(editor, `${NOW}b`); press(editor, 'Tab')
    expect(editor.state.doc.firstChild!.childCount).toBe(1)
    expect(count(editor, 'timedList')).toBe(2)
    press(editor, 'Tab', { shift: true })
    expect(editor.state.doc.firstChild!.childCount).toBe(2)
    expect(count(editor, 'timedList')).toBe(1)
  })

  it('Delete at the end of an item joins the next one', () => {
    const editor = mount([timed('a', 'b')])
    caret(editor, 'a'); press(editor, 'Delete')
    expect(stamps(editor)).toEqual(['ab'])
  })

  it('Mod-Shift-D toggles a paragraph to a timed list and back, one undo step each', () => {
    const editor = mount([p('note')])
    caret(editor, 'note')
    expect(press(editor, 'd', { ctrl: true, shift: true }).defaultPrevented).toBe(true)
    expect(count(editor, 'timedList')).toBe(1)
    editor.commands.undo(); expect(count(editor, 'timedList')).toBe(0)
    editor.commands.redo(); press(editor, 'd', { ctrl: true, shift: true })
    expect(count(editor, 'timedList')).toBe(0)
  })

  it('Markdown round trip keeps the stamps and the list; HTML copy keeps the stamps as text', () => {
    const editor = mount([timed(`${NOW}first`, '30. 10. 2026 — second')])
    const markdown = serializeMarkdownContent(editor, editor.state.doc)
    const back = parseMarkdownContent(editor, markdown)
    expect(back.firstChild!.type.name).toBe('timedList')
    expect(back.firstChild!.children.map(entry => entry.textContent)).toEqual([`${NOW}first`, '30. 10. 2026 — second'])
    editor.commands.selectAll()
    expect(copied(editor).text).toContain(`${NOW}first`)
  })

  it('paste of plain lines into a timed list makes items and stays in the list', () => {
    const editor = mount([timed(`${NOW}first`), p('After')])
    caret(editor, `${NOW}first`)
    editor.view.pasteText('x\ny')
    expect(count(editor, 'timedList')).toBe(1)
    expect(editor.state.doc.lastChild!.textContent).toBe('After')
    expect(stamps(editor)).toEqual([`${NOW}firstx`, 'y'])
  })

  it('typing inside or before a stamp never turns it into plain text: the text goes right after the stamp', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`, 3); type(editor, 'x')
    caret(editor, `${NOW}xfirst`, 0); type(editor, 'y')
    expect(stamps(editor)).toEqual([`${NOW}yxfirst`])
    expect(editor.view.dom.querySelector('.tt-timed-date')).not.toBeNull()
  })

  it('Backspace right after a stamp with text, or Delete inside it, removes the whole stamp', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`, NOW.length); press(editor, 'Backspace')
    expect(stamps(editor)).toEqual(['first'])
    editor.commands.undo()
    caret(editor, `${NOW}first`, 4); press(editor, 'Delete')
    expect(stamps(editor)).toEqual(['first'])
  })

  it('F2, Enter inside the stamp and a click on it edit the stamp; an invalid value keeps the old one', () => {
    const editor = mount([timed(`${NOW}first`)])
    const input = () => document.querySelector<HTMLInputElement>('.md-context-popover input[name="stamp"]')
    caret(editor, `${NOW}first`, 2)
    expect(press(editor, 'F2').defaultPrevented).toBe(true)
    expect(input()!.value).toBe('3. 11. 2026 17:19')
    input()!.value = '31. 2. 2026 25:00'; input()!.form!.requestSubmit()
    expect(stamps(editor)).toEqual([`${NOW}first`])
    expect(document.querySelector('.md-context-popover [role="alert"]')!.textContent).not.toBe('')
    input()!.value = '4. 11. 2026 8:05'; input()!.form!.requestSubmit()
    expect(input()).toBeNull()
    expect(stamps(editor)).toEqual(['4. 11. 2026 08:05 — first'])
    editor.commands.undo()
    expect(stamps(editor)).toEqual([`${NOW}first`])
    caret(editor, `${NOW}first`, 4); press(editor, 'Enter')
    expect(count(editor, 'timedItem')).toBe(1)
    expect(input()!.value).toBe('3. 11. 2026 17:19')
    input()!.form!.querySelector<HTMLButtonElement>('button[type="button"]')!.click()
    const chip = editor.view.dom.querySelector('.tt-timed-time')!
    expect(editor.view.someProp('handleClick', handle => handle(editor.view, editor.state.selection.from, { target: chip } as unknown as MouseEvent))).toBe(true)
    expect(input()).not.toBeNull()
  })

  it('reader: stamps are text, nothing opens, keys change nothing, setEditable keeps content and caret', () => {
    const editor = mount([timed(`${NOW}first`)])
    caret(editor, `${NOW}first`, 5)
    const json = doc(editor), at = editor.state.selection.from
    editor.setEditable(false)
    const chip = editor.view.dom.querySelector<HTMLElement>('.tt-timed-date')!
    chip.click()
    expect(document.querySelector('[role="dialog"], .md-context-popover, input')).toBeNull()
    expect(editor.view.dom.textContent).toContain(`${NOW}first`)
    press(editor, 'Enter'); press(editor, 'Backspace'); press(editor, 'Tab'); press(editor, 'd', { ctrl: true, shift: true })
    expect(doc(editor)).toEqual(json)
    editor.setEditable(true)
    expect(doc(editor)).toEqual(json)
    expect(editor.state.selection.from).toBe(at)
  })
})

/*
 * Pending: the other blocks of the same contract (headings, page links and chips, chrome) become real tests in their own pass.
 */
const contract = ['click and caret', 'Enter', 'Backspace and Delete at the edges', 'copy', 'paste', 'undo as one step', 'keyboard reach', 'stub hydrates without a jump', 'setEditable keeps content and caret']
const blocks: Record<string, string[]> = {
  'page links and chips': [...contract, 'Backspace after a chip returns its text', 'click follows the link for writer and reader (J3.5), Enter/F2 edits the selected chip', 'pasted Atlas URL becomes a chip'],
  chrome: ['every removed toolbar function is reachable without it', 'block handle menu turns a block into another type', 'bubble menu is keyboard reachable', 'strike, inline code and heading shortcuts are in the keyboard help', 'callout is a turn-into target'],
}

for (const [block, behaviours] of Object.entries(blocks)) {
  describe(`one editor: ${block}`, () => {
    for (const behaviour of behaviours) it.todo(behaviour)
  })
}
