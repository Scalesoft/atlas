// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import { TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, type MarkdownEditorController } from '../core'
import { editorForController } from '../core/internal'
import { configureMarkdownEditorView } from '../vue/controllerView'

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
Element.prototype.scrollIntoView ??= function () {}

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

it('marks the caret\'s top-level block, a new one after Enter in the same update, so content-visibility never skips it', () => {
  const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
    paragraph('Blok A'), { type: 'bulletList', content: [{ type: 'listItem', content: [paragraph('Položka')] }] },
  ] } } } })
  controllers.push(controller)
  configureMarkdownEditorView(controller, { compact: false })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!
  const marked = () => [...editor.view.dom.children].filter(block => block.hasAttribute('data-md-caret')).map(block => `${block.tagName}:${block.textContent}`)

  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 7)))
  expect(marked()).toEqual(['P:Blok A'])
  editor.commands.splitBlock()
  expect(marked()).toEqual(['P:'])
  let item = 0
  editor.state.doc.descendants((node, pos) => { if (node.isText && node.text === 'Položka') item = pos })
  editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, item + 2)))
  expect(marked()).toEqual(['UL:Položka'])
})
