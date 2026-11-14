// @vitest-environment jsdom
import { afterEach, beforeAll, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, openMarkdownIntentions, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { createMathPlugin } from '../plugins/math'
import { createConcealedContentPlugin } from '../plugins/concealed'
import { createAssistPlugin, markdownAssistTargets, readMarkdownAssistText, type AssistPluginOptions } from './index'

// P11 in the editor: what the AI actions of a host act on (the selection or element, the heading's section or the block, the page),
// and the text the reading ones may send. A real editor with the module, the host's actions stubbed.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }
document.elementFromPoint ??= () => null
beforeAll(async () => { await import('../concealment') })

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const t = (text: string) => ({ type: 'text', text })
const h = (level: number, text: string): JSONContent => ({ type: 'heading', attrs: { level }, content: [t(text)] })
const p = (text: string): JSONContent => ({ type: 'paragraph', content: [t(text)] })
const doc: JSONContent = { type: 'doc', content: [h(2, 'Úvod'), p('První.'), h(3, 'Podrobnosti'), p('Druhý.'), h(2, 'Další'), p('Mimo.'),
  { type: 'mathBlock', attrs: { data: { version: 1, expression: 'x^2' } } }, { type: 'concealedContent', content: [p('Tajné')] },
  { type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableCell', content: [p('Buňka')] }] }] }] }

function mount(menu?: AssistPluginOptions['selectionMenu']) {
  let editor!: Editor
  const capture: MarkdownPlugin = { id: 'capture', apiVersion: 1, create: () => ({ onCreate: context => { editor = context.editor } }) }

  const plugins = [createMathPlugin(), createConcealedContentPlugin(), createAssistPlugin({ sources: [], ...(menu ? { selectionMenu: menu } : {}) }), capture]
  const value = { format: 'atlas-rich', version: 1, plugins: plugins.slice(0, 2).map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: doc }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value } as never }); controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Dokument' })
  return editor
}
const position = (editor: Editor, type: string, text?: string) => { let found = -1; editor.state.doc.descendants((node, pos) => { if (found < 0 && node.type.name === type && (text === undefined || node.textContent === text)) found = pos; return found < 0 }); return found }

it('finds the section of a heading, the block at the caret, the selected element, and the page last', () => {
  const editor = mount()
  editor.commands.setTextSelection(position(editor, 'heading', 'Úvod') + 2)
  const [section, heading, page] = markdownAssistTargets(editor)
  expect(section).toMatchObject({ kind: 'section', label: 'Sekce „Úvod“', from: 0, to: position(editor, 'heading', 'Další') })
  expect(heading).toMatchObject({ kind: 'block', label: 'Nadpis „Úvod“', from: 0, to: position(editor, 'paragraph', 'První.'), fallback: true })
  expect(page).toMatchObject({ kind: 'page', from: 0, to: editor.state.doc.content.size })
  editor.commands.setTextSelection(position(editor, 'paragraph', 'Druhý.') + 2)
  expect(markdownAssistTargets(editor)[0]).toMatchObject({ kind: 'block', label: 'Odstavec' })
  editor.commands.setNodeSelection(position(editor, 'mathBlock'))
  expect(markdownAssistTargets(editor)[0]).toMatchObject({ kind: 'element', label: 'Vzorec' })
  // A table selected by its grip is the same block as with the caret inside: the writing intents stay.
  editor.commands.setNodeSelection(position(editor, 'table'))
  expect(markdownAssistTargets(editor)[0]).toMatchObject({ kind: 'block', label: 'Tabulka' })
  editor.commands.setTextSelection({ from: 2, to: 4 })
  expect(markdownAssistTargets(editor)[0]).toMatchObject({ kind: 'selection' })
})

it('reads an element in its own dialect for the reading intents, and never a concealed part', () => {
  const editor = mount()
  const math = position(editor, 'mathBlock')
  expect(readMarkdownAssistText(editor, { from: math, to: math + editor.state.doc.nodeAt(math)!.nodeSize })).toContain('x^2')
  const section = markdownAssistTargets((editor.commands.setTextSelection(3), editor))[0]!
  expect(readMarkdownAssistText(editor, section)).toMatch(/Úvod[\s\S]*Podrobnosti[\s\S]*Druhý\./)
  expect(readMarkdownAssistText(editor, { from: 0, to: editor.state.doc.content.size })).toBeNull()
})

it('offers the host actions in „Co s tím jde dělat“ for each target they list, each only while available', async () => {
  const run = vi.fn()
  let answering = true
  const editor = mount({ label: 'AI', available: () => true, actions: [
    { id: 'summarize', label: 'Shrnout', targets: ['selection', 'element', 'section', 'block', 'page'], available: () => answering, run },
    { id: 'shorten', label: 'Zkrátit', intent: 'Přepsat', targets: ['selection', 'section', 'block'], run },
  ] })
  editor.view.focus(); editor.commands.setTextSelection(position(editor, 'heading', 'Úvod') + 2)
  const rows = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(row => row.textContent?.replace(/\s+/g, ' ').trim())
  expect(openMarkdownIntentions(editor)).toBe(true)
  await vi.waitFor(() => expect(rows()).toEqual(expect.arrayContaining([expect.stringContaining('Shrnout'), expect.stringContaining('Zkrátit')])))
  expect(rows().filter(row => row?.includes('Sekce „Úvod“'))).toHaveLength(2)
  expect(rows().filter(row => row?.includes('Stránka'))).toEqual([expect.stringContaining('Shrnout')])
  answering = false
  expect(openMarkdownIntentions(editor)).toBe(true)
  await vi.waitFor(() => expect(rows().some(row => row?.includes('Zkrátit'))).toBe(true))
  expect(rows().some(row => row?.includes('Shrnout'))).toBe(false)
})

it('offers the heading alone for what its section cannot take (a section over the limit), and nothing twice', async () => {
  const run = vi.fn()
  let limit = Infinity
  const editor = mount({ label: 'AI', available: () => true, actions: [
    { id: 'summarize', label: 'Shrnout', targets: ['section', 'block'], available: (_editor, range) => range.to - range.from <= limit, run },
  ] })
  editor.view.focus(); editor.commands.setTextSelection(position(editor, 'heading', 'Úvod') + 2)
  const rows = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(row => row.textContent?.replace(/\s+/g, ' ').trim())
  expect(openMarkdownIntentions(editor)).toBe(true)
  await vi.waitFor(() => expect(rows().some(row => row?.includes('Sekce „Úvod“'))).toBe(true))
  expect(rows().some(row => row?.includes('Nadpis'))).toBe(false)
  limit = editor.state.doc.nodeAt(0)!.nodeSize
  expect(openMarkdownIntentions(editor)).toBe(true)
  await vi.waitFor(() => expect(rows().some(row => row?.includes('Nadpis „Úvod“'))).toBe(true))
  expect(rows().some(row => row?.includes('Sekce'))).toBe(false)
})
