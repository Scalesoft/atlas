// @vitest-environment jsdom
import { afterEach, expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, parseMarkdownContent, serializeMarkdownContent, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { projectMarkdownAssistSelection } from './index'
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
function mount() {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
    document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Before' }] }, { type: 'image', attrs: { src: 'https://private.example/picture', alt: 'private metadata' } }, { type: 'paragraph', content: [{ type: 'text', text: 'after' }] }] } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Document' })
  return { editor, controller }
}
it('public codec parses and serializes the same Markdown used by the optional module', () => {
  const { editor } = mount()
  const markdown = '**Strong** and a [link](https://example.com)\n\n- Item'
  const parsed = parseMarkdownContent(editor, markdown)
  expect(parseMarkdownContent(editor, serializeMarkdownContent(editor, parsed)).eq(parsed)).toBe(true)
})
it('projects opaque nodes without metadata and restores only exact ordered tokens', () => {
  const { editor } = mount()
  const projection = projectMarkdownAssistSelection(editor, { from: 0, to: editor.state.doc.content.size })!
  expect(projection.text).not.toContain('private')
  const token = projection.text.match(/⟦c:[a-f0-9]{16}:1⟧/)![0]
  const restored = projection.restore(projection.text.replace('Before', 'Changed'))
  expect(restored.content![1]).toEqual(editor.state.doc.child(1).toJSON())
  expect(() => projection.restore(projection.text.replace(token, ''))).toThrow()
  expect(() => projection.restore(projection.text + token)).toThrow()
  expect(() => projection.restore(projection.text + '\n\n![new](https://example.com)')).toThrow()
})
it('tracks selected content including attributes while mapping changes outside it', () => {
  const { editor } = mount()
  const projection = projectMarkdownAssistSelection(editor, { from: editor.state.doc.firstChild!.nodeSize, to: editor.state.doc.firstChild!.nodeSize + 1 })!
  editor.view.dispatch(editor.state.tr.insert(0, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Prefix'))))
  expect(projection.range).not.toBeNull()
  const image = projection.range!.from
  editor.view.dispatch(editor.state.tr.setNodeMarkup(image, undefined, { src: 'https://changed.example', alt: 'changed' }))
  expect(projection.range).toBeNull()
  projection.dispose()
})



it('rejects reserved user tokens and preserves a link as an opaque local mark', () => {
  const { editor } = mount()
  editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'See ' }, { type: 'text', text: 'private link label', marks: [{ type: 'link', attrs: { href: 'https://private.example/link' } }] }] }] })
  const snapshot = projectMarkdownAssistSelection(editor, { from: 0, to: editor.state.doc.content.size })!
  expect(snapshot.text).not.toContain('private')
  expect(snapshot.restore(snapshot.text).content![0]!.content![1]).toEqual(editor.state.doc.firstChild!.child(1).toJSON())
  expect(() => snapshot.restore(snapshot.text + '\n::secret{x}')).toThrow()
  snapshot.dispose()
  editor.commands.setContent('<p>literal ⟦c:1234567890abcdef:1⟧</p>')
  expect(projectMarkdownAssistSelection(editor, { from: 0, to: editor.state.doc.content.size })).toBeNull()
})
it('does not retain a pending inline selection after its parent formatting changes', () => {
  const { editor } = mount()
  const snapshot = projectMarkdownAssistSelection(editor, { from: 1, to: 7 })!
  editor.view.dispatch(editor.state.tr.setNodeMarkup(0, editor.schema.nodes.heading, { level: 2 }))
  expect(snapshot.range).toBeNull()
  snapshot.dispose()
})

it('snapshots the default editor selection and maps it through outside edits', () => {
  const { editor } = mount()
  editor.commands.setTextSelection({ from: 1, to: 7 })
  const projection = projectMarkdownAssistSelection(editor)!
  expect(projection.range).toEqual({ from: 1, to: 7 })
  editor.view.dispatch(editor.state.tr.insert(0, editor.schema.nodes.paragraph!.create(null, editor.schema.text('Prefix'))))
  expect(projection.range).toEqual({ from: 9, to: 15 })
  expect(projection.restore(projection.text).content![0]!.content![0]!.text).toBe('Before')
  projection.dispose()
})
