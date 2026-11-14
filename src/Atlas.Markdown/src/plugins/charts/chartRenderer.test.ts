// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defaultChart, type ChartData } from './data'

const mocks = vi.hoisted(() => ({
  init: vi.fn(), use: vi.fn(), setOption: vi.fn(), resize: vi.fn(), dispose: vi.fn(), on: vi.fn(),
}))
vi.mock('echarts/core', () => ({ init: mocks.init, use: mocks.use }))
vi.mock('echarts/charts', () => ({ BarChart: {}, LineChart: {}, PieChart: {} }))
vi.mock('echarts/components', () => ({ GridComponent: {}, LegendComponent: {}, TooltipComponent: {} }))
vi.mock('echarts/renderers', () => ({ SVGRenderer: {} }))
import { createChartRenderer, type MarkdownChartRenderer } from './chartRenderer'

const renderers: MarkdownChartRenderer[] = []
let resizeCallback: ResizeObserverCallback
let disconnect: ReturnType<typeof vi.fn<() => void>>
let motion: MediaQueryList
const option = () => mocks.setOption.mock.lastCall![0]
function mount(data: ChartData = defaultChart, width = 480) {
  const host = document.createElement('div')
  document.body.append(host)
  Object.defineProperties(host, { clientWidth: { value: width, configurable: true }, clientHeight: { value: 320 } })
  const renderer = createChartRenderer(host, data)
  renderers.push(renderer)
  return { host, renderer }
}
beforeEach(() => {
  vi.clearAllMocks()
  mocks.init.mockReturnValue({ setOption: mocks.setOption, resize: mocks.resize, dispose: mocks.dispose, on: mocks.on })
  mocks.setOption.mockImplementation(() => mocks.on.mock.calls.find(call => call[0] === 'finished')?.[1]())
  disconnect = vi.fn()
  vi.stubGlobal('ResizeObserver', class {
    constructor(callback: ResizeObserverCallback) { resizeCallback = callback }
    observe() {}
    disconnect() { disconnect() }
  })
  motion = Object.assign(new EventTarget(), { matches: false, media: '(prefers-reduced-motion: reduce)', onchange: null, addListener: vi.fn(), removeListener: vi.fn() }) as MediaQueryList
  vi.stubGlobal('matchMedia', vi.fn(() => motion))
})
afterEach(() => {
  renderers.splice(0).forEach(renderer => renderer.destroy())
  document.body.replaceChildren()
  vi.unstubAllGlobals()
})

describe('ECharts renderer', () => {
  it('preparation waits for the actual finished event, including replacement updates', async () => {
    mocks.setOption.mockImplementation(() => {})
    const { renderer } = mount()
    const finished = mocks.on.mock.calls.find(call => call[0] === 'finished')![1]
    const done = vi.fn(); const prepared = renderer.prepare!().then(done)
    await Promise.resolve(); expect(done).not.toHaveBeenCalled()
    renderer.update({ ...defaultChart, title: 'Newer' })
    await Promise.resolve(); expect(done).not.toHaveBeenCalled()
    finished(); await prepared; expect(done).toHaveBeenCalledWith(true)
  })
  it('reports an unmeasurable chart as fallback and releases pending readiness on destroy', async () => {
    const hidden = mount(defaultChart, 0)
    expect(await hidden.renderer.prepare!()).toBe(false)
    mocks.setOption.mockImplementation(() => {})
    const visible = mount(); const pending = visible.renderer.prepare!(); visible.renderer.destroy()
    expect(await pending).toBe(false)
  })
  it('updates one SVG instance, retaining identities and short data transitions', () => {
    const { host, renderer } = mount()
    expect(mocks.init).toHaveBeenCalledWith(host, undefined, { renderer: 'svg' })
    const initial = option()
    renderer.update({ ...defaultChart, grid: [['Období', 'Nový název'], ['Leden', '42'], ['Únor', '18']] })
    expect(mocks.init).toHaveBeenCalledTimes(1)
    expect(option().series[0].id).toBe(initial.series[0].id)
    expect(option().series[0].data[0]).toMatchObject({ id: initial.series[0].data[0].id, value: 42 })
    expect(option()).toMatchObject({ animation: true, animationDuration: 250, animationDurationUpdate: 250 })
    expect(mocks.dispose).not.toHaveBeenCalled()
  })
  it('preserves gaps and renders names as rich text rather than HTML', () => {
    mount({ ...defaultChart, kind: 'line', grid: [['X', '<img src=x onerror=alert(1)>'], ['A', '1 234,50'], ['B', ''], ['C', '0']] })
    expect(option().series[0].data.map((value: { value: unknown }) => value.value)).toEqual([1234.5, '-', 0])
    expect(option().series[0]).toMatchObject({ connectNulls: false, name: '<img src=x onerror=alert(1)>' })
    expect(option().tooltip).toMatchObject({ renderMode: 'richText', confine: true })
  })
  it('replaces obsolete series and axes when switching to concentric circular series', () => {
    const { renderer } = mount()
    renderer.update({ ...defaultChart, kind: 'doughnut', grid: [['X', 'První', 'Druhá'], ['A', '4', '6'], ['B', '', '3']] })
    expect(option().xAxis).toEqual([])
    expect(option().grid).toEqual([])
    expect(option().series.map((series: { radius: string[] }) => series.radius)).toEqual([['38%', '58%'], ['60%', '78%']])
    expect(option().series[0].data[1].value).toBe('-')
    renderer.update(defaultChart)
    expect(option().series).toHaveLength(1)
    expect(option().series[0].type).toBe('bar')
    expect(mocks.setOption.mock.lastCall![1]).toEqual({ replaceMerge: ['series', 'xAxis', 'yAxis', 'grid'] })
  })
  it('responds to reduced motion changes and unregisters every resource on destroy', () => {
    const { renderer } = mount()
    Object.defineProperty(motion, 'matches', { value: true, configurable: true })
    motion.dispatchEvent(new Event('change'))
    expect(option()).toMatchObject({ animation: false, animationDurationUpdate: 0, stateAnimation: { duration: 0 } })
    renderer.update({ ...defaultChart, kind: 'pie' })
    expect(option().series[0].emphasis.scale).toBe(false)
    resizeCallback([], {} as ResizeObserver)
    expect(mocks.resize).toHaveBeenCalledWith({ animation: { duration: 0 } })
    renderer.destroy()
    const calls = mocks.setOption.mock.calls.length
    motion.dispatchEvent(new Event('change'))
    resizeCallback([], {} as ResizeObserver)
    renderer.update(defaultChart)
    renderer.destroy()
    expect(mocks.setOption).toHaveBeenCalledTimes(calls)
    expect(mocks.dispose).toHaveBeenCalledTimes(1)
    expect(disconnect).toHaveBeenCalledTimes(1)
  })
  it('waits for a measurable host and uses the latest data on its first render', () => {
    const { host, renderer } = mount(defaultChart, 0)
    expect(mocks.init).not.toHaveBeenCalled()
    renderer.update({ ...defaultChart, kind: 'line' })
    Object.defineProperty(host, 'clientWidth', { value: 400 })
    resizeCallback([], {} as ResizeObserver)
    expect(mocks.init).toHaveBeenCalledTimes(1)
    expect(option().series[0].type).toBe('line')
  })
  it('releases observers and a partially created chart after an initial rendering error', () => {
    mocks.setOption.mockImplementationOnce(() => { throw new Error('Rendering failed') })
    expect(() => mount()).toThrow('Rendering failed')
    expect(disconnect).toHaveBeenCalledTimes(1)
    expect(mocks.dispose).toHaveBeenCalledTimes(1)
  })
})
