// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createMarkdownModuleActivation, prepareMarkdownModulesForExport } from './activation'
import { createMarkdownEditorController, defineMarkdownBlockModule } from '../../core'
import { editorForController } from '../../core/internal'
import { renderMarkdownValue } from '../../engine/staticRenderer'
import type { MarkdownRichValue } from '../../core/types'

const cleanups: Array<() => void> = []
const observations = new Map<Element, IntersectionObserverCallback>()
class Observer {
  private elements: Element[] = []
  constructor(private callback: IntersectionObserverCallback) {}
  observe(element: Element) { this.elements.push(element); observations.set(element, this.callback) }
  unobserve(element: Element) { observations.delete(element); this.elements = this.elements.filter(other => other !== element) }
  disconnect() { for (const element of this.elements) observations.delete(element); this.elements = [] }
}
const visible = (element: Element) => observations.get(element)?.([{ target: element, isIntersecting: true } as IntersectionObserverEntry], {} as IntersectionObserver)
// The shared proximity observer hydrates in an idle batch (a timer without requestIdleCallback).
const tick = async () => { await new Promise(resolve => setTimeout(resolve, 2)); for (let index = 0; index < 12; index++) await Promise.resolve() }
const deferred = () => { let resolve!: (ready: boolean) => void; const promise = new Promise<boolean>(yes => { resolve = yes }); return { promise, resolve } }
beforeEach(() => vi.stubGlobal('IntersectionObserver', Observer))
afterEach(async () => { cleanups.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); observations.clear(); vi.unstubAllGlobals(); vi.restoreAllMocks(); await tick() })
function entry(render = vi.fn(async () => true), owner = document) {
  const container = owner.createElement('section'); owner.body.append(container)
  const signal = new AbortController()
  const activation = createMarkdownModuleActivation({ container, signal: signal.signal, render })
  cleanups.push(() => signal.abort())
  return { container, signal, activation, render }
}

describe('module activation and per-document render queue', () => {
  it('does not render offscreen blocks until approaching the viewport', async () => {
    const f = entry(); await tick()
    expect(f.render).not.toHaveBeenCalled(); expect(f.container.dataset.mdRenderState).toBe('deferred')
    visible(f.container); await tick()
    expect(f.render).toHaveBeenCalledOnce(); expect(f.container.dataset.mdRenderState).toBe('ready')
    expect(observations.has(f.container)).toBe(false)
  })
  it.each(['focusin', 'pointerdown'])('forces activation for %s independently of intersection', async event => {
    const f = entry(); f.container.dispatchEvent(new Event(event, { bubbles: true })); await tick()
    expect(f.render).toHaveBeenCalledOnce()
  })
  it('bounds concurrent asynchronous renderers across separate roots in one document', async () => {
    const gates = Array.from({ length: 4 }, deferred)
    const mounted = gates.map(gate => entry(vi.fn(() => gate.promise)))
    mounted.forEach(f => visible(f.container)); await tick()
    expect(mounted.map(f => f.render.mock.calls.length)).toEqual([1, 1, 0, 0])
    gates[0].resolve(true); await tick()
    expect(mounted.map(f => f.render.mock.calls.length)).toEqual([1, 1, 1, 0])
    gates[1].resolve(true); gates[2].resolve(true); gates[3].resolve(true)
    await prepareMarkdownModulesForExport(document.body)
  })
  it('removes a cancelled queued renderer without invoking it', async () => {
    const first = deferred(), second = deferred()
    const a = entry(vi.fn(() => first.promise)), b = entry(vi.fn(() => second.promise)), c = entry()
    visible(a.container); visible(b.container); visible(c.container); await tick()
    c.signal.abort(); first.resolve(true); second.resolve(true); await tick()
    expect(c.render).not.toHaveBeenCalled()
  })
  it('retains the concurrency slot until a running aborted renderer actually settles', async () => {
    const first = deferred(), second = deferred()
    const a = entry(vi.fn(() => first.promise)), b = entry(vi.fn(() => second.promise)), c = entry()
    visible(a.container); visible(b.container); visible(c.container); await tick(); a.signal.abort(); await tick()
    expect(c.render).not.toHaveBeenCalled()
    first.resolve(true); await tick(); expect(c.render).toHaveBeenCalledOnce(); second.resolve(true)
  })
  it('prepares only the selected export root and counts explicit fallbacks', async () => {
    const a = entry(vi.fn(async () => false)), b = entry()
    expect(await prepareMarkdownModulesForExport(a.container)).toEqual({ ready: 0, fallback: 1 })
    expect(b.render).not.toHaveBeenCalled()
  })
  it('cancels export waiting promptly without cancelling shared visible module rendering', async () => {
    const gate = deferred(), f = entry(vi.fn(() => gate.promise)), cancel = new AbortController()
    const result = prepareMarkdownModulesForExport(f.container, { signal: cancel.signal })
    cancel.abort(); await expect(result).rejects.toMatchObject({ name: 'AbortError' })
    gate.resolve(true); await tick(); expect(f.container.dataset.mdRenderState).toBe('ready')
  })
  it('forces all modules for print while marking the synchronous fallback boundary', async () => {
    const f = entry(); window.dispatchEvent(new Event('beforeprint'))
    expect(f.container.dataset.mdPrintFallback).toBe('true')
    await tick(); expect(f.render).toHaveBeenCalledOnce()
    window.dispatchEvent(new Event('afterprint')); expect(f.container.dataset.mdPrintFallback).toBeUndefined()
  })
  it('renders normally without IntersectionObserver support', async () => {
    vi.stubGlobal('IntersectionObserver', undefined)
    const f = entry(); await tick(); expect(f.render).toHaveBeenCalledOnce()
  })
})

function moduleFixture(fail = false) {
  const render = vi.fn(async (context: { container: HTMLElement }) => { if (fail) throw new Error('Renderer failed'); context.container.textContent = 'Rendered detail' })
  const load = vi.fn(async () => ({ render }))
  const block = defineMarkdownBlockModule({ id: 'viewport-probe', name: 'viewportProbe', label: 'Probe', defaults: { text: 'Saved source' },
    normalize: (value: unknown) => value as { text: string }, text: data => data.text, load })
  const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, plugins: [{ id: 'viewport-probe', version: 1 }],
    document: { type: 'doc', content: [block.node({ text: 'Saved source' })] } as MarkdownRichValue['document'] }
  return { render, load, block, value }
}
describe('real module viewport, selection and readonly fallback', () => {
  it('publishes current source synchronously for print while a retained async repaint is pending', async () => {
    const paint = deferred()
    const block = defineMarkdownBlockModule({ id: 'paint-probe', name: 'paintProbe', label: 'Paint', defaults: { text: 'Original' },
      normalize: (value: unknown) => value as { text: string }, text: data => data.text,
      load: async () => ({ render(context) {
        context.container.textContent = 'Old visual'; let preparation = Promise.resolve(true)
        return { update(next) { preparation = paint.promise.then(ready => { context.container.textContent = `Painted ${next.data.text}`; return ready }) },
          prepare: () => preparation, destroy() {} }
      } }),
    })
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, plugins: [{ id: 'paint-probe', version: 1 }],
      document: { type: 'doc', content: [{ type: 'paragraph' }, block.node({ text: 'Original' }), { type: 'paragraph' }] } as MarkdownRichValue['document'] }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [block.createPlugin()], source: { kind: 'local', value } })
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Pending paint' })
    cleanups.push(() => controller.destroy())
    expect(await prepareMarkdownModulesForExport(host)).toEqual({ ready: 1, fallback: 0 })
    const editor = editorForController(controller)!, element = host.querySelector<HTMLElement>('[data-md-module]')!
    editor.view.dispatch(editor.state.tr.setNodeMarkup(2, undefined, { data: { text: 'Current source' } }))
    window.dispatchEvent(new Event('beforeprint'))
    expect(element.querySelector('[data-module-fallback]')?.textContent).toContain('Current source')
    expect(element.dataset.mdPrintFallback).toBe('true')
    expect(element.textContent).toContain('Old visual')
    const exported = prepareMarkdownModulesForExport(host)
    paint.resolve(true); expect(await exported).toEqual({ ready: 1, fallback: 0 })
    expect(element.querySelector('[data-module-fallback]')).toBeNull(); expect(element.textContent).toContain('Painted Current source')
  })
  it('keeps an inline stub as wide as its text: the loading message is for assistive technology only (J3.9)', () => {
    const chip = defineMarkdownBlockModule({ id: 'chip-probe', name: 'chipProbe', label: 'Chip', defaults: { text: 'Jana' }, inline: true,
      normalize: (value: unknown) => value as { text: string }, text: data => data.text, load: () => new Promise<never>(() => {}) })
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, plugins: [{ id: 'chip-probe', version: 1 }],
      document: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Volá ' }, chip.node({ text: 'Jana' })] }] } as MarkdownRichValue['document'] }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [chip.createPlugin()], source: { kind: 'local', value } })
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Inline stub' })
    cleanups.push(() => controller.destroy())
    const stub = host.querySelector('[data-md-module] [data-module-fallback]')!
    expect(stub.firstElementChild!.textContent).toBe('Jana')
    // The message stays in the DOM (role=status) but takes no width: it is clipped like the block stub's text.
    expect(stub.querySelector('[role=status]')!.classList).toContain('md-module-stub-status')
  })
  it('shows saved content without import offscreen, and NodeSelection activates its renderer', async () => {
    const f = moduleFixture()
    const value: MarkdownRichValue = { ...f.value, document: { type: 'doc', content: [{ type: 'paragraph' }, f.block.node({ text: 'Saved source' }), { type: 'paragraph' }] } as MarkdownRichValue['document'] }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [f.block.createPlugin()], source: { kind: 'local', value } })
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Viewport modules' })
    cleanups.push(() => controller.destroy()); await tick()
    const before = controller.getValue()
    expect(f.load).not.toHaveBeenCalled(); expect(host.textContent).toContain('Saved source')
    editorForController(controller)!.commands.setNodeSelection(2)
    await tick(); expect(f.render).toHaveBeenCalledOnce(); expect(host.textContent).toContain('Rendered detail')
    expect(controller.getValue()).toEqual(before)
  })
  it('forces static readonly modules for export and reports visible failure fallback without changing content', async () => {
    const f = moduleFixture(true), host = document.createElement('div'); document.body.append(host)
    cleanups.push(renderMarkdownValue(f.value, 'rich', [f.block.createPlugin()], host))
    await tick(); expect(f.load).not.toHaveBeenCalled(); expect(host.textContent).toContain('Saved source')
    expect(await prepareMarkdownModulesForExport(host)).toEqual({ ready: 0, fallback: 1 })
    expect(host.querySelector('[role="alert"]')?.textContent).toContain('nepodařilo')
    expect(host.textContent).toContain('Saved source')
  })
  it('destroying an offscreen NodeView removes observation and never imports it later', async () => {
    const f = moduleFixture(), controller = createMarkdownEditorController({ profile: 'rich', plugins: [f.block.createPlugin()], source: { kind: 'local', value: f.value } })
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Destroyed module' })
    const element = host.querySelector('[data-md-module]')!
    controller.destroy(); visible(element); await tick()
    expect(observations.has(element)).toBe(false); expect(f.load).not.toHaveBeenCalled()
  })
  it('refreshes retained fallback data and export readiness when a view gains or loses DOM', async () => {
    const block = defineMarkdownBlockModule({ id: 'retained-probe', name: 'retainedProbe', label: 'Retained', defaults: { text: 'Original' },
      normalize: (value: unknown) => value as { text: string }, text: data => data.text,
      load: async () => ({ render() { return {
        update(context: { container: HTMLElement; data: { text: string } }) { context.container.textContent = context.data.text === 'Rendered' ? 'Actual view' : '' },
        destroy() {},
      } } }),
    })
    const value: MarkdownRichValue = { format: 'atlas-rich', version: 1, plugins: [{ id: 'retained-probe', version: 1 }],
      document: { type: 'doc', content: [{ type: 'paragraph' }, block.node({ text: 'Original' }), { type: 'paragraph' }] } as MarkdownRichValue['document'] }
    const controller = createMarkdownEditorController({ profile: 'rich', plugins: [block.createPlugin()], source: { kind: 'local', value } })
    const host = document.createElement('div'); document.body.append(host); controller.attachView(host, { ariaLabel: 'Retained fallback' })
    cleanups.push(() => controller.destroy())
    const element = host.querySelector<HTMLElement>('[data-md-module]')!, editor = editorForController(controller)!
    expect(await prepareMarkdownModulesForExport(host)).toEqual({ ready: 0, fallback: 1 })
    const update = (text: string) => editor.view.dispatch(editor.state.tr.setNodeMarkup(2, undefined, { data: { text } }))
    update('New source')
    expect(element.textContent).toContain('New source'); expect(element.textContent).not.toContain('Original')
    expect(element.dataset.mdRenderState).toBe('fallback')
    update('Rendered')
    expect(element.querySelector('[data-module-fallback]')).toBeNull(); expect(element.dataset.mdRenderState).toBe('ready')
    expect(await prepareMarkdownModulesForExport(host)).toEqual({ ready: 1, fallback: 0 })
    update('Empty again')
    expect(element.textContent).toContain('Empty again'); expect(element.dataset.mdRenderState).toBe('fallback')
    expect(await prepareMarkdownModulesForExport(host)).toEqual({ ready: 0, fallback: 1 })
  })
})
