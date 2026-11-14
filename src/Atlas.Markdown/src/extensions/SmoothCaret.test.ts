// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import { GapCursor } from '@tiptap/pm/gapcursor'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'
import { createMarkdownKit } from '../createMarkdownKit'
import { createTiptapEditor } from '../engine/editorFactory'
import { smoothCaretAtWrap, smoothCaretShown, smoothCaretSide } from './SmoothCaret'

const editors: Editor[] = []
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
function mount() {
  const element = document.createElement('div'); document.body.appendChild(element)
  const editor = createTiptapEditor({ element, extensions: createMarkdownKit({ profile: 'rich' }).extensions, content: { type: 'doc', content: [
    { type: 'paragraph', content: [{ type: 'text', text: 'ab cd' }, { type: 'hardBreak' }, { type: 'text', text: 'ef' }] },
    { type: 'horizontalRule' },
    { type: 'paragraph' },
  ] } })
  editors.push(editor)
  return editor
}
const select = (editor: Editor, selection: (doc: Editor['state']['doc']) => import('@tiptap/pm/state').Selection) =>
  editor.view.dispatch(editor.state.tr.setSelection(selection(editor.state.doc)))

describe('the smooth caret stands', () => {
  const on = { editable: true, focused: true }

  it('only for a collapsed text caret of an editable, focused editor', () => {
    const editor = mount()
    select(editor, doc => TextSelection.create(doc, 2))
    expect(smoothCaretShown(editor.state, on)).toBe(true)
    expect(smoothCaretShown(editor.state, { editable: false, focused: true })).toBe(false)
    expect(smoothCaretShown(editor.state, { editable: true, focused: false })).toBe(false)
  })

  it('never for a range, a selected node or the gap cursor', () => {
    const editor = mount()
    select(editor, doc => TextSelection.create(doc, 1, 3))
    expect(smoothCaretShown(editor.state, on)).toBe(false)
    const rule = editor.state.doc.child(0).nodeSize
    select(editor, doc => NodeSelection.create(doc, rule))
    expect(smoothCaretShown(editor.state, on)).toBe(false)
    select(editor, doc => new GapCursor(doc.resolve(rule)))
    expect(smoothCaretShown(editor.state, on)).toBe(false)
  })
})

describe('the side the caret leans on', () => {
  it('is the letter before it, else the text after it (block start, after a space or a line break)', () => {
    const { state: { doc } } = mount()
    expect(smoothCaretSide(doc.resolve(2))).toBe(-1) // a|b
    expect(smoothCaretSide(doc.resolve(1))).toBe(1) // |ab
    expect(smoothCaretSide(doc.resolve(4))).toBe(1) // ab |cd: a soft wrap there starts the next line
    expect(smoothCaretSide(doc.resolve(7))).toBe(1) // after the line break
  })

  it('at a soft wrap: the upper line after End, the clicked line after a click, else as the text leans', () => {
    const upper = { left: 300, top: 10, bottom: 30 }, lower = { left: 0, top: 36, bottom: 56 }
    expect(smoothCaretAtWrap(lower, upper, { kind: 'end' })).toBe(upper)
    expect(smoothCaretAtWrap(lower, upper, { kind: 'pointer', y: 20 })).toBe(upper)
    expect(smoothCaretAtWrap(lower, upper, { kind: 'pointer', y: 40 })).toBe(lower)
    expect(smoothCaretAtWrap(lower, upper, null)).toBe(lower)
    expect(smoothCaretAtWrap(upper, { ...upper, left: 310 }, { kind: 'end' })).toBe(upper)
  })
})
