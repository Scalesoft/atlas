// @vitest-environment jsdom
import type { Editor } from '@tiptap/core'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController, markdownInputErrorEvent, type MarkdownEditorController, type MarkdownPlugin, type MarkdownRichValue } from '../core'
import { createChartsPlugin } from './charts'
import { createDiagramsPlugin } from './diagrams'
import { createMathPlugin } from './math'
import { createTocPlugin } from './toc'
import { createAttachmentsPlugin, type AttachmentsPluginOptions } from './attachments'
import { createOpenApiPlugin } from './openapi'

/*
 * Contract 3.6-0, rule 4: when writing stops under an open interaction (a lock, a revoked right, offline) the component closes it,
 * writes nothing, keeps the unfinished input for the next edit and announces it on the page; an upload in flight is called off.
 */
vi.mock('./charts/chartRenderer', () => ({ createChartRenderer: () => ({ update() {}, destroy() {} }) }))
vi.mock('mermaid', () => ({ default: { initialize: vi.fn(), render: vi.fn().mockRejectedValue(new Error('Syntax error')) } }))
vi.mock('./openapi/sourceEditorLoader', () => ({
  prefetchOpenApiSourceEditor: () => {},
  mountOpenApiSourceEditor: async (_host: HTMLElement, options: { source: string }) => ({ getValue: () => options.source, setValue() {}, setFilename() {}, focus() {}, layout() {}, destroy() {} }),
}))

// Cold imports of the block runtimes the modules load on first render, kept out of `ready`'s wait on a loaded host.
await Promise.all([import('./charts/runtime'), import('./math/runtime'), import('katex'), import('./diagrams/runtime'), import('./openapi/runtime'),
  import('./openapi/data'), import('./attachments/runtime')])
const controllers: MarkdownEditorController[] = []
afterEach(() => { controllers.splice(0).forEach(controller => controller.destroy()); document.body.replaceChildren(); vi.clearAllMocks() })

function mount(plugin: MarkdownPlugin, content: object[]) {
  let editor!: Editor
  const driver: MarkdownPlugin = { id: 'drafts-driver', apiVersion: 1, create: () => ({ onCreate(context) { editor = context.editor } }) }
  const value = { format: 'atlas-rich', version: 1, plugins: [{ id: plugin.id, version: plugin.schemaVersion ?? 1 }], document: { type: 'doc', content } } as MarkdownRichValue
  const controller = createMarkdownEditorController({ profile: 'rich', plugins: [plugin, driver], source: { kind: 'local', value } })
  controllers.push(controller)
  const host = document.body.appendChild(document.createElement('div'))
  controller.attachView(host, { ariaLabel: 'Koncepty' })
  // The editor's own normalization (the trailing paragraph) lands with the first transaction, and the modules settle after it.
  editor.view.dispatch(editor.state.tr.setMeta('addToHistory', false))
  const announced: string[] = []
  host.addEventListener(markdownInputErrorEvent, event => announced.push((event as CustomEvent<{ message: string }>).detail.message))
  return { controller, editor, host, announced, stored: () => JSON.stringify(controller.getValue()) }
}
/** Modules render lazily at the viewport and jsdom has no layout: a focus on each activates it. */
async function ready(host: HTMLElement) {
  host.querySelectorAll('[data-md-module]').forEach(element => element.dispatchEvent(new FocusEvent('focusin')))
  await vi.waitFor(() => { if (host.querySelector('[data-md-render-state]:not([data-md-render-state=ready]):not([data-md-render-state=fallback])')) throw new Error('rendering') })
  await new Promise(resolve => setTimeout(resolve, 0))
}
const rest = async () => { for (let i = 0; i < 3; i++) { await Promise.resolve(); await new Promise(resolve => setTimeout(resolve, 0)) } }
const input = (field: HTMLInputElement | HTMLTextAreaElement, text: string) => { field.value = text; field.dispatchEvent(new Event('input', { bubbles: true })) }

describe('an interaction that loses the right to write', () => {
  it('chart: closes the data panel, writes nothing, keeps the draft and offers it again', async () => {
    const data = { version: 1, title: 'Příjmy', kind: 'bar', source: null, sourceStatus: null, grid: [['Čtvrtletí', 'Kč'], ['Q1', '1']] }
    const { controller, host, announced, stored } = mount(createChartsPlugin(), [{ type: 'chart', attrs: { data } }])
    await ready(host)
    host.querySelector<HTMLElement>('.md-chart-data')!.click()
    const cell = () => document.querySelector<HTMLInputElement>('.md-chart-inspector tbody input')!
    await vi.waitFor(() => expect(cell()).not.toBeNull())
    input(cell(), '999')
    const before = stored()
    controller.setEditable(false); await rest()
    expect(document.querySelector('.md-chart-inspector')).toBeNull()
    expect(stored()).toBe(before)
    expect(announced).toHaveLength(1)
    controller.setEditable(true); await ready(host)
    host.querySelector<HTMLElement>('.md-chart-data')!.click()
    await vi.waitFor(() => expect(cell()).not.toBeNull())
    expect(cell().value).toBe('999')
  })

  it('chart: a colleague changing the chart under an open panel is shown, not written over', async () => {
    const data = { version: 1, title: 'Příjmy', kind: 'bar', source: null, sourceStatus: null, grid: [['Čtvrtletí', 'Kč'], ['Q1', '1']] }
    const { editor, host, stored } = mount(createChartsPlugin(), [{ type: 'chart', attrs: { data } }])
    await ready(host)
    host.querySelector<HTMLElement>('.md-chart-data')!.click()
    const cell = () => document.querySelector<HTMLInputElement>('.md-chart-inspector tbody input')!
    await vi.waitFor(() => expect(cell()).not.toBeNull())
    input(cell(), '5')
    cell().focus()
    editor.view.dispatch(editor.state.tr.setNodeAttribute(0, 'data', { ...data, title: 'Cizí název' }).setMeta('addToHistory', false))
    await rest()
    document.querySelector<HTMLFormElement>('.md-chart-inspector form')!.requestSubmit()
    await rest()
    expect(stored()).toContain('Cizí název')
    expect(stored()).not.toContain('"5"')
    expect(document.querySelector('.md-chart-inspector [role=alert]')?.textContent).not.toBe('')
  })

  it('math: keeps a formula that does not parse and puts it back into the field', async () => {
    const { controller, host, announced, stored } = mount(createMathPlugin(), [{ type: 'mathBlock', attrs: { data: { version: 1, expression: 'E = mc^2' } } }])
    await ready(host)
    const field = () => host.querySelector<HTMLTextAreaElement>('textarea')!
    input(field(), '   ')
    const before = stored()
    controller.setEditable(false); await rest(); await ready(host)
    expect(stored()).toBe(before)
    expect(announced).toHaveLength(1)
    controller.setEditable(true); await rest(); await ready(host)
    expect(field().value).toBe('   ')
  })

  it('diagram: keeps text the node does not accept and puts it back into the panel', async () => {
    const { controller, host, announced, stored } = mount(createDiagramsPlugin(), [{ type: 'diagram', attrs: { data: { version: 1, source: 'flowchart LR\n  A --> B', caption: 'Postup' } } }])
    await ready(host)
    const caption = () => host.querySelector<HTMLInputElement>('.md-diagram-source-editor input')!
    input(caption(), 'x'.repeat(501))
    const before = stored()
    controller.setEditable(false); await rest(); await ready(host)
    expect(stored()).toBe(before)
    expect(announced).toHaveLength(1)
    controller.setEditable(true); await rest(); await ready(host)
    expect(caption().value).toHaveLength(501)
  })

  it('table of contents: keeps the title being typed', async () => {
    const { controller, host, announced, stored } = mount(createTocPlugin(), [{ type: 'tableOfContents', attrs: { data: { version: 1, title: 'Obsah', minLevel: 1, maxLevel: 3 } } }])
    await ready(host)
    host.querySelector<HTMLElement>('.md-toc-title')!.click()
    input(host.querySelector<HTMLInputElement>('.md-toc-title-input')!, 'Osnova')
    const before = stored()
    controller.setEditable(false); await rest(); await ready(host)
    expect(host.querySelector('.md-toc-title-input')).toBeNull()
    expect(stored()).toBe(before)
    expect(announced).toHaveLength(1)
    controller.setEditable(true); await rest(); await ready(host)
    host.querySelector<HTMLElement>('.md-toc-title')!.click()
    expect(host.querySelector<HTMLInputElement>('.md-toc-title-input')!.value).toBe('Osnova')
  })

  it('OpenAPI: keeps the source dialog\'s file name', async () => {
    HTMLDialogElement.prototype.showModal = function () { this.setAttribute('open', '') }
    HTMLDialogElement.prototype.close = function () { this.removeAttribute('open') }
    const data = { version: 1, filename: 'openapi.yaml', source: "openapi: 3.1.0\ninfo:\n  title: Zakázky\n  version: '1.0'\npaths: {}\n" }
    const { controller, host, announced, stored } = mount(createOpenApiPlugin(), [{ type: 'openapi', attrs: { data } }])
    await ready(host)
    host.querySelector<HTMLElement>('.md-openapi-edit')!.click()
    const name = () => document.querySelector<HTMLInputElement>('.md-openapi-source-dialog input')!
    input(name(), 'jiny.yaml')
    const before = stored()
    controller.setEditable(false); await rest()
    expect(document.querySelector('.md-openapi-source-dialog')).toBeNull()
    expect(stored()).toBe(before)
    expect(announced).toHaveLength(1)
    controller.setEditable(true); await ready(host)
    host.querySelector<HTMLElement>('.md-openapi-edit')!.click()
    expect(name().value).toBe('jiny.yaml')
  })

  it('attachment: calls off an upload in flight, so no file is left behind', async () => {
    let seen: AbortSignal | undefined
    const upload: NonNullable<AttachmentsPluginOptions['upload']> = { upload: (_file, context) => new Promise((_resolve, reject) => { seen = context.signal; context.signal.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError'))) }) }
    const data = { version: 1, name: 'Zpráva.pdf', url: '/files/a.pdf' }
    const { controller, host, stored } = mount(createAttachmentsPlugin({ upload }), [{ type: 'attachment', attrs: { data } }])
    await ready(host)
    const drop = new Event('drop', { bubbles: true, cancelable: true })
    Object.defineProperty(drop, 'dataTransfer', { value: { files: [new File(['x'], 'nova.pdf', { type: 'application/pdf' })], types: ['Files'] } })
    host.querySelector('.md-attachment')!.dispatchEvent(drop)
    await vi.waitFor(() => expect(seen).toBeDefined())
    expect(seen!.aborted).toBe(false)
    const before = stored()
    controller.setEditable(false); await rest()
    expect(seen!.aborted).toBe(true)
    expect(stored()).toBe(before)
  })
})
