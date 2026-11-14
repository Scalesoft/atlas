import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { createTableControlsView } from '../features/table/overlayView'

export interface TableControlsOptions {
  richTableFeatures: boolean
}

/** Optional view adapter; document operations live in the table feature. */
export const TableControls = Extension.create<TableControlsOptions>({
  name: 'tableControls',
  priority: 200, // before the core clipboard text serializer, so a copied cell range is tab-separated
  addOptions: () => ({ richTableFeatures: false }),
  addProseMirrorPlugins() {
    return [new Plugin({
      key: new PluginKey('tableControls'),
      view: view => createTableControlsView(this.editor, view, this.options.richTableFeatures),
      props: {
        // Copied cells are a grid for a spreadsheet: tab between cells, newline between rows (the HTML flavour stays a table).
        clipboardTextSerializer: slice => {
          // A cell range is a slice of rows (an open table); a whole table is one table node.
          const only = slice.content.childCount === 1 ? slice.content.firstChild! : null
          const grid = only?.type.name === 'table' ? only : slice.content.firstChild?.type.name === 'tableRow' ? slice.content : null
          if (!grid) return ''
          const rows: string[] = []
          grid.forEach(row => { const cells: string[] = []; row.forEach(cell => cells.push(cell.textContent)); rows.push(cells.join('\t')) })
          return rows.join('\n')
        },
      },
    })]
  },
})
