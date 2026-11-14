// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { contactPlace, createContactsPlugin, isContactPlace } from './index'
import type { ContactsPluginOptions, MarkdownContact } from './data'

// P15 „Struktura z šablon“: a template's place for a person is the unresolved chip with its role; Tab reaches it, Enter fills it.
Range.prototype.getClientRects ??= function () { return [] as unknown as DOMRectList }
Range.prototype.getBoundingClientRect ??= function () { return new DOMRect() }

const pm: MarkdownContact = { name: 'Jana Nováková', title: 'Projektová manažerka', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
const developer: MarkdownContact = { name: 'Adam Malý', title: 'Vývojář', objectId: '1b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
const place = (role: string) => ({ type: 'contactInline', attrs: { data: { version: 1, contacts: [contactPlace(role)] } } })
const text = (value: string) => ({ type: 'text', text: value })
function mount(options: ContactsPluginOptions) {
  const content = [{ type: 'paragraph', content: [text('Úvod')] }, { type: 'paragraph', content: [text('PM: '), place('PM')] },
    { type: 'paragraph', content: [text('Čekáme na: '), place('Čekáme na')] }]
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin(options)],
    source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'contacts', version: 2 }], document: { type: 'doc', content } as never } } })
  controllers.push(controller)
  const host = document.createElement('div'); host.className = 'md-editor'; document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!; editor.commands.setTextSelection(1); editor.view.focus()
  return { controller, editor }
}
const key = (editor: Editor, name: string) => editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true }))
const contacts = (editor: Editor) => { const found: MarkdownContact[] = []; editor.state.doc.descendants(node => { if (node.type.name === 'contactInline') found.push((node.attrs.data as { contacts: MarkdownContact[] }).contacts[0]!) }); return found }
const selected = (editor: Editor) => (editor.state.selection as { node?: { attrs: { data: { contacts: MarkdownContact[] } } } }).node?.attrs.data.contacts[0]

describe('a template place for a person', () => {
  it('is an unresolved contact named by its role, nothing else', () => {
    expect(contactPlace('PM')).toEqual({ name: 'PM', role: 'PM', unresolved: true })
    expect(isContactPlace(contactPlace('Čekáme na'))).toBe(true)
    // An ambiguous name waiting for a pick is not a place; neither is a picked person with a page role.
    expect(isContactPlace({ name: 'Jana Nováková', unresolved: true })).toBe(false)
    expect(isContactPlace({ name: 'PM', role: 'PM' })).toBe(false)
  })
  it('shows as the unresolved chip with its role, Tab goes from place to place, Enter offers the project managers first and fills it', async () => {
    const known = vi.fn(async () => [developer, pm])
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => developer)
    const { editor } = mount({ known, pick })
    const chips = await vi.waitFor(() => { const found = [...document.querySelectorAll<HTMLElement>('.md-contact-inline [data-chip-state=unresolved]')]; expect(found).toHaveLength(2); return found })
    expect(chips[0]!.getAttribute('aria-label')).toBe('PM, prázdné místo pro osobu')
    expect(chips[0]!.title).toContain('Tab přejde na další místo')

    key(editor, 'Tab')
    expect(selected(editor)).toEqual(contactPlace('PM'))
    key(editor, 'Tab')
    expect(selected(editor)).toEqual(contactPlace('Čekáme na'))

    editor.commands.setTextSelection(1)
    key(editor, 'Tab')
    key(editor, 'Enter')
    const rows = () => [...document.querySelectorAll('[data-testid=contact-name-offer] [role=option]')].map(row => row.textContent ?? '')
    await vi.waitFor(() => expect(rows()).toHaveLength(3))
    expect(rows()[0]).toContain('Jana Nováková')
    expect(rows()[2]).toContain('Někdo jiný…')
    key(editor, 'Enter')
    // The person, linked, keeps the place's role; it is the contact chip now, not free text.
    await vi.waitFor(() => expect(contacts(editor)[0]).toMatchObject({ name: 'Jana Nováková', objectId: pm.objectId, role: 'PM' }))
    expect(contacts(editor)[0]!.unresolved).toBeUndefined()
    expect(pick).not.toHaveBeenCalled()
  })
  it('„Někdo jiný…“ opens the host’s picker with an empty search, and the pick fills the place', async () => {
    const known = vi.fn(async () => [pm])
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => developer)
    const { editor } = mount({ known, pick })
    await vi.waitFor(() => expect(document.querySelectorAll('.md-contact-inline [data-chip-state=unresolved]')).toHaveLength(2))
    key(editor, 'Tab'); key(editor, 'Tab')
    key(editor, 'Enter')
    await vi.waitFor(() => expect(document.querySelectorAll('[data-testid=contact-name-offer] [role=option]')).toHaveLength(2))
    key(editor, 'ArrowDown'); key(editor, 'Enter')
    await vi.waitFor(() => expect(pick).toHaveBeenCalledOnce())
    expect(pick.mock.calls[0]![0]).toMatchObject({ query: '', inline: true })
    await vi.waitFor(() => expect(contacts(editor)[1]).toMatchObject({ name: 'Adam Malý', role: 'Čekáme na' }))
  })
})
