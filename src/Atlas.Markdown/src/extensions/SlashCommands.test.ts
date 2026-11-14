// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor, JSONContent } from '@tiptap/core'
import { createMarkdownEditorController, defineMarkdownBlockModule, defineMarkdownCommand, type MarkdownAction, type MarkdownEditorController, type MarkdownPlugin } from '../core'
import { editorForController } from '../core/internal'
import { configureMarkdownEditorView } from '../vue/controllerView'
import { slashSuggestion, type SlashCommandItem } from './SlashCommands'

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })

const lookup = defineMarkdownCommand<{ text: string }>({ id: 'test.lookup', apply: ({ text }) => ({ commands }) => commands.insertContent(`found ${text}`) })
const plain = defineMarkdownCommand({ id: 'test.plain', apply: () => ({ commands }) => commands.insertContent('plain') })
const noteInsert = defineMarkdownCommand({ id: 'test.note', apply: () => ({ commands }) => commands.insertContent('note') })
const parameterAction: MarkdownAction = {
  id: 'test.contact', label: 'Kontakty', keywords: ['kontakt', 'osoba'], command: plain(),
  parameter: { label: 'Kontakt', command: text => lookup({ text }) },
}
const plugin = (actions: MarkdownAction[]): MarkdownPlugin => ({ id: 'slash-test', apiVersion: 1, create: () => ({ commands: [lookup, plain, noteInsert], actions: { slash: actions } }) })
const emptyValue = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } as const

function mount(actions: MarkdownAction[] = [parameterAction]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [plugin(actions)], source: { kind: 'local', value: emptyValue } })
  controllers.push(controller)
  configureMarkdownEditorView(controller, { compact: false })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!; editor.view.focus()
  return { controller, editor, root: editor.view.dom }
}
const rows = () => [...document.querySelectorAll('[role=option]')].map(row => row.querySelector('.t')?.textContent ?? '')
const open = async (editor: Editor, text: string) => {
  editor.commands.insertContent(text)
  await vi.waitFor(() => expect(editor.view.dom.getAttribute('aria-expanded')).toBe('true'))
}
const key = (root: HTMLElement, name: string) => { const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }); root.dispatchEvent(event); return event }
const item = (title: string, searchTerms: string[] = []): SlashCommandItem => ({ title, description: '', searchTerms, icon: 'x', command: () => undefined })
const list = (query: string, extra: SlashCommandItem[]) => (slashSuggestion({ extra }).items!({ query, editor: { state: {} } as unknown as Editor } as never) as SlashCommandItem[]).map(entry => entry.title)

describe('slash search', () => {
  it('finds Poznámka for /poznamka and ranks title prefix before keyword and substring', () => {
    const extra = [item('Nepovinná zmínka', ['poznamka']), item('Dopoznámka'), item('Moje poznámka', ['spoiler']), item('Poznámka')]
    const result = list('poznamka', extra)
    expect(result.indexOf('Poznámka')).toBeLessThan(result.indexOf('Moje poznámka'))
    expect(result.indexOf('Poznámka')).toBeLessThan(result.indexOf('Nepovinná zmínka'))
    expect(result.indexOf('Nepovinná zmínka')).toBeLessThan(result.indexOf('Dopoznámka'))
    expect(list('poznámka', extra)).toEqual(result)
  })
  it('folds diacritics in both directions and keeps the order of the list for an empty query', () => {
    expect(list('zminka', [item('Zmínka')])).toContain('Zmínka')
    expect(list('zmínka', [item('Zminka')])).toContain('Zminka')
    expect(list('', [item('Bbb'), item('Aaa')]).slice(-2)).toEqual(['Bbb', 'Aaa'])
  })
  it('finds Časová odrážka by ASCII /cas and /time', () => {
    for (const query of ['cas', 'time', 'timestamp', 'date', 'log']) expect(list(query, [])).toContain('Časová odrážka')
  })
  it('keeps keywords of block modules', () => {
    const define = (options: { id: string; keywords?: string[] }) => defineMarkdownBlockModule<{ value: string }>({ id: options.id, name: `${options.id}Block`, label: 'Karta', defaults: { value: '' }, keywords: options.keywords,
      normalize: value => ({ value: String((value as { value?: unknown } | null)?.value ?? '') }), text: data => data.value, load: async () => ({ render: () => undefined }) })
    const withModule = createMarkdownEditorController({ profile: 'rich', plugins: [define({ id: 'kw', keywords: ['vizitka', 'osoba'] }).createPlugin()], source: { kind: 'local', value: emptyValue } })
    controllers.push(withModule)
    expect(withModule.actions.slash[0]?.keywords).toEqual(['vizitka', 'osoba'])
    expect(withModule.actions.toolbar[0]?.keywords).toEqual(['vizitka', 'osoba'])
    const instance = define({ id: 'nokw' }).createPlugin().create({ profile: 'rich', signal: new AbortController().signal })
    expect(instance.actions?.slash?.[0]).not.toHaveProperty('keywords')
  })
  it('lists an action by a diacritic-free keyword in the open menu', async () => {
    const { editor } = mount([{ id: 'test.mention', label: 'Zmínka', command: noteInsert() }])
    await open(editor, '/zminka')
    expect(rows()).toContain('Zmínka')
  })
})

describe('slash parameter', () => {
  it('continues after a space only for an action with a parameter and dispatches it with the text', async () => {
    const { editor, root } = mount([parameterAction, { id: 'test.note', label: 'Poznámka', command: noteInsert() }])
    await open(editor, '/kontakt Petr Novák')
    await vi.waitFor(() => expect(rows()[0]).toBe('Kontakt: vyhledat „Petr Novák“'))
    expect(key(root, 'Enter').defaultPrevented).toBe(true)
    expect(editor.getText()).toBe('found Petr Novák')
    expect(document.querySelector('[role=listbox]')).toBeNull()
  })
  it('matches the name without diacritics and any keyword of the action', async () => {
    const { editor, root } = mount()
    await open(editor, '/OSOBA   Černý')
    await vi.waitFor(() => expect(rows()[0]).toBe('Kontakt: vyhledat „Černý“'))
    key(root, 'Enter'); expect(editor.getText()).toBe('found Černý')
  })
  it('closes the menu at a space after an action without a parameter, as before', async () => {
    const { editor } = mount([parameterAction, { id: 'test.note', label: 'Poznámka', keywords: ['note'], command: noteInsert() }])
    await open(editor, '/note')
    expect(rows()).toContain('Poznámka')
    editor.commands.insertContent(' dalsi')
    await vi.waitFor(() => expect(document.querySelector('[role=listbox]')).toBeNull())
    expect(editor.getText()).toBe('/note dalsi')
  })
  it('keeps the menu on the plain action while no text follows the space', async () => {
    const { editor, root } = mount()
    await open(editor, '/kontakt ')
    await vi.waitFor(() => expect(rows()).toEqual(['Kontakty']))
    key(root, 'Enter'); expect(editor.getText()).toBe('plain')
  })
  it('still offers an ordinary action whose title starts with the typed words', async () => {
    const { editor } = mount([parameterAction, { id: 'test.inline', label: 'Kontakt v textu', command: noteInsert() }])
    await open(editor, '/kontakt v')
    await vi.waitFor(() => expect(rows()).toEqual(['Kontakt: vyhledat „v“', 'Kontakt v textu']))
  })
  it('reads no parameter from words that still spell the item\'s own name („/úkol z youtracku“; UX ED-5)', async () => {
    const issue: MarkdownAction = { ...parameterAction, id: 'test.issue', label: 'Úkol z YouTracku', keywords: ['úkol', 'youtrack'] }
    const { editor } = mount([issue])
    await open(editor, '/úkol z youtracku')
    await vi.waitFor(() => expect(rows()).toEqual(['Úkol z YouTracku']))
    editor.commands.insertContent(' PRJ-12')
    await vi.waitFor(() => expect(rows()[0]).toBe('Kontakt: vyhledat „PRJ-12“'))
  })
  it('takes the kind of an information box after its name („/box tip“; UX ED-9)', async () => {
    const { editor, root } = mount()
    await open(editor, '/box upoz')
    await vi.waitFor(() => expect(rows()[0]).toBe('Informační box: Upozornění'))
    key(root, 'Enter')
    expect(editor.getJSON().content?.[0]).toMatchObject({ type: 'callout', attrs: { variant: 'warning' } })
  })
  it('keeps the menu open over a space while the words still name an item („/nadpis 2“)', async () => {
    const { editor, root } = mount()
    await open(editor, '/nadpis 2')
    await vi.waitFor(() => expect(rows()[0]).toBe('Nadpis 2'))
    key(root, 'Enter')
    expect(editor.getJSON().content?.[0]).toMatchObject({ type: 'heading', attrs: { level: 2 } })
    expect(editor.getText().trim()).toBe('')
  })
  it('takes the size of a table after its name („/tabulka 3x4“ is three columns, four rows)', async () => {
    const { editor, root } = mount()
    await open(editor, '/tabulka 3x4')
    await vi.waitFor(() => expect(rows()[0]).toBe('Tabulka 3 × 4 (sloupce × řádky)'))
    key(root, 'Enter')
    const table = editor.getJSON().content?.find(node => node.type === 'table') as JSONContent | undefined
    expect(table?.content).toHaveLength(4)
    expect(table?.content?.[0]?.content).toHaveLength(3)
    // The caret waits in the first cell.
    expect(editor.state.selection.$from.node(-1).type.name).toBe('tableHeader')
  })
  it('typed key by key with input rules („2x2“ becomes „2×2“) and Enter at once, „/tabulka 2x2“ is one 2 × 2 table and no text', async () => {
    for (const pause of [0, 1, 5]) {
      const { editor, root } = mount()
      editor.commands.insertContent({ type: 'table', content: [{ type: 'tableRow', content: [{ type: 'tableHeader', content: [{ type: 'paragraph' }] }] }] })
      editor.commands.focus('end')
      for (const char of '/tabulka 2x2') {
        const { from, to } = editor.state.selection
        if (!editor.view.someProp('handleTextInput', handle => handle(editor.view, from, to, char, () => editor.state.tr.insertText(char, from, to)))) editor.view.dispatch(editor.state.tr.insertText(char, from, to))
        if (pause) await new Promise(resolve => setTimeout(resolve, pause))
      }
      expect(key(root, 'Enter').defaultPrevented, `pause ${pause}`).toBe(true)
      const tables = editor.getJSON().content!.filter(node => node.type === 'table')
      expect(tables, `pause ${pause}`).toHaveLength(2)
      expect(tables[1]!.content, `pause ${pause}`).toHaveLength(2)
      expect(editor.getText(), `pause ${pause}`).not.toMatch(/tabulka|2×2|2x2/)
      // The caret waits in the new table, not in the one before it.
      expect(editor.state.selection.$from.index(0), `pause ${pause}`).toBe(1)
      controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren()
    }
  })
  it('„/tabulka 2x2“ arriving as one input (text replacement, an automation) keeps every character, and Enter makes the 2 × 2 table', async () => {
    const { editor, root } = mount()
    const input = new InputEvent('beforeinput', { inputType: 'insertText', data: '/tabulka 2x2', bubbles: true, cancelable: true })
    root.dispatchEvent(input)
    expect(input.defaultPrevented).toBe(true)
    expect(editor.getText()).toBe('/tabulka 2×2')
    expect(key(root, 'Enter').defaultPrevented).toBe(true)
    const table = editor.getJSON().content?.find(node => node.type === 'table') as JSONContent | undefined
    expect(table?.content).toHaveLength(2)
    expect(editor.getText()).not.toMatch(/tabulka|2×2/)
  })
  it('puts text arriving as one input at a caret the browser moved before ProseMirror read it (a click, then the text at once)', () => {
    const { editor, root } = mount()
    editor.commands.setContent({ type: 'doc', content: ['První', 'Druhý'].map(text => ({ type: 'paragraph', content: [{ type: 'text', text }] })) })
    editor.commands.setTextSelection(1)
    // The click's caret, without its selectionchange yet.
    const second = root.querySelectorAll('p')[1]!.firstChild!
    document.getSelection()!.collapse(second, second.textContent!.length)
    const input = new InputEvent('beforeinput', { inputType: 'insertText', data: ' text', bubbles: true, cancelable: true })
    root.dispatchEvent(input)
    expect(editor.getText()).toBe('První\n\nDruhý text')
  })
  it('asks which rows can run once per open menu, not at every keystroke (J12.5)', async () => {
    const { controller, editor } = mount()
    await open(editor, '/')
    await vi.waitFor(() => expect(rows().length).toBeGreaterThan(5))
    const query = vi.spyOn(controller, 'query')
    for (const char of 'nadpis') { editor.view.dispatch(editor.state.tr.insertText(char)); await new Promise(resolve => setTimeout(resolve, 0)) }
    await vi.waitFor(() => expect(rows()[0]).toContain('Nadpis'))
    expect(query).not.toHaveBeenCalled()
  })
  it('offers no size row for a malformed size', async () => {
    const { editor } = mount()
    await open(editor, '/tabulka 3x')
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(rows().some(row => row.includes('×'))).toBe(false)
  })
  it('takes the language of a code block after its name („/kód ts“)', async () => {
    const { editor, root } = mount()
    await open(editor, '/kód ts')
    await vi.waitFor(() => expect(rows()[0]).toBe('Blok kódu: ts'))
    key(root, 'Enter')
    expect(editor.getJSON().content?.[0]).toMatchObject({ type: 'codeBlock', attrs: { language: 'ts' } })
  })
  it('acts on what is typed now when Enter follows the last letter at once', async () => {
    const { editor, root } = mount()
    await open(editor, '/')
    // Several letters in one transaction and Enter before the menu could render them.
    editor.view.dispatch(editor.state.tr.insertText('vodor'))
    key(root, 'Enter')
    expect(editor.getJSON().content?.some(node => node.type === 'horizontalRule')).toBe(true)
    expect(editor.getText()).not.toContain('/')
  })
  it('picks the typed item when a collaboration transaction comes between the slash and fast typing (never the first row of „/“)', async () => {
    for (const remote of ['echo', 'empty']) {
      const { editor, root } = mount()
      await open(editor, '/')
      await vi.waitFor(() => expect(rows().length).toBeGreaterThan(5))
      // y-sync's own transactions (a remote change, the server echoing our edit) carry its meta; the menu must not keep stale rows.
      const tr = editor.state.tr.setMeta('y-sync$', { isChangeOrigin: true })
      if (remote === 'echo') tr.insertText('x', 1, 1).delete(1, 2)
      editor.view.dispatch(tr)
      editor.view.dispatch(editor.state.tr.insertText('h4'))
      key(root, 'Enter')
      expect(JSON.stringify(editor.getJSON().content) + editor.getText(), remote).toContain('"level":4')
      expect(editor.getText().trim(), remote).toBe('')
      controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren()
    }
  })
  it('ranks a picked item up next time', async () => {
    const { editor, root } = mount()
    await open(editor, '/c')
    await vi.waitFor(() => expect(rows().indexOf('Checklist')).toBeLessThan(rows().indexOf('Citace')))
    key(root, 'Escape'); editor.commands.clearContent()
    await open(editor, '/citace')
    key(root, 'Enter')
    editor.commands.insertContent({ type: 'paragraph' })
    await open(editor, '/c')
    await vi.waitFor(() => expect(rows()[0]).toBe('Citace'))
  })
  it('rejects a malformed parameter when the plugin is registered', () => {
    for (const parameter of [{ label: '', command: () => plain() }, { label: 'X' }, 'text']) {
      expect(() => createMarkdownEditorController({ profile: 'rich', plugins: [plugin([{ ...parameterAction, parameter: parameter as never }])], source: { kind: 'local', value: emptyValue } })).toThrow(/Invalid or duplicate slash action/)
    }
  })
})

describe('slash keyboard', () => {
  it('Tab takes the preselected row like Enter (P8) and keeps the focus in the editor', async () => {
    const { editor, root } = mount()
    await open(editor, '/nadpis')
    await vi.waitFor(() => expect(rows().length).toBeGreaterThan(1))
    const tab = key(root, 'Tab')
    expect(tab.defaultPrevented).toBe(true)
    expect(editor.getText()).not.toContain('/nadpis')
    expect(editor.state.doc.firstChild?.type.name).toBe('heading')
    expect(document.querySelector('[role=listbox]')).toBeNull()
    expect(document.activeElement).toBe(root)
  })

  it('Escape closes the menu and leaves the typed text', async () => {
    const { editor, root } = mount()
    await open(editor, '/nadpis')
    await vi.waitFor(() => expect(rows().length).toBeGreaterThan(1))
    key(root, 'Escape')
    expect(editor.getText()).toBe('/nadpis')
    expect(document.querySelector('[role=listbox]')).toBeNull()
  })
})
