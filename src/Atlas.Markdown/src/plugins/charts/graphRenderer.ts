import { init, use, type EChartsType } from 'echarts/core'
import { GraphChart } from 'echarts/charts'
// The charts' own registrations (tooltip, SVG renderer): ECharts is set up in this plugin only.
import './chartRenderer'

use([GraphChart])

/** A network (people and topics) outside a document, on the charts' ECharts: SVG; its owner sets the tooltip's renderMode to richText. */
export const createGraphChart = (host: HTMLElement): EChartsType => init(host, undefined, { renderer: 'svg' })
