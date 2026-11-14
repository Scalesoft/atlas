import { Extension } from '@tiptap/core'
import { CellSelection, isInTable, selectedRect } from '@tiptap/pm/tables'

declare module '@tiptap/core' {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface Commands<ReturnType> {
    cellTextAlign: {
      /** Set per-cell text-align attribute on every cell in the selection. */
      setCellTextAlign: (align: 'left' | 'center' | 'right' | null) => ReturnType
    }
  }
}

/** Rich mode aligns selected cells; Markdown-safe mode aligns whole GFM columns.
 * Both render a data-text-align hook and the corresponding inline text-align style. */
export const CellTextAlign = Extension.create<{ columnScope: boolean }>({
  name: 'cellTextAlign',
  addOptions() { return { columnScope: false } },

  addGlobalAttributes() {
    return [
      {
        types: ['tableCell', 'tableHeader'],
        attributes: {
          textAlign: {
            default: null,
            parseHTML: (el) => el.getAttribute('data-text-align') || el.style.textAlign || el.getAttribute('align') || null,
            renderHTML: (attrs) => {
              // Defense in depth (the server refuses other values): only a known keyword reaches the style.
              if (!['left', 'center', 'right'].includes(attrs.textAlign)) return {}
              return {
                'data-text-align': attrs.textAlign,
                style: `text-align: ${attrs.textAlign}`,
              }
            },
          },
        },
      },
    ]
  },

  addCommands() {
    return {
      setCellTextAlign:
        (align) =>
        ({ tr, state, dispatch }) => {
          let mutated = false
          const sel = state.selection
          const setAlignment = (pos: number) => {
            if (dispatch) tr.setNodeAttribute(pos, 'textAlign', align).setNodeAttribute(pos, 'align', align)
          }
          if (this.options.columnScope) {
            if (!isInTable(state)) return false
            const rect = selectedRect(state)
            const cells = rect.map.cellsInRect({ left: rect.left, right: rect.right, top: 0, bottom: rect.map.height })
            cells.forEach(pos => setAlignment(rect.tableStart + pos))
            return cells.length > 0
          }

          if (sel instanceof CellSelection) {
            sel.forEachCell((node, pos) => {
              if (node.type.name === 'tableCell' || node.type.name === 'tableHeader') {
                setAlignment(pos)
                mutated = true
              }
            })
            return mutated
          }

          // Cursor inside a single cell — walk ancestors.
          const $pos = sel.$from
          for (let depth = $pos.depth; depth > 0; depth--) {
            const ancestor = $pos.node(depth)
            if (ancestor.type.name === 'tableCell' || ancestor.type.name === 'tableHeader') {
              setAlignment($pos.before(depth))
              mutated = true
              break
            }
          }
          return mutated
        },
    }
  },
})
