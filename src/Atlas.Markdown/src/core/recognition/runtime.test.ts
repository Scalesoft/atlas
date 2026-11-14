// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController } from '..'
import { editorForController } from '../internal'
import { createContactsPlugin } from '../../plugins/contacts'
import { createPagesPlugin } from '../../plugins/pages'
import { createYouTrackPlugin } from '../../plugins/youtrack'
import type { MarkdownLexicon } from './lexicon'
import { RECOGNITION_LATE_MS, registerMarkdownRecognizer } from './runtime'
import { textOnlyChange } from './engine'

Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const lexicon: MarkdownLexicon = { version: '1', truncated: false, youTrackProjects: ['PX2'],
  people: [{ key: 'object:0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', contactId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', name: 'Petr Svoboda', aliases: [], uses: 0 }],
  pages: [{ id: 'c0ffee00-0000-4000-8000-000000000001', title: 'Provozní příručka' }] }
const page = { id: 'c0ffee00-0000-4000-8000-000000000001', title: 'Provozní příručka', url: 'https://wiki.example/#/pages/c0ffee00-0000-4000-8000-000000000001' }
const issue = { id: 'id-1', key: 'PX2-1', title: 'Přihlášení', url: 'https://youtrack.example/issue/PX2-1' }

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
const paragraph = (text: string) => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] })
function mount(content: object[], resolve: (key: string) => Promise<typeof issue | null> = async () => issue, currentPage?: string) {
  const plugins = [createContactsPlugin({}), createYouTrackPlugin({ client: { search: async () => [], resolve } }), createPagesPlugin({ source: { search: async () => [page], resolve: async () => page, currentId: () => currentPage } })]
  const controller = createMarkdownEditorController({ profile: 'rich', plugins, lexicon: { read: () => lexicon },
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: plugins.map(plugin => ({ id: plugin.id, version: plugin.schemaVersion ?? 1 })), document: { type: 'doc', content } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)! as Editor; editor.view.focus()
  return editor
}
const idle = () => new Promise(resolve => setTimeout(resolve, 450))
const type = async (editor: Editor, text: string) => { for (const char of text) { editor.view.dispatch(editor.state.tr.insertText(char)); await Promise.resolve() } }
const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init }))
const chips = (editor: Editor) => { const found: string[] = []; editor.state.doc.descendants(node => { if (node.isInline && !node.isText) found.push(node.type.name) }); return found }
const underlined = () => [...document.querySelectorAll('.md-recognition-candidate')].map(element => `${element.getAttribute('data-recognition')}:${element.textContent}`)
const rows = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(row => row.querySelector('.t')?.textContent)

describe('the linking runtime', () => {
  it('underlines a page title like a known name; the actions menu links it, one step', async () => {
    const editor = mount([paragraph('Postup je v Provozní příručka, viz tam.')])
    await vi.waitFor(() => expect(underlined()).toEqual(['page:Provozní příručka']), { timeout: 2000 })
    editor.commands.setTextSelection(16)
    key(editor, ' ', { code: 'Space', ctrlKey: true })
    await vi.waitFor(() => expect(rows()[0]).toBe('Odkaz na stránku „Provozní příručka“'))
    key(editor, 'Enter')
    await vi.waitFor(() => expect(chips(editor)).toEqual(['pageReference']))
    expect(editor.getText()).toBe('Postup je v Provozní příručka, viz tam.')
  })

  it('a click on an underlined title offers it, though the browser tells the caret only later', async () => {
    const editor = mount([paragraph('Postup je v Provozní příručka, viz tam.')])
    await vi.waitFor(() => expect(underlined()).toEqual(['page:Provozní příručka']), { timeout: 2000 })
    editor.commands.setTextSelection(1)
    // ProseMirror's click on the underline, the caret still where it was (1): what a browser's mouseup gives the handler.
    const click = new MouseEvent('click'); Object.defineProperty(click, 'target', { value: document.querySelector('.md-recognition-candidate') })
    editor.view.someProp('handleClick', handle => { handle(editor.view, 16, click) })
    await vi.waitFor(() => expect(rows()[0]).toBe('Odkaz na stránku „Provozní příručka“'))
  })

  it('makes a typed issue key a chip when YouTrack answers in time; a late answer is only offered', async () => {
    const editor = mount([paragraph(''), paragraph('Jinde')])
    editor.commands.setTextSelection(1)
    await idle()
    await type(editor, 'Viz PX2-1 ')
    await vi.waitFor(() => expect(chips(editor)).toEqual(['youtrackIssue']))
    // Late: the answer comes after the person moved on to another block; the key stays text, underlined, picked from the menu.
    let answer!: (value: typeof issue) => void
    const late = mount([paragraph(''), paragraph('Jinde')], () => new Promise(resolve => { answer = resolve }))
    late.commands.setTextSelection(1)
    await idle()
    await type(late, 'Viz PX2-1 ')
    late.commands.setTextSelection(late.state.doc.content.size - 1)
    const now = Date.now()
    vi.spyOn(Date, 'now').mockReturnValue(now + RECOGNITION_LATE_MS + 1)
    answer(issue)
    await vi.waitFor(() => expect(underlined()).toContain('issue:PX2-1'), { timeout: 2000 })
    expect(chips(late)).toEqual([])
    late.commands.setTextSelection(6)
    key(late, ' ', { code: 'Space', ctrlKey: true })
    await vi.waitFor(() => expect(rows()[0]).toBe('Úkol YouTrack PX2-1'))
    key(late, 'Enter')
    await vi.waitFor(() => expect(chips(late)).toEqual(['youtrackIssue']))
  })

  it('decides a name typed before the engine has loaded once it lands: the chip, and Backspace right after takes it back (P17)', async () => {
    // The browser never idles here, so only the first keystroke that ends a word asks for the engine (engine.ts).
    vi.stubGlobal('requestIdleCallback', () => 0)
    try {
      const editor = mount([paragraph('')])
      // The plugins register their recognizers on the editor's `create`, a task after the first render.
      await new Promise(resolve => setTimeout(resolve, 0))
      // Every keystroke in one task: none of them waits for the download the first one started.
      for (const char of 'Volal Petr Svoboda ') editor.view.dispatch(editor.state.tr.insertText(char))
      expect(chips(editor)).toEqual([])
      await vi.waitFor(() => expect(chips(editor)).toEqual(['contactInline']))
      key(editor, 'Backspace')
      expect(editor.getText()).toBe('Volal Petr Svoboda')
    } finally { vi.unstubAllGlobals() }
  })

  it('decides a name typed right after a recognizer registers with an engine already in, before its next scan', async () => {
    const controller = createMarkdownEditorController({ profile: 'rich', lexicon: { read: () => lexicon }, source: { kind: 'local',
      value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [paragraph('')] } as never } } })
    controllers.push(controller)
    controller.attachView(document.body.appendChild(document.createElement('div')), { ariaLabel: 'Editor' })
    const editor = editorForController(controller)! as Editor
    // The engine lands at the first idle and indexes no recognizer.
    await idle()
    registerMarkdownRecognizer(editor, { kind: 'person', chip: () => ({ type: 'hardBreak' }),
      entries: () => [{ kind: 'person', id: 'p1', name: 'Petr Svoboda', form: 'full', auto: true, target: null }] })
    editor.commands.setTextSelection(1)
    for (const char of 'Petr Svoboda ') editor.view.dispatch(editor.state.tr.insertText(char))
    await vi.waitFor(() => expect(chips(editor)).toEqual(['hardBreak']))
  })

  it('leaves text turned back on purpose; once the person edits it, it may be linked again (R1)', async () => {
    const editor = mount([paragraph('')])
    await idle()
    await type(editor, 'Volal Petr Svoboda ')
    expect(chips(editor)).toEqual(['contactInline'])
    editor.commands.setNodeSelection(7)
    key(editor, ' ', { code: 'Space', ctrlKey: true, shiftKey: true })
    expect(editor.getText()).toBe('Volal Petr Svoboda ')
    // Typing on right after it: no chip again, no underline either.
    editor.commands.setTextSelection(19)
    await type(editor, ',')
    await idle()
    expect(chips(editor)).toEqual([])
    expect(underlined()).toEqual([])
    // An edit inside: other words now, so the memory goes; the name typed whole again is linked.
    editor.view.dispatch(editor.state.tr.delete(18, 19))
    editor.commands.setTextSelection(18)
    await type(editor, 'a ')
    expect(chips(editor)).toEqual(['contactInline'])
  })

  it('„Nenabízet“ on an underlined title stops offering that title in this editor, the document untouched (R1)', async () => {
    const editor = mount([paragraph('Postup je v Provozní příručka, viz tam.'), paragraph('Znovu Provozní příručka.')])
    await vi.waitFor(() => expect(underlined()).toEqual(['page:Provozní příručka', 'page:Provozní příručka']), { timeout: 2000 })
    const before = JSON.stringify(editor.getJSON())
    editor.commands.setTextSelection(16)
    key(editor, ' ', { code: 'Space', ctrlKey: true })
    await vi.waitFor(() => expect(rows()).toEqual(['Odkaz na stránku „Provozní příručka“', 'Nenabízet']))
    key(editor, 'ArrowDown'); key(editor, 'Enter')
    await vi.waitFor(() => expect(underlined()).toEqual([]))
    expect(JSON.stringify(editor.getJSON())).toBe(before)
    // Written again, it stays plain too, for as long as the editor is open.
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await type(editor, ' A Provozní příručka.')
    await idle()
    expect(underlined()).toEqual([])
  })

  it('never offers the page being edited as a link to itself', async () => {
    mount([paragraph('Tahle Provozní příručka a Petr Svoboda.')], undefined, page.id)
    await vi.waitFor(() => expect(underlined()).toEqual(['person:Petr Svoboda']), { timeout: 2000 })
  })

  it('underlines only the blocks that changed', async () => {
    const editor = mount([paragraph('Volal Petr Svoboda.'), paragraph('Psal Petr Svoboda.')])
    await vi.waitFor(() => expect(underlined()).toHaveLength(2), { timeout: 2000 })
    const first = document.querySelector('.md-recognition-candidate')
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await type(editor, ' A Provozní příručka.')
    await idle()
    expect(underlined()).toEqual(['person:Petr Svoboda', 'person:Petr Svoboda', 'page:Provozní příručka'])
    // The first block was not read again: its underline is the same element.
    expect(document.querySelector('.md-recognition-candidate')).toBe(first)
  })

  it('a scan that underlines nothing new dispatches no transaction (one per scan redrew the view)', async () => {
    const editor = mount([paragraph('Volal Petr Svoboda.')])
    await vi.waitFor(() => expect(underlined()).toEqual(['person:Petr Svoboda']), { timeout: 2000 })
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await type(editor, ' a nikdo jiný')
    const dispatched = vi.fn()
    editor.on('transaction', dispatched)
    await idle()
    expect(dispatched).not.toHaveBeenCalled()
    // A name typed after it is still underlined by the next scan.
    await type(editor, ' než Provozní příručka')
    await idle()
    expect(underlined()).toEqual(['person:Petr Svoboda', 'page:Provozní příručka'])
  })

  it('reads the entries again when a node of the document changes, not for every typed letter', async () => {
    const editor = mount([paragraph('Volal Petr Svoboda.')])
    await vi.waitFor(() => expect(underlined()).toEqual(['person:Petr Svoboda']), { timeout: 2000 })
    const entries = vi.fn(() => [])
    registerMarkdownRecognizer(editor, { kind: 'probe', entries, chip: () => null })
    await vi.waitFor(() => expect(entries).toHaveBeenCalled(), { timeout: 2000 })
    const read = entries.mock.calls.length
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await type(editor, ' A Provozní příručka.')
    await idle()
    // The scan ran (the new title is underlined) without asking a recognizer again.
    expect(underlined()).toEqual(['person:Petr Svoboda', 'page:Provozní příručka'])
    expect(entries).toHaveBeenCalledTimes(read)
    editor.commands.insertContent({ type: 'hardBreak' })
    await idle()
    expect(entries.mock.calls.length).toBeGreaterThan(read)
  })
})

describe('textOnlyChange', () => {
  const docOf = (editor: Editor) => editor.state.doc
  it('tells typed letters from a node that came, went or changed', () => {
    const editor = mount([paragraph('Ahoj'), paragraph('aa')])
    const before = docOf(editor)
    const typed = before.type.schema.nodes.doc!.create(null, [before.child(0), before.type.schema.nodes.paragraph!.create(null, before.type.schema.text('aaa'))])
    expect(textOnlyChange(before, typed)).toBe(true)
    editor.commands.setTextSelection(3)
    editor.commands.insertContent({ type: 'hardBreak' })
    expect(textOnlyChange(before, docOf(editor))).toBe(false)
    expect(textOnlyChange(docOf(editor), before)).toBe(false)
    expect(textOnlyChange(before, before.type.schema.nodes.doc!.create(null, [before.child(0)]))).toBe(false)
  })
})
