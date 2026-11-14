// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController } from '../core'
import { editorForController } from '../core/internal'
import { configureMarkdownEditorView } from '../vue/controllerView'

/*
 * Headings of the one-editor page model (contract J4.3). The fold, the outline and the removal of the block being typed in are tested next to their code:
 * Atlas.Web/src/editor/headingFold.test.ts, pageOutline.test.ts, ../ui/outline.test.ts, ../collab/remoteRemoval.test.ts.
 */
describe('one editor: headings (J4.3)', () => {
  let controller: MarkdownEditorController | undefined
  afterEach(() => { controller?.destroy(); controller = undefined; document.body.replaceChildren() })
  function mount(content: JSONContent[]) {
    controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content } } } })
    configureMarkdownEditorView(controller, { compact: false })
    const host = document.body.appendChild(document.createElement('div'))
    controller.attachView(host, { ariaLabel: 'Editor' })
    return editorForController(controller)!
  }
  const press = (editor: Editor, key: string, init: KeyboardEventInit = {}) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }))
  const caret = (editor: Editor, pos: number) => editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, pos)))
  const blocks = (editor: Editor) => editor.state.doc.content.content.map(node => `${node.type.name}${node.attrs.level ?? ''}:${node.textContent}`)
  const heading = (text: string): JSONContent => ({ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text }] })
  const paragraph = (text: string): JSONContent => ({ type: 'paragraph', content: [{ type: 'text', text }] })

  it('Enter at the end of a heading starts a paragraph; in the middle it splits the heading', () => {
    const editor = mount([heading('Nadpis')])
    caret(editor, 7)
    press(editor, 'Enter')
    expect(blocks(editor)).toEqual(['heading2:Nadpis', 'paragraph:', 'paragraph:'])
    caret(editor, 4)
    press(editor, 'Enter')
    expect(blocks(editor)).toEqual(['heading2:Nad', 'heading2:pis', 'paragraph:', 'paragraph:'])
  })

  it('Backspace at the start of a heading makes it a paragraph first; the next one joins', () => {
    const editor = mount([paragraph('Před'), heading('Nadpis')])
    caret(editor, 7)
    press(editor, 'Backspace')
    expect(blocks(editor)).toEqual(['paragraph:Před', 'paragraph:Nadpis', 'paragraph:'])
    press(editor, 'Backspace')
    expect(blocks(editor)).toEqual(['paragraph:PředNadpis', 'paragraph:'])
  })

  it('sets the level with Ctrl+Shift+1–6 and text with Ctrl+Shift+0 or Ctrl+Alt+0 (a Czech keyboard has no Ctrl+Alt digits)', () => {
    const editor = mount([paragraph('Text')])
    caret(editor, 2)
    press(editor, '2', { ctrlKey: true, shiftKey: true, code: 'Digit2', keyCode: 50 })
    expect(blocks(editor)[0]).toBe('heading2:Text')
    press(editor, '0', { ctrlKey: true, shiftKey: true, code: 'Digit0', keyCode: 48 })
    expect(blocks(editor)[0]).toBe('paragraph:Text')
    press(editor, '4', { ctrlKey: true, shiftKey: true, code: 'Digit4', keyCode: 52 })
    expect(blocks(editor)[0]).toBe('heading4:Text')
    press(editor, '0', { ctrlKey: true, altKey: true, code: 'Digit0', keyCode: 48 })
    expect(blocks(editor)[0]).toBe('paragraph:Text')
  })
})
