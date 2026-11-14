// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Collaboration from '@tiptap/extension-collaboration'
import { yUndoPluginKey } from '@tiptap/y-tiptap'
import * as Y from 'yjs'
import { CollaborationHistoryBoundary } from '../../collab/historyBoundary'
import { ChipText } from '../../features/chips/chipText'
import { MarkdownRecognition } from '../../core/recognition/runtime'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { createContactsPlugin } from './index'
import type { ContactsPluginOptions, MarkdownContact } from './data'
import { nameKey, recognitionWindow, type MarkdownLexicon } from '../../core'
import { buildRecognitionIndex, recognizeTail, recognizeText } from '../../core/recognition/recognize'
import { personEntries } from './namesLoader'
import { contactsStrings } from './strings'
import { rankPeople } from './names'

// jsdom has no layout; scrolling the caret into view asks for rectangles.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const jana: MarkdownContact = { name: 'Jana Nováková', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', organization: 'Firma' }
const petr: MarkdownContact = { name: 'Petr Svoboda', emails: ['petr@firma.example.test'] }
/** The person entries the core indexes (namesLoader `personEntries`): `people` as a lexicon, `inDoc` as the document's contacts. */
const lexiconOf = (people: readonly (MarkdownContact & { aliases?: string[] })[], truncated = false): MarkdownLexicon => ({ version: '1', truncated, pages: [], youTrackProjects: [],
  people: people.map((person, i) => ({ key: person.objectId ? `object:${person.objectId}` : person.emails?.[0] ? `email:${person.emails[0]}` : `k${i}`, contactId: person.objectId ?? null, userId: null, name: person.name, aliases: person.aliases ?? [], org: person.organization ?? null, uses: 0 })) })
const docOf = (inDoc: readonly MarkdownContact[] = []) => ({ descendants: (visit: (node: unknown) => boolean) => inDoc.forEach(contact => visit({ type: { name: 'contactInline' }, attrs: { data: { contacts: [contact] } } })) }) as never
const indexOf = (people: readonly (MarkdownContact & { aliases?: string[] })[], inDoc: readonly MarkdownContact[] = [], truncated = false) =>
  buildRecognitionIndex(personEntries({}, lexiconOf(people, truncated), docOf(inDoc)), new Set(['person']))
const index = indexOf([jana, petr, { name: 'Eva' }, { name: 'Jan Novák Mladší' }])
const found = (text: string, at = index) => recognizeText(text, at).map(hit => [text.slice(hit.from, hit.to), hit.decision])
const typedName = (before: string, at = index) => { const hit = recognizeTail(before, at); return hit && { from: hit.from, to: hit.to, decision: hit.decision, people: hit.candidates.length } }

describe('known names (pure, through the core)', () => {
  it('keys a name by its folded words; one person once, another of that name a second; document contacts only add new names', () => {
    expect(nameKey('  JANA   Nováková ')).toBe('jana novakova')
    // „Eva“ alone is everyday text: one word is no name.
    expect(found('Eva a Jan Novák Mladší')).toEqual([['Jan Novák Mladší', 'chip']])
    const twice = indexOf([jana, { ...jana, organization: 'Jinde' }, { name: 'Jana Nováková', emails: ['jn@other.example.test'] }], [{ name: 'Jana Nováková' }, { name: 'Karel Malý' }])
    expect(typedName('Volala Jana Nováková ', twice)).toMatchObject({ decision: 'unresolved', people: 2 })
    expect(typedName('Volal Karel Malý ', twice)).toMatchObject({ decision: 'chip', people: 1 })
  })
  it('finds whole names, case and diacritics aside, joined by spaces only; inflected forms are only offered', () => {
    const text = 'Volala jana novakova, pak Petr  Svoboda; ne Petr, Svoboda ani Janě Novákové ani jana.novakova@firma.example.test'
    expect(found(text)).toEqual([['jana novakova', 'chip'], ['Petr  Svoboda', 'chip'], ['Janě Novákové', 'suggest']])
    expect(recognizeText('Jan Novák Mladší', index)).toMatchObject([{ from: 0, to: 16 }])
    expect(recognizeText('Jana Nováková Petr Svoboda', index, 1)).toHaveLength(1)
  })
  it('indexes the aliases of three letters or more beside the names, offers them, never converts one typed just now', () => {
    const martin = { name: 'Marek Musil', objectId: '2b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', aliases: ['MMu', 'MM', 'Mára Musil'] }
    const aliased = indexOf([martin, { ...jana, aliases: ['Jana Nováková'] }])
    // „MM“ (two people's initials) is not looked for.
    expect(found('Zápis pošle MMu, Mára Musil a MM', aliased)).toEqual([['MMu', 'suggest'], ['Mára Musil', 'suggest']])
    expect(typedName('Zápis pošle MMu ', aliased)?.decision).toBe('suggest')
    expect(typedName('pošle Mára Musil ', aliased)?.decision).toBe('suggest')
    expect(typedName('pošle Marek Musil ', aliased)?.decision).toBe('chip')
  })
  it('offers an alias with an initial and its dot („A. Svoboda“), never a piece of an e-mail', () => {
    const aliased = indexOf([{ name: 'Adam Svoboda', objectId: '5b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', aliases: ['A. Svoboda'] }])
    expect(found('Zápis pošle A. Svoboda.', aliased)).toEqual([['A. Svoboda', 'suggest']])
    expect(found('a.svoboda@firma.example.test', aliased)).toEqual([])
  })
  it('keeps a short alias to its learned spelling and out of e-mails; a document contact named like an alias never makes it a name', () => {
    const martin = { name: 'Marek Musil', objectId: '2b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', aliases: ['MMu', 'Mára Musil', 'Mára'] }
    const aliased = indexOf([martin], [{ name: 'Mára Musil' }])
    expect(found('MMU, mmu@firma.example.test, jan.mmu, MMu. Mára i MÁRA', aliased).map(([text]) => text)).toEqual(['MMu', 'Mára', 'MÁRA'])
    expect(typedName('pošle Mára Musil ', aliased)).toMatchObject({ decision: 'suggest', people: 1 })
  })
  it('ranks the person pages close in meaning name after the space’s use, saying why (the host’s optional AI boost)', async () => {
    const [a, b, c] = ['a', 'b', 'c'].map(objectId => ({ name: 'Jan Novák', objectId }))
    const usage = async (id: string) => (id === 'b' ? { here: 0, total: 1, related: true } : id === 'c' ? { here: 1, total: 1 } : { here: 0, total: 9 })
    const ranked = await rankPeople({ usage }, [a!, b!, c!], { doc: docOf(), text: '', signal: new AbortController().signal })
    expect(ranked.map(person => person.contact.objectId)).toEqual(['c', 'b', 'a'])
    expect(ranked[1]!.reason).toBe('použit 1× · na podobných stránkách')
    // Without the boost nothing says it: the order is the use in all.
    const plain = await rankPeople({ usage: async id => (id === 'b' ? { here: 0, total: 1 } : { here: 0, total: 9 }) }, [b!, a!], { doc: docOf(), text: '', signal: new AbortController().signal })
    expect(plain.map(person => [person.contact.objectId, person.reason])).toEqual([['a', 'použit 9×'], ['b', 'použit 1×']])
  })
  it('never makes a chip from a truncated lexicon: one match may not be the only one', () => {
    expect(typedName('Volala Jana Nováková ', indexOf([jana], [], true))?.decision).toBe('suggest')
  })
  it('recognises a name typed just now, ended by a space or punctuation', () => {
    expect(typedName('Volala Jana Nováková ')).toEqual({ from: 7, to: 20, decision: 'chip', people: 1 })
    expect(typedName('Volala Jana Nováková,')?.decision).toBe('chip')
    expect(typedName('Volala Jana Novákov')).toBeNull()
    expect(typedName('Volala Jana Nováková a ')).toBeNull()
    expect(recognizeTail(recognitionWindow(`${'x '.repeat(200)}Petr Svoboda `, 413).text, index)?.text).toBe('Petr Svoboda')
  })
})

const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.useRealTimers() })
const paragraph = (text: string) => ({ type: 'paragraph', content: text ? [{ type: 'text', text }] : [] })
function mount(options: ContactsPluginOptions, content: object[] = [paragraph('')]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin(options)],
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!; editor.view.focus()
  return { controller, editor }
}
/** Typed one character at a time, as the keyboard does: each key its own task. */
const type = async (editor: Editor, text: string) => { for (const char of text) { editor.view.dispatch(editor.state.tr.insertText(char)); await Promise.resolve() } }
const key = (editor: Editor, name: string, init: KeyboardEventInit = {}) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true, ...init }))
const contacts = (editor: Editor) => { const found: MarkdownContact[] = []; editor.state.doc.descendants(node => { if (node.type.name === 'contactInline') found.push((node.attrs.data as { contacts: MarkdownContact[] }).contacts[0]!) }); return found }
const ready = async (known: ReturnType<typeof vi.fn>) => { await vi.waitFor(() => expect(known).toHaveBeenCalled()); await new Promise(resolve => setTimeout(resolve, 450)) }

describe('a typed known name', () => {
  it('becomes the chip at the space; Backspace gives back the name without the space, the space again makes the chip again', async () => {
    const known = vi.fn(async () => [jana])
    const link = vi.fn<NonNullable<ContactsPluginOptions['link']>>(async contact => contact)
    const { editor } = mount({ known, link })
    await ready(known)
    await type(editor, 'Volala Jana Nováková ')
    expect(contacts(editor)).toMatchObject([{ name: 'Jana Nováková', objectId: jana.objectId }])
    // The caret after the space; nothing asked of the host (a directory contact is linked already).
    expect(editor.state.selection.from).toBe(editor.state.doc.content.size - 1)
    expect(link).not.toHaveBeenCalled()
    key(editor, 'Backspace')
    expect(editor.getText()).toBe('Volala Jana Nováková')
    expect(contacts(editor)).toEqual([])
    expect(editor.state.selection.from).toBe(editor.state.doc.content.size - 1)
    await type(editor, ' ')
    expect(contacts(editor)).toHaveLength(1)
  })
  it('Ctrl+Z undoes only the chip, not the typing', async () => {
    const known = vi.fn(async () => [jana])
    const { controller, editor } = mount({ known })
    await ready(known)
    await type(editor, 'Jana Nováková,')
    expect(contacts(editor)).toHaveLength(1)
    controller.dispatch({ type: 'undo' })
    expect(contacts(editor)).toEqual([])
    expect(controller.getProjection().text).toBe('Jana Nováková,')
  })
  it('a name two people have becomes an unresolved chip, never linked by itself, and picking one resolves it', async () => {
    const other: MarkdownContact = { name: 'Jana Nováková', organization: 'Example', emails: ['jana@other.example.test'], objectId: '1b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const known = vi.fn(async () => [jana, other])
    const link = vi.fn<NonNullable<ContactsPluginOptions['link']>>(async contact => contact)
    const usage = vi.fn(async (id: string) => (id === other.objectId ? { here: 2, total: 5 } : { here: 0, total: 9 }))
    const { editor } = mount({ known, link, usage })
    await ready(known)
    await type(editor, 'jana nováková ')
    expect(contacts(editor)).toEqual([{ name: 'jana nováková', unresolved: true }])
    const chip = await vi.waitFor(() => { const element = document.querySelector<HTMLElement>('.md-contact-inline [data-chip-state=unresolved]'); expect(element?.textContent).toContain('nejspíš Example'); return element! })
    expect(chip.getAttribute('aria-label')).toBe('jana nováková, nerozhodnutý kontakt')
    expect(chip.textContent).toContain('nebo Firma')
    expect(link).not.toHaveBeenCalled()
    // A click on the selected chip: the people at once (no wait for usage, P17), then the one used in this space first.
    const waiting: (() => void)[] = [], answer = () => waiting.splice(0).forEach(resolve => resolve())
    usage.mockImplementation(async (id: string) => { await new Promise<void>(resolve => waiting.push(resolve)); return id === other.objectId ? { here: 2, total: 5 } : { here: 0, total: 9 } })
    editor.commands.setNodeSelection(1)
    chip.click()
    const rows = () => [...document.querySelectorAll('[data-testid=contact-name-offer] [role=option]')].map(row => row.textContent ?? '')
    await vi.waitFor(() => expect(rows()).toHaveLength(2))
    expect(rows().join()).not.toContain('v tomto prostoru')
    answer()
    await vi.waitFor(() => expect(rows()[0]).toContain('v tomto prostoru'))
    expect(rows()[0]).toContain('Example')
    key(editor, 'Enter')
    await vi.waitFor(() => expect(contacts(editor)).toMatchObject([{ name: 'Jana Nováková', objectId: other.objectId }]))
    expect(contacts(editor)[0]!.unresolved).toBeUndefined()
  })
  it('a click on an unresolved chip nobody of that name is known for opens the host’s person picker with the name', async () => {
    const known = vi.fn(async () => [petr])
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => jana)
    const { editor } = mount({ known, pick })
    editor.commands.insertContent({ type: 'contactInline', attrs: { data: { version: 1, contacts: [{ name: 'Jan Novák', unresolved: true }] } } })
    const chip = await vi.waitFor(() => { const element = document.querySelector<HTMLElement>('.md-contact-inline [data-chip-state=unresolved]'); expect(element).not.toBeNull(); return element! })
    chip.click()
    await vi.waitFor(() => expect(pick).toHaveBeenCalledOnce())
    expect(pick.mock.calls[0]![0]).toMatchObject({ query: 'Jan Novák', inline: true })
    await vi.waitFor(() => expect(contacts(editor)).toMatchObject([{ name: 'Jana Nováková', objectId: jana.objectId }]))
    expect(contacts(editor)[0]!.unresolved).toBeUndefined()
  })
  it('never in code or headings', async () => {
    const known = vi.fn(async () => [jana])
    const { editor } = mount({ known }, [{ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'x' }] }])
    await ready(known)
    editor.commands.setTextSelection(2)
    await type(editor, ' Jana Nováková ')
    expect(contacts(editor)).toEqual([])
    editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Jana Nováková', marks: [{ type: 'code' }] }] }] })
    editor.commands.setTextSelection(14); await type(editor, ' ')
    expect(contacts(editor)).toEqual([])
  })
  it('a chip turns back into the name with Ctrl+Shift+Space later', async () => {
    const known = vi.fn(async () => [jana])
    const { editor } = mount({ known })
    await ready(known)
    await type(editor, 'Jana Nováková a')
    editor.commands.setNodeSelection(1)
    key(editor, ' ', { code: 'Space', ctrlKey: true, shiftKey: true })
    expect(contacts(editor)).toEqual([])
    expect(editor.getText()).toBe('Jana Nováková a')
    // Turned back on purpose: the punctuation typed right after it leaves it text.
    await type(editor, ',')
    expect(contacts(editor)).toEqual([])
    expect(editor.getText()).toBe('Jana Nováková, a')
  })
  it('on a collaborative page Ctrl+Z undoes only the chip, not the typing', async () => {
    const document = new Y.Doc(), known = vi.fn(async () => [jana])
    const contactsPlugin = createContactsPlugin({ known }).create({ profile: 'rich', signal: new AbortController().signal } as never)
    const editor = new Editor({ element: globalThis.document.body.appendChild(globalThis.document.createElement('div')),
      extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document }), CollaborationHistoryBoundary, ChipText, MarkdownRecognition, ...contactsPlugin.extensions!] })
    try {
      yUndoPluginKey.getState(editor.state)!.undoManager.clear()
      editor.commands.focus('end')
      await ready(known)
      await type(editor, 'Ahoj Jana Nováková ')
      expect(contacts(editor)).toHaveLength(1)
      editor.commands.undo()
      expect(contacts(editor)).toEqual([])
      expect(editor.getText()).toBe('Ahoj Jana Nováková ')
    } finally { editor.destroy(); document.destroy() }
  })
  it('on a collaborative page a chip turned back stays text after someone else edits the page', async () => {
    const document = new Y.Doc(), remote = new Y.Doc(), known = vi.fn(async () => [jana])
    const contactsPlugin = createContactsPlugin({ known }).create({ profile: 'rich', signal: new AbortController().signal } as never)
    const editor = new Editor({ element: globalThis.document.body.appendChild(globalThis.document.createElement('div')),
      extensions: [StarterKit.configure({ undoRedo: false }), Collaboration.configure({ document }), CollaborationHistoryBoundary, ChipText, MarkdownRecognition, ...contactsPlugin.extensions!] })
    try {
      editor.commands.focus('end')
      await ready(known)
      await type(editor, 'Jana Nováková a')
      editor.commands.setNodeSelection(1)
      key(editor, ' ', { code: 'Space', ctrlKey: true, shiftKey: true })
      expect(contacts(editor)).toEqual([])
      // Someone else adds a paragraph before it, then one after it: y-tiptap applies each as a replace of the whole document.
      Y.applyUpdate(remote, Y.encodeStateAsUpdate(document))
      const remoteParagraph = (index: number, text: string) => {
        const paragraph = new Y.XmlElement('paragraph'); paragraph.insert(0, [new Y.XmlText(text)])
        remote.getXmlFragment('default').insert(index, [paragraph])
        Y.applyUpdate(document, Y.encodeStateAsUpdate(remote, Y.encodeStateVector(document)))
      }
      remoteParagraph(0, 'Jinde'); remoteParagraph(2, 'Konec')
      expect(editor.getText()).toBe('Jinde\n\nJana Nováková a\n\nKonec')
      editor.commands.setTextSelection(21)
      await type(editor, ',')
      expect(contacts(editor)).toEqual([])
      expect(editor.getText()).toBe('Jinde\n\nJana Nováková, a\n\nKonec')
    } finally { editor.destroy(); document.destroy(); remote.destroy() }
  })
})

describe('the actions menu rows of contacts', () => {
  it('offers the people of a selected known name, ranked, and „Udělat kontakt“', async () => {
    const other: MarkdownContact = { name: 'Jana Nováková', organization: 'Example', title: 'Projektová manažerka', objectId: '1b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const known = vi.fn(async () => [jana, other])
    const plugin = createContactsPlugin({ known, pick: async () => null })
    const { editor } = mount({ known, pick: async () => null }, [paragraph('PM je Jana Nováková')])
    await ready(known)
    const provider = plugin.create({} as never).intentions!
    editor.commands.setTextSelection({ from: 7, to: 20 })
    const rows = await provider({ editor, from: 7, to: 20, empty: false, text: 'Jana Nováková', signal: new AbortController().signal })
    expect(rows.map(row => row.label)).toEqual(['Je to Jana Nováková', 'Je to Jana Nováková', 'Nevím, kdo z nich (nerozhodnutý kontakt)', 'Udělat kontakt'])
    // „PM“ around the name: the project manager first, and why.
    expect(rows[0]!.description).toContain('Example')
    expect(rows[0]!.description).toContain('Projektová manažerka')
    await rows[2]!.run()
    expect(contacts(editor)).toEqual([{ name: 'Jana Nováková', unresolved: true }])
  })
  it('offers the person of an underlined name with the caret in it, also before a comma (found in the browser)', async () => {
    const known = vi.fn(async () => [petr])
    const plugin = createContactsPlugin({ known })
    const { editor } = mount({ known }, [paragraph('Zápis pošle Petr Svoboda, dnes.')])
    await vi.waitFor(() => expect(document.querySelector('.md-recognition-candidate')).not.toBeNull(), { timeout: 2000 })
    editor.commands.setTextSelection(22)
    // The menu's word is „Svoboda,“ (with the comma): the caret, not the word, finds the name.
    const rows = await plugin.create({} as never).intentions!({ editor, from: 18, to: 26, empty: true, text: 'Svoboda,', signal: new AbortController().signal })
    expect(rows.map(row => row.label)).toEqual(['Je to Petr Svoboda', 'Není to on/ona'])
  })
})


describe('names already written', () => {
  const doc = [paragraph('Schůzku vede Jana Nováková a Petr Svoboda.'), { type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Petr Svoboda' }] },
    { type: 'paragraph', content: [{ type: 'text', text: 'Petr Svoboda', marks: [{ type: 'code' }] }] }]
  const underlined = () => [...document.querySelectorAll('.md-recognition-candidate')].map(element => element.textContent)
  it('underlines known names in prose only; Ctrl+. offers them in the actions menu, „Je to …“ links it, „Není to on/ona“ lets it be', async () => {
    const known = vi.fn(async () => [jana, petr])
    const { editor } = mount({ known }, doc)
    await vi.waitFor(() => expect(underlined()).toEqual(['Jana Nováková', 'Petr Svoboda']), { timeout: 2000 })
    // The caret into the second name, then Ctrl+.: the keyboard path is the one actions menu (P2).
    editor.commands.setTextSelection(editor.state.doc.textContent.indexOf('Petr') + 3)
    key(editor, '.', { ctrlKey: true })
    const rows = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(row => row.querySelector('.t')?.textContent)
    await vi.waitFor(() => expect(rows()).toEqual(['Je to Petr Svoboda', 'Není to on/ona']))
    key(editor, 'ArrowDown'); key(editor, 'Enter')
    await vi.waitFor(() => expect(underlined()).toEqual(['Jana Nováková']))
    editor.commands.setTextSelection(editor.state.doc.textContent.indexOf('Jana') + 2)
    key(editor, '.', { ctrlKey: true })
    await vi.waitFor(() => expect(rows()[0]).toBe('Je to Jana Nováková'))
    key(editor, 'Enter')
    await vi.waitFor(() => expect(contacts(editor)).toMatchObject([{ name: 'Jana Nováková' }]))
    expect(editor.getText()).toContain('Schůzku vede ')
  })
  it('underlines a learned alias and offers its person; typing it never makes a chip; the pick is told to the directory', async () => {
    const martin = { name: 'Marek Musil', objectId: '2b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', aliases: ['MMu'] }
    const known = vi.fn(async () => [martin, jana])
    const learn = vi.fn()
    const { editor } = mount({ known, directory: { search: async () => [], get: async () => null, learn } as ContactsPluginOptions['directory'] }, [paragraph('Zápis pošle MMu.'), paragraph('')])
    await vi.waitFor(() => expect(underlined()).toEqual(['MMu']), { timeout: 2000 })
    // Typed at the end: no chip by itself (initials and abbreviations never auto-convert).
    editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    await type(editor, 'Volal MMu ')
    expect(contacts(editor)).toEqual([])
    editor.commands.setTextSelection(14)
    key(editor, '.', { ctrlKey: true })
    const rows = () => [...document.querySelectorAll('[data-testid=intentions] [role=option]')].map(row => row.querySelector('.t')?.textContent)
    await vi.waitFor(() => expect(rows()[0]).toBe('Je to Marek Musil'))
    key(editor, 'Enter')
    await vi.waitFor(() => expect(contacts(editor)).toEqual([{ name: 'Marek Musil', objectId: martin.objectId }]))
    // The snapshot never carries the aliases; the directory hears the pick and the text it was picked for.
    expect(learn).toHaveBeenCalledWith('MMu', { name: 'Marek Musil', objectId: martin.objectId })
  })
  it('does nothing in a read-only editor', async () => {
    const known = vi.fn(async () => [jana])
    const { editor } = mount({ known }, doc)
    editor.setEditable(false)
    await ready(known)
    expect(underlined()).toEqual([])
  })
  it('stays fast on a long document and caps the underlines', async () => {
    const known = vi.fn(async () => [jana, petr])
    const { editor } = mount({ known }, Array.from({ length: 3000 }, (_, i) => paragraph(`Odstavec ${i}: dnes volala Jana Nováková a pak Petr Svoboda kvůli nasazení u zákazníka.`)))
    const started = performance.now()
    expect(recognizeText(editor.state.doc.textBetween(0, editor.state.doc.content.size, ' '), indexOf([jana, petr]))).toHaveLength(6000)
    // The whole document read (3000 paragraphs, 6000 names) in well under a frame budget of a debounced pause.
    expect(performance.now() - started).toBeLessThan(400)
    await vi.waitFor(() => expect(document.querySelectorAll('.md-recognition-candidate')).toHaveLength(200), { timeout: 3000 })
  })
})

describe('a selected name', () => {
  it('„Udělat kontakt“ asks the picker with the selection and puts the person in its place, one undo back', async () => {
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => jana)
    const { controller, editor } = mount({ pick }, [paragraph('Zavolat Jana Nováková zítra')])
    const action = controller.actions.selection.find(item => item.id === 'contacts.from-selection')!
    editor.commands.setTextSelection({ from: 9, to: 23 })
    expect(controller.query(action.command).enabled).toBe(true)
    controller.dispatch(action.command)
    await vi.waitFor(() => expect(contacts(editor)).toHaveLength(1))
    expect(pick.mock.calls[0]![0]).toMatchObject({ query: 'Jana Nováková', inline: true })
    expect(controller.getProjection().text).not.toContain('Nováková zítra')
    controller.dispatch({ type: 'undo' })
    expect(controller.getProjection().text).toBe('Zavolat Jana Nováková zítra')
  })
  it('Ctrl+. offers the same in the actions menu; no name, no action (two lines, too long, nothing selected)', async () => {
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => null)
    const { controller, editor } = mount({ pick }, [paragraph('Jana Nováková'), paragraph('x'.repeat(90))])
    const action = controller.actions.selection.find(item => item.id === 'contacts.from-selection')!
    editor.commands.setTextSelection({ from: 1, to: 14 })
    key(editor, '.', { ctrlKey: true })
    await vi.waitFor(() => expect(document.querySelector('[data-testid=intentions] [role=option][aria-selected=true] .t')?.textContent).toBe('Udělat kontakt'))
    key(editor, 'Enter')
    await vi.waitFor(() => expect(pick).toHaveBeenCalledOnce())
    editor.commands.setTextSelection({ from: 1, to: 18 })
    expect(controller.query(action.command).enabled).toBe(false)
    editor.commands.setTextSelection({ from: 17, to: 106 })
    expect(controller.query(action.command).enabled).toBe(false)
    editor.commands.setTextSelection(3)
    expect(controller.query(action.command).enabled).toBe(false)
  })
})

describe('the ranking before any request', () => {
  it('puts the person the place names most first, from the lexicon’s uses, with why', async () => {
    const { rankNow } = await import('./names')
    const person = (id: string, org: string, uses: number) => ({ key: `object:${id}`, contactId: id, userId: null, name: 'Jana Nováková', aliases: [], org, role: null, uses })
    const lexicon: MarkdownLexicon = { version: '1', truncated: false, pages: [], youTrackProjects: [],
      people: [person('0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', 'Firma', 0), person('1b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b', 'Jinde', 4)] }
    const people = personEntries({}, lexicon, docOf()).map(entry => entry.target as MarkdownContact)
    expect(rankNow(people, { doc: docOf(), text: 'Volala Jana Nováková' }).map(({ contact, reason }) => [contact.organization, reason]))
      .toEqual([['Jinde', contactsStrings.rankInSpace], ['Firma', undefined]])
  })
  it('in a „PM“ place puts the project manager above someone merely on the page (P15)', async () => {
    const { rankNow } = await import('./names')
    const pm: MarkdownContact = { name: 'Jana Nováková', title: 'Projektová manažerka', objectId: '2b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    expect(rankNow([petr, pm], { doc: docOf([petr]), text: 'PM', role: true })[0]!.contact).toBe(pm)
    // Elsewhere the page's own people still lead.
    expect(rankNow([petr, pm], { doc: docOf([petr]), text: 'PM' })[0]!.contact).toBe(petr)
  })
  it('reads an organization with its legal form („Nemocnice Dolnice a.s.: …“)', async () => {
    const { rankNow } = await import('./names')
    const other: MarkdownContact = { name: 'Jakub Bureš', organization: 'Jiná nemocnice', objectId: '6b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const dolnice: MarkdownContact = { name: 'Jakub Bureš', organization: 'Nemocnice Dolnice a.s.', objectId: '7b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    expect(rankNow([other, dolnice], { doc: docOf(), text: 'Nemocnice Dolnice a.s.: Jakub Bureš' })[0]!.contact).toBe(dolnice)
  })
})
