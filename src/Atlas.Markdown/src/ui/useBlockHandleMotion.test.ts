import { expect, it } from 'vitest'
import { blockControlsInset } from './useBlockHandleMotion'

it('keeps the block controls inside the editor surface only when the gutter is too narrow', () => {
  // A 40px gutter holds the 28px column: no correction.
  expect(blockControlsInset(140, 28, 100)).toBe(0)
  // Exactly as wide as the column: the controls touch the surface edge.
  expect(blockControlsInset(128, 28, 100)).toBe(0)
  // A 20px gutter: shift right by the missing 8px, never further.
  expect(blockControlsInset(120, 28, 100)).toBe(8)
  // The block starts at the surface edge (no gutter at all): the column moves fully inside.
  expect(blockControlsInset(100, 28, 100)).toBe(28)
})
