// @vitest-environment jsdom
import { expect, it, vi } from 'vitest'
import { createMarkdownEditorController } from '../../core'
import { createOpenApiPlugin, defaultOpenApi, insertOpenApi } from './index'
import { isSpecFileName } from './cardEdit'

const source = "openapi: 3.1.0\ninfo:\n  title: Orders API\n  version: '2.0'\npaths: {}\n"
await import('./runtime')

function setup(options = {}) {
  const value = { format: 'atlas-rich' as const, version: 1 as const, document: { type: 'doc' as const, content: [{ type: 'paragraph' }] } }
  const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value }, plugins: [createOpenApiPlugin(options)] })
  const container = document.createElement('div'); document.body.append(container)
  controller.attachView(container, { ariaLabel: 'OpenAPI' })
  controller.dispatch(insertOpenApi({ ...defaultOpenApi, filename: 'orders.yaml', source }))
  return { controller, container }
}
const card = (c: HTMLElement) => c.querySelector<HTMLElement>('.md-openapi-card')!

it('accepts yaml and json names only', () => {
  expect(['a.yaml', 'a.YML', 'a.json'].every(isSpecFileName)).toBe(true)
  expect(isSpecFileName('a.txt')).toBe(false)
})

it('edits mode and filename on the card at once, and replaces the spec from a dropped file', async () => {
  const { controller, container } = setup({ uploaderName: 'Jana' })
  try {
    await vi.waitFor(() => expect(card(container)).toBeTruthy())
    expect(card(container).textContent).toContain('Upravit specifikaci')
    expect(card(container).textContent).toContain('Nahrát soubor')

    card(container).querySelector<HTMLButtonElement>('[data-mode=download]')!.click()
    await vi.waitFor(() => expect(card(container).querySelector('[data-mode=download]')!.getAttribute('aria-pressed')).toBe('true'))
    expect(JSON.stringify(controller.getProjection().rich.document)).toContain('"mode":"download"')

    card(container).querySelector<HTMLElement>('.md-openapi-filename')!.click()
    const field = card(container).querySelector<HTMLInputElement>('.md-openapi-rename')!
    field.value = 'shop.yaml'; field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    await vi.waitFor(() => expect(card(container).querySelector('.md-openapi-filename')!.textContent).toBe('shop.yaml'))

    field.value = ''
    card(container).querySelector<HTMLElement>('.md-openapi-filename')!.click()
    const bad = card(container).querySelector<HTMLInputElement>('.md-openapi-rename')!
    bad.value = 'a/b.yaml'; bad.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    expect(card(container).querySelector('.md-openapi-card-error')!.textContent).not.toBe('')

    const file = new File([source.replace('Orders API', 'Dropped API')], 'dropped.yaml')
    const drop = new Event('drop', { bubbles: true, cancelable: true }) as Event & { dataTransfer?: unknown }
    drop.dataTransfer = { types: ['Files'], files: [file] }
    card(container).dispatchEvent(drop)
    await vi.waitFor(() => expect(controller.getProjection().text).toContain('Dropped API'))
    expect(JSON.stringify(controller.getProjection().rich.document)).toContain('"uploadedBy":"Jana"')

    const bin = new Event('drop', { bubbles: true, cancelable: true }) as Event & { dataTransfer?: unknown }
    bin.dataTransfer = { types: ['Files'], files: [new File(['x'], 'notes.txt')] }
    card(container).dispatchEvent(bin)
    await vi.waitFor(() => expect(card(container).querySelector('.md-openapi-card-error')!.textContent).toContain('YAML'))
    expect(controller.getProjection().text).toContain('Dropped API')
  } finally { controller.destroy(); container.remove() }
})

it('shows no editing controls on a readonly document', async () => {
  const { controller, container } = setup()
  try {
    await vi.waitFor(() => expect(card(container)).toBeTruthy())
    controller.setEditable(false)
    await vi.waitFor(() => expect(card(container).textContent).not.toContain('Upravit specifikaci'))
    // The row holds its place for a reader (same size as a writer's card), inert and not shown.
    expect(card(container).querySelector('.md-openapi-modes')?.hasAttribute('inert')).toBe(true)
  } finally { controller.destroy(); container.remove() }
})
