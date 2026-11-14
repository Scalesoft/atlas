// @vitest-environment jsdom
import { expect, it, vi } from 'vitest'
import { createMarkdownEditorController, type MarkdownModuleNodeContext } from '../../core'
import { createOpenApiPlugin, defaultOpenApi, insertOpenApi, type OpenApiData } from './index'

const source = "openapi: 3.1.0\ninfo:\n  title: Orders API\n  version: '2.0'\npaths: {}\n"

// The block renders only after its lazy runtime loads. A cold import can outlast vi.waitFor's 1 s default
// under machine load, so load it once up front; the waits below then only cover the render.
await import('./runtime')

it.each([
  ['default options', () => createOpenApiPlugin()],
  ['host options', () => createOpenApiPlugin({ uploaderName: 'Jana Nováková', allowRequests: false, proxyUrl: 'https://proxy.example.test' })],
])('dispatches the exported insertOpenApi command for a plugin created with %s', (_label, create) => {
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } }, plugins: [create()] })
  const container = document.createElement('div'); document.body.append(container)
  try {
    controller.attachView(container, { ariaLabel: 'OpenAPI' })
    const data: OpenApiData = { ...defaultOpenApi, filename: 'orders.yaml', source }
    expect(controller.dispatch(insertOpenApi(data))).toBe(true)
    const projection = controller.getProjection()
    expect(projection.text).toContain('Orders API')
    expect(projection.rich.plugins).toContainEqual(expect.objectContaining({ id: 'openapi' }))
    expect(JSON.stringify(projection.rich.document)).toContain('"type":"openapi"')
  } finally { controller.destroy(); container.remove() }
})

it('hands openDetail a context that lives as long as the block, not one render', async () => {
  const contexts: MarkdownModuleNodeContext<OpenApiData>[] = []
  const openDetail = (context: MarkdownModuleNodeContext<OpenApiData>) => { contexts.push(context); return true }
  const value = { format: 'atlas-rich' as const, version: 1 as const, document: { type: 'doc' as const, content: [{ type: 'paragraph' }] } }
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins: [createOpenApiPlugin({ openDetail })] })
  const container = document.createElement('div'); document.body.append(container)
  try {
    controller.attachView(container, { ariaLabel: 'OpenAPI' })
    controller.dispatch(insertOpenApi({ ...defaultOpenApi, filename: 'orders.yaml', source }))
    const open = () => [...container.querySelectorAll<HTMLButtonElement>('.md-openapi-actions button')].find(item => /Dokumentace/.test(item.textContent ?? ''))
    await vi.waitFor(() => expect(open()).toBeTruthy())
    open()!.click()
    expect(contexts).toHaveLength(1)
    const context = contexts[0]!
    expect(context.anchor).toBe(container.querySelector('[data-md-module="openapi"]'))
    const changes: boolean[] = []
    context.onEditableChange(editable => changes.push(editable))
    controller.setEditable(false)
    await vi.waitFor(() => expect(changes).toEqual([false]))
    controller.setEditable(true)
    await vi.waitFor(() => expect(changes).toEqual([false, true]))
    expect(context.signal.aborted).toBe(false)
    context.update!({ ...context.data, source: source.replace('Orders API', 'Renamed API') })
    expect(controller.getProjection().text).toContain('Renamed API')
    // Removing the block ends the detail.
    controller.setValue(value)
    expect(context.signal.aborted).toBe(true)
  } finally { controller.destroy(); container.remove() }
})
