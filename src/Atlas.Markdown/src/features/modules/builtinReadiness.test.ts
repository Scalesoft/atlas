// @vitest-environment jsdom
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest'
import { prepareMarkdownModulesForExport } from '../../core'
import { renderMarkdownValue } from '../../engine/staticRenderer'
import { createChartsPlugin, defaultChart } from '../../plugins/charts'
import { createDiagramsPlugin, createDiagramNode } from '../../plugins/diagrams'
import { chartRuntime } from '../../plugins/charts/runtime'
import type { MarkdownModuleView } from './block'
import type { ChartData } from '../../plugins/charts/data'
import type { MarkdownRichValue } from '../../core/types'

const engines = vi.hoisted(() => {
  let resolve!: () => void
  return { chartImport: new Promise<void>(yes => { resolve = yes }), releaseChartImport: () => resolve(), chart: vi.fn(), mermaid: vi.fn(), decode: vi.fn() }
})
vi.mock('../../plugins/charts/chartRenderer', async () => { await engines.chartImport; return { createChartRenderer: engines.chart } })
vi.mock('mermaid', () => ({ default: { initialize() {}, render: engines.mermaid } }))
const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(yes => { resolve = yes }); return { promise, resolve } }
const tick = async () => { for (let index = 0; index < 25; index++) await Promise.resolve() }
const cleanup: Array<() => void> = []
const originalDecode = Object.getOwnPropertyDescriptor(HTMLImageElement.prototype, 'decode')
beforeAll(() => Object.defineProperty(HTMLImageElement.prototype, 'decode', { configurable: true, value: engines.decode }))
beforeEach(() => { engines.decode.mockResolvedValue(undefined); engines.mermaid.mockResolvedValue({ svg: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"></svg>' }) })
afterEach(async () => { cleanup.splice(0).reverse().forEach(stop => stop()); document.body.replaceChildren(); await tick(); vi.clearAllMocks() })
afterAll(() => { if (originalDecode) Object.defineProperty(HTMLImageElement.prototype, 'decode', originalDecode); else Reflect.deleteProperty(HTMLImageElement.prototype, 'decode') })

describe('actual built-in async readiness', () => {
  it('holds the shared queue through delayed chart engine import and finished preparations', async () => {
    const gates = Array.from({ length: 3 }, () => deferred<boolean>())
    engines.chart.mockImplementation((host: HTMLElement, data: ChartData) => {
      host.append(document.createElementNS('http://www.w3.org/2000/svg', 'svg'))
      return { update() {}, destroy() {}, prepare: () => gates[Number(data.title)].promise }
    })
    const root = document.createElement('div'); document.body.append(root)
    cleanup.push(renderMarkdownValue({ format: 'atlas-rich', version: 1, plugins: [{ id: 'charts', version: 1 }], document: {
      type: 'doc', content: gates.map((_gate, index) => ({ type: 'chart', attrs: { data: { ...defaultChart, title: String(index) } } })),
    } } as unknown as MarkdownRichValue, 'rich', [createChartsPlugin()], root))
    const done = vi.fn(), preparation = prepareMarkdownModulesForExport(root).then(done)
    await tick(); expect(done).not.toHaveBeenCalled(); expect(engines.chart).not.toHaveBeenCalled()
    expect(root.querySelectorAll('[data-md-render-state="loading"]')).toHaveLength(2)
    engines.releaseChartImport()
    await vi.waitFor(() => expect(engines.chart).toHaveBeenCalledTimes(2))
    expect(done).not.toHaveBeenCalled()
    gates[0].resolve(true); await vi.waitFor(() => expect(engines.chart).toHaveBeenCalledTimes(3))
    expect(done).not.toHaveBeenCalled(); gates[1].resolve(true); gates[2].resolve(true)
    await preparation; expect(done).toHaveBeenCalledWith({ ready: 3, fallback: 0 })
  })
  it('rechecks a previously hidden chart instead of retaining an initial fallback forever', async () => {
    const ready = vi.fn().mockResolvedValueOnce(false).mockResolvedValue(true)
    engines.chart.mockImplementation(() => ({ update() {}, destroy() {}, prepare: ready }))
    const container = document.createElement('div'); document.body.append(container); const abort = new AbortController()
    const view = await chartRuntime.render({ container, data: defaultChart, document: { type: 'doc' }, signal: abort.signal }) as MarkdownModuleView<ChartData>
    cleanup.push(() => { abort.abort(); view.destroy() })
    expect(await view.prepare!()).toBe(true)
    expect(ready).toHaveBeenCalledTimes(2)
  })
  it('awaits Mermaid and generated image decode before returning export readiness', async () => {
    const painted = deferred<{ svg: string }>(), decoded = deferred<void>()
    engines.mermaid.mockReturnValueOnce(painted.promise); engines.decode.mockReturnValueOnce(decoded.promise)
    const root = document.createElement('div'); document.body.append(root)
    cleanup.push(renderMarkdownValue({ format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: {
      type: 'doc', content: [createDiagramNode({ version: 1, source: 'graph LR\n A-->B', caption: 'Saved diagram' })],
    } } as any, 'rich', [createDiagramsPlugin()], root))
    const done = vi.fn(), preparation = prepareMarkdownModulesForExport(root).then(done)
    await vi.waitFor(() => expect(engines.mermaid).toHaveBeenCalledOnce())
    expect(done).not.toHaveBeenCalled(); expect(root.textContent).toContain('A-->B')
    painted.resolve({ svg: '<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20"></svg>' })
    await vi.waitFor(() => expect(engines.decode).toHaveBeenCalledOnce())
    expect(done).not.toHaveBeenCalled(); expect(root.querySelector('img')).toBeNull()
    decoded.resolve(); await preparation
    expect(root.querySelector('img')).not.toBeNull(); expect(done).toHaveBeenCalledWith({ ready: 1, fallback: 0 })
  })
  it('reports failed image decoding as explicit readable fallback', async () => {
    engines.decode.mockRejectedValueOnce(new Error('Decode failed'))
    const root = document.createElement('div'); document.body.append(root)
    cleanup.push(renderMarkdownValue({ format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: {
      type: 'doc', content: [createDiagramNode({ version: 1, source: 'graph LR\n A-->B', caption: '' })],
    } } as any, 'rich', [createDiagramsPlugin()], root))
    expect(await prepareMarkdownModulesForExport(root)).toEqual({ ready: 0, fallback: 1 })
    expect(root.textContent).toContain('A-->B'); expect(root.textContent).toContain('Decode failed'); expect(root.querySelector('img')).toBeNull()
    expect(root.querySelectorAll('[role="status"]')).toHaveLength(1)
    expect(root.querySelector('[data-md-render-state="fallback"]')).not.toBeNull()
  })
  it('removes Mermaid scratch DOM immediately on destroy and rejects an outstanding export', async () => {
    const held = deferred<{ svg: string }>(); engines.mermaid.mockReturnValueOnce(held.promise)
    const root = document.createElement('div'); document.body.append(root)
    const stop = renderMarkdownValue({ format: 'atlas-rich', version: 1, plugins: [{ id: 'diagrams', version: 2 }], document: {
      type: 'doc', content: [createDiagramNode({ version: 1, source: 'graph LR\n A-->B', caption: '' })],
    } } as any, 'rich', [createDiagramsPlugin()], root)
    const result = prepareMarkdownModulesForExport(root); const rejected = expect(result).rejects.toMatchObject({ name: 'AbortError' })
    await vi.waitFor(() => expect(engines.mermaid).toHaveBeenCalledOnce())
    expect(document.body.children).toHaveLength(2)
    stop(); expect(document.body.children).toHaveLength(1); await rejected
    held.resolve({ svg: '<svg></svg>' }); await tick(); expect(root.querySelector('img')).toBeNull()
  })
})
