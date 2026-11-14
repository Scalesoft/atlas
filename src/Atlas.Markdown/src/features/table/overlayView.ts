import type { Editor } from '@tiptap/core'
import type { EditorView } from '@tiptap/pm/view'
import type { PluginView } from '@tiptap/pm/state'
import type { Node } from '@tiptap/pm/model'
import { CellSelection } from '@tiptap/pm/tables'
import { findTablePosition, runTableAxisCommand, tableAxisRange, selectedTableAxisRange, type TableAxis, type TableAxisCommand } from './commands'
import { trackTableHover } from './hover'
import { renderTableRails } from './rails'
import { measureTable } from './geometry'
import { createRowResizeSession } from './rowResize'
import { createTableAxisDragSession } from './axisDrag'
import { moveRow } from './rowOrder'
import { moveColumn } from './columnOrder'

interface TableOverlay {
  element: HTMLDivElement
  stopHover: () => void
  resizeObserver?: ResizeObserver
  mutationObserver?: MutationObserver
  /** Geometry the rails were last drawn for, and whether they showed a cell selection. */
  signature?: string
  highlighted?: boolean
}

/** Owns every DOM node, observer, gesture and scheduled callback created for this view. */
export function createTableControlsView(editor: Editor, view: EditorView, richTableFeatures: boolean): PluginView {
  const overlays = new Map<HTMLTableElement, TableOverlay>()
  const doc = view.dom.ownerDocument
  const win = doc.defaultView!
  let destroyed = false
  let frame: number | null = null
  let editable = view.editable
  let tablesInDocument = collectTables(view.state.doc)
  const resize = createRowResizeSession(editor, scheduleSync)
  const columnDrag = createTableAxisDragSession(editor, scheduleSync)
  const rowDrag = createTableAxisDragSession(editor, scheduleSync, 'row')
  let keyboardTarget: { table: HTMLTableElement; index: number; axis: 'col' | 'row' } | null = null
  // Controls exist only for the table under the pointer (sticky until another is hovered), the one holding the
  // selection and the one holding focus: a page with many tables carried a few hundred control elements per table.
  let hovered: HTMLTableElement | null = null
  let selected: HTMLTableElement | null = null

  function selectionTable(): HTMLTableElement | null {
    const { $from } = view.state.selection
    for (let depth = $from.depth; depth > 0; depth--) {
      if ($from.node(depth).type.name === 'table') return tableIn(view.nodeDOM($from.before(depth)))
    }
    return null
  }
  const onOver = (event: Event) => {
    const table = tableIn(event.target)
    if (view.editable && table && table !== hovered) { hovered = table; scheduleSync() }
  }
  view.dom.addEventListener('mouseover', onOver)

  function dispose(table: HTMLTableElement, overlay: TableOverlay) {
    overlay.stopHover()
    overlay.resizeObserver?.disconnect()
    overlay.mutationObserver?.disconnect()
    overlay.element.remove()
    overlays.delete(table)
  }

  function createOverlay(table: HTMLTableElement): TableOverlay {
    const element = doc.createElement('div')
    element.className = 'tt-table-controls'
    element.dataset.testid = 'table-controls'
    element.contentEditable = 'false'
    table.parentElement?.appendChild(element)
    const overlay: TableOverlay = { element, stopHover: () => {} }
    const observeChange = () => { if (!resize.active && !columnDrag.active && !rowDrag.active) scheduleSync() }
    if (typeof ResizeObserver !== 'undefined') {
      overlay.resizeObserver = new ResizeObserver(observeChange)
      overlay.resizeObserver.observe(table)
    }
    if (typeof MutationObserver !== 'undefined') {
      overlay.mutationObserver = new MutationObserver(observeChange)
      overlay.mutationObserver.observe(table, {
        attributes: true, attributeFilter: ['style', 'colspan', 'rowspan', 'data-colwidth'], subtree: true,
      })
    }
    overlays.set(table, overlay)
    return overlay
  }

  function run(table: HTMLTableElement, axis: TableAxis, index: number, command: TableAxisCommand, extend = false) {
    const position = findTablePosition(view.state.doc, table, view)
    if (position >= 0 && runTableAxisCommand(editor, position, axis, index, command, extend)) {
      if (command === 'select') keyboardTarget = { table, index, axis: axis === 'column' ? 'col' : 'row' }
      else editor.view.focus()
      scheduleSync()
    }
  }

  function sync() {
    if (destroyed) return
    const tables = view.editable
      ? Array.from(view.dom.querySelectorAll<HTMLTableElement>('.tableWrapper > table'))
      : []
    const seen = new Set(tables)
    // Dispose first: replacing a table can keep its wrapper, whose old overlay must go too.
    for (const [table, overlay] of overlays) if (!seen.has(table)) dispose(table, overlay)
    if (!view.editable) { hovered = null; resize.cancel(); columnDrag.cancel(); rowDrag.cancel(); return }
    if (resize.active || columnDrag.active || rowDrag.active) return
    if (hovered && !seen.has(hovered)) hovered = null
    selected = selectionTable()
    const focused = doc.activeElement
    const active = tables.filter(table => table === hovered || table === selected
      || (focused && overlays.get(table)?.element.contains(focused)))
    for (const [table, overlay] of overlays) if (!active.includes(table)) dispose(table, overlay)
    // Every read before any write: measuring a table after another table's rails were written would lay the
    // whole document out again, once per table.
    const targets = active.map(table => ({ table, overlay: overlays.get(table) ?? createOverlay(table) }))
    const geometries = targets.map(({ table }) => table.parentElement ? measureTable(table, table.parentElement) : undefined)
    // Only a cell selection highlights rail chips, and only then is a table's document position needed.
    const cellSelection = view.state.selection instanceof CellSelection
    targets.forEach(({ table, overlay }, index) => {
      // A scheduled geometry refresh must not replace the currently focused grip
      // and send the next keyboard move to the document body.
      const focused = doc.activeElement as HTMLElement | null
      if (!keyboardTarget && focused && overlay.element.contains(focused)) {
        const axis = focused.dataset.colIndex !== undefined ? 'col' : focused.dataset.rowIndex !== undefined ? 'row' : null
        if (axis) keyboardTarget = { table, axis, index: Number(axis === 'col' ? focused.dataset.colIndex : focused.dataset.rowIndex) }
      }
      // Rails depend on the measured geometry only: when it is unchanged (typing in a cell of one of many tables) and no
      // cell selection is (or was) highlighted, leave the rails and the hover tracking alone.
      const geometry = geometries[index]
      const signature = geometry && [geometry.tableRect.width, geometry.tableRect.height, ...geometry.columns.flatMap(item => [item.left, item.width]),
        ...geometry.rows.flatMap(item => [item.top, item.height])].map(Math.round).join(',')
      if (signature && signature === overlay.signature && !cellSelection && !overlay.highlighted && !(keyboardTarget?.table === table)) return
      overlay.signature = signature
      overlay.highlighted = cellSelection
      overlay.stopHover()
      const position = cellSelection ? findTablePosition(view.state.doc, table, view) : -1
      renderTableRails(overlay.element, table, {
        richTableFeatures,
        runColumnCommand: (target, index, command, extend) => run(target, 'column', index, command, extend),
        runRowCommand: (target, index, command, extend) => run(target, 'row', index, command, extend),
        selectedRange: (axis, index) => {
          const range = position >= 0 ? selectedTableAxisRange(view.state, position, axis) : null
          return range && index >= range.from && index < range.from + range.count ? range : null
        },
        startRowResize: resize.start,
        startColumnDrag: columnDrag.start,
        startRowDrag: rowDrag.start,
        moveRow: (target, from, to) => {
          const position = findTablePosition(view.state.doc, target, view)
          const range = tableAxisRange(view.state, position, 'row', from)
          const destination = range.from + to - from
          if (position >= 0 && moveRow(editor, position, range.from, destination, range.count)) keyboardTarget = { table: target, index: destination, axis: 'row' }
        },
        moveColumn: (target, from, to) => {
          const position = findTablePosition(view.state.doc, target, view)
          const range = tableAxisRange(view.state, position, 'column', from)
          const destination = range.from + to - from
          if (position >= 0 && moveColumn(editor, position, range.from, destination, range.count)) keyboardTarget = { table: target, index: destination, axis: 'col' }
        },
      }, geometries[index])
      overlay.stopHover = trackTableHover(overlay.element, table)
      if (keyboardTarget?.table === table) {
        const chip = overlay.element.querySelector<HTMLButtonElement>(`[data-${keyboardTarget.axis}-index="${keyboardTarget.index}"]`)
        chip?.setAttribute('data-active', '1')
        chip?.focus({ preventScroll: true })
        if (keyboardTarget.axis === 'row') chip?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' })
        keyboardTarget = null
      }
    })
  }

  function scheduleSync() {
    if (destroyed || frame !== null) return
    frame = win.requestAnimationFrame(() => { frame = null; sync() })
  }

  win.addEventListener('resize', scheduleSync)
  // Not `fonts.ready`: reading it while the editor is built forces a style and layout of the whole page (3.6-G open trace).
  doc.fonts?.addEventListener?.('loadingdone', scheduleSync)
  scheduleSync()
  return {
    update(nextView, previous) {
      const editabilityChanged = editable !== nextView.editable
      editable = nextView.editable
      let tablesChanged = false
      if (!nextView.state.doc.eq(previous.doc)) {
        const nextTables = collectTables(nextView.state.doc)
        tablesChanged = nextTables.length !== tablesInDocument.length
          || nextTables.some((table, index) => !table.eq(tablesInDocument[index]))
        tablesInDocument = nextTables
        // Even a paragraph edit can move the position captured by an active resize.
        resize.cancel()
        columnDrag.cancel(); rowDrag.cancel()
      }
      if (editabilityChanged) { resize.cancel(); columnDrag.cancel(); rowDrag.cancel() }
      // Only a cell selection (or leaving one) changes the rails; a caret moving through text must not re-measure and
      // rewrite every table's rails on every keystroke (long pages with many tables stalled typing).
      const selection = nextView.state.selection
      const railsSelection = (selection instanceof CellSelection || previous.selection instanceof CellSelection) && !selection.eq(previous.selection)
      const selectionMoved = !selection.eq(previous.selection) && selectionTable() !== selected
      if (editabilityChanged || tablesChanged || railsSelection || selectionMoved) scheduleSync()
    },
    destroy() {
      destroyed = true
      resize.cancel()
      columnDrag.destroy(); rowDrag.destroy()
      if (frame !== null) win.cancelAnimationFrame(frame)
      frame = null
      win.removeEventListener('resize', scheduleSync)
      doc.fonts?.removeEventListener?.('loadingdone', scheduleSync)
      view.dom.removeEventListener('mouseover', onOver)
      for (const [table, overlay] of overlays) dispose(table, overlay)
    },
  }
}

/** The table a node, wrapper or event target belongs to. */
function tableIn(target: unknown): HTMLTableElement | null {
  const wrapper = target instanceof Element ? target.closest('.tableWrapper') : null
  return (Array.from(wrapper?.children ?? []).find(child => child.tagName === 'TABLE') as HTMLTableElement | undefined) ?? null
}

/** Includes tables in containers without walking the text inside unrelated blocks. */
function collectTables(doc: Node): Node[] {
  const tables: Node[] = []
  doc.descendants(node => {
    if (node.type.name === 'table') { tables.push(node); return false }
    return !node.isTextblock
  })
  return tables
}
