import { Extension, type Editor } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Plugin, type Transaction } from '@tiptap/pm/state'
import { Mapping } from '@tiptap/pm/transform'
import { CellSelection, TableMap } from '@tiptap/pm/tables'
import { defineMarkdownAttributeValidator } from '../../core'
import { normalizeChart, type ChartData, type ChartSource } from './data'

const types = ['table', 'tableRow', 'tableCell', 'tableHeader']
let sequence = 0
const newId = () => `chart-${globalThis.crypto?.randomUUID?.() ?? `${Date.now().toString(36)}-${++sequence}-${Math.random().toString(36).slice(2)}`}`

/**
 * Keep chart IDs unique: a copy of a table, pasted anywhere (before the original too), loses the IDs the original keeps. Only the table
 * a chart is made from (`within`, its range) gets IDs; other tables carry none (an ID on every table, row and cell made a page's stored
 * JSON and live state several times larger, and incompressible).
 */
export function ensureChartReferences(tr: Transaction, previous?: PMNode, mapping?: Mapping, within?: { from: number; to: number }): void {
  const preferred = new Map<string, number>()
  previous?.descendants((node, pos) => {
    if (!types.includes(node.type.name) || !node.attrs.chartRef) return
    const mapped = mapping?.mapResult(pos, 1)
    if (mapped && !mapped.deleted && tr.doc.nodeAt(mapped.pos)?.attrs.chartRef === node.attrs.chartRef) preferred.set(node.attrs.chartRef, mapped.pos)
  })
  const seen = new Set<string>()
  tr.doc.descendants((node, pos) => {
    if (!types.includes(node.type.name)) return
    const id = node.attrs.chartRef as string | null
    if (id && !seen.has(id) && (!preferred.has(id) || preferred.get(id) === pos)) { seen.add(id); return }
    const next = within && pos >= within.from && pos < within.to ? newId() : null
    if (!next && !id) return
    if (next) seen.add(next)
    tr.setNodeMarkup(pos, undefined, { ...node.attrs, chartRef: next })
  })
}

/** Selected rectangle must include the category column and a header row. */
export function selectedChartSource(tr: Transaction): { source: ChartSource; grid: string[][]; tableAt: number; insertAt: number } | null {
  const selection = tr.selection
  if (!(selection instanceof CellSelection)) return null
  const table = selection.$anchorCell.node(-1)
  const start = selection.$anchorCell.start(-1)
  const map = TableMap.get(table)
  if (map.problems?.length) return null
  const rect = map.rectBetween(selection.$anchorCell.pos - start, selection.$headCell.pos - start)
  if (rect.right - rect.left < 2 || rect.bottom - rect.top < 2 || rect.right - rect.left > 21 || rect.bottom - rect.top > 201) return null
  const cells: string[][] = [], grid: string[][] = []
  const used = new Set<number>()
  for (let row = rect.top; row < rect.bottom; row++) {
    const ids: string[] = [], values: string[] = []
    for (let col = rect.left; col < rect.right; col++) {
      const offset = map.map[row * map.width + col]
      if (used.has(offset)) return null
      used.add(offset)
      const cell = table.nodeAt(offset)!
      if (cell.attrs.colspan !== 1 || cell.attrs.rowspan !== 1) return null
      ids.push(cell.attrs.chartRef); values.push(cell.textContent)
    }
    cells.push(ids); grid.push(values)
  }
  return { source: { tableId: table.attrs.chartRef, cells }, grid, tableAt: start - 1, insertAt: start - 1 + table.nodeSize }
}

/** Move the caret into the first source cell of a linked chart and scroll it into view. */
export function focusChartSource(editor: Editor, source: ChartSource): boolean {
  const id = source.cells[0]?.[0]
  let found = -1
  editor.state.doc.descendants((node, pos) => {
    if (found < 0 && id && node.attrs.chartRef === id && (node.type.name === 'tableCell' || node.type.name === 'tableHeader')) found = pos
    return found < 0
  })
  return found >= 0 && editor.chain().focus().setTextSelection(found + 2).scrollIntoView().run()
}

export function refreshChartSource(data: ChartData, doc: PMNode): ChartData {
  if (!data.source) return data
  let table: PMNode | undefined
  doc.descendants(node => { if (node.type.name === 'table' && node.attrs.chartRef === data.source!.tableId) { table = node; return false } })
  if (!table) return { ...data, sourceStatus: 'missing' }
  const values = new Map<string, string>()
  table.descendants(cell => {
    if (cell.type.spec.tableRole === 'cell' || cell.type.spec.tableRole === 'header_cell') {
      if (cell.attrs.colspan === 1 && cell.attrs.rowspan === 1) values.set(cell.attrs.chartRef, cell.textContent)
      return false
    }
  })
  if (data.source.cells.some(row => row.some(id => !values.has(id)))) return { ...data, sourceStatus: 'invalid' }
  try { return normalizeChart({ ...data, sourceStatus: 'linked', grid: data.source.cells.map(row => row.map(id => values.get(id)!)) }) }
  catch { return { ...data, sourceStatus: 'invalid' } }
}

const holdsChartData = (node: PMNode) => node.type.name === 'chart' || (types.includes(node.type.name) && !!node.attrs.chartRef)

/**
 * Whether `after` has a top-level block that `before` does not (the same immutable node) holding a chart or a node with a chart ID.
 * A node inserted once more (the same object twice) counts as made: its copy needs IDs of its own.
 */
function madeChartBlock(before: PMNode, after: PMNode): boolean {
  const kept = new Set<PMNode>()
  before.forEach(block => { kept.add(block) })
  let found = false
  after.forEach(block => {
    if (found || kept.delete(block)) return
    found = holdsChartData(block)
    block.descendants(node => { found ||= holdsChartData(node); return !found })
  })
  return found
}

export const ChartReferences = Extension.create({
  name: 'chartReferences',
  addGlobalAttributes: () => [{ types, attributes: { chartRef: {
    default: null,
    validate: defineMarkdownAttributeValidator({ kind: 'text', nullable: true, minLength: 1, maxLength: 100 }, value => {
      if (value !== null && (typeof value !== 'string' || !value || value.length > 100)) throw new TypeError('Neplatný identifikátor tabulky.')
    }),
    parseHTML: element => element.getAttribute('data-chart-ref'),
    renderHTML: attrs => attrs.chartRef ? { 'data-chart-ref': attrs.chartRef } : {},
  } } }],
  addProseMirrorPlugins() {
    // After the first pass over the whole page, only a change that adds or removes a top-level block holding a chart or a node with
    // a chart ID can change the IDs or a chart's data: typing elsewhere does not walk the page (~13 ms a keystroke at 4x CPU, perf 4.0).
    let settled = false
    return [new Plugin({
      appendTransaction(transactions, oldState, state) {
        if (!transactions.some(transaction => transaction.docChanged)) return null
        if (settled && !madeChartBlock(oldState.doc, state.doc) && !madeChartBlock(state.doc, oldState.doc)) return null
        settled = true
        const tr = state.tr
        const mapping = new Mapping()
        transactions.forEach(transaction => mapping.appendMapping(transaction.mapping))
        ensureChartReferences(tr, oldState.doc, mapping)
        tr.doc.descendants((node, pos) => {
          if (node.type.name !== 'chart') return
          const data = normalizeChart(node.attrs.data)
          const next = refreshChartSource(data, tr.doc)
          if (JSON.stringify(next) !== JSON.stringify(data)) tr.setNodeMarkup(pos, undefined, { ...node.attrs, data: next })
        })
        // Appended changes belong to the originating transaction's undo event. Do not
        // set addToHistory:false: undo must restore the matching data snapshot too.
        return tr.docChanged ? tr : null
      },
    })]
  },
})
