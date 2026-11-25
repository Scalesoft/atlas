// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { selectBlockRange } from '../drag/blockSelection'
import { NodeSelection, TextSelection } from '@tiptap/pm/state'
import { createMarkdownEditorController, markdownKeyboardShortcuts, openMarkdownIntentions, openMarkdownIntentionsMenu, registerMarkdownIntentions, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../../core'
import { editorForController } from '../../core/internal'
import { createAssistPlugin, type MarkdownAssistSource } from '../../assist'
import { createEmojiPlugin } from '../../plugins/emoji'
import { createMathPlugin } from '../../plugins/math'
import { createPagesPlugin } from '../../plugins/pages'
import { createYouTrackPlugin } from '../../plugins/youtrack'

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const issue = { type: 'youtrackIssue', attrs: { id: '2-1', key: 'PX2-1', title: 'Přístup VPN', url: 'https://youtrack.example/issue/PX2-1', state: null } }
const paragraph = (...content: unknown[]) => ({ type: 'paragraph', content })
const text = (value: string) => ({ type: 'text', text: value })
const pages = createPagesPlugin({ source: { search: async () => [], matchUrl: () => false } as never })
function mount(content: unknown[], plugins: MarkdownPlugin[] = [createYouTrackPlugin({ client: { search: async () => [] }, picker: async () => null }), createEmojiPlugin(), pages]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins,
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: plugins.filter(plugin => plugin.schemaVersion).map(plugin => ({ id: plugin.id, version: plugin.schemaVersion! })),
      document: { type: 'doc', content } as MarkdownRichValue['document'] } } })
  controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!
  editor.commands.focus('end')
  return { controller, editor }
}
const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) => {
  const event = new KeyboardEvent('keydown', { key: name, code: name === ' ' ? 'Space' : undefined, bubbles: true, cancelable: true, ...init })
  editor.view.dom.dispatchEvent(event)
  return event
}
const ctrlSpace = (editor: Editor) => key(editor, ' ', { ctrlKey: true })
const menu = () => document.querySelector<HTMLElement>('[data-testid=intentions]')
const rows = () => [...menu()?.querySelectorAll('[role=option]') ?? []].map(row => row.querySelector('.t')?.textContent ?? '')
const active = () => menu()?.querySelector('[role=option][aria-selected=true] .t')?.textContent
const settle = () => new Promise(resolve => setTimeout(resolve, 0))

describe('„Co s tím jde dělat“ (Ctrl+Space, Alt+Enter)', () => {
  it('is in the keyboard help and opens on Ctrl+Space; plain text with nothing to do says so', () => {
    expect(markdownKeyboardShortcuts.map(entry => entry.keys.join(' '))).toEqual(expect.arrayContaining(['Ctrl Space', 'Alt Enter']))
    const { editor } = mount([paragraph(text('Obyčejná věta'))])
    expect(ctrlSpace(editor).defaultPrevented).toBe(true)
    expect(menu()?.textContent).toContain('Tady není co nabídnout')
    expect(key(editor, 'Escape').defaultPrevented).toBe(true)
    expect(menu()).toBeNull()
  })
  it('opens the same menu on Ctrl+., Shift+F10, the menu key and the right button; Shift + right button stays the browser’s (P2)', () => {
    const { editor } = mount([paragraph(text('Viz '), issue)])
    for (const [name, init] of [['.', { ctrlKey: true }], ['F10', { shiftKey: true }], ['ContextMenu', {}]] as const) {
      expect(key(editor, name, init).defaultPrevented).toBe(true)
      expect(rows()).toEqual(['Převést na text'])
      key(editor, 'Escape')
    }
    // The menu key's own contextmenu (Windows sends it on the release) opens no second menu and no browser menu.
    key(editor, 'ContextMenu')
    const release = new MouseEvent('contextmenu', { bubbles: true, cancelable: true })
    editor.view.dom.dispatchEvent(release)
    expect(release.defaultPrevented).toBe(true)
    expect(document.querySelectorAll('[data-testid=intentions]')).toHaveLength(1)
    key(editor, 'Escape')
    const chip = editor.view.dom.querySelector('.md-chip')!
    // The right button on a chip selects it first, then opens the menu at the pointer with its rows.
    editor.view.posAtCoords = () => ({ pos: 5, inside: 5 })
    const right = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, clientX: 40, clientY: 10 })
    chip.dispatchEvent(right)
    expect(right.defaultPrevented).toBe(true)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(rows()).toContain('Převést na text')
    // Its first row is preselected (Enter takes it); the menu names itself to a reader of the screen only (F11, F12).
    expect(active()).toBe(rows()[0])
    expect(menu()!.getAttribute('aria-label')).toBe('Co s tím jde dělat')
    expect(menu()!.querySelector<HTMLElement>('.md-suggestion-heading')!.hidden).toBe(true)
    key(editor, 'p')
    expect(menu()!.querySelector<HTMLElement>('.md-suggestion-heading')!.hidden).toBe(false)
    key(editor, 'Escape')
    const shifted = new MouseEvent('contextmenu', { bubbles: true, cancelable: true, shiftKey: true })
    chip.dispatchEvent(shifted)
    expect(shifted.defaultPrevented).toBe(false)
    expect(menu()).toBeNull()
  })
  it('a row with a shortcut shows the keys at its end', () => {
    const { editor } = mount([paragraph(text('Text'))])
    registerMarkdownIntentions(editor, () => [{ id: 'k', label: 'Se zkratkou', shortcut: 'Ctrl+D', run: () => undefined }])
    ctrlSpace(editor)
    expect(menu()!.querySelector('[role=option] kbd')?.textContent).toBe('Ctrl+D')
  })
  it('a chip next to the caret: „Převést na text“', () => {
    const { editor } = mount([paragraph(text('Viz '), issue)])
    ctrlSpace(editor)
    expect(rows()).toEqual(['Převést na text'])
    key(editor, 'Enter')
    expect(menu()).toBeNull()
    expect(editor.getText()).toBe('Viz PX2-1')
  })
  it('a selected chip as well, with copying and removing it (the issue stays: „Odebrat“, P12)', () => {
    const { editor } = mount([paragraph(text('Viz '), issue, text(' dál'))])
    editor.view.dispatch(editor.state.tr.setSelection(NodeSelection.create(editor.state.doc, 5)))
    ctrlSpace(editor)
    expect(rows()).toEqual(['Převést na text', 'Kopírovat', 'Odebrat'])
  })
  it('a selection: a page link with its words; a written issue key and shortcode: their chips', async () => {
    const picker = vi.fn(async () => ({ id: '2-7', key: 'PX2-7', title: 'Monitoring', url: 'https://youtrack.example/issue/PX2-7' }))
    const { editor } = mount([paragraph(text('Provozní příručka, úkol PX2-7 a :smile'))], [createYouTrackPlugin({ client: { search: async () => [] }, picker }), createEmojiPlugin(), pages])
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 1, 18)))
    ctrlSpace(editor)
    expect(rows()).toEqual(['Odkaz na stránku „Provozní příručka“'])
    key(editor, 'Enter')
    expect(editor.getText()).toContain('[[Provozní příručka')
    key(editor, 'Escape')

    editor.commands.setContent({ type: 'doc', content: [paragraph(text('Provozní příručka, úkol PX2-7 a :smile'))] } as never)
    editor.commands.setTextSelection(28)
    ctrlSpace(editor)
    expect(rows()).toEqual(['Úkol YouTrack PX2-7'])
    key(editor, 'Enter')
    await vi.waitFor(() => expect(editor.state.doc.firstChild!.child(1).type.name).toBe('youtrackIssue'))
    expect(picker).toHaveBeenCalledWith(expect.objectContaining({ initialQuery: 'PX2-7' }))
    // The issue took the key's place; the words around it stay.
    expect(editor.state.doc.firstChild!.child(0).text).toBe('Provozní příručka, úkol ')
    expect(editor.state.doc.firstChild!.child(2).text).toBe(' a :smile')

    editor.commands.focus('end')
    ctrlSpace(editor)
    expect(rows()).toEqual(['Emoji 😄 (smile)'])
    key(editor, 'Enter')
    expect(editor.state.doc.firstChild!.lastChild!.type.name).toBe('emoji')
  })
  it('a selected element: its toolbar\'s actions, „Kopírovat“ and one delete; a table too, and Enter on „Smazat“ removes it', async () => {
    const { editor } = mount([paragraph(text('Před')), { type: 'mathBlock', attrs: { data: { version: 1, expression: 'x^2' } } }], [createMathPlugin()])
    await vi.waitFor(() => expect(document.querySelector('.md-context-toolbar [aria-label^="Upravit"]')).not.toBeNull(), { timeout: 30_000 })
    editor.commands.setNodeSelection(6)
    ctrlSpace(editor)
    expect(rows()[0]).toMatch(/^Upravit/)
    expect(rows()).toContain('Kopírovat')
    // One delete, in core's word, though the module's toolbar trash runs it.
    expect(rows().filter(row => /Odstranit|Smazat|Odebrat/.test(row))).toEqual(['Smazat'])
    key(editor, 'Escape')

    const cell = (type: string, value: string) => ({ type, content: [paragraph(text(value))] })
    editor.commands.setContent({ type: 'doc', content: [{ type: 'table', content: [{ type: 'tableRow', content: [cell('tableHeader', 'A')] }, { type: 'tableRow', content: [cell('tableCell', '1')] }] }, paragraph(text('Po'))] } as never)
    // Its grip selects it (BlockHandle): one selected element (not a selection of its cells), with the shared selected look.
    selectBlockRange(editor, 0)
    expect(editor.state.selection).toBeInstanceOf(NodeSelection)
    expect(editor.view.nodeDOM(0)).toHaveProperty('className', expect.stringContaining('ProseMirror-selectednode'))
    ctrlSpace(editor)
    expect(rows()).toEqual(['Kopírovat', 'Smazat'])
    key(editor, 'ArrowDown'); key(editor, 'Enter')
    expect(editor.state.doc.firstChild!.type.name).toBe('paragraph')
    expect(editor.getText()).toBe('Po')
  })
  it('arrows walk, typing filters without diacritics, Backspace takes the filter back, Enter runs the active row', () => {
    const { editor } = mount([paragraph(text('Slovo'))])
    const run = vi.fn()
    registerMarkdownIntentions(editor, () => [{ id: 'a', label: 'První', run }, { id: 'b', label: 'Druhá věc', run }, { id: 'c', label: 'Třetí', keywords: ['zvlastni'], run }])
    ctrlSpace(editor)
    expect(rows()).toEqual(['První', 'Druhá věc', 'Třetí'])
    expect(active()).toBe('První')
    key(editor, 'ArrowDown')
    expect(active()).toBe('Druhá věc')
    key(editor, 't'); key(editor, 'r'); key(editor, 'e')
    expect(rows()).toEqual(['Třetí'])
    expect(menu()?.textContent).toContain('„tre“')
    key(editor, 'Backspace'); key(editor, 'Backspace'); key(editor, 'Backspace')
    expect(rows()).toHaveLength(3)
    for (const char of 'zvl') key(editor, char)
    expect(rows()).toEqual(['Třetí'])
    key(editor, 'Enter')
    expect(run).toHaveBeenCalledOnce()
    // The filter went into the menu, never into the text.
    expect(editor.getText()).toBe('Slovo')
  })
  it('adds the rows of a provider that answers later', async () => {
    const { editor } = mount([paragraph(text('Slovo'))])
    let answer!: (rows: { id: string; label: string; run(): void }[]) => void
    registerMarkdownIntentions(editor, () => new Promise(settle => { answer = settle }))
    ctrlSpace(editor)
    expect(menu()?.getAttribute('aria-busy')).toBe('true')
    answer([{ id: 'late', label: 'Pozdě, ale přece', run: () => {} }])
    await settle()
    expect(rows()).toEqual(['Pozdě, ale přece'])
  })
  it('does not open in a read-only editor; another key closes it and goes to the text', () => {
    const { editor } = mount([paragraph(text('Slovo'))])
    registerMarkdownIntentions(editor, () => [{ id: 'a', label: 'Něco', run: () => {} }])
    ctrlSpace(editor)
    expect(key(editor, 'ArrowLeft').defaultPrevented).toBe(false)
    expect(menu()).toBeNull()
    editor.setEditable(false)
    expect(openMarkdownIntentions(editor)).toBe(false)
  })
})

describe('AI rows (the assist module)', () => {
  const source: MarkdownAssistSource = { id: 'test', suggest: async () => [{ id: 'fix', kind: 'fix', title: 'Opravit překlep', icon: 'sparkles', apply: ({ editor }) => editor.commands.insertContent('!') }] }
  it('are absent without the assist module: Alt+Enter is the same menu', () => {
    const { editor } = mount([paragraph(text('Slovo'))])
    registerMarkdownIntentions(editor, () => [{ id: 'a', label: 'Něco', run: () => {} }])
    expect(key(editor, 'Enter', { altKey: true }).defaultPrevented).toBe(true)
    expect(rows()).toEqual(['Něco'])
  })
  it('come from the loaded module: its suggestions first on Alt+Enter, last on Ctrl+Space, and Enter applies one', async () => {
    const { editor } = mount([paragraph(text('Slovo'))], [createAssistPlugin({ sources: [source] })])
    registerMarkdownIntentions(editor, () => [{ id: 'a', label: 'Něco', run: () => {} }])
    key(editor, 'Enter', { altKey: true })
    await vi.waitFor(() => expect(rows()).toEqual(['Opravit překlep', 'Něco']))
    key(editor, 'Escape')
    ctrlSpace(editor)
    expect(rows()).toEqual(['Něco', 'Opravit překlep'])
    key(editor, 'Escape')
    key(editor, 'Enter', { altKey: true })
    key(editor, 'Enter')
    await vi.waitFor(() => expect(editor.getText()).toBe('Slovo!'))
  })
  it('offer the host AI actions over a selection only when they can answer', () => {
    let able = false
    const selectionMenu = { label: 'AI', available: () => able, actions: [{ id: 'shorten', label: 'Zkrátit', run: vi.fn() }] }
    const { editor } = mount([paragraph(text('Dlouhá věta'))], [createAssistPlugin({ sources: [], selectionMenu })])
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 1, 7)))
    ctrlSpace(editor)
    expect(rows()).toEqual([])
    key(editor, 'Escape')
    able = true
    editor.view.dispatch(editor.state.tr.setSelection(TextSelection.create(editor.state.doc, 1, 7)))
    ctrlSpace(editor)
    expect(rows()).toEqual(['Zkrátit'])
    key(editor, 'Enter')
    expect(selectionMenu.actions[0]!.run).toHaveBeenCalledWith(expect.objectContaining({ range: { from: 1, to: 7 } }))
  })
})

describe('the same menu without an editor (a board item)', () => {
  it('opens at its anchor with the first row preselected, filters by typing, Enter runs, rows that answer later join', async () => {
    const run = vi.fn(), closed = vi.fn()
    let answer!: (rows: { id: string; label: string; run: () => void }[]) => void
    const anchor = document.createElement('div'); document.body.append(anchor)
    const opened = openMarkdownIntentionsMenu({ document, anchor: () => anchor.getBoundingClientRect(), contextElement: anchor, onClose: closed,
      rows: [[{ id: 'edit', label: 'Upravit', run: vi.fn() }, { id: 'delete', label: 'Smazat', run }], new Promise(resolve => { answer = resolve })] })
    expect(rows()).toEqual(['Upravit', 'Smazat'])
    expect(active()).toBe('Upravit')
    answer([{ id: 'ai', label: 'Shrnout', run: vi.fn() }])
    await settle()
    expect(rows()).toEqual(['Upravit', 'Smazat', 'Shrnout'])
    for (const letter of 'sma') expect(opened.key(new KeyboardEvent('keydown', { key: letter }))).toBe(true)
    expect(rows()).toEqual(['Smazat'])
    expect(opened.key(new KeyboardEvent('keydown', { key: 'Enter' }))).toBe(true)
    expect(run).toHaveBeenCalledOnce()
    expect(menu()).toBeNull()
    expect(closed).toHaveBeenCalledOnce()
  })
  it('a host\'s row list (the page tree): a line parts its groups, a destructive row is red, a disabled row is skipped', () => {
    const anchor = document.createElement('div'); document.body.append(anchor)
    const opened = openMarkdownIntentionsMenu({ document, anchor: () => anchor.getBoundingClientRect(), contextElement: anchor, rows: [[
      { id: 'open', label: 'Otevřít', separated: true, disabled: true, run: vi.fn() }, { id: 'rename', label: 'Přejmenovat', shortcut: 'F2', run: vi.fn() },
      { id: 'trash', label: 'Přesunout do koše', separated: true, destructive: true, run: vi.fn() }]] })
    // No line above the first row shown; the one above Koš is not an option.
    expect([...menu()!.children].map(child => child.className)).toEqual(['md-suggestion-heading', 'row', 'row on', 'sep', 'row danger'])
    expect(menu()!.querySelector('.sep')!.getAttribute('aria-hidden')).toBe('true')
    expect(active()).toBe('Přejmenovat')
    opened.key(new KeyboardEvent('keydown', { key: 'k' }))
    expect(rows()).toEqual(['Přesunout do koše'])
    expect(menu()!.querySelector('.sep')).toBeNull()
    opened.close()
  })
})
