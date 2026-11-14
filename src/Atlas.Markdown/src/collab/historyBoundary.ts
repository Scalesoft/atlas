import { Extension } from '@tiptap/core'
import { Plugin, PluginKey, type Transaction } from '@tiptap/pm/state'
import { getRelativeSelection, ySyncPluginKey, yUndoPluginKey } from '@tiptap/y-tiptap'
import type * as Y from 'yjs'
import { markdownHistoryBoundary } from '../engine/historyBoundary'

/** A local caret move: the selection changed and the document did not (a remote update carries `y-sync$`). */
const caretMove = (all: readonly Transaction[]) => all.some(tr => tr.selectionSet) && !all.some(tr => tr.docChanged || tr.getMeta('y-sync$'))
/** A key the browser moves the caret with itself: its selection reaches the editor only with the next typed character. */
const caretKey = /^(Arrow(?:Left|Right|Up|Down)|Home|End|PageUp|PageDown)$/

/** Keep explicit moves separate without changing normal typing capture in Yjs; moving the caret starts a new step (J4.4). */
export const CollaborationHistoryBoundary = Extension.create({
  name: 'markdownCollaborationHistoryBoundary',
  addProseMirrorPlugins() {
    const editor = this.editor
    const stop = () => yUndoPluginKey.getState(editor.state)?.undoManager.stopCapturing()
    return [new Plugin({
      key: new PluginKey('markdownCollaborationHistoryBoundary'),
      props: {
        handleKeyDown: (_view, event) => { if (caretKey.test(event.key)) stop(); return false },
        handleDOMEvents: { mousedown: () => { stop(); return false } },
      },
      view(view) {
        // `transaction` carries what a plugin appended only once the state is applied: the boundary then still ends the step.
        const separate = ({ transaction, appendedTransactions = [] }: { transaction: Transaction; appendedTransactions?: readonly Transaction[] }) => {
          const all = [transaction, ...appendedTransactions]
          if (all.some(tr => tr.getMeta(markdownHistoryBoundary)) || caretMove(all)) stop()
        }
        // Yjs synchronization runs during view.updateState, between these events.
        editor.on('beforeTransaction', separate)
        editor.on('transaction', separate)
        // J9.4 / J4.4: y-tiptap gives a new undo step the selection of the last transaction's old state. With an appended transaction
        // (a heading id, a table repair, the trailing paragraph) that state is the changed document Yjs has not seen yet, so Ctrl+Z put
        // the caret after the change or at the end. The step takes the selection of the last state Yjs had instead: before the change.
        const binding = (ySyncPluginKey.getState(view.state) as { binding?: { doc: Y.Doc } } | undefined)?.binding
        const undoManager = yUndoPluginKey.getState(view.state)?.undoManager
        let syncedDoc = view.state.doc
        let synced: ReturnType<typeof getRelativeSelection> | undefined
        const capture = () => { if (binding && view.state.doc === syncedDoc) synced = getRelativeSelection(binding as never, view.state) }
        const afterSync = () => { syncedDoc = view.state.doc; capture() }
        // After y-tiptap's own listener (whichever plugin view came first), with the selection as it was when the step began.
        const added = ({ stackItem }: { stackItem: { meta: Map<unknown, unknown> } }) => {
          const selection = synced
          if (binding && selection) queueMicrotask(() => stackItem.meta.set(binding, selection))
        }
        // y-tiptap hands the step's selection to the binding on `stack-item-popped`, which Yjs emits after the undo has already
        // updated ProseMirror: Ctrl+Z put the caret where the current one resolved (a removed table row or timed item: the next
        // block), and the stale selection then waited for the next change. The undo transaction takes it at its start instead.
        type Binding = { beforeTransactionSelection: unknown }
        const restore = (transaction: Y.Transaction) => {
          if (!binding || !undoManager || transaction.origin !== undoManager) return
          const stack = undoManager.undoing ? undoManager.undoStack : undoManager.redoing ? undoManager.redoStack : []
          const selection = stack.at(-1)?.meta.get(binding) as object | undefined
          // Without the absolute positions: they belong to another document than the one the undo replaces (y-tiptap's recovery threw).
          if (selection) (binding as unknown as Binding).beforeTransactionSelection = { ...selection, absAnchor: null, absHead: null }
        }
        const popped = () => { if (binding) (binding as unknown as Binding).beforeTransactionSelection = null }
        binding?.doc.on('beforeTransaction', restore)
        binding?.doc.on('afterTransaction', afterSync)
        undoManager?.on('stack-item-added', added)
        undoManager?.on('stack-item-popped', popped)
        capture()
        return {
          update: capture,
          destroy() {
            editor.off('beforeTransaction', separate)
            editor.off('transaction', separate)
            binding?.doc.off('beforeTransaction', restore)
            binding?.doc.off('afterTransaction', afterSync)
            undoManager?.off('stack-item-added', added)
            undoManager?.off('stack-item-popped', popped)
          },
        }
      },
    })]
  },
})
