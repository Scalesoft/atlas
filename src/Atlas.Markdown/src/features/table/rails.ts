import { measureTable } from './geometry'
import type { TableAxisCommand } from './commands'
import { isMarkdownComposition } from '../modules/composition'
import { iconSvgMarkup, type MarkdownIconName } from '../../ui/iconMarkup'
import { tableStrings } from '../../ui/strings/table'

export interface TableRailOptions {
  richTableFeatures: boolean
  runColumnCommand(table: HTMLTableElement, index: number, command: TableAxisCommand, extend?: boolean): void
  runRowCommand(table: HTMLTableElement, index: number, command: TableAxisCommand, extend?: boolean): void
  selectedRange?(axis: 'row' | 'column', index: number): { from: number; count: number } | null
  startRowResize(event: PointerEvent, table: HTMLTableElement, row: HTMLTableRowElement, index: number, handle: HTMLElement): void
  startColumnDrag(event: PointerEvent, table: HTMLTableElement, index: number, handle: HTMLElement): void
  startRowDrag(event: PointerEvent, table: HTMLTableElement, index: number, handle: HTMLElement): void
  moveRow(table: HTMLTableElement, from: number, to: number): void
  moveColumn(table: HTMLTableElement, from: number, to: number): void
}

type RailAxis = 'col' | 'row'
type RunCommand = (index: number, command: TableAxisCommand, extend?: boolean) => void
const retainedRails = new WeakMap<HTMLElement, { table: HTMLTableElement; rows: HTMLTableRowElement[]; columns: number; options: TableRailOptions }>()

/**
 * Measures once, then renders the accessible controls without reading editor state. A caller rendering several
 * tables passes each table's geometry measured up front, so writing one table's rails does not force another layout.
 */
export function renderTableRails(overlay: HTMLDivElement, table: HTMLTableElement, options: TableRailOptions,
  geometry = table.parentElement ? measureTable(table, table.parentElement) : undefined) {
  if (!geometry) return
  const { tableRect, wrapperRect, scrollLeft, scrollTop, columns, rows } = geometry
  Object.assign(overlay.style, {
    left: `${tableRect.left - wrapperRect.left + scrollLeft}px`, top: `${tableRect.top - wrapperRect.top + scrollTop}px`,
    width: `${tableRect.width}px`, height: `${tableRect.height}px`,
  })
  const previous = retainedRails.get(overlay)
  if (previous?.table === table && previous.columns === columns.length && previous.rows.length === rows.length
    && previous.options.richTableFeatures === options.richTableFeatures && rows.every((row, index) => row.row === previous.rows[index])
    && overlay.children.length === 2) {
    previous.options = options
    overlay.querySelectorAll<HTMLElement>('.tt-table-col-chip').forEach((chip, index) => {
      const column = columns[index]!
      Object.assign(chip.style, { left: `${column.left}px`, width: `${Math.max(24, column.width)}px` })
      updateSelection(chip, options.selectedRange?.('column', index), 'col')
    })
    overlay.querySelectorAll<HTMLElement>('.tt-table-row-chip').forEach((chip, index) => {
      const row = rows[index]!
      Object.assign(chip.style, { top: `${row.top}px`, height: `${Math.max(24, row.height)}px` })
      updateSelection(chip, options.selectedRange?.('row', index), 'row')
    })
    overlay.querySelectorAll<HTMLElement>('.tt-table-row-resize').forEach((handle, index) => { handle.style.top = `${rows[index]!.end - 2}px` })
    // Hover owns the insertion boundary; the new geometry only moves each inserter to that boundary's edge. A hidden
    // inserter left at the edge of a table that got smaller overflowed the wrapper and showed its scrollbars.
    for (const [axis, items] of [['col', columns], ['row', rows]] as const) {
      const insert = overlay.querySelector<HTMLElement>(`.tt-table-${axis}-insert`)
      const boundary = Number(insert?.dataset.boundary)
      if (insert && boundary > 0) insert.style.setProperty('--tt-insert-offset', `${items[boundary - 1]!.end}px`)
    }
    return
  }
  overlay.replaceChildren()
  setMaskIcons(overlay)
  retainedRails.delete(overlay)
  if (!columns.length || !rows.length) return
  const retained = { table, rows: rows.map(row => row.row), columns: columns.length, options }
  retainedRails.set(overlay, retained)

  const doc = table.ownerDocument
  const colRail = doc.createElement('div')
  colRail.className = 'tt-table-col-rail'
  const rowRail = doc.createElement('div')
  rowRail.className = 'tt-table-row-rail'
  const columnCommand: RunCommand = (index, command, extend) => retained.options.runColumnCommand(table, index, command, extend)
  const rowCommand: RunCommand = (index, command, extend) => retained.options.runRowCommand(table, index, command, extend)

  columns.forEach((column, index) => {
    const chip = selectionChip(doc, 'col', index, columnCommand)
    const range = options.selectedRange?.('column', index)
    chip.setAttribute('aria-pressed', String(!!range))
    if (range) chip.dataset.selected = 'true'
    const count = range?.count ?? 1
    chip.title = tableStrings.moveColumnsTitle(tableStrings.columnQuantityLabel(count))
    chip.setAttribute('aria-keyshortcuts', 'Enter Shift+Enter Delete Shift+ArrowLeft Shift+ArrowRight')
    chip.addEventListener('pointerdown', event => {
      if ((event.target as Element).closest('.tt-table-handle-del')) return
      retained.options.startColumnDrag(event, table, index, chip)
    })
    chip.addEventListener('keydown', event => {
      if (isMarkdownComposition(event) || !event.shiftKey || !['ArrowLeft', 'ArrowRight'].includes(event.key)) return
      event.preventDefault()
      event.stopPropagation()
      retained.options.moveColumn(table, index, index + (event.key === 'ArrowRight' ? 1 : -1))
    })
    Object.assign(chip.style, { left: `${column.left}px`, minWidth: '24px', width: `${Math.max(24, column.width)}px` })
    colRail.appendChild(chip)
  })
  const colInsert = inserter(doc, 'col', columns.length, columnCommand)
  colInsert.style.setProperty('--tt-insert-offset', `${columns[columns.length - 1].end}px`)
  colRail.appendChild(colInsert)

  rows.forEach((row, index) => {
    const chip = selectionChip(doc, 'row', index, rowCommand)
    const range = options.selectedRange?.('row', index)
    chip.setAttribute('aria-pressed', String(!!range))
    if (range) chip.dataset.selected = 'true'
    const count = range?.count ?? 1
    chip.title = tableStrings.moveRowsTitle(tableStrings.rowQuantityLabel(count))
    chip.setAttribute('aria-keyshortcuts', 'Enter Shift+Enter Delete Shift+ArrowUp Shift+ArrowDown')
    chip.addEventListener('pointerdown', event => {
      if ((event.target as Element).closest('.tt-table-handle-del')) return
      retained.options.startRowDrag(event, table, index, chip)
    })
    chip.addEventListener('keydown', event => {
      if (isMarkdownComposition(event) || !event.shiftKey || !['ArrowUp', 'ArrowDown'].includes(event.key)) return
      event.preventDefault()
      event.stopPropagation()
      retained.options.moveRow(table, index, index + (event.key === 'ArrowDown' ? 1 : -1))
    })
    Object.assign(chip.style, { top: `${row.top}px`, minHeight: '24px', height: `${Math.max(24, row.height)}px` })
    rowRail.appendChild(chip)
  })
  const rowInsert = inserter(doc, 'row', rows.length, rowCommand)
  rowInsert.style.setProperty('--tt-insert-offset', `${rows[rows.length - 1].end}px`)
  rowRail.appendChild(rowInsert)

  if (options.richTableFeatures) rows.forEach((row, index) => {
    const handle = doc.createElement('div')
    handle.className = 'tt-table-row-resize'
    handle.dataset.testid = 'table-row-resize'
    handle.setAttribute('role', 'separator')
    handle.setAttribute('aria-orientation', 'horizontal')
    handle.setAttribute('aria-label', tableStrings.resizeRowHeightLabel(index + 1))
    handle.style.top = `${row.end - 2}px`
    handle.addEventListener('pointerdown', event => {
      preventBubbleAndDefault(event)
      retained.options.startRowResize(event, table, row.row, index, handle)
    })
    rowRail.appendChild(handle)
  })
  overlay.append(colRail, rowRail)
}

function updateSelection(chip: HTMLElement, range: { from: number; count: number } | null | undefined, axis: RailAxis) {
  chip.setAttribute('aria-pressed', String(!!range))
  if (range) chip.dataset.selected = 'true'; else delete chip.dataset.selected
  const count = range?.count ?? 1
  chip.title = axis === 'col'
    ? tableStrings.moveColumnsTitle(tableStrings.columnQuantityLabel(count))
    : tableStrings.moveRowsTitle(tableStrings.rowQuantityLabel(count))
}

function selectionChip(doc: Document, axis: RailAxis, index: number, run: RunCommand): HTMLButtonElement {
  const chip = doc.createElement('button')
  chip.type = 'button'
  chip.className = `tt-table-${axis}-chip`
  chip.dataset.testid = `table-${axis}-chip`
  chip.dataset[axis === 'col' ? 'colIndex' : 'rowIndex'] = String(index)
  chip.setAttribute('aria-label', axis === 'col' ? tableStrings.columnPositionLabel(index + 1) : tableStrings.rowPositionLabel(index + 1))
  chip.setAttribute('aria-keyshortcuts', 'Enter Delete')
  chip.appendChild(selectionGrip(doc))

  // One native button/tab stop per axis item, with a visual delete hit area.
  const del = doc.createElement('span')
  del.className = 'tt-table-handle-del'
  del.dataset.testid = `table-${axis}-delete`
  del.dataset.tone = 'danger'
  del.setAttribute('aria-hidden', 'true')
  chip.appendChild(del)
  chip.addEventListener('mousedown', preventBubbleAndDefault)
  chip.addEventListener('pointerdown', preventBubbleAndDefault)
  chip.addEventListener('click', event => {
    const deleting = del.contains(event.target as Node)
    if (deleting) event.stopPropagation()
    run(index, deleting ? 'delete' : 'select', event.shiftKey)
  })
  chip.addEventListener('keydown', event => {
    if (isMarkdownComposition(event)) return
    if (event.shiftKey && (event.key === 'Enter' || event.key === ' ')) {
      event.preventDefault(); run(index, 'select', true); return
    }
    if (event.key === 'Delete' || event.key === 'Backspace') {
      event.preventDefault()
      run(index, 'delete')
    }
  })
  return chip
}

function inserter(doc: Document, axis: RailAxis, count: number, run: RunCommand): HTMLButtonElement {
  const button = doc.createElement('button')
  button.type = 'button'
  button.className = `tt-table-${axis}-insert`
  button.dataset.testid = `table-${axis}-insert`
  button.append(Object.assign(doc.createElement('span'), { className: 'tt-insert-icon' }))
  button.dataset.boundary = String(count)
  button.setAttribute('aria-label', axis === 'col' ? tableStrings.insertColumnAfterLabel(count) : tableStrings.insertRowAfterLabel(count))
  button.addEventListener('mousedown', preventBubbleAndDefault)
  button.addEventListener('pointerdown', preventBubbleAndDefault)
  button.addEventListener('click', event => {
    event.stopPropagation()
    const boundary = Number(button.dataset.boundary ?? count)
    if (boundary <= 0) run(0, 'insertBefore')
    else run(Math.min(boundary - 1, count - 1), 'insertAfter')
  })
  return button
}

function preventBubbleAndDefault(event: MouseEvent) {
  event.preventDefault()
  event.stopPropagation()
}

function selectionGrip(doc: Document): HTMLSpanElement {
  const grip = doc.createElement('span')
  grip.className = 'tt-table-handle-grip'
  grip.setAttribute('aria-hidden', 'true')
  return grip
}


/** Control icons are CSS masks set once per overlay: a drawing per chip was a third of the table's DOM. */
const maskIcons: Record<string, string> = {}
function setMaskIcons(overlay: HTMLElement) {
  for (const [property, name] of [['grip', 'grip-vertical'], ['trash', 'trash-2'], ['plus', 'plus']] as [string, MarkdownIconName][]) {
    maskIcons[property] ??= `url("data:image/svg+xml,${encodeURIComponent(iconSvgMarkup(name, 16).replace('currentColor', '#000'))}")`
    overlay.style.setProperty(`--tt-ic-${property}`, maskIcons[property])
  }
}
