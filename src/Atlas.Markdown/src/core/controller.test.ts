// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController } from '../index'
import { isRichFormat, LEGACY_RICH_FORMAT, RICH_FORMAT } from './index'

const controllers: ReturnType<typeof createMarkdownEditorController>[] = []
afterEach(() => {
  controllers.splice(0).forEach(controller => controller.destroy())
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

function mount(value = '**Ocean**') {
  const controller = createMarkdownEditorController({
    profile: 'markdown-safe', source: { kind: 'local', value },
  })
  controllers.push(controller)
  const host = document.createElement('div')
  document.body.append(host)
  controller.attachView(host, { ariaLabel: 'Popis' })
  return { controller, host }
}

describe('public headless controller', () => {
  it('renders Markdown through a named view and publishes host replacements', () => {
    const { controller, host } = mount()
    expect(host.querySelector('strong')?.textContent).toBe('Ocean')
    expect(host.querySelector('[role="textbox"]')?.getAttribute('aria-label')).toBe('Popis')
    controller.setValue('Nový *popis*')
    expect(host.querySelector('em')?.textContent).toBe('popis')
    expect(controller.getValue()).toBe('Nový *popis*')
  })

  it('keeps the document across view detach and refuses two simultaneous views', () => {
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: 'Původní text' },
    })
    controllers.push(controller)
    const first = document.createElement('div')
    const second = document.createElement('div')
    document.body.append(first, second)
    const detach = controller.attachView(first, { ariaLabel: 'První' })
    expect(() => controller.attachView(second, { ariaLabel: 'Druhý' })).toThrow(/view/i)
    controller.setValue('Zachovaný text')
    detach()
    expect(first.textContent).toBe('')
    controller.attachView(second, { ariaLabel: 'Druhý' })
    expect(second.textContent).toBe('Zachovaný text')
  })

  it('shares command state with dispatch without query causing document changes', () => {
    const { controller, host } = mount('Nadpis')
    const listener = vi.fn()
    controller.subscribe(listener)
    const initialCalls = listener.mock.calls.length
    expect(controller.query({ type: 'set-heading', level: 2 })).toEqual({ enabled: true, active: false })
    expect(listener).toHaveBeenCalledTimes(initialCalls)
    expect(controller.dispatch({ type: 'set-heading', level: 2 })).toBe(true)
    expect(host.querySelector('h2')?.textContent).toBe('Nadpis')
    expect(controller.getValue()).toBe('## Nadpis')
    expect(controller.query({ type: 'set-heading', level: 2 }).active).toBe(true)
    expect(listener.mock.calls.at(-1)?.[0].contentRevision).toBe(1)
  })

  it('does not clear undo when the host echoes a user change', () => {
    const { controller } = mount('Text')
    controller.dispatch({ type: 'set-heading', level: 2 })
    controller.setValue('## Text')
    expect(controller.dispatch({ type: 'undo' })).toBe(true)
    expect(controller.getValue()).toBe('Text')
  })

  it('blocks writes and reports disabled commands when readonly', () => {
    const { controller, host } = mount('Text')
    controller.setEditable(false)
    expect(controller.query({ type: 'set-heading', level: 2 }).enabled).toBe(false)
    expect(controller.dispatch({ type: 'set-heading', level: 2 })).toBe(false)
    expect(controller.getValue()).toBe('Text')
    expect(host.querySelector('.ProseMirror')?.getAttribute('contenteditable')).toBe('false')
    expect(host.querySelector('[role=textbox]')?.getAttribute('aria-readonly')).toBe('true')
    controller.setEditable(true)
    expect(host.querySelector('[role=textbox]')?.getAttribute('aria-readonly')).toBe('false')
  })

  it('publishes a fresh command-state snapshot when formatting an empty selection', () => {
    const { controller } = mount('Text')
    const listener = vi.fn()
    controller.subscribe(listener)
    const previous = controller.state
    const calls = listener.mock.calls.length
    controller.dispatch({ type: 'toggle-bold' })
    expect(controller.query({ type: 'toggle-bold' }).active).toBe(true)
    expect(listener.mock.calls.length).toBeGreaterThan(calls)
    expect(controller.state).not.toBe(previous)
    expect(controller.state.contentRevision).toBe(previous.contentRevision)
  })

  it('applies a pre-mount value without leaving later edits hidden behind the buffer', () => {
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: 'První' },
    })
    controllers.push(controller)
    controller.setValue('Druhý')
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Odložený' })
    controller.dispatch({ type: 'set-heading', level: 2 })
    expect(controller.getValue()).toBe('## Druhý')
  })

  it('recognizes semantically equivalent Markdown echoes without another content revision', () => {
    const { controller } = mount('**Text**')
    const revision = controller.state.contentRevision
    controller.setValue('__Text__')
    expect(controller.state.contentRevision).toBe(revision)
    expect(controller.getValue()).toBe('**Text**')
  })

  it('keeps detached projections consistent with buffered replacements', () => {
    const controller = createMarkdownEditorController({ profile: 'markdown-safe', source: { kind: 'local', value: 'Old' } })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    const detach = controller.attachView(host, { ariaLabel: 'Detached' })
    detach()
    controller.setValue('**New**')
    expect(controller.getProjection().markdown).toBe('**New**')
    expect(controller.getProjection().text).toBe('New')
    const revision = controller.state.contentRevision
    controller.attachView(host, { ariaLabel: 'Remounted' })
    expect(controller.state.contentRevision).toBe(revision)
    expect(host.querySelector('strong')?.textContent).toBe('New')
  })

  it('cannot insert nonportable underline via keyboard in Markdown-safe', () => {
    const { controller, host } = mount('Text')
    const view = host.querySelector('.ProseMirror')!
    view.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', ctrlKey: true, bubbles: true }))
    view.dispatchEvent(new KeyboardEvent('keydown', { key: 'u', ctrlKey: true, bubbles: true }))
    expect(host.querySelector('u')).toBeNull()
    expect(controller.getProjection().markdownLossy).toBe(false)
  })

  it('rejects unknown rich attributes instead of dropping them on mount', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: {
      format: 'atlas-rich', version: 1,
      document: { type: 'doc', content: [{ type: 'paragraph', attrs: { unsupported: 'lost' } }] },
    } } })).toThrow(/attribute/i)
  })

  it('loads a document stored before 5.0 under the legacy format id and writes it back as atlas-rich', () => {
    const document = { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Starý dokument' }] }] }
    expect(isRichFormat(LEGACY_RICH_FORMAT)).toBe(true)
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: { format: LEGACY_RICH_FORMAT, version: 1, document } } })
    controllers.push(controller)
    const host = window.document.createElement('div')
    window.document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Rich' })
    expect(host.textContent).toContain('Starý dokument')
    expect(controller.getValue()).toMatchObject({ format: RICH_FORMAT, version: 1, document })
    controller.setValue({ format: LEGACY_RICH_FORMAT, version: 1, document })
    expect(controller.getValue().format).toBe('atlas-rich')
  })

  it.each([
    { type: 'paragraph', text: 'Lost text' },
    { type: 'paragraph', content: [{ type: 'text', text: 'Text', content: [] }] },
  ])('rejects misplaced rich node fields instead of losing data: %j', node => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: {
      format: 'atlas-rich', version: 1, document: { type: 'doc', content: [node as never] },
    } } })).toThrow(/field/i)
  })

  it('rejects lossy commands in Markdown-safe mode and invalid command parameters', () => {
    const { controller } = mount('Text')
    for (const command of [
      { type: 'toggle-underline' },
      { type: 'toggle-highlight' },
      { type: 'set-cell-background', color: 'red' },
      { type: 'insert-table', rows: 0, columns: 3 },
      { type: 'insert-table', rows: 2, columns: 1.5 },
      { type: 'set-link', href: 'javascript:alert(1)' },
    ] as const) {
      expect(controller.query(command).enabled).toBe(false)
      expect(controller.dispatch(command)).toBe(false)
    }
    expect(controller.getValue()).toBe('Text')
  })

  it('retains rich document attributes and rejects invalid replacement atomically', () => {
    const value = {
      format: 'atlas-rich', version: 1,
      document: { type: 'doc', content: [
        { type: 'paragraph', content: [
          { type: 'text', text: 'Podtržené', marks: [{ type: 'underline' }] },
        ] },
      ] },
    } as const
    const controller = createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value },
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Rich' })
    expect(host.querySelector('u')?.textContent).toBe('Podtržené')
    expect(controller.getProjection().markdownLossy).toBe(true)
    const previous = controller.getValue()
    expect(() => controller.setValue({ ...value, version: 99 } as never)).toThrow(/version/i)
    expect(() => controller.setValue({
      ...value, document: { type: 'doc', content: [{ type: 'missing-node' }] },
    } as never)).toThrow()
    expect(controller.getValue()).toEqual(previous)
  })

  it('returns defensive rich values that cannot mutate the live document', () => {
    const controller = createMarkdownEditorController({
      profile: 'rich', source: { kind: 'local', value: {
        format: 'atlas-rich', version: 1,
        document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Originál' }] }] },
      } },
    })
    controllers.push(controller)
    const host = document.createElement('div')
    document.body.append(host)
    controller.attachView(host, { ariaLabel: 'Rich' })
    const projection = controller.getProjection()
    ;(projection.rich.document as any).content[0].content[0].text = 'Změněno'
    expect(controller.getProjection().text).toBe('Originál')
  })

  it('unsubscribes listeners and destroys idempotently without late callbacks', async () => {
    const { controller, host } = mount('Text')
    const listener = vi.fn()
    const unsubscribe = controller.subscribe(listener)
    unsubscribe()
    const count = listener.mock.calls.length
    controller.setValue('Jiné')
    controller.destroy()
    controller.destroy()
    await new Promise(resolve => setTimeout(resolve, 10))
    expect(listener).toHaveBeenCalledTimes(count)
    expect(host.textContent).toBe('')
    expect(controller.dispatch({ type: 'set-heading', level: 2 })).toBe(false)
    expect(() => controller.attachView(host, { ariaLabel: 'Zaniklý' })).toThrow(/destroy/i)
  })

  it('rejects missing accessible names before mounting any content', () => {
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: 'Text' },
    })
    controllers.push(controller)
    const host = document.createElement('div')
    expect(() => controller.attachView(host, { ariaLabel: '  ' })).toThrow(/label/i)
    expect(host.childElementCount).toBe(0)
  })
})
