// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { collectTocHeadings, createTocNode, createTocPlugin, normalizeToc, type MarkdownTocData } from './index'
import { tocRuntime } from './runtime'
import { createMarkdownEditorController, type MarkdownPlugin, type MarkdownReadonlyJSONContent } from '../../core'
import type { Editor } from '@tiptap/core'

const data: MarkdownTocData = { version: 1, title: 'Obsah', minLevel: 1, maxLevel: 3 }
const rich: MarkdownReadonlyJSONContent = { type: 'doc', content: [
  { type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Úvod' }] },
  { type: 'heading', attrs: { level: 4 }, content: [{ type: 'text', text: 'Skrytý detail' }] },
  { type: 'blockquote', content: [{ type: 'heading', attrs: { level: 2 }, content: [{ type: 'text', text: 'Vnořený' }, { type: 'hardBreak' }, { type: 'text', text: 'nadpis' }] }] },
] }
afterEach(() => { document.body.replaceChildren() })
describe('table of contents', () => {
  it('collects nested headings in document order and retains indices across filtered levels', () => {
    expect(collectTocHeadings(rich, data)).toEqual([{ title: 'Úvod', level: 1, index: 0 }, { title: 'Vnořený nadpis', level: 2, index: 2 }])
    expect(() => normalizeToc({ ...data, minLevel: 4, maxLevel: 2 })).toThrow()
    expect(() => normalizeToc({ ...data, maxLevel: 7 })).toThrow()
    const deep: MarkdownReadonlyJSONContent = { type: 'doc', content: [5, 6].map(level => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text: `H${level}` }] })) }
    expect(collectTocHeadings(deep, { minLevel: 1, maxLevel: 6 }).map(entry => entry.level)).toEqual([5, 6])
    expect(collectTocHeadings(deep, { minLevel: 1, maxLevel: 5 }).map(entry => entry.level)).toEqual([5])
  })
  it('follows stable heading identity when the view order changes before navigation', async () => {
    const documentValue: MarkdownReadonlyJSONContent = { type: 'doc', content: [
      { type: 'heading', attrs: { level: 1, headingId: 'first' }, content: [{ type: 'text', text: 'První' }] },
      { type: 'heading', attrs: { level: 1, headingId: 'second' }, content: [{ type: 'text', text: 'Druhý' }] },
    ] }
    expect(collectTocHeadings(documentValue, data)[0]).toMatchObject({ headingId: 'first' })
    const root = document.createElement('div'); root.innerHTML = '<h1 data-heading-id="first">První</h1><h1 data-heading-id="second">Druhý</h1><section></section>'; document.body.append(root)
    const first = root.firstElementChild as HTMLElement, second = root.children[1] as HTMLElement
    first.scrollIntoView = vi.fn(); second.scrollIntoView = vi.fn()
    const container = root.lastElementChild as HTMLElement
    await tocRuntime.render({ container, rootContainer: root, data, document: documentValue, signal: new AbortController().signal })
    root.insertBefore(second, first)
    container.querySelector<HTMLAnchorElement>('a')!.click()
    expect(first.scrollIntoView).toHaveBeenCalledOnce(); expect(second.scrollIntoView).not.toHaveBeenCalled()
  })
  it('scopes anchors to each readonly document and ignores rendered module headings', async () => {
    const roots: HTMLElement[] = []
    for (let i = 0; i < 2; i++) {
      const root = document.createElement('div'); root.innerHTML = '<h1>Úvod</h1><section data-md-module="other"><h2>Not a document heading</h2></section><h4>Skrytý detail</h4><blockquote><h2>Vnořený nadpis</h2></blockquote><section data-md-module="tableOfContents"></section>'
      document.body.append(root); roots.push(root)
      const container = root.lastElementChild as HTMLElement
      await tocRuntime.render({ container, rootContainer: root, data, document: rich, signal: new AbortController().signal })
      const links = [...container.querySelectorAll<HTMLAnchorElement>('a')]
      expect(links).toHaveLength(2)
      const nested = root.querySelector('blockquote h2') as HTMLElement
      const scroll = vi.fn(); nested.scrollIntoView = scroll
      links[1]!.click(); expect(scroll).toHaveBeenCalledOnce()
      expect(document.activeElement).toBe(nested)
      expect(links[1]?.hash).toBe(`#${nested.id}`)
    }
    expect(roots[0]?.querySelector('h1')?.id).not.toBe(roots[1]?.querySelector('h1')?.id)
    expect(document.querySelector('[data-md-module=other] h2')?.id).toBe('')
  })
  it('handles empty documents and stops navigation after disposal', async () => {
    const root = document.createElement('div'); root.innerHTML = '<h1>Úvod</h1><section></section>'; document.body.append(root)
    const container = root.lastElementChild as HTMLElement
    const abort = new AbortController()
    await tocRuntime.render({ container, rootContainer: root, data, document: rich, signal: abort.signal })
    const heading = root.firstElementChild as HTMLElement; heading.scrollIntoView = vi.fn()
    abort.abort(); container.querySelector('a')?.click(); expect(heading.scrollIntoView).not.toHaveBeenCalled()
    await tocRuntime.render({ container, rootContainer: root, data, document: { type: 'doc' }, signal: new AbortController().signal })
    expect(container.textContent).toContain('Přidejte do dokumentu nadpisy')
    expect(container.querySelector('a')).toBeNull()
  })
  it('updates navigation after editing a heading and selects the current heading in the editor', async () => {
    let editor!: Editor
    const capture: MarkdownPlugin = { id: 'capture-toc', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createTocPlugin(), capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'toc', version: 1 }], document: { type: 'doc', content: [createTocNode(data), { type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Původní' }] }] } } } })
    const root = document.createElement('div'); document.body.append(root)
    try {
      controller.attachView(root, { ariaLabel: 'Editor' })
      await vi.waitFor(() => expect(root.querySelector('nav li a')?.textContent).toBe('Původní'))
      editor.commands.insertContentAt({ from: 2, to: 9 }, 'Nový')
      await vi.waitFor(() => expect(root.querySelector('nav li a')?.textContent).toBe('Nový'))
      root.querySelector<HTMLButtonElement>('nav li a')?.click()
      expect(editor.state.selection.from).toBe(2)
    } finally { controller.destroy() }
  })
  describe('in-place editing', () => {
    const mount = async () => {
      let editor!: Editor
      const capture: MarkdownPlugin = { id: 'capture-toc', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
      const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createTocPlugin(), capture], source: { kind: 'local', value: { format: 'atlas-rich', version: 1, plugins: [{ id: 'toc', version: 1 }], document: { type: 'doc', content: [createTocNode(data), { type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Úvod' }] }] } } } })
      const root = document.createElement('div'); document.body.append(root)
      controller.attachView(root, { ariaLabel: 'Editor' })
      // The editor's own normalization lands with the first transaction, which re-renders the modules once: let it land first.
      editor.view.dispatch(editor.state.tr.setMeta('addToHistory', false))
      await vi.waitFor(() => expect(root.querySelector('.md-toc-title[role="button"]')).not.toBeNull())
      const stored = () => { let found: MarkdownTocData | undefined; editor.state.doc.descendants(node => { if (node.attrs.data?.minLevel) found = node.attrs.data }); return found }
      return { controller, root, stored }
    }
    const keydown = (target: Element, key: string) => target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }))
    it('edits the title where it is shown: Enter saves, Esc cancels, empty falls back to the default', async () => {
      const { controller, root, stored } = await mount()
      try {
        root.querySelector<HTMLElement>('.md-toc-title[role="button"]')!.click()
        let input = root.querySelector<HTMLInputElement>('.md-toc-title-input')!
        expect(document.activeElement).toBe(input)
        input.value = 'Zrušeno'; keydown(input, 'Escape')
        expect(root.querySelector('.md-toc-title-input')).toBeNull(); expect(stored()?.title).toBe('Obsah')
        root.querySelector<HTMLElement>('.md-toc-title[role="button"]')!.click()
        input = root.querySelector<HTMLInputElement>('.md-toc-title-input')!
        input.value = 'Osnova'; keydown(input, 'Enter')
        await vi.waitFor(() => expect(stored()?.title).toBe('Osnova'))
        await vi.waitFor(() => expect(root.querySelector('.md-toc-title[role="button"]')?.textContent).toBe('Osnova'))
        root.querySelector<HTMLElement>('.md-toc-title[role="button"]')!.click()
        input = root.querySelector<HTMLInputElement>('.md-toc-title-input')!
        input.value = '  '; input.dispatchEvent(new Event('blur'))
        await vi.waitFor(() => expect(stored()?.title).toBe('Obsah dokumentu'))
      } finally { controller.destroy() }
    })
    it('changes the heading range from the chip menu at once and keeps the data shape', async () => {
      const { controller, root, stored } = await mount()
      try {
        const chip = root.querySelector<HTMLButtonElement>('.md-toc-range')!
        expect(chip.textContent).toBe('H1–H3')
        chip.click()
        const items = [...document.querySelectorAll<HTMLButtonElement>('.md-context-menu-item')]
        expect(document.activeElement).toBe(items.find(item => item.getAttribute('aria-checked') === 'true'))
        keydown(document.querySelector('[role=menu]')!, 'Escape')
        await vi.waitFor(() => expect(document.querySelector('[role=menu]')).toBeNull()); expect(document.activeElement).toBe(chip)
        chip.click()
        document.querySelectorAll<HTMLButtonElement>('.md-context-menu-item')[1]!.click()
        await vi.waitFor(() => expect(stored()).toEqual({ version: 1, title: 'Obsah', minLevel: 2, maxLevel: 2 }))
        await vi.waitFor(() => expect(root.querySelector('.md-toc-range')?.textContent).toBe('H2'))
      } finally { controller.destroy() }
    })
    it('shows no editing controls in a readonly document', async () => {
      const root = document.createElement('div'); root.innerHTML = '<section></section>'; document.body.append(root)
      const container = root.firstElementChild as HTMLElement
      await tocRuntime.render({ container, rootContainer: root, data, document: rich, signal: new AbortController().signal })
      expect(container.querySelector('.md-toc-title[role="button"], .md-toc-range')).toBeNull()
      expect(container.querySelector('.md-toc-title')?.textContent).toBe('Obsah')
    })
  })
})
