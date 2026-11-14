import { Extension } from '@tiptap/core'

declare module '@tiptap/core' {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface Commands<ReturnType> {
    rowHeight: {
      /** Set or clear the height (in px) on a specific tableRow at `pos`. */
      setRowHeightAt: (pos: number, heightPx: number | null) => ReturnType
    }
  }
}

/**
 * V25 Phase 6 — row resize.
 *
 * Adds a `height` attribute to `tableRow` rendered as inline `style="height: …px"`.
 * The TableControls overlay drags the bottom border of a row to mutate this
 * attribute via the `setRowHeightAt(pos, px)` command.
 *
 * Round-trip caveat: GFM markdown tables don't carry per-row height, so a
 * resize survives in-session HTML / undo history but is lost on a markdown
 * reload. Documented alongside §3.1 in the editor spec.
 */
export const RowHeight = Extension.create({
  name: 'rowHeight',

  addGlobalAttributes() {
    return [
      {
        types: ['tableRow'],
        attributes: {
          height: {
            default: null,
            parseHTML: (el) => {
              const inline = el.style?.height
              if (inline && inline.endsWith('px')) {
                const n = parseInt(inline.slice(0, -2), 10)
                return Number.isFinite(n) ? n : null
              }
              return null
            },
            renderHTML: (attrs) => {
              // Defense in depth (the server refuses other values): only a positive finite number reaches the style.
              if (typeof attrs.height !== 'number' || !Number.isFinite(attrs.height) || attrs.height <= 0) return {}
              return { style: `height: ${attrs.height}px` }
            },
          },
        },
      },
    ]
  },

  addCommands() {
    return {
      setRowHeightAt:
        (pos, heightPx) =>
        ({ tr, dispatch }) => {
          const node = tr.doc.nodeAt(pos)
          if (!node || node.type.name !== 'tableRow') return false
          if (dispatch) tr.setNodeAttribute(pos, 'height', heightPx)
          return true
        },
    }
  },
})
