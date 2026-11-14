// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DOMParser as PMDOMParser, DOMSerializer } from '@tiptap/pm/model'
import {
  createSecretsPlugin, insertSecretReference, insertInlineSecretReference, updateSecretReferencePresentation,
  type SecretCopyContext, type SecretDiscovery, type SecretsPluginOptions, type SecretField, type SecretOperationContext, type SecretRelease, type SecretRevealContext,
} from './index'
import { createApp, h, nextTick } from 'vue'
import { createMarkdownEditorController, migrateMarkdownValue, type MarkdownRichValue } from '../../core'
import MarkdownRenderer from '../../vue/MarkdownRenderer.vue'
import { configureMarkdownEditorView } from '../../vue/controllerView'

const id = 'd847b3af-4a87-4e97-aab8-a74a4cf48c89'
const otherId = 'ccff88d6-437d-4665-83a8-4c179ad8a523'
const value = (attrs: Record<string, unknown> = { secretId: id }): MarkdownRichValue => ({
  format: 'atlas-rich', version: 1, plugins: [{ id: 'secrets', version: 1 }],
  document: { type: 'doc', content: [{ type: 'secretReference', attrs }, { type: 'paragraph' }] },
} as MarkdownRichValue)
const inlineValue = (attrs: Record<string, unknown> = { secretId: id }): MarkdownRichValue => ({
  format: 'atlas-rich', version: 1, plugins: [{ id: 'secrets', version: 2 }],
  document: { type: 'doc', content: [{ type: 'paragraph', content: [
    { type: 'text', text: 'Password ' }, { type: 'secretInlineReference', attrs }, { type: 'text', text: ' stays private.' },
  ] }] },
} as MarkdownRichValue)
const disposers: (() => void)[] = []
beforeEach(() => { vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null) })
afterEach(() => { disposers.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); vi.useRealTimers(); vi.restoreAllMocks() })
// Warm the lazily loaded invisible ink at file load so its cold import is not charged to a test timeout or waitFor on a
// loaded host; `flush` then only lets the settled import land.
await import('../../concealment')
// The invisible ink loads lazily with the first reference; let that import settle too.
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); await import('../../concealment'); for (let i = 0; i < 12; i++) await Promise.resolve(); await nextTick() }
function host() { const element = document.createElement('div'); document.body.append(element); return element }
/** A fake host: values are synthetic canaries in host-created elements, cleared on dispose. */
function service() {
  const disposed = vi.fn()
  return {
    disposed,
    discover: vi.fn(async (_context: SecretOperationContext): Promise<SecretDiscovery> => 'available'),
    requestAccess: vi.fn(async (_context: SecretOperationContext) => {}),
    reveal: vi.fn(async ({ fields, ownerDocument }: SecretRevealContext): Promise<SecretRelease> => {
      const values: Partial<Record<SecretField, HTMLElement>> = {}
      for (const field of fields) { const element = ownerDocument.createElement('span'); element.textContent = `CANARY-${field}`; values[field] = element }
      return { values, dispose: () => { disposed(); for (const element of Object.values(values)) element!.textContent = '' } }
    }),
    copy: vi.fn(async (_context: SecretCopyContext) => {}),
    subscribeRevocation: vi.fn((_id: string, _revoke: () => void) => () => {}),
  }
}
async function reader(services = service(), documentValue = value(), options: Omit<SecretsPluginOptions, 'services'> = {}) {
  const element = host()
  const plugin = createSecretsPlugin({ services, ...options })
  const app = createApp({ render: () => h(MarkdownRenderer, {
    value: documentValue, profile: 'rich', ariaLabel: 'Secrets', plugins: [plugin],
  }) })
  app.mount(element); disposers.push(() => app.unmount()); await flush()
  return { element, app, services, plugin }
}
const visibleButtons = (element: HTMLElement) => [...element.querySelectorAll('button')].filter(item => !item.closest('[hidden]'))
function click(element: HTMLElement, label: string) {
  const button = visibleButtons(element).find(item => item.textContent === label || item.getAttribute('aria-label') === label)
  expect(button, label).toBeDefined(); button!.click()
}
const surfaces = (element: HTMLElement) => [...element.querySelectorAll<HTMLElement>('[data-concealed-surface]')]
const requestPanel = () => document.querySelector<HTMLElement>('.md-secret-request')
/** Types a reason into the open request panel and presses Odeslat. */
async function sendReason(reason = '') {
  const panel = requestPanel()
  expect(panel, 'request panel').not.toBeNull()
  panel!.querySelector('textarea')!.value = reason
  const send = [...panel!.querySelectorAll('button')].find(button => button.textContent === 'Odeslat')!
  send.click(); await flush()
}

async function armRequest(element: HTMLElement) {
  vi.useFakeTimers()
  // The explicit request button is on the password surface at rest; no gesture is needed.
  expect(element.querySelector('[data-concealed-surface]')).not.toBeNull()
  expect(visibleButtons(element).some(button => !button.hidden && button.textContent === 'Požádat o přístup')).toBe(true)
}

describe('secrets persisted model', () => {
  it('updates only a selected reference presentation in its own undo step', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() }), {
      id: 'reference-selection-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) { editor.commands.setNodeSelection(10) } }),
    }], source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    const original = controller.getValue()
    const attrs = () => JSON.parse(JSON.stringify(controller.getProjection().rich)).document.content[0].content[1].attrs
    expect(controller.dispatch(updateSecretReferencePresentation({ fields: ['password', 'username'], showCopy: false, showDetails: true }))).toBe(true)
    // Inline references show several fields too; the list is stored canonically.
    expect(attrs()).toEqual({ secretId: id, field: 'password', fields: ['username', 'password'], showCopy: false, showDetails: true, autoHideSeconds: 60 })
    expect(controller.dispatch(updateSecretReferencePresentation({ field: 'url' }))).toBe(true)
    controller.dispatch({ type: 'undo' })
    expect(attrs().field).toBe('password')
    controller.dispatch({ type: 'undo' })
    expect(controller.getValue()).toEqual(original)
    expect(controller.dispatch(updateSecretReferencePresentation({ secretId: otherId } as never))).toBe(false)
    expect(controller.dispatch(updateSecretReferencePresentation({ field: 'ciphertext' } as never))).toBe(false)
    expect(controller.dispatch(updateSecretReferencePresentation({ showCopy: 'true' } as never))).toBe(false)
    for (const fields of [[], ['url', 'url'], ['ciphertext'], 'password'])
      expect(controller.dispatch(updateSecretReferencePresentation({ fields } as never))).toBe(false)
  })

  it('migrates v1 block references to v4 without changing their document tree', () => {
    const original = value()
    const migrated = migrateMarkdownValue(original, 'rich', [createSecretsPlugin({ services: service() })])
    expect(migrated.plugins).toEqual([{ id: 'secrets', version: 4 }])
    expect(migrated.document).toEqual(original.document)
    expect(original.plugins).toEqual([{ id: 'secrets', version: 1 }])
  })

  it('inserts canonical inline references between sentence text with independent undo steps', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() }), {
      id: 'inline-selection-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) { editor.commands.setTextSelection(10) } }),
    }],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1,
        document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Password  stays private.' }] }] } } } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    for (const attrs of [{ secretId: 'bad' }, { secretId: id.toUpperCase() }, { secretId: id, password: 'LEAK' }])
      expect(controller.dispatch(insertInlineSecretReference(attrs), { replaceRange: { from: 10, to: 10 } })).toBe(false)
    expect(controller.dispatch(insertInlineSecretReference({ secretId: id }), { replaceRange: { from: 10, to: 10 } })).toBe(true)
    expect(controller.getValue().document).toMatchObject(inlineValue().document)
    expect(controller.dispatch(insertInlineSecretReference({ secretId: otherId }))).toBe(true)
    controller.dispatch({ type: 'undo' })
    expect(controller.getValue().document).toMatchObject(inlineValue().document)
    controller.dispatch({ type: 'undo' })
    expect(JSON.stringify(controller.getValue())).not.toContain(id)
    controller.dispatch({ type: 'redo' })
    expect(controller.getValue().document).toMatchObject(inlineValue().document)
  })

  it('omits inline reference IDs and controls from HTML, clipboard and text; Markdown carries only the reference', () => {
    let html = ''
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() }), {
      id: 'inline-serializer-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) {
        const exported = document.createElement('div')
        exported.append(DOMSerializer.fromSchema(editor.schema).serializeFragment(editor.state.doc.content))
        html = exported.innerHTML
      } }),
    }], source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    expect(html).toContain('data-md-secret-inline=""')
    expect(html).not.toContain(id); expect(html).not.toContain('secretId'); expect(html).not.toContain('button')
    expect(controller.getProjection()).toMatchObject({ markdown: `Password :secret{secretId=${id}} stays private.`, text: 'Password  stays private.', markdownLossy: false })
    expect(controller.getValue().document).toMatchObject(inlineValue().document)
  })

  it('a copy pastes back as the same reference in this session; HTML from elsewhere creates none (J3.7)', () => {
    let slice: unknown, foreign: unknown
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() }), {
      id: 'paste-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) {
        const exported = document.createElement('div')
        exported.append(DOMSerializer.fromSchema(editor.schema).serializeFragment(editor.state.doc.content))
        const parse = (html: string) => { const box = document.createElement('div'); box.innerHTML = html; return PMDOMParser.fromSchema(editor.schema).parse(box).toJSON() }
        slice = parse(exported.innerHTML)
        foreign = parse('<p><span data-md-secret-inline="" data-md-secret-slot="0000000000000000"></span></p>')
      } }),
    }], source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    expect(JSON.stringify(slice)).toContain(id)
    expect(JSON.stringify(foreign)).not.toContain('secretInlineReference')
  })

  it('rejects malformed rich references before discovery', () => {
    const services = service()
    const invalidAttrs: Record<string, unknown>[] = [
      { secretId: 'bad' }, { secretId: id.toUpperCase() }, { secretId: id, label: 'LEAK' },
      { secretId: id, field: 'ciphertext' }, { secretId: id, showCopy: 'true' }, { secretId: id, showDetails: 'false' },
      { secretId: id, fields: [] }, { secretId: id, fields: ['url', 'url'] }, { secretId: id, password: 'LEAK' },
    ]
    for (const attrs of invalidAttrs) {
      for (const documentValue of [inlineValue(attrs), value(attrs)])
        expect(() => createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services })],
          source: { kind: 'local', value: documentValue } })).toThrow()
    }
    expect(services.discover).not.toHaveBeenCalled()
  })

  it('stores only canonical references and rejects malformed or extra command attributes', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() })],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    for (const args of [{ secretId: 'bad' }, { secretId: id.toUpperCase() }, { secretId: id, password: 'LEAK' }, { secretId: id, label: 'LEAK' },
      { secretId: id, field: 'ciphertext' }, { secretId: id, showCopy: 1 }, { secretId: id, showDetails: null }]) {
      expect(controller.dispatch(insertSecretReference(args as never))).toBe(false)
      expect(controller.dispatch(insertInlineSecretReference(args as never))).toBe(false)
    }
    expect(controller.dispatch(insertSecretReference({ secretId: id, fields: ['url', 'password'] }))).toBe(true)
    const json = JSON.stringify(controller.getProjection().rich)
    expect(JSON.parse(json).document.content[0].attrs).toEqual({ secretId: id, field: 'password', fields: ['password', 'url'], showCopy: true, showDetails: false, autoHideSeconds: 60 })
    expect(json).not.toContain('LEAK')
    expect(controller.getProjection()).toMatchObject({ markdownLossy: false })
    expect(controller.getProjection().markdown.trim()).toBe(`::secret{secretId=${id} fields="password url"}`)
    expect(controller.getProjection().text).not.toContain(id)
  })

  it('keeps two rapid reference insertions in independent undo steps', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() })],
      source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } } })
    disposers.push(() => controller.destroy()); controller.attachView(host(), { ariaLabel: 'Edit' })
    controller.dispatch(insertSecretReference({ secretId: id }))
    controller.dispatch(insertSecretReference({ secretId: otherId }))
    controller.dispatch({ type: 'undo' })
    expect(JSON.stringify(controller.getValue())).toContain(id)
    expect(JSON.stringify(controller.getValue())).not.toContain(otherId)
    controller.dispatch({ type: 'undo' })
    expect(JSON.stringify(controller.getValue())).not.toContain(id)
  })

  it('HTML and clipboard serialization omit identifiers even while discovery is pending', () => {
    const services = service()
    services.discover.mockImplementation(() => new Promise(() => {}))
    let html = ''
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services }), {
      id: 'serializer-probe', apiVersion: 1, create: () => ({ onCreate({ editor }) {
        const exported = document.createElement('div')
        exported.append(DOMSerializer.fromSchema(editor.schema).serializeFragment(editor.state.doc.content))
        html = exported.innerHTML
      } }),
    }], source: { kind: 'local', value: value() } })
    disposers.push(() => controller.destroy())
    controller.attachView(host(), { ariaLabel: 'Edit' })
    expect(html).not.toContain(id); expect(html).not.toContain('secretId')
    expect(controller.getProjection().markdown.trim()).toBe(`::secret{secretId=${id}}`)
    expect(controller.getProjection().text.trim()).toBe('')
    expect(services.reveal).not.toHaveBeenCalled()
  })
})

describe('secret release in place', () => {
  it('uncovers every shown inline field with one explicit release, never on hover or tap', async () => {
    vi.useFakeTimers()
    const services = service()
    const documentValue = inlineValue({ secretId: id, fields: ['password', 'username'] })
    const { element } = await reader(services, documentValue)
    const surface = element.querySelector<HTMLElement>('p [data-concealed-surface]')!
    expect(surface.dataset.concealedLayout).toBe('inline')
    expect(surfaces(element)).toHaveLength(1)
    surface.dispatchEvent(new Event('pointerenter')); surface.dispatchEvent(new Event('pointermove')); surface.click(); await flush()
    expect(services.reveal).not.toHaveBeenCalled()
    click(surface, 'Odkrýt'); await flush()
    expect(services.reveal).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ secretId: id, fields: ['username', 'password'], ownerDocument: document }))
    expect(surface.textContent).toContain('CANARY-username'); expect(surface.textContent).toContain('CANARY-password')
    // The sentence stays intact and the document never learns the values.
    expect(element.querySelector('p')!.textContent).toMatch(/^Password .* stays private\.$/)
    expect(JSON.stringify(documentValue)).not.toContain('CANARY')
    click(surface, 'Skrýt')
    await vi.advanceTimersByTimeAsync(800)
    expect(element.textContent).not.toContain('CANARY'); expect(services.disposed).toHaveBeenCalledOnce()
    expect(element.innerHTML).not.toContain(id)
  })

  it('copies a single field from the rail without revealing, and each of several fields once revealed', async () => {
    const services = service()
    const { element } = await reader(services, { ...inlineValue(), document: { type: 'doc', content: [
      { type: 'paragraph', content: [{ type: 'secretInlineReference', attrs: { secretId: id } }] },
      { type: 'secretReference', attrs: { secretId: otherId, fields: ['username', 'url'] } },
    ] } } as MarkdownRichValue)
    const [single, card] = surfaces(element)
    click(single!, 'Kopírovat'); await flush()
    expect(services.copy).toHaveBeenLastCalledWith(expect.objectContaining({ secretId: id, field: 'password' }))
    expect(services.reveal).not.toHaveBeenCalled()
    // Several fields: no rail copy, one copy button beside each released value.
    expect(card!.querySelector('[data-concealed-copy]')).toBeNull()
    click(card!, 'Odkrýt'); await flush()
    expect(card!.querySelectorAll('[data-secret-copy]')).toHaveLength(2)
    card!.querySelector<HTMLButtonElement>('[data-secret-copy="url"]')!.click(); await flush()
    expect(services.copy).toHaveBeenLastCalledWith(expect.objectContaining({ secretId: otherId, field: 'url' }))
    expect(card!.querySelector('[data-secret-copy="url"]')!.getAttribute('aria-label')).toBe('Zkopírováno')
    expect(element.textContent).not.toContain('undefined')
  })

  it('omits copy entirely when disabled and marks a field the entry lacks', async () => {
    const services = service()
    services.reveal.mockImplementation(async ({ ownerDocument }) => {
      const username = ownerDocument.createElement('span'); username.textContent = 'CANARY-username'
      return { values: { username } }
    })
    const { element } = await reader(services, value({ secretId: id, fields: ['username', 'url'], showCopy: false }))
    click(element, 'Odkrýt'); await flush()
    expect(element.querySelector('[data-secret-copy]')).toBeNull()
    expect(element.querySelector('[data-field="url"] [data-missing]')!.textContent).toBe('—')
  })

  it.each([
    ['plaintext', { values: { password: 'LEAK' } }],
    ['a missing value map', { password: 'LEAK' }],
    ['a node from another document', 'foreign'],
  ])('refuses %s and asks the host to dispose it', async (_name, result) => {
    const services = service()
    const dispose = vi.fn()
    services.reveal.mockImplementation(async () => {
      if (result !== 'foreign') return { ...(result as object), dispose } as never
      const foreign = document.implementation.createHTMLDocument('x').createElement('span'); foreign.textContent = 'LEAK'
      return { values: { password: foreign }, dispose }
    })
    const { element } = await reader(services)
    click(element, 'Odkrýt'); await flush()
    expect(element.textContent).not.toContain('LEAK')
    expect(dispose).toHaveBeenCalledOnce()
    expect(element.querySelector('[data-concealed-surface]')!.getAttribute('data-concealed-state')).toBe('concealed')
  })

  it('shows field names on a card in canonical order over one surface for every value', async () => {
    const services = service()
    const { element } = await reader(services, value({ secretId: id, fields: ['url', 'username'] }))
    const labels = [...element.querySelectorAll('.md-secret-card__label')].map(label => label.textContent)
    expect(labels).toEqual(['Uživatelské jméno', 'URL'])
    expect(surfaces(element)).toHaveLength(1)
    expect(element.textContent).not.toContain('CANARY')
    click(element, 'Odkrýt'); await flush()
    expect(services.reveal).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ fields: ['username', 'url'] }))
    expect([...element.querySelectorAll('.md-secret-values__item')].map(item => item.textContent)).toEqual(['CANARY-username', 'CANARY-url'])
  })

  it('expands a revealed card to every field in one further release and collapses back', async () => {
    const services = service()
    const { element } = await reader(services, value({ secretId: id, fields: ['password'], showDetails: true }))
    const toggle = () => element.querySelector<HTMLButtonElement>('.md-secret-card__toggle')!
    expect(toggle().textContent).toBe('Podrobnosti'); expect(toggle().getAttribute('aria-expanded')).toBe('false')
    click(element, 'Odkrýt'); await flush()
    toggle().click(); await flush()
    expect(toggle().textContent).toBe('Sbalit'); expect(toggle().getAttribute('aria-expanded')).toBe('true')
    expect([...element.querySelectorAll('.md-secret-card__label')].map(label => label.textContent)).toEqual(['Název', 'Cesta', 'Uživatelské jméno', 'Heslo', 'URL', 'Poznámka'])
    expect(services.reveal).toHaveBeenLastCalledWith(expect.objectContaining({ fields: ['title', 'path', 'username', 'password', 'url', 'notes'] }))
    expect(element.textContent).toContain('CANARY-title')
    expect(services.disposed).toHaveBeenCalledOnce()
    toggle().click(); await flush()
    expect(element.querySelectorAll('.md-secret-card__label')).toHaveLength(1)
  })

  it('shows the KeePass path and notes, and fits the notes row to the released text', async () => {
    const services = service()
    const { element } = await reader(services, value({ secretId: id, fields: ['notes', 'path', 'password'] }))
    const body = element.querySelector<HTMLElement>('.md-secret-card__body')!
    expect([...element.querySelectorAll('.md-secret-card__label')].map(label => label.textContent)).toEqual(['Cesta', 'Heslo', 'Poznámka'])
    // Concealed, the notes row has a fixed synthetic height, never one derived from the text.
    expect(body.style.getPropertyValue('--md-secret-template')).toContain('var(--md-secret-notes-rest)')
    expect(body.style.getPropertyValue('--md-secret-notes-row')).toBe('')
    click(element, 'Odkrýt'); await flush()
    expect(services.reveal).toHaveBeenLastCalledWith(expect.objectContaining({ fields: ['path', 'password', 'notes'] }))
    expect(element.querySelector('[data-field="notes"] .md-secret-values__value')!.textContent).toBe('CANARY-notes')
    expect(element.querySelectorAll('[data-secret-copy]')).toHaveLength(3)
  })

  it('offers no card toggle when every field is already shown or Details is off', async () => {
    const { element } = await reader(service(), value({ secretId: id, fields: ['notes', 'url', 'password', 'username', 'path', 'title'], showDetails: true }))
    expect(element.querySelector('.md-secret-card__toggle')).toBeNull()
    const other = await reader(service(), value({ secretId: id }))
    expect(other.element.querySelector('.md-secret-card__toggle')).toBeNull()
  })

  it('unfolds an inline entry into a card with every field and folds it on Escape or outside press', async () => {
    vi.useFakeTimers()
    const services = service()
    const { element } = await reader(services, inlineValue({ secretId: id, fields: ['username', 'password'], showDetails: true }))
    const entry = element.querySelector<HTMLElement>('.md-secret-entry')!
    const details = entry.querySelector<HTMLButtonElement>('[data-concealed-details]')!
    click(entry, 'Odkrýt'); await flush()
    details.click(); await vi.advanceTimersByTimeAsync(50)
    expect(details.getAttribute('aria-expanded')).toBe('true')
    const panel = entry.querySelector<HTMLElement>('.md-secret-popover')!
    expect([...panel.querySelectorAll('.md-secret-card__label')].map(label => label.textContent)).toEqual(['Název', 'Cesta', 'Uživatelské jméno', 'Heslo', 'URL', 'Poznámka'])
    // Already released values stay released: the card uncovers all fields at once.
    expect(services.reveal).toHaveBeenLastCalledWith(expect.objectContaining({ fields: ['title', 'path', 'username', 'password', 'url', 'notes'] }))
    panel.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    expect(details.getAttribute('aria-expanded')).toBe('false')
    await vi.advanceTimersByTimeAsync(900)
    expect(entry.querySelector('.md-secret-popover')).toBeNull()
    details.click(); await vi.advanceTimersByTimeAsync(50)
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    await vi.advanceTimersByTimeAsync(900)
    expect(entry.querySelector('.md-secret-popover')).toBeNull()
    expect(element.textContent).not.toContain('CANARY-title')
  })

  it('covers revealed values again at the latest after autoHideSeconds', async () => {
    vi.useFakeTimers()
    const services = service()
    const { element } = await reader(services, value({ secretId: id, autoHideSeconds: 5 }))
    click(element, 'Odkrýt'); await flush()
    element.querySelector('[data-concealed-surface]')!.dispatchEvent(new Event('pointermove'))
    await vi.advanceTimersByTimeAsync(5000)
    expect(element.textContent).toContain('CANARY-password')
    await vi.advanceTimersByTimeAsync(800)
    expect(element.textContent).not.toContain('CANARY'); expect(services.disposed).toHaveBeenCalledOnce()
  })

  it('gives the host time for a second factor, then aborts the release', async () => {
    vi.useFakeTimers()
    const services = service()
    services.reveal.mockImplementation(() => new Promise(() => {}))
    const { element } = await reader(services)
    click(element, 'Odkrýt')
    const signal = services.reveal.mock.calls[0]![0].signal
    await vi.advanceTimersByTimeAsync(170_000); expect(signal.aborted).toBe(false)
    await vi.advanceTimersByTimeAsync(10_000); expect(signal.aborted).toBe(true)
    for (const revealTimeoutMs of [500, 600001, Number.NaN]) expect(() => createSecretsPlugin({ services, revealTimeoutMs })).toThrow(RangeError)
  })
})

describe('secret lifecycle and access', () => {
  it('hidden, unknown and failed discovery render no content and never release', async () => {
    for (const result of ['hidden', 'bogus', 'error']) {
      const services = service()
      if (result === 'error') services.discover.mockRejectedValue(new Error('SECRET_ERROR'))
      else services.discover.mockResolvedValue(result as 'hidden')
      const { element, app } = await reader(services)
      expect(element.querySelector('button')).toBeNull()
      expect(element.textContent).toBe('')
      expect(element.innerHTML).not.toContain('SECRET_ERROR')
      expect(services.reveal).not.toHaveBeenCalled()
      app.unmount(); disposers.pop()
    }
  })

  it('request access stays separate from release and hides error payloads', async () => {
    const services = service(); services.discover.mockResolvedValue('requestable')
    services.requestAccess.mockRejectedValue(new Error('VALUE_AND_ERROR'))
    const { element } = await reader(services)
    expect(visibleButtons(element).some(button => button.textContent === 'Odkrýt')).toBe(false)
    expect(element.textContent).toContain('Hodnoty jsou skryté')
    await armRequest(element)
    expect(services.requestAccess).not.toHaveBeenCalled()
    click(element, 'Požádat o přístup'); await flush()
    // The anchored panel asks for a reason first; nothing is sent while it is open.
    expect(services.requestAccess).not.toHaveBeenCalled()
    expect(requestPanel()!.getAttribute('aria-label')).toBe('Žádost o přístup k heslu')
    await sendReason('  Nasazení VPN  ')
    expect(services.requestAccess).toHaveBeenCalledWith({ secretId: id, signal: expect.any(AbortSignal), reason: 'Nasazení VPN' })
    expect(requestPanel()).toBeNull()
    expect(services.reveal).not.toHaveBeenCalled()
    expect(element.textContent).not.toContain('VALUE_AND_ERROR')
  })

  it.each([value, inlineValue])('shows a pending server request without offering another request or release (%#)', async documentValue => {
    vi.useFakeTimers()
    const services = service()
    services.discover.mockResolvedValue({ access: 'requestable', requestPending: true })
    const { element } = await reader(services, documentValue())
    expect(element.textContent).toContain('Žádost odeslána · čeká na schválení')
    element.querySelector('[data-concealed-surface]')!.dispatchEvent(new Event('pointerenter'))
    await vi.advanceTimersByTimeAsync(1200)
    expect(visibleButtons(element).some(button => !button.hidden && button.textContent === 'Požádat o přístup')).toBe(false)
    expect(services.requestAccess).not.toHaveBeenCalled()
    expect(services.reveal).not.toHaveBeenCalled()
  })

  it('turns a whole card into one requestable surface and prevents duplicate submission', async () => {
    const services = service(); services.discover.mockResolvedValue('requestable')
    const { element } = await reader(services, value({ secretId: id, fields: ['username', 'password'], showDetails: true }))
    expect(element.querySelectorAll('[data-concealed-access="denied"]')).toHaveLength(1)
    expect(element.querySelector('.md-secret-card__toggle')).toBeNull()
    await armRequest(element)
    const request = visibleButtons(element).find(button => button.textContent === 'Požádat o přístup')!
    request.click(); request.click(); await flush()
    expect(document.querySelectorAll('.md-secret-request')).toHaveLength(1)
    await sendReason(); request.click(); await flush()
    expect(services.requestAccess).toHaveBeenCalledOnce()
    expect(services.requestAccess.mock.calls[0]![0]).toMatchObject({ reason: '' })
    expect(element.querySelectorAll('[data-concealed-access="pending"]')).toHaveLength(1)
    expect(element.textContent).toContain('Žádost odeslána · čeká na schválení')
    expect(services.reveal).not.toHaveBeenCalled()
  })

  it('ignores discovery completions after unmount', async () => {
    const services = service()
    let finish!: (value: 'available') => void
    services.discover.mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const { element, app } = await reader(services)
    const signal = services.discover.mock.calls[0]![0].signal
    app.unmount(); disposers.pop(); finish('available'); await flush()
    expect(signal.aborted).toBe(true)
    expect(element.textContent).toBe('')
  })

  it('cleans subscriptions and aborts a late access request after reader removal', async () => {
    const services = service(); services.discover.mockResolvedValue('requestable')
    let complete!: () => void
    let requestSignal!: AbortSignal
    const cleanup = vi.fn()
    services.subscribeRevocation.mockReturnValue(cleanup)
    services.requestAccess.mockImplementation(context => { requestSignal = context.signal; return new Promise<void>(resolve => { complete = resolve }) })
    const { element, app } = await reader(services)
    await armRequest(element)
    click(element, 'Požádat o přístup'); await flush()
    await sendReason('late')
    app.unmount(); disposers.pop(); complete(); await flush()
    expect(requestSignal.aborted).toBe(true)
    expect(cleanup).toHaveBeenCalledOnce()
    expect(element.textContent).toBe('')
  })

  it('dismisses the request panel with Zrušit, Escape or a click outside, sending nothing', async () => {
    const services = service(); services.discover.mockResolvedValue('requestable')
    const { element } = await reader(services)
    await armRequest(element)
    for (const dismiss of [
      () => [...requestPanel()!.querySelectorAll('button')].find(button => button.textContent === 'Zrušit')!.click(),
      () => requestPanel()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })),
      () => document.body.dispatchEvent(new Event('pointerdown', { bubbles: true })),
    ]) {
      click(element, 'Požádat o přístup'); await flush()
      const panel = requestPanel()!
      // Shared anchored panel: a dialog with an accessible name, no visible title or close button, focus in the field.
      expect(panel.getAttribute('role')).toBe('dialog')
      expect(panel.querySelector('h1, h2, h3, [aria-label="Zavřít"]')).toBeNull()
      expect(document.activeElement).toBe(panel.querySelector('textarea'))
      expect(panel.querySelector('label')!.textContent).toContain('Důvod žádosti (nepovinný)')
      dismiss(); await flush()
      expect(requestPanel()).toBeNull()
      expect(services.requestAccess).not.toHaveBeenCalled()
      expect(surfaces(element)[0]!.dataset.concealedAccess).toBe('denied')
    }
    // Ctrl+Enter sends from the text area.
    click(element, 'Požádat o přístup'); await flush()
    const reason = requestPanel()!.querySelector('textarea')!
    reason.value = 'Rotace hesel'
    reason.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true, cancelable: true })); await flush()
    expect(services.requestAccess).toHaveBeenCalledWith({ secretId: id, signal: expect.any(AbortSignal), reason: 'Rotace hesel' })
  })

  it('requires a reason when configured, and sends directly without a panel when turned off', async () => {
    const required = service(); required.discover.mockResolvedValue('requestable')
    const first = await reader(required, value(), { requestReason: 'required' })
    await armRequest(first.element)
    click(first.element, 'Požádat o přístup'); await flush()
    await sendReason('   ')
    expect(required.requestAccess).not.toHaveBeenCalled()
    expect(requestPanel()!.querySelector('[role=alert]')!.textContent).toBe('Napište důvod žádosti.')
    await sendReason('Audit')
    expect(required.requestAccess).toHaveBeenCalledWith(expect.objectContaining({ reason: 'Audit' }))
    vi.useRealTimers()

    const direct = service(); direct.discover.mockResolvedValue('requestable')
    const second = await reader(direct, inlineValue(), { requestReason: 'off' })
    await armRequest(second.element)
    click(second.element, 'Požádat o přístup'); await flush()
    expect(requestPanel()).toBeNull()
    expect(direct.requestAccess).toHaveBeenCalledWith({ secretId: id, signal: expect.any(AbortSignal), reason: '' })
    expect(() => createSecretsPlugin({ services: direct, requestReason: 'always' as never })).toThrow(RangeError)
  })

  it('lets the host cover released values again without hiding or revoking the reference', async () => {
    const services = service()
    const { element, plugin } = await reader(services, value({ secretId: id, fields: ['username', 'password'] }))
    click(element, 'Odkrýt'); await flush()
    expect(element.textContent).toContain('CANARY-password')
    expect(plugin.conceal(otherId, { immediate: true })).toBe(0)
    expect(element.textContent).toContain('CANARY-password')
    expect(plugin.conceal(id, { immediate: true })).toBe(1); await flush()
    expect(element.textContent).not.toContain('CANARY'); expect(services.disposed).toHaveBeenCalledOnce()
    // Still visible and interactive: the reader can release again, the host was not asked to rediscover.
    expect(surfaces(element)).toHaveLength(1)
    click(element, 'Odkrýt'); await flush()
    expect(services.reveal).toHaveBeenCalledTimes(2)
    expect(element.textContent).toContain('CANARY-password')
    // The gentle variant rolls the mist back like Hide and disposes the release too.
    expect(plugin.conceal()).toBe(1)
    await vi.waitFor(() => expect(services.disposed).toHaveBeenCalledTimes(2))
    expect(services.discover).toHaveBeenCalledOnce()
    expect(() => plugin.conceal(42 as never)).toThrow(TypeError)
  })

  it('reruns discovery on refresh: approval makes a pending request available, an unchanged answer keeps the values', async () => {
    const services = service()
    services.discover.mockResolvedValue({ access: 'requestable', requestPending: true })
    const { element, plugin } = await reader(services, inlineValue())
    expect(element.textContent).toContain('Žádost odeslána · čeká na schválení')
    services.discover.mockResolvedValue('available')
    expect(plugin.refresh(id)).toBe(1); await flush()
    expect(services.discover).toHaveBeenCalledTimes(2)
    click(element, 'Odkrýt'); await flush()
    expect(element.textContent).toContain('CANARY-password')
    // Same level again: nothing is repainted, the released value stays.
    plugin.refresh(); await flush()
    expect(element.textContent).toContain('CANARY-password'); expect(services.disposed).not.toHaveBeenCalled()
    // A 403 lowered the level: the value is disposed and the reference offers a request instead.
    services.discover.mockResolvedValue('requestable')
    plugin.refresh(id); await flush()
    expect(services.disposed).toHaveBeenCalledOnce()
    expect(element.textContent).not.toContain('CANARY')
    expect(surfaces(element)[0]!.dataset.concealedAccess).toBe('denied')
    expect(plugin.refresh(otherId)).toBe(0)
  })

  it('says a request was not approved when a refresh settles it without access, or when the host reports it', async () => {
    const services = service()
    services.discover.mockResolvedValue({ access: 'requestable', requestPending: true })
    const { element, plugin } = await reader(services)
    services.discover.mockResolvedValue({ access: 'requestable', requestPending: false })
    plugin.refresh(id); await flush()
    expect(surfaces(element)[0]!.dataset.concealedRequestState).toBe('refused')
    expect(element.textContent).toContain('Žádost nebyla schválena')
    expect(element.querySelector('[data-md-secret-repaint]')).not.toBeNull()
    await armRequest(element)
    const reported = service(); reported.discover.mockResolvedValue({ access: 'requestable', requestPending: false, requestRefused: true })
    const later = await reader(reported)
    expect(surfaces(later.element)[0]!.dataset.concealedRequestState).toBe('refused')
  })

  it('discovers a revoked reference again on refresh, and aborts a superseded discovery', async () => {
    const services = service()
    let revoke!: () => void
    services.subscribeRevocation.mockImplementation((_id, callback) => { revoke = callback; return () => {} })
    const { element, plugin, app } = await reader(services)
    revoke(); await flush()
    expect(element.textContent).toBe('')
    const answers: ((value: SecretDiscovery) => void)[] = []
    services.discover.mockImplementation(() => new Promise(resolve => answers.push(resolve)))
    plugin.refresh(); plugin.refresh(); await flush()
    const [older, newer] = services.discover.mock.calls.slice(1).map(call => call[0].signal)
    expect(older!.aborted).toBe(true); expect(newer!.aborted).toBe(false)
    answers[0]!('available'); answers[1]!('available'); await flush()
    expect(surfaces(element)).toHaveLength(1)
    click(element, 'Odkrýt'); await flush()
    expect(element.textContent).toContain('CANARY-password')
    app.unmount(); disposers.pop()
    expect(plugin.refresh()).toBe(0); expect(plugin.conceal()).toBe(0)
  })

  it.each([value, inlineValue])('aborts pending releases on hide, revocation and unmount, isolating instances (%#)', async documentValue => {
    const services = service()
    const revocations: (() => void)[] = []
    services.subscribeRevocation.mockImplementation((_id, revoke) => { revocations.push(revoke); return () => {} })
    services.reveal.mockImplementation(() => new Promise(() => {}))
    const first = await reader(services, documentValue()); const second = await reader(services, documentValue())
    click(first.element, 'Odkrýt'); click(second.element, 'Odkrýt')
    const firstSignal = services.reveal.mock.calls[0]![0].signal
    const secondSignal = services.reveal.mock.calls[1]![0].signal
    click(first.element, 'Skrýt'); await flush()
    expect(firstSignal.aborted).toBe(true); expect(secondSignal.aborted).toBe(false)
    revocations[1]!(); await flush()
    expect(secondSignal.aborted).toBe(true); expect(second.element.querySelector('button')).toBeNull()
    click(first.element, 'Odkrýt'); first.app.unmount(); disposers.splice(0, 1)
    expect(services.reveal.mock.calls[2]![0].signal.aborted).toBe(true)
  })

  it('clears released values at once on revocation', async () => {
    const services = service()
    let revoke!: () => void
    services.subscribeRevocation.mockImplementation((_id, callback) => { revoke = callback; return () => {} })
    const { element } = await reader(services, value({ secretId: id, fields: ['username', 'password'] }))
    click(element, 'Odkrýt'); await flush()
    expect(element.textContent).toContain('CANARY-password')
    revoke(); await flush()
    expect(element.textContent).toBe(''); expect(services.disposed).toHaveBeenCalledOnce()
  })

  it('aborts the pending release when the page hides and does not reopen on return', async () => {
    const services = service()
    services.reveal.mockImplementation(() => new Promise(() => {}))
    const { element } = await reader(services)
    click(element, 'Odkrýt')
    const signal = services.reveal.mock.calls[0]![0].signal
    const visibility = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')
    try {
      document.dispatchEvent(new Event('visibilitychange')); await flush()
      expect(signal.aborted).toBe(true)
      visibility.mockReturnValue('visible')
      document.dispatchEvent(new Event('visibilitychange')); await flush()
      expect(services.reveal).toHaveBeenCalledOnce()
    } finally { visibility.mockRestore() }
  })

  it('ignores late release errors after hide and aborts when an editor reference is replaced', async () => {
    const services = service()
    let fail!: (error: Error) => void
    services.reveal.mockImplementation(() => new Promise((_resolve, reject) => { fail = reject }))
    const controller = createMarkdownEditorController({ profile: 'rich', editable: false, plugins: [createSecretsPlugin({ services })],
      source: { kind: 'local', value: value() } })
    disposers.push(() => controller.destroy())
    const element = host(); controller.attachView(element, { ariaLabel: 'Readonly' }); await flush()
    click(element, 'Odkrýt')
    const snapshot = JSON.stringify(controller.getValue())
    click(element, 'Skrýt'); fail(new Error('LEAK')); await flush()
    expect(element.textContent).not.toContain('LEAK')
    click(element, 'Odkrýt'); controller.setValue(value({ secretId: otherId })); await flush()
    expect(services.reveal.mock.calls[1]![0].signal.aborted).toBe(true)
    expect(snapshot).not.toContain('LEAK')
  })

  it('asks for the request reason in a readonly editor too, sharing its one active popover', async () => {
    const services = service(); services.discover.mockResolvedValue('requestable')
    const controller = createMarkdownEditorController({ profile: 'rich', editable: false, plugins: [createSecretsPlugin({ services })],
      source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy())
    const element = host(); controller.attachView(element, { ariaLabel: 'Readonly' }); await flush()
    await armRequest(element)
    click(element, 'Požádat o přístup'); await flush()
    expect(requestPanel()!.closest('[data-md-overlay-for]')).not.toBeNull()
    await sendReason('Čtenář')
    expect(services.requestAccess).toHaveBeenCalledWith(expect.objectContaining({ reason: 'Čtenář' }))
    expect(JSON.stringify(controller.getValue())).not.toContain('Čtenář')
  })
})

describe('secret settings panel', () => {
  it('edits fields and actions of inline and card references in an anchored panel as one undo step, never while readonly', async () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() })],
      source: { kind: 'local', value: value() } })
    disposers.push(() => controller.destroy())
    const root = host(); controller.attachView(root, { ariaLabel: 'Edit' })
    const attrs = () => JSON.parse(JSON.stringify(controller.getProjection().rich)).document.content[0].attrs
    const settings = () => document.querySelector<HTMLButtonElement>('[aria-label="Upravit"]')
    await vi.waitFor(() => expect(settings()).not.toBeNull())
    // A click on the card edits it where it is shown (P1); a second one closes the panel, the toolbar's „Upravit“ opens it too.
    const card = root.querySelector<HTMLElement>('[data-md-secret-view="block"]')!
    card.click()
    expect(document.querySelector('[role=dialog][aria-label="Zobrazení karty hesla"]')).not.toBeNull()
    card.click()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    settings()!.click()
    const dialog = document.querySelector<HTMLElement>('[role=dialog]')!
    expect(dialog).not.toBeNull()
    const tile = (selector: string) => dialog.querySelector<HTMLButtonElement>(selector)!
    const submit = () => dialog.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(tile('[data-field="password"]').getAttribute('aria-pressed')).toBe('true')
    expect(tile('[data-auto-hide="timed"]').getAttribute('aria-checked')).toBe('true')
    expect(dialog.querySelector<HTMLInputElement>('input[name="autoHideSeconds"]')!.value).toBe('60')
    tile('[data-field="password"]').click()
    expect(tile('[data-field="password"]').getAttribute('aria-pressed')).toBe('false')
    submit()
    expect(dialog.querySelector('[role=alert]')!.textContent).toContain('alespoň jedno')
    tile('[data-field="username"]').click(); tile('[data-field="url"]').click(); tile('[data-option="showDetails"]').click()
    const seconds = dialog.querySelector<HTMLInputElement>('input[name="autoHideSeconds"]')!
    seconds.value = '2'; submit()
    expect(dialog.querySelector('[role=alert]')!.textContent).toContain('od 5 do 3600')
    seconds.value = '15'; submit()
    expect(attrs()).toMatchObject({ fields: ['username', 'url'], showDetails: true, autoHideSeconds: 15 })
    controller.dispatch({ type: 'undo' })
    expect(attrs()).toMatchObject({ fields: null, showDetails: false, autoHideSeconds: 60 })
    await vi.waitFor(() => expect(settings()).not.toBeNull())
    root.querySelector<HTMLElement>('[data-md-secret-view="block"]')!.click()
    const again = document.querySelector<HTMLElement>('[role=dialog]')!
    again.querySelector<HTMLButtonElement>('[data-auto-hide="never"]')!.click()
    expect(again.querySelector<HTMLInputElement>('input[name="autoHideSeconds"]')!.disabled).toBe(true)
    again.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(attrs().autoHideSeconds).toBeNull()
    controller.setEditable(false)
    await vi.waitFor(() => expect(document.querySelector<HTMLElement>('.md-context-toolbar')?.hidden ?? true).toBe(true))
    settings()?.click()
    expect(document.querySelectorAll('[role=dialog]')).toHaveLength(0)
  })

  it('lets an inline reference choose several fields', async () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() })],
      source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy())
    const root = host(); controller.attachView(root, { ariaLabel: 'Edit' })
    await vi.waitFor(() => expect(document.querySelector('[aria-label="Upravit"]')).not.toBeNull())
    root.querySelector<HTMLElement>('[data-md-secret-view="inline"]')!.click()
    const dialog = document.querySelector<HTMLElement>('[role=dialog][aria-label="Zobrazení hesla v textu"]')!
    dialog.querySelector<HTMLButtonElement>('[data-field="username"]')!.click()
    dialog.querySelector('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    expect(JSON.parse(JSON.stringify(controller.getValue())).document.content[0].content[1].attrs.fields).toEqual(['username', 'password'])
  })

  it('leaves a tap on an inline reference\'s ink to the ink (reveal, copy): its key and field names start the edit (P1)', async () => {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services: service() })],
      source: { kind: 'local', value: inlineValue() } })
    disposers.push(() => controller.destroy())
    const root = host(); controller.attachView(root, { ariaLabel: 'Edit' })
    await vi.waitFor(() => expect(document.querySelector('[aria-label="Upravit"]')).not.toBeNull())
    const inline = root.querySelector<HTMLElement>('[data-md-secret-view="inline"]')!
    inline.querySelector<HTMLElement>('.md-secret-entry [data-concealed-surface]')!.click()
    expect(document.querySelector('[role=dialog]')).toBeNull()
    inline.querySelector<HTMLElement>('.md-chip-label')!.click()
    expect(document.querySelector('[role=dialog][aria-label="Zobrazení hesla v textu"]')).not.toBeNull()
  })

  it('draws an inline reference as the shared inline chip: the key and the shown fields in the clear, the values under the ink', async () => {
    const { element } = await reader(service(), inlineValue({ secretId: id, fields: ['username', 'password'] }))
    const chip = element.querySelector<HTMLElement>('.md-chip.md-secret-chip')!
    expect(chip.getAttribute('role')).toBe('group')
    expect(chip.querySelector('.md-chip-label')!.textContent).toBe('Uživatelské jméno · Heslo')
    expect(chip.getAttribute('aria-label')).toBe('Přihlašovací údaje: Uživatelské jméno · Heslo')
    expect(chip.querySelector('.md-chip-media')).not.toBeNull()
    expect(chip.querySelector('.md-secret-entry [data-concealed-surface]')).not.toBeNull()
    expect(chip.textContent).not.toContain('CANARY')
  })
})

describe('editing a reference in place', () => {
  const editorOf = (root: HTMLElement) => (root.querySelector('.ProseMirror') as HTMLElement & { editor: import('@tiptap/core').Editor }).editor
  function edit(documentValue: MarkdownRichValue, pickReplacement?: SecretsPluginOptions['pickReplacement'], services = service()) {
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [createSecretsPlugin({ services, ...(pickReplacement ? { pickReplacement } : {}) })],
      source: { kind: 'local', value: documentValue } })
    disposers.push(() => controller.destroy())
    // The app's editor view (KeyboardFirst's Enter into a toolbar), which a bare controller lacks.
    configureMarkdownEditorView(controller, { compact: false })
    const root = host(); controller.attachView(root, { ariaLabel: 'Edit' })
    const json = () => JSON.parse(JSON.stringify(controller.getProjection().rich))
    return { controller, root, json, services }
  }
  const chip = (root: HTMLElement, key: string) => root.querySelector<HTMLButtonElement>(`[data-secret-chip="${key}"]`)

  it('swaps a card for another secret from the host picker as one undo step, storing only the reference', async () => {
    const pick = vi.fn(async () => otherId)
    const { controller, root, json } = edit(value({ secretId: id, fields: ['username', 'password'], showCopy: false, autoHideSeconds: 30 }), pick)
    await vi.waitFor(() => expect(chip(root, 'swap')).not.toBeNull())
    chip(root, 'swap')!.click(); await flush()
    expect(pick).toHaveBeenCalledWith({ secretId: id, layout: 'block', signal: expect.any(AbortSignal) })
    const attrs = json().document.content[0].attrs
    // The swap changes the id only; the display settings stay and nothing but the reference attributes is stored.
    expect(attrs).toEqual({ secretId: otherId, field: 'password', fields: ['username', 'password'], showCopy: false, showDetails: false, autoHideSeconds: 30 })
    expect(JSON.stringify(json())).not.toContain('CANARY')
    controller.dispatch({ type: 'undo' })
    expect(json().document.content[0].attrs.secretId).toBe(id)
  })

  it('swaps an inline reference from its toolbar, and ignores a closed or invalid answer', async () => {
    const pick = vi.fn<NonNullable<SecretsPluginOptions['pickReplacement']>>(async () => null)
    const { root, json } = edit(inlineValue(), pick)
    const swap = () => document.querySelector<HTMLButtonElement>('[aria-label="Vyměnit heslo"]')
    const inline = () => json().document.content[0].content[1].attrs
    await vi.waitFor(() => expect(swap()).not.toBeNull())
    root.querySelector<HTMLElement>('[data-md-secret-view="inline"]')!.click()
    swap()!.click(); await flush()
    expect(pick).toHaveBeenLastCalledWith(expect.objectContaining({ secretId: id, layout: 'inline' }))
    expect(inline().secretId).toBe(id)
    pick.mockResolvedValueOnce('not-a-secret-id')
    swap()!.click(); await flush()
    expect(inline().secretId).toBe(id)
    pick.mockResolvedValueOnce(otherId)
    swap()!.click(); await flush()
    expect(inline()).toMatchObject({ secretId: otherId, fields: null, autoHideSeconds: 60 })
    expect(pick).toHaveBeenCalledTimes(3)
  })

  it('edits a selected card or inline reference with Enter or F2 where it is shown; the swap is only the named action (P1)', async () => {
    const pick = vi.fn<NonNullable<SecretsPluginOptions['pickReplacement']>>(async () => null)
    const panel = () => document.querySelector<HTMLElement>('.md-secret-settings')
    const escape = () => document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    for (const [documentValue, at, title] of [[value(), 0, 'Zobrazení karty hesla'], [inlineValue(), 10, 'Zobrazení hesla v textu']] as const) {
      const { root } = edit(documentValue, pick)
      await vi.waitFor(() => expect(document.querySelector('[aria-label="Vyměnit heslo"]')).not.toBeNull())
      const editor = editorOf(root)
      // Focused and selected, the toolbar shows: KeyboardFirst's Enter would focus its first button instead.
      editor.commands.setNodeSelection(at); editor.view.focus()
      await vi.waitFor(() => expect(document.querySelector('.md-context-toolbar:not([hidden])')).not.toBeNull())
      expect([...document.querySelectorAll('.md-context-toolbar:not([hidden]) button')].map(button => button.getAttribute('aria-label')).slice(0, 2)).toEqual(['Upravit', 'Vyměnit heslo'])
      for (const key of ['Enter', 'F2']) {
        editor.view.dom.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
        expect(panel()?.getAttribute('aria-label'), key).toBe(title)
        escape()
        expect(panel(), key).toBeNull()
        expect(editor.state.selection.from).toBe(at)
      }
      disposers.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren()
    }
    expect(pick).not.toHaveBeenCalled()
  })

  it('opens the settings from the auto-hide chip of a custom delay instead of losing it', async () => {
    const { root, json } = edit(value({ secretId: id, autoHideSeconds: 120 }))
    await vi.waitFor(() => expect(chip(root, 'autoHide')).not.toBeNull())
    expect(chip(root, 'autoHide')!.textContent).toBe('Skrýt po 120 s')
    chip(root, 'autoHide')!.click(); await flush()
    expect(document.querySelector('.md-secret-settings')).not.toBeNull()
    expect(json().document.content[0].attrs.autoHideSeconds).toBe(120)
  })

  it('drops a swap whose reference was removed while the picker was open', async () => {
    let answer!: (secretId: string) => void
    const pick = vi.fn(() => new Promise<string>(resolve => { answer = resolve }))
    const { root, json } = edit(value(), pick)
    await vi.waitFor(() => expect(chip(root, 'swap')).not.toBeNull())
    chip(root, 'swap')!.click()
    const editor = editorOf(root)
    editor.commands.setNodeSelection(0); editor.commands.deleteSelection()
    answer(otherId); await flush()
    expect(JSON.stringify(json())).not.toContain(otherId)
  })

  it('undoes and redoes the document from Ctrl+Z / Ctrl+Y on a chip, and returns the focus to the card after a swap', async () => {
    const pick = vi.fn(async () => otherId)
    const { root, json } = edit(value(), pick)
    const attrs = () => json().document.content[0].attrs
    await vi.waitFor(() => expect(chip(root, 'field:username')).not.toBeNull())
    chip(root, 'field:username')!.click(); await flush()
    const press = (key: string) => chip(root, 'field:username')!.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: true, bubbles: true, cancelable: true }))
    press('z'); await flush(); expect(attrs().fields).toBeNull()
    press('y'); await flush(); expect(attrs().fields).toEqual(['username', 'password'])
    chip(root, 'swap')!.focus(); chip(root, 'swap')!.click(); await flush()
    expect(attrs().secretId).toBe(otherId)
    // Not left on the page body or some unrelated element, not even for a frame (P7).
    expect(root.contains(document.activeElement)).toBe(true)
  })

  it('toggles fields and auto-hide from chips on the card, keeps one field and offers no swap without a host picker', async () => {
    const { controller, root, json } = edit(value())
    const attrs = () => json().document.content[0].attrs
    await vi.waitFor(() => expect(chip(root, 'field:password')).not.toBeNull())
    expect(chip(root, 'swap')).toBeNull()
    expect(chip(root, 'field:password')!.getAttribute('aria-pressed')).toBe('true')
    expect(chip(root, 'field:password')!.getAttribute('aria-disabled')).toBe('true')
    chip(root, 'field:password')!.click()
    expect(attrs().fields).toBeNull()
    chip(root, 'field:username')!.click(); await flush()
    expect(attrs().fields).toEqual(['username', 'password'])
    // The repainted card gives focus back to the chip that was pressed.
    await vi.waitFor(() => expect(document.activeElement).toBe(chip(root, 'field:username')))
    expect(chip(root, 'autoHide')!.textContent).toBe('Skrýt po 60 s')
    chip(root, 'autoHide')!.click(); await flush()
    expect(attrs().autoHideSeconds).toBeNull()
    controller.dispatch({ type: 'undo' })
    expect(attrs()).toMatchObject({ fields: ['username', 'password'], autoHideSeconds: 60 })
    await vi.waitFor(() => expect(chip(root, 'more')).not.toBeNull())
    chip(root, 'more')!.click()
    expect(document.querySelector('[role=dialog] input[name="autoHideSeconds"]')).not.toBeNull()
    controller.setEditable(false)
    chip(root, 'field:url')?.click()
    expect(attrs().fields).toEqual(['username', 'password'])
  })

  it('shows no chips in read mode or on a reference the reader may not discover', async () => {
    const { element } = await reader()
    expect(element.querySelector('.md-secret-card')).not.toBeNull()
    expect(element.querySelector('.md-secret-edit')).toBeNull()
    const services = service(); services.discover.mockResolvedValue('hidden')
    const { root } = edit(value(), vi.fn(async () => otherId), services)
    await flush()
    expect(root.querySelector('.md-secret-edit')).toBeNull()
  })
})
