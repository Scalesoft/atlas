// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownModuleContext } from '../../core'
import { createAttachmentsRuntime } from './runtime'
import { createAttachmentsPlugin } from './index'
import type { AttachmentData, AttachmentsPluginOptions } from './data'
const editors: Editor[] = []; const lifetimes: AbortController[] = []
afterEach(() => { lifetimes.splice(0).forEach(l => l.abort()); editors.splice(0).forEach(e => e.destroy()); document.body.replaceChildren() })
async function mount(data: Partial<AttachmentData>, options: AttachmentsPluginOptions = {}) {
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr>' }); editors.push(editor)
  const container = document.createElement('div'); document.body.append(container)
  const update = vi.fn(); const lifetime = new AbortController(); lifetimes.push(lifetime)
  const context: MarkdownModuleContext<AttachmentData> = { container, data: { version: 1, name: 'Soubor', url: '', ...data }, document: { type: 'doc' }, signal: lifetime.signal, editor, update }
  const runtime = createAttachmentsRuntime(options); await runtime.render(context)
  return { container, update, runtime, context }
}
const type = (field: HTMLInputElement | HTMLTextAreaElement, value: string, key: string, init: KeyboardEventInit = {}) => {
  field.value = value; field.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init }))
}
describe('attachment card in-place editing', () => {
  it('renames on Enter, cancels on Escape, rejects an empty name', async () => {
    const { container, update } = await mount({ url: '/f.pdf' })
    const name = container.querySelector<HTMLElement>('.md-attachment-name')!
    name.click(); type(container.querySelector('input')!, 'Nový.pdf', 'Escape'); expect(update).not.toHaveBeenCalled()
    name.click(); type(container.querySelector('input')!, 'Nový.pdf', 'Enter')
    expect(update).toHaveBeenCalledWith({ version: 1, name: 'Nový.pdf', url: '/f.pdf' })
    update.mockClear(); name.click(); type(container.querySelector('input')!, '  ', 'Enter'); expect(update).not.toHaveBeenCalled()
    expect(container.querySelector('.md-attachment-error')!.textContent).toBe('Vyplňte název přílohy a podporovanou verzi.')
  })
  it('saves a multiline description only with Ctrl+Enter', async () => {
    const { container, update } = await mount({ url: '/f.pdf' })
    container.querySelector<HTMLElement>('.md-attachment-description')!.click()
    const area = container.querySelector('textarea')!
    type(area, 'a\nb', 'Enter'); expect(update).not.toHaveBeenCalled()
    type(area, 'a\nb', 'Enter', { ctrlKey: true }); expect(update.mock.calls[0][0].description).toBe('a\nb')
  })
  it('edit() focuses the name field; the URL sits under "Další"', async () => {
    const { container, runtime, context } = await mount({ url: '/f.pdf' })
    expect(container.querySelector('details.md-attachment-more summary')!.textContent).toBe('Další')
    runtime.edit!(context); expect(container.querySelector('input.md-attachment-field')).not.toBeNull()
  })
  it('empty card offers upload and takes a dropped file; errors show on the card', async () => {
    const upload = vi.fn().mockRejectedValueOnce(new Error('x')).mockResolvedValue({ name: 'a.txt', url: '/a.txt' })
    const { container, update } = await mount({}, { upload: { upload } })
    expect(container.querySelector('.md-attachment-pick')!.textContent).toBe('Nahrát soubor')
    expect(container.textContent).toContain('nebo přetáhněte soubor sem')
    const drop = () => { const e = new Event('drop', { bubbles: true, cancelable: true }); Object.defineProperty(e, 'dataTransfer', { value: { files: [new File(['x'], 'a.txt')] } }); container.querySelector('.md-attachment')!.dispatchEvent(e) }
    drop(); await vi.waitFor(() => expect(container.querySelector<HTMLElement>('.md-attachment-error')!.hidden).toBe(false))
    drop(); await vi.waitFor(() => expect(update).toHaveBeenCalledTimes(1))
    expect(update.mock.calls[0][0].url).toBe('/a.txt')
  })
  it('says on the card that an upload is running and that a cancelled one stopped', async () => {
    let aborted!: AbortSignal
    const { container, update } = await mount({}, { upload: { upload: (_file, { signal }) => { aborted = signal; return new Promise(() => {}) } } })
    const root = container.querySelector<HTMLElement>('.md-attachment')!
    const e = new Event('drop', { bubbles: true, cancelable: true }); Object.defineProperty(e, 'dataTransfer', { value: { files: [new File(['x'], 'a.txt')] } }); root.dispatchEvent(e)
    expect(root.getAttribute('aria-busy')).toBe('true'); expect(container.querySelector('[role=status]')!.textContent).toBe('Načítání a.txt…')
    ;[...container.querySelectorAll('button')].find(button => button.textContent === 'Zrušit nahrávání')!.click()
    await vi.waitFor(() => expect(container.querySelector('[role=status]')!.textContent).toBe('Nahrávání zrušeno.'))
    expect(aborted.aborted).toBe(true); expect(root.hasAttribute('aria-busy')).toBe(false); expect(update).not.toHaveBeenCalled()
  })
  it('read-only editor gets no edit controls', async () => {
    const host = document.createElement('div'); const editor = new Editor({ element: host, extensions: [StarterKit], editable: false }); editors.push(editor)
    const container = document.createElement('div')
    await createAttachmentsRuntime({}).render({ container, data: { version: 1, name: 'S', url: '/s' }, document: { type: 'doc' }, signal: new AbortController().signal, editor, update: vi.fn() })
    expect(container.querySelector('[data-editable]')).toBeNull()
  })
})
describe('attachment card in an editor', () => {
  const controllers: MarkdownEditorController[] = []
  afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()) })
  async function editorWith(data: AttachmentData, options: AttachmentsPluginOptions = {}) {
    const value = { format: 'atlas-rich', version: 1, plugins: [{ id: 'attachments', version: 1 }], document: { type: 'doc', content: [{ type: 'attachment', attrs: { data } }, { type: 'paragraph' }] } }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createAttachmentsPlugin(options)], source: { kind: 'local', value } as never }); controllers.push(controller)
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Editor' })
    const name = await vi.waitFor(() => { const element = host.querySelector<HTMLElement>('.md-attachment-name[data-editable]'); expect(element).not.toBeNull(); return element! })
    return { host, name, stored: () => JSON.stringify(controller.getProjection().rich.document) }
  }
  const drop = (target: Element, file: File) => { const e = new Event('drop', { bubbles: true, cancelable: true }); Object.defineProperty(e, 'dataTransfer', { value: { files: [file] } }); target.dispatchEvent(e); return e }
  it('a save keeps the card and the keyboard on the field; name, description and URL open where clicked', async () => {
    const { host, name, stored } = await editorWith({ version: 1, name: 'a.pdf', url: '/a.pdf' })
    const card = host.querySelector('.md-attachment')
    name.click(); type(document.activeElement as HTMLInputElement, 'b.pdf', 'Enter')
    expect(stored()).toContain('"name":"b.pdf"')
    expect(host.querySelector('.md-attachment')).toBe(card); expect(document.activeElement).toBe(name)
    for (const selector of ['.md-attachment-description', '.md-attachment-url']) {
      host.querySelector<HTMLElement>(selector)!.click()
      expect(document.activeElement?.classList.contains('md-attachment-field')).toBe(true)
      type(document.activeElement as HTMLInputElement, 'x', 'Escape')
    }
  })
  it('an upload survives an edit of the card, ignores a second dropped file and keeps the new description', async () => {
    let resolve!: (data: Omit<AttachmentData, 'version'>) => void
    const upload = vi.fn(() => new Promise<Omit<AttachmentData, 'version'>>(yes => { resolve = yes }))
    const { host, stored } = await editorWith({ version: 1, name: 'Nová příloha', url: '' }, { upload: { upload } })
    drop(host.querySelector('.md-attachment')!, new File(['x'], 'x.txt'))
    expect(drop(host.querySelector('.md-attachment')!, new File(['y'], 'y.txt')).defaultPrevented).toBe(true)
    host.querySelector<HTMLElement>('.md-attachment-description')!.click()
    type(document.activeElement as HTMLTextAreaElement, 'Popis', 'Enter', { ctrlKey: true })
    expect(stored()).toContain('"description":"Popis"')
    resolve({ name: 'x.txt', url: '/x.txt' })
    await vi.waitFor(() => expect(stored()).toContain('"url":"/x.txt"'))
    expect(stored()).toContain('"description":"Popis"'); expect(upload).toHaveBeenCalledTimes(1)
    expect(host.querySelectorAll('[data-md-module="attachment"]')).toHaveLength(1)
  })
})
