/** All rail coordinates share the table origin, independent of offset-parent CSS. */
export function measureTable(table: HTMLTableElement, wrapper: HTMLElement) {
  const tableRect = table.getBoundingClientRect()
  const wrapperRect = wrapper.getBoundingClientRect()
  const rows = Array.from(table.rows)
  const cells = Array.from(rows[0]?.cells ?? [])
  return {
    // The rails lie inside the wrapper, which scrolls a wide table: their origin is in its scrolled content, not on screen.
    tableRect, wrapperRect, scrollLeft: wrapper.scrollLeft, scrollTop: wrapper.scrollTop,
    columns: cells.map(cell => {
      const rect = cell.getBoundingClientRect()
      return { cell, left: rect.left - tableRect.left, width: rect.width, end: rect.right - tableRect.left }
    }),
    rows: rows.map(row => {
      const rect = row.getBoundingClientRect()
      return { row, top: rect.top - tableRect.top, height: rect.height, end: rect.bottom - tableRect.top }
    }),
  }
}

/**
 * The table toolbar's floating-ui `offset` over the table's wrapper (owner 2026-11-04): its bottom edge on the table's top edge, so
 * it covers the column chips in the wrapper's top gutter (still reachable by keyboard) and never the block before the table. Without
 * that room in the editor (a table that opens the page) or flipped below, its bottom edge on the wrapper's bottom margin, over the
 * bottom gutter and clear of the next block. No space is reserved: readers and writers keep one geometry (J7.1).
 */
export function tableToolbarOffset(placement: string, wrapper: DOMRect, table: DOMRect, editorTop: number, toolbarHeight: number, marginBelow: number): number {
  if (placement.startsWith('bottom')) return marginBelow - toolbarHeight
  return table.top - editorTop < toolbarHeight ? -(wrapper.height + marginBelow) : wrapper.top - table.top
}
