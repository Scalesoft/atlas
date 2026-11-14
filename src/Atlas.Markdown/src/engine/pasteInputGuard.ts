import { isRemoteTransaction } from '../core/recognition/gate'
import { Extension } from '@tiptap/core'
import type { Fragment, Slice } from '@tiptap/pm/model'
import { Plugin } from '@tiptap/pm/state'
import { __pastedCells, CellSelection, isInTable, selectionCell, TableMap } from '@tiptap/pm/tables'
import type { EditorView } from '@tiptap/pm/view'
import { isValidTableSize } from '../core/documentConstraints'
import { dispatchMarkdownInputError } from '../core/inputErrors'
import { clonePlainData } from '../core/plainData'
import { nestsTooDeep, validateBuiltinDocumentConstraints, validatePastedDocumentRules, validateTableDimensions } from './builtinDocumentValidation'
import { closeMarkdownHistory } from './historyBoundary'
import { inputStrings } from '../ui/strings/input'

function validateSlice(slice: Slice): void {
  clonePlainData(slice.content.toJSON())
  validateBuiltinDocumentConstraints(slice.content)
  // Match pastedCells' outer unwrapping, but bound spans before it fills holes.
  let { content, openStart, openEnd } = slice
  while (content.childCount === 1 && ((openStart > 0 && openEnd > 0) || content.firstChild?.type.spec.tableRole === 'table')) {
    openStart--; openEnd--
    content = content.firstChild!.content
  }
  const role = content.firstChild?.type.spec.tableRole
  if (role === 'row') {
    const rows: Fragment[] = []
    content.forEach(row => rows.push(row.content))
    validateTableDimensions(rows)
  } else if (role === 'cell' || role === 'header_cell') validateTableDimensions([content])
}

function validateTablePaste(view: EditorView, slice: Slice): void {
  if (!isInTable(view.state)) return
  // Existing state is bounded before asking TableMap to inspect the target.
  validateBuiltinDocumentConstraints(view.state.doc)
  const cells = __pastedCells(slice)
  if (!cells) return
  if (!isValidTableSize(cells.height, cells.width)) throw new TypeError('Table dimensions exceed the supported limit.')
  // Native CellSelection paste clips/repeats into the selected rectangle.
  if (view.state.selection instanceof CellSelection) return
  const $cell = selectionCell(view.state)
  const start = $cell.start(-1)
  const map = TableMap.get($cell.node(-1))
  const rect = map.findCell($cell.pos - start)
  if (!isValidTableSize(Math.max(map.height, rect.top + cells.height), Math.max(map.width, rect.left + cells.width))) {
    throw new TypeError('Table dimensions exceed the supported limit.')
  }
}

export const PasteInputGuard = Extension.create({
  name: 'markdownPasteInputGuard',
  priority: 10_000,
  onTransaction({ transaction }) {
    if (!transaction.docChanged || transaction.getMeta('paste') !== true || transaction.getMeta('uiEvent') !== 'paste' || this.editor.isDestroyed) return
    // Wait until appended normalization (including trailing paragraphs) has been
    // applied, so the whole paste remains a single undo event.
    this.editor.view.dispatch(closeMarkdownHistory(this.editor.state.tr).setMeta('addToHistory', false))
  },
  addProseMirrorPlugins() {
    const editor = this.editor
    const reject = (view: EditorView, source: 'paste' | 'transaction') => {
      dispatchMarkdownInputError(view.dom, {
        source,
        message: inputStrings.tablePasteRejected,
      })
    }
    return [new Plugin({
      props: {
        handlePaste(view, _event, slice) {
          try { validateSlice(slice); validateTablePaste(view, slice) }
          catch { reject(view, 'paste'); return true }
          // Keep upstream handling, marks, selection, clipping and plugin order.
          return false
        },
      },
      filterTransaction(transaction) {
        if (!transaction.docChanged) return true
        // Native table paste is checked above; default PM paste marks its actual
        // replacement transaction. Never filter remote Yjs updates here.
        if (transaction.getMeta('paste') !== true || transaction.getMeta('uiEvent') !== 'paste') {
          // A server refuses nesting past its depth, which in collaboration would stop the session (y-sync$: ySyncPluginKey).
          return isRemoteTransaction(transaction) || !nestsTooDeep(transaction)
        }
        try {
          validateBuiltinDocumentConstraints(transaction.doc)
          validatePastedDocumentRules(transaction.doc)
          if (nestsTooDeep(transaction)) return reject(editor.view, 'transaction'), false
          closeMarkdownHistory(transaction)
          return true
        }
        catch { reject(editor.view, 'transaction'); return false }
      },
    })]
  },
})
