// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { configureMarkdownEditorView } from '../../vue/controllerView'
import { createContactsPlugin, type ContactsPluginOptions } from './index'

// `/kontakt jan kovarik`: the slash text flows into the host's person picker (Atlas's contact search), or without one into
// the `@` people suggestion of the directory, so the person is found by typing on.
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
const empty = { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } as const

function mount(options: ContactsPluginOptions) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin(options)], source: { kind: 'local', value: empty } })
  controllers.push(controller)
  configureMarkdownEditorView(controller, { compact: false })
  const host = document.createElement('div'); document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Editor' })
  const editor = editorForController(controller)!; editor.view.focus()
  return { controller, editor }
}
const rows = () => [...document.querySelectorAll('[data-testid=slash-menu] [role=option]')].map(row => row.querySelector('.t')?.textContent ?? '')
async function slash(editor: Editor, text: string) {
  editor.commands.insertContent(text)
  await vi.waitFor(() => expect(rows().length).toBeGreaterThan(0))
  editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
}
const nodes = (editor: Editor) => { const found: { type: string; data?: unknown }[] = []; editor.state.doc.descendants(node => { found.push({ type: node.type.name, data: node.attrs.data }) }); return found }

describe('contact picking from the slash menu', () => {
  it('puts several picked people in as one card each, one after another', async () => {
    const { editor } = mount({ pick: async () => [{ name: 'Jan' }, { name: 'Eva' }] })
    await slash(editor, '/kontakt jan')
    await vi.waitFor(() => expect(nodes(editor).filter(node => node.type === 'contactCard')).toHaveLength(2))
    expect(nodes(editor).filter(node => node.type === 'contactCard').map(node => node.data)).toEqual([{ version: 1, contacts: [{ name: 'Jan' }] }, { version: 1, contacts: [{ name: 'Eva' }] }])
  })
  it('„Přidat kontakt“ of a card puts the new contact in as the next block, not into the card', async () => {
    const { editor } = mount({})
    editor.commands.setContent({ type: 'doc', content: [{ type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jan' }] } } }, { type: 'paragraph' }] })
    editor.commands.setNodeSelection(0); editor.view.focus()
    const add = await vi.waitFor(() => { const button = document.querySelector<HTMLButtonElement>('.md-context-toolbar button[aria-label="Přidat kontakt"]'); expect(button).not.toBeNull(); return button! })
    add.click()
    const name = await vi.waitFor(() => { const input = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=name]'); expect(input).not.toBeNull(); return input! })
    name.value = 'Eva'; name.form!.dispatchEvent(new Event('submit', { cancelable: true }))
    await vi.waitFor(() => expect(nodes(editor).filter(node => node.type === 'contactCard')).toHaveLength(2))
    expect(nodes(editor).filter(node => node.type === 'contactCard').map(node => node.data)).toMatchObject([{ version: 1, contacts: [{ name: 'Jan' }] }, { version: 1, contacts: [{ name: 'Eva' }] }])
  })
  it('„Přidat kontakt“ in a host whose block takes no sibling (data-md-single-block) adds to the card', async () => {
    const { editor } = mount({})
    editor.view.dom.parentElement!.setAttribute('data-md-single-block', '')
    editor.commands.setContent({ type: 'doc', content: [{ type: 'contactCard', attrs: { data: { version: 1, contacts: [{ name: 'Jan' }] } } }, { type: 'paragraph' }] })
    editor.commands.setNodeSelection(0); editor.view.focus()
    const add = await vi.waitFor(() => { const button = document.querySelector<HTMLButtonElement>('.md-context-toolbar button[aria-label="Přidat kontakt"]'); expect(button).not.toBeNull(); return button! })
    add.click()
    const name = await vi.waitFor(() => { const input = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=name]'); expect(input).not.toBeNull(); return input! })
    name.value = 'Eva'; name.form!.dispatchEvent(new Event('submit', { cancelable: true }))
    await vi.waitFor(() => expect(nodes(editor).find(node => node.type === 'contactCard')?.data).toMatchObject({ contacts: [{ name: 'Jan' }, { name: 'Eva' }] }))
    expect(nodes(editor).filter(node => node.type === 'contactCard')).toHaveLength(1)
  })
  it('hands the typed text to the host picker and inserts the picked person as a card on an empty line', async () => {
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => ({ name: 'Jan Kovařík', emails: ['jan@example.cz'] }))
    const { editor } = mount({ pick })
    await slash(editor, '/kontakt jan kovarik')
    await vi.waitFor(() => expect(pick).toHaveBeenCalledOnce())
    expect(pick.mock.calls[0]![0]).toMatchObject({ query: 'jan kovarik', inline: false })
    await vi.waitFor(() => expect(nodes(editor).find(node => node.type === 'contactCard')?.data).toMatchObject({ contacts: [{ name: 'Jan Kovařík' }] }))
    expect(editor.getText()).not.toContain('/kontakt')
  })
  it('inserts the person in the text where the slash was typed inside a sentence', async () => {
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => ({ name: 'Jana' }))
    const { editor } = mount({ pick })
    editor.commands.insertContent('Volejte ')
    await slash(editor, '/kontakt jana')
    await vi.waitFor(() => expect(nodes(editor).some(node => node.type === 'contactInline')).toBe(true))
    expect(pick.mock.calls[0]![0]).toMatchObject({ query: 'jana', inline: true })
    expect(editor.state.doc.textContent.startsWith('Volejte ')).toBe(true)
  })
  it('opens the picker with an empty query for a bare /kontakt and leaves the document alone when it is cancelled', async () => {
    const pick = vi.fn<NonNullable<ContactsPluginOptions['pick']>>(async () => null)
    const { editor } = mount({ pick })
    await slash(editor, '/kontakt')
    await vi.waitFor(() => expect(pick).toHaveBeenCalledOnce())
    expect(pick.mock.calls[0]![0].query).toBe('')
    await Promise.resolve()
    expect(nodes(editor).some(node => node.type.startsWith('contact'))).toBe(false)
    expect(editor.getText()).toBe('')
  })
  it('without a host picker continues as the @ people search with the typed text', async () => {
    const search = vi.fn(async () => [{ name: 'Jan Kovařík' }])
    const { editor } = mount({ directory: { search, get: async () => null }, suggestions: { debounceMs: 0 } })
    await slash(editor, '/kontakt jan')
    expect(editor.getText()).toBe('@jan')
    await vi.waitFor(() => expect(search).toHaveBeenCalledWith('jan', expect.anything()))
  })
})
