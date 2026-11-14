import type { Node as PMNode } from '@tiptap/pm/model'
import { chartSourceRectangle } from '../../core'
import { chartsStrings } from './strings'

export type ChartKind = 'bar' | 'line' | 'pie' | 'doughnut'
export interface ChartSource { tableId: string; cells: string[][] }
export interface ChartData {
  version: 1
  title: string
  kind: ChartKind
  /** First row names series; first column names categories. Empty values are gaps. */
  grid: string[][]
  source: ChartSource | null
  sourceStatus: 'linked' | 'missing' | 'invalid' | null
}
export const defaultChart: ChartData = {
  version: 1, get title() { return chartsStrings.newChartTitle }, kind: 'bar', grid: [['Období', 'Hodnota'], ['Leden', '12'], ['Únor', '18']],
  source: null, sourceStatus: null,
}

/** Czech decimal comma and properly grouped spaces; never silently turn invalid input into zero. */
export function parseChartNumber(value: string): number | null {
  const trimmed = value.trim()
  if (!trimmed) return null
  if (!/^[+-]?(?:\d+|\d{1,3}(?:[ \u00a0\u202f]\d{3})+)(?:[,.]\d+)?$/.test(trimmed)) {
    throw new TypeError(chartsStrings.notANumber(value))
  }
  const number = Number(trimmed.replace(/[ \u00a0\u202f]/g, '').replace(',', '.'))
  if (!Number.isFinite(number)) throw new TypeError(chartsStrings.numberTooLarge)
  return number
}

export function validateChartGrid(value: unknown): string[][] {
  if (!Array.isArray(value) || value.length < 2 || value.length > 201) throw new TypeError(chartsStrings.gridNeedsHeaderAndRows)
  const width = Array.isArray(value[0]) ? value[0].length : 0
  if (width < 2 || width > 21) throw new TypeError(chartsStrings.gridNeedsLabelsAndSeries)
  return value.map((row, r) => {
    if (!Array.isArray(row) || row.length !== width) throw new TypeError(chartsStrings.rowsMustMatchWidth)
    return row.map((cell, c) => {
      if (typeof cell !== 'string' || cell.length > 2000) throw new TypeError(chartsStrings.cellTooLong)
      if (r > 0 && c > 0) {
        try { parseChartNumber(cell) } catch (error) { throw new TypeError(chartsStrings.cellError(r + 1, c + 1, (error as Error).message)) }
      }
      return cell
    })
  })
}

export function normalizeChart(value: unknown): ChartData {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new TypeError(chartsStrings.invalidChartData)
  const data = value as Record<string, unknown>
  if (data.version !== 1 || !['bar', 'line', 'pie', 'doughnut'].includes(String(data.kind))) throw new TypeError(chartsStrings.invalidVersionOrKind)
  if (typeof data.title !== 'string' || data.title.length > 300) throw new TypeError(chartsStrings.titleTooLong)
  const grid = validateChartGrid(data.grid)
  if ((data.kind === 'pie' || data.kind === 'doughnut') && grid.some((row, r) => r > 0 && row.some((cell, c) => c > 0 && (parseChartNumber(cell) ?? 0) < 0))) {
    throw new TypeError(chartsStrings.negativeValuesNotAllowed)
  }
  let source: ChartSource | null = null
  if (data.source != null) {
    const raw = data.source as ChartSource
    const validId = (id: unknown): id is string => typeof id === 'string' && id.length > 0 && id.length <= 100
    if (!validId(raw.tableId) || !Array.isArray(raw.cells) || raw.cells.length !== grid.length || raw.cells.some(row => !Array.isArray(row) || row.length !== grid[0].length || row.some(id => !validId(id)))) throw new TypeError(chartsStrings.invalidTableReference)
    if (new Set(raw.cells.flat()).size !== raw.cells.flat().length) throw new TypeError(chartsStrings.mergedCellsNotSupported)
    source = { tableId: raw.tableId, cells: raw.cells.map(row => [...row]) }
  }
  const status = data.sourceStatus
  if (source ? !['linked', 'missing', 'invalid'].includes(String(status)) : status != null) throw new TypeError(chartsStrings.invalidSourceStatus)
  return { version: 1, title: data.title, kind: data.kind as ChartKind, grid, source, sourceStatus: source ? status as ChartData['sourceStatus'] : null }
}

export function chartText(data: ChartData): string {
  return `${data.title}\n${data.grid.map(row => row.join(' · ')).join('\n')}`
}

/**
 * Whether the ```a-chart form cannot carry the chart, so Markdown keeps the readable fallback: a grid cell with `\r`
 * (no escape in a grid cell), or a linked table the dialect cannot name in the document being written.
 */
export const chartMarkdownLossy = (data: ChartData, document?: PMNode): boolean => data.grid.some(row => row.some(cell => cell.includes('\r')))
  || (!!data.source && !(document && chartSourceRectangle(document, data.source)))
