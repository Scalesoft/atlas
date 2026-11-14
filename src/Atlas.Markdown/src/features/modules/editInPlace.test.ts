// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Editor } from '@tiptap/core'
import { createMarkdownEditorController, type MarkdownEditorController, type MarkdownPlugin } from '../../core'
import { editorForController } from '../../core/internal'
import { createAttachmentsPlugin } from '../../plugins/attachments'
import { createChartsPlugin } from '../../plugins/charts'
import { createMathPlugin } from '../../plugins/math'
import { createOpenApiPlugin, defaultOpenApi } from '../../plugins/openapi'
import { createTocPlugin } from '../../plugins/toc'

// The shared block rule (a click on a block runs its edit) together with each plugin's own in-place fields, in a real editor:
// what the plugins' own tests, which render a runtime alone, cannot see.
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren() })

function mount(plugin: MarkdownPlugin, id: string, node: object) {
  const value = { format: 'atlas-rich', version: 1, plugins: [{ id, version: 1 }], document: { type: 'doc', content: [node, { type: 'paragraph' }] } }
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [plugin], source: { kind: 'local', value } as never }); controllers.push(controller)
  const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Editor' })
  return { host, controller, editor: editorForController(controller)! as Editor }
}

// Runtimes (KaTeX, Scalar) load slowly while every CPU runs a test file: the waits are as long as the other editor tests'.
describe('editing where a component is shown, in an editor', { timeout: 60_000 }, () => {
  it('a click on an attachment name or a chart title keeps the keyboard in the field it opened', async () => {
    const { host } = mount(createAttachmentsPlugin(), 'attachments', { type: 'attachment', attrs: { data: { version: 1, name: 'a.pdf', url: '/a.pdf' } } })
    const name = await vi.waitFor(() => { const element = host.querySelector<HTMLElement>('.md-attachment-name[data-editable]'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    name.click()
    expect(document.activeElement?.tagName).toBe('INPUT')

    HTMLDialogElement.prototype.showModal ??= vi.fn()
    const chart = mount(createChartsPlugin(), 'charts', { type: 'chart', attrs: { data: { version: 1, kind: 'bar', title: 'T', grid: [['a', 'b'], ['x', '1']] } } })
    const title = await vi.waitFor(() => { const element = chart.host.querySelector<HTMLElement>('.md-chart-title'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    title.click()
    expect(document.activeElement?.tagName).toBe('INPUT')
  })
  it('a click inside the open formula panel keeps it open; Esc gives the keyboard back to the editor', async () => {
    const { host, editor } = mount(createMathPlugin(), 'math', { type: 'mathBlock', attrs: { data: { version: 1, expression: 'x^2' } } })
    const panel = await vi.waitFor(() => { const element = host.querySelector<HTMLElement>('.md-math-panel'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    // A click on a block formula only selects it; Enter opens its source.
    host.querySelector<HTMLElement>('.md-math')!.click()
    expect(panel.hidden).toBe(true)
    editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    expect(panel.hidden).toBe(false)
    panel.querySelector<HTMLElement>('.md-math-source')!.click()
    expect(panel.hidden).toBe(false)
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    expect(panel.hidden).toBe(true)
    expect(document.activeElement).toBe(editor.view.dom)
  })
  it('the inline formula popover stays open while its formula is typed', async () => {
    const { host } = mount(createMathPlugin(), 'math', { type: 'paragraph', content: [{ type: 'mathInline', attrs: { data: { version: 1, expression: 'a' } } }] })
    const formula = await vi.waitFor(() => { const element = host.querySelector<HTMLElement>('p .md-math'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    formula.click()
    const field = await vi.waitFor(() => { const element = document.querySelector<HTMLTextAreaElement>('.md-context-popover textarea'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    field.value = 'ab'; field.dispatchEvent(new Event('input', { bubbles: true }))
    field.value = 'abc'; field.dispatchEvent(new Event('input', { bubbles: true }))
    expect(field.isConnected).toBe(true)
  })
  it('„Nahrát soubor“ on an API card reaches its file chooser', async () => {
    const source = "openapi: 3.1.0\ninfo:\n  title: Orders API\n  version: '2.0'\npaths: {}\n"
    const { host } = mount(createOpenApiPlugin({}), 'openapi', { type: 'openapi', attrs: { data: { ...defaultOpenApi, filename: 'orders.yaml', source } } })
    const picker = await vi.waitFor(() => { const element = host.querySelector<HTMLInputElement>('.md-openapi-card input[type=file]'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    let prevented: boolean | undefined
    document.addEventListener('click', event => { if (event.target === picker) prevented = event.defaultPrevented })
    ;[...host.querySelectorAll<HTMLButtonElement>('.md-openapi-card button')].find(button => /Nahr/.test(button.textContent ?? ''))!.click()
    expect(prevented).toBe(false)
  })
  it('the API source dialog chooses its file through a Czech button, the native field hidden', async () => {
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.setAttribute('open', '') }
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.removeAttribute('open') }
    const { host } = mount(createOpenApiPlugin({}), 'openapi', { type: 'openapi', attrs: { data: { ...defaultOpenApi, filename: 'orders.yaml' } } })
    const card = await vi.waitFor(() => { const element = host.querySelector<HTMLElement>('.md-openapi-card'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    // A click on the card only selects it; its „Upravit specifikaci“ button opens the source dialog.
    card.querySelector<HTMLElement>('.md-openapi-edit')!.click()
    const dialog = await vi.waitFor(() => { const element = document.querySelector<HTMLElement>('.md-openapi-source-dialog'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    const native = dialog.querySelector<HTMLInputElement>('input[type=file]')!
    expect(native.classList.contains('md-openapi-file-input')).toBe(true)
    expect(dialog.querySelector('.md-openapi-file-chooser')?.textContent).toBe('Vybrat soubor…Žádný soubor')
    const opened = vi.spyOn(native, 'click').mockImplementation(() => {})
    dialog.querySelector<HTMLButtonElement>('.md-openapi-file-chooser button')!.click()
    expect(opened).toHaveBeenCalledOnce()
  })
  it('a chart in a read-only editor offers nothing to edit', async () => {
    const { host, controller } = mount(createChartsPlugin(), 'charts', { type: 'chart', attrs: { data: { version: 1, kind: 'bar', title: 'T', grid: [['a', 'b'], ['x', '1']] } } })
    await vi.waitFor(() => expect(host.querySelector('.md-chart-title')).not.toBeNull(), { timeout: 30_000 })
    controller.setEditable(false)
    await vi.waitFor(() => expect(host.querySelector('.md-chart-kind-chip')).toBeNull(), { timeout: 30_000 })
    expect(host.querySelector('.md-chart-title')?.getAttribute('tabindex')).toBeNull()
  })
  it('a heading range picked in the TOC menu applies, by click and by keyboard', async () => {
    const heading = (level: number, text: string) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })
    const { host, controller } = mount(createTocPlugin(), 'toc', { type: 'tableOfContents', attrs: { data: { version: 1, title: 'Obsah', minLevel: 1, maxLevel: 3 } } })
    controller.dispatch({ type: 'command', id: 'noop' } as never)
    const chip = await vi.waitFor(() => { const element = host.querySelector<HTMLButtonElement>('.md-toc-range'); expect(element).not.toBeNull(); return element! }, { timeout: 30_000 })
    void heading
    chip.click()
    ;[...document.querySelectorAll<HTMLButtonElement>('.md-context-menu-item')].find(item => item.textContent === 'H2–H3')!.click()
    await vi.waitFor(() => expect(host.querySelector('.md-toc-range')?.textContent).toBe('H2–H3'))
    // Ctrl+Z on the component's own control undoes the document, as in its text.
    const range = host.querySelector<HTMLButtonElement>('.md-toc-range')!; range.focus()
    const undo = new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true, cancelable: true }); range.dispatchEvent(undo)
    expect(undo.defaultPrevented).toBe(true)
    await vi.waitFor(() => expect(host.querySelector('.md-toc-range')?.textContent).toBe('H1–H3'))
  })
})
