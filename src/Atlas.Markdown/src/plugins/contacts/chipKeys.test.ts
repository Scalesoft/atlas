// @vitest-environment jsdom
// J3.2: Backspace after a chip / Delete before it select it first, the second press deletes it (one undo step).
import { afterEach, describe, expect, it } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { Node } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { markdownChipDeleteKeys } from '../../core'

const Chip = Node.create({ name: 'contactInline', group: 'inline', inline: true, atom: true, renderHTML: () => ['span', { 'data-chip': '' }] })
const press = (editor: Editor, key: string) => {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })
  return editor.view.someProp('handleKeyDown', handler => handler(editor.view, event) || undefined) ?? false
}
// Destroyed after each test: a live view's DOM observer timer otherwise fires after jsdom is gone (CI run 152).
const editors: Editor[] = []
afterEach(() => { for (const editor of editors.splice(0)) editor.destroy() })
const make = () => track(new Editor({ extensions: [StarterKit, Chip, markdownChipDeleteKeys('contacts', ['contactInline'])],
  content: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'a' }, { type: 'contactInline' }, { type: 'text', text: 'b' }] }] } }))
const track = (editor: Editor) => { editors.push(editor); return editor }

describe('chip delete keys', () => {
  it('Backspace after a chip selects it, the next press deletes it', () => {
    const editor = make(); editor.commands.setTextSelection(3)
    expect(press(editor, 'Backspace')).toBe(true)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(editor.getJSON().content![0]!.content!.some(node => node.type === 'contactInline')).toBe(true)
  })
  it('Delete before a chip selects it', () => {
    const editor = make(); editor.commands.setTextSelection(2)
    expect(press(editor, 'Delete')).toBe(true)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
  })
  it('does nothing away from a chip', () => {
    const editor = make(); editor.commands.setTextSelection(1)
    expect(press(editor, 'Backspace')).toBe(false)
  })
})
