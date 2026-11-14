// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { Extension } from '@tiptap/core'
import { createMarkdownEditorController } from './controller'
import { editorForController } from './internal'
import type { MarkdownEditorController, MarkdownPlugin, MarkdownRichValue } from './index'

const controllers: MarkdownEditorController[] = []
afterEach(() => {
  for (const controller of controllers.splice(0)) {
    try { controller.destroy() } catch { /* Tests deliberately install failing cleanup hooks. */ }
  }
  document.body.replaceChildren()
  vi.restoreAllMocks()
})

function create(plugins: readonly MarkdownPlugin[] = [], onSubscriberError?: (error: unknown) => void) {
  const controller = createMarkdownEditorController({
    profile: 'markdown-safe', source: { kind: 'local', value: 'Content' }, plugins, onSubscriberError,
  })
  controllers.push(controller)
  const host = document.createElement('div')
  document.body.append(host)
  return { controller, host }
}

describe('controller lifecycle boundaries', () => {
  it('rejects malformed JavaScript plugin instances during controller construction', () => {
    for (const createInstance of [
      () => null,
      () => ({ markdownLossy: 'not-a-function' }),
      () => ({ commands: 'not-an-array' }),
    ]) {
      expect(() => createMarkdownEditorController({
        profile: 'markdown-safe', source: { kind: 'local', value: 'Content' },
        plugins: [{ id: 'runtime-invalid', apiVersion: 1, create: createInstance } as unknown as MarkdownPlugin],
      })).toThrow(/invalid plugin instance/)
    }
  })

  it('rejects an invalid lifecycle cleanup immediately and still aborts and cleans earlier hooks', () => {
    const engineCleanup = vi.fn()
    let signal!: AbortSignal
    const plugin: MarkdownPlugin = {
      id: 'invalid-cleanup', apiVersion: 1,
      create: context => {
        signal = context.signal
        return {
          onCreate: () => engineCleanup,
          onAttach: (() => Promise.resolve(() => undefined)) as never,
        }
      },
    }
    const { controller, host } = create([plugin])

    expect(() => controller.attachView(host, { ariaLabel: 'Invalid cleanup' })).toThrow(/cleanup must be a function/)
    expect(engineCleanup).toHaveBeenCalledTimes(1)
    expect(signal.aborted).toBe(true)
    expect(controller.state.lifecycle).toBe('failed')
    expect(editorForController(controller)).toBeNull()
    expect(host.childElementCount).toBe(0)
  })

  it('cancels a reentrant destroy before constructing an editor', () => {
    const { controller, host } = create()
    controller.subscribe(state => { if (state.lifecycle === 'starting') controller.destroy() })

    expect(() => controller.attachView(host, { ariaLabel: 'Cancelled mount' })).toThrow(/destroyed|cancelled/i)
    expect(controller.state.lifecycle).toBe('destroyed')
    expect(editorForController(controller)).toBeNull()
    expect(host.childElementCount).toBe(0)
  })

  it('preserves initialization errors and completes fallback cleanup when an extension destroy hook fails', () => {
    const plugin: MarkdownPlugin = {
      id: 'failed-mount', apiVersion: 1,
      create: () => ({
        extensions: [Extension.create({ name: 'failedDestroy', onDestroy() { throw new Error('teardown failed') } })],
        onAttach() { throw new Error('initialization failed') },
      }),
    }
    const { controller, host } = create([plugin])

    let failure: unknown
    try { controller.attachView(host, { ariaLabel: 'Broken mount' }) } catch (error) { failure = error }
    expect(failure).toBeInstanceOf(AggregateError)
    expect((failure as AggregateError).errors[0]).toMatchObject({ message: 'initialization failed' })
    expect((failure as Error).message).toContain('initialization failed')
    expect(controller.state.lifecycle).toBe('failed')
    expect(controller.state.error?.message).toBe('initialization failed')
    expect(editorForController(controller)).toBeNull()
    expect(host.childElementCount).toBe(0)
    expect(() => controller.attachView(host, { ariaLabel: 'Retry' })).toThrow(/failed/i)
  })

  it('runs cleanup returned after a plugin reentrantly destroys its controller', () => {
    const cleanup = vi.fn()
    const plugin: MarkdownPlugin = {
      id: 'reentrant-create', apiVersion: 1,
      create: () => ({ onCreate({ controller }) { controller.destroy(); return cleanup } }),
    }
    const { controller, host } = create([plugin])

    expect(() => controller.attachView(host, { ariaLabel: 'Reentrant plugin' })).toThrow(/destroyed/i)
    expect(cleanup).toHaveBeenCalledTimes(1)
    expect(controller.state.lifecycle).toBe('destroyed')
    expect(editorForController(controller)).toBeNull()
    expect(host.childElementCount).toBe(0)
  })

  it('isolates subscriber failures and delivers committed state to later subscribers', () => {
    const subscriberError = vi.fn()
    const { controller, host } = create([], subscriberError)
    controller.attachView(host, { ariaLabel: 'Subscriber errors' })
    controller.subscribe(state => { if (state.contentRevision) throw new Error('consumer failed') })
    const downstream = vi.fn()
    controller.subscribe(downstream)

    expect(() => controller.dispatch({ type: 'set-heading', level: 2 })).not.toThrow()
    expect(controller.getValue()).toBe('## Content')
    expect(downstream).toHaveBeenCalledTimes(2)
    expect(subscriberError).toHaveBeenCalledWith(expect.objectContaining({ message: 'consumer failed' }), expect.objectContaining({ contentRevision: 1 }))
  })

  it('queues reentrant publications so every subscriber observes them in order', () => {
    const { controller, host } = create()
    controller.attachView(host, { ariaLabel: 'Ordered states' })
    controller.subscribe(state => {
      if (state.contentRevision === 1 && state.editable) controller.setEditable(false)
    })
    const observed: boolean[] = []
    controller.subscribe(state => { if (state.contentRevision === 1) observed.push(state.editable) })

    controller.dispatch({ type: 'set-heading', level: 2 })
    expect(observed).toEqual([true, false])
  })

  it('delivers one final destroyed state before closing subscriptions', () => {
    const { controller } = create()
    const lifecycles: string[] = []
    controller.subscribe(state => lifecycles.push(state.lifecycle))

    controller.destroy()

    expect(lifecycles).toEqual(['idle', 'destroyed'])
    expect(controller.state.lifecycle).toBe('destroyed')
  })
})

describe('rich document domains', () => {
  function rich(document: MarkdownRichValue['document']): MarkdownRichValue {
    return { format: 'atlas-rich', version: 1, document }
  }

  it.each([
    ['colspan', { colspan: 1_000_000, rowspan: 1, colwidth: null }],
    ['rowspan', { colspan: 1, rowspan: -2, colwidth: null }],
    ['colwidth type', { colspan: 1, rowspan: 1, colwidth: 'not-an-array' }],
    ['colwidth length', { colspan: 2, rowspan: 1, colwidth: [120] }],
  ])('rejects invalid built-in table cell %s before mounting', (_case, attrs) => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: rich({
      type: 'doc', content: [{
        type: 'table', content: [{ type: 'tableRow', content: [{
          type: 'tableCell', attrs,
          content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Imported' }] }],
        }] }],
      }],
    }) } })).toThrow(/table|column/i)
  })

  it('rejects invalid row display attributes before mounting', () => {
    expect(() => createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: rich({
      type: 'doc', content: [{
        type: 'table', content: [{ type: 'tableRow', attrs: { height: -200 }, content: [{
          type: 'tableCell', content: [{ type: 'paragraph' }],
        }] }],
      }],
    }) } })).toThrow(/height/i)
  })

  it('accepts valid merged-cell geometry inside the shared table limit', () => {
    const controller = createMarkdownEditorController({ profile: 'rich', source: { kind: 'local', value: rich({
      type: 'doc', content: [{
        type: 'table', content: [
          { type: 'tableRow', content: [{
            type: 'tableHeader', attrs: { colspan: 2, rowspan: 1, colwidth: null },
            content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Title' }] }],
          }] },
          { type: 'tableRow', content: [
            { type: 'tableCell', content: [{ type: 'paragraph' }] },
            { type: 'tableCell', content: [{ type: 'paragraph' }] },
          ] },
        ],
      }],
    }) } })
    controllers.push(controller)
    expect((controller.getValue() as MarkdownRichValue).document).toBeTruthy()
  })
})
