// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownEditorController } from '../../core'
import { editorForController } from '../../core/internal'
import { createContactsPlugin } from './index'
import type { ContactDirectoryService, MarkdownContact } from './data'

const controllers: MarkdownEditorController[] = []
function mount(directory: ContactDirectoryService, enabled = true) {
  const c = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin({ directory, suggestions: enabled ? { debounceMs: 0 } : false })], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
  controllers.push(c)
  const root = document.createElement('div'); document.body.append(root); c.attachView(root, { ariaLabel: 'Kontakty' })
  return { c, editor: editorForController(c)! }
}
const tick = () => new Promise(resolve => setTimeout(resolve, 25))
afterEach(() => { controllers.splice(0).forEach(c => c.destroy()); document.body.replaceChildren() })
describe('contact typing suggestions', () => {
  it('is opt-in and does not enumerate the directory for an empty query', async () => {
    const directory = { search: vi.fn(async () => []), get: vi.fn(async () => null) }
    const disabled = mount(directory, false); disabled.editor.commands.insertContent('@Jan'); await tick()
    expect(directory.search).not.toHaveBeenCalled()
    const enabled = mount(directory); enabled.editor.commands.insertContent('@'); await tick()
    expect(directory.search).not.toHaveBeenCalled()
    expect(document.querySelector('[role=listbox]')).not.toBeNull()
  })
  it('uses the existing contactInline schema, preserves explicit identity and isolates undo', async () => {
    const person = { name: 'Petr Nový', directoryId: 'directory-1', internalUser: { id: 'employee-7' }, department: 'Vývoj' }
    const directory = { search: vi.fn(async () => [person]), get: vi.fn(async () => null) }
    const { c, editor } = mount(directory)
    editor.commands.insertContent('@Pet'); await tick()
    const option = document.querySelector<HTMLElement>('[role=option]')
    expect(option?.textContent).toContain('Petr Nový')
    option!.click(); await tick()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ type: 'contactInline', attrs: { data: { contacts: [person] } } }] }] })
    expect(c.dispatch({ type: 'undo' })).toBe(true)
    expect(c.getProjection().text).toBe('@Pet')
  })
  it('puts a pick in once the host has linked it, and the typed text back when the person goes back', async () => {
    const id = '0b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b'
    const directory = { search: vi.fn(async () => [{ name: 'Petr Nový' }]), get: vi.fn(async () => null) }
    let answer: 'link' | 'back' = 'back'
    const link = vi.fn(async (contact: MarkdownContact, context: { insert?: { anchor: () => DOMRect } }) => {
      expect(context.insert?.anchor).toBeTypeOf('function')
      return answer === 'link' ? { ...contact, objectId: id } : null
    })
    const c = createMarkdownEditorController({ profile: 'rich', plugins: [createContactsPlugin({ directory, suggestions: { debounceMs: 0 }, link })], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    controllers.push(c)
    const root = document.createElement('div'); document.body.append(root); c.attachView(root, { ariaLabel: 'Kontakty' })
    const editor = editorForController(c)!
    editor.commands.insertContent('@Pet'); await tick()
    document.querySelector<HTMLElement>('[role=option]')!.click(); await tick()
    expect(link).toHaveBeenCalledTimes(1); expect(c.getProjection().text).toBe('@Pet')
    answer = 'link'
    await vi.waitFor(() => expect(document.querySelector('[role=option]')).not.toBeNull())
    document.querySelector<HTMLElement>('[role=option]')!.click(); await tick()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ type: 'contactInline', attrs: { data: { contacts: [{ name: 'Petr Nový', objectId: id }] } } }] }] })
  })
  it('rejects stale answers and aborts when readonly changes during lookup', async () => {
    let resolve!: (value: { name: string }[]) => void
    let signal!: AbortSignal
    const directory = { search: vi.fn((_q: string, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ name: string }[]>(done => { resolve = done }) }), get: vi.fn(async () => null) }
    const { c, editor } = mount(directory); editor.commands.insertContent('@Pet'); await tick()
    c.setEditable(false); await tick()
    expect(signal.aborted).toBe(true)
    resolve([{ name: 'Stará odpověď' }]); await tick()
    expect(document.querySelector('[role=listbox]')).toBeNull()
    expect(c.getProjection().text).toBe('@Pet')
  })
  it('distinguishes duplicate external names and accepts a Czech multiword query', async () => {
    const directory = { search: vi.fn(async (_query: string) => [{ name: 'Jiří Nový', department: 'Obchod' }, { name: 'Jiří Nový', department: 'Vývoj' }]), get: vi.fn(async () => null) }
    const { c, editor } = mount(directory)
    editor.commands.insertContent('@Jiří Nov'); await tick()
    expect(directory.search.mock.calls[0]?.[0]).toBe('Jiří Nov')
    const options = document.querySelectorAll<HTMLElement>('[role=option]'); expect(options).toHaveLength(2)
    options[1]!.click(); await tick()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ attrs: { data: { contacts: [{ name: 'Jiří Nový', department: 'Vývoj' }] } } }] }] })
    expect(JSON.stringify(c.getProjection().rich.document)).not.toContain('internalUser')
  })
  it('rejects out-of-order directory responses and clears old choices before confirming', async () => {
    const pending: Array<{ signal: AbortSignal; done: (items: { name: string }[]) => void }> = []
    const directory = { search: vi.fn((_query: string, { signal }: { signal: AbortSignal }) => new Promise<{ name: string }[]>(done => pending.push({ signal, done }))), get: vi.fn(async () => null) }
    const { c, editor } = mount(directory)
    editor.commands.insertContent('@J'); await vi.waitFor(() => expect(pending).toHaveLength(1))
    editor.commands.insertContent('a'); await vi.waitFor(() => expect(pending).toHaveLength(2))
    expect(pending[0]!.signal.aborted).toBe(true); expect(document.querySelectorAll('[role=option]')).toHaveLength(0)
    pending[1]!.done([{ name: 'Jan Nový' }]); await vi.waitFor(() => expect(document.querySelector('[role=option]')?.textContent).toContain('Jan Nový'))
    pending[0]!.done([{ name: 'Stará odpověď' }]); await tick()
    document.querySelector<HTMLElement>('[role=option]')!.click()
    expect(c.getProjection().text).toContain('Jan Nový'); expect(c.getProjection().text).not.toContain('Stará odpověď')
  })
  it('disposes the old directory before a controller with a new source is mounted', async () => {
    let signal: AbortSignal | undefined, finish: (items: { name: string }[]) => void = () => undefined
    const oldDirectory = { search: vi.fn((_query: string, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ name: string }[]>(done => { finish = done }) }), get: vi.fn(async () => null) }
    const previous = mount(oldDirectory); previous.editor.commands.insertContent('@J'); await vi.waitFor(() => expect(signal).toBeDefined())
    previous.c.destroy(); expect(signal?.aborted).toBe(true)
    const next = mount({ search: vi.fn(async () => [{ name: 'Nový adresář' }]), get: vi.fn(async () => null) })
    next.editor.commands.insertContent('@J'); await vi.waitFor(() => expect(document.querySelector('[role=option]')?.textContent).toContain('Nový adresář'))
    finish([{ name: 'Starý adresář' }]); await tick()
    expect(document.querySelectorAll('[role=listbox]')).toHaveLength(1)
    expect(document.querySelector('[role=option]')?.textContent).toContain('Nový adresář')
  })
  it('tells the directory a pick with the text typed after @, so the host may learn it as the alias', async () => {
    const martin = { name: 'Marek Musil', objectId: '2b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const directory = { search: vi.fn(async () => [martin]), get: vi.fn(async () => null), learn: vi.fn() }
    const { c, editor } = mount(directory)
    editor.commands.insertContent('Volal @MMu'); await tick()
    document.querySelector<HTMLElement>('[role=option]')!.click(); await tick()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ type: 'text' }, { type: 'contactInline', attrs: { data: { contacts: [martin] } } }] }] })
    expect(directory.learn).toHaveBeenCalledWith('MMu', martin)
  })
  it('ranks the people the words before the @ name first, then the ones picked lately, else keeps the directory order', async () => {
    const dev = { name: 'Jana Malá', department: 'Vývoj', objectId: '3b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const pm = { name: 'Jana Nováková', title: 'Projektová manažerka', objectId: '4b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const directory = { search: vi.fn(async () => [dev, pm]), get: vi.fn(async () => null) }
    const names = () => [...document.querySelectorAll('[role=option]')].map(option => option.textContent ?? '')
    const first = mount(directory)
    first.editor.commands.insertContent('PM projektu je @Ja'); await tick()
    expect(names()[0]).toContain('Jana Nováková')
    controllers.splice(controllers.indexOf(first.c), 1)[0]!.destroy(); document.body.replaceChildren()
    const second = mount(directory)
    second.editor.commands.insertContent('Volala @Ja'); await tick()
    expect(names()[0]).toContain('Jana Malá')
    document.querySelectorAll<HTMLElement>('[role=option]')[1]!.click(); await tick()
    second.editor.commands.insertContent(' a @Ja'); await tick()
    // Jana Nováková was picked just now (and is on the page now).
    expect(names()[0]).toContain('Jana Nováková')
  })
  it('lets the context reorder only equally good matches, and a short word before the @ name no role', async () => {
    const martin = { name: 'Marek Musil', objectId: '5b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const eva = { name: 'Eva Marečková', department: 'Marketing', objectId: '6b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const jana = { name: 'Jana Malá', organization: 'Modelsoft', objectId: '7b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    const josef = { name: 'Josef Starý', organization: 'Nemocnice Na Kopečku', objectId: '8b5f3c1e-2d4a-4e8b-9f7a-1c2d3e4f5a6b' }
    // As the host ranks them: the name's beginning, every word of the name, another field.
    const directory = { search: vi.fn(async (query: string) => (query === 'J' ? [jana, josef] : [martin, eva])), get: vi.fn(async () => null) }
    const names = () => [...document.querySelectorAll('[role=option]')].map(option => option.textContent ?? '')
    const first = mount(directory)
    // Eva is on the page already: she still stays under the name that begins as typed (hers only has a word that does).
    first.editor.commands.insertContent([{ type: 'contactInline', attrs: { data: { version: 1, contacts: [eva] } } }, { type: 'text', text: ' a @Mar' }]); await tick()
    expect(names()[0]).toContain('Marek Musil')
    first.editor.commands.insertContent('e'); await tick()
    expect(names()[0]).toContain('Marek Musil')
    controllers.splice(controllers.indexOf(first.c), 1)[0]!.destroy(); document.body.replaceChildren()
    // „na“ is no role: Josef's „Nemocnice Na Kopečku“ does not come first.
    const second = mount(directory)
    second.editor.commands.insertContent('čekáme na @J'); await tick()
    expect(names()[0]).toContain('Jana Malá')
  })
  it.each(['mail@Pet', 'https://example.cz/@Pet', '`@Pet`'])('does not activate within %s', async text => {
    const directory = { search: vi.fn(async () => []), get: vi.fn(async () => null) }
    const { editor } = mount(directory)
    if (text.startsWith('`')) editor.commands.insertContent({ type: 'text', text: '@Pet', marks: [{ type: 'code' }] })
    else editor.commands.insertContent(text)
    await tick(); expect(directory.search).not.toHaveBeenCalled()
  })
})

describe('the `@` list before the directory answers', () => {
  it('shows the host’s cached people at once; the answer replaces them', async () => {
    let answer!: (people: MarkdownContact[]) => void
    const { editor } = mount({ search: () => new Promise<MarkdownContact[]>(done => { answer = done }), get: vi.fn(async () => null),
      cached: query => [{ name: 'Jana Nováková' }].filter(person => person.name.toLowerCase().startsWith(query.toLowerCase())) })
    editor.commands.insertContent('@Ja')
    await Promise.resolve()
    expect([...document.querySelectorAll('[role=option]')].map(option => option.textContent)).toEqual([expect.stringContaining('Jana Nováková')])
    await tick()
    answer([{ name: 'Jana Nováková' }, { name: 'Jan Malý' }])
    await vi.waitFor(() => expect(document.querySelectorAll('[role=option]')).toHaveLength(2))
  })
})
