// @vitest-environment jsdom
import type { Editor } from '@tiptap/core'
import { TextSelection } from '@tiptap/pm/state'
import { afterEach, describe, expect, it } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { createDiagramsPlugin } from '../../plugins/diagrams'

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

function mount() {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const diagram = { type: 'diagram', attrs: { data: { version: 1, source: 'flowchart LR\n  A --> B', caption: '' } } }
  const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
  const value = { format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: { type: 'doc', content: [paragraph('před'), diagram, paragraph('po')] } } as MarkdownRichValue
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createDiagramsPlugin(), capture], source: { kind: 'local', value } })
  controllers.push(controller)
  controller.attachView(document.body.appendChild(document.createElement('div')), { ariaLabel: 'Bloky' })
  const press = (key: string) => editor.view.someProp('handleKeyDown', handle => handle(editor.view, new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })))
  const caretAt = (text: string, offset: number) => {
    let found = -1; editor.state.doc.descendants((node, pos) => { if (node.isText && node.text === text) found = pos })
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, found + offset)))
  }
  return { editor, press, caretAt }
}

/** J4.3: the first Backspace / Delete next to a block selects it, the second deletes it (ProseMirror's join would delete it at once). */
describe('a block next to text', () => {
  it('is selected by Backspace at the start of the text after it, and deleted by the second press', () => {
    const { editor, press, caretAt } = mount()
    caretAt('po', 0)
    expect(press('Backspace')).toBe(true)
    expect(editor.state.selection.constructor.name).toBe('NodeSelection')
    expect((editor.state.selection as unknown as { node: { type: { name: string } } }).node.type.name).toBe('diagram')
    press('Backspace')
    expect(JSON.stringify(editor.getJSON())).not.toContain('"diagram"')
  })

  it('is selected by Delete at the end of the text before it', () => {
    const { editor, press, caretAt } = mount()
    caretAt('před', 4)
    expect(press('Delete')).toBe(true)
    expect((editor.state.selection as unknown as { node?: { type: { name: string } } }).node?.type.name).toBe('diagram')
  })

  it('leaves Backspace inside the text alone', () => {
    const { editor, press, caretAt } = mount()
    caretAt('po', 1)
    press('Backspace')
    expect(editor.state.selection.constructor.name).toBe('TextSelection')
  })
})
