import { defineMarkdownBlockModule, defineMarkdownCommand, pickNamedChoice, registerMarkdownDialectForm, type MarkdownPlugin, type MarkdownPluginCommand } from '../../core'
import { chartsStrings } from './strings'
import { chartMarkdownLossy, chartText, defaultChart, normalizeChart, type ChartData, type ChartKind } from './data'
import { ChartReferences, ensureChartReferences, selectedChartSource } from './source'

export type { ChartData, ChartKind, ChartSource } from './data'
export { defaultChart, normalizeChart, parseChartNumber } from './data'
export type { ChartLook } from './chartRenderer'
/** The charts' ECharts renderer for a chart outside a document (Atlas.Web's page history); ECharts loads with it, on first use. */
export const loadChartRenderer = () => import('./chartRenderer')
/** The same ECharts with the graph series, for a network outside a document (Atlas.Web's Mapa znalostí); loads on first use. */
export const loadGraphRenderer = () => import('./graphRenderer')

// The ```a-chart form loads with the Markdown dialect.
const form = registerMarkdownDialectForm(() => import('./markdown'))

const kindLabel = (kind: ChartKind) => ({ bar: chartsStrings.kindBar, line: chartsStrings.kindLine, pie: chartsStrings.kindPie, doughnut: chartsStrings.kindDoughnut })[kind]
const chartKind = (text: string) => pickNamedChoice(text, (['bar', 'line', 'pie', 'doughnut'] as const).map(kind => [kind, kindLabel(kind), kind] as const))

function definition(insertCommand?: MarkdownPluginCommand<ChartData>) {
  return defineMarkdownBlockModule<ChartData>({
    id: 'charts', name: 'chart', stubHeight: () => 389, dataValidator: 'chart', get label() { return chartsStrings.label }, get description() { return chartsStrings.slashDescription }, icon: 'bar-chart-3', keywords: ['graf', 'chart', 'sloupce', 'kolac'], defaults: defaultChart, insertCommand,
    // `/graf koláč`: that kind of chart.
    parameter: { get label() { return chartsStrings.label }, data: text => { const kind = chartKind(text); return kind && { ...defaultChart, kind } },
      title: text => `${chartsStrings.label}: ${kindLabel(chartKind(text)!)}` },
    normalize: normalizeChart, text: chartText, extensions: [ChartReferences],
    // Editor dialect: core marks a ```a-chart fence and its linked table; data failing validation stays a code block.
    dialect: { tag: 'div[data-md-fence="a-chart"]', lossy: chartMarkdownLossy, write: (data, document) => form().chartMarkdown(data, document), read: element => form().readChartFence(element) },
    html: data => ['figure', {}, ['figcaption', {}, data.title],
      ['table', {}, ['caption', {}, chartsStrings.dataCaption], ...data.grid.map((row, r) => ['tr', {}, ...row.map((cell, c) => [r === 0 || c === 0 ? 'th' : 'td', r === 0 || c === 0 ? { scope: r === 0 ? 'col' : 'row' } : {}, cell])])]],
    load: () => import('./runtime').then(runtime => runtime.chartRuntime),
  })
}
const module = definition()
export const insertChart = module.insert
export const chartNode = module.node

export const insertChartFromSelection = defineMarkdownCommand<{ kind?: ChartKind; title?: string } | void>({
  id: 'charts.from-table',
  apply: options => ({ tr, dispatch }) => {
    // A dry run inspects content without generating IDs or mutating its transaction.
    const preview = selectedChartSource(tr)
    if (!preview) return false
    try {
      normalizeChart({ ...defaultChart, kind: options?.kind ?? 'bar', title: options?.title ?? chartsStrings.fromTableDefaultTitle, grid: preview.grid })
      if (dispatch) {
        ensureChartReferences(tr, undefined, undefined, { from: preview.tableAt, to: preview.insertAt })
        const selected = selectedChartSource(tr)!
        const data = normalizeChart({ ...defaultChart, kind: options?.kind ?? 'bar', title: options?.title ?? chartsStrings.fromTableDefaultTitle, grid: selected.grid, source: selected.source, sourceStatus: 'linked' })
        tr.insert(selected.insertAt, tr.doc.type.schema.nodes.chart.create({ data }))
      }
      return true
    } catch { return false }
  },
})

export function createChartsPlugin(): MarkdownPlugin {
  const plugin = definition(module.insert).createPlugin()
  return { ...plugin, create(context) {
    const instance = plugin.create(context)
    const action = { id: 'charts.from-table', label: chartsStrings.fromTableActionLabel, description: chartsStrings.fromTableActionDescription, icon: 'svg:line-chart', group: 'content', command: insertChartFromSelection() }
    return { ...instance, commands: [...instance.commands!, insertChartFromSelection], actions: {
      slash: instance.actions!.slash, toolbar: [...instance.actions!.toolbar!, action],
    } }
  } }
}
