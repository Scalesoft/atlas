import { getChangedRanges } from '@tiptap/core'
import type { Fragment, Node } from '@tiptap/pm/model'
import type { Transaction } from '@tiptap/pm/state'
import { TableMap } from '@tiptap/pm/tables'
import { imagePresentation } from '../features/images/presentation'
import {
  isValidAlignment, isValidCellColor, isValidColumnWidths, isValidRowHeight,
  isValidTableSize, isValidTableSpan,
} from '../core/documentConstraints'

/** Domain checks that Tiptap's permissive attribute schema cannot express. */
export function validateBuiltinDocument(document: Node): void {
  validateBuiltinDocumentConstraints(document)
  document.descendants(node => {
    if (node.type.name !== 'table') return
    const map = TableMap.get(node)
    if (map.problems?.length) throw new TypeError(`Invalid table geometry: ${map.problems.map(problem => problem.type).join(', ')}.`)
  })
}

/** Node levels a document may nest, the document node being level 1 and text counting as a level (MarkdownDocumentValidator.MaxDepth). */
export const MAX_DOCUMENT_DEPTH = 64

const singleLine = (value: unknown, maxLength: number) =>
  value == null || (typeof value === 'string' && value.length <= maxLength && !/[\u0000-\u001f\u007f]/.test(value))

/**
 * The server rules (`enforcement: "server"` in schema/builtin.ts) that pasted HTML or Markdown can break: an `<ol start>`
 * beyond a safe integer, a long `<ol type>`, a long or multi-line fence language (nesting: nestsTooDeep). A server refuses
 * such a document and in collaboration that stops the session, so a paste producing one is refused. Opening a stored
 * document does not apply them.
 */
export function validatePastedDocumentRules(document: Node): void {
  document.descendants(node => {
    const { start, type, language } = node.attrs
    if ((node.type.name === 'codeBlock' && !singleLine(language, 200))
      || (node.type.name === 'orderedList' && ((start != null && !Number.isSafeInteger(start)) || !singleLine(type, 100)))) {
      throw new TypeError('Pasted content breaks a document rule.')
    }
  })
}

/** Whether a transaction nests changed content past MAX_DOCUMENT_DEPTH, deeper than that content was before (Tab, wrap, indent). */
export function nestsTooDeep(transaction: Transaction): boolean {
  const ranges = getChangedRanges(transaction)
  if (!ranges.length) return false
  const height = (node: Node): number => { let below = 0; node.forEach(child => { below = Math.max(below, height(child)) }); return below + 1 }
  // Over the envelope of the changed ranges: a wrap (ReplaceAroundStep) changes only its wrapper's two edges.
  const deepest = (doc: Node, side: 'oldRange' | 'newRange') => {
    const from = Math.min(...ranges.map(range => range[side].from)), to = Math.max(...ranges.map(range => range[side].to))
    let level = 0
    doc.nodesBetween(from, to, (node, pos) => {
      if (node.childCount && (pos < from || pos + node.nodeSize > to)) return true
      level = Math.max(level, doc.resolve(pos).depth + 1 + height(node))
      return false
    })
    return level
  }
  const level = deepest(transaction.doc, 'newRange')
  return level > MAX_DOCUMENT_DEPTH && level > deepest(transaction.before, 'oldRange')
}

/** Hard bounds only: do not reject holes/header types that table plugins repair. */
export function validateBuiltinDocumentConstraints(document: Node | Fragment): void {
  document.descendants(node => {
    switch (node.type.name) {
      case 'image': imagePresentation(node.attrs.presentation); break
      case 'table': {
        const rows: Fragment[] = []
        node.forEach(row => rows.push(row.content))
        validateTableDimensions(rows)
        break
      }
      case 'tableRow':
        if ('height' in node.attrs && !isValidRowHeight(node.attrs.height)) throw new TypeError('Invalid table row height.')
        break
      case 'tableCell':
      case 'tableHeader':
        validateCell(node)
        break
      case 'paragraph':
      case 'heading':
        if ('textAlign' in node.attrs && !isValidAlignment(node.attrs.textAlign)) throw new TypeError('Invalid text alignment.')
        break
    }
  })
}

/** Bound the rectangle before TableMap/pastedCells can allocate or fill it.
 * Counts rowspan coverage as well as direct cells, including malformed rows
 * that paste's rectangularization would extend with empty cells.
 */
export function validateTableDimensions(rows: readonly Fragment[]): void {
  if (!isValidTableSize(rows.length, 1)) throw new TypeError('Table dimensions exceed the supported limit.')
  const widths: number[] = []
  let widestRow = 0, height = rows.length
  rows.forEach((row, rowIndex) => {
    row.forEach(cell => {
      const { colspan, rowspan } = cell.attrs
      if (!isValidTableSpan(colspan)) throw new TypeError('Invalid table cell colspan.')
      if (!isValidTableSpan(rowspan)) throw new TypeError('Invalid table cell rowspan.')
      height = Math.max(height, rowIndex + rowspan)
      if (!isValidTableSize(height, Math.max(widestRow, colspan))) throw new TypeError('Table dimensions exceed the supported limit.')
      for (let row = rowIndex; row < rowIndex + rowspan; row++) {
        widths[row] = (widths[row] ?? 0) + colspan
        widestRow = Math.max(widestRow, widths[row])
        if (!isValidTableSize(height, widestRow)) throw new TypeError('Table dimensions exceed the supported limit.')
      }
    })
  })
  if (!isValidTableSize(height, widestRow)) throw new TypeError('Table dimensions exceed the supported limit.')
}

function validateCell(cell: Node): void {
  const { colspan, rowspan, colwidth, backgroundColor, textAlign, align } = cell.attrs
  if (!isValidTableSpan(colspan)) throw new TypeError('Invalid table cell colspan.')
  if (!isValidTableSpan(rowspan)) throw new TypeError('Invalid table cell rowspan.')
  if (!isValidColumnWidths(colwidth, colspan)) throw new TypeError('Invalid table cell column widths.')
  if ('backgroundColor' in cell.attrs && !isValidCellColor(backgroundColor)) throw new TypeError('Invalid table cell background color.')
  if ('textAlign' in cell.attrs && !isValidAlignment(textAlign)) throw new TypeError('Invalid table cell alignment.')
  if ('align' in cell.attrs && !isValidAlignment(align)) throw new TypeError('Invalid table cell alignment.')
}
