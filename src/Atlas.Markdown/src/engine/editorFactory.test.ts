// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import { GapCursor } from '@tiptap/pm/gapcursor'
import { NodeSelection } from '@tiptap/pm/state'
import { createMarkdownKit } from '../createMarkdownKit'
import { createTiptapEditor } from './editorFactory'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function mount() {
  const element = document.createElement('div'); document.body.appendChild(element)
  const editor = createTiptapEditor({ element, extensions: createMarkdownKit({ profile: 'rich' }).extensions,
    content: { type: 'doc', content: [{ type: 'horizontalRule' }, { type: 'horizontalRule' }, { type: 'paragraph' }] } })
  editors.push(editor)
  return editor
}

// 3.6 perf F5: the hidden selection restyled the whole page at each block selection (Tiptap's `.ProseMirror-hideselection *` rules).
describe('the hidden native selection', () => {
  it('is scoped to the selected blocks, never a rule over every element under the root', () => {
    mount()
    const css = document.querySelector('style[data-tiptap-style]')!.textContent!
    expect(css).not.toMatch(/\.ProseMirror-hideselection\s+\*/)
    expect(css).not.toMatch(/\.ProseMirror-hideselection\s*\{/)
    expect(css).toContain('.ProseMirror-selectednode, .ProseMirror-selectednoderange, .tableWrapper:has(.selectedCell)) *::selection')
    expect(css).toContain('.ProseMirror.md-gap-cursor { caret-color: transparent; }')
    // A second editor reuses the one style tag as it is.
    mount()
    expect(document.querySelectorAll('style[data-tiptap-style]')).toHaveLength(1)
  })

  it('hides the caret only at a gap cursor, not at a block selection', () => {
    const editor = mount()
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, 0)))
    expect(editor.view.dom.classList.contains('md-gap-cursor')).toBe(false)
    editor.view.dispatch(editor.state.tr.setSelection(new GapCursor(editor.state.doc.resolve(1))))
    expect(editor.view.dom.classList.contains('md-gap-cursor')).toBe(true)
  })
})
