import { describe, expect, it } from 'vitest'
import type { Extension } from '@tiptap/core'
import { CellTextAlign } from './CellTextAlign'
import { RowHeight } from './RowHeight'

// The attribute's renderHTML as Tiptap calls it (L7): only a known value reaches the inline style.
function render(extension: Extension, attribute: string, value: unknown) {
  const [global] = extension.config.addGlobalAttributes!.call({ options: extension.options } as never) as Array<{ attributes: Record<string, { renderHTML(attrs: Record<string, unknown>): Record<string, unknown> }> }>
  return global!.attributes[attribute]!.renderHTML({ [attribute]: value })
}

describe('table style attributes', () => {
  it('writes only a known text alignment into the style', () => {
    expect(render(CellTextAlign, 'textAlign', 'center')).toEqual({ 'data-text-align': 'center', style: 'text-align: center' })
    for (const value of [null, '', 'left; background: url(x)', 'justify', 42]) expect(render(CellTextAlign, 'textAlign', value)).toEqual({})
  })

  it('writes only a positive finite row height into the style', () => {
    expect(render(RowHeight, 'height', 48)).toEqual({ style: 'height: 48px' })
    for (const value of [null, 0, -5, Number.NaN, Number.POSITIVE_INFINITY, '40px; color: red', '40']) expect(render(RowHeight, 'height', value)).toEqual({})
  })
})
