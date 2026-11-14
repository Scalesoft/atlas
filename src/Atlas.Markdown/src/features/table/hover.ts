import { tableStrings } from '../../ui/strings/table'

/** Owns delegated hover listeners and their delayed clear timer for one overlay. */
export function trackTableHover(overlay: HTMLDivElement, table: HTMLTableElement) {
  let clearTimer: ReturnType<typeof setTimeout> | null = null

  const clearActive = () => {
    if (overlay.dataset.columnDragging || overlay.dataset.rowDragging) return
    for (const el of Array.from(overlay.querySelectorAll('[data-active]'))) {
      el.removeAttribute('data-active')
    }
  }

  const setActive = (rowIdx: number, colIdx: number, columnInserter = true) => {
    if (clearTimer) {
      clearTimeout(clearTimer)
      clearTimer = null
    }
    // Geometry is read before any write: a scroll under a still pointer fires mouseover on every tick, and a read after the
    // attribute writes forced a full layout of the page each time (≈40 ms per tick on a big page).
    const row = rowIdx >= 0 ? table.querySelectorAll('tr')[rowIdx] as HTMLTableRowElement | undefined : undefined
    const cell = colIdx >= 0 ? table.querySelector('tr')?.children[colIdx] as HTMLTableCellElement | undefined : undefined
    const tableBox = row || cell ? table.getBoundingClientRect() : undefined
    const rowOffset = row && tableBox ? row.getBoundingClientRect().bottom - tableBox.top : 0
    const colOffset = cell && tableBox ? cell.getBoundingClientRect().right - tableBox.left : 0
    clearActive()
    if (rowIdx >= 0) {
      overlay.querySelector(
        `.tt-table-row-chip[data-row-index="${rowIdx}"]`,
      )?.setAttribute('data-active', '1')
      const mover = overlay.querySelector('.tt-table-row-insert') as HTMLButtonElement | null
      if (row && mover) {
        const boundary = rowIdx + 1
        mover.style.setProperty('--tt-insert-offset', `${rowOffset}px`)
        mover.dataset.boundary = String(boundary)
        mover.setAttribute('aria-label', tableStrings.insertRowAfterLabel(boundary))
        mover.setAttribute('data-active', '1')
      }
    }
    if (colIdx >= 0) {
      overlay.querySelector(
        `.tt-table-col-chip[data-col-index="${colIdx}"]`,
      )?.setAttribute('data-active', '1')
      const mover = overlay.querySelector('.tt-table-col-insert') as HTMLButtonElement | null
      if (cell && mover && columnInserter) {
        const boundary = colIdx + 1
        mover.style.setProperty('--tt-insert-offset', `${colOffset}px`)
        mover.dataset.boundary = String(boundary)
        mover.setAttribute('aria-label', tableStrings.insertColumnAfterLabel(boundary))
        mover.setAttribute('data-active', '1')
      }
    }
  }

  const activateTarget = (target: Element | null) => {
    if (overlay.dataset.columnDragging || overlay.dataset.rowDragging) return
    if (!target) return

    const cell = target.closest('td, th') as HTMLTableCellElement | null
    if (cell && table.contains(cell)) {
      const tr = cell.parentElement as HTMLTableRowElement | null
      if (!tr) return
      const rows = Array.from(table.querySelectorAll('tr'))
      const rowIdx = rows.indexOf(tr)
      const colIdx = Array.from(tr.children).indexOf(cell)
      setActive(rowIdx, colIdx)
      return
    }

    // Hovering a chip/insert directly — read its own data
    // attribute. Lets the user move from cell → chip without the
    // chip blinking out before they reach it.
    const rowChip = target.closest('.tt-table-row-chip') as HTMLElement | null
    if (rowChip) {
      setActive(Number(rowChip.dataset.rowIndex ?? -1), -1)
      return
    }
    const colChip = target.closest('.tt-table-col-chip') as HTMLElement | null
    if (colChip) {
      // The column inserter shares the chip's top gutter and would cover this chip's delete target.
      setActive(-1, Number(colChip.dataset.colIndex ?? -1), false)
      return
    }
    const rowInsert = target.closest('.tt-table-row-insert') as HTMLElement | null
    if (rowInsert) {
      if (clearTimer) { clearTimeout(clearTimer); clearTimer = null }
      clearActive()
      rowInsert.setAttribute('data-active', '1')
      return
    }
    const colInsert = target.closest('.tt-table-col-insert') as HTMLElement | null
    if (colInsert) {
      if (clearTimer) { clearTimeout(clearTimer); clearTimer = null }
      clearActive()
      colInsert.setAttribute('data-active', '1')
      return
    }
  }
  const onOver = (event: Event) => activateTarget(event.target as Element | null)

  const scheduleClear = (event: MouseEvent) => {
    // Skip the schedule if the cursor moved into another part of
    // our control surface — bubbling onOver will pick that up
    // and refresh the active state.
    const next = event.relatedTarget as HTMLElement | null
    if (next && (table.contains(next) || overlay.contains(next))) return
    if (clearTimer) clearTimeout(clearTimer)
    clearTimer = setTimeout(() => {
      clearActive()
      clearTimer = null
    }, 80)
  }

  table.addEventListener('mouseover', onOver)
  overlay.addEventListener('mouseover', onOver)
  // These native controls live inside view.dom. Let their own
  // handlers and browser Tab/Enter behavior run, not editor keymaps.
  const onControlKeydown = (event: KeyboardEvent) => {
    if (['Tab', 'Enter', ' ', 'Delete', 'Backspace'].includes(event.key)) event.stopPropagation()
  }
  overlay.addEventListener('keydown', onControlKeydown)
  table.addEventListener('mouseout', scheduleClear)
  overlay.addEventListener('mouseout', scheduleClear)

  // A geometry refresh replaces the controls while the pointer can stay in
  // the same cell. Firefox does not emit another mouseover in that case.
  activateTarget(table.querySelector('td:hover, th:hover'))

  return () => {
    table.removeEventListener('mouseover', onOver)
    overlay.removeEventListener('mouseover', onOver)
    overlay.removeEventListener('keydown', onControlKeydown)
    table.removeEventListener('mouseout', scheduleClear)
    overlay.removeEventListener('mouseout', scheduleClear)
    if (clearTimer) clearTimeout(clearTimer)
  }
}
