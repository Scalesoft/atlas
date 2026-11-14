import { Table } from '@tiptap/extension-table'
import { Fragment, type Node } from '@tiptap/pm/model'
import { Plugin, type EditorState, type Transaction } from '@tiptap/pm/state'
import { isRemoteTransaction } from './recognition/gate'
import { fixTables, tableEditingKey } from '@tiptap/pm/tables'
import type { MarkdownNodeSpec } from 'tiptap-markdown'
import { loadedMarkdownDialect, MARKDOWN_DIALECT_ID, markdownDialect } from '../engine/markdownDialectLoader'
import type { MarkdownDialectYaml } from '../engine/markdownDialect'

// The installed tiptap-markdown package does not publish its serializer state type.
interface TableSerializerState {
  inTable: boolean
  out: string
  nodes: unknown
  marks: unknown
  options: unknown
  write(value: string): void
  text(value: string, escape?: boolean): void
  ensureNewLine(): void
  renderInline(node: Node): void
  renderContent(node: Node): void
  closeBlock(node: Node): void
}

/** A table GFM cannot carry: the dialect's ```a-table (§4.12), each cell's blocks as Markdown. MarkdownDialect.cs writes the same. */
function writeTableFence(state: TableSerializerState, table: Node): void {
  const dialect = loadedMarkdownDialect() ?? markdownDialect()
  const rows: MarkdownDialectYaml[] = []
  table.forEach(row => {
    const cells: MarkdownDialectYaml[] = []
    row.forEach(cell => {
      const content = new (state.constructor as new (nodes: unknown, marks: unknown, options: unknown) => TableSerializerState)(state.nodes, state.marks, state.options)
      content.renderContent(cell)
      const align = cellAlignment(cell)
      cells.push({
        ...(cell.type.name === 'tableHeader' ? { header: true } : {}),
        ...(cell.attrs.colspan > 1 ? { colspan: cell.attrs.colspan as number } : {}),
        ...(cell.attrs.rowspan > 1 ? { rowspan: cell.attrs.rowspan as number } : {}),
        ...(align ? { align } : {}),
        content: content.out,
      })
    })
    rows.push({ cells })
  })
  state.text(dialect.markdownDialectFence('a-table', dialect.writeMarkdownDialectYaml(rows)), false)
  state.closeBlock(table)
}

const configuredParsers = new WeakSet<object>()
const tableBreakParsing: NonNullable<MarkdownNodeSpec['parse']> = {
  setup(md) {
    if (configuredParsers.has(md)) return
    configuredParsers.add(md)
    md.inline.ruler.before('html_inline', 'atlas_table_break', (state, silent) => {
      if (!state.env.atlasTableBreaks) return false
      const match = /^<br\s*\/?\s*>/i.exec(state.src.slice(state.pos))
      if (!match) return false
      if (!silent) state.push('hardbreak', 'br', 0)
      state.pos += match[0].length
      return true
    })
    md.core.ruler.after('inline', 'atlas_table_breaks', state => {
      let tableDepth = 0
      for (const token of state.tokens) {
        if (token.type === 'table_open') tableDepth++
        if (token.type === 'table_close') tableDepth--
        if (tableDepth && token.type === 'inline') {
          // Reparse only table cells with this narrow bare-br allowlist. General
          // HTML remains disabled; normal escape/code rules still run first.
          md.inline.parse(token.content, md, { ...state.env, atlasTableBreaks: true }, token.children = [])
        }
      }
    })
  },
}

function renderCell(state: TableSerializerState, paragraph: Node): void {
  let content: Node[] = []
  const flush = () => {
    const start = state.out.length
    state.renderInline(paragraph.copy(Fragment.fromArray(content)))
    state.out = state.out.slice(0, start) + state.out.slice(start).replace(/\|/g, '\\|')
    content = []
  }
  paragraph.forEach(node => {
    if (node.type.name === 'hardBreak') { flush(); state.write('<br>'); return }
    if (node.isText && /[\r\n]/.test(node.text!)) {
      node.text!.split(/\r\n|\r|\n/).forEach((part, index) => {
        if (index) { flush(); state.write('<br>') }
        if (part) content.push(node.type.schema.text(part, node.marks))
      })
    } else content.push(node)
  })
  flush()
}

export function cellAlignment(cell: Node): string | null {
  return cell.attrs.textAlign || cell.attrs.align || null
}

export function tableMarkdownLossy(table: Node): boolean {
  const header = table.firstChild!
  let lossy = false
  table.forEach((row, _offset, rowIndex) => {
    if (row.childCount !== header.childCount) lossy = true
    row.forEach((cell, _cellOffset, column) => {
      cell.descendants(node => { if (node.isText && /[\r\n]/.test(node.text!)) lossy = true })
      if (cell.type.name !== (rowIndex === 0 ? 'tableHeader' : 'tableCell')
        || cell.attrs.colspan > 1 || cell.attrs.rowspan > 1
        || cell.childCount !== 1 || cell.firstChild?.type.name !== 'paragraph'
        || cellAlignment(cell) !== cellAlignment(header.child(Math.min(column, header.childCount - 1)))) lossy = true
    })
  })
  return lossy
}

/** Loss that the markdown-safe header/alignment append transaction cannot normalize. */
export function tableMarkdownStructurallyLossy(table: Node): boolean {
  const header = table.firstChild!
  let lossy = false
  table.forEach(row => {
    if (row.childCount !== header.childCount) lossy = true
    row.forEach(cell => {
      cell.descendants(node => { if (node.isText && /[\r\n]/.test(node.text!)) lossy = true })
      if (cell.attrs.colspan > 1 || cell.attrs.rowspan > 1
        || cell.childCount !== 1 || cell.firstChild?.type.name !== 'paragraph') lossy = true
    })
  })
  return lossy
}

/** A change y-prosemirror renders from the shared Yjs document: a collaborator's edit or a Yjs undo. */
export const isRemoteChange = isRemoteTransaction

/** Meta of a transaction that asks for the tables starting at these positions to be repaired (CollaborationTableRepair sends it). */
export const TABLE_REPAIR = 'markdownTableRepair'

/** The requested table positions, mapped into the batch's final document. */
export function tableRepairRequest(transactions: readonly Transaction[]): number[] | null {
  const index = transactions.findIndex(transaction => transaction.getMeta(TABLE_REPAIR))
  if (index < 0) return null
  const later = transactions.slice(index + 1)
  return (transactions[index]!.getMeta(TABLE_REPAIR) as number[]).map(pos => later.reduce((at, transaction) => transaction.mapping.map(at), pos))
}

function fixTablesAt(state: EditorState, positions: number[]): Transaction | undefined {
  // fixTables repairs the tables that differ from the old document; this old document differs in exactly the requested ones.
  const old = positions.reduce((tr, pos) => {
    const table = tr.doc.nodeAt(pos)
    return table?.type.spec.tableRole === 'table' ? tr.replaceWith(pos, pos + table.nodeSize, table.type.create(table.attrs, table.content, table.marks)) : tr
  }, state.tr).doc
  return fixTables(state, { doc: old } as EditorState)
}

/** Whether the table is written as a GFM table (one textblock per cell, no spans, equal rows). */
function tableHasInlineCells(table: Node): boolean {
  let inlineCells = true
  table.forEach(row => {
    if (row.childCount !== table.firstChild!.childCount) inlineCells = false
    row.forEach(cell => {
      if (cell.attrs.colspan > 1 || cell.attrs.rowspan > 1 || cell.childCount !== 1 || !cell.firstChild?.isTextblock) inlineCells = false
    })
  })
  return inlineCells
}

export function tableById(document: Node, id: string): Node | undefined {
  let table: Node | undefined
  document.descendants(node => {
    if (table) return false
    if (node.type.spec.tableRole === 'table' && node.attrs.chartRef === id) table = node
  })
  return table
}

/**
 * The Markdown dialect's `source` of a chart (a-chart, §4.5): the linked table's `{#id}` and the 1-based rows and
 * columns (header row included) its cells cover. Null when the dialect cannot carry the link: the table is missing,
 * is not written as a GFM table, its id is not a dialect id, or the cells are not one rectangle of it.
 */
export function chartSourceRectangle(document: Node, source: { readonly tableId: string; readonly cells: readonly (readonly string[])[] }): { table: string; rows: string; columns: string } | null {
  const table = MARKDOWN_DIALECT_ID.test(source.tableId) ? tableById(document, source.tableId) : undefined
  if (!table || !tableHasInlineCells(table) || !source.cells[0]?.length) return null
  const at = new Map<string, readonly [number, number]>()
  table.forEach((row, _offset, r) => row.forEach((cell, _cellOffset, c) => { if (cell.attrs.chartRef) at.set(cell.attrs.chartRef as string, [r, c]) }))
  const origin = at.get(source.cells[0][0]!)
  if (!origin || !source.cells.every((row, r) => row.every((id, c) => at.get(id)?.[0] === origin[0] + r && at.get(id)?.[1] === origin[1] + c))) return null
  return { table: source.tableId, rows: `${origin[0] + 1}-${origin[0] + source.cells.length}`, columns: `${origin[1] + 1}-${origin[1] + source.cells[0].length}` }
}

// tiptap-markdown's default table serializer drops alignment and replaces
// unsupported table shapes with "[table]" when HTML is disabled.
export const MarkdownTable = Table.extend({
  addProseMirrorPlugins() {
    return (this.parent?.() ?? []).map(plugin => {
      const append = plugin.spec.appendTransaction
      if (plugin.spec.key !== tableEditingKey || !append) return plugin
      return new Plugin({
        ...plugin.spec,
        appendTransaction(transactions, oldState, state) {
          const requested = tableRepairRequest(transactions)
          if (requested) return fixTablesAt(state, requested)?.setMeta('addToHistory', false) ?? null
          // Every collaborator renders a remote change, and y-prosemirror writes nothing to Yjs while it does: a repair appended here
          // would stay local and ride along with each collaborator's next edit, adding the same cells once per collaborator, which
          // breaks the table again on every merge. CollaborationTableRepair asks one collaborator to repair it.
          if (transactions.some(isRemoteChange)) return null
          return append.call(plugin, transactions, oldState, state)
        },
      })
    })
  },
  addStorage() {
    return {
      markdown: {
        parse: tableBreakParsing,
        serialize(state: TableSerializerState, table: Node) {
          if (!tableHasInlineCells(table)) { writeTableFence(state, table); return }
          state.inTable = true
          table.forEach((row, _offset, index) => {
            state.write('| ')
            row.forEach((cell, _cellOffset, column) => {
              if (column) state.write(' | ')
              renderCell(state, cell.firstChild!)
            })
            state.write(' |')
            state.ensureNewLine()
            if (index === 0) {
              const delimiters: string[] = []
              const alignmentDelimiters: Record<string, string> = { left: ':---', center: ':---:', right: '---:' }
              row.forEach(cell => delimiters.push(alignmentDelimiters[cellAlignment(cell) ?? ''] ?? '---'))
              state.write(`| ${delimiters.join(' | ')} |`)
              state.ensureNewLine()
            }
          })
          state.closeBlock(table)
          state.inTable = false
          // A table a chart links to carries its id (dialect `{#id}` paragraph, §4.5); only a chart links one.
          loadedMarkdownDialect()?.writeChartTableId(state, table)
        },
      },
    }
  },
})
