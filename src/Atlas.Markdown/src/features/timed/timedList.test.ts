// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, parseMarkdownContent, serializeMarkdownContent, type MarkdownEditorController, type MarkdownRichValue } from '../../core'
import { editorForController } from '../../core/internal'
import { repairLegacyDocument } from '../../engine/legacyDocument'
import { parseInputDocument, parseMarkdownImport } from '../../engine/markdownInput'
import { decorateStaticTimedLists, timedStamp } from './timedList'

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
// 16:19 UTC is 17:19 in Prague in November.
beforeEach(() => { vi.useFakeTimers({ toFake: ['Date'] }); vi.setSystemTime(new Date('2026-11-03T16:19:00Z')) })
afterEach(() => { vi.useRealTimers(); controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

function mount(content: unknown[], profile: 'rich' | 'markdown-safe' = 'rich') {
  const controller = profile === 'rich'
    ? createMarkdownEditorController({ profile, source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [], document: { type: 'doc', content } as MarkdownRichValue['document'] } } })
    : createMarkdownEditorController({ profile, source: { kind: 'local', value: '' } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!
  editor.commands.focus('end')
  return { controller, editor }
}
const type = (editor: Editor, text: string) => { for (const char of text) {
  const { from, to } = editor.state.selection
  if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
} }
const key = (editor: Editor, name: string) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }))
const item = (text?: string) => ({ type: 'timedItem', content: [{ type: 'paragraph', ...text ? { content: [{ type: 'text', text }] } : {} }] })
const timed = (...texts: (string | undefined)[]) => ({ type: 'timedList', content: texts.map(item) })
const lines = (editor: Editor) => { const found: string[] = []; editor.state.doc.descendants(node => { if (node.type.name === 'timedItem') found.push(node.firstChild!.textContent) }); return found }
const NOW = '3. 11. 2026 17:19 — '

describe('the stamp', () => {
  it('is the day, the time and a dash; an empty rest and a day without a time count too', () => {
    expect(timedStamp('3. 11. 2026 17:19 — text')).toEqual({ dateEnd: 11, timeEnd: 17, textStart: 20 })
    expect(timedStamp('3. 11. 2026 17:19 —')).toEqual({ dateEnd: 11, timeEnd: 17, textStart: 19 })
    expect(timedStamp('15. 10. 2026 — text')).toEqual({ dateEnd: 12, timeEnd: undefined, textStart: 15 })
    expect(timedStamp('2026 — text')).toBeNull()
    expect(timedStamp('3. 11. 2026text')).toBeNull()
  })
})

describe('a timed list', () => {
  it('Enter at the end of an item starts the next one, stamped, the caret after the stamp', () => {
    const { editor } = mount([timed(`${NOW}first`)])
    key(editor, 'Enter')
    expect(lines(editor)).toEqual([`${NOW}first`, NOW])
    expect(editor.state.selection.$from.parentOffset).toBe(NOW.length)
  })

  it('stamps an empty item on its first character, one undo step', () => {
    const { editor } = mount([timed(undefined)])
    type(editor, 'a')
    expect(lines(editor)).toEqual([`${NOW}a`])
    editor.commands.undo()
    expect(lines(editor)).toEqual([''])
  })

  it('leaves a digit typed first as it is: the user writes the date', () => {
    const { editor } = mount([timed(undefined)])
    type(editor, '2. 11. 2026 — b')
    expect(lines(editor)).toEqual(['2. 11. 2026 — b'])
    expect(editor.view.dom.querySelector('.tt-timed-date')?.textContent).toBe('2. 11. 2026')
  })

  it('Backspace after a fresh stamp removes the stamp, the next one the item', () => {
    const { editor } = mount([{ type: 'paragraph', content: [{ type: 'text', text: 'before' }] }, timed(NOW)])
    key(editor, 'Backspace')
    expect(lines(editor)).toEqual([''])
    key(editor, 'Backspace')
    expect(editor.state.doc.child(1).type.name).toBe('paragraph')
    expect(lines(editor)).toEqual([])
  })

  it('Backspace twice in an item after Enter removes the stamp, then the item, the caret back at the end of the one before: no blank row', () => {
    const { editor } = mount([timed(`${NOW}first`)])
    key(editor, 'Enter')
    key(editor, 'Backspace')
    expect(lines(editor)).toEqual([`${NOW}first`, ''])
    key(editor, 'Backspace')
    expect(lines(editor)).toEqual([`${NOW}first`])
    expect(editor.state.doc.childCount).toBe(2)
    expect(editor.state.selection.$from.parent.textContent).toBe(`${NOW}first`)
    expect(editor.state.selection.$from.parentOffset).toBe(`${NOW}first`.length)
  })

  it('Backspace in an item emptied by hand in the middle removes it and keeps the list one list', () => {
    const { editor } = mount([timed(`${NOW}a`, undefined, `${NOW}b`)])
    select(editor, 3 + NOW.length + 1 + 4)
    expect(editor.state.selection.$from.parent.content.size).toBe(0)
    key(editor, 'Backspace')
    expect(editor.state.doc.firstChild!.type.name).toBe('timedList')
    expect(lines(editor)).toEqual([`${NOW}a`, `${NOW}b`])
    expect(editor.state.selection.$from.parent.textContent).toBe(`${NOW}a`)
  })

  it('takes a date written over the stamp, and draws it as the chip', () => {
    const { editor } = mount([timed(`${NOW}text`)])
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 3, 3 + 11)))
    type(editor, '1. 1. 2026')
    expect(lines(editor)).toEqual(['1. 1. 2026 17:19 — text'])
    expect([...editor.view.dom.querySelectorAll('.tt-timed-date, .tt-timed-time')].map(chip => chip.textContent)).toEqual(['1. 1. 2026', ' 17:19'])
  })

  it('toggles from a bullet list, stamping the items, and back keeping the stamps as text', () => {
    const bullets = { type: 'bulletList', content: ['a', 'b'].map(text => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) }
    const { controller, editor } = mount([bullets])
    // From the first item's text to the last one's.
    const both = () => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 3, editor.state.doc.firstChild!.nodeSize - 3)))
    both()
    expect(controller.dispatch({ type: 'toggle-list', list: 'timed' })).toBe(true)
    expect(editor.state.doc.firstChild!.type.name).toBe('timedList')
    expect(lines(editor)).toEqual([`${NOW}a`, `${NOW}b`])
    both()
    expect(controller.dispatch({ type: 'toggle-list', list: 'bullet' })).toBe(true)
    expect(editor.state.doc.firstChild!.type.name).toBe('bulletList')
    expect(editor.state.doc.firstChild!.textContent).toBe(`${NOW}a${NOW}b`)
    // And back: the stamps it kept are recognised, not stamped twice.
    both()
    expect(controller.dispatch({ type: 'toggle-list', list: 'timed' })).toBe(true)
    expect(lines(editor)).toEqual([`${NOW}a`, `${NOW}b`])
  })

  const list =(type: string, item: string, texts: string[]) => ({ type, content: texts.map(text => ({ type: item, content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) })
  const markdown = (editor: Editor) => serializeMarkdownContent(editor, editor.state.doc)
  const select = (editor: Editor, from: number, to = from) => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, from, to)))
  const timedQuery = (controller: MarkdownEditorController) => controller.query({ type: 'toggle-list', list: 'timed' }).active

  it('converts selected bullets in one undo step, every item stamped, one starting with a date keeping it; back as bullets keeping the stamps as text', () => {
    const { controller, editor } = mount([list('bulletList', 'listItem', ['2. 11. 2026 server', '1. 11. 2026 08:00 — dns', 'cert'])])
    expect(timedQuery(controller)).toBe(false)
    select(editor, 3, editor.state.doc.firstChild!.nodeSize - 3)
    expect(controller.dispatch({ type: 'toggle-list', list: 'timed' })).toBe(true)
    expect(timedQuery(controller)).toBe(true)
    expect(controller.query({ type: 'toggle-list', list: 'bullet' }).active).toBe(false)
    expect(markdown(editor)).toBe('- 2\\. 11. 2026 — server\n- 1\\. 11. 2026 08:00 — dns\n- 3\\. 11. 2026 17:19 — cert')
    expect(parseMarkdownContent(editor, markdown(editor)).firstChild!.type.name).toBe('timedList')
    editor.commands.undo()
    expect(editor.state.doc.firstChild!.type.name).toBe('bulletList')
    expect(editor.state.doc.firstChild!.textContent).toBe('2. 11. 2026 server1. 11. 2026 08:00 — dnscert')
    editor.commands.redo()
    select(editor, 3)
    expect(controller.dispatch({ type: 'toggle-list', list: 'bullet' })).toBe(true)
    expect(timedQuery(controller)).toBe(false)
    expect(markdown(editor)).toBe('- 2\\. 11. 2026 — server\n- 1\\. 11. 2026 08:00 — dns\n- 3\\. 11. 2026 17:19 — cert')
    expect(editor.state.doc.firstChild!.type.name).toBe('bulletList')
  })

  it('with only the caret converts the whole list and stamps every item; numbered and task lists too, and back', () => {
    for (const [kind, type, item] of [['ordered', 'orderedList', 'listItem'], ['task', 'taskList', 'taskItem']] as const) {
      const { controller, editor } = mount([list(type, item, ['a', 'b', 'c'])])
      select(editor, 3)
      expect(controller.dispatch({ type: 'toggle-list', list: 'timed' })).toBe(true)
      expect(editor.state.doc.firstChild!.type.name).toBe('timedList')
      expect(lines(editor)).toEqual([`${NOW}a`, `${NOW}b`, `${NOW}c`])
      expect(controller.dispatch({ type: 'toggle-list', list: kind })).toBe(true)
      expect(editor.state.doc.firstChild!.type.name).toBe(type)
      expect(editor.state.doc.firstChild!.textContent).toBe(`${NOW}a${NOW}b${NOW}c`)
    }
  })

  const chord = (editor: Editor, key: string) => {
    const event = new KeyboardEvent('keydown', { key, ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true })
    editor.view.dom.dispatchEvent(event)
    return event
  }

  for (const [digit, type] of [['7', 'orderedList'], ['8', 'bulletList'], ['9', 'taskList']] as const) {
    it(`Ctrl+Shift+${digit} in a timed list retypes it as ${type}, the dates kept as leading text, one undo step`, () => {
      const { editor } = mount([timed(`${NOW}a`, `${NOW}b`)])
      select(editor, 3)
      expect(chord(editor, digit).defaultPrevented).toBe(true)
      expect(editor.state.doc.firstChild!.type.name).toBe(type)
      expect(editor.state.doc.firstChild!.childCount).toBe(2)
      expect(editor.state.doc.firstChild!.textContent).toBe(`${NOW}a${NOW}b`)
      editor.commands.undo()
      expect(editor.state.doc.firstChild!.type.name).toBe('timedList')
      expect(lines(editor)).toEqual([`${NOW}a`, `${NOW}b`])
    })
  }

  it('toggles on Ctrl+Shift+D, the key taken from the browser (Chromium: bookmark all tabs), and off again', () => {
    const { controller, editor } = mount([list('bulletList', 'listItem', ['a'])])
    const press = () => chord(editor, 'd')
    expect(press().defaultPrevented).toBe(true)
    expect(lines(editor)).toEqual([`${NOW}a`])
    press()
    expect(timedQuery(controller)).toBe(false)
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
  })

  it('reads and writes a bullet list\'s Markdown; markdown-safe reads it as a bullet list', () => {
    const { editor } = mount([{ type: 'paragraph' }])
    const markdown = '- 3\\. 11. 2026 17:19 — first\n- 2\\. 11. 2026 — second'
    const doc = parseMarkdownContent(editor, markdown)
    expect(doc.firstChild!.type.name).toBe('timedList')
    expect(serializeMarkdownContent(editor, doc)).toBe(markdown)
    // Not a timed list: an ordered list, a bullet list whose first item is not stamped.
    expect(parseMarkdownContent(editor, '1. 2026\n2. b').firstChild!.type.name).toBe('orderedList')
    expect(parseMarkdownContent(editor, '- a\n- 3\\. 11. 2026 — b').firstChild!.type.name).toBe('bulletList')
    const safe = mount([], 'markdown-safe').editor
    expect(parseMarkdownContent(safe, markdown).firstChild!.type.name).toBe('bulletList')
  })

  it('pastes the unescaped Markdown an AI writes as a timed list, not as nested ordered lists', () => {
    const { editor } = mount([{ type: 'paragraph' }])
    const doc = parseInputDocument(parseMarkdownImport(editor, '- 3. 11. 2026 17:19 — first\n- 30. 10. 2026 — second', { source: 'paste' }), editor.schema)
    expect(doc.firstChild!.type.name).toBe('timedList')
    expect(doc.firstChild!.children.map(entry => entry.textContent)).toEqual(['3. 11. 2026 17:19 — first', '30. 10. 2026 — second'])
  })

  it('Ctrl+Shift+D twice on a paragraph gives the paragraph back without the stamp', () => {
    const { editor } = mount([{ type: 'paragraph', content: [{ type: 'text', text: 'note' }] }])
    chord(editor, 'd'); expect(lines(editor)).toEqual([`${NOW}note`])
    chord(editor, 'd')
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
    expect(editor.state.doc.textContent).toBe('note')
  })

  it('Backspace at the start of an item under a task list makes it a paragraph without the stamp, never a task item', () => {
    const { editor } = mount([list('taskList', 'taskItem', ['task']), timed(`${NOW}a`, `${NOW}b`)])
    const start = editor.state.doc.firstChild!.nodeSize + 3
    select(editor, start); key(editor, 'Backspace')
    expect(editor.state.doc.children.slice(0, 3).map(node => node.type.name)).toEqual(['taskList', 'paragraph', 'timedList'])
    expect(editor.state.doc.child(0).textContent).toBe('task')
    expect(editor.state.doc.child(1).textContent).toBe('a')
    expect(lines(editor)).toEqual([`${NOW}b`])
    expect(editor.state.selection.$from.parent.textContent).toBe('a')
  })

  it('the stamp\'s field stays open after the editor redraws, the keyboard in it, until Esc', async () => {
    const { editor } = mount([timed(`${NOW}first`)])
    const later = () => new Promise(resolve => setTimeout(resolve, 20))
    // mount's focus('end') lands in a frame.
    await later()
    select(editor, 5); key(editor, 'F2')
    expect(document.querySelector('.md-context-popover input[name="stamp"]')).not.toBeNull()
    // ProseMirror's DOM observer and the popover's checks run after the opening: an attribute on a redrawn chip used to close it.
    await later()
    const input = document.querySelector<HTMLInputElement>('.md-context-popover input[name="stamp"]')
    expect(input?.isConnected).toBe(true)
    expect(document.activeElement).toBe(input)
    input!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(document.querySelector('.md-context-popover input')).toBeNull()
    expect(lines(editor)).toEqual([`${NOW}first`])
  })

  it('marks a static rendering\'s stamps as the editor does', () => {
    const root = document.createElement('div')
    root.innerHTML = '<ul data-type="timedList" class="tt-timed-list"><li class="tt-timed-item"><p>3. 11. 2026 17:19 — a</p></li><li class="tt-timed-item"><p>b</p></li></ul>'
    decorateStaticTimedLists(root)
    expect([...root.querySelectorAll('.tt-timed-date, .tt-timed-time')].map(chip => chip.textContent)).toEqual(['3. 11. 2026', ' 17:19'])
    expect(root.textContent).toBe('3. 11. 2026 17:19 — ab')
  })
})

describe('an existing bullet list of dated notes', () => {
  it('becomes a timed list on open, on any page, its attributes kept; other lists stay', () => {
    const list = (first: string, tight = true) => ({ type: 'bulletList', attrs: { tight }, content: [first, 'later'].map(text => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })) })
    const document = { type: 'doc', content: [list('3. 11. 2026 17:19 — a', false), list('plain')] }
    const repaired = repairLegacyDocument(document)
    expect(repaired.content![0]).toMatchObject({ type: 'timedList', attrs: { tight: false }, content: [{ type: 'timedItem' }, { type: 'timedItem' }] })
    expect(repaired.content![1]).toBe(document.content[1])
    expect(document.content[0]!.type).toBe('bulletList')
  })
})
