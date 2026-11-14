// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { tableToolbarOffset } from './geometry'

// A table's wrapper as tables.css lays it out: 14px margin, a 30px top gutter (the column chips) and a 16px bottom one.
const wrapper = (top: number) => new DOMRect(0, top, 600, 30 + 120 + 16)
const table = (top: number) => new DOMRect(28, top + 30, 556, 120)
const height = 42, margin = 14
// floating-ui's main-axis offset: a top placement ends `offset` above the reference's top, a bottom one starts `offset` below its bottom.
const box = (placement: 'top' | 'bottom', top: number, offset: number) => {
  const reference = wrapper(top)
  const y = placement === 'top' ? reference.top - offset - height : reference.bottom + offset
  return { top: y, bottom: y + height }
}

describe('table toolbar placement', () => {
  it('stands over the column chips: its bottom on the table, its top below the previous block’s text', () => {
    const offset = tableToolbarOffset('top-start', wrapper(300), table(300), 0, height, margin)
    const placed = box('top', 300, offset)
    expect(placed.bottom).toBe(table(300).top)
    expect(placed.top).toBeGreaterThanOrEqual(wrapper(300).top - margin)
  })

  it('a table that opens the page keeps it in the editor: over the chips when they fit it, else under the table', () => {
    // Opening the editor: the margin and the gutter (44px) hold the 42px toolbar.
    expect(box('top', 14, tableToolbarOffset('top-start', wrapper(14), table(14), 0, height, margin)).top).toBeGreaterThanOrEqual(0)
    // No room: under the table, its bottom on the wrapper's margin (clear of the next block), never above the editor.
    const placed = box('top', 4, tableToolbarOffset('top-start', wrapper(4), table(4), 0, height, margin))
    expect(placed.bottom).toBe(wrapper(4).bottom + margin)
  })

  it('flipped below the table it covers the bottom gutter, not the next block', () => {
    const placed = box('bottom', 300, tableToolbarOffset('bottom-start', wrapper(300), table(300), 0, height, margin))
    expect(placed.bottom).toBe(wrapper(300).bottom + margin)
  })
})
