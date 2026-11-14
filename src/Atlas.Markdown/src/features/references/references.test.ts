// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { Editor, Node, type JSONContent } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { Slice } from '@tiptap/pm/model'
import type { MarkdownReferenceProvider } from '../../core/plugins/types'
import { createMarkdownReferenceExtension } from './index'

const url = 'https://example.com/issue/DEMO-102'
const issue = (id = '102'): JSONContent => ({ type: 'testReference', attrs: { id, url, label: 'DEMO-102: Opravit čtečku' } })
const Reference = Node.create({
  name: 'testReference', group: 'inline', inline: true, atom: true,
  addAttributes: () => ({ id: { default: null }, url: { default: null }, label: { default: null } }),
  renderHTML: ({ node }) => ['a', { href: node.attrs.url }, ['span', node.attrs.label]],
})
const TextOnlyBlock = Node.create({ name: 'textOnly', group: 'block', content: 'text*', renderHTML: () => ['div', 0] })
const editors: Editor[] = []
// jsdom has no layout; the anchored panel uses ProseMirror's real range geometry API.
beforeAll(async () => {
  Range.prototype.getClientRects = () => [new DOMRect()] as unknown as DOMRectList
  Range.prototype.getBoundingClientRect = () => new DOMRect()
  // The lazy UI module's first, cold import (transform included) otherwise fell into the first test's 1 s wait for its toolbar:
  // ~100 ms alone, 300 ms under 24 parallel runs, and past the wait under the changed.ps1 load (UX EP2-10).
  await import('./runtime')
})
const tick = async () => { for (let i = 0; i < 8; i++) await Promise.resolve() }
afterEach(() => { editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren(); vi.restoreAllMocks() })
function setup(resolveUrl: MarkdownReferenceProvider['resolveUrl'] | null = async () => issue(), content: JSONContent = { type: 'doc', content: [{ type: 'paragraph' }] }, loadRuntime?: () => Promise<typeof import('./runtime')>, matchUrl: MarkdownReferenceProvider['matchUrl'] = value => value.startsWith('https://example.com/issue/')) {
  const host = document.createElement('div'); document.body.append(host)
  const provider: MarkdownReferenceProvider = {
    id: 'test', label: 'Úkol', nodeType: 'testReference',
    read: node => node.type === 'testReference' && node.attrs?.id !== 'unsupported' ? { url: String(node.attrs?.url), label: String(node.attrs?.label) } : null,
    matchUrl, resolveUrl: resolveUrl ?? undefined,
  }
  const editor = new Editor({ element: host, extensions: [StarterKit, Reference, TextOnlyBlock, createMarkdownReferenceExtension([provider], loadRuntime)], content })
  editors.push(editor); editor.view.focus()
  return editor
}
function paste(editor: Editor, value = url, extra: { html?: string; files?: File[]; shiftKey?: boolean } = {}) {
  const event = { clipboardData: { getData: (type: string) => type === 'text/html' ? extra.html ?? '' : type === 'text/plain' ? value : '', files: extra.files ?? [], types: ['text/plain'] }, shiftKey: extra.shiftKey ?? false, preventDefault() {} } as unknown as ClipboardEvent
  let handled = false
  editor.view.someProp('handlePaste', handler => { if (handler(editor.view, event, Slice.empty)) { handled = true; return true } })
  return handled
}
async function click(label: string) {
  let button: HTMLButtonElement | undefined
  await vi.waitFor(() => {
    button = [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.getAttribute('aria-label') === label || item.textContent === label)
    expect(button, label).toBeDefined()
  })
  button!.click()
}
describe('shared reference interaction', () => {
  it('shows a saved relative address as saved and resolves it against the page only to load it', async () => {
    const relative = '#/pages/project', normalized = new URL(relative, document.baseURI).href
    const resolve = vi.fn(async () => issue())
    const editor = setup(resolve, { type: 'doc', content: [{ type: 'paragraph', content: [{ ...issue(), attrs: { id: 'page', url: relative, label: 'Projekt' } }] }] }, undefined, value => value === normalized)
    editor.commands.setNodeSelection(1)
    await click('Upravit adresu')
    // Never the origin the page runs on (review 2026-11-06: http://localhost:5140/#/pages/…).
    expect(document.querySelector<HTMLInputElement>('input')!.value).toBe(relative)
    expect(resolve).not.toHaveBeenCalled()
    await click('Načíst referenci'); await tick()
    expect(resolve).toHaveBeenCalledWith(normalized, expect.objectContaining({ signal: expect.any(AbortSignal) }))
  })

  it('inserts the plain link immediately but waits for the lazy UI before offering conversion', async () => {
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    const load = vi.fn(async () => { await gate; return import('./runtime') })
    const resolve = vi.fn(async () => issue())
    const editor = setup(resolve, undefined, load)
    expect(load).not.toHaveBeenCalled()
    expect(paste(editor, 'https://unknown.example/a')).toBe(false)
    expect(load).not.toHaveBeenCalled()
    paste(editor)
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ type: 'text', text: url, marks: [{ type: 'link', attrs: { href: url } }] })
    expect(document.querySelector('[role="menu"]')).toBeNull()
    expect(resolve).not.toHaveBeenCalled()
    expect(load).toHaveBeenCalledTimes(1)
    // Focus and history metadata do not invalidate the document target.
    editor.view.dispatch(editor.state.tr.setMeta('addToHistory', false))
    release()
    await vi.waitFor(() => expect(document.querySelector('[role="menu"]')).not.toBeNull())
    expect(resolve).not.toHaveBeenCalled()
  })

  it('ignores late offers after target changes or view destruction while the UI module loads', async () => {
    for (const change of ['edit', 'destroy', 'detach'] as const) {
      let release!: () => void
      const gate = new Promise<void>(resolve => { release = resolve })
      const load = vi.fn(async () => { await gate; return import('./runtime') })
      const editor = setup(undefined, undefined, load)
      paste(editor)
      expect(document.querySelector('[role="menu"]')).toBeNull()
      if (change === 'edit') editor.view.dispatch(editor.state.tr.insertText('changed ', 1))
      else if (change === 'destroy') editor.destroy()
      else editor.view.dom.remove()
      release()
      await vi.waitFor(() => expect(load).toHaveResolved())
      await tick()
      expect(document.querySelector('[role="menu"]')).toBeNull()
      if (!editor.isDestroyed) expect(editor.getText()).toContain(url)
      editor.destroy()
    }
  })

  it('starts a current lazy request when plugin views are recreated during an older import', async () => {
    let release!: () => void
    const gate = new Promise<void>(resolve => { release = resolve })
    const load = vi.fn(async () => { await gate; return import('./runtime') })
    const editor = setup(undefined, { type: 'doc', content: [{ type: 'paragraph', content: [issue()] }] }, load)
    editor.commands.setNodeSelection(1)
    expect(load).toHaveBeenCalledTimes(1)
    editor.view.updateState(editor.state.reconfigure({ plugins: [...editor.state.plugins] }))
    expect(load).toHaveBeenCalledTimes(2)
    release()
    await vi.waitFor(() => expect(document.querySelector('[role="toolbar"]')).not.toBeNull())
  })

  it('preserves a reference host click when its pure projection declines the saved shape', () => {
    const load = vi.fn(async () => import('./runtime'))
    const editor = setup(undefined, { type: 'doc', content: [{ type: 'paragraph', content: [issue('unsupported')] }] }, load)
    const anchor = editor.view.dom.querySelector('a')!
    const navigate = vi.fn((event: Event) => event.preventDefault())
    anchor.addEventListener('click', navigate)
    anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    expect(navigate).toHaveBeenCalledTimes(1)
    expect(load).not.toHaveBeenCalled()
  })

  it('keeps an empty saved address outside reference UI and preserves the host click', () => {
    const load = vi.fn(async () => import('./runtime'))
    const editor = setup(undefined, { type: 'doc', content: [{ type: 'paragraph', content: [{ ...issue(), attrs: { ...issue().attrs, url: '' } }] }] }, load)
    const anchor = editor.view.dom.querySelector('a')!
    const navigate = vi.fn((event: Event) => event.preventDefault())
    anchor.addEventListener('click', navigate)
    anchor.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))
    expect(navigate).toHaveBeenCalledTimes(1)
    expect(load).not.toHaveBeenCalled()
  })

  it('maps a pending target through outside edits but cancels after moving the selection', async () => {
    let finish!: (value: JSONContent) => void
    let signal!: AbortSignal
    const editor = setup((_url, context) => { signal = context.signal; return new Promise(resolve => { finish = resolve }) })
    paste(editor); await click('Převést na Úkol'); await tick()
    editor.view.dispatch(editor.state.tr.insertText('prefix ', 1))
    finish(issue()); await tick(); await click('Použít')
    expect(editor.getJSON().content?.[0]?.content).toMatchObject([{ type: 'text', text: 'prefix ' }, issue()])
    editor.commands.undo(); editor.commands.setTextSelection(editor.state.doc.content.size - 1)
    paste(editor); await click('Převést na Úkol'); await tick()
    editor.commands.setTextSelection(1); await tick()
    expect(signal.aborted).toBe(true)
    expect(document.querySelector('[role="dialog"]')).toBeNull()
    finish(issue()); await tick()
    expect(editor.getJSON().content?.[0]?.content?.some(node => node.type === 'testReference')).toBe(false)
  })

  it('offers shared actions for a saved page with a safe relative URL', async () => {
    const editor = setup(undefined, { type: 'doc', content: [{ type: 'paragraph', content: [{ ...issue(), attrs: { id: 'page', url: '#/pages/project', label: 'Projekt' } }] }] })
    editor.commands.setNodeSelection(1); await tick()
    await vi.waitFor(() => expect(document.querySelector('[role="toolbar"][aria-label="Reference"]')).not.toBeNull())
    await click('Převést na běžný odkaz')
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ type: 'text', text: 'Projekt', marks: [{ type: 'link', attrs: { href: '#/pages/project' } }] })
  })

  it('opens the edit from Enter or F2 and leaves every click to the link, as any link in the text (J3.5)', async () => {
    const editor = setup(async () => issue(), { type: 'doc', content: [{ type: 'paragraph', content: [issue()] }] })
    const chip = editor.view.dom.querySelector('a')!
    const panel = () => document.querySelector('[role=dialog]')
    const press = (init: MouseEventInit = {}) => { const event = new MouseEvent('click', { bubbles: true, cancelable: true, button: 0, ...init }); chip.dispatchEvent(event); return event }
    expect(press().defaultPrevented).toBe(false)
    expect(panel()).toBeNull()
    editor.commands.setNodeSelection(1)
    await vi.waitFor(() => expect(document.querySelector('[role="toolbar"][aria-label="Reference"]')).not.toBeNull())
    for (const key of ['Enter', 'F2']) {
      editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
      expect(panel(), key).not.toBeNull()
      document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
      expect(panel(), key).toBeNull()
    }
    expect(press({ ctrlKey: true }).defaultPrevented).toBe(false)
    expect(panel()).toBeNull()
  })

  it('edits a selected reference where it is shown, its identity only after explicit reload and Apply', async () => {
    const resolve = vi.fn(async () => ({ ...issue('103'), attrs: { ...issue('103').attrs, url: 'https://example.com/issue/DEMO-103' } }))
    const editor = setup(resolve, { type: 'doc', content: [{ type: 'paragraph', content: [issue()] }] })
    editor.commands.setNodeSelection(1)
    await click('Upravit adresu')
    await vi.waitFor(() => expect(document.querySelector('[role=dialog] input')).not.toBeNull())
    expect(editor.state.selection.from).toBe(1)
    const input = document.querySelector<HTMLInputElement>('input')!
    input.value = 'https://example.com/issue/DEMO-103'; input.dispatchEvent(new Event('input'))
    expect(resolve).not.toHaveBeenCalled()
    expect(document.querySelector<HTMLButtonElement>('.md-btn-primary')!.disabled).toBe(true)
    await click('Načíst referenci'); await tick()
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ attrs: { id: '102' } })
    await click('Použít')
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ attrs: { id: '103' } })
    editor.commands.undo()
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ attrs: { id: '102' } })
  })

  it('takes a typed address with Enter: it loads the target and applies it (P4)', async () => {
    const resolve = vi.fn(async () => ({ ...issue('103'), attrs: { ...issue('103').attrs, url: 'https://example.com/issue/DEMO-103' } }))
    const editor = setup(resolve, { type: 'doc', content: [{ type: 'paragraph', content: [issue()] }] })
    editor.commands.setNodeSelection(1)
    await click('Upravit adresu')
    await vi.waitFor(() => expect(document.querySelector('[role=dialog] input')).not.toBeNull())
    const input = document.querySelector<HTMLInputElement>('[role=dialog] input')!
    input.value = 'https://example.com/issue/DEMO-103'; input.dispatchEvent(new Event('input'))
    const enter = new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })
    input.dispatchEvent(enter)
    expect(enter.defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ attrs: { id: '103' } }))
    expect(resolve).toHaveBeenCalledTimes(1)
    expect(document.querySelector('[role=dialog]')).toBeNull()
  })

  it('preloads the reference UI when chips arrive after the view and the editor turns editable (a collaborative page)', async () => {
    const load = vi.fn(() => import('./runtime'))
    const editor = setup(async () => issue(), undefined, load)
    editor.setEditable(false)
    // The content arrives while the editor is still read-only: nothing loads yet.
    editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content: [issue()] }] })
    expect(load).not.toHaveBeenCalled()
    editor.setEditable(true)
    expect(load).toHaveBeenCalledTimes(1)
    // Typing afterwards walks nothing more and loads nothing again.
    editor.commands.insertContentAt(editor.state.doc.content.size - 1, 'x')
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('cancels even a replacement with identical text while metadata is pending', async () => {
    let signal!: AbortSignal, finish!: (value: JSONContent) => void
    const editor = setup((_url, context) => { signal = context.signal; return new Promise(resolve => { finish = resolve }) })
    paste(editor); await click('Převést na Úkol'); await tick()
    const text = editor.state.doc.nodeAt(1)!
    editor.view.dispatch(editor.state.tr.replaceWith(1, 1 + url.length, text))
    await tick(); expect(signal.aborted).toBe(true)
    finish(issue()); await tick()
    expect(document.querySelector('[role="dialog"]')).toBeNull()
  })

  it('preserves saved formatting when editing a reference as an ordinary link without a resolver', async () => {
    const editor = setup(null, { type: 'doc', content: [{ type: 'paragraph', content: [{ ...issue(), marks: [{ type: 'bold' }] }] }] })
    editor.commands.setNodeSelection(1); await click('Upravit jako běžný odkaz')
    const input = document.querySelector<HTMLInputElement>('input')!
    input.value = 'https://example.com/new'; input.dispatchEvent(new Event('input')); await click('Použít')
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ type: 'text', text: issue().attrs!.label, marks: expect.arrayContaining([{ type: 'bold' }, { type: 'link', attrs: expect.objectContaining({ href: 'https://example.com/new' }) }]) })
  })

  it('keeps the original link when the schema cannot hold the resolved atom at its target', async () => {
    const editor = setup(undefined, { type: 'doc', content: [{ type: 'textOnly' }] })
    paste(editor); await click('Převést na Úkol'); await tick()
    const before = editor.getJSON()
    await click('Použít')
    expect(editor.getJSON()).toEqual(before)
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ type: 'text', text: url })
    expect(document.querySelector('[role="alert"]')).not.toBeNull()
  })

  it('preserves active text formatting on paste and bypasses stored code marks', () => {
    const editor = setup()
    editor.commands.setMark('bold'); paste(editor)
    expect(editor.getJSON().content?.[0]?.content?.[0]?.marks).toEqual(expect.arrayContaining([{ type: 'bold' }]))
    editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph' }] })
    editor.commands.setMark('code')
    expect(paste(editor)).toBe(false)
  })

  it('rejects late metadata after mark changes, readonly, close and destruction', async () => {
    for (const change of ['marks', 'readonly', 'close', 'destroy'] as const) {
      let finish!: (value: JSONContent) => void
      let signal!: AbortSignal
      const editor = setup((_url, context) => { signal = context.signal; return new Promise(resolve => { finish = resolve }) })
      paste(editor); await click('Převést na Úkol'); await tick()
      if (change === 'marks') editor.view.dispatch(editor.state.tr.addMark(1, 1 + url.length, editor.schema.marks.bold!.create()))
      else if (change === 'readonly') editor.setEditable(false)
      else if (change === 'close') click('Zrušit')
      else editor.destroy()
      await tick(); expect(signal.aborted).toBe(true)
      finish(issue()); await tick()
      expect(document.querySelector('[role="dialog"]')).toBeNull()
      if (!editor.isDestroyed) expect(editor.getText()).toBe(url)
      editor.destroy()
    }
  })

  it('preserves existing paste handling for nonplain URLs, selected labels and code', () => {
    const editor = setup()
    expect(paste(editor, 'https://unknown.example/a')).toBe(false)
    expect(paste(editor, url + '\n')).toBe(false)
    expect(paste(editor, url, { html: '<a>link</a>' })).toBe(false)
    expect(paste(editor, url, { files: [new File(['x'], 'x.txt')] })).toBe(false)
    expect(paste(editor, url, { shiftKey: true })).toBe(false)
    editor.commands.insertContent('label'); editor.commands.setTextSelection({ from: 1, to: 6 })
    expect(paste(editor)).toBe(false)
    editor.commands.setContent({ type: 'doc', content: [{ type: 'codeBlock', content: [{ type: 'text', text: 'code' }] }] })
    editor.commands.setTextSelection(2)
    expect(paste(editor)).toBe(false)
  })

  it('preserves the link on failed or invalid metadata and retries only by explicit action', async () => {
    const resolve = vi.fn<NonNullable<MarkdownReferenceProvider['resolveUrl']>>().mockRejectedValueOnce(new Error('private backend detail')).mockResolvedValueOnce(issue())
    const editor = setup(resolve); paste(editor); await click('Převést na Úkol'); await tick()
    expect(document.querySelector('[role="alert"]')?.textContent).not.toContain('private backend detail')
    expect(editor.getText()).toBe(url)
    await click('Zkusit znovu'); await tick(); await click('Použít')
    expect(resolve).toHaveBeenCalledTimes(2)
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject(issue())
  })

  it('keeps a pasted URL as a link until explicit resolve and Apply, with separate undo', async () => {
    const resolve = vi.fn(async () => issue())
    const editor = setup(resolve)
    expect(paste(editor)).toBe(true); await tick()
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject({ type: 'text', text: url, marks: [{ type: 'link', attrs: { href: url } }] })
    expect(resolve).not.toHaveBeenCalled()
    await click('Převést na Úkol'); await tick()
    expect(editor.getJSON().content?.[0]?.content?.[0]?.type).toBe('text')
    await click('Použít'); await tick()
    expect(editor.getJSON().content?.[0]?.content?.[0]).toMatchObject(issue())
    editor.commands.undo()
    expect(editor.getText()).toBe(url)
    editor.commands.undo()
    expect(editor.getText()).toBe('')
  })
})
