// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { type MarkdownModuleContext, type MarkdownReadonlyJSONContent } from '../../core'
import { createMarkdownContextToolbar } from '../../features/modules/contextToolbar'
import { createContactsRuntime } from './runtime'
import type { ContactsData, MarkdownContact } from './data'

const contexts: AbortController[] = []; const editors: Editor[] = []
function mount(contacts: MarkdownContact[] = [{ name: 'Nový kontakt' }]) {
  const lifetime = new AbortController(); contexts.push(lifetime)
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr>' }); editors.push(editor)
  const container = document.createElement('div'); document.body.append(container)
  const toolbar = createMarkdownContextToolbar({ editor, anchor: container, getPos: () => 0, label: 'Kontakty', signal: lifetime.signal })
  const update = vi.fn()
  editor.commands.setNodeSelection(0); editor.view.focus(); toolbar.refresh()
  const context: MarkdownModuleContext<ContactsData> = { container, data: { version: 1, contacts }, signal: lifetime.signal, document: { type: 'doc' }, editor, update, toolbar }
  return { context, update, lifetime, container, toolbar }
}
const tick = async () => { await Promise.resolve(); await Promise.resolve(); await Promise.resolve(); await Promise.resolve() }
afterEach(() => { contexts.splice(0).forEach(controller => controller.abort()); editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
describe('logo, photo and same names in the chip (package G)', () => {
  const jan = (objectId: string, organization: string, photo?: string): MarkdownContact => ({ name: 'Jan Novák', organization, objectId, ...(photo ? { photo } : {}) })
  const page = (...contacts: MarkdownContact[]) => ({ type: 'doc', content: [{ type: 'paragraph', content: contacts.map(contact => ({ type: 'contactInline', attrs: { data: { version: 1, contacts: [contact] } } })) }] }) as unknown as MarkdownReadonlyJSONContent
  it('shows one lazy picture over the initials and tells two people of one name apart by their organization', async () => {
    const modelsoft = jan('00000000-0000-4000-8000-000000000001', 'Modelsoft', '/api/contacts/00000000-0000-4000-8000-000000000001/photo?v=0123456789abcdef')
    const hospital = jan('00000000-0000-4000-8000-000000000002', 'Nemocnice Dolní Ves')
    const { context, container } = mount([modelsoft])
    context.document = page(modelsoft, hospital)
    await createContactsRuntime({}, true).render(context)
    const images = container.querySelectorAll('img')
    expect(images).toHaveLength(1)
    expect([images[0]!.getAttribute('src'), images[0]!.loading, images[0]!.closest('.md-contact-avatar')?.textContent]).toEqual([modelsoft.photo, 'lazy', 'JN'])
    expect(container.querySelector('.md-chip-detail')?.textContent).toBe('Modelsoft')

    // Alone on the page, the name needs nothing more; without a picture the initials stay.
    const alone = mount([hospital])
    alone.context.document = page(hospital, { ...hospital })
    await createContactsRuntime({}, true).render(alone.context)
    expect([alone.container.querySelector('.md-chip-detail'), alone.container.querySelector('img'), alone.container.querySelector('.md-contact-avatar')?.textContent]).toEqual([null, null, 'JN'])
  })
})
describe('contact contextual management', () => {
  it('automatically resolves internal mentions without writing the document or treating directory IDs as internal', async () => {
    const contact = { name: 'Stored name', emails: ['jana@example.cz'], directoryId: 'address-book-1' }
    const { context, container, update } = mount([contact])
    const resolveInternal = vi.fn(async () => ({ id: 'employee-1', name: 'Jana Nováková' }))
    const cleanup = await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn(), resolveInternal } }, true).render(context)
    await tick()
    expect(container.querySelector('.md-contact-internal')).not.toBeNull()
    expect(container.querySelector('.md-contact-name')!.textContent).toContain('Jana Nováková')
    expect(container.querySelector('.md-contact-download')).toBeNull()
    expect(resolveInternal).toHaveBeenCalledWith(contact, { signal: expect.any(AbortSignal) })
    expect(update).not.toHaveBeenCalled()
    typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()

    const external = mount([{ name: 'Zákazník', directoryId: 'customer-1' }])
    await createContactsRuntime({}, true).render(external.context)
    expect(external.container.querySelector('[data-contact-kind=external]')).not.toBeNull()
  })
  it('a reader card copies a phone or an e-mail, opens the directory and lists where the contact is used', async () => {
    const readerUsage = vi.fn(async () => ({ places: [{ title: 'Zakázka', detail: 'Wiki', url: '#/pages/p1' }], more: 3 }))
    const writeText = vi.fn(async () => {})
    Object.defineProperty(window.navigator, 'clipboard', { value: { writeText }, configurable: true })
    const { context, container } = mount([{ name: 'Jana', objectId: '8e0d3c1e-0000-4000-8000-000000000001', phones: ['+420 123'], emails: ['jana@example.cz'] }])
    delete context.editor; delete context.update
    const cleanup = await createContactsRuntime({ readerUsage, directoryUrl: () => '#/library/contacts' }, true).render(context)
    container.querySelector<HTMLButtonElement>('.md-contact-name')!.click()
    const panel = document.querySelector<HTMLElement>('.md-contact-reader')!
    panel.querySelectorAll<HTMLButtonElement>('.md-contact-copy')[1]!.click()
    expect(writeText).toHaveBeenCalledWith('jana@example.cz')
    expect(panel.querySelector<HTMLAnchorElement>('a[href="#/library/contacts"]')).not.toBeNull()
    await vi.waitFor(() => expect(panel.querySelector('.md-contact-used')!.hasAttribute('hidden')).toBe(false))
    expect(panel.querySelector('.md-contact-used a')!.getAttribute('href')).toBe('#/pages/p1')
    expect(panel.querySelector('.md-contact-used')!.textContent).toContain('a dalších 3')
    typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('a click on a reader block card opens the read-only card; its own links keep their action', async () => {
    const { context, container } = mount([{ name: 'Jana', objectId: '8e0d3c1e-0000-4000-8000-000000000002', emails: ['jana@example.cz'] }])
    delete context.editor; delete context.update
    const cleanup = await createContactsRuntime({}, false).render(context)
    const card = container.querySelector<HTMLElement>('article.md-contact-card')!
    card.querySelector<HTMLAnchorElement>('a')!.addEventListener('click', event => event.preventDefault())
    card.querySelector<HTMLAnchorElement>('a')!.click()
    expect(document.querySelector('.md-contact-reader')).toBeNull()
    card.click()
    expect(document.querySelector('.md-contact-reader .md-contact-copy')).not.toBeNull()
    typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('honors manual internal/external choices and exposes readonly details and vCard outside the sentence', async () => {
    const resolveInternal = vi.fn(async () => ({ id: 'employee-1' }))
    const { context, container } = mount([{ name: '<b>Jana</b>', kind: 'internal', phones: ['+420 123'], emails: ['jana@example.cz'] }])
    delete context.editor; delete context.update
    const cleanup = await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn(), resolveInternal } }, true).render(context)
    expect(resolveInternal).not.toHaveBeenCalled()
    const trigger = container.querySelector<HTMLButtonElement>('.md-contact-name')!
    expect(container.querySelector('a')).toBeNull(); expect(container.querySelector('b')).toBeNull()
    trigger.click()
    const panel = document.querySelector<HTMLElement>('.md-contact-reader')!
    expect(panel.getAttribute('role')).toBe('dialog')
    expect(panel.querySelector<HTMLAnchorElement>('.md-contact-details a')!.href).toBe('tel:+420123')
    expect(panel.querySelector('.md-contact-download')!.getAttribute('aria-label')).toBe('Stáhnout vizitku <b>Jana</b>')
    expect(panel.querySelector('b')).toBeNull()
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(panel.isConnected).toBe(false); expect(document.activeElement).toBe(trigger)
    typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
    const external = mount([{ name: 'Petr', kind: 'external', internalUser: { id: 'employee-2' } }])
    await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn(), resolveInternal } }, true).render(external.context)
    expect(external.container.querySelector('[data-contact-kind=external]')).not.toBeNull()
    expect(resolveInternal).not.toHaveBeenCalled()
  })
  it('aborts and ignores late resolution, preserves snapshots on failure, and closes reader portals on teardown', async () => {
    let finish!: (value: { id: string; name: string }) => void; let signal!: AbortSignal
    const resolveInternal = vi.fn((_contact: Readonly<MarkdownContact>, options: { signal: AbortSignal }) => { signal = options.signal; return new Promise<{ id: string; name: string }>(resolve => { finish = resolve }) })
    const { context, container, update, lifetime } = mount([{ name: 'Jana', internalUser: { id: 'employee-1' } }])
    // A reader's view: a writer's click edits instead (below).
    context.editor!.setEditable(false)
    await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn(), resolveInternal } }, true).render(context); await tick()
    const trigger = container.querySelector<HTMLButtonElement>('.md-contact-name')!
    trigger.click(); expect(document.querySelector('.md-contact-reader')).not.toBeNull()
    lifetime.abort(); expect(signal.aborted).toBe(true); expect(document.querySelector('.md-contact-reader')).toBeNull()
    finish({ id: 'late', name: 'Late result' }); await tick()
    expect(container.textContent).not.toContain('Late result'); expect(update).not.toHaveBeenCalled()
    const fallback = mount([{ name: 'Snapshot', internalUser: { id: 'employee-2' } }])
    await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn(), resolveInternal: async () => { throw new Error('offline') } } }, true).render(fallback.context); await tick()
    expect(fallback.container.querySelector('.md-contact-internal')).not.toBeNull()
  })
  it('persists a manual mode only when the contact form is confirmed and keeps explicit identity snapshots', () => {
    const contact: MarkdownContact = { name: 'Jana', internalUser: { id: 'employee-1' } }
    const { context, container, update } = mount([contact])
    const cleanup = createContactsRuntime({}).edit!({ ...context, anchor: container })
    const select = document.querySelector<HTMLSelectElement>('[name=kind]')!; select.value = 'Externí · vizitka'
    expect(update).not.toHaveBeenCalled()
    select.form!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ ...contact, kind: 'external', phones: [], emails: [] }] })
    cleanup?.()
  })
  it('keeps contact fields compact and reveals optional fields on validation errors without losing their values', () => {
    const { context, container, update } = mount([{ name: 'Jana', phones: ['+420 123 456 789'], website: 'https://example.cz', address: 'Praha' }])
    const cleanup = createContactsRuntime({}).edit!({ ...context, anchor: container })
    const panel = document.querySelector<HTMLElement>('.md-module-form-popover')!
    const optional = panel.querySelector<HTMLDetailsElement>('details')!
    expect(optional.querySelector('summary')?.textContent).toBe('Další údaje'); expect(optional.open).toBe(false)
    expect(panel.querySelector<HTMLTextAreaElement>('[name=phones]')!.rows).toBe(3)
    expect(panel.querySelector<HTMLTextAreaElement>('[name=emails]')!.rows).toBe(3)
    expect(panel.querySelector('[name=title]')!.parentElement!.classList.contains('md-context-field-wide')).toBe(false)
    expect(document.activeElement).toBe(panel.querySelector('[name=name]'))
    const website = panel.querySelector<HTMLInputElement>('[name=website]')!; website.value = 'javascript:invalid'
    const form = panel.querySelector('form')!
    form.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).not.toHaveBeenCalled(); expect(optional.open).toBe(true)
    expect(panel.querySelector('[role=alert]')!.textContent).not.toBe('')
    expect(panel.querySelector<HTMLTextAreaElement>('[name=address]')!.value).toBe('Praha')
    website.value = 'https://updated.example.cz'; optional.open = false
    form.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Jana', phones: ['+420 123 456 789'], emails: [], website: 'https://updated.example.cz', address: 'Praha' }] })
    expect(panel.isConnected).toBe(false); cleanup?.()
  })
  it('keeps directory controls outside the card, searches as one types and ignores superseded searches', async () => {
    const queries: { signal: AbortSignal; resolve: (contacts: MarkdownContact[]) => void }[] = []
    const search = vi.fn((_query: string, { signal }: { signal: AbortSignal }) => new Promise<MarkdownContact[]>(resolve => queries.push({ signal, resolve })))
    const { context, container, update, toolbar } = mount()
    const cleanup = await createContactsRuntime({ directory: { search, get: vi.fn() } }).render(context)
    expect(search).not.toHaveBeenCalled(); expect(container.querySelector('input')).toBeNull()
    expect([...container.querySelectorAll('button')].map(button => button.textContent)).toEqual(['Stáhnout vizitku'])
    toolbar.element.querySelector<HTMLButtonElement>('[aria-label="Přidat z adresáře"]')!.click()
    const panel = document.querySelector('.md-contact-directory')!
    const input = panel.querySelector('input')!
    const type = (text: string) => { input.value = text; input.dispatchEvent(new Event('input')) }
    type('Jana'); await vi.waitFor(() => expect(queries).toHaveLength(1))
    type('Petr'); await vi.waitFor(() => expect(queries).toHaveLength(2))
    expect(queries[0]!.signal.aborted).toBe(true)
    queries[1]!.resolve([{ name: 'Petr', directoryId: 'p' }]); await tick()
    queries[0]!.resolve([{ name: 'Jana' }]); await tick()
    const rows = () => [...panel.querySelectorAll<HTMLElement>('[role=option]')]
    await vi.waitFor(() => expect(rows().map(row => row.textContent)).toEqual(['Petr']))
    // The first row is preselected: Enter takes it (P8).
    expect(rows()[0]!.getAttribute('aria-selected')).toBe('true')
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })); await tick()
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Petr', directoryId: 'p' }] })
    expect(panel.isConnected).toBe(false); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('aborts directory work when its popover closes', async () => {
    let signal!: AbortSignal
    const search = vi.fn((_query: string, options: { signal: AbortSignal }) => { signal = options.signal; return new Promise<MarkdownContact[]>(() => {}) })
    const { context, toolbar } = mount()
    const cleanup = await createContactsRuntime({ directory: { search, get: vi.fn() } }).render(context)
    toolbar.element.querySelector<HTMLButtonElement>('[aria-label="Přidat z adresáře"]')!.click()
    const panel = document.querySelector('.md-contact-directory')!
    const input = panel.querySelector('input')!
    input.value = 'Jana'; input.dispatchEvent(new Event('input')); await vi.waitFor(() => expect(signal).toBeDefined())
    // The first Esc clears the typed text (and its search), the panel stays; the next one closes it (P3).
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(signal.aborted).toBe(true)
    expect(input.value).toBe('')
    expect(panel.isConnected).toBe(true)
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(panel.isConnected).toBe(false); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('retains the stored snapshot until toolbar refresh and aborts late results on teardown', async () => {
    let resolve!: (contact: MarkdownContact) => void; let signal!: AbortSignal
    const get = vi.fn((_id: string, options: { signal: AbortSignal }) => { signal = options.signal; return new Promise<MarkdownContact>(yes => { resolve = yes }) })
    const { context, toolbar, update, lifetime } = mount([{ name: 'Old', directoryId: '42' }])
    const cleanup = await createContactsRuntime({ directory: { search: vi.fn(), get } }).render(context)
    expect(get).not.toHaveBeenCalled()
    toolbar.element.querySelector<HTMLButtonElement>('[aria-label="Aktualizovat z adresáře"]')!.click(); await tick()
    lifetime.abort(); expect(signal.aborted).toBe(true)
    resolve({ name: 'New' }); await tick(); expect(update).not.toHaveBeenCalled(); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('manages list entries in a popover instead of persistent row controls', async () => {
    const { context, container, update } = mount([{ name: 'Jana' }, { name: 'Petr' }])
    const runtime = createContactsRuntime({}); await runtime.render(context)
    expect(container.textContent).not.toContain('Odebrat')
    const cleanup = runtime.edit!({ ...context, anchor: container })
    const panel = document.querySelector('.md-contact-manager')!
    panel.querySelector<HTMLButtonElement>('[aria-label="Odebrat kontakt Petr"]')!.click()
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Jana' }] }); expect(panel.isConnected).toBe(false); cleanup?.()
  })
  it('shows the live contact with the local role, keeps the snapshot when hidden and marks a trashed one', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const snapshot = { name: 'Stará', role: 'PM projektu', objectId: id, phones: ['+420111'] }
    const states: Record<string, unknown> = { ok: { state: 'ok', contact: { name: 'Nová', phones: ['+420222'] } }, hidden: { state: 'hidden' }, trashed: { state: 'trashed' } }
    for (const [state, live] of Object.entries(states)) {
      const { context, container } = mount([snapshot])
      delete context.editor; delete context.update
      let pending: (() => void) | undefined
      await createContactsRuntime({ live: () => new Promise(resolve => { pending = () => resolve(live as never) }) }).render(context)
      expect(container.querySelector('strong')!.textContent).toBe('Stará') // the snapshot is there before the answer
      pending!(); await tick()
      const name = container.querySelector('strong')!.textContent, phone = container.querySelector('a')?.getAttribute('href')
      expect(container.querySelector('.md-contact-role')!.textContent).toBe('PM projektu')
      if (state === 'ok') { expect(name).toBe('Nová'); expect(phone).toBe('tel:+420222') }
      if (state === 'hidden') { expect(name).toBe('Stará'); expect(phone).toBe('tel:+420111'); expect(container.querySelector('.md-contact-unavailable')).toBeNull() }
      if (state === 'trashed') { expect(name).toBe('Stará'); expect(phone).toBeUndefined(); expect(container.querySelector('.md-contact-unavailable')!.textContent).toBe('nedostupný kontakt') }
    }
  })
  it('colours an internal person (the account behind the live contact) on the user colour in the card, the chip and the reader card', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6c', userColor = (user: string) => (user === 'user-1' ? 'rgb(1, 2, 3)' : 'rgb(9, 9, 9)')
    const live = async () => ({ state: 'ok' as const, contact: { name: 'Jana', internalUser: { id: 'user-1', name: 'Jana' } } })
    const block = mount([{ name: 'Jana', objectId: id }]); delete block.context.editor; delete block.context.update
    await createContactsRuntime({ live, userColor }).render(block.context); await tick()
    expect(block.container.querySelector<HTMLElement>('.md-contact-avatar')!.style.backgroundColor).toBe('rgb(1, 2, 3)')
    block.container.querySelector<HTMLElement>('article')!.click()
    expect(document.querySelector<HTMLElement>('.md-contact-reader .md-contact-avatar')!.style.backgroundColor).toBe('rgb(1, 2, 3)')
    const chip = mount([{ name: 'Jana', objectId: id }]); delete chip.context.editor; delete chip.context.update
    await createContactsRuntime({ live, userColor }, true).render(chip.context); await tick()
    expect([chip.container.querySelector('[data-contact-kind]')!.getAttribute('data-contact-kind'), chip.container.querySelector<HTMLElement>('.md-contact-avatar')!.style.backgroundColor]).toEqual(['internal', 'rgb(1, 2, 3)'])
    // The page saying external wins; nobody behind the contact stays plain.
    const external = mount([{ name: 'Jana', objectId: id, kind: 'external' }]); delete external.context.editor; delete external.context.update
    await createContactsRuntime({ live, userColor }).render(external.context); await tick()
    expect(external.container.querySelector<HTMLElement>('.md-contact-avatar')!.style.backgroundColor).toBe('')
  })
  it('links contacts without objectId in an editable document and updates the data once', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const { context, update } = mount([{ name: 'Jana', role: 'PM' }, { name: 'Petr', objectId: id }])
    const link = vi.fn(async (contact: MarkdownContact) => ({ ...contact, objectId: id }))
    await createContactsRuntime({ link }).render(context); await tick()
    expect(link).toHaveBeenCalledTimes(1)
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Jana', role: 'PM', objectId: id }, { name: 'Petr', objectId: id }] })
    const aborted = mount([{ name: 'Eva' }]); let done!: (value: MarkdownContact) => void
    await createContactsRuntime({ link: () => new Promise(resolve => { done = resolve }) }).render(aborted.context)
    aborted.lifetime.abort(); done({ name: 'Eva', objectId: id }); await tick(); expect(aborted.update).not.toHaveBeenCalled()
  })
  it('lets the host veto removing a contact that has objectId', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    for (const answer of [false, true]) {
      const { context, container, update } = mount([{ name: 'Jana', objectId: id }, { name: 'Petr' }])
      const remove = vi.fn(async () => answer)
      const cleanup = createContactsRuntime({ remove }).edit!({ ...context, anchor: container })
      document.querySelector<HTMLButtonElement>('[aria-label="Odebrat kontakt Jana"]')!.click(); await tick()
      expect(remove).toHaveBeenCalled(); expect(update).toHaveBeenCalledTimes(answer ? 1 : 0); cleanup?.()
    }
  })
  it('edits the role in the contact form', () => {
    const { context, container, update } = mount([{ name: 'Jana' }])
    const cleanup = createContactsRuntime({}).edit!({ ...context, anchor: container })
    const role = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=role]')!
    role.value = ' PM '; role.form!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update.mock.calls[0]![0].contacts[0].role).toBe('PM'); cleanup?.()
  })
  it('edit keeps objectId: a linked contact edits only its local role and display and points to the directory', () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const contact: MarkdownContact = { name: 'Jana', phones: ['+420 111'], objectId: id }
    const { context, container, update } = mount([contact])
    const cleanup = createContactsRuntime({ directoryUrl: objectId => `#/contacts/${objectId}` }).edit!({ ...context, anchor: container })
    const panel = document.querySelector<HTMLElement>('.md-module-form-popover')!
    expect([...panel.querySelectorAll<HTMLInputElement>('[name]')].map(input => input.name)).toEqual(['role', 'kind'])
    expect(panel.querySelector<HTMLAnchorElement>('.md-contact-form-note a')!.getAttribute('href')).toBe(`#/contacts/${id}`)
    panel.querySelector<HTMLInputElement>('[name=role]')!.value = 'PM projektu'
    panel.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ ...contact, role: 'PM projektu' }] })
    cleanup?.()
  })
  it('asks the host about a contact added in the form, stores its link and reopens the form when the person goes back', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const { context, container, update } = mount([{ name: 'Jana' }])
    let answer: MarkdownContact | null = null
    const link = vi.fn(async (contact: MarkdownContact, _context: { insert?: unknown }) => answer && { ...contact, objectId: id })
    const submit = (name: string) => { const input = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=name]')!; input.value = name; input.form!.dispatchEvent(new Event('submit', { cancelable: true })) }
    createContactsRuntime({ link }).edit!({ ...context, anchor: container })
    submit('Karel'); await tick()
    expect(link.mock.calls[0]![1].insert).toBeDefined(); expect(update).not.toHaveBeenCalled()
    expect(document.querySelector<HTMLInputElement>('.md-module-form-popover [name=name]')!.value).toBe('Karel')
    answer = { name: 'x' }; submit('Karel'); await tick()
    expect(update.mock.calls[0]![0].contacts[0]).toMatchObject({ name: 'Karel', objectId: id })
  })
  it('dims an unavailable contact, offers no vCard for it, repaints a card only when live data differs and says a failed link quietly', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const inline = mount([{ name: 'Jana', objectId: id }]); delete inline.context.editor; delete inline.context.update
    await createContactsRuntime({ live: async () => ({ state: 'trashed' }) }, true).render(inline.context); await tick()
    expect(inline.container.querySelector('.md-contact-gone')).not.toBeNull()
    inline.container.querySelector<HTMLButtonElement>('.md-contact-name')!.click()
    expect(document.querySelector('.md-contact-reader .md-contact-download')).toBeNull()
    const same = mount([{ name: 'Petr', objectId: id }]); delete same.context.editor; delete same.context.update
    await createContactsRuntime({ live: async () => ({ state: 'ok', contact: { name: 'Petr' } }) }).render(same.context)
    const card = same.container.querySelector('article')!, avatar = card.firstChild; await tick()
    expect(card.firstChild).toBe(avatar); expect(card.classList.contains('md-contact-refreshed')).toBe(false)
    const changed = mount([{ name: 'Petr', objectId: id }]); delete changed.context.editor; delete changed.context.update
    await createContactsRuntime({ live: async () => ({ state: 'ok', contact: { name: 'Petr Nový' } }) }).render(changed.context); await tick()
    expect(changed.container.querySelector('article')!.classList.contains('md-contact-refreshed')).toBe(true)
    const failing = mount([{ name: 'Eva' }])
    await createContactsRuntime({ link: async () => { throw new Error('offline') } }).render(failing.context); await tick()
    expect(failing.container.querySelector('.md-contact-status')!.textContent).toBe('Kontakt se zatím nepodařilo propojit s adresářem.')
    expect(failing.update).not.toHaveBeenCalled()
  })
  it('edits the username in the contact form and shows it labelled on the card', async () => {
    const { context, container, update } = mount([{ name: 'Jana' }])
    const cleanup = createContactsRuntime({}).edit!({ ...context, anchor: container })
    const username = document.querySelector<HTMLInputElement>('.md-module-form-popover [name=username]')!
    username.value = ' jana.n '; username.form!.dispatchEvent(new Event('submit', { cancelable: true }))
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Jana', username: 'jana.n', phones: [], emails: [] }] })
    cleanup?.()
    const readonly = mount([{ name: 'Jana', username: 'jana.n', emails: ['jana@example.cz'] }])
    await createContactsRuntime({}).render(readonly.context)
    expect(readonly.container.querySelector('.md-contact-username')!.textContent).toBe('Uživatelské jméno: jana.n')
    expect(readonly.container.querySelector('.md-contact-username a')).toBeNull()
  })
  it('a resting mouse opens the person card without the focus, leaving closes it, a press keeps it; its QR code unfolds in the card', async () => {
    vi.useFakeTimers()
    try {
      const qrCode = vi.fn(async () => ({ svg: '<svg data-qr></svg>', version: 10 }))
      const { context, container } = mount([{ name: 'Jana Nováková', phones: ['+420 123'] }])
      delete context.editor; delete context.update
      await createContactsRuntime({ qrCode }, true).render(context)
      const trigger = container.querySelector<HTMLButtonElement>('.md-contact-name')!
      const pointer = (target: EventTarget, type: string) => target.dispatchEvent(Object.assign(new Event(type, { bubbles: type === 'pointerdown' }), { pointerType: 'mouse' }))
      pointer(trigger, 'pointerenter'); vi.advanceTimersByTime(400)
      const card = document.querySelector<HTMLElement>('.md-contact-hover')!
      expect(card.contains(document.activeElement)).toBe(false)
      pointer(trigger, 'pointerleave'); vi.advanceTimersByTime(400)
      expect(card.isConnected).toBe(false)
      pointer(trigger, 'pointerenter'); vi.advanceTimersByTime(400)
      const kept = document.querySelector<HTMLElement>('.md-contact-hover')!
      pointer(kept.querySelector('.md-contact-qr-toggle')!, 'pointerdown'); pointer(trigger, 'pointerleave'); vi.advanceTimersByTime(400)
      expect(kept.isConnected).toBe(true)
      const toggle = kept.querySelector<HTMLButtonElement>('.md-contact-qr-toggle')!
      toggle.click(); await vi.runAllTimersAsync()
      expect(toggle.getAttribute('aria-expanded')).toBe('true')
      expect(qrCode).toHaveBeenCalledWith(expect.stringContaining('N:Nováková;Jana;;;'))
      expect(kept.querySelector(`#${toggle.getAttribute('aria-controls')} .md-contact-qr-code svg`)).not.toBeNull()
    } finally { vi.useRealTimers() }
  })
  it('renders readonly telephone and e-mail actions without edit or directory controls', async () => {
    const { context, container } = mount([{ name: '<b>Jana</b>', phones: ['+420 123 456 789'], emails: ['jana@example.cz'] }])
    delete context.editor; delete context.update
    await createContactsRuntime({ directory: { search: vi.fn(), get: vi.fn() } }).render(context)
    expect(container.querySelector('strong')!.textContent).toBe('<b>Jana</b>')
    expect(container.querySelector('strong b')).toBeNull()
    expect(container.querySelector('a')!.href).toBe('tel:+420123456789')
    expect(container.querySelector('input')).toBeNull()
  })
  it('a writer edits a contact by clicking its card or mention: the host edits a linked one, the form an unlinked one', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const linked: MarkdownContact = { name: 'Denis', emails: ['denis@example.cz'], objectId: id }
    const close = vi.fn()
    const editLinked = vi.fn((_contact: MarkdownContact, _context: { anchor: HTMLElement; save: (contact: MarkdownContact) => void }) => close)
    const { context, container, update } = mount([{ name: 'Jana' }, linked])
    await createContactsRuntime({ editLinked }).render(context)
    const cards = container.querySelectorAll<HTMLElement>('.md-contact-card')
    expect(cards[1]!.hasAttribute('data-md-no-select')).toBe(true)
    const mail = cards[1]!.querySelector('a')!; mail.addEventListener('click', event => event.preventDefault()); mail.click()
    expect(editLinked).not.toHaveBeenCalled()
    cards[1]!.querySelector('strong')!.click()
    expect(editLinked).toHaveBeenCalledWith(linked, expect.objectContaining({ anchor: cards[1] }))
    editLinked.mock.calls[0]![1].save({ ...linked, role: 'Správce', username: 'denis' })
    expect(update).not.toHaveBeenCalled(); await new Promise(resolve => setTimeout(resolve))
    expect(update).toHaveBeenCalledWith({ version: 1, contacts: [{ name: 'Jana' }, { ...linked, role: 'Správce', username: 'denis' }] })
    cards[0]!.querySelector('strong')!.click()
    expect(document.querySelector<HTMLInputElement>('.md-module-form-popover [name=emails]')).not.toBeNull()
    expect(editLinked).toHaveBeenCalledTimes(1)

    const inline = mount([linked])
    await createContactsRuntime({ editLinked }, true).render(inline.context)
    inline.container.querySelector<HTMLButtonElement>('.md-contact-name')!.click()
    expect(editLinked).toHaveBeenCalledTimes(2); expect(document.querySelector('.md-contact-reader')).toBeNull()
  })
  it('a reader clicking a card edits nothing', async () => {
    const editLinked = vi.fn(() => () => {})
    const { context, container } = mount([{ name: 'Denis', objectId: '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }])
    context.editor!.setEditable(false)
    await createContactsRuntime({ editLinked }).render(context)
    const card = container.querySelector<HTMLElement>('.md-contact-card')!
    card.click()
    expect(editLinked).not.toHaveBeenCalled(); expect(card.hasAttribute('data-md-editable')).toBe(false)
  })
})
