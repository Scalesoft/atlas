// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { Editor, Node } from '@tiptap/core'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'
import { createMarkdownKit } from '../createMarkdownKit'
import { KeyboardFirst } from './KeyboardFirst'

// J6.3: Esc from a control inside a block leaves the block selected, the way Esc from the block's toolbar does.
const Card = Node.create({
  name: 'testCard', group: 'block', atom: true, selectable: true,
  parseHTML: () => [{ tag: 'div[data-test-card]' }], renderHTML: () => ['div', { 'data-test-card': '' }],
  addNodeView: () => () => {
    const dom = document.createElement('div'), button = document.createElement('button')
    dom.dataset.testCard = ''; button.textContent = 'Akce'; dom.append(button)
    return { dom, stopEvent: () => true }
  },
})
const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function mount() {
  const element = document.createElement('div'); document.body.appendChild(element)
  const editor = new Editor({ element, content: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Před' }] }, { type: 'testCard' }, { type: 'paragraph' }] },
    extensions: [...createMarkdownKit({ profile: 'rich' }).extensions, KeyboardFirst, Card] })
  editors.push(editor)
  return editor
}
const press = (target: Element, key = 'Escape') => { const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }); target.dispatchEvent(event); return event }

describe('Esc from a block control', () => {
  it('selects the block', () => {
    const editor = mount()
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 2)))
    const button = editor.view.dom.querySelector('button')!
    button.focus()
    const event = press(button)
    expect(event.defaultPrevented).toBe(true)
    const selection = editor.state.selection
    expect(selection instanceof NodeSelection && selection.node.type.name).toBe('testCard')
  })
  it('leaves a read-only editor and an owned Escape alone', () => {
    const editor = mount()
    const button = editor.view.dom.querySelector('button')!
    button.dataset.mdOwnEscape = ''
    expect(press(button).defaultPrevented).toBe(false)
    delete button.dataset.mdOwnEscape
    editor.setEditable(false)
    expect(press(button).defaultPrevented).toBe(false)
  })
})
