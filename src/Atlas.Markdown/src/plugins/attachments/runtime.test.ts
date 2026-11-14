// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Editor } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import { type MarkdownModuleContext } from '../../core'
import { createMarkdownContextToolbar } from '../../features/modules/contextToolbar'
import { createAttachmentsRuntime } from './runtime'
import type { AttachmentData, AttachmentUploadService } from './data'
const editors: Editor[] = []; const lifetimes: AbortController[] = []
afterEach(() => { lifetimes.splice(0).forEach(lifetime => lifetime.abort()); editors.splice(0).forEach(editor => editor.destroy()); document.body.replaceChildren() })
async function mount(upload: AttachmentUploadService) {
  const host = document.createElement('div'); document.body.append(host)
  const editor = new Editor({ element: host, extensions: [StarterKit], content: '<hr>' }); editors.push(editor)
  const container = document.createElement('div'); document.body.append(container)
  const update = vi.fn(); const lifetime = new AbortController(); lifetimes.push(lifetime)
  const toolbar = createMarkdownContextToolbar({ editor, anchor: container, getPos: () => 0, label: 'Příloha', signal: lifetime.signal })
  editor.commands.setNodeSelection(0); editor.view.focus(); toolbar.refresh()
  const context: MarkdownModuleContext<AttachmentData> = { container, data: { version: 1, name: 'Soubor', url: '' }, document: { type: 'doc' }, signal: lifetime.signal, editor, update, toolbar }
  const cleanup = await createAttachmentsRuntime({ upload }).render(context)
  expect(container.querySelector('input')).toBeNull()
  toolbar.element.querySelector<HTMLButtonElement>('[aria-label="Nahrát soubor"]')!.click()
  const panel = document.querySelector('.md-attachment-upload')!
  return { container, panel, update, cleanup, lifetime }
}
describe('attachment contextual upload', () => {
  it('cancels upload and ignores a provider that completes after cancellation', async () => {
    let resolve!: (data: Omit<AttachmentData, 'version'>) => void; let signal!: AbortSignal
    const upload = vi.fn((_file: File, options: { signal: AbortSignal }) => { signal = options.signal; return new Promise<Omit<AttachmentData, 'version'>>(yes => { resolve = yes }) })
    const { panel, update, cleanup } = await mount({ upload })
    const input = panel.querySelector('input')!
    Object.defineProperty(input, 'files', { value: [new File(['hello'], 'hello.txt', { type: 'text/plain' })] })
    input.dispatchEvent(new Event('change')); await Promise.resolve(); expect(upload).toHaveBeenCalledTimes(1)
    ;[...panel.querySelectorAll('button')].find(button => button.textContent === 'Zrušit nahrávání')!.click(); expect(signal.aborted).toBe(true)
    resolve({ name: 'hello.txt', url: 'https://example.cz/hello.txt' }); await Promise.resolve(); await Promise.resolve(); await Promise.resolve()
    expect(update).not.toHaveBeenCalled(); expect(input.disabled).toBe(false); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('aborts upload when the popover closes', async () => {
    let signal!: AbortSignal
    const upload = vi.fn((_file: File, options: { signal: AbortSignal }) => { signal = options.signal; return new Promise<Omit<AttachmentData, 'version'>>(() => {}) })
    const { panel, cleanup } = await mount({ upload })
    const input = panel.querySelector('input')!; Object.defineProperty(input, 'files', { value: [new File(['x'], 'x.txt')] }); input.dispatchEvent(new Event('change')); await Promise.resolve()
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(signal.aborted).toBe(true); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
  it('validates uploaded URL before modifying the document', async () => {
    const { panel, update, cleanup } = await mount({ upload: async () => ({ name: 'bad', url: 'javascript:alert(1)' }) })
    const input = panel.querySelector('input')!; Object.defineProperty(input, 'files', { value: [new File(['x'], 'x.txt')] }); input.dispatchEvent(new Event('change'))
    await vi.waitFor(() => expect(panel.querySelector('[role=status]')!.textContent).toContain('nezdařilo'))
    expect(update).not.toHaveBeenCalled(); typeof cleanup === 'function' ? cleanup() : cleanup?.destroy()
  })
})
