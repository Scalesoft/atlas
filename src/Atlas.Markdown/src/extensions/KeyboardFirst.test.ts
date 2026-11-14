// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController } from '../core'
import { editorForController, registerCollaborationAdapter } from '../core/internal'
import { configureMarkdownEditorView } from '../vue/controllerView'
import { createContactsPlugin } from '../plugins/contacts'
import { markdownKeyboardShortcuts, MARKDOWN_LEAVE_START_EVENT, MARKDOWN_SHORTCUT_HELP_EVENT } from './KeyboardFirst'

// The contact card's lazy runtime: imported cold inside the 1 s wait for its toolbar, it missed it under the changed.ps1 load (UX EP2-10).
beforeAll(async () => { await import('../plugins/contacts/runtime') })

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
Element.prototype.scrollIntoView ??= function () {}

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })

function mount(content: JSONContent[] = [{ type: 'paragraph' }], compact = false) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin()], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'contacts', version: 2 }], document: { type: 'doc', content } } } })
  controllers.push(controller)
  configureMarkdownEditorView(controller, { compact })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!; editor.view.focus()
  return editor
}
const press = (editor: Editor, key: string, init: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init })
  editor.view.dom.dispatchEvent(event)
  return event
}
/** Types like a keyboard: through ProseMirror's text input, so input rules run. */
function type(editor: Editor, text: string) {
  for (const char of text) {
    const { from, to } = editor.state.selection
    if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
  }
}
const paragraph = (text: string): JSONContent => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const caretIn = (editor: Editor, text: string, offset = 0) => {
  let found = -1
  editor.state.doc.descendants((node, pos) => { if (found < 0 && node.isTextblock && node.textContent === text) found = pos + 1 })
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, found + offset)))
}
const texts = (editor: Editor) => editor.state.doc.content.content.map(node => node.textContent)

describe('Tab never leaves the text by accident', () => {
  it('keeps Tab in a plain paragraph inside the editor; Escape and then Tab lets focus move on, whether the Esc selected the block or gave the caret back', () => {
    const editor = mount([paragraph('Text')]); caretIn(editor, 'Text', 2)
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(press(editor, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true)
    press(editor, 'Escape')
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    // Esc, Tab leaves the editor every time (the help's „Opustit editor“; review 2026-11-06: it sometimes took Esc, Esc, Tab).
    expect(press(editor, 'Tab').defaultPrevented).toBe(false)
    // A later Tab on the selected block without controls stays, both ways: the page around the editor never takes it (J4.5, J6.2).
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(press(editor, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true)
    press(editor, 'Escape')
    expect(editor.state.selection).toBeInstanceOf(TextSelection)
    expect(press(editor, 'Tab').defaultPrevented).toBe(false)
    // Any other key ends the escape: Tab is the editor's again.
    press(editor, 'ArrowLeft')
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
  })
  it('selects the block around a text range on Escape (J6.3: the bubble went with the Esc before), not a range over two blocks', () => {
    const editor = mount([paragraph('Jedna'), paragraph('Dva')])
    editor.commands.setTextSelection({ from: 2, to: 4 })
    press(editor, 'Escape')
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(editor.state.selection.from).toBe(0)
    editor.commands.setTextSelection({ from: 2, to: 10 })
    press(editor, 'Escape')
    expect(editor.state.selection).not.toBeInstanceOf(NodeSelection)
  })
  it('answers Tab last, after every other extension (tiptap reads priority 0 as 100: a table cell lost its Tab)', () => {
    const editor = mount()
    // Core's Esc step for a selected element comes after it on purpose (Esc only): these keys arm Esc-then-Tab first.
    const names = editor.extensionManager.extensions.map(extension => extension.name).filter(name => name !== 'markdownSelectedElementEscape')
    expect(names.at(-1)).toBe('markdownKeyboardFirst')
  })
  it('leaves Tab to the page in a compact field, where it walks the form', () => {
    const editor = mount([paragraph('Pole')], true); caretIn(editor, 'Pole')
    expect(press(editor, 'Tab').defaultPrevented).toBe(false)
  })
  it('still nests a list item and walks table cells', () => {
    const editor = mount([{ type: 'bulletList', content: [{ type: 'listItem', content: [paragraph('a')] }, { type: 'listItem', content: [paragraph('b')] }] }])
    caretIn(editor, 'b')
    press(editor, 'Tab')
    expect(editor.getJSON().content?.[0]?.content).toHaveLength(1)
    const table = mount([{ type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('x')] }, { type: 'tableCell', content: [paragraph('y')] }] }] }])
    caretIn(table, 'x')
    press(table, 'Tab')
    expect(table.state.selection.$from.parent.textContent).toBe('y')
    press(table, 'Tab')
    expect(table.getJSON().content?.[0]?.content).toHaveLength(2)
  })
  it('leaves past the whole editor, not to a control inside its text (UX ED-16)', () => {
    const editor = mount([paragraph('Text')]); caretIn(editor, 'Text')
    const inside = document.createElement('button'); inside.textContent = 'uvnitř'; editor.view.dom.append(inside)
    const after = document.createElement('button'); after.textContent = 'dál'; document.body.append(after)
    for (const button of [inside, after]) button.getClientRects = () => [new DOMRect(0, 0, 10, 10)] as unknown as DOMRectList
    // The first Esc selects the paragraph, the second gives the caret back and arms Tab to leave.
    press(editor, 'Escape'); press(editor, 'Escape')
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(after)
  })
  it('lets Escape and then Tab leave from a list item, a table cell and a code block too (UX ED-3)', () => {
    const list = mount([{ type: 'bulletList', content: [{ type: 'listItem', content: [paragraph('a')] }, { type: 'listItem', content: [paragraph('b')] }] }])
    caretIn(list, 'b')
    // Esc selects the list, the second gives the caret back; a table cell keeps its caret on the first.
    press(list, 'Escape'); press(list, 'Escape')
    expect(press(list, 'Tab').defaultPrevented).toBe(false)
    expect(list.getJSON().content?.[0]?.content).toHaveLength(2)
    const table = mount([{ type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', content: [paragraph('x')] }, { type: 'tableCell', content: [paragraph('y')] }] }] }])
    caretIn(table, 'x')
    press(table, 'Escape')
    expect(press(table, 'Tab', { shiftKey: true }).defaultPrevented).toBe(false)
    expect(table.state.selection.$from.parent.textContent).toBe('x')
    const code = mount([{ type: 'codeBlock', content: [{ type: 'text', text: 'let a' }] }])
    caretIn(code, 'let a')
    press(code, 'Escape'); press(code, 'Escape')
    expect(press(code, 'Tab').defaultPrevented).toBe(false)
    expect(code.state.doc.textContent).toBe('let a')
  })
  it('Tab on a selected block goes to its toolbar\'s first control, Esc there gives the block back selected (J4.5, J6.3)', async () => {
    const editor = mount([paragraph('a'), { type: 'callout', attrs: { variant: 'note' }, content: [paragraph('uvnitř')] }, paragraph('b')])
    const after = document.createElement('button'); after.textContent = 'Celá obrazovka'; document.body.append(after)
    after.getClientRects = () => [new DOMRect(0, 0, 10, 10)] as unknown as DOMRectList
    // Selected by a click or the grip; after an Esc the Tab leaves the editor (the help's „Escape, Tab“).
    editor.commands.setNodeSelection(editor.state.doc.child(0).nodeSize)
    expect((editor.state.selection as NodeSelection).node?.type.name).toBe('callout')
    await vi.waitFor(() => expect(document.querySelector('.md-context-toolbar:not([hidden]) button')).not.toBeNull())
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    const first = document.activeElement as HTMLElement
    expect(first.closest('.md-context-toolbar')).not.toBeNull()
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(document.activeElement).toBe(editor.view.dom)
    expect((editor.state.selection as NodeSelection).node?.type.name).toBe('callout')
  })
  it('Tab on a selected block without a toolbar goes to the next block\'s first control, Shift+Tab to the previous one\'s last (J6.2(4))', async () => {
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([new DOMRect(0, 0, 10, 10)] as unknown as DOMRectList)
    // A phone link and the card's download button: two controls, so first and last differ.
    const card = { type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jana', phones: ['123'] }] } } }
    const editor = mount([paragraph('a'), card, paragraph('b')])
    const cardDom = () => editor.view.dom.children[1] as HTMLElement
    const controls = () => [...cardDom().querySelectorAll<HTMLElement>('a[href],button,[tabindex]:not([tabindex="-1"])')].filter(element => element.tabIndex >= 0)
    await vi.waitFor(() => expect(controls().length).toBeGreaterThan(1))
    // Selected by a click or the grip (after an Esc, Tab leaves the editor instead).
    const selectLast = () => editor.commands.setNodeSelection(editor.state.doc.content.size - editor.state.doc.lastChild!.nodeSize)
    editor.commands.setNodeSelection(0)
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(controls()[0])
    // Esc there leaves the card selected (J6.3).
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect((editor.state.selection as NodeSelection).node?.type.name).toBe('contactCard')
    selectLast()
    expect(press(editor, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(controls().at(-1))
    // Without a control next to it (b is last, a is first) and nothing after the editor, Tab stays in the editor.
    selectLast(); editor.view.focus()
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(editor.view.dom)
  })
  it('Esc, Tab leaves the editor past the chips and checkboxes in its text, which are no Tab stops (F5: Alt+F10 reaches them)', () => {
    const editor = mount([paragraph('a'), { type: 'paragraph', content: [{ type: 'text', text: 'b ' }] }, paragraph('c')])
    // An in-text control of the next block (a requirement's check, a chip) is out of the Tab order: it is no neighbour control.
    const check = document.createElement('input'); check.type = 'checkbox'; check.tabIndex = -1; check.contentEditable = 'false'
    editor.view.dom.children[1]!.prepend(check)
    const after = document.createElement('button'); after.textContent = 'dál'; document.body.append(after)
    vi.spyOn(HTMLElement.prototype, 'getClientRects').mockReturnValue([new DOMRect(0, 0, 10, 10)] as unknown as DOMRectList)
    caretIn(editor, 'a'); press(editor, 'Escape')
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(press(editor, 'Tab').defaultPrevented).toBe(true)
    expect(document.activeElement).toBe(after)
  })
  it('takes a writer\'s page chip out of the Tab order and gives it back to a reader', async () => {
    const { createMarkdownChip } = await import('../features/chips/chip')
    const link = document.createElement('a')
    const chip = createMarkdownChip(document, { tag: 'a', href: '/p/1', label: 'Stránka', editable: true }, link)
    expect(link.tabIndex).toBe(-1)
    chip.update({ editable: false })
    expect(link.hasAttribute('tabindex')).toBe(false)
  })
})

describe('task checkboxes', () => {
  it('say they are inert for a reader (aria-disabled), not for a writer (review 2026-11-06)', () => {
    const editor = mount([{ type: 'taskList', content: [{ type: 'taskItem', attrs: { checked: false }, content: [paragraph('úkol')] }] }])
    const box = () => editor.view.dom.querySelector('li.tt-task-item > label > input')!
    expect(box().hasAttribute('aria-disabled')).toBe(false)
    editor.setEditable(false)
    expect(box().getAttribute('aria-disabled')).toBe('true')
    editor.setEditable(true)
    expect(box().hasAttribute('aria-disabled')).toBe(false)
  })
})

describe('block keys', () => {
  it('moves the block with the caret by Ctrl+Shift+ArrowDown/Up and keeps the caret in it; Alt+arrows stay the host\'s (folding)', () => {
    const editor = mount([paragraph('one'), paragraph('two'), paragraph('three')]); caretIn(editor, 'one', 2)
    expect(press(editor, 'ArrowDown', { ctrlKey: true, shiftKey: true }).defaultPrevented).toBe(true)
    expect(texts(editor)).toEqual(['two', 'one', 'three'])
    expect(editor.state.selection.empty).toBe(true)
    expect(editor.state.selection.$from.parent.textContent).toBe('one')
    expect(editor.state.selection.$from.parentOffset).toBe(2)
    press(editor, 'ArrowUp', { ctrlKey: true, shiftKey: true })
    expect(texts(editor)).toEqual(['one', 'two', 'three'])
    expect(press(editor, 'ArrowDown', { altKey: true }).defaultPrevented).toBe(false)
  })
  it('duplicates the block by Alt+Shift+ArrowDown with the caret in the copy', () => {
    const editor = mount([paragraph('one'), paragraph('two')]); caretIn(editor, 'one', 1)
    press(editor, 'ArrowDown', { altKey: true, shiftKey: true })
    expect(texts(editor)).toEqual(['one', 'one', 'two'])
    expect(editor.state.selection.$from.index(0)).toBe(1)
  })
  it('deletes the block by Ctrl+Shift+Backspace and puts the caret into the next one', () => {
    const editor = mount([paragraph('one'), paragraph('two')]); caretIn(editor, 'one', 1)
    press(editor, 'Backspace', { ctrlKey: true, shiftKey: true })
    expect(texts(editor)).toEqual(['two'])
    expect(editor.state.selection.$from.parent.textContent).toBe('two')
    editor.commands.undo()
    expect(texts(editor)).toEqual(['one', 'two'])
  })
  it('takes Ctrl+End and Ctrl+Home out of a table cell to the document\'s end and start, Shift extending (F5); elsewhere the browser\'s', () => {
    const cell = (text: string) => ({ type: 'tableCell', content: [paragraph(text)] })
    const editor = mount([paragraph('first'), { type: 'table', content: [{ type: 'tableRow', content: [cell('x'), cell('y')] }] }, paragraph('last')])
    caretIn(editor, 'x', 1)
    expect(press(editor, 'End', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect([editor.state.selection.$head.parent.textContent, editor.state.selection.head]).toEqual(['last', editor.state.doc.content.size - 1])
    caretIn(editor, 'y', 1)
    expect(press(editor, 'Home', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(editor.state.selection.head).toBe(1)
    caretIn(editor, 'y', 1)
    press(editor, 'End', { ctrlKey: true, shiftKey: true })
    expect(editor.state.doc.textBetween(editor.state.selection.from, editor.state.selection.to)).toBe('last')
    caretIn(editor, 'first', 1)
    expect(press(editor, 'End', { ctrlKey: true }).defaultPrevented).toBe(false)
    // A plain Ctrl+Home is the editor's everywhere: the browser's caret at the start right after focus was put back by ProseMirror.
    caretIn(editor, 'last', 2)
    expect(press(editor, 'Home', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(editor.state.selection.head).toBe(1)
  })
  it('duplicates by Ctrl+D as on the board, and deletes a selected block by Delete, a heading with its section (F9)', () => {
    const heading = (text: string): JSONContent => ({ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text }] })
    const editor = mount([heading('A'), paragraph('note'), heading('B'), paragraph('b')]); caretIn(editor, 'note', 1)
    expect(press(editor, 'd', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(texts(editor)).toEqual(['A', 'note', 'note', 'B', 'b'])
    caretIn(editor, 'A', 1); press(editor, 'd', { ctrlKey: true })
    expect(texts(editor)).toEqual(['A', 'note', 'note', 'A', 'note', 'note', 'B', 'b'])
    // Delete on the heading Esc selected takes its section; Delete in the text stays a character's.
    caretIn(editor, 'B'); press(editor, 'Escape')
    expect(press(editor, 'Delete').defaultPrevented).toBe(true)
    expect(texts(editor)).toEqual(['A', 'note', 'note', 'A', 'note', 'note'])
    caretIn(editor, 'A', 0)
    expect(press(editor, 'Delete').defaultPrevented).toBe(false)
  })
  it('duplicates and deletes a heading with its section, as the grip moves it (a requirement keeps its notes)', () => {
    const heading = (text: string, level = 2): JSONContent => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
    const editor = mount([heading('A'), paragraph('note'), heading('B')]); caretIn(editor, 'A', 1)
    press(editor, 'ArrowDown', { altKey: true, shiftKey: true })
    expect(texts(editor).slice(0, 5)).toEqual(['A', 'note', 'A', 'note', 'B'])
    press(editor, 'Backspace', { ctrlKey: true, shiftKey: true })
    expect(texts(editor).slice(0, 3)).toEqual(['A', 'note', 'B'])
  })
  it('moves, duplicates and deletes a selected component the same way (UX ED-2)', () => {
    const card = { type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jana' }] } } }
    const editor = mount([paragraph('a'), card, paragraph('b')])
    const select = () => { let at = -1; editor.state.doc.forEach((node, offset) => { if (at < 0 && node.type.name === 'contactCard') at = offset }); editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, at))) }
    // A trailing empty paragraph follows a component at the end; it is not part of the order.
    const order = () => editor.state.doc.content.content.map(node => node.type.name === 'contactCard' ? 'card' : node.textContent).filter(Boolean)
    select()
    expect(press(editor, 'ArrowUp', { ctrlKey: true, shiftKey: true }).defaultPrevented).toBe(true)
    expect(order()).toEqual(['card', 'a', 'b'])
    expect((editor.state.selection as NodeSelection).node?.type.name).toBe('contactCard')
    press(editor, 'ArrowDown', { ctrlKey: true, shiftKey: true })
    press(editor, 'ArrowDown', { ctrlKey: true, shiftKey: true })
    expect(order()).toEqual(['a', 'b', 'card'])
    press(editor, 'ArrowUp', { altKey: true, shiftKey: true })
    expect(order()).toEqual(['a', 'b', 'card', 'card'])
    expect(editor.state.selection.from).toBe(editor.state.doc.child(0).nodeSize + editor.state.doc.child(1).nodeSize)
    press(editor, 'Backspace', { ctrlKey: true, shiftKey: true })
    expect(order()).toEqual(['a', 'b', 'card'])
  })
  it('returns from a selected component to the text after it by Escape, so typing never replaces it (UX ED-10)', () => {
    const editor = mount([paragraph('a'), { type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jana' }] } } }, paragraph('b')])
    let card = -1; editor.state.doc.forEach((node, offset) => { if (node.type.name === 'contactCard') card = offset })
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, card)))
    expect(press(editor, 'Escape').defaultPrevented).toBe(true)
    expect(editor.state.selection).toBeInstanceOf(TextSelection)
    expect(editor.state.selection.$from.parent.textContent).toBe('b')
    expect(editor.state.selection.$from.parentOffset).toBe(0)
    // Escape counts as before: the next Tab leaves the editor.
    expect(press(editor, 'Tab').defaultPrevented).toBe(false)
  })
  it('Esc in the text selects the block around the caret, the second gives the caret back where it was (J4.1, J6.3)', () => {
    const callout = { type: 'callout', attrs: { variant: 'note' }, content: [paragraph('uvnitř')] }
    const editor = mount([paragraph('a'), callout, { type: 'codeBlock', content: [{ type: 'text', text: 'kód' }] }]); caretIn(editor, 'uvnitř', 3)
    const caret = editor.state.selection.from
    expect(press(editor, 'Escape').defaultPrevented).toBe(true)
    expect((editor.state.selection as NodeSelection).node?.type.name).toBe('callout')
    press(editor, 'Escape')
    expect(editor.state.selection).toBeInstanceOf(TextSelection)
    expect(editor.state.selection.from).toBe(caret)
    // Selected another way (its variant menu, the grip): the second Esc puts the caret into it, not into the code block after it.
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, editor.state.doc.child(0).nodeSize)))
    press(editor, 'Escape')
    expect(editor.state.selection.$from.parent.textContent).toBe('uvnitř')
    // A compact field keeps its caret.
    const field = mount([paragraph('Pole')], true); caretIn(field, 'Pole', 2)
    press(field, 'Escape')
    expect(field.state.selection).toBeInstanceOf(TextSelection)
  })
  it('on a block Esc selected, a typed character and Enter give the caret back into its text; Backspace removes a heading with its section (review 2026-11-06)', () => {
    const heading = (text: string): JSONContent => ({ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text }] })
    const editor = mount([paragraph('Text'), heading('H'), paragraph('pod')]); caretIn(editor, 'Text', 2)
    const caret = editor.state.selection.from
    press(editor, 'Escape')
    // ProseMirror's keypress would replace the whole paragraph with the character; the browser types it at the caret instead.
    editor.view.dom.dispatchEvent(new KeyboardEvent('keypress', { key: 'x', charCode: 120, bubbles: true, cancelable: true }))
    expect(editor.state.selection).toBeInstanceOf(TextSelection)
    expect(editor.state.selection.from).toBe(caret)
    expect(texts(editor)).toEqual(['Text', 'H', 'pod'])
    press(editor, 'Escape')
    expect(press(editor, 'Enter').defaultPrevented).toBe(true)
    expect(editor.state.selection.from).toBe(caret)
    expect(texts(editor)).toEqual(['Text', 'H', 'pod'])
    caretIn(editor, 'H', 1); press(editor, 'Escape')
    expect(press(editor, 'Backspace').defaultPrevented).toBe(true)
    expect(texts(editor)).toEqual(['Text'])
    editor.commands.undo()
    expect(texts(editor)).toEqual(['Text', 'H', 'pod'])
  })
  it('sets headings by Ctrl+Shift+1…6 and text by Ctrl+Shift+0, which a Czech keyboard reaches (Ctrl+Alt is its AltGr; UX ED-4)', () => {
    const editor = mount([paragraph('Nadpis')]); caretIn(editor, 'Nadpis', 2)
    // A Czech layout types the digit with Shift, so the key is the digit itself.
    expect(press(editor, '2', { ctrlKey: true, shiftKey: true, code: 'Digit2' }).defaultPrevented).toBe(true)
    expect(editor.state.doc.firstChild?.type.name).toBe('heading')
    expect(editor.state.doc.firstChild?.attrs.level).toBe(2)
    press(editor, '0', { ctrlKey: true, shiftKey: true, code: 'Digit0' })
    expect(editor.state.doc.firstChild?.type.name).toBe('paragraph')
    // An English layout reports the shifted character; the key code still names the digit.
    press(editor, '#', { ctrlKey: true, shiftKey: true, code: 'Digit3', keyCode: 51 } as KeyboardEventInit)
    expect(editor.state.doc.firstChild?.attrs.level).toBe(3)
  })
  it('asks the host for the shortcut overview by Ctrl+/ and lets the key through when nobody answers', () => {
    const editor = mount()
    expect(press(editor, '/', { ctrlKey: true }).defaultPrevented).toBe(false)
    const listener = vi.fn((event: Event) => event.preventDefault())
    editor.view.dom.addEventListener(MARKDOWN_SHORTCUT_HELP_EVENT, listener)
    expect(press(editor, '/', { ctrlKey: true }).defaultPrevented).toBe(true)
    expect(listener).toHaveBeenCalledOnce()
  })
  it('edits a selected component by Enter instead of adding a paragraph', async () => {
    const editor = mount([paragraph('a'), { type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jana' }] } } }, paragraph('b')])
    let card = -1; editor.state.doc.forEach((node, offset) => { if (node.type.name === 'contactCard') card = offset })
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, card)))
    await vi.waitFor(() => expect(document.querySelector('.md-context-toolbar:not([hidden]) button')).not.toBeNull())
    expect(press(editor, 'Enter').defaultPrevented).toBe(true)
    // The card has an edit of its own (block.ts), so Enter opens it; a component without one lends Enter to its toolbar.
    expect(document.activeElement?.closest('[role=dialog]')).not.toBeNull()
    expect(editor.state.doc.childCount).toBe(3)
  })
})

describe('the host around the text', () => {
  it('hands Up at the very start of the text to the host (the page title) and keeps it otherwise', () => {
    const editor = mount([paragraph('first'), paragraph('second')])
    caretIn(editor, 'first')
    expect(press(editor, 'ArrowUp').defaultPrevented).toBe(false)
    const listener = vi.fn((event: Event) => event.preventDefault())
    editor.view.dom.addEventListener(MARKDOWN_LEAVE_START_EVENT, listener)
    // jsdom has no layout: the first line is the whole first block there.
    vi.spyOn(editor.view, 'endOfTextblock').mockReturnValue(true)
    expect(press(editor, 'ArrowUp').defaultPrevented).toBe(true)
    caretIn(editor, 'second')
    press(editor, 'ArrowUp')
    expect(listener).toHaveBeenCalledOnce()
    // Shift+Tab anywhere in the first block goes back to the title too; further down it stays in the text.
    press(editor, 'Tab', { shiftKey: true })
    expect(listener).toHaveBeenCalledOnce()
    caretIn(editor, 'first', 3)
    expect(press(editor, 'Tab', { shiftKey: true }).defaultPrevented).toBe(true)
    expect(listener).toHaveBeenCalledTimes(2)
  })
  it('does not report focus before a collaborative editor can be written', () => {
    let publish: ((update: { hydrated?: boolean; writable?: boolean }) => void) | undefined
    const adapter = registerCollaborationAdapter(update => { publish = update; return { extensions: [], isRemoteTransaction: () => true, attachEditor() {}, detachEditor() {}, destroy() {} } })
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'collaborative', adapter } })
    controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Collaboration' })
    expect(controller.focus()).toBe(false)
    publish!({ hydrated: true, writable: true })
    expect(controller.focus()).toBe(true)
  })
})

describe('Markdown typed as Markdown', () => {
  it('turns a typed pipe row into a table on Enter, with the caret in its first body cell', () => {
    const editor = mount(); type(editor, '|Jméno|Telefon|')
    press(editor, 'Enter')
    const table = editor.getJSON().content?.[0] as JSONContent | undefined
    expect(table?.type).toBe('table')
    expect(table?.content?.[0]?.content?.map(cell => cell.content?.[0]?.content?.[0]?.text)).toEqual(['Jméno', 'Telefon'])
    expect(table?.content).toHaveLength(2)
    expect(editor.state.selection.$from.node(-1).type.name).toBe('tableCell')
  })
  it('leaves a single cell or ordinary text with pipes alone', () => {
    const editor = mount(); type(editor, 'a | b')
    press(editor, 'Enter')
    expect(editor.getJSON().content?.[0]?.type).toBe('paragraph')
  })
  it('turns a quote that starts with an alert marker into a callout of that kind', () => {
    const editor = mount(); type(editor, '> [!tip] ')
    expect(editor.getJSON().content?.[0]).toMatchObject({ type: 'callout', attrs: { variant: 'tip' } })
    expect(editor.state.selection.$from.parent.textContent).toBe('')
    const plain = mount(); type(plain, '[!warning] Pozor')
    expect(plain.getJSON().content?.[0]).toMatchObject({ type: 'callout', attrs: { variant: 'warning' } })
    expect(plain.state.doc.textContent).toBe('Pozor')
  })
})

describe('the shortcut list for the help overlay', () => {
  it('names every shortcut in Czech, in a group, each key combination once', () => {
    expect(markdownKeyboardShortcuts.length).toBeGreaterThan(15)
    for (const shortcut of markdownKeyboardShortcuts) {
      expect(shortcut.keys.length).toBeGreaterThan(0)
      expect(shortcut.label).toMatch(/\S/)
      expect(shortcut.group).toMatch(/\S/)
    }
    const keys = markdownKeyboardShortcuts.map(shortcut => [shortcut.keys, shortcut.then ?? []].map(step => step.join('+')).join(', '))
    expect(new Set(keys).size).toBe(keys.length)
    expect(markdownKeyboardShortcuts.find(shortcut => shortcut.keys.join('+') === 'Mod+Shift+ArrowUp')?.label).toBe('Posunout blok nahoru')
  })
})
