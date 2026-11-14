// @vitest-environment jsdom
import { afterEach, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController } from '..'
import { editorForController } from '../internal'
import { createContactsPlugin } from '../../plugins/contacts'
import type { MarkdownLexicon } from './lexicon'

// Its own file: the engine (engine.ts) must not have landed before the test starts.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const lexicon: MarkdownLexicon = { version: '1', truncated: false, youTrackProjects: [], pages: [],
  people: [{ key: 'object:0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', contactId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', name: 'Petr Svoboda', aliases: [], uses: 0 }] }
// Cold imports of the engine and the contacts runtime, kept out of the test's wait on a loaded host. Only the module cache is warm:
// the engine's loader (runtime.ts) has not run, so it still lands during the test.
await Promise.all([import('./engine'), import('../../plugins/contacts/runtime'), import('../../plugins/contacts/names')])
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const paragraph = (text: string) => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] })
function mount(text: string, editable = true) {
  const plugins = [createContactsPlugin({})]
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, lexicon: { read: () => lexicon },
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: plugins.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content: [paragraph(text)] } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)! as Editor
  if (!editable) editor.setEditable(false)
  const seen = { transactions: 0 }
  editor.on('transaction', () => { seen.transactions++ })
  return { editor, seen }
}
const chips = (editor: Editor) => { const found: string[] = []; editor.state.doc.descendants(node => { if (node.isInline && !node.isText) found.push(node.type.name) }); return found }
const settle = () => new Promise(resolve => setTimeout(resolve, 450))

it('the engine landing for one editor touches no other: a read-only one and a plain one get no transaction, then or later (P17)', async () => {
  const typing = mount(''), readOnly = mount('Volal Petr Svoboda.', false), plain = mount('Nic tu není.')
  // The plugins register their recognizers a task after the first render, the engine lands at the first idle (catchUp of a name
  // typed before it lands: runtime.test.ts, P17).
  await new Promise(resolve => setTimeout(resolve, 0))
  typing.editor.commands.setTextSelection(1)
  for (const char of 'Volal Petr Svoboda ') typing.editor.view.dispatch(typing.editor.state.tr.insertText(char))
  await vi.waitFor(() => expect(chips(typing.editor)).toEqual(['contactInline']))
  await settle()
  expect(readOnly.seen.transactions).toBe(0)
  expect(plain.seen.transactions).toBe(0)
  // One mounted after the engine is in reads it at once: still nothing to write into a read-only one.
  const later = mount('Psal Petr Svoboda.', false)
  await settle()
  expect(later.seen.transactions).toBe(0)
  expect(readOnly.editor.state.doc.textContent).toBe('Volal Petr Svoboda.')
})
