// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type LocalMarkdownEditorController, type MarkdownRichValue } from '../../core'
import { contactNode, createContactsPlugin, estimateContactsHeight, inlineContactNode, insertContact } from './index'

const controllers: LocalMarkdownEditorController<'rich'>[] = []
function mount(value: MarkdownRichValue, options?: Parameters<typeof createContactsPlugin>[0]) {
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin(options)], source: { kind: 'local', value } }); controllers.push(controller)
  const container = document.createElement('div'); document.body.append(container); controller.attachView(container, { ariaLabel: 'Kontakty' })
  return controller
}
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })
describe('contacts public plugin contract', () => {
  it('roundtrips both inline and list contacts with a single versioned plugin and readable Markdown', () => {
    const document = { type: 'doc', content: [contactNode([{ name: 'Jana', phones: ['+420 123'] }, { name: 'Petr', emails: ['petr@example.cz'] }]), { type: 'paragraph', content: [{ type: 'text', text: 'Kontakt: ' }, inlineContactNode({ name: 'Anna' })] }] }
    const controller = mount({ format: 'atlas-rich', version: 1, plugins: [{ id: 'contacts', version: 1 }], document })
    const projection = controller.getProjection()
    expect(projection.rich.plugins).toContainEqual({ id: 'contacts', version: 2 })
    expect(projection.markdownLossy).toBe(false)
    // One contact per block: a stored card of two opens as two (repairLegacyDocument).
    expect(projection.markdown).toBe('```a-contact\n- name: Jana\n  phones: ["+420 123"]\n```\n\n```a-contact\n- name: Petr\n  emails: [petr@example.cz]\n```\n\nKontakt: [Anna](a-contact:?name=Anna)')
    expect(mount(mount(projection.rich).getValue()).getProjection().rich).toEqual(projection.rich)
  })
  it('preserves explicit internal identity and manual presentation through rich roundtrips', () => {
    const contact = { name: 'Jana', kind: 'external' as const, directoryId: 'customer-4', internalUser: { id: 'employee-8', name: 'Jana Nová' } }
    const controller = mount({ format: 'atlas-rich', version: 1, plugins: [{ id: 'contacts', version: 2 }], document: { type: 'doc', content: [{ type: 'paragraph', content: [inlineContactNode(contact)] }] } })
    expect(controller.getProjection().rich.document).toMatchObject({ content: [{ content: [{ attrs: { data: { version: 1, contacts: [contact] } } }] }] })
    expect(mount(controller.getProjection().rich).getValue()).toEqual(controller.getValue())
  })
  it('inserts through the public command and keeps ordinary undo semantics', () => {
    const controller = mount({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } })
    expect(controller.dispatch(insertContact({ name: 'Jan' }))).toBe(true)
    expect(controller.getProjection().text).toContain('Jan')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getProjection().text).not.toContain('Jan')
  })
  it('follows a merged contact to its target in the document, outside undo', async () => {
    const merged = '11111111-1111-4111-8111-111111111111', target = '22222222-2222-4222-8222-222222222222'
    const controller = mount({ format: 'atlas-rich', version: 1, plugins: [{ id: 'contacts', version: 2 }], document: { type: 'doc', content: [
      contactNode([{ name: 'Petr', objectId: target }, { name: 'Jana', objectId: merged, role: 'PM' }]), { type: 'paragraph', content: [inlineContactNode({ name: 'Jana', objectId: merged })] },
    ] } }, { live: async id => ({ state: 'ok', contact: { name: 'Jana', objectId: id === merged ? target : id } }) })
    const ids = () => JSON.stringify(controller.getProjection().rich.document).match(/[0-9a-f]{8}-1111|[0-9a-f]{8}-2222/g)
    // The runtime loads lazily: wait for the follow, not for a fixed time.
    await vi.waitFor(() => expect(ids()).toEqual(['22222222-2222', '22222222-2222', '22222222-2222']), { timeout: 5000 })
    expect(JSON.stringify(controller.getProjection().rich.document)).toContain('"role":"PM"')
    controller.dispatch({ type: 'undo' })
    expect(ids()).toEqual(['22222222-2222', '22222222-2222', '22222222-2222'])
  })
  it('refuses contacts in markdown-safe', () => {
    expect(() => createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: '' }, plugins: [createContactsPlugin()] })).toThrow(/does not support/)
  })
})

describe('the stand-in of a contact card (J8.1)', () => {
  it('is about as tall as the drawn card: the heights measured in Chromium at 1440 px', () => {
    const near = (height: number, drawn: number) => expect(Math.abs(height - drawn)).toBeLessThan(4)
    near(estimateContactsHeight({ version: 1, contacts: [{ name: 'Jan Novák' }] }), 62)
    near(estimateContactsHeight({ version: 1, contacts: [{ name: 'Jan Novák', organization: 'Dodavatel', emails: ['a@b.cz'] }] }), 87)
    near(estimateContactsHeight({ version: 1, contacts: [{ name: 'Jan Novák', organization: 'Dodavatel', title: 'Správce', role: 'Kontakt pro VPN', emails: ['a@b.cz'] }] }), 105)
    // A role that only repeats the position is not a second line.
    near(estimateContactsHeight({ version: 1, contacts: [{ name: 'Jan Novák', organization: 'Dodavatel', title: 'Správce', role: 'správce ', emails: ['a@b.cz'] }] }), 87)
  })
})
