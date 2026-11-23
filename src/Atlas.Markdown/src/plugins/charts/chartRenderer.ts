import { init, use, type ComposeOption, type EChartsType } from 'echarts/core'
import { BarChart, LineChart, PieChart, type BarSeriesOption, type LineSeriesOption, type PieSeriesOption } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent, type GridComponentOption, type LegendComponentOption, type TooltipComponentOption } from 'echarts/components'
import { SVGRenderer } from 'echarts/renderers'
import { parseChartNumber, type ChartData } from './data'

use([BarChart, LineChart, PieChart, GridComponent, LegendComponent, TooltipComponent, SVGRenderer])

type ChartOption = ComposeOption<BarSeriesOption | LineSeriesOption | PieSeriesOption | GridComponentOption | LegendComponentOption | TooltipComponentOption>
const colors = ['#3997a9', '#9b8ac4', '#d6ac68', '#689c87', '#c37b86', '#6b91bc', '#b68d6e', '#81949a']

/**
 * How a chart outside a document looks (Atlas.Web's page history); a document's chart never has one, its data say everything.
 * `stacked`: series stack into one bar per category. `colors`: the series' colours in order, instead of the palette.
 * `compact`: a small strip without legend and value labels. `marks`: the last series is drawn as diamonds, not bars (publications).
 * `focus`: the category drawn at full strength, the others softened. `tooltip`: its plain text per category. `pick`: a click on a category.
 */
export interface ChartLook {
  stacked?: boolean
  colors?: readonly string[]
  compact?: boolean
  marks?: boolean
  focus?: number
  tooltip?(index: number): string
  pick?(index: number): void
}

export interface MarkdownChartRenderer {
  update(data: ChartData, look?: ChartLook): void
  prepare?(): Promise<boolean>
  destroy(): void
}

function chartOption(host: HTMLElement, data: ChartData, reducedMotion: boolean, look?: ChartLook): ChartOption {
  const style = host.ownerDocument.defaultView?.getComputedStyle(host)
  const token = (name: string, fallback: string) => style?.getPropertyValue(name).trim() || fallback
  const text = token('--text-dim', '#667085')
  const surface = token('--bg-2', '#ffffff')
  const circular = data.kind === 'pie' || data.kind === 'doughnut'
  const rows = data.grid.slice(1)
  const seriesNames = data.grid[0].slice(1)
  const palette = look?.colors ?? colors
  const soft = (index: number) => look?.focus !== undefined && look.focus >= 0 && look.focus !== index
  const series = seriesNames.map<BarSeriesOption | LineSeriesOption | PieSeriesOption>((name, column) => {
    const id = data.source?.cells[0]?.[column + 1] ?? `series-${column}`
    const values = rows.map((row, index) => ({
      id: data.source?.cells[index + 1]?.[0] ?? `row-${index}`,
      name: row[0],
      value: parseChartNumber(row[column + 1]) ?? '-' as const,
      ...(soft(index) ? { itemStyle: { opacity: 0.4 } } : {}),
    }))
    if (circular) {
      // Additional series retain their own values as concentric rings.
      const inner = data.kind === 'doughnut' ? 38 : 0
      const band = (78 - inner) / seriesNames.length
      return {
        id, name, type: 'pie', data: values,
        radius: [`${inner + column * band + (column ? Math.min(2, band / 4) : 0)}%`, `${inner + (column + 1) * band}%`],
        center: ['50%', '57%'], label: { show: false }, labelLine: { show: false },
        stillShowZeroSum: false,
        itemStyle: { borderColor: surface, borderWidth: 2, borderRadius: data.kind === 'doughnut' ? 3 : 0 },
        emphasis: { scale: !reducedMotion, scaleSize: 3 },
        animationType: 'scale', animationTypeUpdate: 'transition',
      }
    }
    const common = { id, name, data: values, emphasis: { focus: 'series' as const }, itemStyle: { color: palette[column % palette.length] } }
    if (look?.marks && column === seriesNames.length - 1) return { ...common, type: 'line', symbol: 'diamond', symbolSize: 9, lineStyle: { width: 0 }, z: 3 }
    return data.kind === 'bar'
      ? { ...common, type: 'bar', barMaxWidth: look?.compact ? 22 : 44, stack: look?.stacked ? 'total' : undefined, itemStyle: { ...common.itemStyle, borderRadius: look?.stacked ? 2 : [4, 4, 0, 0] },
          // A strip of bars grows from left to right, one after another.
          animationDelay: look && !reducedMotion ? (index: number) => Math.min(index * 40, 800) : undefined }
      : { ...common, type: 'line', connectNulls: false, smooth: false, symbol: 'circle', symbolSize: 6, lineStyle: { width: 2.5 }, showSymbol: rows.length <= 30 }
  })
  return {
    color: colors,
    backgroundColor: 'transparent',
    textStyle: { fontFamily: token('--font-sans', 'sans-serif'), color: text },
    animation: !reducedMotion,
    animationDuration: reducedMotion ? 0 : 250,
    animationDurationUpdate: reducedMotion ? 0 : 250,
    animationEasing: 'cubicOut', animationEasingUpdate: 'cubicOut',
    stateAnimation: { duration: reducedMotion ? 0 : 150, easing: 'cubicOut' },
    legend: {
      show: !look?.compact, top: 4, left: 'center', icon: 'circle', itemWidth: 8, itemHeight: 8, itemGap: 18,
      textStyle: { color: text, fontSize: 12 },
      data: circular ? rows.map(row => row[0]) : seriesNames,
    },
    // SVG rich text avoids interpreting document labels as HTML.
    tooltip: {
      trigger: circular ? 'item' : 'axis', renderMode: 'richText', confine: true,
      backgroundColor: surface, borderColor: token('--line', '#dce2e8'), borderWidth: 1,
      padding: [9, 12], textStyle: { color: token('--text', '#233044'), fontSize: 12 },
      transitionDuration: reducedMotion ? 0 : 0.15,
      ...(look?.tooltip ? { axisPointer: { type: 'none' as const }, formatter: (params: unknown) => look.tooltip!((Array.isArray(params) ? params[0] : params as { dataIndex: number }).dataIndex) } : {}),
    },
    grid: circular ? [] : [look?.compact ? { id: 'plot', top: 8, right: 2, bottom: 2, left: 2, containLabel: true } : { id: 'plot', top: 48, right: 12, bottom: 14, left: 12, containLabel: true }],
    xAxis: circular ? [] : [{ id: 'categories', type: 'category', data: rows.map(row => row[0]), boundaryGap: data.kind === 'bar', axisLine: { show: false }, axisTick: { show: false }, axisLabel: { color: text, hideOverlap: true, fontSize: look?.compact ? 10 : 12 } }],
    yAxis: circular ? [] : [{ id: 'values', type: 'value', splitNumber: look?.compact ? 2 : undefined, axisLine: { show: false }, axisTick: { show: false }, axisLabel: { show: !look?.compact, color: text }, splitLine: { lineStyle: { color: token('--line', '#e8edf0'), opacity: 0.55 } } }],
    series,
  }
}

/** Own one instance for the lifetime of a node; setOption preserves data transitions. */
export function createChartRenderer(host: HTMLElement, initialData: ChartData, initialLook?: ChartLook): MarkdownChartRenderer {
  const view = host.ownerDocument.defaultView
  const motion = view?.matchMedia?.('(prefers-reduced-motion: reduce)')
  let data = initialData
  let look = initialLook
  let chart: EChartsType | undefined
  let destroyed = false
  let readiness: Promise<boolean> = Promise.resolve(false)
  let finish: ((ready: boolean) => void) | undefined
  const completed = () => { finish?.(true); finish = undefined }
  const apply = () => {
    if (destroyed) return
    if (!chart) {
      // A lazy module may be mounted inside a detached/hidden editor first.
      if (!host.isConnected || host.clientWidth <= 0 || host.clientHeight <= 0) return
      chart = init(host, undefined, { renderer: 'svg' })
      chart.on('finished', completed)
      chart.on('click', (event: { dataIndex?: number }) => { if (event.dataIndex !== undefined) look?.pick?.(event.dataIndex) })
    }
    finish?.(false)
    readiness = new Promise(resolve => { finish = resolve })
    chart.setOption(chartOption(host, data, motion?.matches ?? false, look), { replaceMerge: ['series', 'xAxis', 'yAxis', 'grid'] })
  }
  const resize = () => {
    if (destroyed || !host.isConnected || host.clientWidth <= 0 || host.clientHeight <= 0) return
    if (!chart) apply()
    else chart.resize({ animation: { duration: 0 } })
  }
  const observer = view?.ResizeObserver ? new view.ResizeObserver(resize) : undefined
  observer?.observe(host)
  if (!observer) view?.addEventListener('resize', resize)
  motion?.addEventListener('change', apply)
  const destroy = () => {
    if (destroyed) return
    destroyed = true
    observer?.disconnect()
    view?.removeEventListener('resize', resize)
    motion?.removeEventListener('change', apply)
    chart?.dispose()
    finish?.(false); finish = undefined
    chart = undefined
  }
  try { apply() } catch (error) { destroy(); throw error }
  return { update(next, nextLook) { if (!destroyed) { data = next; look = nextLook ?? look; apply() } },
    async prepare() {
      if (destroyed) return false
      if (!chart) apply()
      while (!destroyed && chart) {
        const current = readiness, ready = await current
        if (current === readiness) return ready && !destroyed
      }
      return false
    }, destroy }
}
