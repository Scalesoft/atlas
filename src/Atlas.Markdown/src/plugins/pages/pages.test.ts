// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Extension } from '@tiptap/core'
import { Plugin } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../../core'
import { editorForController } from '../../core/internal'
import { createOutlineHighlight } from '../../ui/outline'
import { createPagesPlugin, insertPageReference, pageReferenceNode, normalizePageReference, pageSectionSpec } from './index'
const controllers: MarkdownEditorController[] = []
function mount(plugins = [createPagesPlugin()]) {
  const c = createMarkdownEditorController({ profile: 'rich', plugins, source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
  controllers.push(c); const el = document.createElement('div'); document.body.append(el); c.attachView(el, { ariaLabel: 'Stránky' }); return c
}
afterEach(() => { controllers.splice(0).forEach(c => c.destroy()); document.body.replaceChildren() })
describe('wiki page references', () => {
  it('exposes saved page references without requiring a host source', () => {
    const instance = createPagesPlugin().create({ profile: 'rich', signal: new AbortController().signal })
    const provider = instance.references?.[0]
    expect(provider).toBeDefined()
    expect(provider!.nodeType).toBe('pageReference')
    expect(provider!.read(Object.freeze({ type: 'pageReference', attrs: Object.freeze({ id: 'wiki-42', title: 'Příručka [týmu]', url: '/wiki/prirucka' }) }))).toEqual({ url: '/wiki/prirucka', label: 'Příručka [týmu]' })
    expect(provider!.read({ type: 'paragraph' })).toBeNull()
    expect(provider!.matchUrl).toBeUndefined()
    expect(provider!.resolveUrl).toBeUndefined()
    const withoutResolver = createPagesPlugin({ source: { search: async () => [], matchUrl: () => true } }).create({ profile: 'rich', signal: new AbortController().signal }).references![0]!
    expect(withoutResolver.matchUrl).toBeUndefined()
  })

  it('offers its page search to the link field through the editor (UX WR3-3)', async () => {
    const search = vi.fn(async () => [{ id: 'wiki-42', title: ' Modul ', url: '/wiki/modul', detail: 'Provoz' }, { id: 'wiki-43', title: 'Web', url: '/wiki/web' }])
    const c = mount([createPagesPlugin({ source: { search } })])
    const storage = editorForController(c)!.storage as unknown as { markdownReferences: { providers: { search?: (query: string, context: { signal: AbortSignal }) => Promise<unknown> }[] } }
    const signal = new AbortController().signal
    expect(await storage.markdownReferences.providers[0]!.search!('mod', { signal })).toEqual([
      { url: '/wiki/modul', label: 'Modul', detail: 'Provoz' }, { url: '/wiki/web', label: 'Web' }])
    expect(search).toHaveBeenCalledWith('mod', { signal })
    expect(createPagesPlugin().create({ profile: 'rich', signal }).references![0]!.search).toBeUndefined()
  })

  it('recognizes only host-matched URLs without requests and explicitly resolves normalized page content', async () => {
    const search = vi.fn(async () => [])
    const resolve = vi.fn(async () => null)
    const resolveUrl = vi.fn(async (_url: string, _context: { readonly signal: AbortSignal }) => ({ id: ' wiki-42 ', title: ' Příručka [týmu] ', url: ' /wiki/prirucka ', detail: 'Only in search' }))
    const matchUrl = (url: string) => new URL(url).origin === 'https://wiki.example' && new URL(url).pathname.startsWith('/pages/')
    const provider = createPagesPlugin({ source: { search, resolve, matchUrl, resolveUrl } }).create({ profile: 'rich', signal: new AbortController().signal }).references?.[0]
    expect(provider).toBeDefined()
    expect(provider!.matchUrl!('https://wiki.example/pages/wiki-42')).toBe(true)
    expect(provider!.matchUrl!('https://other.example/pages/wiki-42')).toBe(false)
    expect(provider!.read({ type: 'pageReference', attrs: { id: 'wiki-42', title: 'Příručka [týmu]', url: '/wiki/prirucka' } })).toEqual({ url: '/wiki/prirucka', label: 'Příručka [týmu]' })
    expect(search).not.toHaveBeenCalled()
    expect(resolve).not.toHaveBeenCalled()
    expect(resolveUrl).not.toHaveBeenCalled()
    const signal = new AbortController().signal
    expect(await provider!.resolveUrl!('https://wiki.example/pages/wiki-42', { signal })).toEqual({ type: 'pageReference', attrs: { id: 'wiki-42', title: 'Příručka [týmu]', url: '/wiki/prirucka' } })
    expect(resolveUrl).toHaveBeenCalledWith('https://wiki.example/pages/wiki-42', { signal })
    expect(resolve).not.toHaveBeenCalled()
  })

  it('preserves unavailable resolution and rejects unsafe resolved page data', async () => {
    const resolveUrl = vi.fn(async (url: string) => url.endsWith('/wiki-42') ? { id: 'wiki-42', title: 'Příručka', url: 'javascript:alert(1)' } : null)
    const provider = createPagesPlugin({ source: { search: async () => [], matchUrl: () => true, resolveUrl } }).create({ profile: 'rich', signal: new AbortController().signal }).references?.[0]
    expect(provider).toBeDefined()
    const context = { signal: new AbortController().signal }
    expect(await provider!.resolveUrl!('https://wiki.example/pages/missing', context)).toBeNull()
    await expect(provider!.resolveUrl!('https://wiki.example/pages/wiki-42', context)).rejects.toThrow()
  })

  it('preserves stable identity and snapshot with an explicitly lossy Markdown link', () => {
    const c = mount(); const page = { id: 'wiki-42', title: 'Příručka [týmu]', url: '/wiki/prirucka' }
    expect(c.dispatch(insertPageReference(page))).toBe(true)
    const projection = c.getProjection()
    expect(projection.rich.plugins).toContainEqual({ id: 'pages', version: 1 })
    expect(projection.rich.document).toMatchObject({ content: [{ content: [pageReferenceNode(page)] }] })
    expect(projection.markdown).toContain('/wiki/prirucka')
    expect(projection.markdownLossy).toBe(true)
    const reopened = createMarkdownEditorController({ profile: 'rich', plugins: [createPagesPlugin()], source: { kind: 'local', value: projection.rich } })
    controllers.push(reopened)
    const root = document.createElement('div'); document.body.append(root)
    expect(() => reopened.attachView(root, { ariaLabel: 'Znovuotevřená wiki' })).not.toThrow()
    expect(reopened.getProjection().rich).toEqual(projection.rich)
    expect(c.dispatch({ type: 'undo' })).toBe(true)
    expect(c.getProjection().text).toBe('')
  })
  it('does not offer a picker without a host page source', () => {
    const c = mount(); expect(c.actions.slash).toEqual([])
  })
  it('resolves moved/renamed pages only in the view and preserves unavailable snapshots', async () => {
    const resolve = vi.fn(async () => ({ id: 'wiki-42', title: 'Nový název', url: '/wiki/moved' }))
    const c = mount([createPagesPlugin({ source: { search: async () => [], resolve } })])
    c.dispatch(insertPageReference({ id: 'wiki-42', title: 'Původní název', url: '/wiki/old' }))
    await new Promise(r => setTimeout(r, 20))
    expect(document.querySelector('[data-page-reference]')?.textContent).toBe('Nový název')
    expect(c.getProjection().text).toContain('Původní název')
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ attrs: { title: 'Původní název', id: 'wiki-42' } }] }] })
  })
  it('rejects unsafe URLs and empty identities before insertion', () => {
    expect(() => normalizePageReference({ id: '', title: 'X', url: '/wiki' })).toThrow()
    expect(() => normalizePageReference({ id: 'x', title: 'X', url: 'javascript:alert(1)' })).toThrow()
  })
  it('keeps unavailable pages readable and prevents their navigation callback in readonly', async () => {
    const navigate = vi.fn()
    const c = mount([createPagesPlugin({ source: { search: async () => [], resolve: async () => null }, navigate })])
    c.dispatch(insertPageReference({ id: 'missing', title: 'Uložený název', url: '/wiki/missing' })); c.setEditable(false)
    const link = document.querySelector<HTMLAnchorElement>('[data-page-reference]')!
    await vi.waitFor(() => expect(link.getAttribute('aria-disabled')).toBe('true'))
    expect(link.textContent).toBe('Uložený název'); expect(link.hasAttribute('href')).toBe(false)
    link.click(); expect(navigate).not.toHaveBeenCalled()
    expect(c.getProjection().text).toBe('Uložený název')
  })
  it('does not leave slash-only typing enabled when the search command cannot start in code', async () => {
    const search = vi.fn(async () => [])
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 0 })]), editor = editorForController(c)!
    editor.commands.setCodeBlock()
    const action = c.actions.slash[0]!
    expect(c.dispatch(action.command!)).toBe(false)
    expect(editor.state.doc.textContent).toBe('')
    editor.commands.setParagraph(); editor.commands.insertContent('[[Manual')
    await new Promise(resolve => setTimeout(resolve, 20))
    expect(search).not.toHaveBeenCalled(); expect(document.querySelector('[role=listbox]')).toBeNull()
  })
  it('keeps an open debounced page picker alive when outline navigation mounts and unmounts', async () => {
    const search = vi.fn(async () => [{ id: 'vpn', title: 'VPN guide', url: '/vpn' }])
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 20 })]), editor = editorForController(c)!
    expect(c.dispatch(c.actions.slash[0]!.parameter!.command('vpn')!)).toBe(true)
    const highlight = createOutlineHighlight(editor)
    try {
      await vi.waitFor(() => expect(document.querySelector('[role=option]')?.textContent).toContain('VPN guide'))
      expect(search).toHaveBeenCalledOnce()
      highlight.destroy()
      document.querySelector<HTMLElement>('[role=option]')!.click()
      expect(c.getProjection().text).toBe('VPN guide')
    } finally { highlight.destroy() }
  })
  it('opens the explicit picker immediately after text without enabling general wiki typing', async () => {
    const search = vi.fn(async () => [{ id: 'a', title: 'Guide', url: '/guide' }])
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 0 })]), editor = editorForController(c)!
    editor.commands.insertContent('Přečtěte')
    expect(c.dispatch(c.actions.slash[0]!.command!)).toBe(true)
    editor.commands.insertContent('Guide')
    await vi.waitFor(() => expect(document.querySelector('[role=option]')?.textContent).toContain('Guide'))
    document.querySelector<HTMLElement>('[role=option]')!.click()
    expect(c.getProjection().text).toBe('Přečtěte Guide')
  })
  it('resets slash-only typing after Escape and aborts the pending page search', async () => {
    let signal: AbortSignal | undefined, resolve: (items: { id: string; title: string; url: string }[]) => void = () => undefined
    const search = vi.fn((_query: string, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ id: string; title: string; url: string }[]>(done => { resolve = done }) })
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 0 })]), editor = editorForController(c)!
    expect(c.dispatch(c.actions.slash[0]!.command!)).toBe(true)
    editor.commands.insertContent('Wiki')
    await vi.waitFor(() => expect(signal).toBeDefined())
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(signal?.aborted).toBe(true)
    resolve([{ id: 'old', title: 'Old result', url: '/old' }]); await Promise.resolve()
    editor.commands.insertContent(' [[Manual'); await new Promise(done => setTimeout(done, 20))
    expect(search).toHaveBeenCalledOnce(); expect(document.querySelector('[role=listbox]')).toBeNull()
  })
  it('cancels page resolution on removed references and ignores the old answer', async () => {
    let signal: AbortSignal | undefined, done: (value: { id: string; title: string; url: string }) => void = () => undefined
    const resolve = vi.fn((_id: string, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<{ id: string; title: string; url: string }>(finish => { done = finish }) })
    const c = mount([createPagesPlugin({ source: { search: async () => [], resolve } })])
    c.dispatch(insertPageReference({ id: 'old', title: 'Snapshot', url: '/old' }))
    await vi.waitFor(() => expect(signal).toBeDefined())
    c.dispatch({ type: 'undo' }); expect(signal?.aborted).toBe(true)
    done({ id: 'old', title: 'Late rename', url: '/moved' }); await Promise.resolve()
    expect(document.querySelector('[data-page-reference]')).toBeNull(); expect(c.getProjection().text).toBe('')
  })
  it('shows and opens a section link as a decoration tells it, and leaves a plain page link as it was', async () => {
    let title = 'Sekce teď'
    const open = vi.fn(), resolve = vi.fn(async (id: string) => ({ id, title: 'Název stránky', url: `/moved/${id}` }))
    // A host plugin's decoration: the reference whose url names a section is one.
    const sections: MarkdownPlugin = { id: 'sections-test', apiVersion: 1, create: () => ({ extensions: [Extension.create({ name: 'sectionsTest', addProseMirrorPlugins: () => [new Plugin({
      props: { decorations: state => { const found: Decoration[] = []; state.doc.descendants((node, pos) => {
        if (node.type.name === 'pageReference' && String(node.attrs.url).includes('heading=')) found.push(Decoration.node(pos, pos + 1, {}, { [pageSectionSpec]: { title, open } }))
      }); return DecorationSet.create(state.doc, found) } } })] })] }) }
    const c = mount([createPagesPlugin({ source: { search: async () => [], resolve } }), sections]), editor = editorForController(c)!
    c.dispatch(insertPageReference({ id: 'p1', title: 'Uložená sekce', url: '#/pages/p1?heading=a' }))
    c.dispatch(insertPageReference({ id: 'p2', title: 'Uložená stránka', url: '/p2' }))
    const [section, plain] = [...document.querySelectorAll<HTMLAnchorElement>('[data-page-reference]')]
    expect(section!.textContent).toBe('Sekce teď')
    await vi.waitFor(() => expect(plain!.textContent).toBe('Název stránky'))
    expect(section!.textContent).toBe('Sekce teď'); expect(section!.getAttribute('href')).toBe('#/pages/p1?heading=a'); expect(plain!.getAttribute('href')).toBe('/moved/p2')
    title = 'Sekce přejmenovaná'; editor.view.dispatch(editor.state.tr.insertText('x', 1))
    expect(section!.textContent).toBe('Sekce přejmenovaná')
    c.setEditable(false); section!.click()
    expect(open).toHaveBeenCalledOnce()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{}, { attrs: { title: 'Uložená sekce' } }, { attrs: { title: 'Uložená stránka' } }] }] })
  })
})
describe('page suggestion detail and slash parameter', () => {
  it('shows the detail in the list but stores only id, title and url', async () => {
    const search = vi.fn(async () => [{ id: 'a', title: 'Guide', url: '/guide', detail: 'Provoz · Koncept' }, { id: 'b', title: 'Guide 2', url: '/guide-2' }])
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 0 })]), editor = editorForController(c)!
    expect(c.dispatch(c.actions.slash[0]!.command!)).toBe(true)
    editor.commands.insertContent('Guide')
    await vi.waitFor(() => expect(document.querySelectorAll('[role=option]').length).toBe(2))
    const rows = [...document.querySelectorAll<HTMLElement>('[role=option]')]
    expect(rows[0]!.textContent).toContain('Provoz · Koncept'); expect(rows[0]!.textContent).not.toContain('/guide')
    expect(rows[1]!.textContent).toContain('/guide-2')
    rows[0]!.click()
    expect(c.getProjection().rich.document).toMatchObject({ content: [{ content: [{ type: 'pageReference', attrs: { id: 'a', title: 'Guide', url: '/guide' } }] }] })
    const node = editor.state.doc.firstChild!.firstChild!
    expect(Object.keys(node.attrs).sort()).toEqual(['id', 'title', 'url'])
    expect(c.getProjection().markdown).not.toContain('Koncept')
  })
  it('keeps a valid detail through normalizePageReference and drops an invalid one without rejecting the page', () => {
    const page = { id: 'a', title: 'A', url: '/a' }
    expect(normalizePageReference({ ...page, detail: '  Provoz · Koncept ' })).toEqual({ ...page, detail: 'Provoz · Koncept' })
    for (const detail of ['', '   ', 'a\nb', 'x'.repeat(201), 5, null, {}]) expect(normalizePageReference({ ...page, detail })).toEqual(page)
    expect(normalizePageReference(page)).toEqual(page)
    expect(pageReferenceNode({ ...page, detail: 'Koncept' })).toEqual({ type: 'pageReference', attrs: page })
  })
  it('offers the search as a slash action with a parameter that starts the [[ session with that text', async () => {
    const search = vi.fn(async (_query: string, _context: { signal: AbortSignal }) => [{ id: 'a', title: 'VPN', url: '/vpn' }])
    const c = mount([createPagesPlugin({ source: { search }, debounceMs: 0 })]), editor = editorForController(c)!
    const action = c.actions.slash[0]!
    expect(action.parameter?.label).toBeTruthy()
    expect(c.dispatch(action.parameter!.command('vpn  přístup')!)).toBe(true)
    expect(editor.getText()).toBe('[[vpn přístup')
    await vi.waitFor(() => expect(search).toHaveBeenCalledWith('vpn přístup', expect.anything()))
  })
})
