// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, markdownKeyboardShortcuts, markMarkdownAutoConversion, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { editorForController } from '../../core/internal'
import { createEmojiPlugin } from '../../plugins/emoji'
import { createMathPlugin } from '../../plugins/math'
import { createPagesPlugin } from '../../plugins/pages'
import { createYouTrackPlugin } from '../../plugins/youtrack'

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const issue = { type: 'youtrackIssue', attrs: { id: '2-1', key: 'PX2-1', title: 'Přístup VPN', url: 'https://youtrack.example/issue/PX2-1', state: null } }
const page = { type: 'pageReference', attrs: { id: '11111111-1111-4111-8111-111111111111', title: 'Provozní příručka', url: 'https://atlas.example/#/pages/1' } }
function mount(content: unknown[], plugins: MarkdownPlugin[] = [createYouTrackPlugin({ client: { search: async () => [] } }), createEmojiPlugin({ enableEmoticons: true }), createPagesPlugin(), createMathPlugin()]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins,
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: plugins.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })),
      document: { type: 'doc', content } as MarkdownRichValue['document'] } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!
  editor.commands.focus('end')
  return { controller, editor }
}
/** As the keyboard types: through the input rules, one character at a time. */
const type = (editor: Editor, text: string) => { for (const char of text) {
  const { from, to } = editor.state.selection
  if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
} }
const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: name, code: name === ' ' ? 'Space' : undefined, bubbles: true, cancelable: true, ...init }))
const toText = (editor: Editor) => key(editor, ' ', { ctrlKey: true, shiftKey: true })
const types = (editor: Editor) => { const found: string[] = []; editor.state.doc.descendants(node => { if (!node.isText && node.isInline) found.push(node.type.name) }); return found }
const paragraph = (...content: unknown[]) => ({ type: 'paragraph', content })

describe('an automatic chip', () => {
  /** „Viz PX2-1 “ typed, then the key becomes the issue (as the requirement title does once YouTrack answers). */
  const converted = () => {
    const mounted = mount([{ type: 'paragraph' }])
    type(mounted.editor, 'Viz PX2-1 ')
    const { editor } = mounted, from = 5, node = editor.schema.nodeFromJSON(issue)
    // The chip and the space that made it, which stays after it.
    editor.view.dispatch(markMarkdownAutoConversion(editor.state.tr.replaceWith(from, from + 5, node), { from, to: from + 2, text: 'PX2-1' }))
    return mounted
  }
  it('Backspace right after it deletes the trigger: the typed text without it, the caret at its end', () => {
    const { editor } = converted()
    expect(types(editor)).toEqual(['youtrackIssue'])
    key(editor, 'Backspace')
    expect(editor.getText()).toBe('Viz PX2-1')
    expect(editor.state.selection.from).toBe(10)
    expect(types(editor)).toEqual([])
  })
  it('only right after it: a character typed after it or a caret move forgets it', () => {
    const { editor } = converted()
    type(editor, ',')
    key(editor, 'Backspace')
    expect(types(editor)).toEqual(['youtrackIssue'])
    const again = converted().editor
    again.commands.setTextSelection(1); again.commands.setTextSelection(7)
    key(again, 'Backspace')
    expect(types(again)).toEqual(['youtrackIssue'])
  })
  it('is an undo step of its own: Ctrl+Z undoes the conversion, not the typing', () => {
    const { controller, editor } = converted()
    controller.dispatch({ type: 'undo' })
    expect(editor.getText()).toBe('Viz PX2-1 ')
    expect(types(editor)).toEqual([])
  })
  it('for typed $x$ and :smile:: Backspace gives the text without the trigger, the trigger again re-makes it, Ctrl+Z only the chip', () => {
    const { controller, editor } = mount([{ type: 'paragraph' }])
    type(editor, 'Plocha $x^2$ ')
    expect(types(editor)).toEqual(['mathInline'])
    key(editor, 'Backspace')
    expect(editor.getText()).toBe('Plocha $x^2$')
    expect(editor.state.selection.from).toBe(13)
    type(editor, ' ')
    expect(types(editor)).toEqual(['mathInline'])
    type(editor, 'a :smile:')
    expect(types(editor)).toEqual(['mathInline', 'emoji'])
    key(editor, 'Backspace')
    expect(editor.getText()).toMatch(/ a :smile$/u)
    expect(types(editor)).toEqual(['mathInline'])
    type(editor, ':')
    expect(types(editor)).toEqual(['mathInline', 'emoji'])
    controller.dispatch({ type: 'undo' })
    expect(types(editor)).toEqual(['mathInline'])
    expect(editor.getText()).toMatch(/ a :smile$/u)
  })
  it('the chip that a reverted text was is not made again by anything but the trigger', () => {
    const { editor } = mount([{ type: 'paragraph' }])
    type(editor, 'Viz $x$ ')
    key(editor, 'Backspace')
    // Typing elsewhere, moving the caret and a re-render leave the text as text.
    editor.commands.setTextSelection(1); type(editor, 'A')
    editor.commands.setContent(editor.getJSON())
    expect(types(editor)).toEqual([])
    expect(editor.getText()).toBe('AViz $x$')
  })
})

describe('a chip back to text later', () => {
  it('lists the shortcut in the keyboard overview', () => {
    expect(markdownKeyboardShortcuts.some(entry => entry.keys.join(' ') === 'Mod Shift Space' && entry.label.startsWith('Převést čip na text'))).toBe(true)
  })
  it('Ctrl+Shift+Space on a selected issue or page, or right after an emoji, keeps the visible text and no hidden data', () => {
    const { controller, editor } = mount([paragraph({ type: 'text', text: 'Úkol ' }, issue, { type: 'text', text: ', stránka ' }, page, { type: 'text', text: ' a ' }, { type: 'emoji', attrs: { name: 'smile' } })])
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, 6)))
    toText(editor)
    const pageAt = editor.state.doc.textContent.indexOf('stránka ') + 'stránka '.length + 1
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, pageAt)))
    toText(editor)
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, editor.state.doc.content.size - 1)))
    toText(editor)
    expect(types(editor)).toEqual([])
    expect(editor.getText()).toBe('Úkol PX2-1, stránka Provozní příručka a 😄')
    expect(JSON.stringify(controller.getProjection().rich.document)).not.toMatch(/youtrack\.example|atlas\.example|"name":"smile"/u)
    controller.dispatch({ type: 'undo' })
    expect(types(editor)).toEqual(['emoji'])
  })
  it('„Převést na text“ in the toolbar of a selected issue and of an inline formula', async () => {
    const { editor } = mount([paragraph({ type: 'text', text: 'Úkol ' }, issue, { type: 'text', text: ' a ' }, { type: 'mathInline', attrs: { data: { version: 1, expression: 'x^2' } } })])
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, 6)))
    const shown = () => vi.waitFor(() => { const found = document.querySelector<HTMLButtonElement>('.md-context-toolbar:not([hidden]) button[aria-label^="Převést na text"]'); expect(found).not.toBeNull(); return found! })
    const button = await shown()
    expect(button.getAttribute('aria-label')).toBe('Převést na text (Ctrl+Shift+Mezerník)')
    button.click()
    expect(editor.getText()).toContain('Úkol PX2-1 a')
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, editor.state.doc.content.size - 2)))
    ;(await shown()).click()
    expect(types(editor)).toEqual([])
    expect(editor.getText()).toBe('Úkol PX2-1 a $x^2$')
  })
})
