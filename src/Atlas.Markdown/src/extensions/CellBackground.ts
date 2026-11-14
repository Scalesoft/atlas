import { Extension } from '@tiptap/core'
import { CellSelection } from '@tiptap/pm/tables'

declare module '@tiptap/core' {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface Commands<ReturnType> {
    cellBackground: {
      /** Set/clear `data-bg` on every cell in the current selection. */
      setCellBackground: (color: string | null) => ReturnType
    }
  }
}

/**
 * V25 Phase 5b — adds a `backgroundColor` attribute to `tableCell` and
 * `tableHeader`. A CSS rule on `[data-bg=…]` picks one of five preset tints
 * from the project palette. `null` clears the highlight.
 *
 * Round-trip caveat: GFM markdown tables don't carry per-cell colour, so a
 * cell highlight survives in-session HTML / undo history but is lost on a
 * markdown reload. Documented in `Resources/Specifications/Old/v25-editor.md`
 * §3.1.1.
 */
export const CellBackground = Extension.create({
  name: 'cellBackground',

  addGlobalAttributes() {
    return [
      {
        types: ['tableCell', 'tableHeader'],
        attributes: {
          backgroundColor: {
            default: null,
            parseHTML: (el) => el.getAttribute('data-bg') || null,
            renderHTML: (attrs) => {
              if (!attrs.backgroundColor) return {}
              return { 'data-bg': attrs.backgroundColor }
            },
          },
        },
      },
    ]
  },

  addCommands() {
    return {
      setCellBackground:
        (color) =>
        ({ tr, state, dispatch }) => {
          let mutated = false
          const sel = state.selection

          if (sel instanceof CellSelection) {
            // Multi-cell selection — TableMap-aware iterator handles colspan/rowspan.
            sel.forEachCell((node, pos) => {
              if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') {
                if (dispatch) tr.setNodeAttribute(pos, 'backgroundColor', color)
                mutated = true
              }
            })
            return mutated
          }

          // TextSelection / cursor — walk ancestors looking for the enclosing cell.
          const $pos = sel.$from
          for (let depth = $pos.depth; depth > 0; depth--) {
            const ancestor = $pos.node(depth)
            if (ancestor.type.name === 'tableCell' || ancestor.type.name === 'tableHeader') {
              if (dispatch) tr.setNodeAttribute($pos.before(depth), 'backgroundColor', color)
              mutated = true
              break
            }
          }
          return mutated
        },
    }
  },
})
