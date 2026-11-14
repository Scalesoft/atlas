import type { Node as PMNode } from '@tiptap/pm/model'
import { chartSourceRectangle, markdownDialect, markdownStrings, registerMarkdownStrings, type MarkdownStrings } from '../../core'
import { chartMarkdownLossy, type ChartData } from './data'

/**
 * Markdown dialect form of a chart (dialect spec §4.5): ```a-chart with an a-YAML header (`title`, `kind`, `source`),
 * a `---` line and the grid as a GFM table. A linked table is named by its `{#id}` paragraph and the 1-based rows and
 * columns of the rectangle (header row included); reading takes the values from that table. Loads with the dialect.
 */
const input = () => markdownStrings.input
/** Default (Czech) text of the `charts` section that only the dialect form renders. */
const dialectStrings = {
  invalidMarkdownGrid: 'Graf potřebuje za řádkem --- mřížku jako tabulku Markdownu (záhlaví, oddělovač, řádky).',
  linkedTableMissing: id => `Tabulka {#${id}} pro graf neexistuje.`,
  linkedTableWins: id => `Mřížka grafu se liší od tabulky {#${id}}; platí hodnoty tabulky.`,
} satisfies Partial<MarkdownStrings['charts']>
export type ChartsDialectKey = keyof typeof dialectStrings
const chartsStrings = registerMarkdownStrings('charts', dialectStrings)
const escapeCell = (cell: string) => cell.replace(/\\/g, '\\\\').replace(/\|/g, '\\|').replace(/\n/g, '\\n')

export function chartMarkdown(data: ChartData, document?: PMNode): string | undefined {
  if (chartMarkdownLossy(data, document)) return undefined
  const source = data.source && document ? chartSourceRectangle(document, data.source) : null
  const { markdownDialectFence, writeMarkdownDialectYaml } = markdownDialect()
  const header = writeMarkdownDialectYaml({
    ...(data.title ? { title: data.title } : {}), ...(data.kind !== 'bar' ? { kind: data.kind } : {}), ...(source ? { source } : {}),
  })
  const row = (cells: readonly string[]) => `| ${cells.map(escapeCell).join(' | ')} |`
  const grid = [row(data.grid[0]!), `| ${data.grid[0]!.map(() => '---').join(' | ')} |`, ...data.grid.slice(1).map(row)]
  return markdownDialectFence('a-chart', [...header ? [header] : [], '---', ...grid].join('\n'))
}

/** Cells of a GFM table row; `\|`, `\\` and `\n` are escapes, cells are trimmed (N11). */
function rowCells(line: string): string[] {
  const cells: string[] = []
  let current = '', closed = false
  const text = line.trim()
  for (let index = text.startsWith('|') ? 1 : 0; index < text.length; index++) {
    const char = text[index]!
    closed = false
    if (char === '\\' && index + 1 < text.length) { current += char + text[++index]; continue }
    if (char === '|') { cells.push(current); current = ''; closed = true; continue }
    current += char
  }
  if (!closed) cells.push(current)
  return cells.map(cell => cell.trim().replace(/\\([\\|n])/g, (_, escaped: string) => escaped === 'n' ? '\n' : escaped))
}

function readGrid(lines: string[]): string[][] {
  while (lines.length && !lines[lines.length - 1]!.trim()) lines.pop()
  const [header, delimiter, ...rows] = lines
  if (!header || !delimiter || !rowCells(delimiter).every(cell => /^:?-+:?$/.test(cell)) || rowCells(delimiter).length !== rowCells(header).length) {
    throw new TypeError(chartsStrings.invalidMarkdownGrid)
  }
  return [header, ...rows].map(rowCells)
}

const RANGE = /^([1-9]\d{0,2})-([1-9]\d{0,2})$/
function range(value: unknown): [number, number] {
  const match = typeof value === 'string' ? RANGE.exec(value) : null
  if (!match || Number(match[2]) < Number(match[1])) throw new TypeError(chartsStrings.invalidTableReference)
  return [Number(match[1]) - 1, Number(match[2]) - 1]
}

function known(value: unknown, keys: readonly string[]): Record<string, unknown> {
  if (value === null) return {}
  if (typeof value !== 'object' || Array.isArray(value)) throw new TypeError(input().dialectInvalidShape)
  const unknownKey = Object.keys(value).find(key => !keys.includes(key))
  if (unknownKey) throw new TypeError(input().dialectUnknownKey(unknownKey))
  return Object.fromEntries(Object.entries(value).filter(([, item]) => item !== null))
}

/** The linked table's cell ids and values for the rectangle; the table wins over the fence's grid. */
function linkedTable(element: HTMLElement, tableId: string, rows: [number, number], columns: [number, number]): { cells: string[][]; grid: string[][] } {
  let root: Element = element
  while (root.parentElement) root = root.parentElement
  const table = [...root.querySelectorAll('table[data-chart-ref]')].find(candidate => candidate.getAttribute('data-chart-ref') === tableId)
  if (!table) throw new TypeError(chartsStrings.linkedTableMissing(tableId))
  const trs = [...table.querySelectorAll('tr')].filter(tr => tr.closest('table') === table)
  const cells: string[][] = [], grid: string[][] = []
  for (let r = rows[0]; r <= rows[1]; r++) {
    const row = [...trs[r]?.children ?? []]
    const ids: string[] = [], values: string[] = []
    for (let c = columns[0]; c <= columns[1]; c++) {
      const cell = row[c]
      const id = cell?.getAttribute('data-chart-ref')
      if (!cell || !id) throw new TypeError(chartsStrings.invalidTableReference)
      ids.push(id); values.push(cell.textContent ?? '')
    }
    cells.push(ids); grid.push(values)
  }
  return { cells, grid }
}

export function readChartFence(element: HTMLElement): ChartData {
  const lines = (element.getAttribute('data-md-source') ?? '').replace(/\r\n?/g, '\n').split('\n')
  const separator = lines.indexOf('---')
  if (separator < 0) throw new TypeError(chartsStrings.invalidMarkdownGrid)
  const { isChartHeaderText, parseMarkdownDialectYaml, reportMarkdownDialectWarning } = markdownDialect()
  const header = known(parseMarkdownDialectYaml(lines.slice(0, separator).join('\n'), isChartHeaderText), ['title', 'kind', 'source'])
  const grid = readGrid(lines.slice(separator + 1))
  const base = { version: 1, title: header.title ?? '', kind: header.kind ?? 'bar' }
  if (header.source === undefined) return { ...base, grid, source: null, sourceStatus: null } as ChartData
  const source = known(header.source, ['table', 'rows', 'columns'])
  if (typeof source.table !== 'string') throw new TypeError(chartsStrings.invalidTableReference)
  const linked = linkedTable(element, source.table, range(source.rows), range(source.columns))
  if (JSON.stringify(linked.grid) !== JSON.stringify(grid)) reportMarkdownDialectWarning(element, chartsStrings.linkedTableWins(source.table))
  return { ...base, grid: linked.grid, source: { tableId: source.table, cells: linked.cells }, sourceStatus: 'linked' } as ChartData
}
